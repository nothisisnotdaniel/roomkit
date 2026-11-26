namespace RoomKit;

/// <summary>Server-wide limit on <c>POST /api/push/test</c>: at most one test send per <see cref="Window"/>, timed on
/// the injected <see cref="TimeProvider"/>.</summary>
internal sealed class PushTestLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly TimeProvider time;
    private readonly object gate = new();
    private DateTimeOffset? lastSend;

    public PushTestLimiter(TimeProvider time)
    {
        this.time = time;
    }

    /// <summary>True (and the window starts now) when no test send was allowed in the last <see cref="Window"/>.</summary>
    public bool TryStart()
    {
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (lastSend is DateTimeOffset last && now - last < Window && now >= last)
            {
                return false;
            }

            lastSend = now;
            return true;
        }
    }
}
