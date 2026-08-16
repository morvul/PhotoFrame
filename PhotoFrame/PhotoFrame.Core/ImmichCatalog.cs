using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PhotoFrame
{
    /// <summary>Альбом на сервере Immich.</summary>
    /// <param name="Id">Идентификатор альбома, он же часть адреса запроса.</param>
    /// <param name="Name">Название, как его видит пользователь.</param>
    /// <param name="AssetCount">Сколько в альбоме объектов по данным сервера.</param>
    public sealed record ImmichAlbum(string Id, string Name, int AssetCount);

    /// <summary>Снимок или видео на сервере Immich.</summary>
    /// <param name="Id">Идентификатор объекта: по нему строится адрес загрузки.</param>
    /// <param name="FileName">Исходное имя файла — только для подписей и отладки.</param>
    /// <param name="IsVideo">True для видео: такие объекты рамка пока пропускает.</param>
    public readonly record struct ImmichAsset(string Id, string FileName, bool IsVideo);

    /// <summary>
    /// Адреса и разбор ответов Immich.
    /// </summary>
    /// <remarks>
    /// В отличие от Google Photos, здесь есть документированный HTTP API, поэтому
    /// разбирается JSON, а не вёрстка страницы. Ключ доступа создаётся в самом Immich
    /// (Account Settings → API Keys) и передаётся заголовком x-api-key.
    ///
    /// Ответы читаются через JsonDocument, а не через десериализацию в типы: полей у
    /// объекта Immich несколько десятков, нужны из них три, а версии сервера эти поля
    /// добавляют и переименовывают. Пропущенное поле здесь означает пропущенный объект,
    /// а не исключение на весь ответ.
    ///
    /// Разбор вынесен из проекта приложения в Core, чтобы покрыть его тестами: собрать
    /// MAUI под Android ради проверки одной строки JSON невозможно.
    /// </remarks>
    public static class ImmichCatalog
    {
        /// <summary>
        /// Приводит введённый пользователем адрес сервера к виду «схема://хост:порт».
        /// </summary>
        /// <remarks>
        /// Адрес набирают на самой рамке, пультом по экранной клавиатуре, поэтому здесь
        /// прощаются частые огрехи: пропущенная схема (в домашней сети сервер почти
        /// всегда по http), косая черта на конце и скопированный из документации
        /// суффикс /api — его добавляет уже сама сборка адресов.
        /// </remarks>
        public static string NormalizeServerUrl(string? serverUrl)
        {
            string trimmedUrl = (serverUrl ?? string.Empty).Trim();
            if (trimmedUrl.Length == 0)
            {
                return string.Empty;
            }

            if (!trimmedUrl.Contains("://", StringComparison.Ordinal))
            {
                trimmedUrl = "http://" + trimmedUrl;
            }

            trimmedUrl = trimmedUrl.TrimEnd('/');

            if (trimmedUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                trimmedUrl = trimmedUrl[..^"/api".Length];
            }

            return trimmedUrl;
        }

        /// <summary>Список альбомов пользователя.</summary>
        public static string BuildAlbumsUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/albums";

        /// <summary>Постраничный поиск по библиотеке или по одному альбому.</summary>
        public static string BuildSearchUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/search/metadata";

        /// <summary>Кто владелец ключа: этим запросом проверяется связь.</summary>
        public static string BuildIdentityUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/users/me";

        /// <summary>
        /// Адрес готового изображения объекта.
        /// </summary>
        /// <remarks>
        /// Берётся preview, а не original: сервер уже сделал из снимка JPEG около
        /// 1440 пикселей по длинной стороне, и для экрана 1280x800 этого с запасом.
        /// Оригинал был бы в разы тяжелее и на рамке всё равно ужимался бы при показе.
        /// </remarks>
        public static string BuildPreviewUrl(string serverUrl, string assetId) =>
            NormalizeServerUrl(serverUrl) + "/api/assets/" + Uri.EscapeDataString(assetId)
            + "/thumbnail?size=preview";

        /// <summary>
        /// Тело запроса к постраничному поиску.
        /// </summary>
        /// <remarks>
        /// Содержимое альбома берётся тем же запросом с фильтром albumIds, а не через
        /// /api/albums/{id}: проверенный сервер отдаёт оттуда одни сведения об альбоме,
        /// без списка снимков (682 байта, поля assets нет вовсе) — даже с явным
        /// withoutAssets=false. Через поиск ответ ещё и постраничный, так что альбом
        /// на десять тысяч кадров не придёт одним куском.
        ///
        /// Тип объектов намеренно не ограничивается: видео нужно посчитать, чтобы
        /// сказать, сколько их пропущено, а отфильтрованных сервером не увидеть.
        /// </remarks>
        /// <param name="albumId">Пусто — искать по всей библиотеке.</param>
        public static string BuildSearchRequestBody(int pageNumber, int pageSize, string? albumId = null)
        {
            string albumFilter = string.IsNullOrEmpty(albumId)
                ? string.Empty
                : $",\"albumIds\":[\"{albumId}\"]";

            return $"{{\"page\":{pageNumber},\"size\":{pageSize},\"withDeleted\":false{albumFilter}}}";
        }

        /// <summary>Разбирает ответ /api/albums.</summary>
        public static List<ImmichAlbum> ParseAlbums(string albumsJson)
        {
            var albums = new List<ImmichAlbum>();

            using JsonDocument? document = TryParse(albumsJson);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return albums;
            }

            foreach (JsonElement albumElement in document.RootElement.EnumerateArray())
            {
                string? albumId = ReadString(albumElement, "id");
                if (albumId is null)
                {
                    continue;
                }

                albums.Add(new ImmichAlbum(
                    albumId,
                    ReadString(albumElement, "albumName") ?? albumId,
                    ReadInt(albumElement, "assetCount")));
            }

            return albums;
        }

        /// <summary>
        /// Разбирает ответ /api/search/metadata.
        /// </summary>
        /// <param name="nextPageNumber">
        /// Номер следующей страницы либо 0, если эта страница последняя. Поле nextPage
        /// сервер отдаёт строкой, и в разных версиях оно бывает и числом, и null.
        /// </param>
        public static List<ImmichAsset> ParseSearchAssets(string searchJson, out int nextPageNumber)
        {
            nextPageNumber = 0;

            using JsonDocument? document = TryParse(searchJson);
            if (document is null
                || !document.RootElement.TryGetProperty("assets", out JsonElement assetsElement))
            {
                return new List<ImmichAsset>();
            }

            if (assetsElement.TryGetProperty("nextPage", out JsonElement nextPageElement))
            {
                nextPageNumber = nextPageElement.ValueKind switch
                {
                    JsonValueKind.Number => nextPageElement.TryGetInt32(out int page) ? page : 0,
                    JsonValueKind.String =>
                        int.TryParse(nextPageElement.GetString(), out int page) ? page : 0,
                    _ => 0,
                };
            }

            return assetsElement.TryGetProperty("items", out JsonElement itemsElement)
                ? ReadAssetArray(itemsElement)
                : new List<ImmichAsset>();
        }

        private static List<ImmichAsset> ReadAssetArray(JsonElement arrayElement)
        {
            var assets = new List<ImmichAsset>();

            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return assets;
            }

            foreach (JsonElement assetElement in arrayElement.EnumerateArray())
            {
                string? assetId = ReadString(assetElement, "id");
                if (assetId is null)
                {
                    continue;
                }

                // Удалённое в корзину сервера показывать незачем: пользователь уже сказал,
                // что этот снимок ему не нужен.
                if (ReadBool(assetElement, "isTrashed"))
                {
                    continue;
                }

                bool isVideo = string.Equals(
                    ReadString(assetElement, "type"), "VIDEO", StringComparison.OrdinalIgnoreCase);

                assets.Add(new ImmichAsset(
                    assetId,
                    ReadString(assetElement, "originalFileName") ?? assetId,
                    isVideo));
            }

            return assets;
        }

        /// <summary>
        /// Ответ мог оказаться не JSON — например, страницей входа обратного прокси.
        /// Такой случай разбирается вызывающим кодом как «объектов не нашлось».
        /// </summary>
        private static JsonDocument? TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadString(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static int ReadInt(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int value)
                ? value
                : 0;

        private static bool ReadBool(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.True;
    }
}
