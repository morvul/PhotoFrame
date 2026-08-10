using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;

namespace PhotoFrame
{
    public partial class MainPage : ContentPage
    {
        /// <summary>Сколько секунд панель висит на экране после касания.</summary>
        private const int PanelRevealSeconds = 10;

        /// <summary>
        /// Толщина обводки в пикселях. Время крупное, ему нужен чуть заметнее контур,
        /// чем мелкой дате — одинаковая толщина делала дату «жирной».
        /// </summary>
        private const double TimeOutlineWidth = 1.5;
        private const double DateOutlineWidth = 1;

        /// <summary>
        /// Обводка не чисто чёрная: полупрозрачная читается так же, но не выглядит
        /// нарисованной поверх снимка.
        /// </summary>
        private const double OutlineOpacity = 0.55;

        /// <summary>
        /// Смещения копий текста, из которых складывается обводка. Восемь направлений
        /// дают ровный контур; меньше — и на диагоналях появляются просветы.
        /// </summary>
        private static readonly (double X, double Y)[] OutlineOffsets =
        {
            (-1, -1), (0, -1), (1, -1),
            (-1, 0), (1, 0),
            (-1, 1), (0, 1), (1, 1),
        };

        /// <summary>
        /// Углы, по которым «гуляют» часы. Центр не используется: там кадр интереснее всего,
        /// а сверху ещё и панель управления.
        /// </summary>
        private static readonly (LayoutOptions Horizontal, LayoutOptions Vertical, Thickness Margin)[]
            ClockPositions =
            {
                (LayoutOptions.Start, LayoutOptions.End, new Thickness(48, 0, 0, 40)),
                (LayoutOptions.End, LayoutOptions.End, new Thickness(0, 0, 48, 40)),
                (LayoutOptions.End, LayoutOptions.Start, new Thickness(0, 132, 48, 0)),
                (LayoutOptions.Start, LayoutOptions.Start, new Thickness(48, 132, 0, 0)),
            };

        private readonly CompositePhotoSource _photoSource;
        private readonly System.Timers.Timer _slideshowTimer;
        private readonly System.Timers.Timer _albumPollTimer;
        private readonly System.Timers.Timer _panelHideTimer;
        private readonly System.Timers.Timer _clockTimer;

        /// <summary>Как часто перечитываются значения датчиков, минуты.</summary>
        private const int SensorRefreshMinutes = 5;

        /// <summary>Копии текста времени: восемь для обводки плюс одна основная.</summary>
        private readonly List<Label> _clockTimeLabels;
        private readonly List<Label> _clockDateLabels;
        private readonly List<Label> _sensorLabels;

        private readonly HomeAssistantClient _homeAssistantClient;
        private readonly System.Timers.Timer _sensorTimer;

        /// <summary>Не даём проверке по таймеру наложиться на нажатие кнопки.</summary>
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private List<string> _localPhotoPaths = new();

        /// <summary>Индекс показанного сейчас кадра. -1 — ещё ничего не показано.</summary>
        private int _currentPhotoIndex = -1;

        private int _clockPositionIndex;

        /// <summary>Ночной режим сейчас активен. null — состояние ещё не определялось.</summary>
        private bool? _isNightModeActive;

        /// <summary>Минута, для которой уже нарисован ночной кадр.</summary>
        private string? _lastRenderedNightMinute;

        /// <summary>Текущая фаза шахматной маски ночных часов.</summary>
        private bool _nightMaskPhaseShifted;

        /// <summary>Растёт на каждом кадре: отбрасывает анализ устаревшего снимка.</summary>
        private int _photoGeneration;

        /// <summary>Текущий кадр — видеофайл.</summary>
        private bool _isCurrentSlideVideo;

        /// <summary>Видео сейчас воспроизводится, слайд-шоу приостановлено.</summary>
        private bool _isVideoPlaying;

