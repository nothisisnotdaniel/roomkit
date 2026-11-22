namespace RoomKit;

/// <summary>Watches the factory root for things the user should hear about and sends a push for each: a job that
/// moves into a blocked stage (<see cref="IJobFormat.BlockedStages"/>, kind <c>needs-human</c>) (<c>merge-conflict</c> when its note starts with "merge conflict"), <c>ci.status</c>
/// becoming <c>failure</c>, a job moving into the done stage (only with <c>notifyDone</c>), new <c>[?]</c> lines in a
/// project and a new updater result (<c>updated</c>/<c>update_failed</c>, only with <c>notifyUpdate</c>).
/// <see cref="Scan"/> runs every 30 s on the <see cref="TimeProvider"/>; the first scan only records a baseline for
/// jobs and backlogs, while an updater result whose <c>run</c> differs from <c>updateResultNotified</c> is handled on
/// every scan, the first one too. A job or backlog that can't be read keeps its last known state, and <c>[?]</c>
/// lines once seen stay seen, so no event is sent twice.</summary>
internal sealed class PushWatcher : IHostedService, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Stages a payload may name (the format's <see cref="IJobFormat.Stages"/>); any other value from a file
    /// is left out.</summary>
    public static IReadOnlySet<string> KnownStages => new HashSet<string>(RoomContext.Jobs.Stages, StringComparer.Ordinal);

    private readonly string factoryRoot;

    private readonly PushStore store;

    private readonly PushSender sender;

    private readonly UpdateResultReader results;

    private readonly TimeProvider time;

    private readonly ILogger<PushWatcher> logger;

    private readonly SemaphoreSlim scanning = new(1, 1);

    private readonly Dictionary<string, JobState> jobs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, HashSet<string>> approvals = new(StringComparer.Ordinal);

    private bool baseline;

    private ITimer? timer;

    public PushWatcher(
        FactoryRoot root, PushStore store, PushSender sender, TimeProvider time, ILogger<PushWatcher> logger)
    {
        factoryRoot = root.Path;
        this.store = store;
        this.sender = sender;
        results = new UpdateResultReader(store.Folder, store.Owner);
        this.time = time;
        this.logger = logger;
    }

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

    /// <summary>Reads the jobs and every project's <c>[?]</c> lines, compares them with the last scan and sends one
    /// notification per new event. Returns the payloads it sent (or tried to send).</summary>
    public async Task<IReadOnlyList<PushPayload>> Scan()
    {
        await scanning.WaitAsync();
        try
        {
            List<PushPayload> events = Collect();
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

    /// <summary>Updates the remembered state and returns the events since the last scan (none on the
    /// baseline).</summary>
    private List<PushPayload> Collect()
    {
        List<PushPayload> events = [];
        HashSet<string> projects = new(ProjectFinder.Find(factoryRoot).Select(project => project.Name), StringComparer.Ordinal);
        IJobFormat format = RoomContext.Jobs;
        IReadOnlySet<string> knownStages = KnownStages;
        foreach (RoomJob job in format.ReadAll(factoryRoot))
        {
            JobState now = new(job.Stage, job.CiStatus);
            jobs.TryGetValue(job.Id, out JobState? before);
            jobs[job.Id] = now;
            if (!baseline)
            {
                continue;
            }

            string? project = job.Project is not null && projects.Contains(job.Project) ? job.Project : null;
            string? stage = job.Stage is not null && knownStages.Contains(job.Stage) ? job.Stage : null;
            if (IsBlocked(format, job.Stage) && !IsBlocked(format, before?.Stage))
            {
                bool conflict = job.Note is not null
                    && job.Note.TrimStart().StartsWith("merge conflict", StringComparison.OrdinalIgnoreCase);
                events.Add(new PushPayload(conflict ? PushPayload.MergeConflict : PushPayload.NeedsHuman, project, job.Id, stage));
            }

            if (job.CiStatus == "failure" && before?.CiStatus != "failure")
            {
                events.Add(new PushPayload(PushPayload.CiFailed, project, job.Id, stage));
            }

            if (job.Stage == format.DoneStage && before?.Stage != format.DoneStage)
            {
                events.Add(new PushPayload(PushPayload.Done, project, job.Id, stage));
            }
        }

        foreach (ProjectFolder project in ProjectFinder.Find(factoryRoot))
        {
            Backlog? backlog = BacklogReader.Read(project);
            if (backlog is null)
            {
                continue;
            }

            if (!approvals.TryGetValue(project.Name, out HashSet<string>? seen))
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                approvals[project.Name] = seen;
            }

            int added = 0;
            foreach (BacklogEntry entry in backlog.Approval)
            {
                if (seen.Add(Key(entry)))
                {
                    added++;
                }
            }

            if (baseline && added > 0)
            {
                events.Add(new PushPayload(PushPayload.Approval, project.Name, null, null, added));
            }
        }

        baseline = true;
        return events;
    }

    /// <summary>True when the stage is one of the format's <see cref="IJobFormat.BlockedStages"/>.</summary>
    private static bool IsBlocked(IJobFormat format, string? stage)
    {
        return stage is not null && format.BlockedStages.Contains(stage, StringComparer.Ordinal);
    }

    /// <summary>A line's identity: its number when it has one, else its text.</summary>
    private static string Key(BacklogEntry entry)
    {
        return entry.Number is int number ? "#" + number : entry.Title + "\u0000" + entry.Rest;
    }

    private sealed record JobState(string? Stage, string? CiStatus);
}
