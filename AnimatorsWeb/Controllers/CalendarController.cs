using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Controllers
{
    [AllowAnonymous]
    public class CalendarController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DiscordClient _client;
        DiscordCalendarApplyService _applyService;
        private readonly DiscordCalendarSyncService _sync;
        private static DateTime _lastPullUtc = DateTime.MinValue;
        private static readonly object _pullLock = new object();


        public CalendarController(
            AppDbContext db,
            DiscordClient client,
            DiscordCalendarSyncService discordSync,
            DiscordCalendarApplyService applyService)
        {
            _db = db;
            _client = client;
            _sync = discordSync;
            _applyService = applyService;
        }

        public async Task<IActionResult> Index(int? year, int? month)
        {
            if (User.HasClaim("is_moderator", "true"))
            {
                bool needPull = false;
                lock (_pullLock)
                {
                    if ((DateTime.UtcNow - _lastPullUtc).TotalMinutes >= 1)
                    {
                        _lastPullUtc = DateTime.UtcNow;
                        needPull = true;
                    }
                }
                if (needPull)
                {
                    try { await _sync.PullAsync(); } catch { /* не роняем Index */ }
                }
            }

            // «Сегодня» по МСК
            var mskNow = DateTime.UtcNow.AddHours(3);
            int y = year ?? mskNow.Year;
            int m = month ?? mskNow.Month;

            if (m < 1) { m = 12; y--; }
            if (m > 12) { m = 1; y++; }

            var firstOfMonthUtc = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-3); // 00:00 МСК 1-го числа в UTC
            var nextMonthUtc = firstOfMonthUtc.AddMonths(1);

            // Сетка начинается с понедельника недели, в которой 1-е число
            int startShift = ((int)firstOfMonthUtc.AddHours(3).DayOfWeek + 6) % 7;  // Mon=0..Sun=6
            var gridStartMsk = firstOfMonthUtc.AddHours(3).Date.AddDays(-startShift);
            var gridStartUtc = DateTime.SpecifyKind(gridStartMsk.AddHours(-3), DateTimeKind.Utc);

            // 6 недель (42 ячейки), как в Google Calendar
            var gridEndUtc = gridStartUtc.AddDays(42);

            var baseEvents = await _db.CalendarEvents
                .Where(e => (e.StartUtc >= gridStartUtc && e.StartUtc < gridEndUtc)
                         || (e.IsRecurring && e.StartUtc < gridEndUtc))
                .OrderBy(e => e.StartUtc)
                .ToListAsync();

            var events = new List<CalendarEvent>();
            foreach (var ev in baseEvents)
            {
                if (!ev.IsRecurring || string.IsNullOrEmpty(ev.RecurrenceRuleJson))
                {
                    if (ev.StartUtc >= gridStartUtc && ev.StartUtc < gridEndUtc)
                        events.Add(ev);
                    continue;
                }

                var occurrences = GetOccurrences(ev, gridStartUtc, gridEndUtc);
                foreach (var start in occurrences)
                {
                    events.Add(new CalendarEvent
                    {
                        Id = ev.Id,
                        Title = ev.Title,
                        Description = ev.Description,
                        Location = ev.Location,
                        StartUtc = start,
                        EndUtc = start + (ev.EndUtc - ev.StartUtc),
                        IsRecurring = true,
                        EventPresetId = ev.EventPresetId,
                        PatternInstanceId = ev.PatternInstanceId,
                        DiscordEventId = ev.DiscordEventId,
                        Source = ev.Source,
                        SkipDiscordSync = ev.SkipDiscordSync,
                    });
                }
            }

            var model = new CalendarPageViewModel
            {
                Year = y,
                Month = m,
                FirstOfMonthUtc = firstOfMonthUtc,
                IsModerator = User.HasClaim("is_moderator", "true")
            };

            if (model.IsModerator)
            {
                model.Presets = await _db.EventPresets.OrderBy(p => p.Name).ToListAsync();
                model.Patterns = await _db.EventPatterns.OrderBy(p => p.Name).ToListAsync();

                var guild = _client.Guilds.Values.FirstOrDefault();
                model.VoiceChannels = guild?.Channels.Values
                    .Where(c => c.Type == DiscordChannelType.Voice)
                    .OrderBy(c => c.Name)
                    .Select(c => new GuildMemberOption { Id = c.Id, Name = c.Name })
                    .ToList() ?? new List<GuildMemberOption>();
            }

            // Группируем по дню (МСК) — по дню НАЧАЛА события
            var eventsByDayMsk = events
                .GroupBy(e => DateOnly.FromDateTime(e.StartUtc.AddHours(3)))
                .ToDictionary(g => g.Key, g => g.ToList());

            var todayMsk = DateOnly.FromDateTime(mskNow);

            for (int i = 0; i < 42; i++)
            {
                var dateMsk = DateOnly.FromDateTime(gridStartMsk.AddDays(i));
                var cell = new CalendarDayCell
                {
                    DateMsk = dateMsk,
                    IsCurrentMonth = dateMsk.Month == m && dateMsk.Year == y,
                    IsToday = dateMsk == todayMsk,
                    Events = eventsByDayMsk.GetValueOrDefault(dateMsk) ?? new List<CalendarEvent>()
                };
                model.Days.Add(cell);
            }

            model.AllGridEvents = events;

            return View(model);
        }

        // === CRUD пресетов (модалка на странице календаря) ===

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> SavePreset(
         int? id, string name, string defaultTitle,
         string? defaultDescription, int durationHours, string? location,
         bool? createInDiscord,
         string? defaultStartTimeUtc, string? color)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, error = "Название пресета обязательно" });

            if (durationHours <= 0 || durationHours > 720)
                return Json(new { success = false, error = "Длительность от 1 до 720 часов" });


            EventPreset preset;
            if (id.HasValue && id.Value > 0)
            {
                var existing = await _db.EventPresets.FindAsync(id.Value);
                if (existing == null) return Json(new { success = false, error = "Пресет не найден" });
                preset = existing;
            }
            else
            {
                preset = new EventPreset();
                _db.EventPresets.Add(preset);
            }

            var oldStartTimeUtc = preset.DefaultStartTimeUtc;
            TimeSpan? newStartTimeUtc = null;
            if (!string.IsNullOrWhiteSpace(defaultStartTimeUtc) && TimeSpan.TryParse(defaultStartTimeUtc, out var parsedUtc))
            {

                // МСК → UTC
                newStartTimeUtc = parsedUtc;
                if (newStartTimeUtc < TimeSpan.Zero) newStartTimeUtc = newStartTimeUtc.Value.Add(TimeSpan.FromHours(24));
            }

            preset.Name = name.Trim();
            preset.DefaultTitle = (defaultTitle ?? "").Trim();
            preset.DefaultDescription = string.IsNullOrWhiteSpace(defaultDescription) ? null : defaultDescription.Trim();
            preset.DurationHours = durationHours;
            preset.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
            if (createInDiscord.HasValue) preset.CreateInDiscord = createInDiscord.Value;
            preset.DefaultStartTimeUtc = newStartTimeUtc;
            preset.Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();

            await _db.SaveChangesAsync();

            // Синхронизируем связанные события + сдвиг времени
            var relatedEvents = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id)
                .ToListAsync();

            TimeSpan? delta = null;
            if (oldStartTimeUtc.HasValue && newStartTimeUtc.HasValue && oldStartTimeUtc != newStartTimeUtc)
                delta = newStartTimeUtc.Value - oldStartTimeUtc.Value;

            var now = DateTime.UtcNow;
            foreach (var ev in relatedEvents)
            {
                ev.Title = preset.DefaultTitle;
                ev.Description = preset.DefaultDescription;
                ev.Location = preset.Location;

                if (delta.HasValue && ev.StartUtc > now)
                {
                    ev.StartUtc = ev.StartUtc.Add(delta.Value);
                }
                ev.EndUtc = ev.StartUtc.AddHours(preset.DurationHours);
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true, id = preset.Id });
        }
        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> MoveEvent(int eventId, int daysDelta)
        {
            if (daysDelta == 0) return Json(new { success = true });

            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });

            if (ev.PatternInstanceId.HasValue)
            {
                var instanceId = ev.PatternInstanceId.Value;
                var instance = await _db.PatternInstances.FindAsync(instanceId);
                if (instance != null) instance.IsActive = false;
                var siblings = await _db.CalendarEvents
                    .Where(e => e.PatternInstanceId == instanceId)
                    .ToListAsync();
                foreach (var s in siblings) s.PatternInstanceId = null;
            }

            var oldDow = ((int)ev.StartUtc.DayOfWeek + 6) % 7;
            ev.StartUtc = DateTime.SpecifyKind(ev.StartUtc.AddDays(daysDelta), DateTimeKind.Utc);
            ev.EndUtc = DateTime.SpecifyKind(ev.EndUtc.AddDays(daysDelta), DateTimeKind.Utc);
            var newDow = ((int)ev.StartUtc.DayOfWeek + 6) % 7;

            if (ev.IsRecurring && !string.IsNullOrEmpty(ev.RecurrenceRuleJson) && oldDow != newDow)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(ev.RecurrenceRuleJson);
                    var root = doc.RootElement;
                    int frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2;
                    int interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1;

                    if (frequency == 2)
                    {
                        ev.RecurrenceRuleJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            frequency = 2,
                            interval = interval,
                            by_weekday = new[] { newDow }
                        });
                    }
                }
                catch { }
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeletePreset(int id)
        {
            var preset = await _db.EventPresets.FindAsync(id);
            if (preset == null)
                return Json(new { success = false, error = "Пресет не найден" });

            // Удаляем все связанные события на календаре
            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId == id)
                .ToListAsync();
            _db.CalendarEvents.RemoveRange(events);

            // Каскадно удалятся PatternEvent (FK CASCADE), но не CalendarEvent (FK SET NULL)
            _db.EventPresets.Remove(preset);

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> GetPresets()
        {
            var presets = await _db.EventPresets.OrderBy(p => p.Name).ToListAsync();
            return Json(presets.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                defaultTitle = p.DefaultTitle,
                defaultDescription = p.DefaultDescription,
                durationHours = p.DurationHours,
                location = p.Location,
                createInDiscord = p.CreateInDiscord,
                isRecurring = p.IsRecurring,
                recurrenceFrequency = p.RecurrenceFrequency,
                recurrenceInterval = p.RecurrenceInterval,
                recurrenceByWeekdays = p.RecurrenceByWeekdays,
                color = p.Color
            }));
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> CreateEvent(int presetId, string startUtc, string? dateMsk)
        {
            var preset = await _db.EventPresets.FindAsync(presetId);
            if (preset == null) return Json(new { success = false, error = "Пресет не найден" });

            if (!DateTime.TryParse(startUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                return Json(new { success = false, error = "Неверное время" });

            var startP = DateTime.SpecifyKind(start.ToUniversalTime(), DateTimeKind.Utc);
            var endP = startP.AddHours(preset.DurationHours);

            var ev = new CalendarEvent
            {
                EventPresetId = preset.Id,
                Title = preset.DefaultTitle,
                Description = preset.DefaultDescription,
                Location = preset.Location,
                StartUtc = startP,
                EndUtc = endP,
                Source = "Site",
                SkipDiscordSync = !preset.CreateInDiscord
            };

            _db.CalendarEvents.Add(ev);
            await _db.SaveChangesAsync();
            return Json(new { success = true, id = ev.Id });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> UpdateEvent(
    int id, string title, string? description, string? location,
    string startUtc, string endUtc,
    bool? isRecurring, int? recurrenceFrequency, string? recurrenceByWeekdays)
        {
            var ev = await _db.CalendarEvents.FindAsync(id);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });

            if (string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, error = "Заголовок обязателен" });

            if (!DateTime.TryParse(startUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                return Json(new { success = false, error = "Неверное начало" });

            if (!DateTime.TryParse(endUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                return Json(new { success = false, error = "Неверное окончание" });

            if (end <= start)
                return Json(new { success = false, error = "Окончание должно быть позже начала" });

            if (ev.PatternInstanceId.HasValue)
            {
                var instanceId = ev.PatternInstanceId.Value;
                var instance = await _db.PatternInstances.FindAsync(instanceId);
                if (instance != null) instance.IsActive = false;
                var siblings = await _db.CalendarEvents.Where(e => e.PatternInstanceId == instanceId).ToListAsync();
                foreach (var s in siblings) s.PatternInstanceId = null;
            }

            ev.Title = title.Trim();
            ev.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            ev.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
            ev.StartUtc = DateTime.SpecifyKind(start.ToUniversalTime(), DateTimeKind.Utc);
            ev.EndUtc = DateTime.SpecifyKind(end.ToUniversalTime(), DateTimeKind.Utc);

            if (isRecurring.HasValue)
            {
                if (isRecurring.Value && recurrenceFrequency.HasValue)
                {
                    int[]? byWeekday = null;
                    if (recurrenceFrequency == 2)
                    {
                        if (!string.IsNullOrWhiteSpace(recurrenceByWeekdays))
                        {
                            byWeekday = recurrenceByWeekdays
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(int.Parse).ToArray();
                        }
                        else
                        {
                            byWeekday = new[] { ((int)ev.StartUtc.DayOfWeek + 6) % 7 };
                        }
                    }
                    ev.IsRecurring = true;
                    ev.RecurrenceRuleJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        frequency = recurrenceFrequency.Value,
                        interval = 1,
                        by_weekday = byWeekday
                    });
                    if (string.IsNullOrEmpty(ev.RecurrenceExceptionsJson))
                        ev.RecurrenceExceptionsJson = "[]";
                }
                else
                {
                    ev.IsRecurring = false;
                    ev.RecurrenceRuleJson = null;
                    ev.RecurrenceExceptionsJson = null;
                }
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeleteEvent(int id)
        {
            var ev = await _db.CalendarEvents.FindAsync(id);
            if (ev == null)
                return Json(new { success = false, error = "Событие не найдено" });

            // Если из паттерна — расформировать
            if (ev.PatternInstanceId.HasValue)
            {
                var instanceId = ev.PatternInstanceId.Value;
                var instance = await _db.PatternInstances.FindAsync(instanceId);
                if (instance != null) instance.IsActive = false;

                var siblings = await _db.CalendarEvents
                    .Where(e => e.PatternInstanceId == instanceId && e.Id != ev.Id)
                    .ToListAsync();
                foreach (var s in siblings)
                    s.PatternInstanceId = null;
            }

            _db.CalendarEvents.Remove(ev);
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        // ===== ПАТТЕРНЫ =====

        [HttpGet]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> PatternEditor(int? id)
        {
            var guild = _client.Guilds.Values.FirstOrDefault();
            var voiceChannels = guild?.Channels.Values
                .Where(c => c.Type == DiscordChannelType.Voice)
                .OrderBy(c => c.Name)
                .Select(c => new GuildMemberOption { Id = c.Id, Name = c.Name })
                .ToList() ?? new List<GuildMemberOption>();

            var presets = await _db.EventPresets.OrderBy(p => p.Name).ToListAsync();

            if (!id.HasValue || id.Value == 0)
            {
                // Создание
                return View("PatternEditor", new PatternEditorPageViewModel
                {
                    IsNew = true,
                    Presets = presets,
                    VoiceChannels = voiceChannels,
                    Pattern = new EventPattern { Name = "", CycleWeeksCount = 1 }
                });
            }

            var pattern = await _db.EventPatterns
                .Include(p => p.Events)
                .FirstOrDefaultAsync(p => p.Id == id.Value);

            if (pattern == null) return NotFound();

            return View("PatternEditor", new PatternEditorPageViewModel
            {
                IsNew = false,
                Presets = presets,
                VoiceChannels = voiceChannels,
                Pattern = pattern
            });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> CreatePattern(string name, int cycleWeeksCount, bool autoRecurring = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, error = "Название обязательно" });

            if (cycleWeeksCount < 1 || cycleWeeksCount > 12)
                return Json(new { success = false, error = "Недель от 1 до 12" });

            var pattern = new EventPattern
            {
                Name = name.Trim(),
                CycleWeeksCount = cycleWeeksCount,
                AutoRecurring = autoRecurring
            };
            _db.EventPatterns.Add(pattern);
            await _db.SaveChangesAsync();

            return Json(new { success = true, id = pattern.Id });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> UpdatePatternMeta(int id, string name, int cycleWeeksCount, bool autoRecurring = true)
        {
            var pattern = await _db.EventPatterns.Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == id);
            if (pattern == null)
                return Json(new { success = false, error = "Паттерн не найден" });

            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, error = "Название обязательно" });

            if (cycleWeeksCount < 1 || cycleWeeksCount > 12)
                return Json(new { success = false, error = "Недель от 1 до 12" });

            if (cycleWeeksCount < pattern.CycleWeeksCount)
            {
                var toRemove = pattern.Events.Where(e => e.WeekOffset >= cycleWeeksCount).ToList();
                _db.PatternEvents.RemoveRange(toRemove);
            }

            pattern.Name = name.Trim();
            pattern.CycleWeeksCount = cycleWeeksCount;
            pattern.AutoRecurring = autoRecurring;

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeletePattern(int id)
        {
            var pattern = await _db.EventPatterns.FindAsync(id);
            if (pattern == null)
                return Json(new { success = false, error = "Паттерн не найден" });

            // Удалить все instance и их будущие события
            var instances = await _db.PatternInstances.Where(pi => pi.EventPatternId == id).ToListAsync();
            var instanceIds = instances.Select(i => i.Id).ToList();

            var events = await _db.CalendarEvents
                .Where(e => e.PatternInstanceId.HasValue && instanceIds.Contains(e.PatternInstanceId.Value))
                .ToListAsync();

            _db.CalendarEvents.RemoveRange(events);
            _db.PatternInstances.RemoveRange(instances);
            _db.EventPatterns.Remove(pattern);
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> AddPatternEvent(
            int patternId, int presetId, int weekOffset, int dayOfWeek, string startTimeUtc)
        {
            var pattern = await _db.EventPatterns.FindAsync(patternId);
            if (pattern == null) return Json(new { success = false, error = "Паттерн не найден" });

            var preset = await _db.EventPresets.FindAsync(presetId);
            if (preset == null) return Json(new { success = false, error = "Пресет не найден" });

            if (weekOffset < 0 || weekOffset >= pattern.CycleWeeksCount)
                return Json(new { success = false, error = "Неверная неделя" });

            if (dayOfWeek < 0 || dayOfWeek > 6)
                return Json(new { success = false, error = "Неверный день" });

            if (!TimeSpan.TryParse(startTimeUtc, out var timeUtc))
                timeUtc = new TimeSpan(17, 0, 0);

            var pe = new PatternEvent
            {
                EventPatternId = patternId,
                EventPresetId = presetId,
                WeekOffset = weekOffset,
                DayOfWeek = dayOfWeek,
                StartTimeUtc = timeUtc
            };
            _db.PatternEvents.Add(pe);
            await _db.SaveChangesAsync();

            return Json(new { success = true, id = pe.Id });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> UpdatePatternEventTime(int id, string startTimeUtc)
        {
            var pe = await _db.PatternEvents.FindAsync(id);
            if (pe == null) return Json(new { success = false, error = "Не найдено" });

            if (!TimeSpan.TryParse(startTimeUtc, out var timeUtc))
                return Json(new { success = false, error = "Неверное время" });

            pe.StartTimeUtc = timeUtc;
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeletePatternEvent(int id)
        {
            var pe = await _db.PatternEvents.FindAsync(id);
            if (pe == null) return Json(new { success = false, error = "Не найдено" });

            _db.PatternEvents.Remove(pe);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> PlacePattern(int patternId, string dateMsk)
        {
            var pattern = await _db.EventPatterns.Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == patternId);
            if (pattern == null)
                return Json(new { success = false, error = "Паттерн не найден" });

            if (pattern.Events.Count == 0)
                return Json(new { success = false, error = "Паттерн пуст — добавь в него события" });

            if (!DateOnly.TryParse(dateMsk, out var dropDate))
                return Json(new { success = false, error = "Неверная дата" });

            // Понедельник недели дропа (МСК)
            int dow = ((int)dropDate.DayOfWeek + 6) % 7;    // Mon=0..Sun=6
            var mondayMsk = dropDate.AddDays(-dow);
            var mondayUtc = DateTime.SpecifyKind(
                mondayMsk.ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Utc);

            var instance = new PatternInstance
            {
                EventPatternId = patternId,
                StartWeekUtc = mondayUtc,
                MaterializedUntilUtc = mondayUtc,
                IsActive = true
            };
            _db.PatternInstances.Add(instance);
            await _db.SaveChangesAsync();

            var materialization = HttpContext.RequestServices.GetRequiredService<PatternMaterializationService>();
            var created = await materialization.MaterializeAsync(instance.Id);

            return Json(new { success = true, instanceId = instance.Id, created });
        }
        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeleteSeriesEvents(int eventId)
        {
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            if (!ev.EventPresetId.HasValue) return Json(new { success = false, error = "У события нет пресета" });

            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId == ev.EventPresetId.Value)
                .ToListAsync();

            var count = events.Count;
            _db.CalendarEvents.RemoveRange(events);
            await _db.SaveChangesAsync();

            return Json(new { success = true, deleted = count });
        }
        private static List<DateTime> GetOccurrences(CalendarEvent ev, DateTime rangeStart, DateTime rangeEnd)
        {
            var result = new List<DateTime>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ev.RecurrenceRuleJson!);
                var root = doc.RootElement;
                int frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2;
                int interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1;
                int[]? byWeekday = null;
                if (root.TryGetProperty("by_weekday", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array)
                    byWeekday = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();

                var exceptions = new HashSet<DateTime>();
                if (!string.IsNullOrEmpty(ev.RecurrenceExceptionsJson))
                {
                    try
                    {
                        var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ev.RecurrenceExceptionsJson);
                        if (list != null)
                            foreach (var s in list)
                                if (DateTime.TryParse(s, null,
                                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                                    out var dt))
                                    exceptions.Add(dt);
                    }
                    catch { }
                }

                var start = ev.StartUtc;

                if (frequency == 2) // weekly
                {
                    var byWeekdayList = byWeekday ?? new[] { ((int)start.DayOfWeek + 6) % 7 };
                    var startMonday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                    var rangeMonday = rangeStart.Date.AddDays(-(((int)rangeStart.DayOfWeek + 6) % 7));

                    var iter = startMonday;
                    while (iter < rangeMonday)
                        iter = iter.AddDays(7 * interval);
                    if (iter > rangeMonday)
                        iter = iter.AddDays(-7 * interval);

                    while (iter < rangeEnd)
                    {
                        foreach (var wd in byWeekdayList)
                        {
                            var date = iter.AddDays(wd);
                            var occ = new DateTime(date.Year, date.Month, date.Day,
                                start.Hour, start.Minute, start.Second, DateTimeKind.Utc);
                            if (occ < start) continue;
                            if (occ < rangeStart || occ >= rangeEnd) continue;
                            if (exceptions.Contains(occ)) continue;
                            result.Add(occ);
                        }
                        iter = iter.AddDays(7 * interval);
                    }
                }
                else if (frequency == 3) // daily
                {
                    var iter = start;
                    while (iter < rangeStart) iter = iter.AddDays(interval);
                    while (iter < rangeEnd)
                    {
                        if (!exceptions.Contains(iter)) result.Add(iter);
                        iter = iter.AddDays(interval);
                    }
                }
                else
                {
                    if (start >= rangeStart && start < rangeEnd) result.Add(start);
                }
            }
            catch { }
            return result;
        }


        [HttpGet]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> ApplyToDiscordPreview()
        {
            var plan = await _applyService.PreviewAsync();

            return Json(new
            {
                toCreate = plan.ToCreate,
                toUpdate = plan.ToUpdate,
                toDelete = plan.ToDelete,
                items = plan.Items.Select(i => new
                {
                    action = i.Action,
                    title = i.Title,
                    startUtc = i.StartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                })
            });
        }

        [HttpPost]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> ApplyToDiscord()
        {
            var plan = await _applyService.ApplyAsync();
            return Json(new
            {
                success = true,
                toCreate = plan.ToCreate,
                toUpdate = plan.ToUpdate,
                toDelete = plan.ToDelete
            });
        }
    }
}