        public MainPage()
        {
            InitializeComponent();

            // Shell создаёт страницу через DataTemplate, минуя контейнер, поэтому сервис
            // достаём из провайдера вручную — иначе на каждый показ страницы появлялся бы
            // новый экземпляр со своим HttpClient.
            _photoSource = IPlatformApplication.Current?.Services.GetService<CompositePhotoSource>()
                           ?? new CompositePhotoSource(
                               new SharedAlbumPhotoSource(), new LocalFolderPhotoSource());

            _clockTimeLabels = BuildOutlinedText(
                ClockTimeHost, fontSize: 68, isBold: true, Colors.White, TimeOutlineWidth);
            _clockDateLabels = BuildOutlinedText(
                ClockDateHost, fontSize: 22, isBold: false, Color.FromArgb("#F0F0F0"), DateOutlineWidth);
            _sensorLabels = BuildOutlinedText(
                SensorHost, fontSize: 24, isBold: true, Color.FromArgb("#BFEFFF"), DateOutlineWidth);

            _homeAssistantClient =
                IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _sensorTimer = new System.Timers.Timer(
                TimeSpan.FromMinutes(SensorRefreshMinutes).TotalMilliseconds)
            {
                AutoReset = true,
            };
            _sensorTimer.Elapsed += OnSensorTimerElapsed;

            _slideshowTimer = new System.Timers.Timer { AutoReset = true };
            _slideshowTimer.Elapsed += OnSlideshowTimerElapsed;

            _albumPollTimer = new System.Timers.Timer { AutoReset = true };
            _albumPollTimer.Elapsed += OnAlbumPollTimerElapsed;

            _panelHideTimer = new System.Timers.Timer(
                TimeSpan.FromSeconds(PanelRevealSeconds).TotalMilliseconds)
            {
                AutoReset = false,
            };
            _panelHideTimer.Elapsed += OnPanelHideTimerElapsed;

            // Минуты меняются раз в 60 секунд, но опрос раз в 10 секунд гарантирует,
            // что показанное время не отстанет заметно после выхода из сна.
            _clockTimer = new System.Timers.Timer(TimeSpan.FromSeconds(10).TotalMilliseconds)
            {
                AutoReset = true,
            };
            _clockTimer.Elapsed += OnClockTimerElapsed;

            VideoPlayer.PlaybackFinished += OnVideoPlaybackFinished;
        }

        /// <summary>
        /// Собирает текст с обводкой: восемь чёрных копий со смещением и одна основная сверху.
        /// В MAUI у Label нет обводки текста, а одной тени не хватает на светлых снимках.
        /// </summary>
        private static List<Label> BuildOutlinedText(
            Grid host, double fontSize, bool isBold, Color fillColor, double outlineWidth)
        {
            var labels = new List<Label>(OutlineOffsets.Length + 1);

            foreach ((double offsetX, double offsetY) in OutlineOffsets)
            {
                var outlineLabel = new Label
                {
                    FontSize = fontSize,
                    FontAttributes = isBold ? FontAttributes.Bold : FontAttributes.None,
                    TextColor = Colors.Black,
                    Opacity = OutlineOpacity,
                    TranslationX = offsetX * outlineWidth,
                    TranslationY = offsetY * outlineWidth,
                    InputTransparent = true,
                };

                host.Add(outlineLabel);
                labels.Add(outlineLabel);
            }

            var fillLabel = new Label
            {
                FontSize = fontSize,
                FontAttributes = isBold ? FontAttributes.Bold : FontAttributes.None,
                TextColor = fillColor,
                InputTransparent = true,
            };

            host.Add(fillLabel);
            labels.Add(fillLabel);
            return labels;
        }

        private static void SetOutlinedText(List<Label> labels, string text)
        {
            foreach (Label label in labels)
            {
                label.Text = text;
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Настройки могли измениться на экране настроек, поэтому перечитываем их
            // каждый раз при возврате, а не только в конструкторе.
            ApplySettings();

            // Сначала показываем то, что уже лежит на диске: рамка не должна стоять
            // чёрной, пока идёт сетевой запрос.
            LoadPhotosFromCache();

            _albumPollTimer.Start();

            // Проверка при запуске — иначе после перезагрузки рамки новые снимки
            // ждали бы до следующего срабатывания таймера. Плюс явный запрос
            // с экрана настроек.
            bool syncRequestedFromSettings = SettingsPage.SyncRequestedOnReturn;
            SettingsPage.SyncRequestedOnReturn = false;
            _ = SynchronizeAlbumAsync(forceDownload: syncRequestedFromSettings);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Без остановки таймеры продолжают тикать и держать ссылку на страницу.
            _slideshowTimer.Stop();
            _albumPollTimer.Stop();
            _panelHideTimer.Stop();
            _clockTimer.Stop();
            _sensorTimer.Stop();

            // Уходя со страницы, освобождаем проигрыватель: иначе звук продолжится
            // на экране настроек.
            StopVideoPlayback();
        }

        private void OnSensorTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            _ = RefreshSensorsAsync();
        }

