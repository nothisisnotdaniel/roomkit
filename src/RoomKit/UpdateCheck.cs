using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>A SemVer 2.0.0 version without build metadata (<c>MAJOR.MINOR.PATCH[-pre.release]</c>), ordered by
/// SemVer precedence. Numbers have no leading zeros; a <c>+build</c> part is refused.</summary>
internal sealed partial class SemVer : IComparable<SemVer>
{
    /// <summary>Longer input is refused before the pattern runs.</summary>
    public const int MaxLength = 128;

    private readonly string[] numbers;

    private readonly string[] prerelease;

    private readonly string text;

    private SemVer(string text, string[] numbers, string[] prerelease)
    {
        this.text = text;
        this.numbers = numbers;
        this.prerelease = prerelease;
    }

    /// <summary>The version, or <c>null</c> when <paramref name="value"/> is not valid SemVer without build
    /// metadata.</summary>
    public static SemVer? Parse(string? value)
    {
        if (value is null || value.Length > MaxLength)
        {
            return null;
        }

        Match match = Pattern().Match(value);
        if (!match.Success)
        {
            return null;
        }

        string[] numbers = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        return new SemVer(value, numbers, prerelease);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int index = 0; index < numbers.Length; index++)
        {
            int compared = CompareNumbers(numbers[index], other.numbers[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        // A version with a prerelease has lower precedence than the same version without one.
        if (prerelease.Length == 0 || other.prerelease.Length == 0)
        {
            return other.prerelease.Length.CompareTo(prerelease.Length);
        }

        for (int index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
        {
            int compared = CompareIdentifiers(prerelease[index], other.prerelease[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    public bool IsNewerThan(SemVer other)
    {
        return CompareTo(other) > 0;
    }

    public override string ToString()
    {
        return text;
    }

    /// <summary>Numeric identifiers have no leading zeros, so the longer one is larger; equal lengths compare
    /// ordinally (no overflow for any length).</summary>
    private static int CompareNumbers(string left, string right)
    {
        return left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : Math.Sign(string.CompareOrdinal(left, right));
    }

    /// <summary>Numeric identifiers sort below alphanumeric ones; alphanumeric ones compare in ASCII order.</summary>
    private static int CompareIdentifiers(string left, string right)
    {
        bool leftNumeric = IsNumeric(left);
        bool rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumbers(left, right);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool IsNumeric(string identifier)
    {
        foreach (char c in identifier)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)"
        + "(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>The version this server runs (tests put another one in).</summary>
internal sealed class RunningVersion
{
    public RunningVersion(string value)
    {
        Value = value;
    }

    /// <summary><see cref="AppVersion.Current"/>.</summary>
    public static RunningVersion Current => new(AppVersion.Current);

    public string Value { get; }
}

/// <summary>The HTTP handler the update check goes through (tests put a fake one in), and its timeout.</summary>
internal sealed class UpdateTransport
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public UpdateTransport(HttpMessageHandler handler)
        : this(handler, DefaultTimeout)
    {
    }

    /// <summary>A transport with another timeout (tests use a short one for a hanging fake).</summary>
    public UpdateTransport(HttpMessageHandler handler, TimeSpan timeout)
    {
        Handler = handler;
        Timeout = timeout;
    }

    public HttpMessageHandler Handler { get; }

    /// <summary>For the whole check: connect, headers and body.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The real handler: no redirects, no cookies, no proxy.</summary>
    public static UpdateTransport CreateDefault()
    {
        return new UpdateTransport(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = DefaultTimeout,
        });
    }
}

/// <summary>The latest release found, its release page (built from the tag, never taken from the response) and
/// the update channel (<see cref="UpdateChannel"/>) it was found on.</summary>
internal sealed record UpdateRelease(SemVer Version, string Url, string Channel = UpdateChannel.Stable);

/// <summary>The last release a check found, kept in memory only; <c>null</c> until a check succeeds.</summary>
internal sealed class UpdateState
{
    private UpdateRelease? latest;

    public UpdateRelease? Latest
    {
        get => Volatile.Read(ref latest);
        set => Volatile.Write(ref latest, value);
    }
}

/// <summary>Checks the latest GitHub Release of <see cref="RepoSlug"/> at start and every 6 h on the
/// <see cref="TimeProvider"/>, on the channel <c>settings.json</c> names (<c>stable</c> when it is missing or
/// refused): one <c>GET</c> without credentials, no redirects, a 10 s timeout and a 1 MiB body cap. On
/// <c>stable</c> it asks <see cref="LatestUrl"/> and keeps only a <c>tag_name</c> of <c>v</c> + SemVer; on
/// <c>develop</c> it asks <see cref="ListUrl"/> and keeps the highest (by SemVer precedence) non-draft
/// <c>tag_name</c> that is exactly <c>vX.Y.Z</c> or <c>vX.Y.Z-develop.N</c>. The release URL is rebuilt from the
/// tag. Anything else keeps the last known state. When the release is newer than the running version and has
/// not been announced yet (<c>updateNotified</c> in <c>push.json</c>), one <c>update</c> push is sent (unless
/// <c>notifyUpdate</c> is off) and the version is stored. A <c>dev</c> or non-SemVer running version makes no
/// request. Logs hold only a status code or a fixed text.</summary>
internal sealed partial class UpdateChecker : IHostedService, IDisposable
{
    /// <summary>The app's <see cref="RoomKitOptions.ReleaseRepository"/>; must equal the slug in <c>RELEASES</c> in
    /// the app's <c>scripts/launch-agent.sh</c>.</summary>
    public static string RepoSlug => RoomContext.Options.ReleaseRepository;

    public static string LatestUrl => "https://api.github.com/repos/" + RepoSlug + "/releases/latest";

    public static string ListUrl => "https://api.github.com/repos/" + RepoSlug + "/releases?per_page=100";

    public static string ReleasePrefix => "https://github.com/" + RepoSlug + "/releases/tag/";

    public const int MaxBodyBytes = 1024 * 1024;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly UpdateState state;

    private readonly RunningVersion running;

    private readonly UpdateTransport transport;

    private readonly UpdateSettingsStore settings;

    private readonly PushStore store;

    private readonly PushSender sender;

    private readonly TimeProvider time;

    private readonly ILogger<UpdateChecker> logger;

    private readonly HttpClient client;

    private readonly SemaphoreSlim checking = new(1, 1);

    private readonly CancellationTokenSource stopping = new();

    private readonly object lifecycle = new();

    private ITimer? timer;

    private bool disposed;

    public UpdateChecker(
        UpdateState state, RunningVersion running, UpdateTransport transport, UpdateSettingsStore settings,
        PushStore store, PushSender sender, TimeProvider time, ILogger<UpdateChecker> logger)
    {
        this.state = state;
        this.running = running;
        this.transport = transport;
        this.settings = settings;
        this.store = store;
        this.sender = sender;
        this.time = time;
        this.logger = logger;
        client = new HttpClient(transport.Handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Starts the first check (without waiting for the network) and the 6 h timer.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = time.CreateTimer(_ => _ = CheckSafely(), null, Interval, Interval);
        _ = CheckSafely();
        return Task.CompletedTask;
    }

    /// <summary>Stops the timer and cancels a running check; does nothing once disposed, so the host may call it
    /// any number of times and before or after <see cref="Dispose"/>.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (lifecycle)
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }

            timer?.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
            stopping.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>Cancels a running check before disposing what it uses; runs its body at most once.</summary>
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopping.Cancel();
            timer?.Dispose();
            client.Dispose();
            stopping.Dispose();
        }
    }

    private bool IsDisposed()
    {
        lock (lifecycle)
        {
            return disposed;
        }
    }

    /// <summary>The timer's check: skipped while another one runs; never throws.</summary>
    public async Task CheckSafely()
    {
        SemVer? current = SemVer.Parse(running.Value);
        if (current is null || IsDisposed() || !await checking.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (IsDisposed())
            {
                return;
            }

            await CheckHeld(current);
        }
        catch (Exception)
        {
            // A failed check must never stop the server or surface as an unobserved task exception.
            logger.LogWarning("Update check failed");
        }
        finally
        {
            checking.Release();
        }
    }

    /// <summary>One check: asks GitHub for the latest release, keeps it when valid and sends the <c>update</c>
    /// push once per new version. Checks run one at a time.</summary>
    public async Task Check()
    {
        SemVer? current = SemVer.Parse(running.Value);
        if (current is null)
        {
            return;
        }

        await checking.WaitAsync();
        try
        {
            await CheckHeld(current);
        }
        finally
        {
            checking.Release();
        }
    }

    /// <summary>The channel to check: the stored one, or <c>stable</c> when <c>settings.json</c> is refused.</summary>
    public string Channel()
    {
        try
        {
            return settings.Settings().Channel;
        }
        catch (PrivateFileException)
        {
            return UpdateChannel.Stable;
        }
    }

    /// <summary>The check itself; the caller holds <c>checking</c>.</summary>
    private async Task CheckHeld(SemVer current)
    {
        UpdateRelease? found = await Fetch(Channel());
        if (found is null)
        {
            return;
        }

        state.Latest = found;
        if (found.Version.IsNewerThan(current))
        {
            await Notify(found.Version);
        }
    }

    private async Task<UpdateRelease?> Fetch(string channel)
    {
        bool develop = channel == UpdateChannel.Develop;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        timeout.CancelAfter(transport.Timeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, develop ? ListUrl : LatestUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", RoomContext.Options.ShortName);
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            int status = (int)response.StatusCode;
            logger.LogInformation("Update check returned {Status}", status);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            byte[]? body = await ReadCapped(response.Content, timeout.Token);
            if (body is null)
            {
                logger.LogWarning("Update check response too large");
                return null;
            }

            SemVer? version = develop ? ParseList(body) : ParseTag(body);
            if (version is null)
            {
                logger.LogWarning("Update check response not valid");
                return null;
            }

            return new UpdateRelease(version, ReleasePrefix + "v" + version, channel);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            logger.LogWarning("Update check timed out");
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning("Update check failed");
            return null;
        }
    }

    /// <summary>The body when it is at most <see cref="MaxBodyBytes"/>, read up to one byte past the cap whatever
    /// <c>Content-Length</c> says; <c>null</c> when longer.</summary>
    private static async Task<byte[]?> ReadCapped(HttpContent content, CancellationToken cancellation)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellation);
        using MemoryStream body = new();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while (body.Length <= MaxBodyBytes && (read = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            body.Write(chunk, 0, read);
        }

        return body.Length > MaxBodyBytes ? null : body.ToArray();
    }

    /// <summary>The version from <c>tag_name</c> when it is <c>v</c> + SemVer without build metadata.</summary>
    private static SemVer? ParseTag(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tag_name", out JsonElement tag)
                || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string text = tag.GetString()!;
            return text.StartsWith('v') ? SemVer.Parse(text[1..]) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The highest version (SemVer precedence) among the array entries that are objects, whose
    /// <c>draft</c> is not <c>true</c> and whose <c>tag_name</c> is exactly <c>vX.Y.Z</c> or
    /// <c>vX.Y.Z-develop.N</c>; <c>null</c> when the body is no JSON array or no entry qualifies.</summary>
    private static SemVer? ParseList(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            SemVer? best = null;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || (entry.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                    || !entry.TryGetProperty("tag_name", out JsonElement tag)
                    || tag.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string text = tag.GetString()!;
                SemVer? version = ChannelTag().IsMatch(text) ? SemVer.Parse(text[1..]) : null;
                if (version is not null && (best is null || version.IsNewerThan(best)))
                {
                    best = version;
                }
            }

            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        "^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-develop\\.[1-9][0-9]*)?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChannelTag();

    /// <summary>Stores <c>updateNotified</c> first, then sends (only with <c>notifyUpdate</c>), so a version is
    /// announced at most once, also across restarts. Refused push files send and store nothing. The read and the
    /// write take the store lock separately; that is safe only because checks run one at a time (<c>checking</c>),
    /// so a second caller must hold that semaphore too.</summary>
    private async Task Notify(SemVer version)
    {
        string text = version.ToString();
        try
        {
            PushSettings settings = store.Settings();
            if (string.Equals(settings.UpdateNotified, text, StringComparison.Ordinal))
            {
                return;
            }

            store.SetUpdateNotified(text);
            if (settings.NotifyUpdate)
            {
                await sender.Send(new PushPayload(PushPayload.Update, null, null, null, Version: text), stopping.Token);
            }
        }
        catch (PushStoreException)
        {
            logger.LogWarning("Push files refused; update notice dropped");
        }
    }
}
