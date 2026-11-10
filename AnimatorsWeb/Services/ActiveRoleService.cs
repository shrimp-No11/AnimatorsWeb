using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ActiveRoleService
    {
        private readonly AppDbContext _db;
        private readonly DiscordClient _client;
        private readonly ILogger<ActiveRoleService> _logger;

        public ActiveRoleService(
            AppDbContext db,
            DiscordClient client,
            ILogger<ActiveRoleService> logger)
        {
            _db = db;
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Считает, кому нужно выдать/снять роль. Ничего не применяет.
        /// </summary>
        public async Task<ActiveRolePreviewResult> PreviewAsync()
        {
            var result = new ActiveRolePreviewResult();

            var settings = await _db.ActiveRoleSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                result.Error = "Настройки не сохранены. Заполните страницу и сохраните.";
                return result;
            }

            if (!settings.IsEnabled)
            {
                result.Error = "Функция выключена.";
                return result;
            }

            if (settings.RoleId == 0)
            {
                result.Error = "Не указан ID роли «Актив».";
                return result;
            }

            var guild = _client.Guilds.Values.FirstOrDefault();
            if (guild == null)
            {
                result.Error = "Гильдия не найдена.";
                return result;
            }

            // Проверим, что роль существует
            DiscordRole? role = null;
            try { role = await guild.GetRoleAsync(settings.RoleId); }
            catch { }
            if (role == null)
            {
                result.Error = $"Роль с ID {settings.RoleId} не найдена на сервере.";
                return result;
            }

            var cleanupCutoff = GetCleanupCutoff(settings);
            var hasOldActivities = await _db.UserActivities.AnyAsync(a => a.Date < cleanupCutoff);
            result.IsFirstRun = !hasOldActivities;

            // === Данные из БД ===
            var mskNow = DateTime.UtcNow.AddHours(3);

            var messageCutoff = DateOnly.FromDateTime(mskNow.AddDays(-settings.MessageWindowDays));
            var voiceCutoff = DateOnly.FromDateTime(mskNow.AddDays(-settings.VoiceWindowDays));
            var eventCutoffUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-settings.EventWindowDays), DateTimeKind.Utc);

            // Уникальные дни с сообщениями
            Dictionary<ulong, int> messageDays = new();
            if (settings.MessageEnabled)
            {
                messageDays = await _db.UserActivities
                    .Where(a => a.Type == ActivityType.Message && a.Date >= messageCutoff)
                    .GroupBy(a => a.DiscordUserId)
                    .Select(g => new
                    {
                        UserId = g.Key,
                        Count = g.Select(a => a.Date).Distinct().Count()
                    })
                    .ToDictionaryAsync(x => x.UserId, x => x.Count);
            }

            // Уникальные дни с войсом
            Dictionary<ulong, int> voiceDays = new();
            if (settings.VoiceEnabled)
            {
                voiceDays = await _db.UserActivities
                    .Where(a => a.Type == ActivityType.VoiceJoin && a.Date >= voiceCutoff)
                    .GroupBy(a => a.DiscordUserId)
                    .Select(g => new
                    {
                        UserId = g.Key,
                        Count = g.Select(a => a.Date).Distinct().Count()
                    })
                    .ToDictionaryAsync(x => x.UserId, x => x.Count);
            }

            // Участники ивентов за окно
            HashSet<ulong> eventParticipants = new();
            if (settings.EventEnabled)
            {
                var recentJams = await _db.AnimJams
                    .Where(j => j.StartDate >= eventCutoffUtc)
                    .Select(j => new { j.Number, j.EventType })
                    .ToListAsync();

                var recentKeys = recentJams
                    .Select(j => (j.Number, j.EventType))
                    .ToHashSet();

                if (recentKeys.Count > 0)
                {
                    var parts = await _db.Participants
                        .Select(p => new { p.DiscordUserId, p.AnimJamNumber, p.EventType })
                        .ToListAsync();

                    eventParticipants = parts
                        .Where(p => recentKeys.Contains((p.AnimJamNumber, p.EventType)))
                        .Select(p => p.DiscordUserId)
                        .ToHashSet();
                }
            }

            // === Обход участников гильдии ===
            var members = guild.Members.Values
                .Where(m => !m.IsBot)
                .ToList();

            result.TotalMembers = members.Count;

            foreach (var member in members)
            {
                var hasRole = member.Roles.Any(r => r.Id == settings.RoleId);

                var messageOk = settings.MessageEnabled
                    && messageDays.GetValueOrDefault(member.Id) >= settings.MessageDaysRequired;

                var voiceOk = settings.VoiceEnabled
                    && voiceDays.GetValueOrDefault(member.Id) >= settings.VoiceDaysRequired;

                var eventOk = settings.EventEnabled
                    && eventParticipants.Contains(member.Id);

                var shouldHaveRole = messageOk || voiceOk || eventOk;

                ActiveRoleAction action;
                if (shouldHaveRole && !hasRole) action = ActiveRoleAction.Grant;
                else if (!shouldHaveRole && hasRole)
                {
                    // Первый прогон: не снимаем
                    action = result.IsFirstRun ? ActiveRoleAction.NoChange : ActiveRoleAction.Revoke;
                }
                else action = ActiveRoleAction.NoChange;

                switch (action)
                {
                    case ActiveRoleAction.Grant: result.WillGrant++; break;
                    case ActiveRoleAction.Revoke: result.WillRevoke++; break;
                    default: result.NoChange++; break;
                }

                result.Items.Add(new ActiveRolePreviewItem
                {
                    DiscordUserId = member.Id,
                    DisplayName = member.DisplayName ?? member.Username ?? "",
                    Action = action,
                    HasRoleNow = hasRole,
                    ShouldHaveRole = shouldHaveRole,
                    MessageCondition = messageOk,
                    VoiceCondition = voiceOk,
                    EventCondition = eventOk
                });
            }

            result.Success = true;
            return result;
        }

        /// <summary>
        /// Применяет изменения к Discord и обновляет LastCheckUtc.
        /// Пересчитывает заново — что preview показал, то и применится (с точностью до свежих данных).
        /// </summary>
        public async Task<ActiveRolePreviewResult> ApplyAsync()
        {
            var result = await PreviewAsync();
            if (!result.Success) return result;

            var settings = await _db.ActiveRoleSettings.FirstOrDefaultAsync();
            if (settings == null) return result;

            var guild = _client.Guilds.Values.FirstOrDefault();
            if (guild == null) return result;

            DiscordRole? role = null;
            try { role = await guild.GetRoleAsync(settings.RoleId); }
            catch { }
            if (role == null)
            {
                result.Success = false;
                result.Error = "Роль не найдена на сервере.";
                return result;
            }

            int granted = 0, revoked = 0, errors = 0;

            foreach (var item in result.Items)
            {
                if (item.Action == ActiveRoleAction.NoChange) continue;

                try
                {
                    var member = await guild.GetMemberAsync(item.DiscordUserId);
                    if (member == null) continue;

                    if (item.Action == ActiveRoleAction.Grant)
                    {
                        if (!member.Roles.Any(r => r.Id == role.Id))
                        {
                            await member.GrantRoleAsync(role);
                            granted++;
                        }
                    }
                    else if (item.Action == ActiveRoleAction.Revoke)
                    {
                        if (member.Roles.Any(r => r.Id == role.Id))
                        {
                            await member.RevokeRoleAsync(role);
                            revoked++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "Active role apply failed for {UserId}", item.DiscordUserId);
                }

                await Task.Delay(500);
            }

            settings.LastCheckUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Active role apply complete: granted {Granted}, revoked {Revoked}, errors {Errors}",
                granted, revoked, errors);

            return result;
        }
        public static DateOnly GetCleanupCutoff(ActiveRoleSettings settings)
        {
            int maxWindow = Math.Max(
                settings.MessageEnabled ? settings.MessageWindowDays : 0,
                settings.VoiceEnabled ? settings.VoiceWindowDays : 0);

            int keepDays = maxWindow + 7;
            var mskNow = DateTime.UtcNow.AddHours(3);
            return DateOnly.FromDateTime(mskNow.AddDays(-keepDays));
        }
    }
}