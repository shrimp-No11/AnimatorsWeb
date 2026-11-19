using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Controllers
{
    public class CalendarController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DiscordClient _client;
        DiscordCalendarApplyService _applyService;
        private readonly DiscordCalendarSyncService _sync;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CalendarController> _logger;


        public CalendarController(
            AppDbContext db,
            DiscordClient client,
            DiscordCalendarSyncService discordSync,
            DiscordCalendarApplyService applyService,
            IWebHostEnvironment env,
            ILogger<CalendarController> logger)
        {
            _db = db;
            _client = client;
            _sync = discordSync;
            _applyService = applyService;
            _env = env;
            _logger = logger;
        }

        [AllowAnonymous]
        public async Task<IActionResult> Index(int? year, int? month)
        {
            // Pull вынесен в фоновую службу: страница не должна ждать responses Discord.
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
                .Where(e => !e.IsHidden
                         && ((e.StartUtc >= gridStartUtc && e.StartUtc < gridEndUtc)
                          || (e.IsRecurring && e.StartUtc < gridEndUtc)))
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

            // Пресеты нужны ВСЕМ посетителям: по ним цвета карточек на сетке.
            model.Presets = await _db.EventPresets
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.Name)
                .ToListAsync();

            if (model.IsModerator)
            {
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
            model.HasPendingApply = model.Presets.Any(p => p.PendingApply);

            return View(model);
        }

        /// <summary>
        /// Версия календаря для авто-обновления открытой страницы.
        /// Это in-memory счётчик: ни БД, ни Discord не опрашиваются.
        /// </summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult SyncVersion()
        {
            Response.Headers.CacheControl = "no-store";
            return Json(new { version = CalendarChangeNotifier.Version });
        }

        /// <summary>
        /// Любая успешная POST-правка календаря — сигнал другим открытым страницам.
        /// </summary>
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            base.OnActionExecuted(context);

            if (context.Exception == null
                && !string.Equals(context.HttpContext.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && context.Result is JsonResult { Value: not null } json)
            {
                var success = json.Value.GetType().GetProperty("success")?.GetValue(json.Value);
                if (success is true) CalendarChangeNotifier.Bump();
            }
        }
        
        // === CRUD пресетов (модалка на странице календаря) ===

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> SavePreset(
    int? id, string name, string defaultTitle,
    string? defaultDescription, int durationHours, string? location,
    bool? createInDiscord, bool? isDiscordSeries,
    string? defaultStartTimeUtc, string? color,
    string? coverImageUrl = null, IFormFile? coverImage = null,
    int? recurrenceInterval = null)
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

                // Нельзя снять галочку, если серия уже создана в Discord
                if (existing.DiscordSeriesEventId.HasValue && isDiscordSeries == false)
                    return Json(new { success = false, error = "Нельзя снять галочку — серия уже создана в Discord" });

                preset = existing;
            }
            else
            {
                preset = new EventPreset();
                _db.EventPresets.Add(preset);
            }

            TimeSpan? newStartTimeUtc = null;
            if (!string.IsNullOrWhiteSpace(defaultStartTimeUtc)
                && TimeSpan.TryParse(defaultStartTimeUtc, out var parsedUtc))
                newStartTimeUtc = parsedUtc;

            preset.Name = name.Trim();
            preset.DefaultTitle = (defaultTitle ?? "").Trim();
            preset.DefaultDescription = string.IsNullOrWhiteSpace(defaultDescription) ? null : defaultDescription.Trim();
            preset.DurationHours = durationHours;
            preset.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
            preset.DefaultStartTimeUtc = newStartTimeUtc;
            preset.Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
            preset.CoverImageUrl = string.IsNullOrWhiteSpace(coverImageUrl) ? null : coverImageUrl.Trim();

            if (coverImage != null && coverImage.Length > 0)
            {
                if (coverImage.Length > 5 * 1024 * 1024)
                    return Json(new { success = false, error = "Обложка больше 5 МБ" });
                var ext = Path.GetExtension(coverImage.FileName).ToLowerInvariant();
                if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }.Contains(ext))
                    return Json(new { success = false, error = "Нужен JPG, PNG, WebP или GIF" });

                var folder = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", "covers");
                Directory.CreateDirectory(folder);
                var fileName = $"{Guid.NewGuid():N}{ext}";
                var finalPath = Path.Combine(folder, fileName);
                if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath);
                using (var stream = new FileStream(finalPath, FileMode.Create))
                    await coverImage.CopyToAsync(stream);
                preset.CoverImageFileName = fileName;
            }

            if (isDiscordSeries.HasValue) preset.IsDiscordSeries = isDiscordSeries.Value;

            if (recurrenceInterval is 1 or 2)
                preset.RecurrenceInterval = recurrenceInterval.Value;

            if (createInDiscord.HasValue)
                preset.CreateInDiscord = createInDiscord.Value;

            // У серии событие в Discord создаётся всегда.
            if (preset.IsDiscordSeries)
            {
                preset.CreateInDiscord = true;
                preset.PendingApply = true;
            }

            await _db.SaveChangesAsync();

            // Пресет — это шаблон: его изменения должны сразу отображаться на всех
            // будущих карточках, включая карточки серий.
            var relatedEvents = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id && !e.IsHidden)
                .ToListAsync();
            var nowUtc = DateTime.UtcNow;
            foreach (var ev in relatedEvents)
            {
                ev.Title = preset.DefaultTitle;
                ev.Description = preset.DefaultDescription;
                ev.Location = preset.Location;

                if (ev.StartUtc > nowUtc && newStartTimeUtc.HasValue)
                    ev.StartUtc = DateTime.SpecifyKind(ev.StartUtc.Date.Add(newStartTimeUtc.Value), DateTimeKind.Utc);

                ev.EndUtc = ev.StartUtc.AddHours(preset.DurationHours);
            }
            await _db.SaveChangesAsync();

            await SyncPresetStateFromCardsAsync(preset);
            await _db.SaveChangesAsync();

            return Json(new { success = true, id = preset.Id });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> MoveEvent(int eventId, int daysDelta, string? originalStartUtc = null)
        {
            if (daysDelta == 0) return Json(new { success = true });

            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });

            // Карточка повторяющегося события в сетке — это вхождение мастер-строки с тем же Id.
            // Переносим не мастер (иначе сдвинется вся серия), а пишем moved exception.
            // Само перенесённое вхождение GetOccurrences нарисует из этого исключения.
            var baseStart = ev.StartUtc;
            if (!string.IsNullOrWhiteSpace(originalStartUtc)
                && DateTime.TryParse(originalStartUtc, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
                baseStart = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

            CalendarTrace.Write("UI", $"Move id={eventId} base={baseStart:O} delta={daysDelta} " +
                $"preset={ev.EventPresetId} recurring={ev.IsRecurring} storedOrig={ev.OriginalStartUtc:O}");

            if (ev.IsRecurring && baseStart != ev.StartUtc)
            {
                var exceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                EventExceptionJson.Move(exceptions, baseStart, baseStart.AddDays(daysDelta));
                ev.RecurrenceExceptionsJson = EventExceptionJson.Serialize(exceptions);

                await _db.SaveChangesAsync();
                return Json(new { success = true, moved = true });
            }

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

            ev.OriginalStartUtc ??= baseStart;
            ev.StartUtc = DateTime.SpecifyKind(baseStart.AddDays(daysDelta), DateTimeKind.Utc);
            ev.EndUtc = DateTime.SpecifyKind(ev.EndUtc.AddDays(daysDelta), DateTimeKind.Utc);
            await _db.SaveChangesAsync();

            await RefreshSeriesStateForCardAsync(ev);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        /// <summary>После правки карточки пересобирает состояние её серии.</summary>
        private async Task RefreshSeriesStateForCardAsync(CalendarEvent ev)
        {
            if (!ev.EventPresetId.HasValue) return;
            var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
            if (preset?.IsDiscordSeries != true) return;
            await SyncPresetStateFromCardsAsync(preset);
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeletePreset(int id)
        {
            var preset = await _db.EventPresets.FindAsync(id);
            if (preset == null)
                return Json(new { success = false, error = "Пресет не найден" });

            // Удаление пресета = удаление события/серии из Discord. Карточки снимаем с сетки,
            // но пока не удаляем из БД: Apply должен увидеть их DiscordEventId и выполнить DELETE.
            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId == id)
                .ToListAsync();
            foreach (var ev in events) ev.IsHidden = true;
            preset.IsDeleted = true;
            await _db.SaveChangesAsync();

            // Удаляем сразу, чтобы результат был виден без Apply.
            var idsToDelete = events
                .Where(e => e.DiscordEventId.HasValue)
                .Select(e => e.DiscordEventId!.Value)
                .ToList();
            if (preset.DiscordSeriesEventId.HasValue)
                idsToDelete.Add(preset.DiscordSeriesEventId.Value);

            foreach (var discordId in idsToDelete.Distinct())
                await _sync.DeleteInDiscordAsync(discordId);

            // Apply больше не должен пытаться удалить уже удалённую серию.
            preset.DiscordSeriesEventId = null;
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> MakeEventSeries(int eventId)
        {
            CalendarTrace.Write("UI", $"Extend requested eventId={eventId}");
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            if (!ev.EventPresetId.HasValue) return Json(new { success = false, error = "У события нет пресета" });

            var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
            if (preset == null) return Json(new { success = false, error = "Пресет не найден" });
            if (preset.IsDeleted) return Json(new { success = false, error = "Пресет удалён" });

            // «Продлить» работает с любым пресетом: если серией он ещё не был — становится ей.
            if (!preset.IsDiscordSeries)
            {
                preset.IsDiscordSeries = true;
                preset.CreateInDiscord = true;
            }

            var all = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id)
                .ToListAsync();

            // Скрытые «надгробия» (следы прошлых удалений) занимали недельные слоты,
            // из-за чего кнопка создавала 0 карточек. Убираем их.
            var stale = all
                .Where(e => e.IsHidden
                            && !e.DiscordEventId.HasValue
                            && !e.PatternInstanceId.HasValue)
                .ToList();
            if (stale.Count > 0) _db.CalendarEvents.RemoveRange(stale);

            var kept = all.Except(stale).ToList();
            var duration = ev.EndUtc - ev.StartUtc;
            int created = 0, revived = 0;

            // Слоты считает чистый планировщик (см. SeriesExtensionPlanner):
            // правило берётся из настроек серии, а не из кликнутой карточки.
            var slots = SeriesExtensionPlanner.NextSlots(
                preset,
                kept.Select(e => (Utc(e.StartUtc), e.OriginalStartUtc, e.IsHidden)).ToList());

            var hiddenBySlot = kept
                .Where(e => e.IsHidden && !e.PatternInstanceId.HasValue)
                .GroupBy(e => Utc(e.StartUtc))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var start in slots)
            {
                if (hiddenBySlot.TryGetValue(start, out var hidden))
                {
                    hidden.IsHidden = false;
                    hidden.OriginalStartUtc = null;
                    hidden.Title = preset.DefaultTitle;
                    hidden.Description = preset.DefaultDescription;
                    hidden.Location = preset.Location;
                    hidden.EndUtc = DateTime.SpecifyKind(start.Add(duration), DateTimeKind.Utc);
                    // Карточка серии не должна помнить одиночное событие Discord —
                    // серией управляет пресет.
                    hidden.DiscordEventId = null;
                    hidden.DiscordSnapshot = null;
                    revived++;
                    continue;
                }

                _db.CalendarEvents.Add(new CalendarEvent
                {
                    EventPresetId = preset.Id,
                    PatternInstanceId = null,
                    Title = preset.DefaultTitle,
                    Description = preset.DefaultDescription,
                    Location = preset.Location,
                    StartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc),
                    EndUtc = DateTime.SpecifyKind(start.Add(duration), DateTimeKind.Utc),
                    Source = "Site",
                    SkipDiscordSync = false
                });
                created++;
            }

            await _db.SaveChangesAsync();

            // Хранимые исключения должны совпадать с сеткой ещё до Apply,
            // иначе Pull/продление увидели бы старое состояние.
            await SyncPresetStateFromCardsAsync(preset);
            await _db.SaveChangesAsync();

            CalendarTrace.Write("UI",
                $"Extend done eventId={eventId} created={created} revived={revived}");
            return Json(new { success = true, created = created + revived, revived });
        }

        /// <summary>
        /// Пересобирает сохранённые правило/исключения серии из текущей сетки карточек.
        /// Вызывается после локальных правок, чтобы состояние сайта было внутренне согласовано.
        /// </summary>
        private async Task SyncPresetStateFromCardsAsync(EventPreset preset)
        {
            if (!preset.IsDiscordSeries) return;

            preset.PendingApply = true;

            var cards = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id && !e.IsHidden)
                .OrderBy(e => e.StartUtc)
                .Select(e => new { e.StartUtc, e.OriginalStartUtc })
                .ToListAsync();

            if (cards.Count == 0)
            {
                // На сетке не осталось ни одной карточки — значит вся серия отменена.
                // Иначе Pull/Apply вернули бы в Discord все вхождения, которые модератор удалил.
                var rule = DiscordRuleJson.Parse(preset.RecurrenceRuleJson);
                if (rule == null)
                {
                    preset.RecurrenceExceptionsJson = null;
                    return;
                }

                var from = DateTime.UtcNow.AddDays(-1);
                var until = DateTime.UtcNow.AddDays(SeriesOccurrenceExtender.HorizonWeeks * 7);
                var cancels = SeriesOccurrenceCalculator
                    .Calculate(rule, from, until, Array.Empty<PresetExceptionRecord>(), Array.Empty<DateTime>())
                    .Select(start => new PresetExceptionRecord
                    {
                        OriginalStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc),
                        IsCanceled = true,
                        NewStartUtc = null
                    })
                    .ToList();

                preset.RecurrenceExceptionsJson = cancels.Count == 0
                    ? null
                    : System.Text.Json.JsonSerializer.Serialize(cancels);
                return;
            }

            var horizon = Utc(cards.Max(c => c.StartUtc)).AddDays(7);
            var rolling = DateTime.UtcNow.AddDays(SeriesOccurrenceExtender.HorizonWeeks * 7);
            if (rolling > horizon) horizon = rolling;

            var calc = SeriesRulePlanner.Build(
                preset,
                cards.Select(c => (c.StartUtc, c.OriginalStartUtc)).ToList(),
                horizon);

            if (calc == null) return;

            var exceptions = calc.Exceptions
                .Select(e => new PresetExceptionRecord
                {
                    OriginalStartUtc = e.OriginalStartUtc,
                    IsCanceled = e.IsCanceled,
                    NewStartUtc = e.NewStartUtc
                })
                .ToList();

            preset.RecurrenceExceptionsJson = exceptions.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(exceptions);
        }

        private static DateTime Utc(DateTime value)
            => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> RemoveEventFromSeries(int eventId)
        {
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            if (!ev.EventPresetId.HasValue) return Json(new { success = false, error = "У события нет пресета" });

            var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
            if (preset == null || !preset.IsDiscordSeries)
                return Json(new { success = false, error = "Пресет не является Discord-серией" });

            ev.IsHidden = true;
            await _db.SaveChangesAsync();
            await SyncPresetStateFromCardsAsync(preset);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeleteAllSeriesEvents(int presetId)
        {
            var preset = await _db.EventPresets.FindAsync(presetId);
            if (preset == null) return Json(new { success = false, error = "Пресет не найден" });
            if (!preset.IsDiscordSeries) return Json(new { success = false, error = "Пресет не является Discord-серией" });

            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId == presetId)
                .ToListAsync();

            var list = new List<PresetExceptionRecord>();
            if (!string.IsNullOrEmpty(preset.RecurrenceExceptionsJson))
            {
                try
                {
                    list = System.Text.Json.JsonSerializer
                        .Deserialize<List<PresetExceptionRecord>>(preset.RecurrenceExceptionsJson) ?? new();
                }
                catch { }
            }
            foreach (var ev in events)
            {
                ev.IsHidden = true;
                CancelInList(list, ev.OriginalStartUtc ?? ev.StartUtc);
            }
            preset.RecurrenceExceptionsJson = System.Text.Json.JsonSerializer.Serialize(list);
            preset.PendingApply = true;
            await _db.SaveChangesAsync();

            return Json(new { success = true, deleted = events.Count });
        }

        [HttpGet]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> GetPresets()
        {
            var presets = await _db.EventPresets
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.Name)
                .ToListAsync();

            return Json(presets.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                defaultTitle = p.DefaultTitle,
                defaultDescription = p.DefaultDescription,
                durationHours = p.DurationHours,
                location = p.Location,
                createInDiscord = p.CreateInDiscord,
                isDiscordSeries = p.IsDiscordSeries,
                discordSeriesEventId = p.DiscordSeriesEventId?.ToString(),
                color = p.Color
            }));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
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

            // Перетаскивание пресета всегда создаёт РОВНО ОДНУ карточку.
            // Повтор включается отдельной кнопкой на карточке.
            _db.CalendarEvents.Add(ev);
            await _db.SaveChangesAsync();

            await RefreshSeriesStateForCardAsync(ev);
            await _db.SaveChangesAsync();
            return Json(new { success = true, id = ev.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> UpdateEvent(
    int id, string title, string? description, string? location,
    string startUtc, string endUtc,
    bool? isRecurring, int? recurrenceFrequency, string? recurrenceByWeekdays,
    string? originalStartUtc = null)
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

            var startP = DateTime.SpecifyKind(start.ToUniversalTime(), DateTimeKind.Utc);
            var endP = DateTime.SpecifyKind(end.ToUniversalTime(), DateTimeKind.Utc);
            CalendarTrace.Write("UI", $"Update id={id} old={ev.StartUtc:O} new={startP:O} " +
                $"preset={ev.EventPresetId} recurring={ev.IsRecurring} originalArg={originalStartUtc}");

            // Правка конкретного вхождения recurring-серии: не трогаем мастер,
            // а пишем moved exception. Вхождение отрисуется из него.
            var occurrenceStart = ev.StartUtc;
            if (!string.IsNullOrWhiteSpace(originalStartUtc)
                && DateTime.TryParse(originalStartUtc, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsedOccurrence))
                occurrenceStart = DateTime.SpecifyKind(parsedOccurrence, DateTimeKind.Utc);

            if (ev.IsRecurring && occurrenceStart != ev.StartUtc)
            {
                var exceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                EventExceptionJson.Move(exceptions, occurrenceStart, startP);
                ev.RecurrenceExceptionsJson = EventExceptionJson.Serialize(exceptions);

                await _db.SaveChangesAsync();
                return Json(new { success = true, moved = true });
            }

            // У Discord-серии название/описание/локация задаются пресетами (одна серия =
            // одно событие Discord), поэтому правка отдельного вхождения просто молча
            // потерялась бы при Apply. Явно запрещаем её и просим редактировать серию.
            if (ev.EventPresetId.HasValue)
            {
                var linkedPreset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
                if (linkedPreset?.IsDiscordSeries == true)
                {
                    var newTitle = title.Trim();
                    var newDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
                    var newLocation = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
                    if (ev.Title != newTitle || ev.Description != newDescription || ev.Location != newLocation)
                        return Json(new
                        {
                            success = false,
                            error = "У вхождения серии меняются только дата и время. Всё остальное — в «Настроить серию»."
                        });
                }
            }

            if (ev.PatternInstanceId.HasValue)
            {
                var instanceId = ev.PatternInstanceId.Value;
                var instance = await _db.PatternInstances.FindAsync(instanceId);
                if (instance != null) instance.IsActive = false;
                var siblings = await _db.CalendarEvents.Where(e => e.PatternInstanceId == instanceId).ToListAsync();
                foreach (var s in siblings) s.PatternInstanceId = null;
            }

            var previousStart = ev.StartUtc;
            ev.Title = title.Trim();
            ev.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            ev.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
            if (previousStart != startP)
                ev.OriginalStartUtc ??= previousStart;
            ev.StartUtc = startP;
            ev.EndUtc = endP;

            // Если это отцепленное вхождение — обновляем время в moved exception мастера.
            if (ev.OriginalStartUtc.HasValue)
            {
                var master = await _db.CalendarEvents.FirstOrDefaultAsync(e =>
                    e.EventPresetId == ev.EventPresetId && e.IsRecurring && e.Id != ev.Id);
                if (master != null)
                {
                    var exceptions = EventExceptionJson.Parse(master.RecurrenceExceptionsJson);
                    EventExceptionJson.Move(exceptions, ev.OriginalStartUtc.Value, startP);
                    master.RecurrenceExceptionsJson = EventExceptionJson.Serialize(exceptions);
                }
            }

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
            await RefreshSeriesStateForCardAsync(ev);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeleteEvent(int id, string? originalStartUtc = null)
        {
            var ev = await _db.CalendarEvents.FindAsync(id);
            if (ev == null)
                return Json(new { success = false, error = "Событие не найдено" });
            CalendarTrace.Write("UI", $"Delete id={id} start={ev.StartUtc:O} preset={ev.EventPresetId} " +
                $"recurring={ev.IsRecurring} arg={originalStartUtc}");

            // Удаление карточки НЕ удаляет событие из Discord — пока жив пресет, событие живёт.
            // Для повторяющегося события это отмена конкретного вхождения (exception).
            if (ev.IsRecurring)
            {
                var date = ev.StartUtc;
                if (!string.IsNullOrWhiteSpace(originalStartUtc)
                    && DateTime.TryParse(originalStartUtc, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsed))
                    date = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

                var list = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                EventExceptionJson.Cancel(list, date);
                ev.RecurrenceExceptionsJson = EventExceptionJson.Serialize(list);
                await _db.SaveChangesAsync();
                return Json(new { success = true, cancelled = true });
            }

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

            ev.IsHidden = true;
            if (ev.EventPresetId.HasValue)
            {
                var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
                if (preset?.IsDiscordSeries == true)
                {
                    await _db.SaveChangesAsync();
                    await SyncPresetStateFromCardsAsync(preset);
                }
            }
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

            var presets = await _db.EventPresets
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.Name)
                .ToListAsync();

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
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> RemoveEventOccurrence(int eventId, string originalStartUtc)
        {
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            if (!ev.IsRecurring) return Json(new { success = false, error = "Событие не повторяется" });

            if (!DateTime.TryParse(originalStartUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var occ))
                return Json(new { success = false, error = "Неверная дата" });
            occ = DateTime.SpecifyKind(occ, DateTimeKind.Utc);

            var list = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
            EventExceptionJson.Cancel(list, occ);
            var json = EventExceptionJson.Serialize(list);
            if (ev.RecurrenceExceptionsJson != json)
            {
                ev.RecurrenceExceptionsJson = json;
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true });
        }

       

        [HttpPost]
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
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
            // 00:00 МСК = 21:00 UTC предыдущего дня (в Index сетка считается так же).
            var mondayUtc = DateTime.SpecifyKind(
                mondayMsk.ToDateTime(TimeOnly.MinValue).AddHours(-3),
                DateTimeKind.Utc);

            var existingInstance = await _db.PatternInstances
                .FirstOrDefaultAsync(i => i.IsActive
                    && i.EventPatternId == patternId
                    && i.StartWeekUtc == mondayUtc);

            if (existingInstance != null)
                return Json(new { success = false, error = "Этот паттерн уже размещён на этой неделе" });

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
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> DeleteSeriesEvents(int eventId)
        {
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            if (!ev.EventPresetId.HasValue) return Json(new { success = false, error = "У события нет пресета" });

            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId == ev.EventPresetId.Value)
                .ToListAsync();

            var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
            var list = new List<PresetExceptionRecord>();
            if (preset != null && !string.IsNullOrEmpty(preset.RecurrenceExceptionsJson))
            {
                try
                {
                    list = System.Text.Json.JsonSerializer
                        .Deserialize<List<PresetExceptionRecord>>(preset.RecurrenceExceptionsJson) ?? new();
                }
                catch { }
            }
            foreach (var item in events)
            {
                item.IsHidden = true;
                if (preset != null) CancelInList(list, item.OriginalStartUtc ?? item.StartUtc);
            }
            if (preset != null)
            {
                preset.RecurrenceExceptionsJson = System.Text.Json.JsonSerializer.Serialize(list);
                preset.PendingApply = true;
            }
            await _db.SaveChangesAsync();

            return Json(new { success = true, deleted = events.Count });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> CancelAllOccurrences(int eventId)
        {
            var ev = await _db.CalendarEvents.FindAsync(eventId);
            if (ev == null) return Json(new { success = false, error = "Событие не найдено" });
            var now = DateTime.UtcNow;

            if (ev.EventPresetId.HasValue)
            {
                var preset = await _db.EventPresets.FindAsync(ev.EventPresetId.Value);
                if (preset?.IsDiscordSeries == true)
                {
                    var cards = await _db.CalendarEvents
                        .Where(e => e.EventPresetId == preset.Id && !e.IsHidden && e.StartUtc > now)
                        .ToListAsync();
                    var list = new List<PresetExceptionRecord>();
                    if (!string.IsNullOrEmpty(preset.RecurrenceExceptionsJson))
                    {
                        try
                        {
                            list = System.Text.Json.JsonSerializer
                                .Deserialize<List<PresetExceptionRecord>>(preset.RecurrenceExceptionsJson) ?? new();
                        }
                        catch { }
                    }
                    foreach (var card in cards)
                    {
                        card.IsHidden = true;
                        CancelInList(list, card.OriginalStartUtc ?? card.StartUtc);
                    }
                    preset.RecurrenceExceptionsJson = System.Text.Json.JsonSerializer.Serialize(list);
                    preset.PendingApply = true;
                    await _db.SaveChangesAsync();
                    return Json(new { success = true, cancelled = cards.Count });
                }
            }

            if (ev.IsRecurring)
            {
                var occurrences = GetOccurrences(ev, now, now.AddDays(13 * 7));
                var list = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                foreach (var occ in occurrences) EventExceptionJson.Cancel(list, occ);
                ev.RecurrenceExceptionsJson = EventExceptionJson.Serialize(list);

                var detached = await _db.CalendarEvents
                    .Where(e => e.EventPresetId == ev.EventPresetId && e.SkipDiscordSync
                             && !e.IsHidden && e.StartUtc > now)
                    .ToListAsync();
                foreach (var d in detached) d.IsHidden = true;

                await _db.SaveChangesAsync();
                return Json(new { success = true, cancelled = occurrences.Count });
            }

            return Json(new { success = false, error = "Событие не повторяется" });
        }

        /// <summary>Отмена вхождения: если уже был перенос — превращаем его в отмену.</summary>
        private static void CancelInList(List<PresetExceptionRecord> list, DateTime originalUtc)
        {
            var existing = list.FirstOrDefault(x => x.OriginalStartUtc == originalUtc);
            if (existing == null)
                list.Add(new PresetExceptionRecord { OriginalStartUtc = originalUtc, IsCanceled = true });
            else
            {
                existing.IsCanceled = true;
                existing.NewStartUtc = null;
            }
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

                var exceptionRecords = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                var exceptions = exceptionRecords
                    .Select(r => r.OriginalStartUtc)
                    .ToHashSet();

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

                // Перенесённые вхождения рисуем прямо из исключения — отдельные карточки-дубли не нужны.
                foreach (var moved in exceptionRecords
                             .Where(r => !r.IsCanceled && r.NewStartUtc.HasValue)
                             .Select(r => DateTime.SpecifyKind(r.NewStartUtc!.Value, DateTimeKind.Utc)))
                {
                    if (moved >= rangeStart && moved < rangeEnd && !result.Contains(moved))
                        result.Add(moved);
                }
            }
            catch { }
            return result;
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> PullFromDiscord()
        {
            // Ручное обновление = «покажи фактическое состояние Discord»:
            // даже неприменённые локальные правки уступают Discord.
            CalendarTrace.Write("UI", "Pull from Discord requested (force)");
            try
            {
                await _sync.PullAsync(force: true, ct: HttpContext.RequestAborted);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pull from Discord failed");
                CalendarTrace.Write("PULL", $"fatal: {ex.GetType().Name}: {ex.Message}");
                return Json(new { success = false, error = "Ошибка получения из Discord: " + ex.Message });
            }
        }

        [HttpGet]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> ApplyToDiscordPreview()
        {
            try
            {
                var plan = await _applyService.PreviewAsync();

                return Json(new
                {
                    success = true,
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply preview failed");
                CalendarTrace.Write("APPLY", $"preview fatal: {ex.GetType().Name}: {ex.Message}");
                return Json(new { success = false, error = "Ошибка подготовки: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "ModeratorOnly")]
        public async Task<IActionResult> ApplyToDiscord()
        {
            try
            {
                // Не привязываем к RequestAborted: если прокси оборвёт соединение,
                // синхронизация должна завершиться, а не остановиться на полпути.
                var plan = await _applyService.ApplyAsync();
                return Json(new
                {
                    success = true,
                    toCreate = plan.ToCreate,
                    toUpdate = plan.ToUpdate,
                    toDelete = plan.ToDelete,
                    errors = plan.Errors
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply to Discord failed");
                CalendarTrace.Write("APPLY", $"fatal: {ex.GetType().Name}: {ex.Message}");
                return Json(new { success = false, error = "Ошибка отправки в Discord: " + ex.Message });
            }
        }
    }
}