using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>One item claim read from a repo's <c>origin</c> (factory#54): the backlog item from the ref name, and the
/// cleaned <c>Job:</c>, <c>Machine:</c> and <c>Claimed:</c> values of its message (<c>null</c> when unknown). <c>Job</c>
/// is set only when it matches <see cref="IJobFormat.IdPattern"/>, <c>Claimed</c> only when it is exactly
/// <c>YYYY-MM-DDTHH:MM:SSZ</c>.</summary>
public sealed record ItemClaim(int Item, string? Job, string? Machine, string? Claimed);

/// <summary>What <see cref="ClaimWatcher"/> knows about one repo's claims: the claims, lowest item first, the UTC time
/// of the last successful read (<c>null</c> before one, without an <c>origin</c>, or once it is more than
/// <see cref="ClaimWatcher.MaxAge"/> old) and whether the last read failed.</summary>
public sealed record ClaimState(IReadOnlyList<ItemClaim> Claims, DateTimeOffset? Checked, bool Stale)
{
    /// <summary>No claims, never checked, not stale.</summary>
    public static readonly ClaimState None = new([], null, false);
}

/// <summary>Reads the item claims (<c>refs/factory/claims/&lt;n&gt;</c>) on <c>origin</c> of every project repo
/// (<see cref="ProjectFinder"/>) and of the factory root (as <c>factory</c>) in the background and keeps them in
/// memory, so <c>GET /api/backlogs</c> shows another machine's running jobs without running git. <see cref="StartAsync"/>
/// starts a first <see cref="Scan"/> without waiting for it; then a timer on the <see cref="TimeProvider"/> runs it
/// every <see cref="Interval"/>. Scans run one at a time. A repo read less than <see cref="Interval"/> ago is skipped;
/// claims are fetched only when <c>ls-remote</c> shows a new or changed sha, and at most <see cref="MaxClaims"/> are
/// read per repo. A failed read keeps the last claims and marks them stale; claims older than <see cref="MaxAge"/> are
/// dropped. Claim text comes from anyone who can push to <c>origin</c>: only the first <c>Job</c>, <c>Machine</c> and
/// <c>Claimed</c> line counts, each loses its Unicode category C characters and is capped at
/// <see cref="MaxValueLength"/>, and a message over <see cref="MaxMessageBytes"/> is not read. Only fixed texts are
/// logged.</summary>
public sealed partial class ClaimWatcher : IHostedService, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Claims last read longer ago than this are no longer shown.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public const int MaxClaims = 200;

    public const int MaxMessageBytes = 4096;

    public const int MaxValueLength = 120;

    private static readonly string[] Keys = ["Job", "Machine", "Claimed"];

    private readonly FactoryRoot root;

    private readonly TimeProvider time;

    private readonly ILogger<ClaimWatcher> logger;

    private readonly SemaphoreSlim scanning = new(1, 1);

    private readonly ConcurrentDictionary<string, RepoClaims> repos = new(StringComparer.Ordinal);

    private ITimer? timer;

    private Task? first;

    public ClaimWatcher(FactoryRoot root, TimeProvider time, ILogger<ClaimWatcher> logger)
    {
        this.root = root;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>The claim state of a project (<c>factory</c> for the factory root); <see cref="ClaimState.None"/> before
    /// its first read. Claims last read more than <see cref="MaxAge"/> ago come back empty, with no checked
    /// time.</summary>
    public ClaimState State(string project)
    {
        if (!repos.TryGetValue(project, out RepoClaims? repo))
        {
            return ClaimState.None;
        }

        ClaimState state = repo.State;
        if (state.Checked is DateTimeOffset checkedAt && time.GetUtcNow() - checkedAt > MaxAge)
        {
            return new ClaimState([], null, state.Stale);
        }

        return state;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        first = Task.Run(TimerScan, CancellationToken.None);
        timer = time.CreateTimer(_ => _ = TimerScan(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    /// <summary>Stops the timer and waits for the first scan when it still runs, so no git call outlives the
    /// host.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (first is not null)
        {
            try
            {
                await first.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The host gave up waiting.
            }
        }
    }

    public void Dispose()
    {
        timer?.Dispose();
    }

    /// <summary>Reads the claims of every project repo and the factory root that was not read in the last
    /// <see cref="Interval"/>. A call made while another runs waits for it. Never throws for a repo that fails.</summary>
    public async Task Scan()
    {
        await scanning.WaitAsync();
        try
        {
            List<(string Key, string Folder)> targets = [];
            try
            {
                targets.AddRange(ProjectFinder.Find(root.Path).Select(project => (project.Name, project.Path)));
            }
            catch (Exception)
            {
                logger.LogWarning("Claim scan could not list the projects");
            }

            targets.Add((FactoryBacklog.Name, root.Path));
            foreach ((string key, string folder) in targets)
            {
                try
                {
                    await ScanRepo(key, folder);
                }
                catch (Exception)
                {
                    // Never the exception's message: it could hold paths or remote text.
                    logger.LogWarning("Claim read failed; the last known claims are kept");
                    MarkStale(key);
                }
            }
        }
        finally
        {
            scanning.Release();
        }
    }

    private async Task TimerScan()
    {
        try
        {
            await Scan();
        }
        catch (Exception)
        {
            logger.LogWarning("Claim scan failed");
        }
    }

    private async Task ScanRepo(string key, string folder)
    {
        DateTimeOffset now = time.GetUtcNow();
        repos.TryGetValue(key, out RepoClaims? last);
        if (last?.Read is DateTimeOffset read && now - read < Interval)
        {
            return;
        }

        if (!HasOwnRepo(folder) || !await ProjectGit.HasOrigin(folder))
        {
            repos[key] = new RepoClaims(now, new Dictionary<int, (string, ItemClaim)>(), ClaimState.None);
            return;
        }

        IReadOnlyList<(string Sha, string Ref)>? refs = await ProjectGit.ClaimRefs(folder);
        if (refs is null)
        {
            Fail(key, now, last);
            return;
        }

        SortedDictionary<int, string> listed = [];
        foreach ((string sha, string name) in refs)
        {
            int? item = ItemOf(name);
            if (item is int number)
            {
                listed.TryAdd(number, sha);
            }
        }

        List<(int Item, string Sha)> wanted = listed.Take(MaxClaims).Select(pair => (pair.Key, pair.Value)).ToList();
        IReadOnlyDictionary<int, (string Sha, ItemClaim Claim)> known =
            last?.Known ?? new Dictionary<int, (string, ItemClaim)>();
        bool changed = wanted.Any(pair => !known.TryGetValue(pair.Item, out (string Sha, ItemClaim Claim) old) || old.Sha != pair.Sha);
        if (changed && !await ProjectGit.FetchClaims(folder))
        {
            Fail(key, now, last);
            return;
        }

        Dictionary<int, (string Sha, ItemClaim Claim)> next = [];
        foreach ((int item, string sha) in wanted)
        {
            if (known.TryGetValue(item, out (string Sha, ItemClaim Claim) old) && old.Sha == sha)
            {
                next[item] = old;
                continue;
            }

            ItemClaim? claim = await ReadClaim(folder, item, sha);
            if (claim is null)
            {
                Fail(key, now, last);
                return;
            }

            next[item] = (sha, claim);
        }

        repos[key] = new RepoClaims(now, next, new ClaimState([.. next.Values.Select(entry => entry.Claim)], now, false));
    }

    /// <summary>The claim of a fetched commit, with unknown fields when its message is over
    /// <see cref="MaxMessageBytes"/> or the ref points at anything but a commit (a tag, tree or blob is never read,
    /// so a tag can't bring in a large peeled message); <c>null</c> when git fails.</summary>
    private static async Task<ItemClaim?> ReadClaim(string folder, int item, string sha)
    {
        string? type = await ProjectGit.ObjectType(folder, sha);
        if (type is null)
        {
            return null;
        }

        if (type != "commit")
        {
            return new ItemClaim(item, null, null, null);
        }

        long? size = await ProjectGit.ObjectSize(folder, sha);
        if (size is null)
        {
            return null;
        }

        if (size > MaxMessageBytes)
        {
            return new ItemClaim(item, null, null, null);
        }

        string? message = await ProjectGit.CommitMessage(folder, sha);
        return message is null ? null : Parse(item, message);
    }

    /// <summary>The claim from its message: the first <c>Key: value</c> line of each key counts (as job.py reads
    /// it), each value cleaned with <see cref="Clean"/>; an empty value is <c>null</c>.</summary>
    public static ItemClaim Parse(int item, string message)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string line in message.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            string name = line[..separator];
            if (Keys.Contains(name, StringComparer.Ordinal) && !fields.ContainsKey(name))
            {
                fields[name] = Clean(line[(separator + 2)..]);
            }
        }

        string? job = fields.GetValueOrDefault("Job");
        string? machine = fields.GetValueOrDefault("Machine");
        string? claimed = fields.GetValueOrDefault("Claimed");
        return new ItemClaim(
            item,
            job is not null && RoomContext.Jobs.IdPattern.IsMatch(job) ? job : null,
            string.IsNullOrEmpty(machine) ? null : machine,
            claimed is not null && ClaimedPattern().IsMatch(claimed) ? claimed : null);
    }

    /// <summary>The value without Unicode category C characters (control, format, bidi, surrogate, private use,
    /// unassigned), trimmed and capped at <see cref="MaxValueLength"/> code points.</summary>
    public static string Clean(string value)
    {
        StringBuilder text = new();
        for (int index = 0; index < value.Length; index++)
        {
            if (!Rune.TryGetRuneAt(value, index, out Rune rune))
            {
                // A lone surrogate: category C.
                continue;
            }

            if (rune.Utf16SequenceLength == 2)
            {
                index++;
            }

            if (!IsOther(Rune.GetUnicodeCategory(rune)))
            {
                text.Append(rune.ToString());
            }
        }

        string trimmed = text.ToString().Trim();
        StringBuilder capped = new();
        int count = 0;
        foreach (Rune rune in trimmed.EnumerateRunes())
        {
            if (count == MaxValueLength)
            {
                break;
            }

            capped.Append(rune.ToString());
            count++;
        }

        return capped.ToString();
    }

    /// <summary>The item of a claim ref: its last part after <c>refs/factory/claims/</c> must be 1 to 6 digits, else
    /// <c>null</c>.</summary>
    public static int? ItemOf(string refName)
    {
        if (!refName.StartsWith(ProjectGit.ClaimsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = refName[ProjectGit.ClaimsPrefix.Length..];
        return ItemPattern().IsMatch(rest) ? int.Parse(rest, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    private static bool IsOther(UnicodeCategory category)
    {
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    private static bool HasOwnRepo(string folder)
    {
        string dotGit = Path.Combine(folder, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>A failed read: the last claims and checked time stay, marked stale.</summary>
    private void Fail(string key, DateTimeOffset now, RepoClaims? last)
    {
        repos[key] = last is null
            ? new RepoClaims(now, new Dictionary<int, (string, ItemClaim)>(), ClaimState.None with { Stale = true })
            : last with { Read = now, State = last.State with { Stale = true } };
    }

    private void MarkStale(string key)
    {
        DateTimeOffset now = time.GetUtcNow();
        repos.TryGetValue(key, out RepoClaims? last);
        Fail(key, now, last);
    }

    [GeneratedRegex(@"\A[0-9]{1,6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClaimedPattern();

    /// <summary>One repo's last read time, the claims behind it by item (with their sha) and its state.</summary>
    private sealed record RepoClaims(
        DateTimeOffset Read, IReadOnlyDictionary<int, (string Sha, ItemClaim Claim)> Known, ClaimState State);
}
