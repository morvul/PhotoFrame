using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>
    /// Читает значения датчиков из Home Assistant по локальному REST API.
    /// </summary>
    /// <remarks>
    /// Выбран именно Home Assistant, а не облако Tuya: долгоживущий токен HA не истекает,
    /// тогда как подписка Tuya IoT Core прекращает работу API по окончании пробного
    /// периода. Обращение идёт по внутреннему адресу, поэтому датчики читаются и при
    /// отсутствии интернета.
    /// </remarks>
    public class HomeAssistantClient
    {
        /// <summary>
        /// От какого количества выбранных датчиков выгоднее один запрос всех состояний,
        /// чем по запросу на каждую сущность.
        /// </summary>
        /// <remarks>
        /// Порог грубый: ответ /api/states — это состояние всего дома (десятки килобайт),
        /// а запрос одной сущности стоит ещё и обращения по сети. На пяти датчиках расходы
        /// примерно равны, дальше выигрывает пакетное чтение.
        /// </remarks>
        private const int BulkReadThreshold = 5;

        private const string CameraEntityPrefix = "camera.";

        /// <summary>Сколько ждать весь обмен по WebSocket, от подключения до ответа камеры.</summary>
        private static readonly TimeSpan WebSocketTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;

        public HomeAssistantClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            if (!string.IsNullOrEmpty(LocalConfig.HomeAssistantToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", LocalConfig.HomeAssistantToken);
            }
        }

        /// <summary>True, если адрес и токен заданы в secrets.props.</summary>
        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(LocalConfig.HomeAssistantBaseUrl)
            && !string.IsNullOrWhiteSpace(LocalConfig.HomeAssistantToken);

        private static string BaseUrl => LocalConfig.HomeAssistantBaseUrl.TrimEnd('/');

        /// <summary>
        /// Возвращает все сущности с числовым значением и единицей измерения —
        /// то есть то, что имеет смысл вывести на экран.
        /// </summary>
        public async Task<List<HomeAssistantSensor>> GetNumericSensorsAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var sensors = new List<HomeAssistantSensor>();

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                HomeAssistantSensor? sensor = TryReadSensor(entity);
                if (sensor is not null)
                {
                    sensors.Add(sensor);
                }
            }

            // Сортировка по «интересности» класса, затем по имени. Простая сортировка по
            // имени класса ставила первыми battery: шесть батареек шторок заслоняли
            // термометры, ради которых список и открывают.
            sensors.Sort((first, second) =>
            {
                int byPriority = GetDeviceClassPriority(first.DeviceClass)
                                 - GetDeviceClassPriority(second.DeviceClass);
                if (byPriority != 0)
                {
                    return byPriority;
                }

                int byClass = string.Compare(
                    first.DeviceClass ?? "￿", second.DeviceClass ?? "￿",
                    StringComparison.OrdinalIgnoreCase);

                return byClass != 0
                    ? byClass
                    : string.Compare(
                        first.DisplayName, second.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return sensors;
        }

        /// <summary>
        /// Насколько класс датчика уместен на фоторамке: чем меньше число, тем выше в списке.
        /// </summary>
        /// <remarks>
        /// Заряд батареек в списке нужен последним: он есть почти у каждого устройства
        /// и на рамке никому не интересен.
        /// </remarks>
        private static int GetDeviceClassPriority(string? deviceClass) => deviceClass switch
        {
            "temperature" => 0,
            "humidity" => 1,
            "pressure" or "atmospheric_pressure" => 2,
            "carbon_dioxide" or "pm25" or "pm10" or "aqi" => 3,
            "illuminance" => 4,
            "power" or "current" or "voltage" => 5,
            "energy" => 6,
            "battery" => 9,
            _ => 7,
        };

        /// <summary>
        /// Читает текущие значения выбранных датчиков в порядке, заданном пользователем.
        /// </summary>
        /// <remarks>
        /// Пока датчиков мало, каждая сущность запрашивается отдельно: это дешевле, чем
        /// тянуть состояние всего дома. С длинным списком выгоднее наоборот — один запрос
        /// вместо десятка обращений по сети, см. <see cref="BulkReadThreshold"/>.
        /// </remarks>
        public async Task<List<(string EntityId, string Text)>> ReadSensorValuesAsync(
            IReadOnlyList<string> entityIds,
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            if (entityIds.Count >= BulkReadThreshold)
            {
                return await ReadSensorValuesInBulkAsync(entityIds, cancellationToken)
                    .ConfigureAwait(false);
            }

            var formattedValues = new List<(string EntityId, string Text)>(entityIds.Count);

            foreach (string entityId in entityIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using JsonDocument stateDocument = await GetJsonAsync(
                        "/api/states/" + Uri.EscapeDataString(entityId), cancellationToken)
                        .ConfigureAwait(false);

                    HomeAssistantSensor? sensor = TryReadSensor(stateDocument.RootElement);
                    if (sensor is not null)
                    {
                        formattedValues.Add((entityId, sensor.ValueWithUnit));
                    }
                }
                catch (PhotoSourceException singleSensorFailure)
                {
                    // Один недоступный датчик не должен убирать с экрана остальные.
                    System.Diagnostics.Debug.WriteLine(
                        $"Датчик {entityId} не прочитан: {singleSensorFailure.Message}");
                }
            }

            return formattedValues;
        }

        /// <summary>
        /// Все сущности камер, по алфавиту. Пустой список, если камер нет.
        /// </summary>
        /// <remarks>
        /// Алфавитный порядок, а не порядок ответа /api/states: последний не гарантирован
        /// и может меняться при перезапуске Home Assistant, а рамке нужен стабильный выбор.
        /// Список, а не первая камера: одна из нескольких может не отдавать поток (сама
        /// камера offline, интеграция не поддерживает stream), и это видно только выбором.
        /// </remarks>
        public async Task<List<string>> GetCameraEntityIdsAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var cameraEntityIds = new List<string>();

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                if (entity.TryGetProperty("entity_id", out JsonElement entityIdElement)
                    && entityIdElement.GetString() is string entityId
                    && entityId.StartsWith(CameraEntityPrefix, StringComparison.Ordinal))
                {
                    cameraEntityIds.Add(entityId);
                }
            }

            cameraEntityIds.Sort(StringComparer.OrdinalIgnoreCase);
            return cameraEntityIds;
        }

        /// <summary>
        /// Запрашивает адрес живого потока камеры со звуком.
        /// </summary>
        /// <remarks>
        /// REST-адреса Home Assistant отдают только снимок или MJPEG без звука.
        /// Ссылку на HLS-плейлист со звуком отдаёт лишь команда camera/stream по
        /// WebSocket API. Соединение открывается на одну команду и закрывается сразу
        /// после ответа — держать его на всё время просмотра не нужно, ссылка уже несёт
        /// временный токен доступа в собственном пути.
        /// </remarks>
        public async Task<string> GetCameraStreamUrlAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var socket = new ClientWebSocket();

            // Своя граница по времени: без неё зависший обмен по WebSocket держал бы
            // страницу камеры на «Подключение...» бесконечно — ни исключения, ни следа
            // в журнале, только вечный спиннер.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(WebSocketTimeout);
            CancellationToken linkedToken = timeoutCts.Token;

            try
            {
                await socket.ConnectAsync(BuildWebSocketUri(), linkedToken).ConfigureAwait(false);

                // Первое сообщение сервера — приглашение авторизоваться, само оно не нужно.
                using (await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false))
                {
                }

                await SendJsonAsync(
                    socket,
                    new { type = "auth", access_token = LocalConfig.HomeAssistantToken },
                    linkedToken).ConfigureAwait(false);

                using JsonDocument authResponse =
                    await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false);

                if (ReadStringAttribute(authResponse.RootElement, "type") != "auth_ok")
                {
                    throw new PhotoSourceException("Home Assistant отклонил токен по WebSocket.");
                }

                await SendJsonAsync(
                    socket,
                    new { id = 1, type = "camera/stream", entity_id = entityId },
                    linkedToken).ConfigureAwait(false);

                using JsonDocument streamResponse =
                    await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false);

                if (!streamResponse.RootElement.TryGetProperty("success", out JsonElement successElement)
                    || successElement.ValueKind != JsonValueKind.True
                    || !streamResponse.RootElement.TryGetProperty("result", out JsonElement resultElement)
                    || !resultElement.TryGetProperty("url", out JsonElement urlElement)
                    || urlElement.GetString() is not string streamPath)
                {
                    throw new PhotoSourceException("Камера не отдаёт поток через Home Assistant.");
                }

                string streamUrl = streamPath.StartsWith("/", StringComparison.Ordinal)
                    ? BaseUrl + streamPath
                    : streamPath;

                // Разово в журнал: тот же адрес можно проверить curl'ом отдельно от
                // рамки, чтобы понять, кто именно не отвечает — Home Assistant или
                // ExoPlayer на устройстве.
                FrameLog.Info($"Поток камеры {entityId}: {streamUrl}");

                return streamUrl;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FrameLog.Warn($"Home Assistant не ответил по WebSocket за {WebSocketTimeout}.");
                throw new PhotoSourceException("Камера Home Assistant не ответила по WebSocket вовремя.");
            }
            catch (WebSocketException socketFailure)
            {
                FrameLog.Warn(
                    $"WebSocket Home Assistant недоступен ({socketFailure.WebSocketErrorCode}): "
                    + $"{socketFailure.Message} — {socketFailure.InnerException?.Message}");
                throw new PhotoSourceException("Home Assistant не ответил по WebSocket.", socketFailure);
            }
            finally
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket
                            .CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        // Соединение всё равно больше не нужно — не закрылось, не беда.
                    }
                }
            }
        }

        private static Uri BuildWebSocketUri()
        {
            string httpBaseUrl = BaseUrl;

            string scheme = httpBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "wss://"
                : "ws://";

            int schemeSeparatorIndex = httpBaseUrl.IndexOf("://", StringComparison.Ordinal);
            string hostAndPath = schemeSeparatorIndex >= 0
                ? httpBaseUrl[(schemeSeparatorIndex + 3)..]
                : httpBaseUrl;

            return new Uri(scheme + hostAndPath + "/api/websocket");
        }

        private static Task SendJsonAsync(
            ClientWebSocket socket, object payload, CancellationToken cancellationToken)
        {
            byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            return socket.SendAsync(
                payloadBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private static async Task<JsonDocument> ReceiveJsonAsync(
            ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var messageBuffer = new MemoryStream();
            var receiveBuffer = new byte[8192];

            while (true)
            {
                WebSocketReceiveResult receiveResult = await socket
                    .ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new PhotoSourceException("Home Assistant закрыл WebSocket-соединение.");
                }

                messageBuffer.Write(receiveBuffer, 0, receiveResult.Count);

                if (receiveResult.EndOfMessage)
                {
                    break;
                }
            }

            messageBuffer.Position = 0;
            return JsonDocument.Parse(messageBuffer);
        }

        /// <summary>
        /// Читает значения одним запросом всех состояний, сохраняя порядок выбора.
        /// </summary>
        /// <remarks>
        /// Датчики, которых в ответе нет (сущность удалили или переименовали), просто
        /// пропускаются: остальные значения показать всё равно нужно.
        /// </remarks>
        private async Task<List<(string EntityId, string Text)>> ReadSensorValuesInBulkAsync(
            IReadOnlyList<string> entityIds,
            CancellationToken cancellationToken)
        {
            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var requestedIds = new HashSet<string>(entityIds, StringComparer.OrdinalIgnoreCase);
            var valuesByEntityId = new Dictionary<string, string>(
                entityIds.Count, StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                HomeAssistantSensor? sensor = TryReadSensor(entity);
                if (sensor is not null && requestedIds.Contains(sensor.EntityId))
                {
                    valuesByEntityId[sensor.EntityId] = sensor.ValueWithUnit;
                }
            }

            var formattedValues = new List<(string EntityId, string Text)>(entityIds.Count);

            foreach (string entityId in entityIds)
            {
                if (valuesByEntityId.TryGetValue(entityId, out string? valueWithUnit))
                {
                    formattedValues.Add((entityId, valueWithUnit));
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Датчик {entityId} отсутствует в ответе Home Assistant.");
                }
            }

            return formattedValues;
        }

        /// <summary>
        /// Разбирает одну сущность. Возвращает null, если это не числовой датчик:
        /// у выключателей и текстовых сущностей показывать нечего.
        /// </summary>
        private static HomeAssistantSensor? TryReadSensor(JsonElement entity)
        {
            if (!entity.TryGetProperty("entity_id", out JsonElement entityIdElement)
                || entityIdElement.GetString() is not string entityId)
            {
                return null;
            }

            if (!entity.TryGetProperty("state", out JsonElement stateElement)
                || stateElement.GetString() is not string state)
            {
                return null;
            }

            if (!entity.TryGetProperty("attributes", out JsonElement attributes))
            {
                return null;
            }

            string? unit = ReadStringAttribute(attributes, "unit_of_measurement");
            if (string.IsNullOrEmpty(unit))
            {
                return null;
            }

            // Датчик может быть "unavailable" или "unknown" — такое не показываем.
            if (!double.TryParse(
                    state, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }

            string displayName = ReadStringAttribute(attributes, "friendly_name") ?? entityId;
            string? deviceClass = ReadStringAttribute(attributes, "device_class");

            return new HomeAssistantSensor(entityId, displayName, state, unit, deviceClass);
        }

        private static string? ReadStringAttribute(JsonElement attributes, string name) =>
            attributes.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// Снимок камеры одним запросом — простой JPEG без звука и без задержки ffmpeg.
        /// </summary>
        /// <remarks>
        /// Резервный способ показать камеру, если поток из <see cref="GetCameraStreamUrlAsync"/>
        /// не поднимается: облачные интеграции (Tuya и подобные) часто отдают снимок отдельным
        /// лёгким запросом к своему облаку, а не тем же RTSP-адресом с истекающей подписью,
        /// который может быть недоступен именно в момент запроса потока.
        /// </remarks>
        public async Task<byte[]> GetCameraSnapshotAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using HttpResponseMessage response = await SendGetAsync(
                "/api/camera_proxy/" + Uri.EscapeDataString(entityId), cancellationToken)
                .ConfigureAwait(false);

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendGetAsync(
            string relativePath, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .GetAsync(BaseUrl + relativePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException networkFailure)
            {
                // Сообщение на экране рамки одно и то же для любой сетевой причины,
                // а настоящая — только здесь, в журнале adb logcat.
                FrameLog.Warn(
                    $"Home Assistant недоступен ({relativePath}): "
                    + $"{networkFailure.Message} — {networkFailure.InnerException?.Message}");
                throw new PhotoSourceException(
                    "Home Assistant недоступен по указанному адресу.", networkFailure);
            }
            catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
            {
                FrameLog.Warn($"Home Assistant не ответил вовремя ({relativePath}): {timeout.Message}");
                throw new PhotoSourceException("Home Assistant не ответил вовремя.", timeout);
            }
            catch (UriFormatException badUrl)
            {
                throw new PhotoSourceException(
                    "Адрес Home Assistant выглядит некорректно.", badUrl);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new PhotoSourceException(
                    "Home Assistant отклонил токен. Создайте новый долгоживущий токен.");
            }

            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                response.Dispose();
                throw new PhotoSourceException($"Home Assistant вернул ошибку ({statusCode}).");
            }

            return response;
        }

        private async Task<JsonDocument> GetJsonAsync(
            string relativePath, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response =
                await SendGetAsync(relativePath, cancellationToken).ConfigureAwait(false);

            string responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(responseBody);
            }
            catch (JsonException malformedJson)
            {
                throw new PhotoSourceException(
                    "Home Assistant вернул некорректный JSON.", malformedJson);
            }
        }

        private static void EnsureConfigured()
        {
            if (!IsConfigured)
            {
                throw new PhotoSourceException(
                    "Не заданы адрес и токен Home Assistant в secrets.props.");
            }
        }
    }
}
