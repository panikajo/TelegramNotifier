using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TelegramNotifier;

internal sealed class TelegramApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string _token = string.Empty;

    public TelegramApiClient(ILogger logger,HttpMessageHandler? handler=null)
    {
        _logger=logger;
        _http=handler==null?new HttpClient():new HttpClient(handler);
        _http.Timeout=TimeSpan.FromSeconds(15);
    }

    public void SetToken(string token) => _token = token?.Trim() ?? string.Empty;

    public async Task<bool> SendMessageAsync(
        TelegramDestinationConfig destination,
        string html,
        CancellationToken cancellationToken = default)
    {
        if (!destination.Enabled || destination.ChatId == 0 || string.IsNullOrWhiteSpace(_token))
            return false;

        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = destination.ChatId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["disable_notification"] = destination.DisableNotification,
            ["disable_web_page_preview"] = true
        };

        if (destination.MessageThreadId > 0)
            payload["message_thread_id"] = destination.MessageThreadId;

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{_token}/sendMessage",
                payload,
                cancellationToken);

            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "[TelegramNotifier] Telegram API returned HTTP {Status}: {Body}",
                (int)response.StatusCode,
                TrimForLog(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TelegramNotifier] Failed to send Telegram message");
        }

        return false;
    }

    private static string TrimForLog(string value) =>
        value.Length <= 500 ? value : value[..500];

    public void Dispose() => _http.Dispose();
}
