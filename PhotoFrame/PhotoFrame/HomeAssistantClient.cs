using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

        private async Task<JsonDocument> GetJsonAsync(
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
                throw new PhotoSourceException(
                    "Home Assistant недоступен по указанному адресу.", networkFailure);
            }
            catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PhotoSourceException("Home Assistant не ответил вовремя.", timeout);
            }
            catch (UriFormatException badUrl)
            {
                throw new PhotoSourceException(
                    "Адрес Home Assistant выглядит некорректно.", badUrl);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new PhotoSourceException(
                        "Home Assistant отклонил токен. Создайте новый долгоживущий токен.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new PhotoSourceException(
                        $"Home Assistant вернул ошибку ({(int)response.StatusCode}).");
                }

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
