namespace TelegramNotifier;

// One instance per plugin load. Background HTTP completions from an old load
// can never change the successful-status snapshot of a new load.
internal sealed class StatusDelivery
{
    private readonly SemaphoreSlim _serial = new(1,1);
    private string? _lastSuccessful;

    public async Task SendAsync(string text, bool force, Func<CancellationToken,Task<bool>> send, CancellationToken token)
    {
        bool entered=false;
        try
        {
            entered=await _serial.WaitAsync(0,token);
            if (!entered || (!force && string.Equals(text,_lastSuccessful,StringComparison.Ordinal))) return;
            if (await send(token) && !token.IsCancellationRequested) _lastSuccessful=text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (entered) _serial.Release(); }
    }
}
