namespace RoomKit.Tests;

/// <summary>A test-only app on RoomKit's core alone: no job format, no <see cref="RoomKitOptions.CaptureNote"/>, port
/// 8202, repository <c>example/coreroom</c>, a solution file name no folder has (so it finds no factory root). It maps
/// no endpoints of its own.</summary>
internal static class CoreRoom
{
    public const string Version = "2.0.1";

    public static RoomKitOptions Options { get; } = new()
    {
        AppName = "Core Room",
        ShortName = "CoreRoom",
        DataFolderName = "CoreRoom",
        SolutionFileName = "CoreRoom.slnx",
        DefaultPort = 8202,
        ReleaseRepository = "example/coreroom",
        InformationalVersion = Version,
    };

    public static void Map(RoomRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
    }
}

/// <summary>A push source a test fills: every <see cref="Collect"/> returns what was queued since the last one, or
/// throws when <see cref="Fail"/> is set.</summary>
internal sealed class QueuedPushSource : IPushSource
{
    private readonly Queue<PushEvent> queued = new();

    /// <summary>When set, <see cref="Collect"/> throws an exception with this message.</summary>
    public string? Fail { get; set; }

    public int Calls { get; private set; }

    public void Add(PushEvent item)
    {
        queued.Enqueue(item);
    }

    public Task<IReadOnlyList<PushEvent>> Collect(CancellationToken cancellation)
    {
        Calls++;
        if (Fail is not null)
        {
            throw new InvalidOperationException(Fail);
        }

        IReadOnlyList<PushEvent> events = [.. queued];
        queued.Clear();
        return Task.FromResult(events);
    }
}
