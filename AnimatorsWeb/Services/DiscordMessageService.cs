using AnimatorsWeb.Services;
using DSharpPlus;
using DSharpPlus.Entities;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public class DiscordMessageService
{
    private readonly DiscordClient _client;
    private readonly IConfiguration _config;
    public const ulong WinnerRoleId = 1273218724585013278;

    public DiscordMessageService(DiscordClient client, IConfiguration config)
    {
        _client = client;
        _config = config;
    }

    public async Task SendMessageAsync(ulong channelId, string message)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        await channel.SendMessageAsync(message);
    }
    public async Task ResetAndGrantRoleAsync(ulong guildId, ulong roleId, IEnumerable<ulong> winnerUserIds)
    {
        if (roleId == 0) return;

        var guild = await _client.GetGuildAsync(guildId);
        if (guild == null)
        {
            Console.WriteLine($"Гильдия {guildId} не найдена");
            return;
        }

        var role = await guild.GetRoleAsync(roleId);
        if (role == null)
        {
            Console.WriteLine($"Роль {roleId} не найдена");
            return;
        }

        // === Забираем роль у всех, у кого она есть ===
        var membersWithRole = guild.Members.Values
            .Where(m => m.Roles.Any(r => r.Id == roleId))
            .ToList();

        foreach (var member in membersWithRole)
        {
            try
            {
                await member.RevokeRoleAsync(role);
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось снять роль у {member.Username}: {ex.Message}");
            }
        }

        // === Выдаём новым победителям ===
        var uniqueIds = winnerUserIds.Where(id => id != 0).Distinct().ToList();

        foreach (var userId in uniqueIds)
        {
            try
            {
                var member = await guild.GetMemberAsync(userId);
                if (member == null) continue;
                if (member.Roles.Any(r => r.Id == roleId)) continue;

                await member.GrantRoleAsync(role);
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось выдать роль {userId}: {ex.Message}");
            }
        }

        Console.WriteLine($"Роль победителей обновлена: снята у {membersWithRole.Count}, выдана {uniqueIds.Count}");
    }
    public async Task<ulong> SendThemePollAsync(ulong channelId, int animJamNumber, List<string> themes)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var poll = new DiscordPollBuilder()
            .WithQuestion($"Голосование за тему аниджема #{animJamNumber}")
            .WithDuration(24);

        foreach (var theme in themes.Take(10))
            poll.AddOption(theme);

        var builder = new DiscordMessageBuilder().WithPoll(poll);
        var message = await builder.SendAsync(channel);

        return message.Id; // ← ID сообщения
    }
    public async Task<DiscordPoll?> GetPollResultAsync(ulong channelId, ulong messageId)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return null;

        var message = await channel.GetMessageAsync(messageId);
        return message?.Poll; // в 5.x poll-объект
    }
    public async Task<ulong> SendAnnouncementAsync(ulong channelId, string text, IEnumerable<ulong>? roleIds = null)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var builder = new DiscordMessageBuilder().WithContent(text);

        if (roleIds != null)
        {
            var mentions = roleIds
                .Where(id => id != 0)
                .Select(id => (IMention)new RoleMention(id))
                .ToArray();

            if (mentions.Length > 0)
                builder.WithAllowedMentions(mentions);
        }

        var message = await builder.SendAsync(channel);
        return message.Id;
    }

    public async Task<string?> GetWinningThemeViaRestAsync(ulong channelId, ulong messageId)
    {
        using var http = new HttpClient();
        var token = _config["Discord:BotToken"];
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        var url = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}";
        var json = await http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("poll", out var pollEl)) return null;
        if (!pollEl.TryGetProperty("results", out var resultsEl)) return null;
        if (!resultsEl.TryGetProperty("answer_counts", out var countsEl)) return null;

        // Сопоставляем answer_id с текстом варианта
        var answerTexts = new Dictionary<int, string>();
        foreach (var a in pollEl.GetProperty("answers").EnumerateArray())
        {
            int id = a.GetProperty("answer_id").GetInt32();
            string text = a.GetProperty("poll_media").GetProperty("text").GetString() ?? "";
            answerTexts[id] = text;
        }

        int bestId = -1;
        int bestCount = -1;
        bool tie = false;

        foreach (var c in countsEl.EnumerateArray())
        {
            int id = c.GetProperty("id").GetInt32();
            int count = c.GetProperty("count").GetInt32();

            if (count > bestCount)
            {
                bestCount = count;
                bestId = id;
                tie = false;
            }
            else if (count == bestCount)
            {
                tie = true;
            }
        }

        if (tie || bestId == -1) return null;
        return answerTexts.GetValueOrDefault(bestId);
    }
    public class ParticipantInfo
    {
        public ulong UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public ulong MessageId { get; set; }
        public ulong ChannelId { get; set; }
    }

    public List<(ulong Id, string Name)> GetAllGuildMembers()
    {
        var guild = _client.Guilds.Values.FirstOrDefault();
        if (guild == null) return new List<(ulong, string)>();

        return guild.Members.Values
            .Where(m => !m.IsBot)
            .Select(m => (m.Id, string.IsNullOrEmpty(m.DisplayName) ? m.Username : m.DisplayName))
            .OrderBy(x => x.Item2)
            .ToList();
    }
    public async Task<List<ParticipantInfo>> CollectParticipantsAsync(
    ulong channelId, DateTime startUtc)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return new List<ParticipantInfo>();

        var participants = new List<ParticipantInfo>();
        var seen = new HashSet<(ulong UserId, string WorkUrl)>();

        var cutoff = new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
        var guild = _client.Guilds.Values.FirstOrDefault();

        var messages = new List<DiscordMessage>();
        await foreach (var msg in channel.GetMessagesAsync(100))
        {
            messages.Add(msg);
        }

        var ordered = messages.OrderBy(m => m.Timestamp).ToList();

        // === Первый проход: авторы работ ===
        foreach (var msg in ordered)
        {
            if (msg.Timestamp < cutoff) continue;
            bool isOurBot = msg.Author.Id == _client.CurrentUser.Id;
            if (msg.Author.IsBot && !isOurBot) continue;

            string workUrl = ExtractWorkUrl(msg);
            if (string.IsNullOrEmpty(workUrl)) continue;

            // Дедуп: один и тот же человек + одна и та же работа = одна запись
            if (!seen.Add((msg.Author.Id, workUrl))) continue;

            string messageUrl = msg.JumpLink?.ToString() ?? "";
            string displayName = await ResolveDisplayNameAsync(guild, msg.Author.Id, msg.Author.Username);

            participants.Add(new ParticipantInfo
            {
                UserId = msg.Author.Id,
                DisplayName = displayName,
                WorkUrl = workUrl,
                MessageUrl = messageUrl,
                MessageId = msg.Id,
                ChannelId = channelId
            });
        }

        // === Второй проход: соавторы по упоминаниям ===
        foreach (var msg in ordered)
        {
            if (msg.Timestamp < cutoff) continue;
            bool isOurBot = msg.Author.Id == _client.CurrentUser.Id;
            if (msg.Author.IsBot && !isOurBot) continue;

            var mentionedIds = GetMentionedUserIds(msg.Content);
            if (mentionedIds.Count == 0) continue;

            string workUrl = ExtractWorkUrl(msg);
            if (string.IsNullOrEmpty(workUrl)) continue;

            string messageUrl = msg.JumpLink?.ToString() ?? "";

            foreach (var userId in mentionedIds)
            {
                if (userId == msg.Author.Id) continue;
                if (!seen.Add((userId, workUrl))) continue;

                DiscordUser? user = null;
                try { user = await _client.GetUserAsync(userId); } catch { }
                if (user == null || user.IsBot) continue;

                string displayName = await ResolveDisplayNameAsync(guild, userId, user.Username);

                participants.Add(new ParticipantInfo
                {
                    UserId = userId,
                    DisplayName = displayName,
                    WorkUrl = workUrl,
                    MessageUrl = messageUrl,
                    MessageId = msg.Id,
                    ChannelId = channelId
                });
            }
        }

        Console.WriteLine($"Собрано участников: {participants.Count}");
        return participants;
    }

    private static string ExtractWorkUrl(DiscordMessage msg)
    {
        if (msg.Attachments?.Any() == true)
            return msg.Attachments.First().Url?.ToString() ?? "";

        if (msg.Embeds?.Any() == true)
            return msg.Embeds.First().Url?.ToString() ?? "";

        if (msg.Content?.Contains("http") == true)
        {
            var words = msg.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.FirstOrDefault(w => w.StartsWith("http")) ?? "";
        }

        return "";
    }

    private static async Task<string> ResolveDisplayNameAsync(DiscordGuild? guild, ulong userId, string fallback)
    {
        if (guild == null) return fallback;
        try
        {
            var member = await guild.GetMemberAsync(userId);
            if (member != null && !string.IsNullOrEmpty(member.DisplayName))
                return member.DisplayName;
        }
        catch { }
        return fallback;
    }

    private static List<ulong> GetMentionedUserIds(string content)
    {
        var result = new List<ulong>();
        if (string.IsNullOrEmpty(content)) return result;

        foreach (Match m in Regex.Matches(content, @"<@!?(\d+)>"))
        {
            if (ulong.TryParse(m.Groups[1].Value, out var id))
                result.Add(id);
        }
        return result;
    }
    private string FormatMention(string displayName, List<ParticipantInfo> participants)
    {
        var info = participants.FirstOrDefault(p => p.DisplayName == displayName);
        if (info != null && info.UserId != 0)
            return $"<@{info.UserId}>"; // правильное упоминание
        return "@" + displayName; // fallback, если ID нет
    }
    public async Task<ulong> SendMessageWithMentionsAsync(ulong channelId, string content, IEnumerable<ulong> userIds)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var mentions = userIds
            .Where(id => id != 0)
            .Select(id => (IMention)new UserMention(id))
            .ToArray();

        var builder = new DiscordMessageBuilder()
            .WithContent(content);

        if (mentions.Length > 0)
            builder.WithAllowedMentions(mentions);

        var message = await builder.SendAsync(channel);
        return message.Id;
    }
    public async Task<ulong> SendCustomPollAsync(
    ulong channelId, string question, List<string> options, int durationHours)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var poll = new DiscordPollBuilder()
            .WithQuestion(question)
            .WithDuration(durationHours);

        foreach (var opt in options.Take(10))
            poll.AddOption(opt);

        var builder = new DiscordMessageBuilder().WithPoll(poll);
        var message = await builder.SendAsync(channel);

        return message.Id;
    }

    public class PollAnswerResult
    {
        public int AnswerId { get; set; }
        public int Count { get; set; }
        public string Text { get; set; } = "";
    }

    public class PollResults
    {
        public List<PollAnswerResult> Answers { get; set; } = new();
    }

    // Получает результаты опроса (answer_counts + тексты ответов)
    public async Task<PollResults?> GetPollResultsAsync(ulong channelId, ulong messageId)
    {
        using var http = new HttpClient();
        var token = _config["Discord:BotToken"];
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        var url = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}";
        var json = await http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("poll", out var pollEl)) return null;

        var result = new PollResults();

        // Тексты вариантов
        var texts = new Dictionary<int, string>();
        foreach (var a in pollEl.GetProperty("answers").EnumerateArray())
        {
            int id = a.GetProperty("answer_id").GetInt32();
            string text = a.GetProperty("poll_media").GetProperty("text").GetString() ?? "";
            texts[id] = text;
        }

        // Счётчики голосов (могут отсутствовать, если опрос ещё идёт)
        if (pollEl.TryGetProperty("results", out var resultsEl) &&
            resultsEl.TryGetProperty("answer_counts", out var countsEl))
        {
            foreach (var c in countsEl.EnumerateArray())
            {
                int id = c.GetProperty("id").GetInt32();
                int count = c.GetProperty("count").GetInt32();
                result.Answers.Add(new PollAnswerResult
                {
                    AnswerId = id,
                    Count = count,
                    Text = texts.GetValueOrDefault(id, "")
                });
            }
        }

        return result;
    }

    // Получает список user_id, проголосовавших за конкретный ответ
    public async Task<List<ulong>> GetVotersAsync(ulong channelId, ulong messageId, int answerId)
    {
        using var http = new HttpClient();
        var token = _config["Discord:BotToken"];
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        var url = $"https://discord.com/api/v10/channels/{channelId}/polls/{messageId}/answers/{answerId}";
        var json = await http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var voters = new List<ulong>();

        if (doc.RootElement.TryGetProperty("users", out var usersEl))
        {
            foreach (var u in usersEl.EnumerateArray())
            {
                if (u.TryGetProperty("id", out var idEl) &&
                    ulong.TryParse(idEl.GetString(), out ulong id))
                {
                    voters.Add(id);
                }
            }
        }

        return voters;
    }

    // Отправляет сообщение с текстом и списком медиа-ссылок (по одному на строку)
    public async Task SendWinnersAnnouncementAsync(ulong channelId, string text)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        await channel.SendMessageAsync(text);
    }
    public async Task<ulong?> FindUserIdByDisplayNameAsync(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var guild = _client.Guilds.Values.FirstOrDefault();
        if (guild == null) return null;

        var trimmed = displayName.Trim();

        // 1. Точное совпадение по DisplayName (серверный ник)
        var member = guild.Members.Values.FirstOrDefault(m =>
            string.Equals(m.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        // 2. Точное совпадение по Username (глобальный)
        member = guild.Members.Values.FirstOrDefault(m =>
            string.Equals(m.Username, trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        // 3. Частичное совпадение (начинается с ...), если точного нет
        member = guild.Members.Values.FirstOrDefault(m =>
            m.DisplayName.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        return null;
    }
    public ulong? FindUserIdByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var guild = _client.Guilds.Values.FirstOrDefault();
        if (guild == null) return null;

        var trimmed = displayName.Trim();

        // 1. Точное совпадение по DisplayName (серверный ник)
        var member = guild.Members.Values.FirstOrDefault(m =>
            string.Equals(m.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        // 2. Точное совпадение по Username (глобальный)
        member = guild.Members.Values.FirstOrDefault(m =>
            string.Equals(m.Username, trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        // 3. Частичное совпадение — ник начинается с введённого текста
        member = guild.Members.Values.FirstOrDefault(m =>
            m.DisplayName.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
        if (member != null) return member.Id;

        return null;
    }

    private static readonly string[] AttachmentExtensions =
    { ".mp4", ".gif", ".webm", ".mov", ".png", ".jpg", ".jpeg" };

    public static bool IsAttachmentUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        return AttachmentExtensions.Any(ext => lower.Contains(ext));
    }

    public static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrEmpty(name)) return "work.mp4";
            return name;
        }
        catch
        {
            return "work.mp4";
        }
    }

    // Скачивает файл в память. Возвращает null, если файл слишком большой или произошла ошибка.
    public async Task<Stream?> DownloadFileAsync(string url)
    {
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Discord-лимит для ботов — 10 МБ (без бустов), подстрахуемся
            if (response.Content.Headers.ContentLength > 10_000_000)
                return null;

            var memory = new MemoryStream();
            await response.Content.CopyToAsync(memory);
            memory.Position = 0;
            return memory;
        }
        catch
        {
            return null;
        }
    }

    // Отправляет сообщение с текстом, опциональным вложением и упоминаниями
    public async Task<ulong> SendMessageWithFileAndMentionsAsync(
        ulong channelId,
        string content,
        string? fileName,
        Stream? fileStream,
        IEnumerable<ulong> userIds)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var mentions = userIds
            .Where(id => id != 0)
            .Select(id => (IMention)new UserMention(id))
            .ToArray();

        var builder = new DiscordMessageBuilder().WithContent(content);

        if (fileStream != null && !string.IsNullOrEmpty(fileName))
            builder.AddFile(fileName, fileStream);

        if (mentions.Length > 0)
            builder.WithAllowedMentions(mentions);

        var message = await builder.SendAsync(channel);
        return message.Id;
    }
    // Возвращает множество уникальных user_id, поставивших реакции на сообщение
    public async Task<HashSet<ulong>> GetUniqueReactorsAsync(ulong channelId, ulong messageId)
    {
        var reactors = new HashSet<ulong>();

        using var http = new HttpClient();
        var token = _config["Discord:BotToken"];
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        // 1. Получаем список эмодзи из сообщения
        var messageUrl = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}";
        string messageJson;

        try
        {
            var response = await http.GetAsync(messageUrl);
            if (!response.IsSuccessStatusCode) return reactors;
            messageJson = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return reactors;
        }

        using var msgDoc = JsonDocument.Parse(messageJson);
        if (!msgDoc.RootElement.TryGetProperty("reactions", out var reactionsArr))
            return reactors;

        var emojis = new List<string>();
        foreach (var r in reactionsArr.EnumerateArray())
        {
            if (!r.TryGetProperty("emoji", out var emojiEl)) continue;

            string? emojiId = null;
            if (emojiEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                emojiId = idEl.GetString();

            string name = emojiEl.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? ""
                : "";

            if (!string.IsNullOrEmpty(emojiId))
                emojis.Add($"{name}:{emojiId}");
            else
                emojis.Add(Uri.EscapeDataString(name));
        }

        // 2. Для каждого эмодзи получаем список пользователей
        foreach (var emoji in emojis)
        {
            string? after = null;
            int safetyCounter = 0;

            while (true)
            {
                if (safetyCounter++ > 50) break; // защита

                var url = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}/reactions/{emoji}?limit=100";
                if (after != null) url += $"&after={after}";

                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(url);
                    if (!response.IsSuccessStatusCode) break;
                }
                catch { break; }

                var json = await response.Content.ReadAsStringAsync();
                using var usersDoc = JsonDocument.Parse(json);
                if (usersDoc.RootElement.ValueKind != JsonValueKind.Array) break;

                int count = 0;
                ulong lastId = 0;

                foreach (var u in usersDoc.RootElement.EnumerateArray())
                {
                    if (u.TryGetProperty("id", out var idEl) &&
                        ulong.TryParse(idEl.GetString(), out ulong uid))
                    {
                        bool isBot = u.TryGetProperty("bot", out var botEl) && botEl.GetBoolean();
                        if (!isBot)
                            reactors.Add(uid);

                        lastId = uid;
                        count++;
                    }
                }

                if (count < 100) break;

                string newAfter = lastId.ToString();
                if (after == newAfter) break; // защита от зацикливания
                after = newAfter;
            }
        }

        return reactors;
    }
    public async Task<string?> GetFreshAttachmentUrlAsync(ulong channelId, ulong messageId)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return null;

        var message = await channel.GetMessageAsync(messageId);
        if (message == null) return null;

        var attachment = message.Attachments?.FirstOrDefault();
        return attachment?.Url?.ToString();
    }


    public async Task<ulong> SendRoleMentionsAsync(ulong channelId, IEnumerable<ulong> roleIds, string message = "")
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var mentions = roleIds
            .Where(id => id != 0)
            .Select(id => (IMention)new RoleMention(id))
            .ToArray();

        var content = string.IsNullOrWhiteSpace(message)
            ? string.Join(" ", roleIds.Select(id => $"<@&{id}>"))
            : message;

        var builder = new DiscordMessageBuilder().WithContent(content);

        if (mentions.Length > 0)
            builder.WithAllowedMentions(mentions);

        var msg = await builder.SendAsync(channel);
        return msg.Id;
    }



    // Отправляет или редактирует сообщения с правилами.
    // Возвращает список ID всех сообщений в порядке отправки.
    public async Task<List<ulong>> PublishRulesAsync(
        ulong channelId, string content, List<ulong>? existingMessageIds = null)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var chunks = SplitIntoChunks(content, RulesConstants.MaxMessageLength);
        var resultIds = new List<ulong>();

        existingMessageIds ??= new List<ulong>();

        for (int i = 0; i < chunks.Count; i++)
        {
            ulong msgId;

            if (i < existingMessageIds.Count)
            {
                // Редактируем существующее
                var msg = await channel.GetMessageAsync(existingMessageIds[i]);
                if (msg != null)
                {
                    await msg.ModifyAsync(chunks[i]);
                    msgId = msg.Id;
                }
                else
                {
                    var newMsg = await channel.SendMessageAsync(chunks[i]);
                    msgId = newMsg.Id;
                }
            }
            else
            {
                // Создаём новое
                var newMsg = await channel.SendMessageAsync(chunks[i]);
                msgId = newMsg.Id;
            }

            resultIds.Add(msgId);
        }

        // Если старых сообщений было больше — удаляем лишние
        for (int i = chunks.Count; i < existingMessageIds.Count; i++)
        {
            try
            {
                var msg = await channel.GetMessageAsync(existingMessageIds[i]);
                if (msg != null) await msg.DeleteAsync();
            }
            catch { }
        }

        return resultIds;
    }

    public async Task<List<ulong>> PublishRulesChunksAsync(
    ulong channelId, List<string> chunks, List<ulong>? existingMessageIds = null)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null)
            throw new InvalidOperationException($"Канал {channelId} не найден.");

        var resultIds = new List<ulong>();
        existingMessageIds ??= new List<ulong>();

        for (int i = 0; i < chunks.Count; i++)
        {
            ulong msgId;
            if (i < existingMessageIds.Count)
            {
                var msg = await channel.GetMessageAsync(existingMessageIds[i]);
                if (msg != null)
                {
                    await msg.ModifyAsync(chunks[i]);
                    msgId = msg.Id;
                }
                else
                {
                    var newMsg = await channel.SendMessageAsync(chunks[i]);
                    msgId = newMsg.Id;
                }
            }
            else
            {
                var newMsg = await channel.SendMessageAsync(chunks[i]);
                msgId = newMsg.Id;
            }
            resultIds.Add(msgId);
        }

        for (int i = chunks.Count; i < existingMessageIds.Count; i++)
        {
            try
            {
                var msg = await channel.GetMessageAsync(existingMessageIds[i]);
                if (msg != null) await msg.DeleteAsync();
            }
            catch { }
        }

        return resultIds;
    }

    // Разбивает текст на куски по maxLen, не разрезая слова/строки
    private static List<string> SplitIntoChunks(string text, int maxLen)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            // Если строка сама больше maxLen — режем её жёстко
            if (line.Length > maxLen)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().TrimEnd());
                    current.Clear();
                }

                for (int i = 0; i < line.Length; i += maxLen)
                {
                    int len = Math.Min(maxLen, line.Length - i);
                    chunks.Add(line.Substring(i, len));
                }
                continue;
            }

            // Если добавление строки превысит лимит — закрываем текущий чанк
            if (current.Length + line.Length + 1 > maxLen)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().TrimEnd());

        return chunks;
    }

    // Ставит реакцию ✅ под сообщением
    public async Task AddAcceptReactionAsync(ulong channelId, ulong messageId)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId);
        if (msg == null) return;

        await msg.CreateReactionAsync(DiscordEmoji.FromUnicode(RulesConstants.AcceptEmoji));
    }

    // Получает список user_id, поставивших ✅
    public async Task<List<ulong>> GetAcceptReactorsAsync(ulong channelId, ulong messageId)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return new List<ulong>();

        var msg = await channel.GetMessageAsync(messageId);
        if (msg == null) return new List<ulong>();

        var result = new List<ulong>();

        await foreach (var user in msg.GetReactionsAsync(DiscordEmoji.FromUnicode(RulesConstants.AcceptEmoji)))
        {
            if (user.IsBot) continue;
            result.Add(user.Id);
        }

        return result;
    }

    // Удаляет реакцию конкретного пользователя
    public async Task RemoveAcceptReactionAsync(ulong channelId, ulong messageId, ulong userId)
    {
        var channel = await _client.GetChannelAsync(channelId);
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId);
        if (msg == null) return;

        var user = await _client.GetUserAsync(userId);
        if (user == null) return;

        try
        {
            await msg.DeleteReactionAsync(DiscordEmoji.FromUnicode(RulesConstants.AcceptEmoji), user);
        }
        catch { }
    }

    // Выдаёт роль пользователю
    public async Task GrantRoleAsync(ulong guildId, ulong userId, ulong roleId)
    {
        var guild = await _client.GetGuildAsync(guildId);
        if (guild == null) return;

        var member = await guild.GetMemberAsync(userId);
        if (member == null) return;

        // Проверяем, есть ли уже роль
        if (member.Roles.Any(r => r.Id == roleId)) return;

        var role = await guild.GetRoleAsync(roleId);
        if (role == null) return;

        await member.GrantRoleAsync(role);
    }
    public ulong GetFirstGuildId()
    {
        return _client.Guilds.Values.FirstOrDefault()?.Id ?? 0;
    }
    public async Task<HashSet<ulong>> FilterUsersWithRoleAsync(IEnumerable<ulong> userIds, ulong roleId)
    {
        var result = new HashSet<ulong>();
        var guild = _client.Guilds.Values.FirstOrDefault();
        if (guild == null || roleId == 0) return result;

        foreach (var userId in userIds.Distinct())
        {
            // 1. Пробуем кэш
            if (guild.Members.TryGetValue(userId, out var cached) && cached != null)
            {
                if (cached.Roles.Any(r => r.Id == roleId))
                    result.Add(userId);
                continue;
            }

            // 2. Fallback — REST
            try
            {
                var member = await guild.GetMemberAsync(userId);
                if (member != null && member.Roles.Any(r => r.Id == roleId))
                    result.Add(userId);
            }
            catch { }
        }

        return result;
    }

}