        /// <summary>
        /// Обновляет строку с показаниями датчиков.
        /// </summary>
        /// <remarks>
        /// При недоступном Home Assistant строка убирается целиком: устаревшее значение
        /// температуры хуже, чем отсутствие значения, а текст ошибки поверх фотографии
        /// не нужен — он есть на экране настроек.
        /// </remarks>
        private async Task RefreshSensorsAsync()
        {
            string[] entityIds = FrameSettings.SensorEntityIds;

            if (!FrameSettings.ShowSensors || entityIds.Length == 0
                || !HomeAssistantClient.IsConfigured)
            {
                await MainThread.InvokeOnMainThreadAsync(() => SensorHost.IsVisible = false)
                    .ConfigureAwait(true);
                return;
            }

            try
            {
                List<(string EntityId, string Text)> values = await _homeAssistantClient
                    .ReadSensorValuesAsync(entityIds).ConfigureAwait(true);

                // Значок перед значением: все выбранные датчики могут быть термометрами,
                // и без него непонятно, где какая температура.
                var parts = new List<string>(values.Count);
                foreach ((string entityId, string text) in values)
                {
                    string icon = FrameSettings.GetSensorIcon(entityId);
                    parts.Add(string.IsNullOrEmpty(icon) ? text : icon + " " + text);
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (parts.Count == 0)
                    {
                        SensorHost.IsVisible = false;
                        return;
                    }

                    SetOutlinedText(_sensorLabels, string.Join("   ", parts));
                    SensorHost.IsVisible = _isNightModeActive != true;
                }).ConfigureAwait(true);
            }
            catch (PhotoSourceException sensorFailure)
            {
                System.Diagnostics.Debug.WriteLine($"Датчики не прочитаны: {sensorFailure.Message}");
                await MainThread.InvokeOnMainThreadAsync(() => SensorHost.IsVisible = false)
                    .ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Блокирует кнопку «назад» на экране слайд-шоу.
        /// </summary>
        /// <remarks>
        /// MainPage — корень навигации, поэтому системное «назад» закрывало приложение,
        /// и рамка оставалась на лаунчере до ручного запуска. Для устройства, которое
        /// должно просто стоять и показывать фотографии, это недопустимо.
        /// </remarks>
        protected override bool OnBackButtonPressed() => true;

        private void ApplySettings()
        {
            _slideshowTimer.Interval =
                TimeSpan.FromSeconds(FrameSettings.SlideshowIntervalSeconds).TotalMilliseconds;
            _albumPollTimer.Interval =
                TimeSpan.FromHours(FrameSettings.AlbumPollIntervalHours).TotalMilliseconds;

            SlideshowImage.Aspect = FrameSettings.FillScreen ? Aspect.AspectFill : Aspect.AspectFit;

            // Панель всегда скрыта при возврате на экран: поверх фотографии не должно
            // быть ничего лишнего, а показывается она касанием.
            ControlPanel.IsVisible = false;
            UpdateVideoControlsVisibility();
            _panelHideTimer.Stop();

            ClockOverlay.IsVisible = FrameSettings.ShowClock;
            ClockDateHost.IsVisible = FrameSettings.ShowDate;

            // Расписание могли изменить в настройках — пересчитываем режим с нуля.
            _isNightModeActive = null;

            // Таймер работает всегда, даже если часы поверх снимка отключены:
            // по нему же переключается ночной режим.
            UpdateClock();
            _clockTimer.Start();

            // Датчики опрашиваются реже часов: комнатная температура не меняется за минуту.
            _sensorTimer.Start();
            _ = RefreshSensorsAsync();
        }

        private void OnClockTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(UpdateClock);
        }

