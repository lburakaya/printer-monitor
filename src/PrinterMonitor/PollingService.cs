using System.Collections.Concurrent;

namespace PrinterMonitor;

public sealed class PollCoordinator(PrinterStore store, IPrinterAdapter adapter, MonitorOptions options, ILogger<PollCoordinator> logger)
{
    private readonly SemaphoreSlim slots = new(options.MaxConcurrency);
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public int ActiveCount => active.Count;

    public async Task<PrinterReading?> PollAsync(Guid id, CancellationToken token)
    {
        if (!active.TryAdd(id, 0)) return store.Find(id)?.Reading;
        try
        {
            await slots.WaitAsync(token);
            try
            {
                var row = store.Find(id);
                if (row is null) return null;
                PrinterReading reading;
                try { reading = await adapter.ReadAsync(row.Config, store.Secrets(row), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Do not log exception text: third-party errors can contain sensitive values.
                    logger.LogWarning("Yazıcı {Id} sorgusu başarısız: {ErrorType}", id, ex.GetType().Name);
                    reading = new("error", "Sorgu tamamlanamadı. Yapılandırma ve servis kayıtlarını kontrol edin.", null, null, [], DateTimeOffset.UtcNow, null);
                }
                if (reading.Status != "online" && row.Reading is { } previous)
                    reading = reading with { Supplies = previous.Supplies, Model = previous.Model, Serial = previous.Serial, LastSuccessAt = previous.LastSuccessAt };
                store.Record(row.Config, reading);
                return reading;
            }
            finally { slots.Release(); }
        }
        finally { active.TryRemove(id, out _); }
    }
    public Task PollAllAsync(CancellationToken token) => Parallel.ForEachAsync(store.All(),
        new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = token },
        async (row, ct) => { await PollAsync(row.Config.Id, ct); });
}

public sealed class PollingService(PollCoordinator coordinator, PrinterStore store, MonitorOptions options, ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        try
        {
            do
            {
                try { await coordinator.PollAllAsync(stoppingToken); store.Prune(options.HistoryDays); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Sorgu döngüsü hatası: {ErrorType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
