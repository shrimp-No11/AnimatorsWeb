using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using static DiscordMessageService;


namespace AnimatorsWeb.Controllers
{
    [Authorize(Policy = "ModeratorOnly")]
    public class ModeratorController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DiscordMessageService _discord;
        private readonly DiscordClient _discordClient;
        private readonly ChannelSettingsService _settingsService;
        private readonly RulesService _rulesService;
        private readonly DesignSettingsService _designService;
        private readonly FeaturedThumbnailService _thumbService;
        private readonly IWebHostEnvironment _env;
        const ulong role2dId = 1126390388006072380UL; // ← сюда ID роли 2d Аниматор
        const ulong role3dId = 1536428705105051808UL; // ← сюда ID роли 3d Аниматор
        const ulong ActiveRoleId = 1507425845558509718UL; // роль "Актив"
        private const string AnotherPollOption = "#голосую в другом опросе";

        public ModeratorController(
            DiscordClient discordClient,
            AppDbContext db,
            DiscordMessageService discord,
            ChannelSettingsService settingsService,
            RulesService rulesService,
            DesignSettingsService designService,
            FeaturedThumbnailService thumbService,
            IWebHostEnvironment env)
        {
            _discordClient = discordClient;
            _db = db;
            _discord = discord;
            _settingsService = settingsService;
            _rulesService = rulesService;
            _designService = designService;
            _thumbService = thumbService;
            _env = env;
        }

        // Главная модераторская — настройки каналов
        public async Task<IActionResult> Index()
        {
            var settings = await _settingsService.GetAsync();
            var model = new ChannelSettingsViewModel
            {
                Settings = settings,
                ChannelList = GetChannelSelectList()
            };

            ViewBag.PendingLinksCount = await _db.LinkedAccounts
        .CountAsync(la => !la.IsConfirmed);

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveChannels(ChannelSettings settings)
        {
            await _settingsService.SaveAsync(settings);
            TempData["Result"] = "Настройки каналов сохранены";
            return RedirectToAction("Index");
        }

        private SelectList GetChannelSelectList(ulong? selectedId = null)
        {
            var guild = _discordClient.Guilds.Values.FirstOrDefault();
            if (guild == null)
            {
                return new SelectList(Enumerable.Empty<SelectListItem>());
            }

            var channels = guild.Channels.Values
                .Where(c => c.Type == DiscordChannelType.Text)
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    Id = c.Id,
                    Name = $"#{c.Name}"
                })
                .ToList();

