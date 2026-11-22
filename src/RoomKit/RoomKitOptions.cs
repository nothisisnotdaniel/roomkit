namespace RoomKit;

/// <summary>The values that make a RoomKit server one app: its names, its data folder, the solution file that marks
/// its repo, its port, its GitHub release repository and the note a captured draft gets. Checked by
/// <see cref="RoomKitApp"/> before anything starts.</summary>
public sealed record RoomKitOptions
{
    /// <summary>The name people read.</summary>
    public required string AppName { get; init; }

    /// <summary>One word of letters, digits, <c>.</c> or <c>-</c>: the
    /// <c>version</c> line (<c>&lt;short name&gt; &lt;version&gt;</c>), the update check's <c>User-Agent</c>, the
    /// push subject and the server project in fixed help texts (<c>src/&lt;short name&gt;.Server</c>).</summary>
    public required string ShortName { get; init; }

    /// <summary>The folder under <c>LocalApplicationData</c> (on macOS <c>~/Library/Application Support</c>) that
    /// holds the token, push, update and worktree files; one name, no path separator.</summary>
    public required string DataFolderName { get; init; }

    /// <summary>The solution file the root locator walks up to; the factory root is its folder's parent.</summary>
    public required string SolutionFileName { get; init; }

    /// <summary>The port the server listens on, on <c>127.0.0.1</c> only (1 to 65535).</summary>
    public required int DefaultPort { get; init; }

    /// <summary>The GitHub <c>owner/name</c> whose releases the update check asks for.</summary>
    public required string ReleaseRepository { get; init; }

    /// <summary>The dated note a captured draft story gets.</summary>
    public required string CaptureNote { get; init; }

    /// <summary>The app assembly's informational version (<c>dev</c> when <c>null</c> or empty); RoomKit's own
    /// version is never used.</summary>
    public string? InformationalVersion { get; init; }
}
