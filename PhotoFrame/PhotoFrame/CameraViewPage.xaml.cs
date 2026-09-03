using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Снимки камер Home Assistant, обновляемые по таймеру.
    /// </summary>
    /// <remarks>
    /// Загружает снимок камеры и обновляет его каждую ~1.2 секунды. Можно переключаться между камерами.
    /// </remarks>
    public partial class CameraViewPage : ContentPage
    {
        private const int SnapshotIntervalMilliseconds = 1200;

        private const int SnapshotCrossfadeMilliseconds = 300;

        private readonly HomeAssistantClient _client;

        private readonly System.Timers.Timer _snapshotTimer =
            new(SnapshotIntervalMilliseconds) { AutoReset = true };

        private List<string> _cameraEntityIds = new();

        private int _cameraIndex;

        private string? _snapshotEntityId;

        /// <summary>Идёт ли уже запрос снимка: не даёт следующему тику таймера обогнать его.</summary>
        private bool _isRefreshingSnapshot;

        /// <summary>Какой слой сейчас на виду — на него и не пишем следующий кадр.</summary>
        private bool _snapshotIntoTopLayer;

        public CameraViewPage()
        {
            InitializeComponent();

            // Тот же клиент, что и у остальных страниц: он держит долгоживущий
            // HttpClient, и отдельный экземпляр на каждый вход в камеру был бы
            // лишним подключением, которое никто не закрывает.
            _client = IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _snapshotTimer.Elapsed += OnSnapshotTimerElapsed;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (!HomeAssistantClient.IsConfigured)
            {
                ShowStatus("Не задан адрес и токен Home Assistant в secrets.props.");
                return;
            }

            ShowStatus("Поиск камер...");

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

            PlayCurrentCamera();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            StopSnapshotPolling();
        }

        private void PlayCurrentCamera()
        {
            string entityId = _cameraEntityIds[_cameraIndex];

            HeaderLabel.Text = $"Камера ({_cameraIndex + 1}/{_cameraEntityIds.Count}) — {entityId}";

            StartSnapshotPolling(entityId);
        }

        private void StartSnapshotPolling(string entityId)
        {
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
        /// скачком на быстро обновляющемся снимке камеры выглядела рябью, а не потоком.
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
