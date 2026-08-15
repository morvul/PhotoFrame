using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Экран настроек рамки: ссылка на альбом, темп слайд-шоу, периодичность проверки.
    /// </summary>
    public partial class SettingsPage : ContentPage
    {
        /// <summary>
        /// Ставится в true, когда пользователь попросил обновиться сразу после сохранения.
        /// MainPage читает флаг в OnAppearing — так экраны не держат ссылок друг на друга.
        /// </summary>
        public static bool SyncRequestedOnReturn { get; set; }

        public SettingsPage()
        {
            InitializeComponent();

            SlideshowIntervalPicker.ItemsSource = BuildSecondsChoices();
            PollIntervalPicker.ItemsSource = BuildHoursChoices();

            PanelRevealPicker.ItemsSource = BuildPanelRevealChoices();
            AlbumLimitPicker.ItemsSource = BuildAlbumLimitChoices();

            string[] hourChoices = BuildHourOfDayChoices();
            NightStartPicker.ItemsSource = hourChoices;
            NightEndPicker.ItemsSource = hourChoices;

            NightColorPicker.ItemsSource = NightClockPalette.BuildChoiceLabels();
            LaunchDelayPicker.ItemsSource = BuildLaunchDelayChoices();
        }

        /// <summary>0 в списке означает «без предела».</summary>
        private static string[] BuildAlbumLimitChoices()
        {
            var labels = new string[FrameSettings.AlbumPhotoLimitChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                int limit = FrameSettings.AlbumPhotoLimitChoices[index];
                labels[index] = limit == 0 ? "без предела" : limit + " фото";
            }

            return labels;
        }

        private static string[] BuildPanelRevealChoices()
        {
            var labels = new string[FrameSettings.PanelRevealChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                labels[index] = FrameSettings.PanelRevealChoices[index] + " сек";
            }

            return labels;
        }

        /// <summary>0 в списке означает «сразу».</summary>
        private static string[] BuildLaunchDelayChoices()
        {
            var labels = new string[FrameSettings.LaunchOnBootDelayChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                int delaySeconds = FrameSettings.LaunchOnBootDelayChoices[index];
                labels[index] = delaySeconds == 0 ? "сразу" : delaySeconds + " сек";
            }

            return labels;
        }

        /// <summary>Часы суток: индекс в списке равен самому часу.</summary>
        private static string[] BuildHourOfDayChoices()
        {
            var hourLabels = new string[24];
            for (int hour = 0; hour < hourLabels.Length; hour++)
            {
                hourLabels[hour] = $"{hour:D2}:00";
            }

            return hourLabels;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            LoadCurrentSettings();
        }

        private static string[] BuildSecondsChoices()
        {
            var choiceLabels = new string[FrameSettings.SlideshowIntervalChoices.Length];
            for (int choiceIndex = 0; choiceIndex < choiceLabels.Length; choiceIndex++)
            {
                int seconds = FrameSettings.SlideshowIntervalChoices[choiceIndex];
                choiceLabels[choiceIndex] = seconds < 60
                    ? $"{seconds} сек"
                    : $"{seconds / 60} мин";
            }

            return choiceLabels;
        }

        private static string[] BuildHoursChoices()
        {
            var choiceLabels = new string[FrameSettings.PollIntervalChoices.Length];
            for (int choiceIndex = 0; choiceIndex < choiceLabels.Length; choiceIndex++)
            {
                choiceLabels[choiceIndex] = $"каждые {FrameSettings.PollIntervalChoices[choiceIndex]} ч";
            }

            return choiceLabels;
        }

        private void LoadCurrentSettings()
        {
            UseSharedAlbumSwitch.IsToggled = FrameSettings.UseSharedAlbum;
            UseLocalFoldersSwitch.IsToggled = FrameSettings.UseLocalFolders;
            UpdateSourcePanels();

            ShareUrlEntry.Text = FrameSettings.SharedAlbumUrl;

            AlbumLimitPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.AlbumPhotoLimitChoices, FrameSettings.AlbumPhotoLimit);
            if (AlbumLimitPicker.SelectedIndex < 0)
            {
                AlbumLimitPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.AlbumPhotoLimitChoices, AppSettings.DefaultAlbumPhotoLimit);
            }
            AlbumVideosSwitch.IsToggled = FrameSettings.DownloadAlbumVideos;
            MotionPhotosSwitch.IsToggled = FrameSettings.AnimateMotionPhotos;
            RecursiveSwitch.IsToggled = FrameSettings.LocalFolderRecursive;
            ShowSelectedFolders();
            ShuffleSwitch.IsToggled = FrameSettings.ShufflePhotos;
            FillScreenSwitch.IsToggled = FrameSettings.FillScreen;
            ShowClockSwitch.IsToggled = FrameSettings.ShowClock;
            ShowDateSwitch.IsToggled = FrameSettings.ShowDate;
            ShowCaptureInfoSwitch.IsToggled = FrameSettings.ShowCaptureInfo;

            PanelRevealPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.PanelRevealChoices, FrameSettings.PanelRevealSeconds);
            if (PanelRevealPicker.SelectedIndex < 0)
            {
                PanelRevealPicker.SelectedIndex = 2;
            }

            ShowSensorsSwitch.IsToggled = FrameSettings.ShowSensors;
            SensorPanel.IsVisible = FrameSettings.ShowSensors;
            ShowSelectedSensors();

            NightModeSwitch.IsToggled = FrameSettings.NightModeEnabled;
            NightStartPicker.SelectedIndex = FrameSettings.NightStartHour;
            NightEndPicker.SelectedIndex = FrameSettings.NightEndHour;

            NightColorPicker.SelectedIndex =
                NightClockPalette.IndexOfHex(FrameSettings.NightClockColorHex);

            NightBrightnessSlider.Value = FrameSettings.NightScreenBrightnessPercent;
            NightIntensitySlider.Value = FrameSettings.NightClockIntensityPercent;
            ShowNightBrightness();
            ShowNightIntensity();

            SlideshowIntervalPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.SlideshowIntervalChoices, FrameSettings.SlideshowIntervalSeconds);
            PollIntervalPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.PollIntervalChoices, FrameSettings.AlbumPollIntervalHours);

            // Если сохранённое значение не из списка (например, после смены набора вариантов),
            // не оставляем Picker пустым.
            if (SlideshowIntervalPicker.SelectedIndex < 0)
            {
                SlideshowIntervalPicker.SelectedIndex = 1;
            }

            if (PollIntervalPicker.SelectedIndex < 0)
            {
                PollIntervalPicker.SelectedIndex = 2;
            }

            LaunchOnBootSwitch.IsToggled = FrameSettings.LaunchOnBoot;
            LaunchDelayPanel.IsVisible = FrameSettings.LaunchOnBoot;

            LaunchDelayPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.LaunchOnBootDelayChoices, FrameSettings.LaunchOnBootDelaySeconds);

            if (LaunchDelayPicker.SelectedIndex < 0)
            {
                LaunchDelayPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.LaunchOnBootDelayChoices, 10);
            }

            ShowDiagnostics();
        }

        private void OnNightBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
            ShowNightBrightness();

        private void OnNightIntensityChanged(object? sender, ValueChangedEventArgs e) =>
            ShowNightIntensity();

        /// <summary>Ноль на шкале яркости означает «не трогать подсветку».</summary>
        private void ShowNightBrightness()
        {
            int percent = (int)Math.Round(NightBrightnessSlider.Value);
            NightBrightnessValueLabel.Text = percent == 0 ? "как в системе" : $"{percent} %";
        }

        private void ShowNightIntensity() =>
            NightIntensityValueLabel.Text = $"{(int)Math.Round(NightIntensitySlider.Value)} %";

        private void OnLaunchOnBootToggled(object? sender, ToggledEventArgs e) =>
            LaunchDelayPanel.IsVisible = LaunchOnBootSwitch.IsToggled;

        /// <summary>
        /// Раскрывает и сворачивает раздел настроек.
        /// </summary>
        /// <remarks>
        /// Раздел передаётся параметром жеста, поэтому обработчик один на все заголовки
        /// и знать о них ничего не должен. Значок раскрытия — первый символ подписи,
        /// так что менять его можно, не трогая остальной текст.
        ///
        /// Заголовок собран из Border с Label, а не из Button: у кнопки MAUI не
        /// настраивается выравнивание текста, и подпись раздела оставалась по центру.
        /// </remarks>
        private static void OnSectionHeaderTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Border header
                || header.Content is not Label title
                || e.Parameter is not VisualElement section)
            {
                return;
            }

            bool expanding = !section.IsVisible;
            section.IsVisible = expanding;
            title.Text = (expanding ? '▾' : '▸') + title.Text[1..];
        }

        /// <summary>Настройки источника показываются только когда сам источник включён.</summary>
        private void UpdateSourcePanels()
        {
            SharedAlbumPanel.IsVisible = UseSharedAlbumSwitch.IsToggled;
            LocalFolderPanel.IsVisible = UseLocalFoldersSwitch.IsToggled;
        }

        private void OnSourceToggled(object? sender, ToggledEventArgs e) => UpdateSourcePanels();

        private void ShowSelectedFolders()
        {
            string[] folderPaths = FrameSettings.LocalFolderPaths;

            SelectedFoldersLabel.Text = folderPaths.Length == 0
                ? "Папки не выбраны"
                : $"Выбрано папок: {folderPaths.Length}";

            SelectedFoldersDetailLabel.Text = folderPaths.Length == 0
                ? "Нажмите «Выбрать папки…»"
                : string.Join("\n", folderPaths);
        }

        /// <summary>
        /// Сохраняем перед уходом на выбор папок: иначе введённая ссылка на альбом
        /// и переключатели потерялись бы при возврате.
        /// </summary>
        private async void OnPickFoldersClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            await Shell.Current.GoToAsync(nameof(FolderPickerPage));
        }

        private void OnShowSensorsToggled(object? sender, ToggledEventArgs e) =>
            SensorPanel.IsVisible = ShowSensorsSwitch.IsToggled;

        private void ShowSelectedSensors()
        {
            string[] entityIds = FrameSettings.SensorEntityIds;

            SelectedSensorsLabel.Text = entityIds.Length == 0
                ? "Датчики не выбраны"
                : $"Выбрано датчиков: {entityIds.Length}";

            SelectedSensorsDetailLabel.Text = entityIds.Length == 0
                ? "Нажмите «Выбрать датчики…»"
                : string.Join(", ", entityIds);
        }

        private async void OnPickSensorsClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            await Shell.Current.GoToAsync(nameof(SensorPickerPage));
        }

        private void ShowDiagnostics()
        {
            int cachedPhotoCount = SharedAlbumPhotoSource.GetCachedPhotoPaths().Count
                                   + new LocalFolderPhotoSource().GetPhotoPaths().Count;
            PhotoCountLabel.Text = $"Снимков готово к показу: {cachedPhotoCount}";

            DateTime? lastSyncUtc = FrameSettings.LastSyncUtc;
            LastSyncLabel.Text = lastSyncUtc is null
                ? "Последнее обновление: ещё не было"
                : $"Последнее обновление: {lastSyncUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm}";

            StorageLabel.Text = DescribeFreeSpace();
            CacheSizeLabel.Text = DescribePhotoCacheSize();
            VersionLabel.Text = $"Версия {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        }

        /// <summary>
        /// Свободное место в разделе, где приложение хранит скачанные снимки.
        /// </summary>
        /// <remarks>
        /// Через DriveInfo это посчитать нельзя: Path.GetPathRoot на Unix возвращает "/",
        /// а корень на Android — крошечный rootfs (здесь 0,45 ГБ) и к /data отношения не
        /// имеет. Получалось, что рамка с 14 ГБ свободного места сообщала о почти полном
        /// диске. StatFs считает именно тот раздел, в котором лежит переданный путь.
        /// </remarks>
        private static string DescribeFreeSpace()
        {
            try
            {
                var storageStats = new Android.OS.StatFs(FileSystem.AppDataDirectory);

                double freeGigabytes = storageStats.AvailableBytes / 1024d / 1024d / 1024d;
                double totalGigabytes = storageStats.TotalBytes / 1024d / 1024d / 1024d;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "Свободно в памяти рамки: {0:F1} из {1:F1} ГБ",
                    freeGigabytes,
                    totalGigabytes);
            }
            catch (Exception storageQueryFailure) when (
                storageQueryFailure is Java.Lang.Throwable or IOException
                    or UnauthorizedAccessException)
            {
                // Справочная строка не должна ронять экран настроек.
                return "Свободное место: неизвестно";
            }
        }

        /// <summary>
        /// Сколько занимает кэш скачанных снимков. Снимки из локальных папок не копируются
        /// и в этот размер не входят.
        /// </summary>
        private static string DescribePhotoCacheSize()
        {
            string cacheDirectory = SharedAlbumPhotoSource.PhotoLibraryDirectory;
            if (!Directory.Exists(cacheDirectory))
            {
                return "Кэш скачанных снимков: пуст";
            }

            long totalBytes = 0;
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(cacheDirectory))
                {
                    totalBytes += new FileInfo(filePath).Length;
                }
            }
            catch (Exception sizeQueryFailure) when (
                sizeQueryFailure is IOException or UnauthorizedAccessException)
            {
                return "Кэш скачанных снимков: размер неизвестен";
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                "Кэш скачанных снимков: {0:F0} МБ",
                totalBytes / 1024d / 1024d);
        }

        private void ApplySettings()
        {
            FrameSettings.UseSharedAlbum = UseSharedAlbumSwitch.IsToggled;
            FrameSettings.UseLocalFolders = UseLocalFoldersSwitch.IsToggled;

            FrameSettings.DownloadAlbumVideos = AlbumVideosSwitch.IsToggled;
            FrameSettings.AnimateMotionPhotos = MotionPhotosSwitch.IsToggled;

            // Список папок редактируется только на FolderPickerPage, здесь он не трогается.
            FrameSettings.LocalFolderRecursive = RecursiveSwitch.IsToggled;
            FrameSettings.SharedAlbumUrl = ShareUrlEntry.Text ?? string.Empty;

            if (AlbumLimitPicker.SelectedIndex >= 0)
            {
                FrameSettings.AlbumPhotoLimit =
                    FrameSettings.AlbumPhotoLimitChoices[AlbumLimitPicker.SelectedIndex];
            }
            FrameSettings.ShufflePhotos = ShuffleSwitch.IsToggled;
            FrameSettings.FillScreen = FillScreenSwitch.IsToggled;
            FrameSettings.ShowSensors = ShowSensorsSwitch.IsToggled;

            // Список датчиков правится только на SensorPickerPage.
            FrameSettings.ShowClock = ShowClockSwitch.IsToggled;
            FrameSettings.ShowDate = ShowDateSwitch.IsToggled;
            FrameSettings.ShowCaptureInfo = ShowCaptureInfoSwitch.IsToggled;

            if (PanelRevealPicker.SelectedIndex >= 0)
            {
                FrameSettings.PanelRevealSeconds =
                    FrameSettings.PanelRevealChoices[PanelRevealPicker.SelectedIndex];
            }
            FrameSettings.NightModeEnabled = NightModeSwitch.IsToggled;

            // Индекс в списке часов совпадает с самим часом.
            if (NightStartPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightStartHour = NightStartPicker.SelectedIndex;
            }

            if (NightEndPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightEndHour = NightEndPicker.SelectedIndex;
            }

            if (NightColorPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightClockColorHex =
                    NightClockPalette.Choices[NightColorPicker.SelectedIndex].Hex;
            }

            FrameSettings.NightScreenBrightnessPercent = (int)Math.Round(NightBrightnessSlider.Value);
            FrameSettings.NightClockIntensityPercent = (int)Math.Round(NightIntensitySlider.Value);

            FrameSettings.LaunchOnBoot = LaunchOnBootSwitch.IsToggled;

            if (LaunchDelayPicker.SelectedIndex >= 0)
            {
                FrameSettings.LaunchOnBootDelaySeconds =
                    FrameSettings.LaunchOnBootDelayChoices[LaunchDelayPicker.SelectedIndex];
            }

            if (SlideshowIntervalPicker.SelectedIndex >= 0)
            {
                FrameSettings.SlideshowIntervalSeconds =
                    FrameSettings.SlideshowIntervalChoices[SlideshowIntervalPicker.SelectedIndex];
            }

            if (PollIntervalPicker.SelectedIndex >= 0)
            {
                FrameSettings.AlbumPollIntervalHours =
                    FrameSettings.PollIntervalChoices[PollIntervalPicker.SelectedIndex];
            }
        }

        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            SyncRequestedOnReturn = true;
            await Shell.Current.GoToAsync("..");
        }

        private async void OnSaveOnlyClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            await Shell.Current.GoToAsync("..");
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            // Уходим без сохранения: настройки применяются только кнопками сохранения.
            await Shell.Current.GoToAsync("..");
        }
    }
}
