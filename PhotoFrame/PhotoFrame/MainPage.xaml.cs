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
        /// Неразрывный пробел между значком датчика и его значением.
        /// </summary>
        /// <remarks>
        /// Перенос по словам рвёт строку по любому пробелу, и значение уходило на другую
        /// строку в отрыве от своего значка. Значки между собой разделены обычными
        /// пробелами, поэтому переносится строка именно по ним. Константа, а не сам
        /// символ в коде: невидимый пробел в исходнике не отличить от обычного.
        /// </remarks>
        private const char NonBreakingSpace = '\u00A0';

        /// <summary>
        /// Предельная ширина строки датчиков в единицах устройства.
        /// </summary>
        /// <remarks>
        /// Примерно половина ширины экрана рамки (1280): дальше показания начинают
        /// перечёркивать середину кадра, а перенос по словам оставляет их у своего угла.
        /// </remarks>
        private const double SensorLineMaximumWidth = 620;

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

        /// <summary>Опрос позиции воспроизведения: у VideoView нет события о прогрессе.</summary>
        private readonly System.Timers.Timer _videoProgressTimer;

        /// <summary>Гасит всплывающее сообщение.</summary>
        private readonly System.Timers.Timer _toastHideTimer;

        /// <summary>Копии текста времени: восемь для обводки плюс одна основная.</summary>
        private readonly List<Label> _clockTimeLabels;
        private readonly List<Label> _clockDateLabels;
        private readonly List<Label> _sensorLabels;
        private readonly List<Label> _captureInfoLabels;
        private readonly List<Label> _photoCounterLabels;

        private readonly HomeAssistantClient _homeAssistantClient;

        /// <summary>Минута, для которой датчики уже перечитаны.</summary>
        private string? _lastSensorMinute;

        /// <summary>
        /// Показания датчиков одной строкой. Ночью попадают в сам кадр часов, поэтому
        /// нужны отдельно от меток наложения.
        /// </summary>
        private string? _sensorLineText;

        /// <summary>Не даём проверке по таймеру наложиться на нажатие кнопки.</summary>
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private List<string> _localPhotoPaths = new();

        /// <summary>Индекс показанного сейчас кадра. -1 — ещё ничего не показано.</summary>
        private int _currentPhotoIndex = -1;

        /// <summary>
        /// Сохранённый порядок показа уже пробовали восстановить.
        /// </summary>
        /// <remarks>
        /// Попытка ровно одна, при первой загрузке: дальше набор меняют обновления
        /// источников, и продолжать прежнюю последовательность уже незачем.
        /// </remarks>
        private bool _hasTriedRestoringOrder;

        private int _clockPositionIndex;

        /// <summary>Ночной режим сейчас активен. null — состояние ещё не определялось.</summary>
        private bool? _isNightModeActive;

        /// <summary>Минута, для которой уже нарисован ночной кадр.</summary>
        private string? _lastRenderedNightMinute;

        /// <summary>Текущая фаза шахматной маски ночных часов.</summary>
        private bool _nightMaskPhaseShifted;

        /// <summary>Кадры, лежащие в двух слоях ночных часов.</summary>
        private Android.Graphics.Bitmap? _frontNightFrame;
        private Android.Graphics.Bitmap? _backNightFrame;

        /// <summary>Показанный кадр лежит в верхнем слое.</summary>
        private bool _nightFrameInFrontLayer;

        /// <summary>Сколько кадров ночных часов отрисовано за время работы.</summary>
        private int _nightFramesRendered;

        /// <summary>Растёт на каждом кадре: отбрасывает анализ устаревшего снимка.</summary>
        private int _photoGeneration;

        /// <summary>Текущий кадр — видеофайл.</summary>
        private bool _isCurrentSlideVideo;

        /// <summary>Видео сейчас воспроизводится, слайд-шоу приостановлено.</summary>
        private bool _isVideoPlaying;

        /// <summary>У текущего кадра есть данные о съёмке.</summary>
        private bool _hasCaptureInfo;

        /// <summary>Кадр, о котором спрашивает выдвинутое подтверждение.</summary>
        private string? _pendingRemovalPath;

        /// <summary>Заставка кадра альбома, чей клип сейчас играет.</summary>
        private string? _currentAlbumVideoPoster;

        /// <summary>
        /// Играет клип живого фото, а не видео.
        /// </summary>
        /// <remarks>
        /// Отличается от воспроизведения видео тем, что слайд-шоу не остановлено, кнопок
        /// управления нет и по окончании кадр не листается.
        /// </remarks>
        private bool _isMotionPlayback;

        /// <summary>
        /// Файл, который проигрывается на текущем слайде.
        /// </summary>
        /// <remarks>
        /// Для видео из папки это сам слайд, а для кадра альбома — догруженный клип,
        /// лежащий рядом с заставкой.
        /// </remarks>
        private string? _currentVideoPath;

        public MainPage()
        {
            InitializeComponent();

            // Shell создаёт страницу через DataTemplate, минуя контейнер, поэтому сервис
            // достаём из провайдера вручную — иначе на каждый показ страницы появлялся бы
            // новый экземпляр со своим HttpClient.
            _photoSource = IPlatformApplication.Current?.Services.GetService<CompositePhotoSource>()
                           ?? new CompositePhotoSource(
                               new SharedAlbumPhotoSource(),
                               new ImmichPhotoSource(),
                               new LocalFolderPhotoSource());

            _clockTimeLabels = BuildOutlinedText(
                ClockTimeHost, fontSize: 68, isBold: true, Colors.White, TimeOutlineWidth);
            _clockDateLabels = BuildOutlinedText(
                ClockDateHost, fontSize: 22, isBold: false, Color.FromArgb("#F0F0F0"), DateOutlineWidth);
            _sensorLabels = BuildOutlinedText(
                SensorHost, fontSize: 24, isBold: true, Color.FromArgb("#BFEFFF"), DateOutlineWidth);
            _captureInfoLabels = BuildOutlinedText(
                CaptureInfoHost, fontSize: 17, isBold: false, Color.FromArgb("#D6D6D6"),
                DateOutlineWidth);
            _photoCounterLabels = BuildOutlinedText(
                PhotoCounterHost, fontSize: 17, isBold: false, Color.FromArgb("#D6D6D6"),
                DateOutlineWidth);

            // Датчиков можно выбрать сколько угодно, поэтому строка с показаниями
            // переносится по словам и не уезжает за край экрана.
            foreach (Label sensorLabel in _sensorLabels)
            {
                sensorLabel.LineBreakMode = LineBreakMode.WordWrap;
                sensorLabel.MaximumWidthRequest = SensorLineMaximumWidth;
            }

            _homeAssistantClient =
                IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _slideshowTimer = new System.Timers.Timer { AutoReset = true };
            _slideshowTimer.Elapsed += OnSlideshowTimerElapsed;

            _albumPollTimer = new System.Timers.Timer { AutoReset = true };
            _albumPollTimer.Elapsed += OnAlbumPollTimerElapsed;

            // Интервал задаётся в ApplySettings: он настраивается пользователем.
            _panelHideTimer = new System.Timers.Timer { AutoReset = false };
            _panelHideTimer.Elapsed += OnPanelHideTimerElapsed;

            // Минуты меняются раз в 60 секунд, но опрос раз в 10 секунд гарантирует,
            // что показанное время не отстанет заметно после выхода из сна.
            _clockTimer = new System.Timers.Timer(TimeSpan.FromSeconds(10).TotalMilliseconds)
            {
                AutoReset = true,
            };
            _clockTimer.Elapsed += OnClockTimerElapsed;

            // Полсекунды достаточно: полоса длиной 520 px на минутном клипе сдвигается
            // примерно на 4 px за такт, дробить мельче незачем.
            _videoProgressTimer = new System.Timers.Timer(500) { AutoReset = true };
            _videoProgressTimer.Elapsed += OnVideoProgressTimerElapsed;

            // Пяти секунд хватает, чтобы прочитать строку вроде «Обновлено: 468 фото».
            _toastHideTimer = new System.Timers.Timer(TimeSpan.FromSeconds(5).TotalMilliseconds)
            {
                AutoReset = false,
            };
            _toastHideTimer.Elapsed += OnToastHideTimerElapsed;

            VideoPlayer.PlaybackFinished += OnVideoPlaybackFinished;
            VideoPlayer.PlaybackFailed += OnVideoPlaybackFailed;

            // Разовая запись в журнал: какие форматы устройство вообще умеет
            // декодировать. Нужна, чтобы решать про HEVC и VP9 по данным.
            CodecProbe.LogVideoDecoders();

            // Раз в минуту — строка о состоянии рамки в файл. Системный журнал
            // перезапуск не переживает, а зависает рамка по ночам.
            FrameHeartbeat.Start(ReadHeartbeatState);
        }

        /// <summary>Состояние рамки для строки журнала.</summary>
        private HeartbeatState ReadHeartbeatState() => new(
            IsNightMode: _isNightModeActive == true,
            PhotoNumber: _currentPhotoIndex + 1,
            PhotoCount: _localPhotoPaths.Count,
            NightFramesRendered: _nightFramesRendered,
            PlayingClip: _currentVideoPath is null
                ? null
                : Path.GetFileName(_currentVideoPath));

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

        /// <summary>
        /// Показывает всплывающее сообщение и гасит его через несколько секунд.
        /// </summary>
        /// <remarks>
        /// Сообщение живёт отдельно от панели управления: результат обновления и ошибки
        /// должны быть видны без касания экрана, но и оставаться на снимке навсегда им
        /// незачем. Ночью сообщения не показываются — весь смысл ночного режима в том,
        /// чтобы экран не светил.
        /// </remarks>
        private void ShowToast(string text)
        {
            if (_isNightModeActive == true)
            {
                return;
            }

            ToastLabel.Text = text;
            ToastPanel.Opacity = 1;
            ToastPanel.IsVisible = true;

            // Каждое новое сообщение продлевает показ: во время загрузки они идут чередой.
            _toastHideTimer.Stop();
            _toastHideTimer.Start();
        }

        private void OnToastHideTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Плавное угасание: резкое исчезновение надписи поверх снимка заметнее,
                // чем само сообщение.
                await ToastPanel.FadeToAsync(0, 400).ConfigureAwait(true);
                ToastPanel.IsVisible = false;
            });
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

            _videoProgressTimer.Stop();

            // Уходя со страницы, освобождаем проигрыватель: иначе звук продолжится
            // на экране настроек.
            StopVideoPlayback();

            // И возвращаем обычную яркость: окно одно на все страницы, и приглушённый
            // экран сделал бы настройки нечитаемыми.
            ApplyScreenBrightness(nightMode: false);
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
                // Внутри значка стоит неразрывный пробел: перенос по словам иначе мог
                // оторвать значение от своего значка и увести его на другую строку.
                var parts = new List<string>(values.Count);
                foreach ((string entityId, string text) in values)
                {
                    string icon = FrameSettings.GetSensorIcon(entityId);
                    parts.Add(string.IsNullOrEmpty(icon) ? text : icon + NonBreakingSpace + text);
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (parts.Count == 0)
                    {
                        SensorHost.IsVisible = false;
                        return;
                    }

                    _sensorLineText = string.Join("   ", parts);
                    SetOutlinedText(_sensorLabels, _sensorLineText);
                    SensorHost.IsVisible = _isNightModeActive != true;

                    // Ночью показания входят в сам кадр часов, поэтому его нужно
                    // перерисовать: минута та же, а значения уже другие.
                    if (_isNightModeActive == true)
                    {
                        _lastRenderedNightMinute = null;
                        UpdateClock();
                    }
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
            _panelHideTimer.Interval =
                TimeSpan.FromSeconds(FrameSettings.PanelRevealSeconds).TotalMilliseconds;

            SlideshowImage.Aspect = FrameSettings.FillScreen ? Aspect.AspectFill : Aspect.AspectFit;

            // Панель всегда скрыта при возврате на экран: поверх фотографии не должно
            // быть ничего лишнего, а показывается она касанием.
            ControlPanel.IsVisible = false;
            UpdateTapRevealedOverlays();
            _panelHideTimer.Stop();
            ResetRemoveConfirm();

            ClockOverlay.IsVisible = FrameSettings.ShowClock;
            ClockDateHost.IsVisible = FrameSettings.ShowDate;

            ApplyNightClockAppearance();

            // Расписание могли изменить в настройках — пересчитываем режим с нуля.
            _isNightModeActive = null;

            // Таймер работает всегда, даже если часы поверх снимка отключены:
            // по нему же переключается ночной режим.
            UpdateClock();
            _clockTimer.Start();

            // Датчики перечитываются вместе со сменой минуты на часах — см. UpdateClock.
            _lastSensorMinute = null;
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

            // Значения датчиков обновляются вместе с показанным временем: раз в минуту,
            // а не по отдельному расписанию, из-за которого они выглядели устаревшими.
            if (_lastSensorMinute != formattedTime)
            {
                _lastSensorMinute = formattedTime;
                _ = RefreshSensorsAsync();
            }

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

            // Состояние могло быть неизвестно: ApplySettings сбрасывает его при каждом
            // возврате на страницу, и это не то же самое, что наступление утра.
            bool wasNight = _isNightModeActive == true;

            _isNightModeActive = shouldBeNight;
            NightOverlay.IsVisible = shouldBeNight;
            ApplyScreenBrightness(shouldBeNight);

            // Кадр часов рисуется заново при каждом входе в ночной режим.
            _lastRenderedNightMinute = null;

            if (shouldBeNight)
            {
                // Смена кадров ночью не нужна, и таймер незачем держать работающим.
                _slideshowTimer.Stop();
                ClockOverlay.IsVisible = false;

                // Ночью показания датчиков, подпись кадра и отметку видео не выводим.
                SensorHost.IsVisible = false;
                AlbumVideoBadge.IsVisible = false;

                // И тем более не проигрываем видео.
                StopVideoPlayback();
                VideoControls.IsVisible = false;
                return;
            }

            // Кадр 1280x800 незачем держать в памяти днём.
            ReleaseNightClockFrame();
            ClockOverlay.IsVisible = FrameSettings.ShowClock;

            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            // Утром показываем следующий кадр сразу, не дожидаясь интервала. А вот при
            // возврате со страницы настроек или сведений о файле кадр менять нельзя:
            // именно из-за этого слайд «сам» перещёлкивался при закрытии экрана.
            if (wasNight)
            {
                ShowNextPhoto();
            }

            _slideshowTimer.Start();
        }

        /// <summary>
        /// Красит резервные метки ночных часов так же, как рисованный кадр.
        /// </summary>
        /// <remarks>
        /// Метки видны, только если отрисовка кадра не удалась, но выглядеть при этом
        /// они должны так же: иначе сбой заодно менял бы цвет и яркость часов.
        /// </remarks>
        private void ApplyNightClockAppearance()
        {
            var clockColor = Color.FromArgb(FrameSettings.NightClockColorHex);

            NightTimeLabel.TextColor = clockColor;
            NightDateLabel.TextColor = clockColor;

            // Резервные метки гаснут вместе с рисунком, только вдвое сильнее: шахматной
            // маски, которая делит яркость кадра пополам, у них нет.
            double intensity = Math.Clamp(FrameSettings.NightClockIntensityPercent / 100d, 0.01, 1.0);
            NightFallbackClock.Opacity = intensity / 2;
        }

        /// <summary>
        /// Приглушает подсветку экрана на время ночных часов.
        /// </summary>
        /// <remarks>
        /// Яркость задаётся окну, а не системе: системная требует особого разрешения
        /// WRITE_SETTINGS, спорит с автоматическим режимом и осталась бы изменённой
        /// после удаления приложения. Значение окна перекрывает и автоматику, и действует
        /// только пока окно на экране.
        ///
        /// -1 (BrightnessOverrideNone) возвращает экран к системной яркости.
        /// </remarks>
        private static void ApplyScreenBrightness(bool nightMode)
        {
            int brightnessPercent = FrameSettings.NightScreenBrightnessPercent;

            // 0 — пользователь не захотел, чтобы рамка трогала подсветку.
            float brightness = nightMode && brightnessPercent > 0
                ? Math.Clamp(brightnessPercent, 1, 100) / 100f
                : -1f;

            try
            {
                Android.Views.Window? window = Platform.CurrentActivity?.Window;
                if (window?.Attributes is not Android.Views.WindowManagerLayoutParams attributes)
                {
                    return;
                }

                attributes.ScreenBrightness = brightness;
                window.Attributes = attributes;
            }
            catch (Java.Lang.Throwable brightnessFailure)
            {
                // Рамка просто останется на системной яркости.
                System.Diagnostics.Debug.WriteLine(
                    $"Яркость экрана не изменена: {brightnessFailure.Message}");
            }
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
                string colorHex = FrameSettings.NightClockColorHex;

                // Датчики показываются и ночью, но только если их вообще просили показывать.
                string? sensorText = FrameSettings.ShowSensors ? _sensorLineText : null;

                int intensityPercent = FrameSettings.NightClockIntensityPercent;

                // Рисуем в кадр незанятого слоя: он уже отцеплен от своего ImageView,
                // и переиспользовать его безопасно. Так за ночь не выделяется по четыре
                // мегабайта в минуту.
                Android.Graphics.Bitmap? reusableFrame =
                    _nightFrameInFrontLayer ? _backNightFrame : _frontNightFrame;

                Android.Graphics.Bitmap renderedFrame = await Task.Run(
                    () => NightClockRenderer.RenderBitmap(
                        widthPixels, heightPixels, formattedTime, formattedDate, sensorText,
                        phaseShifted, colorHex, intensityPercent, reusableFrame))
                    .ConfigureAwait(true);

                _nightFramesRendered++;

                ShowNightClockFrame(renderedFrame);
            }
            catch (Exception renderFailure)
            {
                // Не удалось — под изображением остаются обычные метки с часами.
                System.Diagnostics.Debug.WriteLine($"Ночные часы не отрисованы: {renderFailure}");
                _lastRenderedNightMinute = null;
            }
        }

        /// <summary>
        /// Показывает готовый кадр ночных часов, подменяя картинку без пропадания.
        /// </summary>
        /// <remarks>
        /// Кадр отдаётся платформенному ImageView напрямую: свойство Source в MAUI
        /// загружается асинхронно и на это время гасит картинку, из-за чего на смене
        /// минуты экран заметно мигал чёрным.
        ///
        /// Слои чередуются. Новый кадр всегда попадает в свободный слой, и лишь после
        /// этого гасится тот, что показывал прошлую минуту: пока новый кадр не на месте,
        /// на экране остаётся прежний. С одним слоем оставался один чёрный кадр — между
        /// очисткой ImageView и отрисовкой нового содержимого.
        /// </remarks>
        private void ShowNightClockFrame(Android.Graphics.Bitmap renderedFrame)
        {
            bool intoFrontLayer = !_nightFrameInFrontLayer;

            Image targetLayer = intoFrontLayer ? NightClockFrontImage : NightClockBackImage;
            Image previousLayer = intoFrontLayer ? NightClockBackImage : NightClockFrontImage;

            if (!TrySetLayerFrame(targetLayer, renderedFrame))
            {
                // Обработчик ещё не создан — кадр покажется на следующей минуте,
                // а пока видны резервные метки.
                if (!ReferenceEquals(renderedFrame, _frontNightFrame)
                    && !ReferenceEquals(renderedFrame, _backNightFrame))
                {
                    ReleaseFrame(renderedFrame);
                }

                _lastRenderedNightMinute = null;
                return;
            }

            if (intoFrontLayer)
            {
                ReleaseFrame(_frontNightFrame, renderedFrame);
                _frontNightFrame = renderedFrame;
            }
            else
            {
                ReleaseFrame(_backNightFrame, renderedFrame);
                _backNightFrame = renderedFrame;
            }

            // Прежний слой только отцепляем: его кадр пригодится через минуту.
            DetachLayer(previousLayer);
            _nightFrameInFrontLayer = intoFrontLayer;
        }

        private static bool TrySetLayerFrame(Image layer, Android.Graphics.Bitmap frame)
        {
            if (layer.Handler?.PlatformView is not Android.Widget.ImageView imageView)
            {
                return false;
            }

            imageView.SetImageBitmap(frame);
            return true;
        }

        /// <summary>Отцепляет кадр от слоя, не трогая сам кадр.</summary>
        private static void DetachLayer(Image layer)
        {
            if (layer.Handler?.PlatformView is Android.Widget.ImageView imageView)
            {
                imageView.SetImageBitmap(null);
            }
        }

        /// <summary>
        /// Освобождает кадр, если он больше не нужен.
        /// </summary>
        /// <remarks>
        /// Recycle, а не только Dispose: пиксели живут в native-куче, и без него они
        /// ждут сборщика мусора — на устройстве с гигабайтом памяти это и есть та самая
        /// утечка по четыре мегабайта в минуту.
        /// </remarks>
        private static void ReleaseFrame(
            Android.Graphics.Bitmap? frame, Android.Graphics.Bitmap? keepIfSame = null)
        {
            if (frame is null || ReferenceEquals(frame, keepIfSame))
            {
                return;
            }

            frame.Recycle();
            frame.Dispose();
        }

        /// <summary>Убирает ночные часы с экрана и освобождает оба кадра.</summary>
        private void ReleaseNightClockFrame()
        {
            DetachLayer(NightClockFrontImage);
            DetachLayer(NightClockBackImage);

            ReleaseFrame(_frontNightFrame);
            ReleaseFrame(_backNightFrame);

            _frontNightFrame = null;
            _backNightFrame = null;
            _nightFrameInFrontLayer = false;
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

            ApplyClockContentAlignment(horizontal.Alignment == LayoutAlignment.End);
        }

        /// <summary>
        /// Разворачивает содержимое блока часов к тому краю, у которого он стоит.
        /// </summary>
        /// <remarks>
        /// Ширину блока задаёт самая длинная строка — время. В правых углах дата и
        /// показания датчиков оказывались прижаты к её левому краю, то есть висели с
        /// отступом от края экрана. Небольшие отступы строк тоже зеркалятся, иначе
        /// в правых углах они отодвигают текст не от края, а от времени.
        /// </remarks>
        private void ApplyClockContentAlignment(bool alignToRight)
        {
            TextAlignment textAlignment = alignToRight ? TextAlignment.End : TextAlignment.Start;
            LayoutOptions hostAlignment = alignToRight ? LayoutOptions.End : LayoutOptions.Start;

            foreach (List<Label> labels in
                     new[] { _clockTimeLabels, _clockDateLabels, _sensorLabels })
            {
                foreach (Label label in labels)
                {
                    label.HorizontalTextAlignment = textAlignment;
                }
            }

            ClockTimeHost.HorizontalOptions = hostAlignment;
            ClockDateHost.HorizontalOptions = hostAlignment;
            SensorHost.HorizontalOptions = hostAlignment;

            ClockDateHost.Margin = alignToRight
                ? new Thickness(0, -8, 4, 0)
                : new Thickness(4, -8, 0, 0);

            SensorHost.Margin = alignToRight
                ? new Thickness(0, 4, 4, 0)
                : new Thickness(4, 4, 0, 0);
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
        /// Если не трогать экран, панель уходит сама через настроенное время.
        /// </summary>
        private void OnScreenTapped(object? sender, TappedEventArgs e)
        {
            _panelHideTimer.Stop();

            if (ControlPanel.IsVisible)
            {
                ControlPanel.IsVisible = false;
                UpdateTapRevealedOverlays();

                // Панель ушла вместе с вопросом — считаем это отказом и возвращаем показ.
                if (ResetRemoveConfirm())
                {
                    _slideshowTimer.Start();
                }

                return;
            }

            ControlPanel.IsVisible = true;
            UpdateTapRevealedOverlays();
            _panelHideTimer.Start();
        }

        private void OnPanelHideTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ControlPanel.IsVisible = false;
                UpdateTapRevealedOverlays();
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

            string mediaPath = _localPhotoPaths[_currentPhotoIndex];

            // У кадра альбома слайд — это заставка, а сведения нужны о самом клипе:
            // его размер, разрешение и длительность. Пока клип не скачан, показываем
            // то, что есть, — заставку.
            FileInfoPage.MediaPath = _currentVideoPath
                                     ?? SlideClips.FindReady(mediaPath)
                                     ?? mediaPath;

            await Shell.Current.GoToAsync(nameof(FileInfoPage));
        }

        /// <summary>
        /// Убирает показанный кадр из слайд-шоу, перенося файл в корзину.
        /// </summary>
        /// <remarks>
        /// Кадр запоминается до вопроса, а таймеры на это время останавливаются: иначе
        /// слайд-шоу успело бы шагнуть дальше и в корзину уехал бы не тот файл.
        /// </remarks>
        private async void OnRemoveClicked(object? sender, EventArgs e)
        {
            if (_localPhotoPaths.Count == 0 || _pendingRemovalPath is not null)
            {
                return;
            }

            // Пока висит вопрос, кадр не должен смениться, иначе в корзину уехал бы
            // не тот файл. Панель тоже не убираем сама собой.
            _slideshowTimer.Stop();
            _panelHideTimer.Stop();

            _pendingRemovalPath = _localPhotoPaths[_currentPhotoIndex];

            RemoveConfirmPanel.IsVisible = true;
            await AnimateRemoveConfirmAsync(shown: true).ConfigureAwait(true);
        }

        private async void OnCancelRemoveClicked(object? sender, EventArgs e)
        {
            await HideRemoveConfirmAsync().ConfigureAwait(true);

            _panelHideTimer.Start();
            _slideshowTimer.Start();
        }

        private async void OnConfirmRemoveClicked(object? sender, EventArgs e)
        {
            string? mediaPath = _pendingRemovalPath;
            await HideRemoveConfirmAsync().ConfigureAwait(true);

            if (mediaPath is null)
            {
                return;
            }

            // Проигрыватель держит файл открытым, и переименование под ним не пройдёт.
            StopVideoPlayback();

            try
            {
                await MediaTrash.MoveToTrashAsync(mediaPath).ConfigureAwait(true);
            }
            catch (PhotoSourceException trashFailure)
            {
                ShowToast(trashFailure.Message);
                _panelHideTimer.Start();
                _slideshowTimer.Start();
                return;
            }

            // Запись на сервере осталась, поэтому кадр надо ещё и внести в список убранных,
            // иначе следующая синхронизация скачает его заново.
            if (MediaTrash.IsDownloadedPhoto(mediaPath))
            {
                FrameSettings.AddTrashedAlbumFileName(Path.GetFileName(mediaPath));
            }

            // У Immich есть своя корзина, и убрать кадр только на рамке мало: на телефоне
            // и в браузере снимок остался бы на месте. Ссылку на общий альбом Google так
            // не тронешь — там правит только владелец альбома.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);
            if (immichSlide is not null)
            {
                _ = TrashOnImmichServerAsync(immichSlide.AssetId);
            }

            RemoveCurrentPhotoFromShow();
        }

        /// <summary>
        /// Убирает кадр и в корзину самого Immich.
        /// </summary>
        /// <remarks>
        /// Не дожидаясь ответа: кадр с рамки уже убран, и держать ради сетевого запроса
        /// застывшую панель незачем. Сообщаем только об отказе — обычно это ключ доступа,
        /// созданный без права на удаление.
        /// </remarks>
        private async Task TrashOnImmichServerAsync(string assetId)
        {
            ImmichPhotoSource immichSource =
                IPlatformApplication.Current?.Services.GetService<ImmichPhotoSource>()
                ?? new ImmichPhotoSource();

            string? failureMessage = await immichSource
                .TryTrashOnServerAsync(assetId)
                .ConfigureAwait(true);

            if (failureMessage is not null)
            {
                ShowToast($"С рамки убрано, но в Immich осталось: {failureMessage}");
            }
        }

        /// <summary>
        /// Выдвигает подтверждение вправо от кнопки и убирает его обратно.
        /// </summary>
        /// <remarks>
        /// Смещение отрицательное в скрытом виде, поэтому панель выезжает из-под самой
        /// кнопки, а не появляется рядом с ней.
        /// </remarks>
        private Task AnimateRemoveConfirmAsync(bool shown)
        {
            return Task.WhenAll(
                RemoveConfirmPanel.TranslateToAsync(shown ? 0 : -44, 0, 180),
                RemoveConfirmPanel.FadeToAsync(shown ? 1 : 0, 180));
        }

        private async Task HideRemoveConfirmAsync()
        {
            _pendingRemovalPath = null;

            await AnimateRemoveConfirmAsync(shown: false).ConfigureAwait(true);
            RemoveConfirmPanel.IsVisible = false;
        }

        /// <summary>
        /// Убирает подтверждение без анимации. Возвращает true, если вопрос действительно
        /// висел, — тогда вызывающий код возобновляет показ.
        /// </summary>
        /// <remarks>
        /// Нужно, когда панель управления исчезает целиком: анимировать то, что уже
        /// скрыто, незачем, а состояние сбросить обязательно.
        /// </remarks>
        private bool ResetRemoveConfirm()
        {
            if (_pendingRemovalPath is null)
            {
                return false;
            }

            _pendingRemovalPath = null;

            RemoveConfirmPanel.IsVisible = false;
            RemoveConfirmPanel.Opacity = 0;
            RemoveConfirmPanel.TranslationX = -44;
            return true;
        }

        /// <summary>
        /// Выбрасывает показанный кадр из списка и переходит к следующему.
        /// </summary>
        /// <remarks>
        /// Манифесты источников не переписываются: и альбом, и папки при чтении пропускают
        /// файлы, которых нет на диске, поэтому убранный кадр не вернётся и после перезапуска.
        /// </remarks>
        private void RemoveCurrentPhotoFromShow()
        {
            int removedIndex = _currentPhotoIndex;
            _localPhotoPaths.RemoveAt(removedIndex);

            // Порядок изменился — сохранённый список без этого стал бы неприменим целиком.
            SlideshowStateStore.SaveOrder(_localPhotoPaths);

            ShowToast($"Убрано в корзину. Осталось {_localPhotoPaths.Count} фото");

            if (_localPhotoPaths.Count == 0)
            {
                SlideshowImage.Source = null;
                _hasCaptureInfo = false;
                _isCurrentSlideVideo = false;
                _currentPhotoIndex = -1;
                UpdateTapRevealedOverlays();
                return;
            }

            _panelHideTimer.Start();

            // Прежний индекс теперь указывает на следующий кадр; ShowPhotoAt приведёт
            // его к границам списка сам.
            ShowPhotoAt(removedIndex);
            _slideshowTimer.Start();
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
                    ShowToast("Проверка источников...");
                });

                // Прогресс приходит только от альбома: локальные папки ничего не качают,
                // и писать "Загрузка" про обход файлов было бы неправдой.
                var downloadProgress = new Progress<(int Completed, int Total)>(progress =>
                    ShowToast($"Загрузка из альбома: {progress.Completed} из {progress.Total}..."));

                AlbumSyncResult syncResult = await _photoSource
                    .RefreshAsync(forceDownload, downloadProgress)
                    .ConfigureAwait(true);

                FrameSettings.LastSyncUtc = DateTime.UtcNow;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    LoadPhotosFromCache();
                    ShowToast(DescribeSyncResult(syncResult));
                });
            }
            catch (PhotoSourceException syncFailure)
            {
                // Причина показывается на экране: у рамки нет консоли, и это
                // единственный канал диагностики для пользователя.
                await MainThread.InvokeOnMainThreadAsync(() => ShowToast(syncFailure.Message));
            }
            catch (Exception unexpectedFailure)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    ShowToast("Непредвиденная ошибка обновления."));
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
                // Ни времени, ни даты: сообщение и так живёт пять секунд и появляется
                // сразу после проверки, так что «когда» очевидно из самого его появления.
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

            // Кадров меньше, чем есть в источнике, — и это надо объяснить, иначе кажется,
            // что показывается всё. Причины две, и путать их нельзя: предел задан
            // человеком, а неудачная загрузка — нет.
            if (syncResult.AvailableCount > syncResult.TotalPhotoCount)
            {
                int photoLimit = FrameSettings.AlbumPhotoLimit;

                statusText += photoLimit > 0 && syncResult.AvailableCount > photoLimit
                    ? $" из {syncResult.AvailableCount} в источнике (предел загрузки)"
                    : $" из {syncResult.AvailableCount} в источнике";
            }

            // Один источник мог отказать, а показывать всё равно есть что — не скрываем это.
            return syncResult.Warning is null
                ? statusText
                : $"{statusText}. {syncResult.Warning}";
        }

        /// <summary>
        /// Перечитывает список кадров, задавая порядок показа.
        /// </summary>
        /// <remarks>
        /// Порядок при случайном показе создаётся ровно один раз — в момент обновления
        /// набора. Пока набор тот же, список и текущее место сохраняются: иначе возврат
        /// с экрана настроек или из сведений о файле пересоздавал бы порядок, и шаг назад
        /// приводил к кадрам, которых в этом показе ещё не было.
        /// </remarks>
        private void LoadPhotosFromCache()
        {
            List<string> loadedPhotoPaths = _photoSource.GetPhotoPaths();

            // Сравнение строит множество из трёх тысяч путей, поэтому считается один раз.
            bool sameSet = HasSamePhotoSet(loadedPhotoPaths);

            if (sameSet && _currentPhotoIndex >= 0)
            {
                // Набор тот же — показ продолжается с того же кадра. Таймер при уходе
                // со страницы останавливается, поэтому его нужно завести снова.
                if (_isNightModeActive != true && !_isVideoPlaying)
                {
                    _slideshowTimer.Stop();
                    _slideshowTimer.Start();
                }

                return;
            }

            int restoredIndex = -1;

            if (!sameSet)
            {
                _localPhotoPaths = loadedPhotoPaths;
                _currentPhotoIndex = -1;

                // При первой загрузке пробуем продолжить с того же кадра и в том же
                // порядке, что были до выключения рамки.
                if (!_hasTriedRestoringOrder)
                {
                    _hasTriedRestoringOrder = true;
                    restoredIndex = SlideshowStateStore.TryRestoreOrder(_localPhotoPaths);
                }

                if (restoredIndex < 0)
                {
                    if (FrameSettings.ShufflePhotos)
                    {
                        ShufflePhotoOrder(_localPhotoPaths);
                    }

                    SlideshowStateStore.SaveOrder(_localPhotoPaths);
                }
            }

            // Ночью слайд-шоу не крутится: новые снимки подхватятся утром.
            if (_localPhotoPaths.Count == 0 || _isNightModeActive == true)
            {
                _slideshowTimer.Stop();
                return;
            }

            if (restoredIndex >= 0)
            {
                ShowPhotoAt(restoredIndex);
            }
            else
            {
                ShowNextPhoto();
            }

            _slideshowTimer.Start();
        }

        /// <summary>
        /// True, если набор кадров совпадает с показываемым — без учёта порядка.
        /// </summary>
        private bool HasSamePhotoSet(List<string> loadedPhotoPaths)
        {
            if (loadedPhotoPaths.Count != _localPhotoPaths.Count)
            {
                return false;
            }

            var shownPaths = new HashSet<string>(_localPhotoPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string loadedPath in loadedPhotoPaths)
            {
                if (!shownPaths.Contains(loadedPath))
                {
                    return false;
                }
            }

            return true;
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

            // Порядок не пересоздаётся на новом круге: иначе шаг назад после последнего
            // кадра уводил бы в уже другую случайную последовательность. Новый порядок
            // появляется вместе с новым набором снимков — см. LoadPhotosFromCache.
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

            // Чтобы после включения рамки показ продолжился с этого же кадра.
            SlideshowStateStore.SaveLastShown(mediaPath);

            // Источник или имя файла слева от счётчика: сам счётчик остаётся прижатым
            // к углу. Кадр альбома назван хэшем ссылки, и такое имя не говорит ничего —
            // куда полезнее знать, что снимок пришёл из Google Photos.
            // Имя файла кадра Immich — хэш, и подписывать им кадр бессмысленно; зато
            // исходное имя сохранено при синхронизации, и оно как раз читаемое.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            string frameLabel = MediaTrash.IsAlbumPhoto(mediaPath)
                ? "Google Photos"
                : immichSlide is not null
                    ? "Immich/" + immichSlide.FileName
                    : MediaTrash.IsImmichPhoto(mediaPath)
                        // Список ещё не составлен — до ближайшей синхронизации у кадра
                        // есть только имя-хэш, и показывать его незачем.
                        ? "Immich"
                        : MediaLabelFormatter.Describe(mediaPath);

            SetOutlinedText(
                _photoCounterLabels, $"{frameLabel}  ·  {_currentPhotoIndex + 1}/{photoCount}");

            // Сразу переставляем часы по кругу: если разбор снимка не удастся или
            // затянется, надпись всё равно не останется на прежнем месте.
            MoveClockToNextPosition();

            _isCurrentSlideVideo = MediaFileTypes.IsVideo(mediaPath);
            _currentVideoPath = _isCurrentSlideVideo ? mediaPath : null;
            _currentAlbumVideoPoster = null;
            UpdateTapRevealedOverlays();

            // Подпись читается для любого кадра, и для видео тоже: дата съёмки есть
            // и в контейнере.
            _ = ShowCaptureInfoAsync(mediaPath, photoGeneration);

            if (!_isCurrentSlideVideo)
            {
                SlideshowImage.Source = ImageSource.FromFile(mediaPath);

                if (FrameSettings.ShowClock)
                {
                    _ = PlaceClockOverPhotoAsync(mediaPath, photoGeneration);
                }

                // За сетевым кадром может стоять клип: заставка уже на экране, а сам
                // файл забирается только теперь — см. SlideClips.
                SlideClip? slideClip = FrameSettings.DownloadAlbumVideos
                    ? SlideClips.Find(mediaPath)
                    : null;

                AlbumVideoBadge.IsVisible = false;

                if (slideClip is { IsMotionPhoto: false })
                {
                    ShowAlbumVideoBadge(slideClip.DurationMilliseconds, isReady: false);
                    _ = PlayAlbumVideoAsync(mediaPath, photoGeneration);
                }
                else if (slideClip is { IsMotionPhoto: true } && FrameSettings.AnimateMotionPhotos)
                {
                    // Живое фото оживает молча: ни отметки, ни полосы загрузки — кадр
                    // должен выглядеть снимком, который просто на секунду ожил.
                    _ = AnimateMotionPhotoAsync(mediaPath, photoGeneration);
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

        /// <summary>
        /// Подписывает кадр тем, чем и когда он снят.
        /// </summary>
        /// <remarks>
        /// Чтение EXIF — обращение к диску, поэтому идёт в фоне; результат применяется
        /// только если кадр за это время не сменился. Если данных нет, строка убирается:
        /// подпись «неизвестно» поверх фотографии никому не нужна.
        /// </remarks>
        private async Task ShowCaptureInfoAsync(string mediaPath, int photoGeneration)
        {
            if (!FrameSettings.ShowCaptureInfo)
            {
                _hasCaptureInfo = false;
                UpdateTapRevealedOverlays();
                return;
            }

            // У кадра Immich EXIF читать неоткуда: рамка показывает превью с сервера,
            // а из него съёмочные поля вырезаны. Зато сервер отдал их при синхронизации.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            MediaDetailsReader.CaptureInfo captureInfo = immichSlide is null
                ? await Task
                    .Run(() => MediaDetailsReader.ReadCaptureInfo(mediaPath))
                    .ConfigureAwait(true)
                : new MediaDetailsReader.CaptureInfo(
                    immichSlide.CameraName.Length == 0 ? null : immichSlide.CameraName,
                    immichSlide.TakenAt);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            var parts = new List<string>(2);

            if (!string.IsNullOrEmpty(captureInfo.Device))
            {
                parts.Add(captureInfo.Device);
            }

            if (captureInfo.TakenAt is { } takenAt)
            {
                parts.Add(takenAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
            }

            _hasCaptureInfo = parts.Count > 0;

            if (_hasCaptureInfo)
            {
                SetOutlinedText(_captureInfoLabels, string.Join("  ·  ", parts));
            }

            UpdateTapRevealedOverlays();
        }

        /// <summary>
        /// Догружает видео кадра альбома и, если слайд ещё на экране, включает его.
        /// </summary>
        /// <remarks>
        /// Пока клип качается, на экране остаётся заставка — слайд-шоу идёт своим ходом.
        /// Если кадр за это время сменился, найденный файл просто остаётся в кэше
        /// и сыграет на следующем круге, уже без ожидания.
        /// </remarks>
        private async Task PlayAlbumVideoAsync(string posterPath, int photoGeneration)
        {
            // Progress создан в UI-потоке, поэтому его обратные вызовы приходят туда же.
            var downloadProgress = new Progress<double>(fraction =>
            {
                if (photoGeneration == _photoGeneration)
                {
                    AlbumVideoProgressBar.Progress = Math.Clamp(fraction, 0, 1);
                }
            });

            string? videoPath = await SlideClips
                .TryGetAsync(posterPath, downloadProgress).ConfigureAwait(true);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            if (videoPath is null)
            {
                // Заставка остаётся на экране, но молчать не стоит: иначе кадр выглядит
                // обычным снимком, который почему-то помечен как видео.
                AlbumVideoBadge.IsVisible = false;
                ShowToast("Видео не загрузилось");
                return;
            }

            if (_isNightModeActive == true)
            {
                return;
            }

            AlbumVideoBadge.IsVisible = false;

            _isCurrentSlideVideo = true;
            _currentVideoPath = videoPath;
            _currentAlbumVideoPoster = posterPath;
            UpdateTapRevealedOverlays();

            StartVideoPlayback();
        }

        /// <summary>
        /// Клип не открылся: отмечаем его и идём дальше.
        /// </summary>
        /// <remarks>
        /// Часть видео Google хранит в VP9, а на рамке этот кодек только программный —
        /// такие файлы проигрыватель не открывает вовсе и показывает нулевую длину.
        /// Отметка не даёт качать их снова на каждом круге.
        /// </remarks>
        private void OnVideoPlaybackFailed(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                string? posterPath = _currentAlbumVideoPoster;
                bool wasMotionPlayback = _isMotionPlayback;

                StopVideoPlayback();
                AlbumVideoBadge.IsVisible = false;

                if (posterPath is not null)
                {
                    SlideClips.MarkUnplayable(posterPath);

                    // О живом фото не сообщаем: оно и так остаётся снимком, а сообщение
                    // на каждый такой кадр было бы навязчивым.
                    if (!wasMotionPlayback)
                    {
                        ShowToast("Это видео рамка не проигрывает — оставили кадром");
                    }
                }

                _isCurrentSlideVideo = false;
                _currentVideoPath = null;
                _currentAlbumVideoPoster = null;
                UpdateTapRevealedOverlays();

                // Показ продолжается: на экране остаётся заставка, а дальше обычный ход.
                if (_localPhotoPaths.Count > 0 && _isNightModeActive != true)
                {
                    _slideshowTimer.Stop();
                    _slideshowTimer.Start();
                }
            });
        }

        /// <summary>
        /// Оживляет живое фото: проигрывает клип один раз и оставляет кадр снимком.
        /// </summary>
        /// <remarks>
        /// Слайд-шоу при этом не останавливается: кадр держится своё обычное время, а
        /// секунда движения — лишь его начало. Так ведут себя живые фото на телефоне,
        /// и повторять их по кругу незачем.
        /// </remarks>
        private async Task AnimateMotionPhotoAsync(string posterPath, int photoGeneration)
        {
            string? clipPath = await SlideClips.TryGetAsync(posterPath).ConfigureAwait(true);

            if (clipPath is null
                || photoGeneration != _photoGeneration
                || _isNightModeActive == true)
            {
                return;
            }

            _isMotionPlayback = true;
            _currentAlbumVideoPoster = posterPath;

            VideoPlayer.IsLooping = false;
            VideoPlayer.IsMuted = true;
            VideoPlayer.SourcePath = clipPath;
            VideoPlayer.Play();
        }

        /// <summary>Отметка «за этим кадром видео» с его длительностью.</summary>
        private void ShowAlbumVideoBadge(int durationMilliseconds, bool isReady)
        {
            // Длительность известна не всегда: в выдаче Immich её у части объектов
            // просто нет. Показывать «0:00» в таком случае хуже, чем не показывать
            // ничего, — отметка нужна, чтобы кадр не выглядел обычным снимком.
            string clipTime = durationMilliseconds > 0
                ? "  " + ClipTimeFormatter.Describe(durationMilliseconds)
                : string.Empty;

            AlbumVideoBadgeLabel.Text = isReady
                ? $"▶{clipTime}"
                : $"▶{clipTime}  ·  загрузка…";

            AlbumVideoProgressBar.Progress = 0;
            AlbumVideoProgressBar.IsVisible = !isReady;
            AlbumVideoBadge.IsVisible = _isNightModeActive != true;
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
        /// Кнопки видео и подпись кадра живут по тем же правилам, что и панель
        /// управления: поверх снимка не должно быть ничего лишнего, пока экран не тронули.
        /// </summary>
        private void UpdateTapRevealedOverlays()
        {
            bool panelVisible = ControlPanel.IsVisible;
            bool isDayTime = _isNightModeActive != true;

            VideoControls.IsVisible = _isCurrentSlideVideo && isDayTime && panelVisible;
            CaptureInfoHost.IsVisible = _hasCaptureInfo && isDayTime && panelVisible;

            PhotoCounterHost.IsVisible =
                _localPhotoPaths.Count > 0 && _currentPhotoIndex >= 0 && isDayTime && panelVisible;

            // Ночью показывать нечего: обновлять, смотреть сведения и убирать кадр —
            // всё это про снимок, которого на экране нет. Настройки остаются.
            PanelLeftButtons.IsVisible = isDayTime;

            PlayPauseButton.Text = _isVideoPlaying ? "⏸" : "▶";
            RepeatButton.Opacity = FrameSettings.VideoRepeat ? 1.0 : 0.45;
            MuteButton.Text = FrameSettings.VideoMuted ? "🔇" : "🔊";

            int volumePercent = SystemVolume.Percent;
            VolumeLabel.Text = FrameSettings.VideoMuted
                ? "тихо"
                : volumePercent < 0 ? "—" : $"{volumePercent}%";
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

                // Полосу оставляем на месте: пауза — это не сброс позиции.
                _videoProgressTimer.Stop();

                // На паузе слайд-шоу тоже стоит: пользователь ещё смотрит этот кадр.
                UpdateTapRevealedOverlays();
                return;
            }

            StartVideoPlayback();
        }

        /// <summary>
        /// Запускает видео текущего кадра и придерживает слайд-шоу.
        /// </summary>
        private void StartVideoPlayback()
        {
            if (!_isCurrentSlideVideo || _localPhotoPaths.Count == 0 || _currentVideoPath is null)
            {
                return;
            }

            // Пока играет видео, кадры не сменяются.
            _slideshowTimer.Stop();

            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;

            // Для кадра альбома путь ведёт к догруженному клипу, а не к самому слайду:
            // слайд — это заставка.
            VideoPlayer.SourcePath = _currentVideoPath;
            VideoPlayer.Play();

            _isVideoPlaying = true;
            _videoProgressTimer.Start();
            UpdateTapRevealedOverlays();
        }

        private void OnVideoProgressTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(UpdateVideoProgress);
        }

        /// <summary>
        /// Обновляет полосу позиции. Пока файл не подготовлен, длительность приходит
        /// отрицательной — в этом случае полосу не трогаем.
        /// </summary>
        private void UpdateVideoProgress()
        {
            (int positionMilliseconds, int durationMilliseconds) = VideoPlayer.QueryProgress();

            if (durationMilliseconds <= 0)
            {
                return;
            }

            VideoProgressBar.Progress =
                Math.Clamp(positionMilliseconds / (double)durationMilliseconds, 0, 1);

            VideoPositionLabel.Text = ClipTimeFormatter.Describe(positionMilliseconds);
            VideoDurationLabel.Text = ClipTimeFormatter.Describe(durationMilliseconds);
        }


        private void OnRepeatClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoRepeat = !FrameSettings.VideoRepeat;
            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            UpdateTapRevealedOverlays();
        }

        private void OnMuteClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoMuted = !FrameSettings.VideoMuted;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;
            UpdateTapRevealedOverlays();
        }

        private void OnVolumeUpClicked(object? sender, EventArgs e) => ChangeVolume(louder: true);

        private void OnVolumeDownClicked(object? sender, EventArgs e) => ChangeVolume(louder: false);

        /// <summary>
        /// Двигает громкость устройства на один шаг.
        /// </summary>
        /// <remarks>
        /// Заодно снимает беззвучный режим: иначе кнопки громкости молча ничего не меняли
        /// бы, и это выглядело бы поломкой.
        /// </remarks>
        private void ChangeVolume(bool louder)
        {
            if (louder)
            {
                SystemVolume.Raise();
            }
            else
            {
                SystemVolume.Lower();
            }

            if (FrameSettings.VideoMuted)
            {
                FrameSettings.VideoMuted = false;
                VideoPlayer.IsMuted = false;
            }

            UpdateTapRevealedOverlays();
        }

        /// <summary>
        /// Видео доиграло. При включённом повторе сюда не приходим — проигрыватель
        /// начинает заново сам.
        /// </summary>
        private void OnVideoPlaybackFinished(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Живое фото отыграло свою секунду: гасим проигрыватель и оставляем
                // кадр на экране — время слайда не кончилось.
                if (_isMotionPlayback)
                {
                    StopVideoPlayback();
                    return;
                }

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
            if (!_isVideoPlaying && VideoPlayer.SourcePath is null && !_isMotionPlayback)
            {
                return;
            }

            // Stop сам гасит видео перетеканием, открывая снимок под ним: раньше кадр
            // исчезал разом и на переходе к снимку била чёрная вспышка.
            VideoPlayer.Stop();
            VideoPlayer.SourcePath = null;
            _isVideoPlaying = false;

            _isMotionPlayback = false;

            _videoProgressTimer.Stop();
            VideoProgressBar.Progress = 0;
            VideoPositionLabel.Text = ClipTimeFormatter.Describe(0);
            VideoDurationLabel.Text = ClipTimeFormatter.Describe(0);
        }
    }
}