            return new SelectList(channels, "Id", "Name", selectedId);
        }

        // ========== ГОЛОСОВАНИЕ ЗА ТЕМУ ==========

        public async Task<IActionResult> ChooseTheme()
        {
            var settings = await _settingsService.GetAsync();

            int nextNumber = _db.AnimJams.Any()
                ? _db.AnimJams.Max(j => j.Number) + 1
                : 1;
            ViewBag.NextNumber = nextNumber;

            var model = new ChannelSelectViewModel
            {
                ChannelList = GetChannelSelectList(settings.ThemePollChannelId),
                SelectedChannelId = settings.ThemePollChannelId ?? 0
            };
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> PublishThemePoll(ulong selectedChannelId, int animJamNumber, List<string> themes)
        {
            if (selectedChannelId == 0)
            {
                ViewBag.Error = "Пожалуйста, выберите канал.";
                return await ReturnChooseThemeWithSettings();
            }

            var validThemes = themes
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Take(10)
                .ToList();

            if (validThemes.Count < 2)
            {
                ViewBag.Error = "Нужно минимум 2 темы.";
                return await ReturnChooseThemeWithSettings();
            }

            var messageId = await _discord.SendThemePollAsync(selectedChannelId, animJamNumber, validThemes);

            _db.ThemePolls.Add(new ThemePoll
            {
                AnimJamNumber = animJamNumber,
                ChannelId = selectedChannelId,
                MessageId = messageId
            });
            await _db.SaveChangesAsync();



            await _discord.SendRoleMentionsAsync(
                selectedChannelId,
                new[] { role2dId, role3dId },
                $"<@&{role2dId}>, <@&{role3dId}>, голосование за тему {animJamNumber}го аниджема началось!");

            ViewBag.Result = "Голосование опубликовано!";

            return await ReturnChooseThemeWithSettings();
        }

        private async Task<IActionResult> ReturnChooseThemeWithSettings()
        {
            var settings = await _settingsService.GetAsync();
            var model = new ChannelSelectViewModel
            {
                ChannelList = GetChannelSelectList(settings.ThemePollChannelId),
                SelectedChannelId = settings.ThemePollChannelId ?? 0
            };
            return View("ChooseTheme", model);
        }

        // ========== ОТПРАВКА СООБЩЕНИЯ ==========

        public IActionResult SendMessage()
        {
            var model = new ChannelSelectViewModel
            {
                ChannelList = GetChannelSelectList()
            };
            ViewBag.MaxFileSizeBytes = _discord.GetMaxFileSizeBytes();
            ViewBag.MaxFileSizeMb = _discord.GetMaxFileSizeMb();
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SendAnnouncement(ulong selectedChannelId, string text, List<IFormFile>? files)
        {
            bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";

            IActionResult Fail(string msg)
            {
                if (isAjax) return Json(new { success = false, error = msg });
                TempData["Error"] = msg;
                return RedirectToAction("SendMessage");
            }

            if (selectedChannelId == 0)
                return Fail("Не выбран канал.");

            var fileList = files?.Where(f => f.Length > 0).ToList() ?? new List<IFormFile>();

            if (string.IsNullOrWhiteSpace(text) && fileList.Count == 0)
                return Fail("Нечего отправлять — ни текста, ни файлов.");

            var maxSize = _discord.GetMaxFileSizeBytes();
            var maxSizeMb = maxSize / 1_000_000;

            foreach (var f in fileList)
            {
                if (f.Length > maxSize)
                    return Fail($"Файл «{f.FileName}» больше {maxSizeMb} МБ — Discord не примет (лимит текущего сервера).");
            }

            var streams = new List<(string FileName, Stream Stream)>();
            try
            {
                foreach (var f in fileList)
                    streams.Add((f.FileName, f.OpenReadStream()));

                await _discord.SendMessageWithFilesAsync(selectedChannelId, text ?? "", streams);
            }
            catch (Exception ex) when (IsTooLargeException(ex))
            {
                return Fail($"Файл превышает лимит Discord для этого сервера ({maxSizeMb} МБ).");
            }
            catch (Exception ex)
            {
                return Fail($"Ошибка отправки: {ex.Message}");
            }
            finally
            {
                foreach (var s in streams) s.Stream.Dispose();
            }

            var resultMsg = fileList.Count > 0
                ? $"Сообщение с {fileList.Count} файл(ами) отправлено!"
                : "Сообщение отправлено!";

            if (isAjax) return Json(new { success = true, message = resultMsg });

            TempData["Result"] = resultMsg;
            return RedirectToAction("SendMessage");
        }

        private static bool IsTooLargeException(Exception ex)
        {
            if (ex.Message.Contains("413") ||
                ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("File size", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("request entity", StringComparison.OrdinalIgnoreCase))
                return true;

            return ex.InnerException != null && IsTooLargeException(ex.InnerException);
        }


        // ========== СОЗДАНИЕ АНИДЖЕМА ==========

        // GET: страница создания
        public async Task<IActionResult> CreateAnimJam()
        {
            var settings = await _settingsService.GetAsync();

            int nextNumber = _db.AnimJams.Any() ? _db.AnimJams.Max(j => j.Number) + 1 : 1;

            var model = new CreateAnimJamViewModel
            {
                Number = nextNumber,
                EndDate = DateTime.SpecifyKind(GetNextSunday1800Msk().AddHours(-3), DateTimeKind.Utc),
                ChannelId = settings.AnimJamStartChannelId ?? 0,
                ChannelList = GetChannelSelectList(settings.AnimJamStartChannelId)
            };

            await TryFillWinningTheme(model);
            return View(model);
        }

        // POST: публикация
        [HttpPost]
        public async Task<IActionResult> CreateAnimJam(CreateAnimJamViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Theme))
                ModelState.AddModelError("Theme", "Тема обязательна");

            if (!ModelState.IsValid)
            {
                model.ChannelList = GetChannelSelectList(model.ChannelId);
                return View(model);
            }

            // 1. Создаём запись в БД
            var jam = new AnimJam
            {
                Number = model.Number,
                Theme = model.Theme,
                StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
                EndDate = DateTime.SpecifyKind(model.EndDate, DateTimeKind.Utc)
            };

            Console.WriteLine($"Saving StartDate: {jam.StartDate:yyyy-MM-dd HH:mm:ss} Kind={jam.StartDate.Kind}");

            _db.AnimJams.Add(jam);

            // 2. Публикуем объявление
            string text = BuildAnnouncementText(jam);
            ulong messageId = await _discord.SendAnnouncementAsync(
                    model.ChannelId,
                    text,
                    new[] { ModeratorController.role2dId, ModeratorController.role3dId });

            // 3. Формируем ссылку на сообщение и сохраняем
            ulong guildId = _discordClient.Guilds.Values.First().Id;
            jam.AnounceMessage = $"https://discord.com/channels/{guildId}/{model.ChannelId}/{messageId}";
            await _db.SaveChangesAsync();

            TempData["Result"] = $"Аниджем #{jam.Number} создан и опубликован!";
            return RedirectToAction("Index");
        }

        private async Task TryFillWinningTheme(CreateAnimJamViewModel model)
        {
            var poll = await _db.ThemePolls
                .Where(p => p.AnimJamNumber == model.Number)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (poll == null)
            {
                model.PollError = "Опрос для этого номера не найден";
                return;
            }

            var results = await _discord.GetPollResultsAsync(poll.ChannelId, poll.MessageId);

            if (results == null || results.Answers.Count == 0)
            {
                model.PollError = "Опрос ещё идёт или результаты недоступны";
                return;
            }

            int maxCount = results.Answers.Max(a => a.Count);
            var leaders = results.Answers.Where(a => a.Count == maxCount).ToList();

            // Единоличный лидер — сразу берём
            if (leaders.Count == 1)
            {
                ApplyTheme(model, leaders[0].Text);
                return;
            }

            // === Тай-брейк 1: голоса пользователей с ролью "Победитель аниджема" ===
            var winnerFiltered = await FilterLeadersByRoleAsync(poll, leaders, DiscordMessageService.WinnerRoleId);
            if (winnerFiltered.Count < leaders.Count)
                leaders = winnerFiltered;

            if (leaders.Count == 1)
            {
                ApplyTheme(model, leaders[0].Text);
                return;
            }

            // === Тай-брейк 2: голоса пользователей с ролью "Актив" ===
            var activeFiltered = await FilterLeadersByRoleAsync(poll, leaders, ActiveRoleId);
            if (activeFiltered.Count < leaders.Count)
                leaders = activeFiltered;

            if (leaders.Count == 1)
            {
                ApplyTheme(model, leaders[0].Text);
                return;
            }

            // === Всё ещё ничья — отдаём модератору ===
            model.TieCandidates = leaders.Select(l => l.Text).ToList();
            model.PollError = "Ничья — выберите тему вручную";
        }

        private void ApplyTheme(CreateAnimJamViewModel model, string theme)
        {
            model.WinningThemeFromPoll = theme;
            if (string.IsNullOrWhiteSpace(model.Theme))
                model.Theme = theme;
        }

        private async Task<List<DiscordMessageService.PollAnswerResult>> FilterLeadersByRoleAsync(
            ThemePoll poll,
            List<DiscordMessageService.PollAnswerResult> leaders,
            ulong roleId)
        {
            if (leaders.Count <= 1 || roleId == 0) return leaders;

            var counts = new Dictionary<int, int>();
            foreach (var leader in leaders)
            {
                var voters = await _discord.GetVotersAsync(poll.ChannelId, poll.MessageId, leader.AnswerId);
                var withRole = await _discord.FilterUsersWithRoleAsync(voters, roleId);
                counts[leader.AnswerId] = withRole.Count;
            }

            int maxRoleCount = counts.Values.Max();

            // Никто из обладателей роли не голосовал — оставляем ничью как есть
            if (maxRoleCount == 0) return leaders;

            return leaders.Where(l => counts[l.AnswerId] == maxRoleCount).ToList();
        }

        private DateTime GetNextSunday1800Msk()
        {
            var mskNow = DateTime.UtcNow.AddHours(3);

            int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)mskNow.DayOfWeek + 7) % 7;
            if (daysUntilSunday == 0 && mskNow.TimeOfDay >= new TimeSpan(18, 0, 0))
                daysUntilSunday = 7;

            return mskNow.Date.AddDays(daysUntilSunday).AddHours(18);
        }

        private string BuildAnnouncementText(AnimJam jam)
        {
            long endUnix = new DateTimeOffset(DateTime.SpecifyKind(jam.EndDate, DateTimeKind.Utc)).ToUnixTimeSeconds();
            string endLine = $"<t:{endUnix}:F> (<t:{endUnix}:R>)";

            return
                $"<@&{role2dId}>  <@&{role3dId}>\n" +
                $"{jam.Number}AniJam начинается !\n\n" +
                $"Дедлайн - {endLine}\n" +
                $"Тема - {jam.Theme}\n\n" +
                "Правила:\n" +
                "1) Следовать теме (хоть как-то)\n" +
                "2) Новая анимация, сделана специально для AniJam\n" +
                "3) Звук - желательно, не обязательно\n" +
                "4) Главное - веселье, а не качество\n\n" +
                "Номинации:\n" +
                $"{EmojiHelper.Star}Лучшая идея\n" +
                $"{EmojiHelper.Hammer}Лучшее техническое исполнение\n" +
                $"{EmojiHelper.GoldPoopDiscord}Золотая какаха\n\n" +
                "Скидывать работы в этот канал.\n" +
                "Комментарии к анимациям - в ветках\n" +
                "*Анимации могут быть использованы в видеобзоре аниджема для ютуб-канала \"Сообщество Аниматоров\"";
        }
        [HttpGet]
        public async Task<IActionResult> CreateWinnerPoll(int? animJamNumber)
        {
            var settings = await _settingsService.GetAsync();

            var jam = animJamNumber.HasValue
                ? await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == animJamNumber)
                : await _db.AnimJams.OrderByDescending(j => j.Number).FirstOrDefaultAsync();

            int number = jam?.Number ?? 0;

            var model = new WinnerPollViewModel
            {
                AnimJamNumber = number,
                PollTitle = $"{EmojiHelper.Trophy} Победитель {number}го аниджема",
                IdeaTitle = $"{EmojiHelper.Star} Лучшая идея {number}го аниджема",
                TechnicalTitle = $"{EmojiHelper.Hammer} Лучшее техническое исполнение {number}го аниджема",
                ChannelId = settings.WinnerPollChannelId ?? 0,
                ChannelList = GetChannelSelectList(settings.WinnerPollChannelId)
            };

            // Список всех участников гильдии — для автозаполнения
            model.GuildMembers = _discord.GetAllGuildMembers()
                .Select(m => new GuildMemberOption { Id = m.Id, Name = m.Name })
                .ToList();

            if (jam != null && settings.AnimJamStartChannelId.HasValue)
            {
                var participants = await _discord.CollectParticipantsAsync(
                    settings.AnimJamStartChannelId.Value, jam.StartDate);

                // Группируем по WorkUrl → каждая работа = одна команда
                var worksByUrl = participants
                    .Where(p => !string.IsNullOrEmpty(p.WorkUrl))
                    .GroupBy(p => p.WorkUrl)
                    .ToList();

                var ideaSection = new PollSection
                {
                    Nomination = NominationType.Idea,
                    NominationDisplay = NominationHelper.GetDisplay(NominationType.Idea, number)
                };
                var techSection = new PollSection
                {
                    Nomination = NominationType.Technical,
                    NominationDisplay = NominationHelper.GetDisplay(NominationType.Technical, number)
                };

                foreach (var group in worksByUrl)
                {
                    var primary = group.First();

                    var people = group
                        .Select(p => new PollParticipant
                        {
                            DiscordUserId = p.UserId,
                            DisplayName = p.DisplayName
                        })
                        .GroupBy(x => x.DiscordUserId)
                        .Select(x => x.First())
                        .OrderBy(x => x.DisplayName)
                        .ToList();

                    var team = new PollTeam
                    {
                        WorkUrl = primary.WorkUrl,
                        MessageUrl = primary.MessageUrl,
                        Participants = people
                    };

                    ideaSection.Teams.Add(team);
                    techSection.Teams.Add(team);
                }

                // Если ничего не нашли — пустая команда для ручного ввода
                if (ideaSection.Teams.Count == 0)
                {
                    ideaSection.Teams.Add(new PollTeam { Participants = new List<PollParticipant> { new PollParticipant() } });
                    techSection.Teams.Add(new PollTeam { Participants = new List<PollParticipant> { new PollParticipant() } });
                }

                model.Sections.Add(ideaSection);
                model.Sections.Add(techSection);

                model.Message = participants.Any()
                    ? $"Найдено работ: {worksByUrl.Count} (участников: {participants.Count})"
                    : "Участники не найдены — заполните вручную";
            }

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> CreateWinnerPoll(WinnerPollViewModel model)
        {
            if (model.Sections.Count == 0)
            {
                ModelState.AddModelError("", "Нет секций");
            }

            // Собираем команды из секций — только те, где есть имена
            var allTeams = new List<PollTeam>();
            foreach (var s in model.Sections)
            {
                foreach (var t in s.Teams)
                {
                    t.Participants = t.Participants
                        .Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
                        .ToList();
                    if (t.Participants.Count == 0) continue;
                    allTeams.Add(t);
                }
            }

            // Уникальные работы (для опроса)
            var uniqueWorks = allTeams
                .GroupBy(t => string.Join(",", t.Participants.Select(p => p.DisplayName)))
                .Select(g => g.First())
                .ToList();

            if (uniqueWorks.Count == 0)
                ModelState.AddModelError("", "Нужен хотя бы 1 вариант");

            if (!ModelState.IsValid)
            {
                var settings = await _settingsService.GetAsync();
                model.ChannelList = GetChannelSelectList(model.ChannelId);
                model.GuildMembers = _discord.GetAllGuildMembers()
                    .Select(m => new GuildMemberOption { Id = m.Id, Name = m.Name })
                    .ToList();
                return View(model);
            }

            // Один вариант — сразу в объявление
            if (uniqueWorks.Count == 1)
            {
                TempData["Info"] = "Работа только одна. Голосование не нужно — сразу переходим к объявлению победителей.";
                return RedirectToAction("AnnounceWinners", new { animJamNumber = model.AnimJamNumber });
            }

            // === Сохраняем участников в БД сразу ===
            var newParticipants = new List<Participant>();
            var seen = new HashSet<(ulong, string)>();

            foreach (var team in allTeams)
            {
                foreach (var p in team.Participants)
                {
                    var userId = ResolveUserIdByName(p.DisplayName, model.GuildMembers);
                    if (userId == null) continue;

                    var key = (userId.Value, team.WorkUrl);
                    if (!seen.Add(key)) continue;

                    newParticipants.Add(new Participant
                    {
                        AnimJamNumber = model.AnimJamNumber,
                        DiscordUserId = userId.Value,
                        DisplayName = p.DisplayName,
                        WorkUrl = team.WorkUrl,
                        MessageUrl = team.MessageUrl
                    });
                }
            }

            var oldParticipants = await _db.Participants
                .Where(p => p.AnimJamNumber == model.AnimJamNumber)
                .ToListAsync();

            bool same = oldParticipants.Count == newParticipants.Count &&
                oldParticipants.All(op => newParticipants.Any(np =>
                    np.DiscordUserId == op.DiscordUserId && np.WorkUrl == op.WorkUrl));

            if (!same)
            {
                _db.Participants.RemoveRange(oldParticipants);
                _db.Participants.AddRange(newParticipants);
                await _db.SaveChangesAsync();
            }

            // === Формируем опции для опроса ===
            var optionTexts = uniqueWorks
                .Select(t => FormatTeamNames(t.Participants.Select(p => p.DisplayName).ToList()))
                .ToList();

            var optionsJson = System.Text.Json.JsonSerializer.Serialize(uniqueWorks);

            // Удаляем старые опросы для этого аниджема и номинации
            var nominationTypesToDelete = model.Mode == WinnerPollMode.Combined
                ? new[] { NominationType.Winner }
                : new[] { NominationType.Idea, NominationType.Technical };

            var oldPolls = await _db.WinnerPolls
                .Where(p => p.AnimJamNumber == model.AnimJamNumber
                         && nominationTypesToDelete.Contains(p.Nomination))
                .ToListAsync();

            var oldPollIds = oldPolls.Select(p => p.Id).ToList();
            var oldPollMessages = await _db.WinnerPollMessages
                .Where(m => oldPollIds.Contains(m.WinnerPollId))
                .ToListAsync();

            _db.WinnerPollMessages.RemoveRange(oldPollMessages);
            _db.WinnerPolls.RemoveRange(oldPolls);
            await _db.SaveChangesAsync();

            // Публикация в Discord — по одной номинации, возможно несколько сообщений
            async Task<WinnerPoll> PublishPollAsync(string title, NominationType nomination)
            {
                var poll = new WinnerPoll
                {
                    AnimJamNumber = model.AnimJamNumber,
                    Nomination = nomination,
                    ChannelId = model.ChannelId,
                    MessageId = 0,  // заполним после публикации первым
                    CreatedAt = DateTime.UtcNow,
                    OptionsJson = optionsJson
                };
                _db.WinnerPolls.Add(poll);
                await _db.SaveChangesAsync();  // получаем poll.Id

                var chunks = BuildPollChunks(optionTexts);
                for (int i = 0; i < chunks.Count; i++)
                {
                    var msgId = await _discord.SendCustomPollAsync(
                        model.ChannelId, title, chunks[i], model.DurationHours);

                    _db.WinnerPollMessages.Add(new WinnerPollMessage
                    {
                        WinnerPollId = poll.Id,
                        ChannelId = model.ChannelId,
                        MessageId = msgId,
                        Order = i
                    });

                    if (i == 0)
                        poll.MessageId = msgId;  // для обратной совместимости
                }

                await _db.SaveChangesAsync();
                return poll;
            }

            if (model.Mode == WinnerPollMode.Combined)
            {
                await PublishPollAsync(model.PollTitle, NominationType.Winner);
            }
            else
            {
                await PublishPollAsync(model.IdeaTitle, NominationType.Idea);
                await PublishPollAsync(model.TechnicalTitle, NominationType.Technical);
            }

            TempData["Result"] = model.Mode == WinnerPollMode.Combined
                ? "Опрос опубликован!"
                : "Опубликованы опросы: за лучшую идею и за техническое исполнение!";

            return RedirectToAction("Index");
        }

        private static string FormatTeamNames(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} и {names[1]}";
            return string.Join(", ", names.Take(names.Count - 1)) + " и " + names.Last();
        }

        private ulong? ResolveUserIdByName(string name, List<GuildMemberOption> guildMembers)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var match = guildMembers.FirstOrDefault(m =>
                string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Id;

            return _discord.FindUserIdByDisplayName(name);
        }


        [HttpGet]
        public async Task<IActionResult> AnnounceWinners(int? animJamNumber)
        {
            var model = new WinnersPageViewModel();

            var jams = await _db.AnimJams.OrderByDescending(j => j.Number).ToListAsync();
            model.AllJams = jams;

            if (!animJamNumber.HasValue) return View(model);

            var jam = jams.FirstOrDefault(j => j.Number == animJamNumber.Value);
            if (jam == null)
            {
                model.Message = "Аниджем не найден";
                return View(model);
            }

            model.AnimJamNumber = jam.Number;
            model.Theme = jam.Theme;
            model.AnnouncementUrl = jam.AnounceMessage ?? "";

            var settings = await _settingsService.GetAsync();

            // Загрузка опросов для этого аниджема
            var polls = await _db.WinnerPolls
                .Where(p => p.AnimJamNumber == jam.Number)
                .ToListAsync();

            // Сколько сообщений-опросов было (для UI)
            if (polls.Count > 0)
            {
                var pollIds = polls.Select(p => p.Id).ToList();
                model.PollMessageCount = await _db.WinnerPollMessages
                    .Where(m => pollIds.Contains(m.WinnerPollId))
                    .CountAsync();
            }


            model.PublishChannelId = settings.WinnersAnnouncementChannelId
                      ?? (polls.Count > 0 ? (ulong?)polls.First().ChannelId : null)
                      ?? settings.WinnerPollChannelId
                      ?? settings.AnimJamStartChannelId
                      ?? 0;

            if (polls.Count == 1 && polls[0].Nomination == NominationType.Winner)
                model.Mode = WinnerPollMode.Combined;
            else
                model.Mode = WinnerPollMode.TwoNominations;

            // === Участники — из БД, без Discord ===
            var dbParticipants = await _db.Participants
                .Where(p => p.AnimJamNumber == jam.Number)
                .ToListAsync();

            // === Освежаем CDN-ссылки (mp4/webm/gif/png/jpg и т.п.) ===
            // YouTube и прочие не-вложения пропускаем
            var uniqueWorks = dbParticipants
                .Where(p => !string.IsNullOrEmpty(p.WorkUrl) && !string.IsNullOrEmpty(p.MessageUrl))
                .Where(p => DiscordMessageService.IsAttachmentUrl(p.WorkUrl))
                .GroupBy(p => p.WorkUrl)
                .Select(g => g.First())
                .ToList();

            var refreshedMap = new Dictionary<string, string>(); // oldWorkUrl -> freshWorkUrl


            foreach (var sample in uniqueWorks)
            {
                var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(sample.MessageUrl);
                if (chId == null || msgId == null) continue;

                string? fresh = null;
                try
                {
                    fresh = await _discord.GetFreshAttachmentUrlAsync(chId.Value, msgId.Value);
                }
                catch { }

                if (!string.IsNullOrEmpty(fresh) && fresh != sample.WorkUrl)
                    refreshedMap[sample.WorkUrl] = fresh;
            }

            if (refreshedMap.Count > 0)
            {
                // Обновляем у участников
                foreach (var p in dbParticipants)
                    if (refreshedMap.TryGetValue(p.WorkUrl, out var fresh))
                        p.WorkUrl = fresh;

                // Обновляем у победителей этого аниджема с той же старой ссылкой
                var oldUrls = refreshedMap.Keys.ToList();
                var relatedWinners = await _db.Winners
                    .Where(w => w.AnimJamNumber == jam.Number && oldUrls.Contains(w.WorkUrl))
                    .ToListAsync();

                foreach (var w in relatedWinners)
                    if (refreshedMap.TryGetValue(w.WorkUrl, out var fresh))
                        w.WorkUrl = fresh;

                await _db.SaveChangesAsync();
            }


            var participants = dbParticipants
                .Select(p =>
                {
                    var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(p.MessageUrl);
                    return new ParticipantInfo
                    {
                        UserId = p.DiscordUserId,
                        DisplayName = p.DisplayName,
                        WorkUrl = p.WorkUrl,
                        MessageUrl = p.MessageUrl,
                        ChannelId = chId ?? 0,
                        MessageId = msgId ?? 0
                    };
                })
                .ToList();

            // === Секции ===
            var orderedNominations = model.Mode == WinnerPollMode.Combined
                ? new[] { NominationType.Winner }
                : new[] { NominationType.Idea, NominationType.Technical };

            var allWinnerIds = new HashSet<ulong>();

            foreach (var nomination in orderedNominations)
            {
                var section = new NominationSection
                {
                    Nomination = nomination,
                    NominationDisplay = NominationHelper.GetDisplay(nomination, jam.Number),
                    NominationDisplayHtml = NominationHelper.GetDisplayHtml(nomination, jam.Number)
                };

                var poll = polls.FirstOrDefault(p => p.Nomination == nomination);

                if (poll != null)
                {
                    var (winners, excludedVotes, totalVotes) =
                    await DetermineWinnersAsync(poll, participants);

                    section.ExcludedVotes = excludedVotes;
                    section.TotalVotes = totalVotes;
                    if (winners.Count > 0)
                    {
                        section.Teams = winners;

                        foreach (var team in winners)
                        {
                            foreach (var name in team.Participants)
                            {
                                var info = participants.FirstOrDefault(p => p.DisplayName == name);
                                if (info != null) allWinnerIds.Add(info.UserId);
                            }
                        }
                    }
                }
                else if (participants.Count == 1)
                {
                    var single = participants[0];
                    section.Teams = new List<WinnerTeam>
                    {
                        new WinnerTeam
                        {
                            WorkUrl = single.WorkUrl,
                            Participants = new List<string> { single.DisplayName }
                        }
                    };
                    allWinnerIds.Add(single.UserId);
                }

                if (section.Teams.Count == 0)
                    section.Teams.Add(new WinnerTeam());

                // Сколько сообщений-опросов было в этой номинации
                if (polls.Count > 0)
                {
                    var pollForNom = polls.FirstOrDefault(p => p.Nomination == nomination);
                    if (pollForNom != null)
                    {
                        section.PollMessageCount = await _db.WinnerPollMessages
                            .Where(m => m.WinnerPollId == pollForNom.Id)
                            .CountAsync();
                    }
                }

                model.Sections.Add(section);
            }

            // === Золотая какаха ===
            var honorableSection = await DetermineHonorableMentionAsync(
                participants, allWinnerIds, jam.Number);

            if (honorableSection != null)
                model.Sections.Add(honorableSection);
            
            return View(model);
        }

        private string GetNominationDisplay(NominationType nom, int jamNumber)
        {
            return nom switch
            {
                NominationType.Idea => $"✨ победитель в лучшей идее {jamNumber}го аниджема",
                NominationType.Technical => $"🔨 победитель в техническом исполнении {jamNumber}го аниджема",
                NominationType.Winner => $"🏆 победитель {jamNumber}го аниджема",
                NominationType.HonorableMention => $"🎖 почётное упоминание {jamNumber}го аниджема",
                _ => "Победитель"
            };
        }
        private async Task<NominationSection?> DetermineHonorableMentionAsync(
    List<ParticipantInfo> participants,
    HashSet<ulong> alreadyWon,
    int jamNumber)
        {
            if (participants.Count < 2) return null;

            // Собираем "счёт реакторов" для каждого участника
            var scores = new Dictionary<ulong, int>();

            foreach (var p in participants)
            {
                if (alreadyWon.Contains(p.UserId)) continue;
                if (p.MessageId == 0) continue;

                var reactors = await _discord.GetUniqueReactorsAsync(p.ChannelId, p.MessageId);
                reactors.Remove(p.UserId);

                // Если у человека несколько работ — берём максимум по ним
                if (scores.TryGetValue(p.UserId, out var existing))
                    scores[p.UserId] = Math.Max(existing, reactors.Count);
                else
                    scores[p.UserId] = reactors.Count;
            }

            if (scores.Count == 0) return null;

            int max = scores.Values.Max();
            if (max <= 0) return null;

            var winnerIds = scores.Where(kv => kv.Value == max).Select(kv => kv.Key).ToList();

            var section = new NominationSection
            {
                Nomination = NominationType.HonorableMention,
                NominationDisplay = NominationHelper.GetDisplay(NominationType.HonorableMention, jamNumber),
                NominationDisplayHtml = NominationHelper.GetDisplayHtml(NominationType.HonorableMention, jamNumber)
            };

            foreach (var userId in winnerIds)
            {
                var info = participants.FirstOrDefault(p => p.UserId == userId);
                if (info == null) continue;

                var parts = info.DisplayName
                    .Split(new[] { ",", " и " }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                section.Teams.Add(new WinnerTeam
                {
                    WorkUrl = info.WorkUrl,
                    Participants = parts.Count > 0 ? parts : new List<string> { info.DisplayName }
                });
            }

            return section;
        }

        private async Task<(List<WinnerTeam> Teams, int ExcludedVotes, int TotalVotes)> DetermineWinnersAsync(
    WinnerPoll poll, List<ParticipantInfo> participants)
    {
        var savedTeams = string.IsNullOrEmpty(poll.OptionsJson)
            ? new List<PollTeam>()
            : System.Text.Json.JsonSerializer.Deserialize<List<PollTeam>>(poll.OptionsJson)
              ?? new List<PollTeam>();

        var messages = await _db.WinnerPollMessages
            .Where(m => m.WinnerPollId == poll.Id)
            .OrderBy(m => m.Order)
            .ToListAsync();

        // Fallback для старых опросов без WinnerPollMessage
        if (messages.Count == 0 && poll.MessageId != 0)
        {
            var legacy = await DetermineWinnersLegacyAsync(poll, savedTeams, participants);
            return (legacy, 0, 0);
        }

        // userId -> список (pollIdx, optionText) обычных вариантов
        var userVotes = new Dictionary<ulong, List<(int PollIdx, string OptionText)>>();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var results = await _discord.GetPollResultsAsync(msg.ChannelId, msg.MessageId);
            if (results == null) continue;

            foreach (var ans in results.Answers)
            {
                if (ans.Text == AnotherPollOption) continue;

                var voters = await _discord.GetVotersAsync(msg.ChannelId, msg.MessageId, ans.AnswerId);
                foreach (var uid in voters)
                {
                    if (!userVotes.TryGetValue(uid, out var list))
                    {
                        list = new List<(int, string)>();
                        userVotes[uid] = list;
                    }
                    list.Add((i, ans.Text));
                }
            }
        }

        int totalVoters = userVotes.Count;
        int excluded = userVotes.Values.Count(v => v.Count > 1);

        // Учитываем только тех, кто проголосовал ровно за одного участника
        var votesByOption = new Dictionary<string, int>();
        foreach (var votes in userVotes.Values)
        {
            if (votes.Count != 1) continue;
            var opt = votes[0].OptionText;
            votesByOption[opt] = votesByOption.GetValueOrDefault(opt) + 1;
        }

        var result = new List<WinnerTeam>();

        if (votesByOption.Count == 0)
            return (result, excluded, totalVoters);

        if (votesByOption.Count == 1)
        {
            var single = votesByOption.Keys.First();
            return (new List<WinnerTeam> { BuildTeamFromOption(single, savedTeams, participants) },
                    excluded, totalVoters);
        }

        int max = votesByOption.Values.Max();
        var winnerNames = votesByOption
            .Where(kv => kv.Value == max)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var name in winnerNames)
            result.Add(BuildTeamFromOption(name, savedTeams, participants));

        return (result, excluded, totalVoters);
    }

        // Старая логика для опросов, созданных до WinnerPollMessage
        private async Task<List<WinnerTeam>> DetermineWinnersLegacyAsync(
            WinnerPoll poll, List<PollTeam> savedTeams, List<ParticipantInfo> participants)
        {
            var result = new List<WinnerTeam>();

            var pollResults = await _discord.GetPollResultsAsync(poll.ChannelId, poll.MessageId);
            if (pollResults == null || pollResults.Answers.Count == 0) return result;

            if (pollResults.Answers.Count == 1)
            {
                var single = pollResults.Answers[0];
                return new List<WinnerTeam> { BuildTeamFromOption(single.Text, savedTeams, participants) };
            }

            var adjusted = new List<(string Text, int Count)>();
            foreach (var ans in pollResults.Answers)
            {
                var voters = await _discord.GetVotersAsync(poll.ChannelId, poll.MessageId, ans.AnswerId);
                int selfVotes = 0;

                var team = savedTeams.FirstOrDefault(t =>
                    FormatTeamNames(t.Participants.Select(p => p.DisplayName).ToList()) == ans.Text);

                if (team != null)
                {
                    var authorIds = team.Participants
                        .Where(p => p.DiscordUserId != 0)
                        .Select(p => p.DiscordUserId)
                        .ToHashSet();
                    foreach (var voterId in voters)
                        if (authorIds.Contains(voterId)) selfVotes++;
                }

                adjusted.Add((ans.Text, ans.Count - selfVotes));
            }

            int max = adjusted.Max(a => a.Count);
            if (max <= 0) return result;

            var winnerNames = adjusted.Where(a => a.Count == max).Select(a => a.Text).ToList();
            foreach (var name in winnerNames)
                result.Add(BuildTeamFromOption(name, savedTeams, participants));

            return result;
        }

        private WinnerTeam BuildTeamFromOption(
    string optionText,
    List<PollTeam> savedTeams,
    List<ParticipantInfo> participants)
        {
            // Прямой матч по тексту опции
            var match = savedTeams.FirstOrDefault(t =>
                FormatTeamNames(t.Participants.Select(p => p.DisplayName).ToList()) == optionText);

            if (match != null)
            {
                return new WinnerTeam
                {
                    WorkUrl = match.WorkUrl,
                    MessageUrl = match.MessageUrl,
                    Participants = match.Participants
                        .Select(p => p.DisplayName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList()
                };
            }

            // Fallback для старых опросов без OptionsJson: ищем по имени
            var info = participants.FirstOrDefault(p => p.DisplayName == optionText);
            string workUrl = info?.WorkUrl ?? "";

            var collaborators = participants
                .Where(p => p.WorkUrl == workUrl && !string.IsNullOrEmpty(workUrl))
                .Select(p => p.DisplayName)
                .Distinct()
                .ToList();

            return new WinnerTeam
            {
                WorkUrl = workUrl,
                MessageUrl = info?.MessageUrl ?? "",
                Participants = collaborators.Count > 0
                    ? collaborators
                    : new List<string> { optionText }
            };
        }

        private ParticipantInfo? FindOptionByName(
            string optionText,
            List<(string OptionText, ParticipantInfo Participant)> pollOptions,
            List<ParticipantInfo> participants)
        {
            var match = pollOptions.FirstOrDefault(o => o.OptionText == optionText);
            if (match.Participant != null) return match.Participant;
            return participants.FirstOrDefault(p => p.DisplayName == optionText);
        }

        [HttpPost]
        public async Task<IActionResult> AnnounceWinners(WinnersPageViewModel model)
        {
            if (model.Sections.Count == 0)
                ModelState.AddModelError("", "Нет разделов для публикации");

            if (!ModelState.IsValid)
            {
                model.AllJams = await _db.AnimJams.OrderByDescending(j => j.Number).ToListAsync();
                return View(model);
            }

            var jam = await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == model.AnimJamNumber);

            var dbParticipants = await _db.Participants
                .Where(p => p.AnimJamNumber == model.AnimJamNumber)
                .ToListAsync();

            var participants = dbParticipants
                .Select(p =>
                {
                    var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(p.MessageUrl);
                    return new ParticipantInfo
                    {
                        UserId = p.DiscordUserId,
                        DisplayName = p.DisplayName,
                        WorkUrl = p.WorkUrl,
                        MessageUrl = p.MessageUrl,
                        ChannelId = chId ?? 0,
                        MessageId = msgId ?? 0
                    };
                })
                .ToList();

            // === Формируем новый набор участников для БД ===
            var newParticipants = participants
                .GroupBy(p => new { p.UserId, p.WorkUrl })
                .Select(g => g.First())
                .Select(p => new Participant
                {
                    AnimJamNumber = model.AnimJamNumber,
                    DiscordUserId = p.UserId,
                    DisplayName = p.DisplayName,
                    WorkUrl = p.WorkUrl,
                    MessageUrl = p.MessageUrl
                })
                .ToList();

            // Дополняем участниками команд, которых нет в списке (коллаборации)
            var participantIds = newParticipants.Select(p => p.DiscordUserId).ToHashSet();

            foreach (var section in model.Sections)
            {
                foreach (var team in section.Teams)
                {
                    // Находим работу этой команды у кого-то из участников — источник URL и MessageId
                    Participant? sourceParticipant = null;

                    foreach (var name in team.Participants.Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        var userId = ResolveUserId(name, participants);
                        if (userId == null || userId == 0) continue;

                        // Если этот человек уже в списке — берём его запись как источник данных
                        var existing = participants.FirstOrDefault(p => p.UserId == userId.Value);
                        if (existing != null)
                        {
                            sourceParticipant = new Participant
                            {
                                AnimJamNumber = model.AnimJamNumber,
                                DiscordUserId = existing.UserId,
                                DisplayName = existing.DisplayName,
                                WorkUrl = team.WorkUrl,
                                MessageUrl = existing.MessageUrl,
                            };
                            break;
                        }
                    }

                    // Добавляем каждого члена команды, кого ещё нет в Participants
                    foreach (var name in team.Participants.Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        var userId = ResolveUserId(name, participants);
                        if (userId == null || userId == 0) continue;

                        if (participantIds.Contains(userId.Value)) continue;

                        // Находим DisplayName для этого ника
                        var displayName = name;
                        var info = participants.FirstOrDefault(p => p.UserId == userId.Value);
                        if (info != null) displayName = info.DisplayName;

                        newParticipants.Add(new Participant
                        {
                            AnimJamNumber = model.AnimJamNumber,
                            DiscordUserId = userId.Value,
                            DisplayName = displayName,
                            WorkUrl = team.WorkUrl,
                            MessageUrl = sourceParticipant?.MessageUrl ?? ""
                        });

                        participantIds.Add(userId.Value);
                    }
                }
            }

            // === Формируем новый набор победителей для БД ===
            var newWinners = new List<Winner>();
            foreach (var section in model.Sections)
            {
                foreach (var team in section.Teams)
                {
                    foreach (var name in team.Participants.Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        var userId = ResolveUserId(name, participants);
                        if (userId == null || userId == 0) continue;

                        newWinners.Add(new Winner
                        {
                            AnimJamNumber = model.AnimJamNumber,
                            DiscordUserId = userId.Value,
                            Nomination = section.Nomination,
                            WorkUrl = team.WorkUrl 
                        });
                    }
                }
            }

            // === Сравниваем с тем, что уже в БД ===
            var oldParticipants = await _db.Participants
                .Where(p => p.AnimJamNumber == model.AnimJamNumber)
                .ToListAsync();

            var oldWinners = await _db.Winners
                .Where(w => w.AnimJamNumber == model.AnimJamNumber)
                .ToListAsync();

            bool participantsSame =
                oldParticipants.Count == newParticipants.Count &&
                oldParticipants.All(op => newParticipants.Any(np =>
                    np.DiscordUserId == op.DiscordUserId &&
                    np.DisplayName == op.DisplayName &&
                    np.WorkUrl == op.WorkUrl &&
                    np.MessageUrl == op.MessageUrl));

            bool winnersSame =
                oldWinners.Count == newWinners.Count &&
                oldWinners.All(ow => newWinners.Any(nw =>
                    nw.DiscordUserId == ow.DiscordUserId &&
                    nw.Nomination == ow.Nomination));

            // === Сохраняем, если что-то изменилось ===
            if (!participantsSame || !winnersSame)
            {
                _db.Participants.RemoveRange(oldParticipants);
                _db.Winners.RemoveRange(oldWinners);

                _db.Participants.AddRange(newParticipants);
                _db.Winners.AddRange(newWinners);

                await _db.SaveChangesAsync();
            }
            // === Обновляем роль «Победитель аниджема» ===
            ulong winnerGuildId = _discordClient.Guilds.Values.First().Id;

            var winnerUserIds = newWinners
                .Select(w => w.DiscordUserId)
                .Distinct();

            await _discord.ResetAndGrantRoleAsync(
                winnerGuildId,
                DiscordMessageService.WinnerRoleId,
                winnerUserIds);

            // === Публикуем сообщения ===
            var messages = BuildWinnerMessages(model, participants);

            ulong? firstMessageId = null;

            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];

                Stream? fileStream = null;
                string? fileName = null;

                if (!string.IsNullOrWhiteSpace(msg.FileUrl))
                {
                    fileStream = await _discord.DownloadFileAsync(msg.FileUrl);
                    if (fileStream != null)
                        fileName = DiscordMessageService.GetFileNameFromUrl(msg.FileUrl);
                    else
                    {
                        // Не удалось скачать — оставим ссылку в тексте как fallback
                        msg.Content = msg.Content + "\n" + msg.FileUrl;
                    }
                }

                try
                {
                    ulong msgId = await _discord.SendMessageWithFileAndMentionsAsync(
                        model.PublishChannelId, msg.Content, fileName, fileStream, msg.Mentions);

                    if (i == 0)
                        firstMessageId = msgId;
                }
                finally
                {
                    fileStream?.Dispose();
                }
            }

            // === Сохраняем ссылку на первое сообщение ===
            if (firstMessageId.HasValue && jam != null)
            {
                ulong guildId = _discordClient.Guilds.Values.First().Id;
                jam.ResultsMessageUrl = $"https://discord.com/channels/{guildId}/{model.PublishChannelId}/{firstMessageId.Value}";
                await _db.SaveChangesAsync();
            }

            // Фоново генерируем обложки для участников этого аниджема
            var newParticipantsSnapshot = newParticipants
                .Select(p => new { p.Id, p.WorkUrl, p.MessageUrl })
                .ToList();

            _ = Task.Run(async () =>
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var thumb = scope.ServiceProvider.GetRequiredService<FeaturedThumbnailService>();
                foreach (var p in newParticipantsSnapshot)
                {
                    var mt = DetectMediaType(p.WorkUrl);
                    if (mt == "video" || mt == "image" || mt == "youtube")
                        await thumb.EnsureAsync(p.Id, p.WorkUrl, p.MessageUrl, mt);
                    await Task.Delay(300);
                }
            });

            TempData["Result"] = "Объявление победителей опубликовано!";
            return RedirectToAction("Index");
        }
        [HttpPost]
        public async Task<IActionResult> UploadRuleAttachment(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, error = "Файл пустой" });

            const long maxSize = 8_000_000;
            if (file.Length > maxSize)
                return Json(new { success = false, error = "Файл больше 8 МБ" });

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";

            var storedName = Guid.NewGuid().ToString("N") + ext;
            var dir = Path.Combine(_env.WebRootPath, "uploads", "rules");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, storedName);

            await using (var fs = System.IO.File.Create(path))
            {
                await file.CopyToAsync(fs);
            }

            return Json(new { success = true, storedName, fileName = file.FileName });
        }

        private List<WinnerMessage> BuildWinnerMessages(
    WinnersPageViewModel model, List<ParticipantInfo> participants)
        {
            var messages = new List<WinnerMessage>();

            // 1. Информация об аниджеме
            var info = new System.Text.StringBuilder();
            info.AppendLine($"╠═════                     AniJam {model.AnimJamNumber}                     ═════╣");
            info.AppendLine($"                                                  {model.Theme}");
            info.AppendLine();
            if (!string.IsNullOrWhiteSpace(model.AnnouncementUrl))
                info.AppendLine($"[остальные анимации]({model.AnnouncementUrl})");

            messages.Add(new WinnerMessage { Content = info.ToString().TrimEnd() });

            // 2. Команды, победившие в обеих номинациях
            var combinedTeams = new List<WinnerTeam>();

            if (model.Mode == WinnerPollMode.TwoNominations)
            {
                var idea = model.Sections.FirstOrDefault(s => s.Nomination == NominationType.Idea);
                var tech = model.Sections.FirstOrDefault(s => s.Nomination == NominationType.Technical);

                if (idea != null && tech != null)
                {
                    var techUrls = tech.Teams
                        .Where(t => !string.IsNullOrWhiteSpace(t.WorkUrl))
                        .Select(t => t.WorkUrl.Trim())
                        .ToHashSet();

                    var ideaUrls = idea.Teams
                        .Where(t => !string.IsNullOrWhiteSpace(t.WorkUrl))
                        .Select(t => t.WorkUrl.Trim())
                        .ToHashSet();

                    combinedTeams.AddRange(
                        idea.Teams.Where(t => !string.IsNullOrWhiteSpace(t.WorkUrl)
                                              && techUrls.Contains(t.WorkUrl.Trim())));

                    idea.Teams = idea.Teams
                        .Where(t => string.IsNullOrWhiteSpace(t.WorkUrl)
                                    || !techUrls.Contains(t.WorkUrl.Trim()))
                        .ToList();

                    tech.Teams = tech.Teams
                        .Where(t => string.IsNullOrWhiteSpace(t.WorkUrl)
                                    || !ideaUrls.Contains(t.WorkUrl.Trim()))
                        .ToList();
                }
            }

            // 3. Объединённые победители
            foreach (var team in combinedTeams)
            {
                if (!team.Participants.Any(p => !string.IsNullOrWhiteSpace(p))) continue;

                messages.Add(BuildTeamMessage(
                    NominationHelper.GetCombinedDisplay(model.AnimJamNumber),
                    team, participants));
            }

            // 4. Обычные номинации
            foreach (var section in model.Sections)
            {
                foreach (var team in section.Teams)
                {
                    if (!team.Participants.Any(p => !string.IsNullOrWhiteSpace(p))) continue;

                    messages.Add(BuildTeamMessage(
                        section.NominationDisplay,
                        team, participants));
                }
            }

            return messages;
        }

        private WinnerMessage BuildTeamMessage(
    string nominationDisplay, WinnerTeam team, List<ParticipantInfo> participants)
        {
            var mentions = new List<ulong>();
            var mentionStrings = new List<string>();

            foreach (var name in team.Participants.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var infoPart = participants.FirstOrDefault(p => p.DisplayName == name);
                ulong? userId = infoPart?.UserId;

                if (userId == null || userId == 0)
                    userId = _discord.FindUserIdByDisplayName(name);

                if (userId.HasValue && userId.Value != 0)
                {
                    mentions.Add(userId.Value);
                    mentionStrings.Add($"<@{userId.Value}>");
                }
                else
                {
                    mentionStrings.Add("@" + name);
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{nominationDisplay} - {FormatParticipantsList(mentionStrings)}");

            // Если ссылка — не файл (YouTube и т.п.) — оставляем её в тексте.
            // Если файл — прикрепим отдельно, ссылку в текст не пишем.
            string? fileUrl = null;

            if (!string.IsNullOrWhiteSpace(team.WorkUrl))
            {
                if (DiscordMessageService.IsAttachmentUrl(team.WorkUrl))
                    fileUrl = team.WorkUrl;
                else
                    sb.AppendLine(team.WorkUrl);
            }

            return new WinnerMessage
            {
                Content = sb.ToString().TrimEnd(),
                Mentions = mentions,
                FileUrl = fileUrl
            };
        }
        

        private string FormatParticipantsList(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} и {names[1]}";
            return string.Join(", ", names.Take(names.Count - 1)) + " и " + names.Last();
        }
        private ulong? ResolveUserId(string name, List<ParticipantInfo> participants)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var info = participants.FirstOrDefault(p => p.DisplayName == name);
            if (info != null && info.UserId != 0) return info.UserId;

            return _discord.FindUserIdByDisplayName(name);
        }
        private class WinnerMessage
        {
            public string Content { get; set; } = "";
            public List<ulong> Mentions { get; set; } = new();
            public string? FileUrl { get; set; }
        }



        [HttpGet]
        public async Task<IActionResult> Rules()
        {
            var settings = await _rulesService.GetAsync();
            var model = new RulesViewModel
            {
                Settings = settings,
                ChannelList = GetChannelSelectList(settings.ChannelId)
            };

            if (!string.IsNullOrEmpty(settings.SectionsJson))
            {
                try
                {
                    model.Sections = System.Text.Json.JsonSerializer
                        .Deserialize<List<RuleSection>>(settings.SectionsJson) ?? new();
                }
                catch { }
            }

            if (model.Sections.Count == 0 && !string.IsNullOrWhiteSpace(settings.Content))
                model.Sections.Add(new RuleSection { Content = settings.Content });

            if (model.Sections.Count == 0)
                model.Sections.Add(new RuleSection());

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveRules(RulesViewModel model)
        {
            await _rulesService.SaveAsync(model.Settings, model.Sections);
            TempData["Result"] = "Правила сохранены и опубликованы";
            return RedirectToAction("Rules");
        }
        [HttpPost]
        public async Task<IActionResult> CleanupRulesReactions()
        {
            try
            {
                var settings = await _rulesService.GetAsync();

                if (settings.ChannelId == 0 || settings.LastMessageId == 0)
                {
                    TempData["Error"] = "Сначала сохраните правила — канал и сообщения не настроены.";
                    return RedirectToAction("Rules");
                }

                // Парсим AllMessageIds (fallback на LastMessageId)
                var messageIds = (settings.AllMessageIds ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => ulong.TryParse(s.Trim(), out var id) ? id : 0)
                    .Where(id => id != 0)
                    .ToList();

                if (messageIds.Count == 0)
                    messageIds.Add(settings.LastMessageId);

                await _discord.CleanupRulesReactionsAsync(
                    settings.ChannelId,
                    messageIds,
                    settings.LastMessageId,
                    settings.RoleId,
                    grantRoles: true);

                TempData["Result"] = $"Приборка выполнена: обработано {messageIds.Count} сообщений, всем проголосовавшим выданы роли, лишние ✅ удалены.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Ошибка приборки: {ex.Message}";
            }

            return RedirectToAction("Rules");
        }


        private static string FormatNames(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} и {names[1]}";
            return string.Join(", ", names.Take(names.Count - 1)) + " и " + names.Last();
        }

        // ====== СВЯЗАННЫЕ АККАУНТЫ ======

        [HttpGet]
        public async Task<IActionResult> LinkedAccounts()
        {
            var model = new ModeratorLinkedAccountsViewModel();

            var all = await _db.LinkedAccounts
                .OrderBy(la => la.IsConfirmed)   // неподтверждённые сверху
                .ThenBy(la => la.Id)
                .ToListAsync();

            // Один запрос на все id: последний DisplayName из Participants
            var allIds = all
                .SelectMany(la => new[] { la.PrimaryDiscordId, la.LinkedDiscordId ?? 0 })
                .Where(x => x != 0)
                .Distinct()
                .ToList();

            var nameById = await _db.Participants
                .Where(p => allIds.Contains(p.DiscordUserId))
                .GroupBy(p => p.DiscordUserId)
                .Select(g => new
                {
                    Id = g.Key,
                    Name = g.OrderByDescending(p => p.AnimJamNumber)
                            .Select(p => p.DisplayName)
                            .FirstOrDefault()
                })
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            foreach (var la in all)
            {
                var row = new LinkedAccountRow { Link = la };

                row.PrimaryName = nameById.GetValueOrDefault(la.PrimaryDiscordId);
                if (la.LinkedDiscordId.HasValue)
                    row.LinkedNameResolved = nameById.GetValueOrDefault(la.LinkedDiscordId.Value);

                model.Rows.Add(row);
            }

            return View(model);
        }
        [HttpGet]
        public async Task<IActionResult> SearchParticipants(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                return Json(Array.Empty<object>());

            var q = query.Trim();

            var matchedIds = await _db.Participants
                .Where(p => EF.Functions.ILike(p.DisplayName, $"%{q}%"))
                .Select(p => p.DiscordUserId)
                .Distinct()
                .Take(50)
                .ToListAsync();

            if (matchedIds.Count == 0)
                return Json(Array.Empty<object>());

            // ВСЕ работы этих id — чтобы статистика не зависела от смены ника
            var allRecords = await _db.Participants
                .Where(p => matchedIds.Contains(p.DiscordUserId))
                .ToListAsync();

            var result = allRecords
                .GroupBy(p => p.DiscordUserId)
                .Select(g => new
                {
                    DiscordUserId = g.Key.ToString(),            
                    DisplayName = g.OrderByDescending(p => p.AnimJamNumber)
                                   .Select(p => p.DisplayName)
                                   .FirstOrDefault() ?? "",
                    WorksCount = g.Count(),
                    LastAnimJam = g.Max(p => p.AnimJamNumber)
                })
                .OrderByDescending(x => x.LastAnimJam)
                .ToList();

            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> ApproveLinkedAccount(int id, ulong? resolvedId)
        {
            var la = await _db.LinkedAccounts.FirstOrDefaultAsync(x => x.Id == id);
            if (la == null) return NotFound();

            if (la.IsConfirmed)
            {
                TempData["Error"] = "Заявка уже подтверждена";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            ulong? targetId = la.LinkedDiscordId ?? resolvedId;
            if (!targetId.HasValue)
            {
                TempData["Error"] = "Не указан ID аккаунта для подтверждения";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            if (targetId.Value == la.PrimaryDiscordId)
            {
                TempData["Error"] = "Нельзя связать аккаунт с самим собой";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            var exists = await _db.Participants.AnyAsync(p => p.DiscordUserId == targetId.Value);
            if (!exists)
            {
                TempData["Error"] = "Указанный ID не найден среди участников";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            var duplicate = await _db.LinkedAccounts.AnyAsync(x =>
                x.Id != id &&
                ((x.PrimaryDiscordId == la.PrimaryDiscordId && x.LinkedDiscordId == targetId) ||
                 (x.PrimaryDiscordId == targetId && x.LinkedDiscordId == la.PrimaryDiscordId)));

            if (duplicate)
            {
                TempData["Error"] = "Связь между этими аккаунтами уже существует";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            la.LinkedDiscordId = targetId;
            la.LinkedName = null;
            la.IsConfirmed = true;
            await _db.SaveChangesAsync();

            TempData["Result"] = "Связь подтверждена";
            return RedirectToAction(nameof(LinkedAccounts));
        }

        [HttpPost]
        public async Task<IActionResult> RejectLinkedAccount(int id)
        {
            var la = await _db.LinkedAccounts.FirstOrDefaultAsync(x => x.Id == id);
            if (la == null) return NotFound();

            _db.LinkedAccounts.Remove(la);
            await _db.SaveChangesAsync();

            TempData["Result"] = "Связь удалена";
            return RedirectToAction(nameof(LinkedAccounts));
        }

        // ====== ДИЗАЙН ======

        [HttpGet]
        public async Task<IActionResult> Design()
        {
            var model = new DesignSettingsViewModel
            {
                Design = await _designService.GetAsync(),
                AvatarUrl = _designService.GetLocalAvatarUrl() ?? _designService.GetGuildIconUrl()
            };
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveDesign(DesignSettings design)
        {
            var current = await _designService.GetAsync();
            design.PaletteUpdatedAt = current.PaletteUpdatedAt;

            await _designService.SaveAsync(design);
            TempData["Result"] = "Настройки дизайна сохранены";
            return RedirectToAction(nameof(Design));
        }
        [HttpPost]
        public async Task<IActionResult> SaveBackgroundDecoration(IFormFile? file, bool repeat)
        {
            if (file != null && file.Length > 0)
            {
                var err = await _designService.SaveBackgroundDecorationFileAsync(file, repeat);
                if (err == null) TempData["Result"] = "Фон-украшение обновлён";
                else TempData["Error"] = err;
            }
            else
            {
                var settings = await _designService.GetAsync();
                settings.RepeatBackgroundDecoration = repeat;
                await _designService.SaveAsync(settings);
                TempData["Result"] = "Настройки сохранены";
            }
            return RedirectToAction(nameof(Design));
        }

        [HttpPost]
        public async Task<IActionResult> RemoveBackgroundDecoration()
        {
            await _designService.RemoveBackgroundDecorationAsync();
            TempData["Result"] = "Фон-украшение удалён";
            return RedirectToAction(nameof(Design));
        }

        [HttpPost]
        public async Task<IActionResult> UpdatePaletteFromAvatar()
        {
            var error = await _designService.UpdateFromAvatarAsync();
            if (error == null)
                TempData["Result"] = "Палитра обновлена из аватарки сервера";
            else
                TempData["Error"] = error;

            return RedirectToAction(nameof(Design));
        }

        // ====== РАБОТЫ НА ГЛАВНОЙ ======

        [HttpGet]
        public async Task<IActionResult> FeaturedWorks()
        {
            var selected = await GetSelectedOrderedAsync();
            var model = new FeaturedWorksViewModel
            {
                Selected = await BuildFeaturedItemsAsync(selected)
            };
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> SearchFeaturedWorks(
    int offset = 0, int limit = 24,
    string? participantQuery = null, int? animJamNumber = null)
        {
            var baseQuery = _db.Participants.Where(p => !string.IsNullOrEmpty(p.WorkUrl));

            if (animJamNumber.HasValue)
                baseQuery = baseQuery.Where(p => p.AnimJamNumber == animJamNumber.Value);

            if (!string.IsNullOrWhiteSpace(participantQuery))
            {
                var q = participantQuery.Trim();
                var matchedIds = await _db.Participants
                    .Where(p => p.DiscordUserId.ToString().Contains(q)
                             || EF.Functions.ILike(p.DisplayName, $"%{q}%"))
                    .Select(p => p.DiscordUserId)
                    .Distinct()
                    .ToListAsync();

                if (matchedIds.Count == 0)
                    return Json(new { items = new List<object>(), hasMore = false });

                baseQuery = baseQuery.Where(p => matchedIds.Contains(p.DiscordUserId));
            }

            // Тянем в память (объём маленький) → стабильная дедупликация и сортировка
            var all = await baseQuery
                .Select(p => new { p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName })
                .ToListAsync();

            var deduped = all
                .GroupBy(x => x.WorkUrl)
                .Select(g => g.OrderByDescending(x => x.AnimJamNumber)
                              .ThenByDescending(x => x.Id)
                              .First())
                .OrderByDescending(x => x.AnimJamNumber)
                .ThenByDescending(x => x.Id)
                .ToList();

            var page = deduped.Skip(offset).Take(limit + 1).ToList();
            var hasMore = page.Count > limit;
            if (hasMore) page = page.Take(limit).ToList();

            var items = page
                .Select(p => BuildFeaturedItem(p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName, null))
                .ToList();

            return Json(new { items, hasMore });
        }
        [HttpGet]
        public IActionResult GetThumbnails(string ids)
        {
            var result = new Dictionary<int, string?>();
            if (string.IsNullOrWhiteSpace(ids)) return Json(result);

            foreach (var s in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(s, out var id)) continue;
                result[id] = _thumbService.GetUrl(id);
            }
            return Json(result);
        }
        [HttpPost]
        public async Task<IActionResult> GenerateAllThumbnails()
        {
            var all = await _db.Participants
                .Where(p => !string.IsNullOrEmpty(p.WorkUrl))
                .ToListAsync();

            int queued = 0;
            var pending = all
                .Where(p => !_thumbService.HasThumbnail(p.Id))
                .Select(p => new { p.Id, p.WorkUrl, p.MessageUrl, MediaType = DetectMediaType(p.WorkUrl) })
                .Where(x => x.MediaType == "video" || x.MediaType == "image" || x.MediaType == "youtube")
                .ToList();

            _ = Task.Run(async () =>
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var thumb = scope.ServiceProvider.GetRequiredService<FeaturedThumbnailService>();
                foreach (var p in pending)
                {
                    await thumb.EnsureAsync(p.Id, p.WorkUrl, p.MessageUrl, p.MediaType);
                }
            });

            TempData["Result"] = $"Запущена генерация для {pending.Count} работ (в фоне)";
            return RedirectToAction(nameof(FeaturedWorks));

        }

        [HttpGet]
        public async Task<IActionResult> RenderSelectedPartial()
        {
            var selected = await GetSelectedOrderedAsync();
            var items = await BuildFeaturedItemsAsync(selected);
            return PartialView("_FeaturedSelected", items);
        }

        [HttpPost]
        public async Task<IActionResult> AddFeaturedWork(int participantId)
        {
            if (await _db.LandingFeaturedWorks.AnyAsync(f => f.ParticipantId == participantId))
                return Json(new { success = false, error = "Уже добавлено" });

            var maxOrder = await _db.LandingFeaturedWorks.AnyAsync()
                ? await _db.LandingFeaturedWorks.MaxAsync(f => f.Order)
                : 0;

            _db.LandingFeaturedWorks.Add(new LandingFeaturedWork
            {
                ParticipantId = participantId,
                Order = maxOrder + 1
            });
            await _db.SaveChangesAsync();

            var participant = await _db.Participants.FirstOrDefaultAsync(p => p.Id == participantId);
            if (participant != null)
            {
                var mediaType = DetectMediaType(participant.WorkUrl);
                await _thumbService.EnsureAsync(participant.Id, participant.WorkUrl, participant.MessageUrl, mediaType);
            }

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveFeaturedWork(int id)
        {
            var f = await _db.LandingFeaturedWorks.FirstOrDefaultAsync(x => x.Id == id);
            if (f == null) return Json(new { success = false });

            _db.LandingFeaturedWorks.Remove(f);
            await _db.SaveChangesAsync();
            _thumbService.Remove(f.ParticipantId);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> MoveFeaturedWork(int id, int delta)
        {
            var all = await _db.LandingFeaturedWorks.OrderBy(x => x.Order).ToListAsync();
            var idx = all.FindIndex(x => x.Id == id);
            if (idx < 0) return Json(new { success = false });

            var newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= all.Count) return Json(new { success = false });

            var tmp = all[idx].Order;
            all[idx].Order = all[newIdx].Order;
            all[newIdx].Order = tmp;

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ClearFeaturedWorks()
        {
            var all = await _db.LandingFeaturedWorks.ToListAsync();
            _db.LandingFeaturedWorks.RemoveRange(all);
            await _db.SaveChangesAsync();
            _thumbService.ClearAll();
            return Json(new { success = true });
        }
        [HttpPost]
        public async Task<IActionResult> RefreshThumbnails()
        {
            var selected = await GetSelectedOrderedAsync();
            var ids = selected.Select(f => f.ParticipantId).ToList();
            var parts = await _db.Participants.Where(p => ids.Contains(p.Id)).ToListAsync();

            foreach (var p in parts)
            {
                var mediaType = DetectMediaType(p.WorkUrl);
                await _thumbService.EnsureAsync(p.Id, p.WorkUrl, p.MessageUrl, mediaType);
            }

            TempData["Result"] = $"Обложки обновлены ({parts.Count})";
            return RedirectToAction(nameof(FeaturedWorks));
        }

        // --- Хелперы ---
        private static string DetectMediaType(string workUrl)
        {
            if (string.IsNullOrEmpty(workUrl)) return "link";
            var lower = workUrl.ToLowerInvariant();

            if (lower.Contains("youtube.com") || lower.Contains("youtu.be")) return "youtube";
            if (lower.Contains(".mp4") || lower.Contains(".webm") || lower.Contains(".mov")) return "video";
            if (System.Text.RegularExpressions.Regex.IsMatch(workUrl,
                @"\.(gif|png|jpg|jpeg)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "image";
            return "link";
        }

        private async Task<List<LandingFeaturedWork>> GetSelectedOrderedAsync()
        {
            var selected = await _db.LandingFeaturedWorks.OrderBy(f => f.Order).ToListAsync();
            if (selected.Count == 0) return selected;

            var existingIds = (await _db.Participants.Select(p => p.Id).ToListAsync()).ToHashSet();
            var dead = selected.Where(f => !existingIds.Contains(f.ParticipantId)).ToList();
            if (dead.Any())
            {
                _db.LandingFeaturedWorks.RemoveRange(dead);
                await _db.SaveChangesAsync();
                selected = selected.Where(f => existingIds.Contains(f.ParticipantId)).ToList();
            }
            return selected;
        }

        private async Task<List<FeaturedWorkItem>> BuildFeaturedItemsAsync(List<LandingFeaturedWork> selected)
        {
            if (selected.Count == 0) return new();

            var ids = selected.Select(f => f.ParticipantId).ToList();
            var parts = await _db.Participants
                .Where(p => ids.Contains(p.Id))
                .ToListAsync();
            var byId = parts.ToDictionary(p => p.Id);

            var result = new List<FeaturedWorkItem>();
            foreach (var f in selected)
            {
                if (!byId.TryGetValue(f.ParticipantId, out var p)) continue;
                result.Add(BuildFeaturedItem(p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName, f.Id));
            }
            return result;
        }

        private FeaturedWorkItem BuildFeaturedItem(int participantId, int jam, string workUrl,
                                            string messageUrl, string name, int? featuredId)
        {
            var item = new FeaturedWorkItem
            {
                FeaturedId = featuredId,
                ParticipantId = participantId,
                AnimJamNumber = jam,
                WorkUrl = workUrl,
                MessageUrl = messageUrl,
                DisplayName = name
            };

            // 1. Локальная обложка — самая быстрая
            var localThumb = _thumbService.GetUrl(participantId);
            if (!string.IsNullOrEmpty(localThumb))
            {
                item.HasLocalThumbnail = true;
                item.MediaType = "image";
                item.ThumbnailUrl = localThumb;
                return item;
            }

            // 2. YouTube — CDN быстрый
            var ytId = YouTubeHelper.ExtractId(workUrl);
            if (ytId != null)
            {
                item.MediaType = "youtube";
                item.ThumbnailUrl = $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg";
                return item;
            }

            // 3. Видео без обложки — иконка, не грузим /Media/StreamWork
            if (workUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                workUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
                workUrl.Contains(".mov", StringComparison.OrdinalIgnoreCase))
            {
                item.MediaType = "video";
                item.ThumbnailUrl = "";
                return item;
            }

            // 4. Картинки без обложки — иконка (CDN всё равно протух)
            if (System.Text.RegularExpressions.Regex.IsMatch(workUrl,
                @"\.(gif|png|jpg|jpeg)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                item.MediaType = "image";
                item.ThumbnailUrl = "";
                return item;
            }

            item.MediaType = "link";
            item.ThumbnailUrl = "";
            return item;
        }

        private static List<List<string>> BuildPollChunks(List<string> options)
        {
            // До 10 вариантов — один опрос без служебного
            if (options.Count <= 10)
                return new List<List<string>> { new List<string>(options) };

            var chunks = new List<List<string>>();

            // Первый опрос: 9 обычных + служебный в конце
            var first = options.Take(9).ToList();
            first.Add(AnotherPollOption);
            chunks.Add(first);

            int idx = 9;
            while (idx < options.Count)
            {
                var chunk = new List<string> { AnotherPollOption };
                var take = Math.Min(9, options.Count - idx);
                chunk.AddRange(options.Skip(idx).Take(take));
                chunks.Add(chunk);
                idx += take;
            }

            return chunks;
        }
        // === МОДЕРАЦИЯ РАБОТ АНИДЖЕМА ===

        [HttpPost]
        public async Task<IActionResult> DeleteParticipant(int id, int animJamNumber)
        {
            var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return Json(new { success = false, error = "Запись не найдена" });

            if (p.AnimJamNumber != animJamNumber)
                return Json(new { success = false, error = "Неверный номер аниджема" });

            // Удаляем обложку
            _thumbService.Remove(p.Id);

            _db.Participants.Remove(p);
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> AddParticipantToJam(
    int animJamNumber, ulong discordId, string workUrl)
        {
            if (animJamNumber <= 0)
                return Json(new { success = false, error = "Неверный номер аниджема" });

            if (discordId == 0)
                return Json(new { success = false, error = "Укажите Discord ID" });

            if (string.IsNullOrWhiteSpace(workUrl))
                return Json(new { success = false, error = "Укажите ссылку на работу" });

            var input = workUrl.Trim();

            string finalWorkUrl = input;
            string finalMessageUrl = "";
            string mediaType = "link";

            if (YouTubeHelper.IsYouTubeUrl(input))
            {
                mediaType = "youtube";
            }
            else if (input.Contains("discord.com/channels/"))
            {
                var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(input);
                if (chId == null || msgId == null)
                    return Json(new { success = false, error = "Не удалось разобрать Discord-ссылку" });

                var fresh = await _discord.GetFreshAttachmentUrlAsync(chId.Value, msgId.Value);
                if (string.IsNullOrEmpty(fresh))
                    return Json(new { success = false, error = "В сообщении нет вложения (или оно недоступно)" });

                finalMessageUrl = input;
                finalWorkUrl = fresh;

                var lower = fresh.ToLowerInvariant();
                if (lower.Contains(".mp4") || lower.Contains(".webm") || lower.Contains(".mov"))
                    mediaType = "video";
                else if (System.Text.RegularExpressions.Regex.IsMatch(fresh, @"\.(gif|png|jpg|jpeg)(\?|$)",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    mediaType = "image";
            }
            else
            {
                return Json(new { success = false, error = "Поддерживаются только YouTube и ссылки на сообщения Discord" });
            }

            // Имя: Discord → последнее из БД → заглушка
            string finalName = "";
            var guild = _discordClient.Guilds.Values.FirstOrDefault();
            if (guild != null)
            {
                try
                {
                    var member = await guild.GetMemberAsync(discordId);
                    if (member != null) finalName = member.DisplayName ?? member.Username ?? "";
                }
                catch { }
            }

            if (string.IsNullOrEmpty(finalName))
            {
                finalName = await _db.Participants
                    .Where(p => p.DiscordUserId == discordId)
                    .OrderByDescending(p => p.AnimJamNumber)
                    .Select(p => p.DisplayName)
                    .FirstOrDefaultAsync() ?? "";
            }

            if (string.IsNullOrEmpty(finalName))
                finalName = "[аккаунт удалён]";

            // Дубликат?
            var exists = await _db.Participants.AnyAsync(p =>
                p.AnimJamNumber == animJamNumber &&
                p.DiscordUserId == discordId &&
                p.WorkUrl == finalWorkUrl);

            if (exists)
                return Json(new { success = false, error = "Такая работа уже есть в аниджеме" });

            var participant = new Participant
            {
                AnimJamNumber = animJamNumber,
                DiscordUserId = discordId,
                DisplayName = finalName,
                WorkUrl = finalWorkUrl,
                MessageUrl = finalMessageUrl
            };

            _db.Participants.Add(participant);
            await _db.SaveChangesAsync();

            var pid = participant.Id;
            var mt = mediaType;
            _ = Task.Run(() => _thumbService.EnsureAsync(pid, finalWorkUrl, finalMessageUrl, mt));

            return Json(new { success = true, id = participant.Id });
        }



        // ===== РЕДАКТОР АНИДЖЕМА =====

        [HttpGet]
        public async Task<IActionResult> EditAnimJam(int number)
        {
            var jam = await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == number);
            if (jam == null) return NotFound();

            var participants = await _db.Participants
                .Where(p => p.AnimJamNumber == number)
                .ToListAsync();

            var winners = await _db.Winners
                .Where(w => w.AnimJamNumber == number)
                .ToListAsync();

            // Группируем по WorkUrl (пустые — каждый отдельно)
            var groups = participants
                .GroupBy(p => string.IsNullOrEmpty(p.WorkUrl) ? $"__empty_{p.Id}" : p.WorkUrl)
                .ToList();

            var model = new AnimJamEditPageViewModel
            {
                Number = number,
                Theme = jam.Theme,
                StartDate = jam.StartDate,
                EndDate = jam.EndDate,
                GuildMembers = _discord.GetAllGuildMembers()
                    .Select(m => new GuildMemberOption { Id = m.Id, Name = m.Name })
                    .ToList()

            };

            foreach (var group in groups)
            {
                var first = group.First();
                var workUrl = first.WorkUrl;

                var nomination = NominationEditType.Participant;
                if (!string.IsNullOrEmpty(workUrl))
                {
                    var workWinners = winners.Where(w => w.WorkUrl == workUrl).ToList();

                    bool hasIdea = workWinners.Any(w => w.Nomination == NominationType.Idea);
                    bool hasTech = workWinners.Any(w => w.Nomination == NominationType.Technical);
                    bool hasWinner = workWinners.Any(w => w.Nomination == NominationType.Winner);
                    bool hasHonor = workWinners.Any(w => w.Nomination == NominationType.HonorableMention);

                    if (hasIdea && hasTech) nomination = NominationEditType.Absolute;
                    else if (hasWinner) nomination = NominationEditType.Winner;
                    else if (hasIdea) nomination = NominationEditType.Idea;
                    else if (hasTech) nomination = NominationEditType.Technical;
                    else if (hasHonor) nomination = NominationEditType.HonorableMention;
                }

                model.Works.Add(new EditWorkItem
                {
                    GroupKey = first.Id,
                    WorkUrl = workUrl,
                    MessageUrl = first.MessageUrl,
                    Nomination = nomination,
                    People = group
                        .GroupBy(p => p.DiscordUserId)
                        .Select(g => new EditPerson
                        {
                            DiscordUserId = g.Key,
                            DisplayName = g.First().DisplayName
                        })
                        .OrderBy(p => p.DisplayName)
                        .ToList()
                });
            }

            // Сортировка работ по имени первого участника
            model.Works = model.Works
                .OrderBy(w => w.People.FirstOrDefault()?.DisplayName ?? "")
                .ToList();


            // Уникальные участники из БД (последнее имя по каждому ID)
            var dbMembers = await _db.Participants
                .GroupBy(p => p.DiscordUserId)
                .Select(g => new
                {
                    Id = g.Key,
                    Name = g.OrderByDescending(p => p.AnimJamNumber)
                            .Select(p => p.DisplayName)
                            .FirstOrDefault()
                })
                .ToListAsync();

            model.DbMembers = dbMembers
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => new DbMemberOption { Id = m.Id, Name = m.Name })
                .OrderBy(m => m.Name)
                .ToList();

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveWork(
            int animJamNumber,
            int groupKey,
            string? workUrl,
            string? messageUrl,
            NominationEditType nomination,
            [FromForm] List<EditPerson> people)
        {
            if (animJamNumber <= 0)
                return Json(new { success = false, error = "Неверный номер аниджема" });

            people ??= new List<EditPerson>();
            var validPeople = new List<EditPerson>();
            var guild = _discordClient.Guilds.Values.FirstOrDefault();

            foreach (var person in people)
            {
                ulong id = person.DiscordUserId;
                string name = (person.DisplayName ?? "").Trim();

                // 1. ID есть → ник из Discord (приоритет ID)
                if (id != 0)
                {
                    string? discordName = null;
                    if (guild != null)
                    {
                        try
                        {
                            var member = await guild.GetMemberAsync(id);
                            if (member != null)
                                discordName = member.DisplayName ?? member.Username;
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(discordName))
                        name = discordName;
                    else if (string.IsNullOrEmpty(name))
                    {
                        // Аккаунт удалён / вышел — берём последний из БД
                        name = await _db.Participants
                            .Where(p => p.DiscordUserId == id)
                            .OrderByDescending(p => p.AnimJamNumber)
                            .Select(p => p.DisplayName)
                            .FirstOrDefaultAsync() ?? "";
                    }
                }
                // 2. ID нет, ник есть → ищем ID в БД
                else if (!string.IsNullOrEmpty(name))
                {
                    id = await _db.Participants
                        .Where(p => p.DisplayName.ToLower() == name.ToLower())
                        .OrderByDescending(p => p.AnimJamNumber)
                        .Select(p => p.DiscordUserId)
                        .FirstOrDefaultAsync();
                }

                if (id == 0 || string.IsNullOrEmpty(name))
                    continue;

                validPeople.Add(new EditPerson
                {
                    DiscordUserId = id,
                    DisplayName = name
                });
            }

            // Удалить дубликаты по ID (если модератор случайно добавил дважды)
            validPeople = validPeople
                .GroupBy(p => p.DiscordUserId)
                .Select(g => g.First())
                .ToList();

            if (validPeople.Count == 0)
                return Json(new { success = false, error = "Не удалось определить участников. Укажите ID или ник из базы." });

            // === Находим старую группу (если редактирование) ===
            List<Participant> oldGroup = new();
            string oldWorkUrl = "";

            if (groupKey > 0)
            {
                var anchor = await _db.Participants.FirstOrDefaultAsync(p => p.Id == groupKey);
                if (anchor == null)
                    return Json(new { success = false, error = "Работа не найдена" });

                oldWorkUrl = anchor.WorkUrl;

                if (!string.IsNullOrEmpty(oldWorkUrl))
                {
                    oldGroup = await _db.Participants
                        .Where(p => p.AnimJamNumber == animJamNumber && p.WorkUrl == oldWorkUrl)
                        .ToListAsync();
                }
                else
                {
                    oldGroup = new List<Participant> { anchor };
                }
            }

            // === Определяем финальные WorkUrl и MessageUrl ===
            string finalWorkUrl = (workUrl ?? "").Trim();
            string finalMessageUrl = (messageUrl ?? "").Trim();

            // Если указана Discord-ссылка — вытаскиваем CDN (если WorkUrl пуст или это просто тот же message)
            if (!string.IsNullOrEmpty(finalMessageUrl) && finalMessageUrl.Contains("discord.com/channels/"))
            {
                var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(finalMessageUrl);
                if (chId != null && msgId != null)
                {
                    var fresh = await _discord.GetFreshAttachmentUrlAsync(chId.Value, msgId.Value);
                    if (!string.IsNullOrEmpty(fresh))
                        finalWorkUrl = fresh;
                }
            }

            // === Удаляем старую группу + Winners + обложки ===
            if (oldGroup.Count > 0)
            {
                if (!string.IsNullOrEmpty(oldWorkUrl))
                {
                    var oldWinners = await _db.Winners
                        .Where(w => w.AnimJamNumber == animJamNumber && w.WorkUrl == oldWorkUrl)
                        .ToListAsync();
                    _db.Winners.RemoveRange(oldWinners);
                }

                foreach (var p in oldGroup)
                    _thumbService.Remove(p.Id);

                _db.Participants.RemoveRange(oldGroup);
            }

            // === Создаём новых Participant ===
            var newParticipants = validPeople.Select(person => new Participant
            {
                AnimJamNumber = animJamNumber,
                DiscordUserId = person.DiscordUserId,
                DisplayName = person.DisplayName.Trim(),
                WorkUrl = finalWorkUrl,
                MessageUrl = finalMessageUrl
            }).ToList();

            _db.Participants.AddRange(newParticipants);
            await _db.SaveChangesAsync();

            // === Создаём Winners согласно номинации ===
            if (nomination != NominationEditType.Participant && !string.IsNullOrEmpty(finalWorkUrl))
            {
                var newWinners = new List<Winner>();
                foreach (var p in newParticipants)
                {
                    switch (nomination)
                    {
                        case NominationEditType.Idea:
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.Idea, WorkUrl = finalWorkUrl });
                            break;
                        case NominationEditType.Technical:
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.Technical, WorkUrl = finalWorkUrl });
                            break;
                        case NominationEditType.Winner:
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.Winner, WorkUrl = finalWorkUrl });
                            break;
                        case NominationEditType.Absolute:
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.Idea, WorkUrl = finalWorkUrl });
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.Technical, WorkUrl = finalWorkUrl });
                            break;
                        case NominationEditType.HonorableMention:
                            newWinners.Add(new Winner { AnimJamNumber = animJamNumber, DiscordUserId = p.DiscordUserId, Nomination = NominationType.HonorableMention, WorkUrl = finalWorkUrl });
                            break;
                    }
                }
                _db.Winners.AddRange(newWinners);
                await _db.SaveChangesAsync();
            }

            // === Обложки в фоне ===
            if (!string.IsNullOrEmpty(finalWorkUrl))
            {
                var mediaType = DetectMediaType(finalWorkUrl);
                foreach (var p in newParticipants)
                {
                    var pid = p.Id;
                    var wu = finalWorkUrl;
                    var mu = finalMessageUrl;
                    var mt = mediaType;
                    _ = Task.Run(() => _thumbService.EnsureAsync(pid, wu, mu, mt));
                }
            }

            return Json(new { success = true });
        }


        [HttpPost]
        public async Task<IActionResult> DeleteAnimJam(int number)
        {
            var jam = await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == number);
            if (jam == null)
                return Json(new { success = false, error = "Аниджем не найден" });

            // Удаляем всё связанное
            var participants = await _db.Participants
                .Where(p => p.AnimJamNumber == number).ToListAsync();
            var winners = await _db.Winners
                .Where(w => w.AnimJamNumber == number).ToListAsync();
            var themePolls = await _db.ThemePolls
                .Where(t => t.AnimJamNumber == number).ToListAsync();
            var winnerPolls = await _db.WinnerPolls
                .Where(w => w.AnimJamNumber == number).ToListAsync();

            // Медиа-обложки
            foreach (var p in participants)
                _thumbService.Remove(p.Id);

            // LandingFeaturedWorks: записи с participantId из этого аниджема
            var participantIds = participants.Select(p => p.Id).ToList();
            var featured = await _db.LandingFeaturedWorks
                .Where(f => participantIds.Contains(f.ParticipantId)).ToListAsync();

            _db.LandingFeaturedWorks.RemoveRange(featured);
            _db.Winners.RemoveRange(winners);
            _db.WinnerPolls.RemoveRange(winnerPolls);
            _db.ThemePolls.RemoveRange(themePolls);
            _db.Participants.RemoveRange(participants);
            _db.AnimJams.Remove(jam);

            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteWork(int animJamNumber, int groupKey)
        {
            var anchor = await _db.Participants.FirstOrDefaultAsync(p => p.Id == groupKey);
            if (anchor == null)
                return Json(new { success = false, error = "Работа не найдена" });

            var oldWorkUrl = anchor.WorkUrl;

            List<Participant> group;
            if (!string.IsNullOrEmpty(oldWorkUrl))
            {
                group = await _db.Participants
                    .Where(p => p.AnimJamNumber == animJamNumber && p.WorkUrl == oldWorkUrl)
                    .ToListAsync();
            }
            else
            {
                group = new List<Participant> { anchor };
            }

            if (!string.IsNullOrEmpty(oldWorkUrl))
            {
                var winners = await _db.Winners
                    .Where(w => w.AnimJamNumber == animJamNumber && w.WorkUrl == oldWorkUrl)
                    .ToListAsync();
                _db.Winners.RemoveRange(winners);
            }

            foreach (var p in group)
                _thumbService.Remove(p.Id);

            _db.Participants.RemoveRange(group);
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> SaveAnimJamMeta(
                int number,
                int newNumber,
                string theme,
                string startDateUtc,
                string endDateUtc)
        {
            var jam = await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == number);
            if (jam == null)
                return Json(new { success = false, error = "Аниджем не найден" });

            if (newNumber <= 0)
                return Json(new { success = false, error = "Номер должен быть положительным" });

            if (newNumber != number)
            {
                var conflict = await _db.AnimJams.AnyAsync(j => j.Number == newNumber);
                if (conflict)
                    return Json(new { success = false, error = $"Аниджем #{newNumber} уже существует" });
            }

            // Даты
            if (!DateTime.TryParse(startDateUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                return Json(new { success = false, error = "Неверная дата начала" });

            if (!DateTime.TryParse(endDateUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                return Json(new { success = false, error = "Неверная дата окончания" });

            if (end <= start)
                return Json(new { success = false, error = "Дата окончания должна быть позже начала" });

            // Смена номера — каскадно по всем связанным таблицам
            if (newNumber != number)
            {
                await _db.Participants
                    .Where(p => p.AnimJamNumber == number)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.AnimJamNumber, newNumber));

                await _db.Winners
                    .Where(w => w.AnimJamNumber == number)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.AnimJamNumber, newNumber));

                await _db.ThemePolls
                    .Where(t => t.AnimJamNumber == number)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AnimJamNumber, newNumber));

                await _db.WinnerPolls
                    .Where(w => w.AnimJamNumber == number)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.AnimJamNumber, newNumber));

                jam.Number = newNumber;
            }

            jam.Theme = theme?.Trim() ?? "";
            jam.StartDate = DateTime.SpecifyKind(start.ToUniversalTime(), DateTimeKind.Utc);
            jam.EndDate = DateTime.SpecifyKind(end.ToUniversalTime(), DateTimeKind.Utc);

            await _db.SaveChangesAsync();

            return Json(new { success = true, newNumber = jam.Number });
        }

        [HttpPost]
        public async Task<IActionResult> CreateAnimJamQuick()
        {
            int nextNumber = _db.AnimJams.Any()
                ? await _db.AnimJams.MaxAsync(j => j.Number) + 1
                : 1;

            var jam = new AnimJam
            {
                Number = nextNumber,
                Theme = "Новый аниджем",
                StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
                EndDate = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(7), DateTimeKind.Utc)
            };

            _db.AnimJams.Add(jam);
            await _db.SaveChangesAsync();

            TempData["Result"] = $"Аниджем #{nextNumber} создан. Заполните данные в редакторе.";
            return RedirectToAction("EditAnimJam", "Moderator", new { number = nextNumber });
        }

    }

}