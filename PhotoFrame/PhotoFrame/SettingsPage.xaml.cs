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

            string[] hourChoices = BuildHourOfDayChoices();
            NightStartPicker.ItemsSource = hourChoices;
            NightEndPicker.ItemsSource = hourChoices;
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
            RecursiveSwitch.IsToggled = FrameSettings.LocalFolderRecursive;
            ShowSelectedFolders();
            ShuffleSwitch.IsToggled = FrameSettings.ShufflePhotos;
            FillScreenSwitch.IsToggled = FrameSettings.FillScreen;
            ShowClockSwitch.IsToggled = FrameSettings.ShowClock;
            ShowDateSwitch.IsToggled = FrameSettings.ShowDate;

            NightModeSwitch.IsToggled = FrameSettings.NightModeEnabled;
            NightStartPicker.SelectedIndex = FrameSettings.NightStartHour;
            NightEndPicker.SelectedIndex = FrameSettings.NightEndHour;

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

            ShowDiagnostics();
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
            VersionLabel.Text = $"Версия {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        }

        private static string DescribeFreeSpace()
        {
            try
            {
                var appDataDrive = new DriveInfo(
                    Path.GetPathRoot(FileSystem.AppDataDirectory) ?? "/");

                double freeGigabytes = appDataDrive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                return string.Format(
                    CultureInfo.CurrentCulture, "Свободно на устройстве: {0:F1} ГБ", freeGigabytes);
            }
            catch (Exception driveQueryFailure) when (
                driveQueryFailure is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Размер диска — справочная информация, из-за неё экран настроек падать не должен.
                return "Свободное место: неизвестно";
            }
        }

        private void ApplySettings()
        {
            FrameSettings.UseSharedAlbum = UseSharedAlbumSwitch.IsToggled;
            FrameSettings.UseLocalFolders = UseLocalFoldersSwitch.IsToggled;

            // Список папок редактируется только на FolderPickerPage, здесь он не трогается.
            FrameSettings.LocalFolderRecursive = RecursiveSwitch.IsToggled;
            FrameSettings.SharedAlbumUrl = ShareUrlEntry.Text ?? string.Empty;
            FrameSettings.ShufflePhotos = ShuffleSwitch.IsToggled;
            FrameSettings.FillScreen = FillScreenSwitch.IsToggled;
            FrameSettings.ShowClock = ShowClockSwitch.IsToggled;
            FrameSettings.ShowDate = ShowDateSwitch.IsToggled;
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
