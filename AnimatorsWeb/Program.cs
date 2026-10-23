using AnimatorsWeb.Data;
using AnimatorsWeb.Services;
using AspNet.Security.OAuth.Discord;
using DSharpPlus;
using DSharpPlus.Extensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var token = builder.Configuration["Discord:BotToken"]
    ?? throw new InvalidOperationException("Токен бота не найден");


// Регистрируем фоновый сервис, который будет использовать клиент
builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddScoped<DiscordMessageService>();
builder.Services.AddDiscordClient(
    token,
    DiscordIntents.AllUnprivileged
        | DiscordIntents.MessageContents
        | DiscordIntents.GuildMembers
        | DiscordIntents.GuildMessageReactions
);

builder.Services.ConfigureEventHandlers(handling => handling
    .AddEventHandlers<BotStartedHandler>(ServiceLifetime.Singleton)
    .AddEventHandlers<RulesReactionHandler>(ServiceLifetime.Transient)
);

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = DiscordAuthenticationDefaults.AuthenticationScheme;
})
    .AddCookie()
    .AddDiscord(options =>
    {
        options.ClientId = builder.Configuration["Discord:ClientId"];
        options.ClientSecret = builder.Configuration["Discord:ClientSecret"];
        options.Scope.Add("identify");
        options.Scope.Add("guilds.members.read"); // Для проверки ролей
        options.SaveTokens = true; // Сохраняем токен, он понадобится для запросов к API
        options.Events.OnCreatingTicket = async context =>
        {
            const ulong guildId = 1007550083627233290;
            const ulong presidentRoleId = 1007550169526587392;
            const ulong assistantRoleId = 1325850174194581556;

            var http = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", context.AccessToken);

            var response = await http.GetAsync($"https://discord.com/api/v10/users/@me/guilds/{guildId}/member");

            if (!response.IsSuccessStatusCode)
            {
                // Пользователь не на сервере или токен не даёт доступ
                context.Identity.AddClaim(new Claim("is_moderator", "false"));
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var roles = doc.RootElement.GetProperty("roles");

            bool isModerator = false;
            foreach (var role in roles.EnumerateArray())
            {
                var roleId = ulong.Parse(role.GetString());
                if (roleId == presidentRoleId || roleId == assistantRoleId)
                {
                    isModerator = true;
                    break;
                }
            }

            context.Identity.AddClaim(new Claim("is_moderator", isModerator ? "true" : "false"));
            var userId = context.User.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(userId))
                context.Identity.AddClaim(new Claim("discord_id", userId));
            context.Identity.AddClaim(new Claim(ClaimTypes.Name, context.User.GetProperty("username").GetString()));
        };
    });
// настройка политики
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ModeratorOnly", policy =>
        policy.RequireClaim("is_moderator", "true"));
});

builder.Services.AddHttpClient();
builder.Services.AddScoped<ChannelSettingsService>();
builder.Services.AddScoped<RulesService>();
builder.Services.ConfigureEventHandlers(handling => handling
    .AddEventHandlers<BotStartedHandler>(ServiceLifetime.Singleton)
    .AddEventHandlers<RulesReactionHandler>(ServiceLifetime.Transient)
);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<PaletteExtractionService>();
builder.Services.AddScoped<DesignSettingsService>();
builder.Services.AddSingleton<FeaturedThumbnailService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.UseStaticFiles();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
