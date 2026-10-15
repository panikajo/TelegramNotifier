namespace TelegramNotifier;

public sealed class TelegramNotifierConfig
{
    public int Version { get; set; } = 3;
    public string BotToken { get; set; } = string.Empty;
    public string Language { get; set; } = "uk";
    public ServerStatusConfig ServerStatus { get; set; } = new();
    public PlayerConnectionConfig PlayerConnections { get; set; } = new();
}

public class TelegramDestinationConfig
{
    public bool Enabled { get; set; } = true;
    public long ChatId { get; set; }
    public int MessageThreadId { get; set; }
    public bool DisableNotification { get; set; } = true;
}

public sealed class ServerStatusConfig : TelegramDestinationConfig
{
    // Status only: minimum 1 minute, maximum 1440 minutes.
    public int SendIntervalMinutes { get; set; } = 5;
    public bool SendUnchangedPeriodicStatus { get; set; } = false;
}

public sealed class PlayerConnectionConfig : TelegramDestinationConfig
{
    public bool IgnoreBots { get; set; } = true;
}
