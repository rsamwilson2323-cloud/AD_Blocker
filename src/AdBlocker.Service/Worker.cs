namespace AdBlocker.Service;

public sealed class Worker : BackgroundService
{
    private readonly AdEngine _engine;
    public Worker(AdEngine engine) => _engine = engine;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _engine.StartAsync(ct);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await _engine.StopAsync();
    }
}
