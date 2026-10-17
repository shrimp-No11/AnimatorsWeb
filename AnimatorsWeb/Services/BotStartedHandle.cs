using DSharpPlus;
using DSharpPlus.EventArgs;

public class BotStartedHandler : IEventHandler<ClientStartedEventArgs>
{
    private readonly DiscordClient _client;

    public BotStartedHandler(DiscordClient client)
    {
        _client = client;
    }

    public Task HandleEventAsync(DiscordClient sender, ClientStartedEventArgs eventArgs)
    {
        Console.WriteLine($"Бот {sender.CurrentUser?.Username} в сети!");
        return Task.CompletedTask;
    }
}