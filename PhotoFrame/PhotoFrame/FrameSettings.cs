using System;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Настройки, которые пользователь меняет прямо на рамке.
    /// </summary>
    /// <remarks>
    /// Хранятся в <see cref="Preferences"/>, то есть переживают перезапуск и обновление
    /// приложения. Значения из secrets.props используются только как начальные: рамка
    /// стоит на полке без клавиатуры и монитора, и пересобирать APK ради смены интервала
    /// показа — плохая идея.
    /// </remarks>
    public static class FrameSettings
    {
        private const string UseSharedAlbumKey = "use_shared_album";
        private const string UseLocalFoldersKey = "use_local_folders";
        private const string LocalFolderPathsKey = "local_folder_paths";
        private const string LocalFolderRecursiveKey = "local_folder_recursive";
        private const string ShareUrlKey = "share_url";
        private const string SlideshowIntervalKey = "slideshow_interval_seconds";
        private const string PollIntervalKey = "poll_interval_hours";
        private const string ShuffleKey = "shuffle_photos";
        private const string FillScreenKey = "fill_screen";
        private const string NightModeKey = "night_mode_enabled";
        private const string NightStartHourKey = "night_start_hour";
        private const string NightEndHourKey = "night_end_hour";
        private const string ShowClockKey = "show_clock";
        private const string ShowDateKey = "show_date";
        private const string LastSyncKey = "last_sync_utc";

        /// <summary>Варианты длительности показа одного кадра, секунды.</summary>
        public static readonly int[] SlideshowIntervalChoices = { 5, 10, 15, 30, 60, 300 };

        /// <summary>Варианты периода проверки альбома, часы.</summary>
        public static readonly int[] PollIntervalChoices = { 1, 3, 6, 12, 24 };

        /// <summary>
        /// Брать снимки из общего альбома Google Photos. Может быть включено
        /// одновременно с <see cref="UseLocalFolders"/> — тогда наборы объединяются.
        /// </summary>
        public static bool UseSharedAlbum
        {
            get => Preferences.Default.Get(UseSharedAlbumKey, true);
            set => Preferences.Default.Set(UseSharedAlbumKey, value);
        }

        /// <summary>Брать снимки из папок на устройстве.</summary>
        public static bool UseLocalFolders
        {
            get => Preferences.Default.Get(UseLocalFoldersKey, false);
            set => Preferences.Default.Set(UseLocalFoldersKey, value);
        }

        /// <summary>
        /// Папки со снимками, по одной на строку. Порядок задаёт порядок показа.
        /// </summary>
        public static string[] LocalFolderPaths
        {
            get
            {
                string storedPaths = Preferences.Default.Get(LocalFolderPathsKey, string.Empty);
                return storedPaths.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            set => Preferences.Default.Set(
                LocalFolderPathsKey, value is null ? string.Empty : string.Join('\n', value));
        }

        /// <summary>Заходить ли во вложенные папки.</summary>
        public static bool LocalFolderRecursive
        {
            get => Preferences.Default.Get(LocalFolderRecursiveKey, true);
            set => Preferences.Default.Set(LocalFolderRecursiveKey, value);
        }

        /// <summary>
        /// Ссылка на публично расшаренный альбом Google Photos.
        /// По умолчанию берётся из secrets.props, дальше живёт в настройках устройства.
        /// </summary>
        public static string SharedAlbumUrl
        {
            get => Preferences.Default.Get(ShareUrlKey, LocalConfig.SharedAlbumUrl);
            set => Preferences.Default.Set(ShareUrlKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>Сколько секунд показывается один снимок.</summary>
        public static int SlideshowIntervalSeconds
        {
            get => Preferences.Default.Get(SlideshowIntervalKey, 10);
            set => Preferences.Default.Set(SlideshowIntervalKey, value);
        }

        /// <summary>Как часто рамка сама проверяет альбом на изменения.</summary>
        public static int AlbumPollIntervalHours
        {
            get => Preferences.Default.Get(PollIntervalKey, 6);
            set => Preferences.Default.Set(PollIntervalKey, value);
        }

        /// <summary>Показывать снимки в случайном порядке.</summary>
        public static bool ShufflePhotos
        {
            get => Preferences.Default.Get(ShuffleKey, false);
            set => Preferences.Default.Set(ShuffleKey, value);
        }

        /// <summary>
        /// True — кадр растягивается на весь экран с обрезкой (AspectFill),
        /// False — вписывается целиком (AspectFit). По умолчанию не обрезаем:
        /// в альбоме встречаются вертикальные снимки.
        /// </summary>
        public static bool FillScreen
        {
            get => Preferences.Default.Get(FillScreenKey, false);
            set => Preferences.Default.Set(FillScreenKey, value);
        }

        /// <summary>
        /// Ночью показывать вместо слайд-шоу крупные приглушённые часы.
        /// </summary>
        /// <remarks>
        /// Рамка обычно стоит в комнате, где спят: яркие сменяющиеся снимки ночью мешают,
        /// а часы на чёрном фоне остаются полезными.
        /// </remarks>
        public static bool NightModeEnabled
        {
            get => Preferences.Default.Get(NightModeKey, true);
            set => Preferences.Default.Set(NightModeKey, value);
        }

        /// <summary>Час начала ночного режима.</summary>
        public static int NightStartHour
        {
            get => Preferences.Default.Get(NightStartHourKey, 22);
            set => Preferences.Default.Set(NightStartHourKey, value);
        }

        /// <summary>Час окончания ночного режима.</summary>
        public static int NightEndHour
        {
            get => Preferences.Default.Get(NightEndHourKey, 7);
            set => Preferences.Default.Set(NightEndHourKey, value);
        }

        /// <summary>
        /// True, если указанное время попадает в ночной интервал.
        /// </summary>
        /// <remarks>
        /// Интервал обычно переходит через полночь (22:00–07:00), поэтому сравнение
        /// «начало &lt;= час &lt; конец» здесь не работает.
        /// </remarks>
        public static bool IsNightHour(int hour)
        {
            int startHour = NightStartHour;
            int endHour = NightEndHour;

            if (startHour == endHour)
            {
                return false;
            }

            return startHour < endHour
                ? hour >= startHour && hour < endHour
                : hour >= startHour || hour < endHour;
        }

        /// <summary>Показывать часы поверх снимка.</summary>
        public static bool ShowClock
        {
            get => Preferences.Default.Get(ShowClockKey, true);
            set => Preferences.Default.Set(ShowClockKey, value);
        }

        /// <summary>Показывать дату под часами.</summary>
        public static bool ShowDate
        {
            get => Preferences.Default.Get(ShowDateKey, true);
            set => Preferences.Default.Set(ShowDateKey, value);
        }

        /// <summary>Когда последний раз успешно синхронизировались. UTC.</summary>
        public static DateTime? LastSyncUtc
        {
            get
            {
                long storedTicks = Preferences.Default.Get(LastSyncKey, 0L);
                return storedTicks == 0 ? null : new DateTime(storedTicks, DateTimeKind.Utc);
            }
            set => Preferences.Default.Set(LastSyncKey, value?.Ticks ?? 0L);
        }

        /// <summary>True, если ссылка на альбом так и не задана.</summary>
        public static bool IsAlbumConfigured => !string.IsNullOrWhiteSpace(SharedAlbumUrl);
    }
}
