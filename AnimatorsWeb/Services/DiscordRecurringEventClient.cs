using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimatorsWeb.Services
{
    public class DiscordRecurringEventClient
    {
        public const int FrequencyWeekly = 2;

        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<DiscordRecurringEventClient> _logger;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Для тел событий: image=null означает «убрать обложку».</summary>
        private static readonly JsonSerializerOptions JsonOptsWithNulls = new();

        public DiscordRecurringEventClient(
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger<DiscordRecurringEventClient> logger)
        {
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        private HttpClient CreateClient()
        {
            var token = _config["Discord:BotToken"]
                ?? throw new InvalidOperationException("Discord:BotToken is not configured");
            var http = _httpFactory.CreateClient("DiscordApi");
            http.Timeout = TimeSpan.FromSeconds(20);
            http.BaseAddress ??= new Uri("https://discord.com/api/v10/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            if (!http.DefaultRequestHeaders.Contains("User-Agent"))
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DiscordBot (https://animators-community.com.ru, 1.0)");
            return http;
        }

        // -------- Events --------

        public async Task<string?> CreateRecurringAsync(
            ulong guildId,
            ulong? channelId,
            string name,
            string? description,
            DateTime startUtc,
            DateTime endUtc,
            DiscordRecurrenceRule rule,
            CancellationToken ct = default,
            string? imageDataUri = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["scheduled_start_time"] = ToIso(startUtc),
                ["scheduled_end_time"] = ToIso(endUtc),
                ["privacy_level"] = 2,
                ["image"] = imageDataUri,
                ["entity_type"] = channelId.HasValue ? 2 : 3,
                ["recurrence_rule"] = new Dictionary<string, object?>
                {
                    ["start"] = rule.Start,
                    ["frequency"] = rule.Frequency,
                    ["interval"] = rule.Interval,
                    ["by_weekday"] = rule.ByWeekday
                }
            };
            if (channelId.HasValue) body["channel_id"] = channelId.Value.ToString();
            else body["entity_metadata"] = new Dictionary<string, object?> { ["location"] = "" };

            var json = JsonSerializer.Serialize(body, JsonOptsWithNulls);
            var (ok, resp) = await SendAsync(HttpMethod.Post, $"guilds/{guildId}/scheduled-events", json, ct);
            if (!ok || resp == null)
            {
                _logger.LogError("CreateRecurringAsync failed: {Status} {Body}", resp?.Status, resp?.Body);
                return null;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                return idEl.GetString();
            return null;
        }

        public async Task<bool> ModifyRecurringAsync(
            ulong guildId,
            ulong eventId,
            string name,
            string? description,
            DateTime startUtc,
            DateTime endUtc,
            DiscordRecurrenceRule? rule,
            CancellationToken ct = default,
            bool includeRule = true,
            string? imageDataUri = null,
            bool keepExistingImageWhenNull = false)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["scheduled_start_time"] = ToIso(startUtc),
                ["scheduled_end_time"] = ToIso(endUtc)
            };
            // Для обычного обновления image=null очищает обложку. При усыновлении
            // существующего события обложку не трогаем, если у пресета её нет.
            if (!(keepExistingImageWhenNull && imageDataUri == null))
                body["image"] = imageDataUri;
            if (rule != null && includeRule)
            {
                body["recurrence_rule"] = new Dictionary<string, object?>
                {
                    ["start"] = rule.Start,
                    ["frequency"] = rule.Frequency,
                    ["interval"] = rule.Interval,
                    ["by_weekday"] = rule.ByWeekday
                };
            }

            var json = JsonSerializer.Serialize(body, JsonOptsWithNulls);
            var (ok, resp) = await SendAsync(HttpMethod.Patch, $"guilds/{guildId}/scheduled-events/{eventId}", json, ct);
            if (!ok)
                _logger.LogWarning("ModifyRecurringAsync failed for {EventId}: {Status} {Body}", eventId, resp?.Status, resp?.Body);
            return ok;
        }

        public async Task<bool> DeleteAsync(ulong guildId, ulong eventId, CancellationToken ct = default)
        {
            var (ok, resp) = await SendAsync(HttpMethod.Delete, $"guilds/{guildId}/scheduled-events/{eventId}", null, ct);
            if (!ok)
                _logger.LogWarning("DeleteAsync failed for {EventId}: {Status} {Body}", eventId, resp?.Status, resp?.Body);
            return ok;
        }

        // -------- Exceptions --------

        /// <summary>
        /// Создаёт или ПОЛНОСТЬЮ ПЕРЕЗАПИСЫВАЕТ исключение с тем же original_scheduled_start_time.
        /// Для перенесённого вхождения передайте newStartUtc и isCanceled=false — Discord
        /// использует scheduled_start_time как новое время этого вхождения.
        /// </summary>
        public async Task<string?> AddExceptionAsync(
            ulong guildId,
            ulong eventId,
            DateTime originalStartUtc,
            bool isCanceled,
            DateTime? newStartUtc = null,
            CancellationToken ct = default,
            DateTime? newEndUtc = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["original_scheduled_start_time"] = ToIso(originalStartUtc),
                ["is_canceled"] = isCanceled
            };
            if (!isCanceled && newStartUtc.HasValue)
            {
                body["scheduled_start_time"] = ToIso(newStartUtc.Value);
                if (newEndUtc.HasValue) body["scheduled_end_time"] = ToIso(newEndUtc.Value);
            }
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var (ok, resp) = await SendAsync(HttpMethod.Post, $"guilds/{guildId}/scheduled-events/{eventId}/exceptions", json, ct);
            if (!ok || resp == null)
            {
                // 409 = exception уже существует — считаем успехом при синхронизации
                if (resp?.Status == HttpStatusCode.Conflict) return "";
                _logger.LogWarning("AddExceptionAsync failed for {EventId} at {Time}: {Status} {Body}",
                    eventId, originalStartUtc, resp?.Status, resp?.Body);
                return null;
            }
            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.TryGetProperty("event_exception_id", out var idEl))
                return idEl.GetString();
            return "";
        }

        public async Task<bool> RemoveExceptionAsync(
            ulong guildId,
            ulong eventId,
            string exceptionId,
            CancellationToken ct = default)
        {
            var (ok, resp) = await SendAsync(
                HttpMethod.Delete,
                $"guilds/{guildId}/scheduled-events/{eventId}/exceptions/{exceptionId}",
                null, ct);
            if (!ok && resp?.Status != HttpStatusCode.NotFound)
                _logger.LogWarning("RemoveExceptionAsync failed: {Status} {Body}", resp?.Status, resp?.Body);
            return ok;
        }

        public async Task<List<DiscordExceptionInfo>> GetExceptionsAsync(
            ulong guildId,
            ulong eventId,
            CancellationToken ct = default)
        {
            var (ok, resp) = await SendAsync(
                HttpMethod.Get,
                $"guilds/{guildId}/scheduled-events/{eventId}/exceptions",
                null, ct);
            if (!ok || resp == null) return new();
            return ParseExceptions(resp.Body);
        }

        /// <summary>
        /// Возвращает null при сетевой ошибке/неуспешном ответе, чтобы вызывающий
        /// код не принял «не удалось прочитать» за «исключений нет» и не создал дубли.
        /// </summary>
        public async Task<List<DiscordExceptionInfo>?> GetExceptionsFromEventAsync(
            ulong guildId,
            ulong eventId,
            CancellationToken ct = default)
        {
            var (ok, resp) = await SendAsync(
                HttpMethod.Get,
                $"guilds/{guildId}/scheduled-events/{eventId}",
                null, ct);
            if (!ok || resp == null)
            {
                _logger.LogWarning("GetExceptionsFromEventAsync failed for {EventId}: {Status} {Body}",
                    eventId, resp?.Status.ToString() ?? "(no response)", resp?.Body ?? "(null)");
                return null;
            }
            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("guild_scheduled_event_exceptions", out var arrEl))
                return new();
            return ParseExceptionsArray(arrEl);
        }

        /// <summary>event_exception_id — snowflake, описывающий исходное время вхождения.</summary>
        private static DateTime SnowflakeToUtc(string? value)
        {
            if (!ulong.TryParse(value, out var id)) return default;
            var milliseconds = (long)(id >> 22) + 1420070400000L; // Discord epoch
            return DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime, DateTimeKind.Utc);
        }

        private static List<DiscordExceptionInfo> ParseExceptions(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            return ParseExceptionsArray(doc.RootElement);
        }

        private static List<DiscordExceptionInfo> ParseExceptionsArray(JsonElement arrEl)
        {
            var result = new List<DiscordExceptionInfo>();
            if (arrEl.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in arrEl.EnumerateArray())
            {
                var info = new DiscordExceptionInfo
                {
                    ExceptionId = el.TryGetProperty("event_exception_id", out var idEl) ? idEl.GetString() ?? "" : "",
                    IsCanceled = el.TryGetProperty("is_canceled", out var cEl) && cEl.GetBoolean()
                };
                // Discord не возвращает original_scheduled_start_time в объекте исключения:
                // исходный слот зашит в event_exception_id (snowflake).
                if (el.TryGetProperty("original_scheduled_start_time", out var tEl)
                    && tEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(tEl.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dt))
                {
                    info.OriginalStartUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
                else if (el.TryGetProperty("event_exception_id", out var exceptionIdEl)
                         && exceptionIdEl.ValueKind == JsonValueKind.String)
                {
                    info.OriginalStartUtc = SnowflakeToUtc(exceptionIdEl.GetString());
                }
                // Время перенесённого вхождения (для is_canceled=false)
                if (el.TryGetProperty("scheduled_start_time", out var nEl)
                    && nEl.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(nEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var nd))
                        info.NewStartUtc = DateTime.SpecifyKind(nd, DateTimeKind.Utc);
                }
                result.Add(info);
            }
            return result;
        }

        // -------- Helpers --------

        private static string ToIso(DateTime dt) =>
            dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        private record HttpResult(bool Ok, HttpStatusCode Status, string? Body);

        private async Task<(bool Ok, HttpResult? Resp)> SendAsync(
    HttpMethod method, string path, string? jsonBody, CancellationToken ct)
        {
            const int maxRetries = 3;

            // POST создания события нельзя повторять после сетевой ошибки: первый
            // запрос мог дойти, и повтор создаст дубликат. POST исключений безопасен —
            // Discord отвечает 409 на уже существующее исключение.
            bool retryable = method != HttpMethod.Post
                             || path.EndsWith("/exceptions", StringComparison.OrdinalIgnoreCase);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var http = CreateClient();
                using var req = new HttpRequestMessage(method, path);
                if (jsonBody != null)
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                try
                {
                    using var resp = await http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if ((int)resp.StatusCode == 429 && attempt < maxRetries)
                    {
                        double retryAfterSec = 1.0;
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var sec))
                                retryAfterSec = Math.Min(sec, 30.0);
                        }
                        catch { }
                        _logger.LogWarning("Rate limited on {Method} {Path}, waiting {Sec:F2}s before retry #{Attempt}",
                            method, path, retryAfterSec, attempt + 1);
                        await Task.Delay(TimeSpan.FromSeconds(retryAfterSec + 0.2), ct);
                        continue;
                    }

                    // Кратковременные 5xx от Discord/прокси имеет смысл повторить.
                    if (retryable && attempt < maxRetries && (int)resp.StatusCode is 500 or 502 or 503 or 504)
                    {
                        _logger.LogWarning(
                            "Transient {Status} on {Method} {Path}, retry #{Attempt}",
                            (int)resp.StatusCode, method, path, attempt + 1);
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
                        continue;
                    }

                    var result = new HttpResult(resp.IsSuccessStatusCode, resp.StatusCode, body);
                    return (resp.IsSuccessStatusCode, result);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (retryable && attempt < maxRetries)
                    {
                        _logger.LogWarning(ex,
                            "HTTP request failed: {Method} {Path}, retry #{Attempt}",
                            method, path, attempt + 1);
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
                        continue;
                    }

                    _logger.LogError(ex, "HTTP request failed: {Method} {Path}", method, path);
                    return (false, null);
                }
            }

            _logger.LogError("HTTP request failed after retries: {Method} {Path}", method, path);
            return (false, null);
        }
        public async Task<DiscordEventRawInfo?> GetEventAsync(ulong guildId, ulong eventId, CancellationToken ct = default)
        {
            var (ok, resp) = await SendAsync(HttpMethod.Get, $"guilds/{guildId}/scheduled-events/{eventId}", null, ct);
            if (!ok || resp == null)
            {
                _logger.LogWarning("GetEventAsync failed for {EventId}: status={Status} body={Body}",
                    eventId,
                    resp?.Status.ToString() ?? "(no response)",
                    resp?.Body ?? "(null)");
                return null;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            return ParseEvent(doc.RootElement);
        }

        public async Task<string?> CreateSingleAsync(
    ulong guildId, ulong? channelId, string name, string? description,
    DateTime startUtc, DateTime endUtc, string? externalLocation,
    CancellationToken ct = default, string? imageDataUri = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["scheduled_start_time"] = ToIso(startUtc),
                ["scheduled_end_time"] = ToIso(endUtc),
                ["privacy_level"] = 2,
                ["image"] = imageDataUri,
                ["entity_type"] = channelId.HasValue ? 2 : 3,
            };
            if (channelId.HasValue) body["channel_id"] = channelId.Value.ToString();
            else body["entity_metadata"] = new Dictionary<string, object?> { ["location"] = externalLocation ?? "" };

            var json = JsonSerializer.Serialize(body, JsonOptsWithNulls);
            var (ok, resp) = await SendAsync(HttpMethod.Post, $"guilds/{guildId}/scheduled-events", json, ct);
            if (!ok || resp == null)
            {
                _logger.LogError("CreateSingleAsync failed: {Status} {Body}", resp?.Status, resp?.Body);
                return null;
            }
            using var doc = JsonDocument.Parse(resp.Body);
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        public async Task<bool> ModifySingleAsync(
            ulong guildId, ulong eventId, string name, string? description,
            DateTime startUtc, DateTime endUtc, ulong? channelId, string? externalLocation,
            CancellationToken ct = default, string? imageDataUri = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["scheduled_start_time"] = ToIso(startUtc),
                ["scheduled_end_time"] = ToIso(endUtc),
                ["image"] = imageDataUri
            };
            if (channelId.HasValue) body["channel_id"] = channelId.Value.ToString();
            else body["entity_metadata"] = new Dictionary<string, object?> { ["location"] = externalLocation ?? "" };

            var json = JsonSerializer.Serialize(body, JsonOptsWithNulls);
            var (ok, resp) = await SendAsync(HttpMethod.Patch, $"guilds/{guildId}/scheduled-events/{eventId}", json, ct);
            if (!ok)
                _logger.LogWarning("ModifySingleAsync failed for {EventId}: {Status} {Body}", eventId, resp?.Status, resp?.Body);
            return ok;
        }
        public async Task<Dictionary<ulong, DiscordEventRawInfo>> GetAllEventsRawAsync(
    ulong guildId, CancellationToken ct = default)
        {
            var result = new Dictionary<ulong, DiscordEventRawInfo>();
            var (ok, resp) = await SendAsync(HttpMethod.Get, $"guilds/{guildId}/scheduled-events", null, ct);
            if (!ok || resp == null)
            {
                _logger.LogWarning("GetAllEventsRawAsync failed: {Status} {Body}", resp?.Status, resp?.Body);
                return result;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idEl)) continue;
                if (!ulong.TryParse(idEl.GetString(), out var id)) continue;

                var info = ParseEvent(el);
                result[id] = info;
            }
            return result;
        }

        public static DiscordEventRawInfo ParseEvent(JsonElement el)
        {
            var info = new DiscordEventRawInfo { RawBody = el.GetRawText() };

            if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && ulong.TryParse(idEl.GetString(), out var id)) info.Id = id;

            if (el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                info.Name = nameEl.GetString() ?? "";

            if (el.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                info.Description = descEl.GetString();

            if (el.TryGetProperty("channel_id", out var chEl) && chEl.ValueKind == JsonValueKind.String
                && ulong.TryParse(chEl.GetString(), out var chId)) info.ChannelId = chId;

            if (el.TryGetProperty("scheduled_start_time", out var stEl) && stEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(stEl.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var st))
                info.StartUtc = DateTime.SpecifyKind(st, DateTimeKind.Utc);

            if (el.TryGetProperty("scheduled_end_time", out var etEl) && etEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(etEl.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var et))
                info.EndUtc = DateTime.SpecifyKind(et, DateTimeKind.Utc);

            if (el.TryGetProperty("status", out var sEl) && sEl.TryGetInt32(out var status))
                info.Status = status;

            if (el.TryGetProperty("recurrence_rule", out var rrEl) && rrEl.ValueKind == JsonValueKind.Object)
            {
                info.IsRecurring = true;
                info.RecurrenceRuleJson = rrEl.GetRawText();
            }

            if (el.TryGetProperty("guild_scheduled_event_exceptions", out var exEl))
                info.Exceptions = ParseExceptionsArray(exEl);

            return info;
        }
    }

    public class DiscordExceptionInfo
    {
        public string ExceptionId { get; set; } = "";
        public DateTime OriginalStartUtc { get; set; }
        public bool IsCanceled { get; set; }
        /// <summary>Новое начало для перенесённого вхождения (null для отмены).</summary>
        public DateTime? NewStartUtc { get; set; }
    }



    public class DiscordEventRawInfo
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public ulong? ChannelId { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
        public int Status { get; set; }
        public bool IsRecurring { get; set; }
        public string? RecurrenceRuleJson { get; set; }
        public List<DiscordExceptionInfo> Exceptions { get; set; } = new();
        public string RawBody { get; set; } = "";
    }
}