        private void UpdateClock()
        {
            DateTime localNow = DateTime.Now;
            string formattedTime = localNow.ToString("HH:mm", CultureInfo.CurrentCulture);

            // "Воскресенье, 9 августа" — первая буква заглавная, иначе выглядит небрежно.
            string rawDate = localNow.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            string formattedDate =
                char.ToUpper(rawDate[0], CultureInfo.CurrentCulture) + rawDate[1..];

            SetOutlinedText(_clockTimeLabels, formattedTime);

            if (FrameSettings.ShowDate)
            {
                SetOutlinedText(_clockDateLabels, formattedDate);
            }

            NightTimeLabel.Text = formattedTime;
            NightDateLabel.Text = formattedDate;

            ApplyNightMode(localNow);

            if (_isNightModeActive == true)
            {
                _ = RefreshNightClockAsync(formattedTime, formattedDate);
            }
        }

        /// <summary>
        /// Включает и выключает ночной режим по расписанию. Вызывается тем же таймером,
        /// что обновляет часы, поэтому переключение происходит в течение 10 секунд
        /// после наступления нужного часа.
        /// </summary>
        private void ApplyNightMode(DateTime localNow)
        {
            bool shouldBeNight = FrameSettings.NightModeEnabled
                                 && FrameSettings.IsNightHour(localNow.Hour);

            if (_isNightModeActive == shouldBeNight)
            {
                return;
            }

            _isNightModeActive = shouldBeNight;
            NightOverlay.IsVisible = shouldBeNight;

            // Кадр часов рисуется заново при каждом входе в ночной режим.
            _lastRenderedNightMinute = null;

            if (shouldBeNight)
            {
                // Смена кадров ночью не нужна, и таймер незачем держать работающим.
                _slideshowTimer.Stop();
                ClockOverlay.IsVisible = false;

                // Ночью показания датчиков не выводим.
                SensorHost.IsVisible = false;

                // И тем более не проигрываем видео.
                StopVideoPlayback();
                VideoControls.IsVisible = false;
                return;
            }

            // Кадр 1280x800 незачем держать в памяти днём.
            NightClockImage.Source = null;
            ClockOverlay.IsVisible = FrameSettings.ShowClock;

            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            // Утром показываем следующий кадр сразу, не дожидаясь интервала.
            ShowNextPhoto();
            _slideshowTimer.Start();
        }

        /// <summary>
        /// Перерисовывает ночные часы, когда изменилась минута, и сдвигает шахматную маску.
        /// </summary>
        private async Task RefreshNightClockAsync(string formattedTime, string formattedDate)
        {
            // Отрисовываем только при смене минуты: кодирование PNG на весь экран
            // недёшево, а каждые 10 секунд картинка одна и та же.
            if (_lastRenderedNightMinute == formattedTime)
            {
                return;
            }

            _lastRenderedNightMinute = formattedTime;

            // Инверсия маски на каждом обновлении: светятся уже другие пиксели.
            _nightMaskPhaseShifted = !_nightMaskPhaseShifted;
            bool phaseShifted = _nightMaskPhaseShifted;

            DisplayInfo displayInfo = DeviceDisplay.Current.MainDisplayInfo;
            int widthPixels = (int)displayInfo.Width;
            int heightPixels = (int)displayInfo.Height;

            if (widthPixels <= 0 || heightPixels <= 0)
            {
                return;
            }

            try
            {
                byte[] pngBytes = await Task.Run(() => NightClockRenderer.RenderPng(
                    widthPixels, heightPixels, formattedTime, formattedDate, phaseShifted))
                    .ConfigureAwait(true);

                NightClockImage.Source = ImageSource.FromStream(() => new MemoryStream(pngBytes));
            }
            catch (Exception renderFailure)
            {
                // Не удалось — под изображением остаются обычные метки с часами.
                System.Diagnostics.Debug.WriteLine($"Ночные часы не отрисованы: {renderFailure}");
                _lastRenderedNightMinute = null;
            }
        }

        /// <summary>Переставляет часы в следующий угол — по одному шагу на кадр.</summary>
        private void MoveClockToNextPosition()
        {
            ApplyClockPosition((_clockPositionIndex + 1) % ClockPositions.Length);
        }

        private void ApplyClockPosition(int positionIndex)
        {
            _clockPositionIndex = positionIndex;
            (LayoutOptions horizontal, LayoutOptions vertical, Thickness margin) =
                ClockPositions[_clockPositionIndex];

            ClockOverlay.HorizontalOptions = horizontal;
            ClockOverlay.VerticalOptions = vertical;
            ClockOverlay.Margin = margin;
        }

