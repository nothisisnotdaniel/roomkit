namespace RoomKit.Tests;

/// <summary>When the login check script tests cannot run: on Windows, or without <c>jq</c> in the folders the
/// script run puts on <c>PATH</c>.</summary>
internal static class ShellScriptSkip
{
    /// <summary>The folders (besides the fake bin) that the script run uses as <c>PATH</c>.</summary>
    internal static readonly string[] PathFolders = ["/usr/bin", "/bin", "/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>Null when the tests can run, else the reason to skip them.</summary>
    internal static string? SkipReason(bool isWindows, Func<string, bool> fileExists)
    {
        if (isWindows)
        {
            return "needs /bin/sh, which Windows does not have";
        }

        foreach (string folder in PathFolders)
        {
            if (fileExists(folder + "/jq"))
            {
                return null;
            }
        }

        return "needs jq on PATH (" + string.Join(", ", PathFolders) + "), and none was found";
    }

    /// <summary>The reason for this machine.</summary>
    internal static string? SkipReason()
    {
        return SkipReason(OperatingSystem.IsWindows(), File.Exists);
    }
}
