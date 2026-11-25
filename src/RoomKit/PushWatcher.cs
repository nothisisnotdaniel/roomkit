namespace RoomKit;

/// <summary>The core push service: on every scan it asks each registered <see cref="IPushSource"/> for its new events,
/// handles a new updater result (<c>updated</c>/<c>update_failed</c>, only with <c>notifyUpdate</c>) and sends one push
/// per event and subscription through <see cref="PushSender"/> (a <c>done</c> event only with <c>notifyDone</c>).
/// <see cref="Scan"/> runs every 30 s on the <see cref="TimeProvider"/>; an updater result whose <c>run</c> differs
/// from <c>updateResultNotified</c> is handled on every scan, the first one too. A source that throws loses its events
/// of that scan and only a fixed text is logged; the other sources' events are still sent. The
/// <see cref="FactoryRoot"/> constructor watches the factory through <see cref="FactoryPushSource"/>.</summary>
internal sealed class PushWatcher : IHostedService, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Stages a payload may name (the format's <see cref="IJobFormat.Stages"/>); any other value from a file
    /// is left out.</summary>
    public static IReadOnlySet<string> KnownStages => FactoryPushSource.KnownStages;

    private readonly IReadOnlyList<IPushSource> sources;

    private readonly PushStore store;

    private readonly PushSender sender;

    private readonly UpdateResultReader results;

    private readonly TimeProvider time;

    private readonly ILogger<PushWatcher> logger;

    private readonly SemaphoreSlim scanning = new(1, 1);

    private ITimer? timer;

    /// <summary>Watches the factory root's jobs and backlogs (and the updater result).</summary>
    public PushWatcher(
        FactoryRoot root, PushStore store, PushSender sender, TimeProvider time, ILogger<PushWatcher> logger)
        : this([new FactoryPushSource(root)], store, sender, time, logger)
    {
    }

    /// <summary>Watches the given sources (and the updater result), in their order.</summary>
    public PushWatcher(
        IEnumerable<IPushSource> sources, PushStore store, PushSender sender, TimeProvider time, ILogger<PushWatcher> logger)
    {
        this.sources = [.. sources];
        this.store = store;
        this.sender = sender;
        results = new UpdateResultReader(store.Folder, store.Owner);
        this.time = time;
        this.logger = logger;
    }

    /// <summary>The sources, in the order they are asked.</summary>
    public IReadOnlyList<IPushSource> Sources => sources;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = time.CreateTimer(_ => _ = TimerScan(), null, Interval, Interval);
        return TimerScan();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer?.Dispose();
        scanning.Dispose();
    }

    /// <summary>Asks every source for its new events, adds a new updater result and sends one notification per
    /// event. Returns the payloads it sent (or tried to send).</summary>
    public async Task<IReadOnlyList<PushPayload>> Scan()
    {
        await scanning.WaitAsync();
        try
        {
            List<PushPayload> events = await Collect();
            UpdateResult? result = results.Read();
            if (events.Count == 0 && result is null)
            {
                return events;
            }

            bool notifyDone;
            try
            {
                PushSettings settings = store.Settings();
                notifyDone = settings.NotifyDone;
                if (result is not null && !string.Equals(result.Run, settings.UpdateResultNotified, StringComparison.Ordinal))
                {
                    // Stored first, so a result is announced at most once, also across restarts.
                    store.SetUpdateResultNotified(result.Run);
                    if (settings.NotifyUpdate)
                    {
                        events.Add(PushPayload.From(result));
                    }
                }
            }
            catch (PushStoreException)
            {
                logger.LogWarning("Push files refused; events dropped");
                return [];
            }

            List<PushPayload> sent = [];
            foreach (PushPayload payload in events)
            {
                if (payload.Kind == PushPayload.Done && !notifyDone)
                {
                    continue;
                }

                try
                {
                    await sender.Send(payload, CancellationToken.None);
                    sent.Add(payload);
                }
                catch (PushStoreException)
                {
                    logger.LogWarning("Push files refused; events dropped");
                    return sent;
                }
            }

            return sent;
        }
        finally
        {
            scanning.Release();
        }
    }

    private async Task TimerScan()
    {
        if (scanning.CurrentCount == 0)
        {
            return;
        }

        try
        {
            await Scan();
        }
        catch (Exception)
        {
            // A failed scan must never stop the server or surface as an unobserved task exception.
            logger.LogWarning("Push scan failed; its events are dropped");
        }
    }

    /// <summary>Every source's events in source order; a failing source adds none.</summary>
    private async Task<List<PushPayload>> Collect()
    {
        List<PushPayload> events = [];
        foreach (IPushSource source in sources)
        {
            try
            {
                IReadOnlyList<PushEvent> found = await source.Collect(CancellationToken.None);
                events.AddRange(found.Select(item => item.Payload));
            }
            catch (Exception)
            {
                // Never the exception's message: a source's text could hold paths or record text. Cancellation is
                // caught too while the token is CancellationToken.None; rethrow it once a real token is passed.
                logger.LogWarning("Push source failed; its events are dropped");
            }
        }

        return events;
    }
}
