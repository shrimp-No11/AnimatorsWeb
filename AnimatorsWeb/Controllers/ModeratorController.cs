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
        const ulong role2dId = 1126390388006072380UL; // ← сюда ID роли 2d Аниматор
        const ulong role3dId = 1536428705105051808UL; // ← сюда ID роли 3d Аниматор

        public ModeratorController(
                DiscordClient discordClient,
                AppDbContext db,
                DiscordMessageService discord,
                ChannelSettingsService settingsService,
                RulesService rulesService)
        {
            _discordClient = discordClient;
            _db = db;
            _discord = discord;
            _settingsService = settingsService;
            _rulesService = rulesService;
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
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SendAnnouncement(ulong selectedChannelId, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                TempData["Error"] = "Текст пустой!";
                return RedirectToAction("SendMessage");
            }

            await _discord.SendMessageAsync(selectedChannelId, text);
            TempData["Result"] = "Сообщение отправлено!";
            return RedirectToAction("SendMessage");
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

            // Используем REST-метод вместо старого GetWinningThemeViaRestAsync
            var winner = await _discord.GetWinningThemeViaRestAsync(poll.ChannelId, poll.MessageId);

            if (winner == null)
            {
                model.PollError = "Опрос ещё идёт или ничья";
                return;
            }

            model.WinningThemeFromPoll = winner;
            if (string.IsNullOrWhiteSpace(model.Theme))
                model.Theme = winner;
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

            var publishedPolls = new List<WinnerPoll>();

            if (model.Mode == WinnerPollMode.Combined)
            {
                var messageId = await _discord.SendCustomPollAsync(
                    model.ChannelId, model.PollTitle, optionTexts, model.DurationHours);

                publishedPolls.Add(new WinnerPoll
                {
                    AnimJamNumber = model.AnimJamNumber,
                    Nomination = NominationType.Winner,
                    ChannelId = model.ChannelId,
                    MessageId = messageId,
                    CreatedAt = DateTime.UtcNow,
                    OptionsJson = optionsJson
                });
            }
            else
            {
                var ideaMsgId = await _discord.SendCustomPollAsync(
                    model.ChannelId, model.IdeaTitle, optionTexts, model.DurationHours);
                var techMsgId = await _discord.SendCustomPollAsync(
                    model.ChannelId, model.TechnicalTitle, optionTexts, model.DurationHours);

                publishedPolls.Add(new WinnerPoll
                {
                    AnimJamNumber = model.AnimJamNumber,
                    Nomination = NominationType.Idea,
                    ChannelId = model.ChannelId,
                    MessageId = ideaMsgId,
                    CreatedAt = DateTime.UtcNow,
                    OptionsJson = optionsJson
                });
                publishedPolls.Add(new WinnerPoll
                {
                    AnimJamNumber = model.AnimJamNumber,
                    Nomination = NominationType.Technical,
                    ChannelId = model.ChannelId,
                    MessageId = techMsgId,
                    CreatedAt = DateTime.UtcNow,
                    OptionsJson = optionsJson
                });
            }

            // Удаляем старые опросы для этого аниджема и номинации
            var nominationTypes = publishedPolls.Select(p => p.Nomination).ToList();
            var oldPolls = await _db.WinnerPolls
                .Where(p => p.AnimJamNumber == model.AnimJamNumber && nominationTypes.Contains(p.Nomination))
                .ToListAsync();
            _db.WinnerPolls.RemoveRange(oldPolls);

            _db.WinnerPolls.AddRange(publishedPolls);
            await _db.SaveChangesAsync();

            TempData["Result"] = model.Mode == WinnerPollMode.Combined
                ? "Опрос опубликован!"
                : "Опубликованы два опроса: за лучшую идею и за техническое исполнение!";

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

            var polls = await _db.WinnerPolls
                .Where(p => p.AnimJamNumber == jam.Number)
                .ToListAsync();

            if (polls.Count > 0)
                model.PublishChannelId = polls.First().ChannelId;
            else
                model.PublishChannelId = settings.WinnerPollChannelId
                                      ?? settings.WinnersAnnouncementChannelId
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
                    var winners = await DetermineWinnersAsync(poll, participants);
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

        private async Task<List<WinnerTeam>> DetermineWinnersAsync(
    WinnerPoll poll, List<ParticipantInfo> participants)
{
    // Опции из БД (сохранены при публикации)
    var savedTeams = string.IsNullOrEmpty(poll.OptionsJson)
        ? new List<PollTeam>()
        : System.Text.Json.JsonSerializer.Deserialize<List<PollTeam>>(poll.OptionsJson)
          ?? new List<PollTeam>();

    var result = new List<WinnerTeam>();

    var pollResults = await _discord.GetPollResultsAsync(poll.ChannelId, poll.MessageId);
    if (pollResults == null || pollResults.Answers.Count == 0) return result;

    // Одиночный вариант — он победитель
    if (pollResults.Answers.Count == 1)
    {
        var single = pollResults.Answers[0];
        return new List<WinnerTeam> { BuildTeamFromOption(single.Text, savedTeams, participants) };
    }

    // Считаем голоса с поправкой на самоголосование
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

    var winnerNames = adjusted
        .Where(a => a.Count == max)
        .Select(a => a.Text)
        .ToList();

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

            TempData["Result"] = "Объявление победителей опубликовано!";
            return RedirectToAction("Index");
        }
  
        private List<WinnerMessage> BuildWinnerMessages(
    WinnersPageViewModel model, List<ParticipantInfo> participants)
        {
            var messages = new List<WinnerMessage>();

            // 1. Информация об аниджеме
            var info = new System.Text.StringBuilder();
            info.AppendLine($"╠══════════ AniJam {model.AnimJamNumber} ══════════╣");
            info.AppendLine($"                    {model.Theme}");
            info.AppendLine();
            if (!string.IsNullOrWhiteSpace(model.AnnouncementUrl))
                info.AppendLine($"[остальные анимации]({model.AnnouncementUrl})");

            messages.Add(new WinnerMessage { Content = info.ToString().TrimEnd() });

            // 2. Команды, победившие в обеих номинациях
            var combinedTeams = new List<WinnerTeam>();

            if (model.Mode == WinnerPollMode.TwoNominations && model.Sections.Count == 2)
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
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveRules(RulesViewModel model)
        {
            await _rulesService.SaveAsync(model.Settings);
            TempData["Result"] = "Правила сохранены и опубликованы";
            return RedirectToAction("Rules");
        }
       

        private static string FormatNames(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} и {names[1]}";
            return string.Join(", ", names.Take(names.Count - 1)) + " и " + names.Last();
        }

    }

}