        /// <summary>
        /// Подбирает для часов самый ровный угол снимка, избегая лиц.
        /// </summary>
        /// <remarks>
        /// Анализ идёт в фоне и применяется, только если кадр за это время не сменился:
        /// иначе часы прыгнули бы по данным уже неактуальной фотографии.
        /// </remarks>
        private async Task PlaceClockOverPhotoAsync(string photoPath, int photoGeneration)
        {
            int? bestCorner = await Task.Run(
                () => ClockPlacementAnalyzer.ChooseBestCorner(photoPath)).ConfigureAwait(true);

            if (bestCorner is null || photoGeneration != _photoGeneration)
            {
                return;
            }

            ApplyClockPosition(bestCorner.Value);
        }

        /// <summary>
        /// Касание переключает панель управления: первое показывает, второе убирает.
        /// Если не трогать экран, панель уходит сама через <see cref="PanelRevealSeconds"/> секунд.
        /// </summary>
        private void OnScreenTapped(object? sender, TappedEventArgs e)
        {
            _panelHideTimer.Stop();

            if (ControlPanel.IsVisible)
            {
                ControlPanel.IsVisible = false;
                UpdateVideoControlsVisibility();
                return;
            }

            ControlPanel.IsVisible = true;
            UpdateVideoControlsVisibility();
            _panelHideTimer.Start();
        }

