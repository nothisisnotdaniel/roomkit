namespace RoomKit;

/// <summary>The factory module's push source: a job that moves into a blocked stage
/// (<see cref="IJobFormat.BlockedStages"/>, kind <c>needs-human</c>; <c>merge-conflict</c> when its note starts with
/// "merge conflict"), <c>ci.status</c> becoming <c>failure</c>, a job moving into the done stage (sent only with
/// <c>notifyDone</c>) and new <c>[?]</c> lines in a project. The first call only records a baseline. A job or backlog
/// that can't be read keeps its last known state, and <c>[?]</c> lines once seen stay seen, so no event is reported
/// twice. Jobs are read only through <see cref="RoomContext.Jobs"/>.</summary>
internal sealed class FactoryPushSource : IPushSource
{
    private readonly string factoryRoot;

    private readonly Dictionary<string, JobState> jobs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, HashSet<string>> approvals = new(StringComparer.Ordinal);

    private bool baseline;

    public FactoryPushSource(FactoryRoot root)
    {
        factoryRoot = root.Path;
    }

    /// <summary>Stages a payload may name (the format's <see cref="IJobFormat.Stages"/>); any other value from a file
    /// is left out.</summary>
    public static IReadOnlySet<string> KnownStages => new HashSet<string>(RoomContext.Jobs.Stages, StringComparer.Ordinal);

    public Task<IReadOnlyList<PushEvent>> Collect(CancellationToken cancellation)
    {
        IReadOnlyList<PushEvent> events = [.. CollectPayloads().Select(payload => new PushEvent(payload))];
        return Task.FromResult(events);
    }

    /// <summary>Updates the remembered state and returns the events since the last scan (none on the
    /// baseline).</summary>
    private List<PushPayload> CollectPayloads()
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
