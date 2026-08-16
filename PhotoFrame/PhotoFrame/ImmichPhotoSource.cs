using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Загружает снимки с домашнего сервера Immich.
    /// </summary>
    /// <remarks>
    /// Зачем ещё один сетевой источник: Immich принимает автозагрузку с телефона, в том
    /// числе с iPhone, — а именно этого не умеет ссылка на общий альбом Google, где
    /// кадры приходится добавлять руками.
    ///
    /// Здесь, в отличие от альбома Google, есть документированный API: список объектов
    /// приходит JSON-ом, ключ доступа передаётся заголовком x-api-key. Ключ создаётся
    /// в самом Immich и хранится только в настройках устройства — в сборку он не
    /// попадает и в репозитории его нет.
    ///
    /// Устройство кэша повторяет альбомное: имя файла выводится из идентификатора
    /// объекта, поэтому добавленные на сервер снимки докачиваются поодиночке, а не
    /// весь набор заново. Порядок показа фиксируется манифестом.
    ///
    /// Видео пока пропускаются: их воспроизведение завязано на кэш клипов альбома
    /// Google, и переиспользовать его как есть нельзя. Сколько видео пропущено,
    /// сообщается в итоге обновления, чтобы это не выглядело пропажей кадров.
    /// </remarks>
    public class ImmichPhotoSource : IPhotoSource
    {
        private const string PhotoDirectoryName = "immich";

        /// <summary>Список файлов в порядке показа. Пишется последним — это точка фиксации.</summary>
        private const string ManifestFileName = "immich.manifest";

        private const string SignatureKey = "immich_signature";

        /// <summary>Ключ доступа передаётся этим заголовком — так описано в API Immich.</summary>
        private const string ApiKeyHeaderName = "x-api-key";

        /// <summary>
        /// Сколько объектов запрашивается за один раз при обходе всей библиотеки.
        /// Тысяча записей — это около мегабайта JSON: и запросов немного, и разбор
        /// не съедает память рамки.
        /// </summary>
        private const int SearchPageSize = 1000;

        /// <summary>
        /// Предел на число страниц поиска. Библиотека на десятки тысяч снимков не должна
        /// заставлять рамку ходить по серверу без конца: до предела кадров, заданного
        /// в настройках, дело дойдёт куда раньше.
        /// </summary>
        private const int MaxSearchPages = 50;

        private readonly HttpClient _httpClient;

        public ImmichPhotoSource()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>Каталог со снимками и манифестом.</summary>
        public static string PhotoLibraryDirectory =>
            Path.Combine(FileSystem.AppDataDirectory, PhotoDirectoryName);

        /// <inheritdoc />
        public bool IsConfigured => FrameSettings.IsImmichConfigured;

        /// <inheritdoc />
        public string DescribeConfiguration()
        {
            if (!FrameSettings.IsImmichConfigured)
            {
                return "Сервер или ключ не заданы";
            }

            return FrameSettings.ImmichAlbumId.Length == 0
                ? "Вся библиотека"
                : "Альбом: " + FrameSettings.ImmichAlbumName;
        }

        /// <inheritdoc />
        public List<string> GetPhotoPaths() => GetCachedPhotoPaths();

        /// <summary>
        /// Проверяет связь с сервером и возвращает список альбомов.
        /// </summary>
        /// <remarks>
        /// Вызывается кнопкой на экране настроек: без неё пользователю пришлось бы
        /// набирать идентификатор альбома вручную, а это UUID.
        /// </remarks>
        public async Task<List<ImmichAlbum>> GetAlbumsAsync(
            string serverUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            string albumsJson = await GetStringAsync(
                ImmichCatalog.BuildAlbumsUrl(serverUrl), apiKey, cancellationToken)
                .ConfigureAwait(false);

            return ImmichCatalog.ParseAlbums(albumsJson);
        }

        /// <inheritdoc />
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!FrameSettings.IsImmichConfigured)
            {
                throw new PhotoSourceException(
                    "Immich не настроен: откройте настройки (⚙) и укажите адрес сервера и ключ доступа.");
            }

            string serverUrl = FrameSettings.ImmichServerUrl;
            string apiKey = FrameSettings.ImmichApiKey;

            List<ImmichAsset> assets = await GetAssetsAsync(
                serverUrl, apiKey, FrameSettings.ImmichAlbumId, cancellationToken).ConfigureAwait(false);

            // Видео остаются в наборе: качается их заставка, а сам клип забирается,
            // когда слайд-шоу до кадра дойдёт, — см. ImmichVideoCache. Выключенная
            // настройка убирает их совсем: заставка без возможности воспроизвести
            // только сбивает с толку.
            if (!FrameSettings.DownloadAlbumVideos)
            {
                assets.RemoveAll(asset => asset.IsVideo);
            }

            int trashedCount = RemoveTrashedAssets(assets);

            if (assets.Count == 0)
            {
                throw new PhotoSourceException(trashedCount > 0
                    ? "Все снимки Immich убраны в корзину."
                    : "На сервере Immich не нашлось ни одного снимка.");
            }

            // Тот же предел, что и для альбома Google, и по той же причине: каждый кадр —
            // это запрос и место на диске рамки.
            int availableCount = assets.Count;
            int photoLimit = FrameSettings.AlbumPhotoLimit;

            if (photoLimit > 0 && assets.Count > photoLimit)
            {
                assets.RemoveRange(photoLimit, assets.Count - photoLimit);
            }

            string signature = ComputeSignature(assets);

            // Самый частый случай: на сервере ничего не изменилось, диск можно не трогать.
            if (!forceRefresh && ImmichSidecar.HasIndex && IsAlreadyDownloaded(signature))
            {
                int cachedCount = GetCachedPhotoPaths().Count;
                return new AlbumSyncResult(
                    cachedCount, DownloadedCount: 0, cachedCount, RemovedCount: 0, availableCount);
            }

            AlbumSyncResult result = await SyncPhotosAsync(
                serverUrl, apiKey, assets, progress, cancellationToken).ConfigureAwait(false);

            Preferences.Default.Set(SignatureKey, signature);

            return result with { AvailableCount = availableCount };
        }

        /// <summary>
        /// Объекты библиотеки либо одного альбома, страница за страницей.
        /// </summary>
        /// <param name="albumId">Пусто — вся библиотека.</param>
        private async Task<List<ImmichAsset>> GetAssetsAsync(
            string serverUrl, string apiKey, string albumId, CancellationToken cancellationToken)
        {
            var assets = new List<ImmichAsset>();
            string searchUrl = ImmichCatalog.BuildSearchUrl(serverUrl);
            int pageNumber = 1;

            for (int pageIndex = 0; pageIndex < MaxSearchPages && pageNumber > 0; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Post, searchUrl)
                {
                    Content = new StringContent(
                        ImmichCatalog.BuildSearchRequestBody(pageNumber, SearchPageSize, albumId),
                        Encoding.UTF8,
                        "application/json"),
                };

                string pageJson = await SendAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
                assets.AddRange(ImmichCatalog.ParseSearchAssets(pageJson, out pageNumber));
            }

            if (assets.Count == 0 && albumId.Length > 0)
            {
                throw new PhotoSourceException(
                    $"Альбом «{FrameSettings.ImmichAlbumName}» пуст либо больше не существует. " +
                    "Выберите альбом заново в настройках (⚙).");
            }

            return assets;
        }

        private Task<string> GetStringAsync(
            string requestUrl, string apiKey, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            return SendAsync(request, apiKey, cancellationToken);
        }

        /// <summary>
        /// Отправляет запрос с ключом доступа и разбирает частые отказы в понятный текст.
        /// </summary>
        private async Task<string> SendAsync(
            HttpRequestMessage request, string apiKey, CancellationToken cancellationToken)
        {
            using (request)
            {
                request.Headers.Add(ApiKeyHeaderName, apiKey);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient
                        .SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException networkFailure)
                {
                    throw new PhotoSourceException(
                        $"Нет связи с сервером Immich ({FrameSettings.ImmichServerUrl}).",
                        networkFailure);
                }
                catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new PhotoSourceException("Сервер Immich не ответил вовремя.", timeout);
                }
                catch (UriFormatException badUrl)
                {
                    throw new PhotoSourceException(
                        "Адрес сервера Immich выглядит некорректно.", badUrl);
                }
                catch (InvalidOperationException badUrl)
                {
                    throw new PhotoSourceException(
                        "Адрес сервера Immich выглядит некорректно.", badUrl);
                }

                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw new PhotoSourceException(
                            "Immich не принял ключ доступа. Создайте новый в Account Settings → API Keys.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new PhotoSourceException(
                            $"Сервер Immich ответил ошибкой ({(int)response.StatusCode}).");
                    }

                    return await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Приводит кэш в соответствие с сервером: качает недостающее, убирает лишнее.
        /// </summary>
        private async Task<AlbumSyncResult> SyncPhotosAsync(
            string serverUrl,
            string apiKey,
            List<ImmichAsset> assets,
            IProgress<(int Completed, int Total)>? downloadProgress,
            CancellationToken cancellationToken)
        {
            string photoDirectory = PhotoLibraryDirectory;
            Directory.CreateDirectory(photoDirectory);
            RemoveLeftoverTempFiles(photoDirectory);

            var desiredFileNames = new List<string>(assets.Count);
            var missingAssets = new List<(ImmichAsset Asset, string FileName)>();

            foreach (ImmichAsset asset in assets)
            {
                string fileName = BuildCacheFileName(asset.Id);
                desiredFileNames.Add(fileName);

                if (!File.Exists(Path.Combine(photoDirectory, fileName)))
                {
                    missingAssets.Add((asset, fileName));
                }
            }

            int downloadedCount = 0;
            for (int missingIndex = 0; missingIndex < missingAssets.Count; missingIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (ImmichAsset asset, string fileName) = missingAssets[missingIndex];

                if (await TryDownloadPhotoAsync(
                        serverUrl, apiKey, photoDirectory, asset.Id, fileName, cancellationToken)
                    .ConfigureAwait(false))
                {
                    downloadedCount++;
                }

                downloadProgress?.Report((missingIndex + 1, missingAssets.Count));
            }

            // В манифест попадает только то, что действительно лежит на диске: один
            // неудачно скачанный снимок не должен ломать показ.
            var availableFileNames = new List<string>(desiredFileNames.Count);
            foreach (string fileName in desiredFileNames)
            {
                if (File.Exists(Path.Combine(photoDirectory, fileName)))
                {
                    availableFileNames.Add(fileName);
                }
            }

            if (availableFileNames.Count == 0)
            {
                throw new PhotoSourceException(
                    "Не удалось получить из Immich ни одного снимка — прежний набор сохранён.");
            }

            int removedCount = RemoveFilesOutsideLibrary(photoDirectory, availableFileNames);
            WriteManifest(photoDirectory, availableFileNames);

            // Список пишется после файлов: подписи и клипы нужны только тем кадрам,
            // которые уже лежат в кэше.
            ImmichSidecar.WriteIndex(assets, asset => BuildCacheFileName(asset.Id));

            return new AlbumSyncResult(
                TotalPhotoCount: availableFileNames.Count,
                DownloadedCount: downloadedCount,
                ReusedCount: availableFileNames.Count - downloadedCount,
                RemovedCount: removedCount);
        }

        private async Task<bool> TryDownloadPhotoAsync(
            string serverUrl,
            string apiKey,
            string photoDirectory,
            string assetId,
            string fileName,
            CancellationToken cancellationToken)
        {
            string finalPath = Path.Combine(photoDirectory, fileName);
            string tempPath = finalPath + ".tmp";

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, ImmichCatalog.BuildPreviewUrl(serverUrl, assetId));
                request.Headers.Add(ApiKeyHeaderName, apiKey);

                using HttpResponseMessage response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                byte[] imageBytes = await response.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (imageBytes.Length == 0)
                {
                    return false;
                }

                // Сначала во временный файл, потом переименование: оборванная загрузка
                // не оставит битый кадр под именем, которое кэш считает готовым.
                await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(tempPath, finalPath, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception singlePhotoFailure) when (
                singlePhotoFailure is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // Один недоступный снимок не должен срывать всю синхронизацию.
                FrameLog.Warn($"Снимок Immich {assetId} не скачан: {singlePhotoFailure.Message}");
                TryDeleteFile(tempPath);
                return false;
            }
        }

        /// <summary>
        /// Убирает из набора снимки, отправленные в корзину рамки, и возвращает их число.
        /// </summary>
        private static int RemoveTrashedAssets(List<ImmichAsset> assets)
        {
            string[] trashedFileNames = FrameSettings.TrashedAlbumFileNames;
            if (trashedFileNames.Length == 0)
            {
                return 0;
            }

            var trashedSet = new HashSet<string>(trashedFileNames, StringComparer.OrdinalIgnoreCase);
            return assets.RemoveAll(asset => trashedSet.Contains(BuildCacheFileName(asset.Id)));
        }

        /// <summary>
        /// Имя файла в кэше.
        /// </summary>
        /// <remarks>
        /// Идентификатор объекта — UUID, то есть уже готовое имя файла, но полагаться
        /// на это нельзя: сервер вправе выдавать любые строки, а в имени файла нельзя
        /// косую черту. Хэш и короче, и безопасен.
        /// </remarks>
        private static string BuildCacheFileName(string assetId)
        {
            byte[] idHash = SHA256.HashData(Encoding.UTF8.GetBytes(assetId));
            return string.Concat(Convert.ToHexString(idHash).AsSpan(0, 20), ".jpg");
        }

        /// <summary>True, если набор совпадает с уже скачанным и файлы на месте.</summary>
        private static bool IsAlreadyDownloaded(string signature) =>
            Preferences.Default.Get(SignatureKey, string.Empty) == signature
            && GetCachedPhotoPaths().Count > 0;

        /// <summary>Отпечаток набора: по нему видно, менялось ли что-то на сервере.</summary>
        private static string ComputeSignature(List<ImmichAsset> assets)
        {
            var assetIds = new List<string>(assets.Count);
            foreach (ImmichAsset asset in assets)
            {
                assetIds.Add(asset.Id);
            }

            byte[] joinedIdsHash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', assetIds)));
            return Convert.ToHexString(joinedIdsHash);
        }

        /// <summary>Удаляет кадры, которых на сервере больше нет.</summary>
        private static int RemoveFilesOutsideLibrary(string photoDirectory, List<string> keepFileNames)
        {
            var keepSet = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);
            int removedCount = 0;

            foreach (string existingPath in Directory.GetFiles(photoDirectory))
            {
                if (!MediaFileTypes.IsSupportedMedia(existingPath))
                {
                    continue;
                }

                if (!keepSet.Contains(Path.GetFileName(existingPath)) && TryDeleteFile(existingPath))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        private static void RemoveLeftoverTempFiles(string photoDirectory)
        {
            foreach (string tempPath in Directory.GetFiles(photoDirectory, "*.tmp"))
            {
                TryDeleteFile(tempPath);
            }
        }

        /// <summary>
        /// Пишет манифест через временный файл: пока он не заменён, слайд-шоу
        /// продолжает работать по прежнему списку.
        /// </summary>
        private static void WriteManifest(string photoDirectory, List<string> orderedFileNames)
        {
            string manifestPath = Path.Combine(photoDirectory, ManifestFileName);
            string tempManifestPath = manifestPath + ".tmp";

            try
            {
                File.WriteAllLines(tempManifestPath, orderedFileNames);
                File.Move(tempManifestPath, manifestPath, overwrite: true);
            }
            catch (Exception manifestFailure) when (
                manifestFailure is IOException or UnauthorizedAccessException)
            {
                TryDeleteFile(tempManifestPath);
                throw new PhotoSourceException(
                    "Не удалось сохранить список снимков Immich на диске рамки.", manifestFailure);
            }
        }

        private static bool TryDeleteFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (Exception deleteFailure) when (
                deleteFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Не удалось удалить {filePath}: {deleteFailure.Message}");
                return false;
            }
        }

        /// <summary>Пути к снимкам в порядке, взятом из манифеста.</summary>
        public static List<string> GetCachedPhotoPaths()
        {
            string photoDirectory = PhotoLibraryDirectory;
            string manifestPath = Path.Combine(photoDirectory, ManifestFileName);
            var photoPaths = new List<string>();

            if (!File.Exists(manifestPath))
            {
                return photoPaths;
            }

            try
            {
                foreach (string fileName in File.ReadAllLines(manifestPath))
                {
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string photoPath = Path.Combine(photoDirectory, fileName);
                    if (File.Exists(photoPath))
                    {
                        photoPaths.Add(photoPath);
                    }
                }
            }
            catch (Exception manifestReadFailure) when (
                manifestReadFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Манифест Immich не прочитан: {manifestReadFailure.Message}");
            }

            return photoPaths;
        }
    }
}
