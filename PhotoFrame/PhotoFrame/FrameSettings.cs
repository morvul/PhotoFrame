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
        private const string ShowSensorsKey = "show_sensors";
        private const string SensorEntityIdsKey = "sensor_entity_ids";
        private const string SensorIconsKey = "sensor_icons";
        private const string VideoRepeatKey = "video_repeat";
        private const string VideoMutedKey = "video_muted";
        private const string ShowCaptureInfoKey = "show_capture_info";
        private const string PanelRevealKey = "panel_reveal_seconds";
        private const string AlbumPhotoLimitKey = "album_photo_limit";
        private const string ShowClockKey = "show_clock";
        private const string ShowDateKey = "show_date";
        private const string LastSyncKey = "last_sync_utc";
        private const string TrashedAlbumFilesKey = "trashed_album_files";

        /// <summary>Варианты длительности показа одного кадра, секунды.</summary>
        public static readonly int[] SlideshowIntervalChoices = { 5, 10, 15, 30, 60, 300 };

        /// <summary>Варианты периода проверки альбома, часы.</summary>
        public static readonly int[] PollIntervalChoices = { 1, 3, 6, 12, 24 };

        /// <summary>Варианты времени, через которое панель управления сама скрывается.</summary>
        public static readonly int[] PanelRevealChoices = { 3, 5, 10, 20, 30, 60 };

        /// <summary>
        /// Варианты предела на число скачиваемых из альбома кадров. 0 — без предела.
        /// </summary>
        public static readonly int[] AlbumPhotoLimitChoices = { 100, 250, 500, 1000, 2000, 5000, 0 };

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
            return NightSchedule.IsNightHour(hour, NightStartHour, NightEndHour);
        }

        /// <summary>Показывать значения датчиков Home Assistant поверх снимка.</summary>
        public static bool ShowSensors
        {
            get => Preferences.Default.Get(ShowSensorsKey, false);
            set => Preferences.Default.Set(ShowSensorsKey, value);
        }

        /// <summary>
        /// Выбранные датчики в порядке показа. Количество не ограничено: строка с
        /// показаниями переносится по словам.
        /// </summary>
        public static string[] SensorEntityIds
        {
            get
            {
                string storedIds = Preferences.Default.Get(SensorEntityIdsKey, string.Empty);
                return storedIds.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            set => Preferences.Default.Set(
                SensorEntityIdsKey, value is null ? string.Empty : string.Join('\n', value));
        }

        /// <summary>
        /// Значок, выбранный для датчика. Пустая строка — без значка.
        /// </summary>
        /// <remarks>
        /// Хранится как строки "entity_id\tзначок": выбранные датчики могут все оказаться
        /// термометрами, и без значка непонятно, где какая температура.
        /// </remarks>
        public static string GetSensorIcon(string entityId)
        {
            foreach (string line in ReadSensorIconLines())
            {
                int separatorIndex = line.IndexOf('\t');
                if (separatorIndex > 0
                    && line.AsSpan(0, separatorIndex).SequenceEqual(entityId))
                {
                    return line[(separatorIndex + 1)..];
                }
            }

            return string.Empty;
        }

        public static void SetSensorIcon(string entityId, string icon)
        {
            var kept = new List<string>();
            foreach (string line in ReadSensorIconLines())
            {
                int separatorIndex = line.IndexOf('\t');
                if (separatorIndex > 0
                    && !line.AsSpan(0, separatorIndex).SequenceEqual(entityId))
                {
                    kept.Add(line);
                }
            }

            if (!string.IsNullOrEmpty(icon))
            {
                kept.Add(entityId + '\t' + icon);
            }

            Preferences.Default.Set(SensorIconsKey, string.Join('\n', kept));
        }

        private static string[] ReadSensorIconLines() =>
            Preferences.Default.Get(SensorIconsKey, string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Повторять видео по кругу вместо перехода к следующему кадру.</summary>
        public static bool VideoRepeat
        {
            get => Preferences.Default.Get(VideoRepeatKey, false);
            set => Preferences.Default.Set(VideoRepeatKey, value);
        }

        /// <summary>
        /// Воспроизводить видео без звука.
        /// </summary>
        /// <remarks>
        /// По умолчанию включено: рамка стоит в комнате, и внезапный звук из фотографии
        /// пугает сильнее, чем радует.
        /// </remarks>
        public static bool VideoMuted
        {
            get => Preferences.Default.Get(VideoMutedKey, true);
            set => Preferences.Default.Set(VideoMutedKey, value);
        }

        /// <summary>
        /// Показывать, чем и когда снят кадр.
        /// </summary>
        /// <remarks>
        /// Строка появляется только если данные есть: Google при пересжатии вырезает EXIF,
        /// и у снимков из общего альбома подписи обычно не будет.
        /// </remarks>
        public static bool ShowCaptureInfo
        {
            get => Preferences.Default.Get(ShowCaptureInfoKey, true);
            set => Preferences.Default.Set(ShowCaptureInfoKey, value);
        }

        /// <summary>
        /// Сколько секунд панель управления остаётся на экране после касания.
        /// </summary>
        public static int PanelRevealSeconds
        {
            get => Preferences.Default.Get(PanelRevealKey, 10);
            set => Preferences.Default.Set(PanelRevealKey, value);
        }

        /// <summary>
        /// Сколько кадров максимум скачивать из общего альбома. 0 — без предела.
        /// </summary>
        /// <remarks>
        /// Ограничение касается только альбома: каждый его кадр — это HTTP-запрос и место
        /// на диске. Локальные папки не ограничены, там файлы не копируются.
        /// </remarks>
        public static int AlbumPhotoLimit
        {
            get => Preferences.Default.Get(AlbumPhotoLimitKey, AppSettings.DefaultAlbumPhotoLimit);
            set => Preferences.Default.Set(AlbumPhotoLimitKey, value);
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

        /// <summary>
        /// Имена файлов кадров альбома, убранных в корзину.
        /// </summary>
        /// <remarks>
        /// Без этого списка кнопка «Убрать» для альбома выглядела бы сломанной: файла в
        /// кэше нет, ссылка в альбоме осталась, и очередная синхронизация скачивала бы
        /// снимок заново. Имя файла — хэш ссылки, поэтому список остаётся верным и после
        /// пересоздания кэша.
        /// </remarks>
        public static string[] TrashedAlbumFileNames =>
            Preferences.Default.Get(TrashedAlbumFilesKey, string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Запоминает, что кадр альбома убран, и качать его больше не нужно.</summary>
        public static void AddTrashedAlbumFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var storedNames = new List<string>(TrashedAlbumFileNames);
            foreach (string storedName in storedNames)
            {
                if (storedName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            storedNames.Add(fileName);
            Preferences.Default.Set(TrashedAlbumFilesKey, string.Join('\n', storedNames));
        }

        /// <summary>True, если ссылка на альбом так и не задана.</summary>
        public static bool IsAlbumConfigured => !string.IsNullOrWhiteSpace(SharedAlbumUrl);
    }
}
