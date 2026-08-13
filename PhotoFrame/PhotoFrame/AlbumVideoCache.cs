using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Забирает видео общего альбома в момент, когда его слайд показан.
    /// </summary>
    /// <remarks>
    /// Видео не качаются при синхронизации нарочно: минутный клип весит десятки
    /// мегабайт, и тянуть их все ради кадров, до которых показ может не дойти, незачем.
    /// Поэтому в кэше сперва лежит только заставка, а сам файл появляется рядом
    /// (то же имя, расширение .mp4), когда слайд-шоу доходит до кадра. Раз скачанный
    /// клип остаётся в кэше и на следующем круге играет сразу.
    ///
    /// Ссылки на видео нет в списке альбома — она есть только на странице отдельного
    /// кадра, поэтому здесь два запроса: страница кадра и сам файл.
    /// </remarks>
    internal static class AlbumVideoCache
    {
        private const string IndexFileName = "album.videos";
        private const string AlbumUrlKey = "album_resolved_url";

        /// <summary>
        /// Предел на размер одного клипа. Место на рамке не бесконечно, а размер известен
        /// из заголовков до начала загрузки.
        /// </summary>
        private const long MaxVideoBytes = 200L * 1024 * 1024;

        /// <summary>Одновременно качаем один клип: показан всё равно один кадр.</summary>
        private static readonly SemaphoreSlim DownloadGate = new(1, 1);

        private static string IndexPath =>
            IoPath.Combine(SharedAlbumPhotoSource.PhotoLibraryDirectory, IndexFileName);

        /// <summary>
        /// True, если список видео уже составлен.
        /// </summary>
        /// <remarks>
        /// Проверяется перед тем, как пропустить синхронизацию по совпавшему отпечатку
        /// альбома: кэш, набранный прежними версиями, знает только о снимках, и без
        /// полного прохода видео так и остались бы заставками.
        /// </remarks>
        public static bool HasIndex => File.Exists(IndexPath);

        /// <summary>Запоминает адрес альбома после переходов — из него собирается адрес кадра.</summary>
        public static void RememberAlbumUrl(string resolvedAlbumUrl) =>
            Preferences.Default.Set(AlbumUrlKey, resolvedAlbumUrl);

        /// <summary>
        /// Записывает, какие заставки на самом деле видео и какому кадру принадлежат.
        /// </summary>
        public static void WriteIndex(List<AlbumItem> albumItems, Func<AlbumItem, string> posterFileName)
        {
            var lines = new List<string>();
            foreach (AlbumItem albumItem in albumItems)
            {
                if (albumItem.IsVideo && !string.IsNullOrEmpty(albumItem.ItemId))
                {
                    lines.Add(string.Join(
                        '\t',
                        posterFileName(albumItem),
                        albumItem.ItemId,
                        albumItem.VideoDurationMilliseconds));
                }
            }

            FrameLog.Info($"Видео в альбоме: {lines.Count}");

            string indexPath = IndexPath;
            string temporaryPath = indexPath + ".tmp";

            try
            {
                File.WriteAllLines(temporaryPath, lines);
                File.Move(temporaryPath, indexPath, overwrite: true);
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                // Без списка видео покажутся заставками — как до появления этой возможности.
                System.Diagnostics.Debug.WriteLine(
                    $"Список видео альбома не сохранён: {writeFailure.Message}");
            }
        }

        /// <summary>True, если за этой заставкой стоит видео.</summary>
        public static bool IsVideoPoster(string posterPath) => FindItemId(posterPath) is not null;

        /// <summary>
        /// Длительность видео за заставкой в миллисекундах; 0 — это не видео.
        /// </summary>
        public static int FindVideoDuration(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);

            foreach (string line in ReadIndex())
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 3
                    && parts[0].Equals(posterFileName, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[2], out int durationMilliseconds))
                {
                    return durationMilliseconds;
                }
            }

            return 0;
        }

        /// <summary>Путь к уже скачанному клипу либо null.</summary>
        public static string? FindReadyVideo(string posterPath)
        {
            string videoPath = BuildVideoPath(posterPath);
            return File.Exists(videoPath) ? videoPath : null;
        }

        /// <summary>
        /// Возвращает путь к клипу, при необходимости скачав его. Null — не удалось.
        /// </summary>
        public static async Task<string?> TryGetVideoAsync(
            string posterPath, CancellationToken cancellationToken = default)
        {
            string? readyPath = FindReadyVideo(posterPath);
            if (readyPath is not null)
            {
                return readyPath;
            }

            string? itemId = FindItemId(posterPath);
            string? itemPageUrl = BuildItemPageUrl(itemId);
            if (itemPageUrl is null)
            {
                return null;
            }

            await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Пока ждали очереди, клип мог уже скачаться.
                return FindReadyVideo(posterPath)
                       ?? await DownloadAsync(posterPath, itemPageUrl, cancellationToken)
                           .ConfigureAwait(false);
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        private static async Task<string?> DownloadAsync(
            string posterPath, string itemPageUrl, CancellationToken cancellationToken)
        {
            string videoPath = BuildVideoPath(posterPath);
            string temporaryPath = videoPath + ".tmp";

            // Свой клиент: у страницы альбома свой, живущий в источнике снимков, и делить
            // его между слоями незачем.
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/120.0.0.0 Safari/537.36");

            try
            {
                string itemPageHtml = await httpClient
                    .GetStringAsync(itemPageUrl, cancellationToken).ConfigureAwait(false);

                string? videoUrl = AlbumVideoUrlExtractor.Extract(itemPageHtml);
                if (videoUrl is null)
                {
                    FrameLog.Warn("На странице кадра нет ссылки на видео.");
                    return null;
                }

                using HttpResponseMessage response = await httpClient
                    .GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    FrameLog.Warn($"Видео не отдано: {(int)response.StatusCode}.");
                    return null;
                }

                if (response.Content.Headers.ContentLength > MaxVideoBytes)
                {
                    FrameLog.Warn(
                        $"Видео пропущено: {response.Content.Headers.ContentLength} байт.");
                    return null;
                }

                // Через временный файл: прерванная загрузка не оставит обрезанный клип
                // под именем, которое кэш считает готовым.
                await using (FileStream temporaryFile = File.Create(temporaryPath))
                {
                    await response.Content.CopyToAsync(temporaryFile, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(temporaryPath, videoPath, overwrite: true);
                FrameLog.Info($"Видео скачано: {new FileInfo(videoPath).Length} байт.");
                return videoPath;
            }
            catch (Exception downloadFailure) when (
                downloadFailure is HttpRequestException or IOException
                    or UnauthorizedAccessException or TaskCanceledException)
            {
                FrameLog.Warn($"Видео не скачано: {downloadFailure.Message}");
                TryDelete(temporaryPath);
                return null;
            }
        }

        /// <summary>Клип лежит рядом с заставкой под тем же именем.</summary>
        private static string BuildVideoPath(string posterPath) =>
            IoPath.ChangeExtension(posterPath, ".mp4");

        private static string? FindItemId(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);

            foreach (string line in ReadIndex())
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2
                    && parts[0].Equals(posterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1];
                }
            }

            return null;
        }

        private static string[] ReadIndex()
        {
            try
            {
                return File.Exists(IndexPath) ? File.ReadAllLines(IndexPath) : Array.Empty<string>();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Список видео альбома не прочитан: {readFailure.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Адрес страницы кадра: из /share/&lt;альбом&gt;?key=… получается
        /// /share/&lt;альбом&gt;/photo/&lt;кадр&gt;?key=…
        /// </summary>
        private static string? BuildItemPageUrl(string? itemId)
        {
            string albumUrl = Preferences.Default.Get(AlbumUrlKey, string.Empty);

            if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(albumUrl)
                || !Uri.TryCreate(albumUrl, UriKind.Absolute, out Uri? albumUri))
            {
                return null;
            }

            string albumPath = albumUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return $"{albumPath}/photo/{itemId}{albumUri.Query}";
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception deleteFailure) when (
                deleteFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Не удалось убрать {filePath}: {deleteFailure.Message}");
            }
        }
    }
}
