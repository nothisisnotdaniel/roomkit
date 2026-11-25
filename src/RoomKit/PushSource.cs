using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>Something the push watcher asks, on every scan (every 30 s), for the events the user should hear about
/// since the last scan. Register one through <see cref="RoomServices.AddPushSource(IPushSource)"/>. A source
/// remembers what it already reported: the watcher sends every event it returns, once per subscription, through
/// RoomKit's push sender. When a source throws, its events of that scan are dropped and only a fixed text is logged
/// (never the exception's message); the other sources' events are still sent.</summary>
public interface IPushSource
{
    /// <summary>The new events since the last call (none on the first call, when the source only learns where things
    /// stand, is the usual choice).</summary>
    public Task<IReadOnlyList<PushEvent>> Collect(CancellationToken cancellation);
}

/// <summary>One notification of an app's own <see cref="IPushSource"/>: a kind, a title and a tag. The payload the
/// device gets is exactly <c>{"kind":…,"title":…,"tag":…}</c>, encrypted for each subscription; nothing else from
/// the app goes into it. The shipped <c>sw.js</c> takes the shown title from its fixed table by kind (RoomKit's kinds
/// plus <c>PushAppTitles</c> in <c>room.js</c>, else <c>PushFallbackTitle</c>), never from the payload.</summary>
public sealed partial class PushEvent
{
    /// <summary>The longest <see cref="Title"/> and <see cref="Tag"/>.</summary>
    public const int MaxTextLength = 120;

    private static readonly IReadOnlySet<string> KitKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        PushPayload.NeedsHuman, PushPayload.MergeConflict, PushPayload.CiFailed, PushPayload.Done, PushPayload.Approval,
        PushPayload.Test, PushPayload.Update, PushPayload.Updated, PushPayload.UpdateFailed,
    };

    /// <summary>An app event. <paramref name="kind"/> is 1 to 40 lower-case letters, digits, <c>-</c> or <c>_</c>
    /// (starting with a letter or digit) and not one of RoomKit's own kinds; <paramref name="title"/> and
    /// <paramref name="tag"/> are one line of 1 to <see cref="MaxTextLength"/> characters. Anything else throws
    /// <see cref="ArgumentException"/>.</summary>
    public PushEvent(string kind, string title, string tag)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(tag);
        if (!KindPattern().IsMatch(kind) || KitKinds.Contains(kind))
        {
            throw new ArgumentException("A push kind must be a short lower-case name that is not RoomKit's.", nameof(kind));
        }

        if (!IsText(title))
        {
            throw new ArgumentException("A push title must be one short line.", nameof(title));
        }

        if (!IsText(tag))
        {
            throw new ArgumentException("A push tag must be one short line.", nameof(tag));
        }

        Kind = kind;
        Title = title;
        Tag = tag;
        Payload = new PushPayload(kind, null, null, null, Title: title, Tag: tag);
    }

    /// <summary>One of RoomKit's own events (the factory module's job and backlog events).</summary>
    internal PushEvent(PushPayload payload)
    {
        Kind = payload.Kind;
        Title = PushPayload.Reasons.GetValueOrDefault(payload.Kind, payload.Kind);
        Tag = payload.Kind + ":" + (payload.Job ?? payload.Project ?? "");
        Payload = payload;
    }

    /// <summary>What the event is about, for example <c>pr-review</c>.</summary>
    public string Kind { get; }

    /// <summary>The notification's title.</summary>
    public string Title { get; }

    /// <summary>The notification's tag: a later notification with the same tag replaces the earlier one.</summary>
    public string Tag { get; }

    /// <summary>What is sent.</summary>
    internal PushPayload Payload { get; }

    private static bool IsText(string text)
    {
        return text.Length is > 0 and <= MaxTextLength && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();
}
