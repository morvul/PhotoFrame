using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Показывает снимки из одной или нескольких папок на устройстве.
    /// </summary>
    /// <remarks>
    /// Файлы намеренно НЕ копируются в кэш приложения: на рамке всего несколько гигабайт,
    /// и дублировать содержимое папки незачем. Вместо этого ведётся манифест с путями
    /// в порядке показа — тот же приём, что и для альбома Google, поэтому страница
    /// слайд-шоу об источнике ничего не знает.
    /// </remarks>
    public class LocalFolderPhotoSource : IPhotoSource
    {
        private const string ManifestFileName = "local.manifest";

        /// <summary>Расширения, которые Android 8.1 умеет показывать без дополнительных кодеков.</summary>
        private static readonly string[] SupportedExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        /// <summary>
        /// Каталоги, которые пропускаются при обходе.
        /// </summary>
        /// <remarks>
        /// Приложения хранят рядом с фотографиями миниатюры: у Frameo, например, в
        /// frameo_files/cache/galleryThumbnails лежит 151 превью по ~19 КБ против 66
        /// полноразмерных снимков в frameo_files/media. Без этого фильтра слайд-шоу
        /// на две трети состояло из размытых миниатюр.
        /// </remarks>
        private static readonly string[] SkippedDirectoryNames =
            { "cache", "caches", "thumbnails", "temp", "tmp" };

        /// <inheritdoc />
        public bool IsConfigured => FrameSettings.LocalFolderPaths.Length > 0;

        private static string ManifestPath =>
            Path.Combine(FileSystem.AppDataDirectory, ManifestFileName);

        /// <inheritdoc />
        public string DescribeConfiguration()
        {
            string[] folderPaths = FrameSettings.LocalFolderPaths;
            if (folderPaths.Length == 0)
            {
                return "Папки не выбраны";
            }

            int existingFolderCount = 0;
            foreach (string folderPath in folderPaths)
            {
                if (Directory.Exists(folderPath))
                {
                    existingFolderCount++;
                }
            }

            return existingFolderCount == folderPaths.Length
                ? $"Папок: {folderPaths.Length}"
                : $"Папок: {folderPaths.Length}, доступно {existingFolderCount}";
        }

        /// <inheritdoc />
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                throw new PhotoSourceException(
                    "Не выбрана ни одна папка. Откройте настройки (⚙) и укажите папки со снимками.");
            }

            await EnsureStoragePermissionAsync().ConfigureAwait(false);

            List<string> foundPhotoPaths = ScanConfiguredFolders(cancellationToken);

            if (foundPhotoPaths.Count == 0)
            {
                throw new PhotoSourceException(
                    "В указанных папках нет изображений. Проверьте пути и права доступа " +
                    $"(поддерживаются {string.Join(", ", SupportedExtensions)}).");
            }

            // Сравниваем с прошлым проходом, чтобы показать осмысленные «новых N / удалено M».
            var previousPaths = new HashSet<string>(ReadManifest(), StringComparer.OrdinalIgnoreCase);
            int addedCount = 0;
            foreach (string photoPath in foundPhotoPaths)
            {
                if (!previousPaths.Contains(photoPath))
                {
                    addedCount++;
                }
            }

            var currentPaths = new HashSet<string>(foundPhotoPaths, StringComparer.OrdinalIgnoreCase);
            int removedCount = 0;
            foreach (string previousPath in previousPaths)
            {
                if (!currentPaths.Contains(previousPath))
                {
                    removedCount++;
                }
            }

            WriteManifest(foundPhotoPaths);
            progress?.Report((foundPhotoPaths.Count, foundPhotoPaths.Count));

            return new AlbumSyncResult(
                TotalPhotoCount: foundPhotoPaths.Count,
                DownloadedCount: addedCount,
                ReusedCount: foundPhotoPaths.Count - addedCount,
                RemovedCount: removedCount);
        }

        /// <summary>
        /// Обходит настроенные папки. Порядок стабильный: сначала в порядке настроек,
        /// внутри папки — по имени файла.
        /// </summary>
        private static List<string> ScanConfiguredFolders(CancellationToken cancellationToken)
        {
            var foundPhotoPaths = new List<string>();
            var alreadyAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folderPath in FrameSettings.LocalFolderPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (string filePath in
                         EnumeratePhotoFiles(folderPath, FrameSettings.LocalFolderRecursive))
                {
                    if (alreadyAdded.Add(filePath))
                    {
                        foundPhotoPaths.Add(filePath);
                    }

                    if (foundPhotoPaths.Count >= AppSettings.MaxPhotosToDownload)
                    {
                        return foundPhotoPaths;
                    }
                }
            }

            return foundPhotoPaths;
        }

        /// <summary>
        /// Перечисляет изображения в папке, пропуская служебные каталоги с миниатюрами.
        /// </summary>
        /// <remarks>
        /// Обход написан вручную, а не через SearchOption.AllDirectories, именно ради
        /// возможности не заходить в отдельные подкаталоги. Этим же методом пользуется
        /// экран выбора папок, поэтому показанные там счётчики совпадают с тем,
        /// что реально попадёт в слайд-шоу.
        /// </remarks>
        public static IEnumerable<string> EnumeratePhotoFiles(string rootPath, bool recurse)
        {
            if (!Directory.Exists(rootPath))
            {
                yield break;
            }

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectory = pendingDirectories.Pop();

                string[] filePaths;
                try
                {
                    filePaths = Directory.GetFiles(currentDirectory);
                }
                catch (Exception scanFailure) when (
                    scanFailure is IOException or UnauthorizedAccessException)
                {
                    // Недоступный каталог не должен ломать обход остальных.
                    System.Diagnostics.Debug.WriteLine(
                        $"Каталог {currentDirectory} не прочитан: {scanFailure.Message}");
                    continue;
                }

                Array.Sort(filePaths, StringComparer.OrdinalIgnoreCase);

                foreach (string filePath in filePaths)
                {
                    if (IsSupportedImage(filePath))
                    {
                        yield return filePath;
                    }
                }

                if (!recurse)
                {
                    continue;
                }

                string[] subdirectories;
                try
                {
                    subdirectories = Directory.GetDirectories(currentDirectory);
                }
                catch (Exception scanFailure) when (
                    scanFailure is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

                // Кладём в стек в обратном порядке, чтобы обход шёл по алфавиту.
                for (int index = subdirectories.Length - 1; index >= 0; index--)
                {
                    if (!ShouldSkipDirectory(subdirectories[index]))
                    {
                        pendingDirectories.Push(subdirectories[index]);
                    }
                }
            }
        }

        /// <summary>Каталоги с миниатюрами и кэшем в слайд-шоу не нужны.</summary>
        private static bool ShouldSkipDirectory(string directoryPath)
        {
            string directoryName = Path.GetFileName(directoryPath);

            // Скрытые каталоги вроде .thumbnails создаёт сама система.
            if (directoryName.StartsWith('.'))
            {
                return true;
            }

            if (directoryName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string skippedName in SkippedDirectoryNames)
            {
                if (directoryName.Equals(skippedName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True, если файл — изображение поддерживаемого формата.</summary>
        public static bool IsSupportedImage(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            foreach (string supportedExtension in SupportedExtensions)
            {
                if (string.Equals(extension, supportedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// На Android 8.1 чтение чужих папок требует разрешения, выданного в рантайме,
        /// а не только объявленного в манифесте.
        /// </summary>
        private static async Task EnsureStoragePermissionAsync()
        {
            PermissionStatus permissionStatus =
                await Permissions.CheckStatusAsync<Permissions.StorageRead>().ConfigureAwait(false);

            if (permissionStatus != PermissionStatus.Granted)
            {
                permissionStatus =
                    await Permissions.RequestAsync<Permissions.StorageRead>().ConfigureAwait(false);
            }

            if (permissionStatus != PermissionStatus.Granted)
            {
                throw new PhotoSourceException(
                    "Нет разрешения на чтение файлов. Выдайте приложению доступ к памяти " +
                    "в настройках Android.");
            }
        }

        /// <inheritdoc />
        public List<string> GetPhotoPaths()
        {
            var photoPaths = new List<string>();

            foreach (string photoPath in ReadManifest())
            {
                // Файл могли удалить между обновлениями — молча пропускаем.
                if (File.Exists(photoPath))
                {
                    photoPaths.Add(photoPath);
                }
            }

            return photoPaths;
        }

        private static IEnumerable<string> ReadManifest()
        {
            if (!File.Exists(ManifestPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                return File.ReadAllLines(ManifestPath);
            }
            catch (Exception manifestReadFailure) when (
                manifestReadFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Манифест папок не прочитан: {manifestReadFailure.Message}");
                return Array.Empty<string>();
            }
        }

        private static void WriteManifest(List<string> photoPaths)
        {
            string temporaryPath = ManifestPath + ".tmp";

            try
            {
                File.WriteAllLines(temporaryPath, photoPaths);
                File.Move(temporaryPath, ManifestPath, overwrite: true);
            }
            catch (Exception manifestFailure) when (
                manifestFailure is IOException or UnauthorizedAccessException)
            {
                throw new PhotoSourceException(
                    "Не удалось сохранить список снимков на диске рамки.", manifestFailure);
            }
        }
    }
}
