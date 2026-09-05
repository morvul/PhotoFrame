using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Живой поток камеры Home Assistant без звука. Можно переключаться между камерами.
    /// </summary>
    /// <remarks>
    /// Чем показывать камеру, решает сама Home Assistant через атрибут
    /// <c>frontend_stream_type</c>: HLS-плейлист играет ExoPlayer, MJPEG идёт одним долгим
    /// соединением (см. <see cref="HomeAssistantClient.StreamCameraMjpegFramesAsync"/>), а
    /// WebRTC на рамке не поддержан. Если поток не открылся или обрывается на середине
    /// просмотра, страница сама переходит на резерв: снимок камеры, обновляемый по таймеру
    /// с периодом из настроек рамки (см. <see cref="FrameSettings.CameraSnapshotIntervalMilliseconds"/>).
    /// </remarks>
    public partial class CameraViewPage : ContentPage
    {
        private const int SnapshotCrossfadeMilliseconds = 300;

        /// <summary>Сколько ждать первый кадр живого потока, прежде чем перейти на резерв.</summary>
        private const int MjpegFirstFrameTimeoutMilliseconds = 20000;

        /// <summary>
        /// Сколько ждать первый кадр HLS-потока, прежде чем пробовать другой способ.
        /// Больше, чем у MJPEG, и заметно: Home Assistant только запускает ffmpeg, а на
        /// 32-битной рамке первый кадр дополнительно ждёт JIT-компиляции медиа-классов —
        /// на практике кадр приходит далеко за пятнадцать секунд.
        /// </summary>
        private const int HlsFirstFrameTimeoutMilliseconds = 30000;

        private readonly HomeAssistantClient _client;

        // Интервал берётся из настроек при каждом входе на страницу (см. OnAppearing),
        // это лишь стартовое значение таймера до первого чтения.
        private readonly System.Timers.Timer _snapshotTimer =
            new(FrameSettings.CameraSnapshotIntervalMilliseconds) { AutoReset = true };

        private List<string> _cameraEntityIds = new();

        private int _cameraIndex;

        private string? _snapshotEntityId;

        /// <summary>Идёт ли уже запрос снимка: не даёт следующему тику таймера обогнать его.</summary>
        private bool _isRefreshingSnapshot;

        /// <summary>Какой слой сейчас на виду — на него и не пишем следующий кадр.</summary>
        private bool _snapshotIntoTopLayer;

        /// <summary>
        /// Токен текущей попытки живого потока. Замена другим экземпляром — это и есть
        /// «останови прошлый поток»: старый цикл видит несовпадение и завершается сам.
        /// </summary>
        private CancellationTokenSource? _mjpegStreamCts;

        /// <summary>Сущность, которую показываем сейчас. По ней понимаем, чей поток оборвался.</summary>
        private string? _currentEntityId;

        /// <summary>
        /// Камеры, которым Home Assistant не отдаёт живой HLS (облачные, без RTSP-источника).
        /// Для них HLS-попытку пропускаем и идём сразу на MJPEG/снимки — иначе страница зря
        /// висит на «Подключение...» до таймаута, а толку нет.
        /// </summary>
        private readonly HashSet<string> _camerasWithoutHls = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Бренды облачных камер, которым HA не может отдать HLS без локального RTSP.</summary>
        private static readonly HashSet<string> CloudOnlyCameraBrands = new(StringComparer.OrdinalIgnoreCase)
        {
            "Tuya",
        };

        /// <summary>
        /// Сигнал «пришёл первый кадр HLS» для текущей попытки. Каждая попытка живёт со
        /// своим экземпляром: устаревший кадр от прошлой камеры не должен будить новую.
        /// </summary>
        private TaskCompletionSource<bool>? _firstFrameTcs;

        public CameraViewPage()
        {
            InitializeComponent();

            // Тот же клиент, что и у остальных страниц: он держит долгоживущий
            // HttpClient, и отдельный экземпляр на каждый вход в камеру был бы
            // лишним подключением, которое никто не закрывает.
            _client = IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _snapshotTimer.Elapsed += OnSnapshotTimerElapsed;
            LiveVideoPlayer.PlaybackFailed += OnLiveVideoFailed;
            LiveVideoPlayer.FirstFrameRendered += OnLiveVideoFirstFrame;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Могли поменять на экране настроек, пока страницы камеры не было на экране.
            _snapshotTimer.Interval = FrameSettings.CameraSnapshotIntervalMilliseconds;

            if (!HomeAssistantClient.IsConfigured)
            {
                ShowStatus("Не заданы адрес и токен Home Assistant в настройках рамки.");
                return;
            }

            ShowStatus("Поиск камер...");

            _ = LoadCamerasAsync();
        }

        private async Task LoadCamerasAsync()
        {
            try
            {
                // Список запрашивается заново при каждом входе на страницу: список камер
                // мог измениться, а рамка не должна показывать камеру, которой уже нет.
                _cameraEntityIds = await _client.GetCameraEntityIdsAsync().ConfigureAwait(true);
            }
            catch (PhotoSourceException lookupFailure)
            {
                ShowStatus(lookupFailure.Message);
                return;
            }

            if (_cameraEntityIds.Count == 0)
            {
                ShowStatus("В Home Assistant нет ни одной камеры.");
                return;
            }

            NextCameraButton.IsVisible = _cameraEntityIds.Count > 1;
            _cameraIndex = 0;

            // Узнаём, что сама Home Assistant считает подходящим способом показа для
            // каждой камеры, и запоминаем: от этого зависит, какой механизм запускать.
            foreach (string cameraId in _cameraEntityIds)
            {
                string? streamType = null;
                try
                {
                    streamType = await _client
                        .GetCameraFrontendStreamTypeAsync(cameraId).ConfigureAwait(true);
                    FrameLog.Info($"{cameraId}: frontend_stream_type = {streamType ?? "(нет)"}");
                }
                catch (PhotoSourceException lookupFailure)
                {
                    FrameLog.Warn($"{cameraId}: frontend_stream_type не прочитан ({lookupFailure.Message})");
                }

                // Облачные камеры (Tuya и подобные) HA не отдаёт живым HLS без RTSP-источника —
                // таких в HLS-попытке нет смысла. Отмечаем их, чтобы идти сразу на MJPEG/снимки.
                try
                {
                    string? brand = await _client.GetCameraBrandAsync(cameraId).ConfigureAwait(true);
                    if (brand is not null && CloudOnlyCameraBrands.Contains(brand))
                    {
                        _camerasWithoutHls.Add(cameraId);
                        FrameLog.Info($"{cameraId}: бренд {brand} — HLS пропускаем, идём на MJPEG/снимки");
                    }
                }
                catch (PhotoSourceException lookupFailure)
                {
                    FrameLog.Warn($"{cameraId}: бренд не прочитан ({lookupFailure.Message})");
                }
            }

            PlayCurrentCamera();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            StopSnapshotPolling();
            StopMjpegStream();
            StopHlsStream();
            _currentEntityId = null;
        }

        private void PlayCurrentCamera()
        {
            string entityId = _cameraEntityIds[_cameraIndex];
            _currentEntityId = entityId;

            StopSnapshotPolling();
            StopMjpegStream();
            StopHlsStream();

            // Слои снимка от прошлой камеры или прошлой попытки не нужны — первый
            // пришедший кадр сам их сменит через обычный для своего режима переход.
            SnapshotImage.IsVisible = false;
            SnapshotImage.Opacity = 0;
            SnapshotImageTop.IsVisible = false;
            SnapshotImageTop.Opacity = 0;

            HeaderLabel.Text = $"Камера ({_cameraIndex + 1}/{_cameraEntityIds.Count}) — {entityId}";
            ShowStatus("Подключение...");

            // Живой поток пробуем так: сначала HLS (его отдаёт camera/stream), и только
            // если он не отдал кадр — MJPEG, а в самом крайнем случае снимки. Атрибут
            // frontend_stream_type у камер не всегда заполнен (в этой версии Home Assistant —
            // вовсе нет), так что полагаться на него нельзя. Но облачные камеры без RTSP
            // (Tuya и подобные) HLS не отдают вовсе — для них сразу MJPEG/снимки.
            if (_camerasWithoutHls.Contains(entityId))
            {
                _ = RunMjpegStreamAsync(entityId);
            }
            else
            {
                _ = RunHlsStreamAsync(entityId);
            }
        }

        private void StopMjpegStream()
        {
            _mjpegStreamCts?.Cancel();
            _mjpegStreamCts?.Dispose();
            _mjpegStreamCts = null;
        }

        /// <summary>
        /// Живой HLS-поток камеры. Получает адрес плейлиста по WebSocket (camera/stream),
        /// отдаёт его ExoPlayer и оставляет играть по кругу. Если поток не поднялся или
        /// оборвался, страница уходит на резерв — снимки по таймеру.
        /// </summary>
        private async Task RunHlsStreamAsync(string entityId)
        {
            string streamUrl;
            try
            {
                streamUrl = await _client
                    .GetCameraStreamUrlAsync(entityId).ConfigureAwait(true);
            }
            catch (PhotoSourceException streamFailure)
            {
                if (_currentEntityId == entityId)
                {
                    FrameLog.Warn(
                        $"{entityId}: HLS-поток не открылся ({streamFailure.Message}), пробуем MJPEG");
                    _ = RunMjpegStreamAsync(entityId);
                }
                return;
            }

            // Пока запрашивали адрес, переключили камеру или ушли со страницы.
            if (_currentEntityId != entityId)
            {
                return;
            }

            var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrameTcs = firstFrame;

            // Сначала показываем вид, чтобы у него появился платформенный обработчик,
            // и только потом отдаём адрес: иначе SourcePath был бы записан раньше,
            // чем ExoPlayer успел бы его принять.
            LiveVideoPlayer.IsVisible = true;
            LiveVideoPlayer.SourcePath = streamUrl;
            LiveVideoPlayer.IsLooping = true;
            LiveVideoPlayer.IsMuted = true;
            LiveVideoPlayer.Play();
            StatusLabel.IsVisible = false;

            // Ждём первый кадр. Если его нет — HLS живой картинки не даёт (поток не поднялся
            // на стороне Home Assistant), пробуем MJPEG, а тот сам перейдёт на снимки.
            Task completed = await Task.WhenAny(
                firstFrame.Task, Task.Delay(HlsFirstFrameTimeoutMilliseconds)).ConfigureAwait(true);

            if (completed != firstFrame.Task && _currentEntityId == entityId)
            {
                FrameLog.Warn($"{entityId}: HLS не отдал кадр, пробуем MJPEG");
                StopHlsStream();
                _ = RunMjpegStreamAsync(entityId);
            }
        }

        /// <summary>
        /// Останавливает HLS-поток. Начинается всё с того же рукопожатия, что и у клипов,
        /// — слои угасают и отдают декодер, а страница прячет видео.
        /// </summary>
        private void StopHlsStream()
        {
            // Устаревшая попытка не должна реагировать на первый кадр от прошлой камеры.
            _firstFrameTcs = null;
            // Одного SourcePath=null достаточно: он сам гасит и пересоздаёт слои.
            // Лишний Stop() сверху давал двойной teardown — лишние Init/Release декодера,
            // из-за которых повторное открытие ловит отказ переинициализации (-1010).
            LiveVideoPlayer.SourcePath = null;
            LiveVideoPlayer.IsVisible = false;
        }

        /// <summary>
        /// ExoPlayer не смог ни открыть, ни доиграть поток — пробуем MJPEG, а тот уже сам
        /// уйдёт на снимки, если и он не даст кадров.
        /// </summary>
        private void OnLiveVideoFailed(object? sender, EventArgs e)
        {
            // Уже на MJPEG — не перезапускаем его повторным фолбэком.
            if (_currentEntityId is string entityId && _mjpegStreamCts is null)
            {
                FrameLog.Warn($"{entityId}: HLS оборвался, пробуем MJPEG");
                StopHlsStream();
                _ = RunMjpegStreamAsync(entityId);
            }
        }

        /// <summary>
        /// Пришёл первый кадр HLS — снимаем ожидание: поток живой, оставляем играть.
        /// </summary>
        private void OnLiveVideoFirstFrame(object? sender, EventArgs e) =>
            _firstFrameTcs?.TrySetResult(true);

        /// <summary>
        /// Держит соединение открытым и показывает кадры по мере прихода. Если поток не
        /// открылся или обрывается на середине, страница сама переходит на резерв.
        /// </summary>
        private async Task RunMjpegStreamAsync(string entityId)
        {
            var cts = new CancellationTokenSource();
            _mjpegStreamCts = cts;

            // Часы на первый кадр: без этого зависший обмен (заголовки пришли, а кадра
            // от источника нет) держал бы страницу на «Подключение...» бесконечно —
            // ни ошибки, ни следа в журнале, только вечная надпись. Дальше, пока кадры
            // идут, ограничение не нужно — снимаем его после первого же кадра.
            cts.CancelAfter(MjpegFirstFrameTimeoutMilliseconds);
            bool receivedFirstFrame = false;

            try
            {
                await foreach (byte[] jpegBytes in _client
                    .StreamCameraMjpegFramesAsync(entityId, cts.Token).ConfigureAwait(true))
                {
                    // Пока кадр ждали, могли переключить камеру, уйти со страницы или
                    // уже перейти на резерв — устаревший кадр показывать не нужно.
                    if (_mjpegStreamCts != cts)
                    {
                        return;
                    }

                    if (!receivedFirstFrame)
                    {
                        receivedFirstFrame = true;
                        cts.CancelAfter(Timeout.InfiniteTimeSpan);
                        FrameLog.Info($"{entityId}: поток камеры открылся");
                    }

                    ShowMjpegFrame(jpegBytes);
                }

                // Цикл закончился сам — источник закрыл соединение, а не мы его остановили.
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn($"{entityId}: поток камеры закрылся, переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            catch (OperationCanceledException) when (!receivedFirstFrame)
            {
                // Часы на первый кадр вышли, а не мы сами остановили поток.
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn($"{entityId}: поток камеры не ответил вовремя, переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            catch (OperationCanceledException)
            {
                // Ушли со страницы, переключили камеру или сами остановили поток — не ошибка.
            }
            catch (PhotoSourceException streamFailure)
            {
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn(
                        $"{entityId}: поток камеры не открылся ({streamFailure.Message}), "
                        + "переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            finally
            {
                if (_mjpegStreamCts == cts)
                {
                    _mjpegStreamCts = null;
                }

                cts.Dispose();
            }
        }

        /// <summary>
        /// Показывает кадр живого потока без перехода: кадры и так сменяют друг друга
        /// часто, а затухание на каждый только смазывало бы картинку.
        /// </summary>
        private void ShowMjpegFrame(byte[] jpegBytes)
        {
            SnapshotImage.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            SnapshotImage.Opacity = 1;
            SnapshotImage.IsVisible = true;
            SnapshotImageTop.Opacity = 0;
            StatusLabel.IsVisible = false;
        }

        private void StartSnapshotPolling(string entityId)
        {
            StopMjpegStream();
            StopHlsStream();
            ShowStatus("Загрузка снимка...");

            // Слои снимка от предыдущей камеры не нужны: первый кадр новой камеры
            // сам проступит поверх них через обычный переход.
            _isRefreshingSnapshot = false;

            _snapshotEntityId = entityId;
            _snapshotTimer.Start();

            // Не ждать первого тика таймера — первый снимок нужен сразу.
            _ = RefreshSnapshotAsync(entityId);
        }

        private void StopSnapshotPolling()
        {
            _snapshotTimer.Stop();
            _snapshotEntityId = null;
        }

        private async Task RefreshSnapshotAsync(string entityId)
        {
            // Пока снимок ждали, могли переключить камеру или уйти со страницы —
            // устаревший ответ показывать не нужно.
            if (_snapshotEntityId != entityId)
            {
                return;
            }

            // Предыдущий запрос этой же камеры ещё не завершился: ответ Home Assistant
            // иногда занимает больше периода таймера, и без этой проверки более
            // медленный старый снимок мог прийти позже уже показанного нового.
            if (_isRefreshingSnapshot)
            {
                return;
            }

            _isRefreshingSnapshot = true;
            try
            {
                byte[] jpegBytes = await _client.GetCameraSnapshotAsync(entityId).ConfigureAwait(true);

                if (_snapshotEntityId != entityId)
                {
                    return;
                }

                await ShowSnapshotAsync(jpegBytes).ConfigureAwait(true);
            }
            catch (PhotoSourceException snapshotFailure)
            {
                if (_snapshotEntityId == entityId)
                {
                    ShowStatus($"{entityId}: {snapshotFailure.Message}");
                }
            }
            finally
            {
                _isRefreshingSnapshot = false;
            }
        }

        /// <summary>
        /// Выводит новый снимок плавным переходом, как и слайд-шоу на главном экране.
        /// </summary>
        /// <remarks>
        /// Кадр пишется в скрытый слой и проступает поверх видимого: подмена картинки
        /// скачком на резервных, редко обновляемых снимках выглядела бы рябью.
        /// </remarks>
        private async Task ShowSnapshotAsync(byte[] jpegBytes)
        {
            bool intoTopLayer = !_snapshotIntoTopLayer;
            Image targetLayer = intoTopLayer ? SnapshotImageTop : SnapshotImage;
            Image otherLayer = intoTopLayer ? SnapshotImage : SnapshotImageTop;

            targetLayer.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            targetLayer.Opacity = 0;
            targetLayer.IsVisible = true;
            _snapshotIntoTopLayer = intoTopLayer;

            StatusLabel.IsVisible = false;

            await targetLayer.FadeToAsync(1, SnapshotCrossfadeMilliseconds).ConfigureAwait(true);

            // Прежний слой убираем не сразу, а после перехода: пока оба видны,
            // старый снимок и просвечивает через новый, давая тот самый переход.
            otherLayer.Opacity = 0;
        }

        private void OnSnapshotTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            string? entityId = _snapshotEntityId;
            if (entityId is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => _ = RefreshSnapshotAsync(entityId));
        }

        private void ShowStatus(string text)
        {
            StatusLabel.Text = text;
            StatusLabel.IsVisible = true;
        }

        private void OnNextCameraClicked(object? sender, EventArgs e)
        {
            if (_cameraEntityIds.Count == 0)
            {
                return;
            }

            _cameraIndex = (_cameraIndex + 1) % _cameraEntityIds.Count;
            PlayCurrentCamera();
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
