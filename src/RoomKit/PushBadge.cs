namespace RoomKit;

/// <summary>The host's app badge count for the pushes RoomKit's server sends. Register one through
/// <see cref="RoomServices.SetPushBadge(IPushBadgeSource)"/>. The push sender asks it once per send (only when there
/// is at least one subscription) and adds the count as the payload's <c>badge</c> field, so the app icon's badge
/// updates while the app is closed. A count above 999 is sent as 999; <c>null</c> or a negative count leaves
/// <c>badge</c> out. When the source throws, the push is still sent, without a badge, and only a fixed text is logged
/// (never the exception's message).</summary>
public interface IPushBadgeSource
{
    /// <summary>The badge count to send now, or <c>null</c> for no <c>badge</c> field.</summary>
    public Task<int?> Current(CancellationToken cancellation);
}