        private void OnPanelHideTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ControlPanel.IsVisible = false;
                UpdateVideoControlsVisibility();
            });
        }

        /// <summary>
        /// Двойное касание переключает кадр. Сторона определяется по точке касания,
        /// а не двумя отдельными зонами: одна зона надёжнее в hit-testing и не спорит
        /// с распознавателем одиночного касания.
        /// </summary>
        private void OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            Point? tapPosition = e.GetPosition(TouchOverlay);

            // Если платформа не сообщила координату, трактуем как «вперёд».
            bool isLeftHalf = tapPosition is { } position
                              && TouchOverlay.Width > 0
                              && position.X < TouchOverlay.Width / 2;

            StepPhoto(isLeftHalf ? -1 : +1);
        }

        /// <summary>
        /// Ручное переключение кадра. Таймер перезапускается, иначе следующий кадр мог бы
        /// сменииться сразу после двойного касания.
        /// </summary>
        private void StepPhoto(int direction)
        {
            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            ShowPhotoAt(_currentPhotoIndex + direction);

            _slideshowTimer.Stop();
            _slideshowTimer.Start();
        }

        private async void OnSettingsClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync(nameof(SettingsPage));
        }

        private async void OnFileInfoClicked(object? sender, EventArgs e)
        {
            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            FileInfoPage.MediaPath = _localPhotoPaths[_currentPhotoIndex];
            await Shell.Current.GoToAsync(nameof(FileInfoPage));
        }

        private async void OnSyncClicked(object? sender, EventArgs e)
        {
            await SynchronizeAlbumAsync(forceDownload: true);
        }

        private void OnAlbumPollTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            _ = SynchronizeAlbumAsync(forceDownload: false);
        }

        /// <summary>
        /// Опрашивает включённые источники и, если набор снимков изменился, догружает новые.
        /// </summary>
        /// <param name="forceDownload">
        /// Обновлять даже если набор совпадает — для явного запроса пользователя.
        /// </param>
        private async Task SynchronizeAlbumAsync(bool forceDownload)
        {
            // Проверка по таймеру не должна прерывать уже идущую синхронизацию.
            if (!await _syncGate.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SyncButton.IsEnabled = false;
                    StatusLabel.Text = "Проверка источников...";
                });

                var downloadProgress = new Progress<(int Completed, int Total)>(progress =>
                    StatusLabel.Text = $"Загрузка {progress.Completed} из {progress.Total}...");

                AlbumSyncResult syncResult = await _photoSource
                    .RefreshAsync(forceDownload, downloadProgress)
                    .ConfigureAwait(true);

                FrameSettings.LastSyncUtc = DateTime.UtcNow;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    LoadPhotosFromCache();
                    StatusLabel.Text = DescribeSyncResult(syncResult);
                });
            }
            catch (PhotoSourceException syncFailure)
            {
                // Причина показывается на экране: у рамки нет консоли, и это
                // единственный канал диагностики для пользователя.
                await MainThread.InvokeOnMainThreadAsync(() => StatusLabel.Text = syncFailure.Message);
            }
            catch (Exception unexpectedFailure)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusLabel.Text = "Непредвиденная ошибка обновления.");
                System.Diagnostics.Debug.WriteLine(unexpectedFailure);
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() => SyncButton.IsEnabled = true);
                _syncGate.Release();
            }
        }

        /// <summary>
        /// Текст статуса. Отдельно показывает, сколько кадров пришлось качать, — так видно,
        /// что повторная синхронизация не перекачивает всё заново.
        /// </summary>
        private static string DescribeSyncResult(AlbumSyncResult syncResult)
        {
            string statusText;
            if (syncResult.DownloadedCount == 0 && syncResult.RemovedCount == 0)
            {
                statusText = $"Без изменений: {syncResult.TotalPhotoCount} фото";
            }
            else
            {
                // "убрано", а не "удалено": для папок на устройстве снимки лишь перестают
                // показываться, файлы остаются на месте. Счётчик общий для обоих
                // источников, поэтому формулировка должна быть верна и там, и там.
                string changeSummary = syncResult.RemovedCount == 0
                    ? $"новых {syncResult.DownloadedCount}"
                    : $"новых {syncResult.DownloadedCount}, убрано {syncResult.RemovedCount}";

                statusText = $"Обновлено: {syncResult.TotalPhotoCount} фото ({changeSummary})";
            }

            // Один источник мог отказать, а показывать всё равно есть что — не скрываем это.
            return syncResult.Warning is null
                ? statusText
                : $"{statusText}. {syncResult.Warning}";
        }

        private void LoadPhotosFromCache()
        {
            _localPhotoPaths = _photoSource.GetPhotoPaths();
            _currentPhotoIndex = -1;

            if (FrameSettings.ShufflePhotos)
            {
                ShufflePhotoOrder(_localPhotoPaths);
            }

            // Ночью слайд-шоу не крутится: новые снимки подхватятся утром.
            if (_localPhotoPaths.Count == 0 || _isNightModeActive == true)
            {
                _slideshowTimer.Stop();
                return;
            }

            ShowNextPhoto();
            _slideshowTimer.Start();
        }

        /// <summary>Перемешивание Фишера — Йетса по месту.</summary>
        private static void ShufflePhotoOrder(List<string> photoPaths)
        {
            for (int currentIndex = photoPaths.Count - 1; currentIndex > 0; currentIndex--)
            {
                int swapIndex = Random.Shared.Next(currentIndex + 1);
                (photoPaths[currentIndex], photoPaths[swapIndex]) =
                    (photoPaths[swapIndex], photoPaths[currentIndex]);
            }
        }

        private void OnSlideshowTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Таймер срабатывает в фоновом потоке, а Source трогать можно только из UI-потока.
            MainThread.BeginInvokeOnMainThread(ShowNextPhoto);
        }

        private void ShowNextPhoto()
        {
            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            // Новый круг — новый порядок, иначе перемешивание видно только один раз.
            if (FrameSettings.ShufflePhotos && _currentPhotoIndex + 1 >= _localPhotoPaths.Count)
            {
                ShufflePhotoOrder(_localPhotoPaths);
            }

            ShowPhotoAt(_currentPhotoIndex + 1);
        }

        private void ShowPhotoAt(int photoIndex)
        {
            int photoCount = _localPhotoPaths.Count;
            if (photoCount == 0)
            {
                return;
            }

            // Уходя с кадра, всегда гасим проигрыватель: иначе видео продолжало бы
            // играть под уже следующей фотографией.
            StopVideoPlayback();

            // Оператор % в C# сохраняет знак, поэтому для шага назад нужна нормализация.
            _currentPhotoIndex = ((photoIndex % photoCount) + photoCount) % photoCount;

            string mediaPath = _localPhotoPaths[_currentPhotoIndex];
            int photoGeneration = ++_photoGeneration;

            // Сразу переставляем часы по кругу: если разбор снимка не удастся или
            // затянется, надпись всё равно не останется на прежнем месте.
            MoveClockToNextPosition();

            _isCurrentSlideVideo = MediaFileTypes.IsVideo(mediaPath);
            UpdateVideoControlsVisibility();

            if (!_isCurrentSlideVideo)
            {
                SlideshowImage.Source = ImageSource.FromFile(mediaPath);

                if (FrameSettings.ShowClock)
                {
                    _ = PlaceClockOverPhotoAsync(mediaPath, photoGeneration);
                }

                return;
            }

            // У видео нет готовой картинки, поэтому показываем кадр из него самого:
            // он виден, пока проигрыватель готовится, и остаётся фоном при ошибке.
            SlideshowImage.Source = null;
            _ = ShowVideoPosterAsync(mediaPath, photoGeneration);

            // Видео начинается само, как только слайд-шоу до него дошло, и по окончании
            // рамка переходит к следующему кадру. Ночью — не начинается.
            if (_isNightModeActive != true)
            {
                StartVideoPlayback();
            }
        }

        private async Task ShowVideoPosterAsync(string videoPath, int photoGeneration)
        {
            string? posterPath = await Task.Run(() => VideoThumbnailCache.GetOrCreate(videoPath))
                .ConfigureAwait(true);

            if (photoGeneration != _photoGeneration || posterPath is null)
            {
                return;
            }

            SlideshowImage.Source = ImageSource.FromFile(posterPath);

            if (FrameSettings.ShowClock)
            {
                await PlaceClockOverPhotoAsync(posterPath, photoGeneration).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Кнопки видео живут по тем же правилам, что и панель управления: поверх снимка
        /// не должно быть ничего лишнего, пока экран не тронули.
        /// </summary>
        private void UpdateVideoControlsVisibility()
        {
            VideoControls.IsVisible = _isCurrentSlideVideo
                                      && _isNightModeActive != true
                                      && ControlPanel.IsVisible;

            PlayPauseButton.Text = _isVideoPlaying ? "⏸" : "▶";
            RepeatButton.Opacity = FrameSettings.VideoRepeat ? 1.0 : 0.45;
            MuteButton.Text = FrameSettings.VideoMuted ? "🔇" : "🔊";
        }

        private void OnPlayPauseClicked(object? sender, EventArgs e)
        {
            if (!_isCurrentSlideVideo || _localPhotoPaths.Count == 0)
            {
                return;
            }

            if (_isVideoPlaying)
            {
                VideoPlayer.Pause();
                _isVideoPlaying = false;

                // На паузе слайд-шоу тоже стоит: пользователь ещё смотрит этот кадр.
                UpdateVideoControlsVisibility();
                return;
            }

            StartVideoPlayback();
        }

        /// <summary>
        /// Запускает видео текущего кадра и придерживает слайд-шоу.
        /// </summary>
        private void StartVideoPlayback()
        {
            if (!_isCurrentSlideVideo || _localPhotoPaths.Count == 0)
            {
                return;
            }

            // Пока играет видео, кадры не сменяются.
            _slideshowTimer.Stop();

            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;
            VideoPlayer.SourcePath = _localPhotoPaths[_currentPhotoIndex];
            VideoPlayer.IsVisible = true;
            VideoPlayer.Play();

            _isVideoPlaying = true;
            UpdateVideoControlsVisibility();
        }

        private void OnRepeatClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoRepeat = !FrameSettings.VideoRepeat;
            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            UpdateVideoControlsVisibility();
        }

        private void OnMuteClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoMuted = !FrameSettings.VideoMuted;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;
            UpdateVideoControlsVisibility();
        }

        /// <summary>
        /// Видео доиграло. При включённом повторе сюда не приходим — проигрыватель
        /// начинает заново сам.
        /// </summary>
        private void OnVideoPlaybackFinished(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StopVideoPlayback();

                // Возвращаем обычный ход слайд-шоу и сразу переходим к следующему кадру.
                if (_localPhotoPaths.Count > 0 && _isNightModeActive != true)
                {
                    ShowNextPhoto();
                    _slideshowTimer.Start();
                }
            });
        }

        private void StopVideoPlayback()
        {
            if (!_isVideoPlaying && VideoPlayer.SourcePath is null)
            {
                return;
            }

            VideoPlayer.Stop();
            VideoPlayer.SourcePath = null;
            VideoPlayer.IsVisible = false;
            _isVideoPlaying = false;
        }
    }
}
