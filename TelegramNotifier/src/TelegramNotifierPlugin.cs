using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace TelegramNotifier;

[PluginMetadata(
    Id = "TelegramNotifier",
    Version = "1.1.1",
    Name = "TelegramNotifier",
    Author = "OpenAI for SwiftlyS2",
    Description = "Sends CS2 server status and player connection notifications to Telegram.")]
public sealed class TelegramNotifierPlugin : BasePlugin
{
    private const string ConfigFile = "telegram_notifier.jsonc";
    private const string ConfigSection = "TelegramNotifier";

    private readonly Func<ILogger,TelegramApiClient> _clientFactory;
    private StatusDelivery _statusDelivery = new();
    private bool _loaded;
    private readonly HashSet<ulong> _notifiedSteamIds = [];
    private readonly Dictionary<int, ulong> _steamIdByPlayerId = [];
    private TelegramNotifierConfig _config = new();
    private TranslationCatalog _translations = new();
    private TelegramApiClient? _telegram;
    private CancellationTokenSource? _statusTimer;
    private CancellationTokenSource _lifetime = new();
    private string _currentMap = string.Empty;

    public TelegramNotifierPlugin(ISwiftlyCore core) : this(core,logger=>new TelegramApiClient(logger)) { }

    internal TelegramNotifierPlugin(ISwiftlyCore core,Func<ILogger,TelegramApiClient> clientFactory) : base(core)
    {
        _clientFactory=clientFactory;
    }

    public override void Load(bool hotReload)
    {
        Unload();
        LoadConfiguration();
        _translations = TranslationCatalog.Load(Core.PluginPath, _config.Language, Core.Logger);
        _telegram = _clientFactory(Core.Logger);
        _statusDelivery = new StatusDelivery();
        _telegram.SetToken(_config.BotToken);
        _currentMap = ReadCurrentMap();

        Core.Event.OnClientSteamAuthorize += OnClientSteamAuthorize;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapLoad += OnMapLoad;

        _loaded = true;
        StartStatusTimer();
        RequestStatusUpdate(force: true);

        if (string.IsNullOrWhiteSpace(_config.BotToken))
            Core.Logger.LogWarning("[TelegramNotifier] BotToken is empty. Configure {ConfigFile}.", ConfigFile);
        else
            Core.Logger.LogInformation("[TelegramNotifier] Plugin loaded successfully.");
    }

    public override void Unload()
    {
        _loaded = false;
        Core.Event.OnClientSteamAuthorize -= OnClientSteamAuthorize;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapLoad -= OnMapLoad;

        _statusTimer?.Cancel();
        _statusTimer?.Dispose();
        _statusTimer = null;

        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();

        _telegram?.Dispose();
        _telegram = null;
        _notifiedSteamIds.Clear();
        _steamIdByPlayerId.Clear();
    }

    private void LoadConfiguration()
    {
        Core.Configuration.InitializeJsonWithModel<TelegramNotifierConfig>(ConfigFile,ConfigSection);
        var path=Core.Configuration.GetConfigPath(ConfigFile);
        var options=new JsonSerializerOptions
        { PropertyNameCaseInsensitive=true,ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true };
        using var document=JsonDocument.Parse(File.ReadAllText(path),new JsonDocumentOptions
        { CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true });
        var section=document.RootElement;
        if (TryProperty(section,ConfigSection,out var wrapped)) section=wrapped;
        _config=section.Deserialize<TelegramNotifierConfig>(options) ?? throw new InvalidDataException("Invalid TelegramNotifier configuration.");
        _config.ServerStatus??=new ServerStatusConfig();
        _config.PlayerConnections??=new PlayerConnectionConfig();
        // Legacy config remains usable. Explicit minutes always take precedence.
        if (TryProperty(section,"ServerStatus",out var status) && status.ValueKind==JsonValueKind.Object &&
            !TryProperty(status,"SendIntervalMinutes",out _) && TryProperty(status,"SendIntervalSeconds",out var old) && old.ValueKind==JsonValueKind.Number && old.TryGetInt32(out var seconds))
        {
            _config.ServerStatus.SendIntervalMinutes=(int)Math.Ceiling(Math.Clamp(seconds,1,86400)/60d);
            Core.Logger.LogInformation("[TelegramNotifier] Legacy SendIntervalSeconds read as {Minutes} minute(s). Add ServerStatus.SendIntervalMinutes to the existing config.",_config.ServerStatus.SendIntervalMinutes);
        }
        _config.ServerStatus.SendIntervalMinutes=Math.Clamp(_config.ServerStatus.SendIntervalMinutes,1,1440);
    }

