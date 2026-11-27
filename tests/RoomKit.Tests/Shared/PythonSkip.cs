namespace RoomKit.Tests;

/// <summary>When the <c>ci_changes.py</c> suite cannot run: on Windows, or without <c>python3</c> in any
/// <c>PATH</c> folder.</summary>
internal static class PythonSkip
{
    /// <summary>Null when the suite can run, else the reason to skip it; the folders come from this process's
    /// <c>PATH</c>.</summary>
    internal static string? SkipReason(bool isWindows, Func<string, bool> fileExists)
    {
        return SkipReason(isWindows, fileExists, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>Null when the suite can run, else the reason to skip it, with <paramref name="path"/> as
    /// <c>PATH</c>.</summary>
    internal static string? SkipReason(bool isWindows, Func<string, bool> fileExists, string? path)
    {
        if (isWindows)
        {
            return "runs the CI change check, which runs only on Linux and macOS (not Windows)";
        }

        foreach (string folder in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (fileExists(Path.Combine(folder, "python3")))
            {
                return null;
            }
        }

        return "needs python3 on PATH, and none was found";
    }

    /// <summary>The reason for this machine.</summary>
    internal static string? SkipReason()
    {
        return SkipReason(OperatingSystem.IsWindows(), File.Exists);
    }
}