    private static bool TryProperty(JsonElement element,string name,out JsonElement value)
    {
        if (element.ValueKind==JsonValueKind.Object)
            foreach(var property in element.EnumerateObject())
                if(property.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) { value=property.Value; return true; }
        value=default; return false;
    }

    private void StartStatusTimer()
    {
        if (!_config.ServerStatus.Enabled) return;
        var delivery=_statusDelivery;
        var seconds=_config.ServerStatus.SendIntervalMinutes*60f;
        _statusTimer=Core.Scheduler.DelayAndRepeatBySeconds(seconds,seconds,()=>
        {
            if (_loaded && ReferenceEquals(delivery,_statusDelivery))
                RequestStatusUpdate(_config.ServerStatus.SendUnchangedPeriodicStatus);
        });
        Core.Logger.LogInformation("[TelegramNotifier] Server status interval: {Minutes} minute(s). Player-count and map events do not bypass this timer.",_config.ServerStatus.SendIntervalMinutes);
    }

    private void OnClientSteamAuthorize(IOnClientSteamAuthorizeEvent @event)
    {
        if (!_loaded) return;
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player is null)
            return;

        if (_config.PlayerConnections.IgnoreBots && player.IsFakeClient)
            return;

        if (player.SteamID == 0 || !_notifiedSteamIds.Add(player.SteamID))
            return;

        _steamIdByPlayerId[player.PlayerID] = player.SteamID;
        var message = FormatPlayerMessage(player);
        _ = SendPlayerMessageAsync(message, _lifetime.Token);

    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        if (!_loaded) return;
        if (_steamIdByPlayerId.Remove(@event.PlayerId, out var recordedSteamId))
            _notifiedSteamIds.Remove(recordedSteamId);

        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player is not null)
        {
            _notifiedSteamIds.Remove(player.SteamID);
            _notifiedSteamIds.Remove(player.UnauthorizedSteamID);
        }

    }

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        if (!_loaded) return;
        // The next scheduled status contains the latest map and online count.
        _currentMap = @event.MapName;
    }

    private void RequestStatusUpdate(bool force)
    {
        if (!_loaded || !_config.ServerStatus.Enabled || _telegram is null) return;
        var text=FormatStatusMessage(); // Game objects are read only on the game thread.
        var client=_telegram;
        var destination=_config.ServerStatus;
        var token=_lifetime.Token;
        _ = _statusDelivery.SendAsync(text,force,ct=>client.SendMessageAsync(destination,text,ct),token);
    }

    private Task SendPlayerMessageAsync(string message, CancellationToken cancellationToken) =>
        _telegram?.SendMessageAsync(_config.PlayerConnections, message, cancellationToken)
        ?? Task.FromResult(false);

    private string FormatStatusMessage()
    {
        var hostname = Core.ConVar.FindAsString("hostname")?.ValueAsString ?? "CS2 Server";
        var current = Core.PlayerManager.GetAllValidPlayers().Count(player => !player.IsFakeClient);
        var maximum = Core.PlayerManager.MaxPlayers;
        var map = string.IsNullOrWhiteSpace(_currentMap) ? ReadCurrentMap() : _currentMap;

        return _translations.ServerStatus
            .Replace("{HOSTNAME}", WebUtility.HtmlEncode(hostname), StringComparison.Ordinal)
            .Replace("{MAP}", WebUtility.HtmlEncode(map), StringComparison.Ordinal)
            .Replace("{CURRENT}", current.ToString(), StringComparison.Ordinal)
            .Replace("{MAX}", maximum.ToString(), StringComparison.Ordinal);
    }

    private string FormatPlayerMessage(IPlayer player)
    {
        var steamId = player.SteamID.ToString();
        return _translations.PlayerConnected
            .Replace("{NAME}", WebUtility.HtmlEncode(player.Name), StringComparison.Ordinal)
            .Replace("{STEAMID}", steamId, StringComparison.Ordinal)
            .Replace("{AUTH}", steamId, StringComparison.Ordinal);
    }

    private string ReadCurrentMap()
    {
        try
        {
            return Core.Engine.GlobalVars.MapName.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}
