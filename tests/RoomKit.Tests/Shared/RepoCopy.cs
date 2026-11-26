using System.Diagnostics;

namespace RoomKit.Tests;

/// <summary>A copy of the repo's <c>src/</c>, <c>docs/</c>, <c>Directory.Build.props</c>, <c>.editorconfig</c> and
/// <c>RoomKit.slnx</c> (without <c>bin</c> or <c>obj</c>) in its own temp folder, deleted on dispose, so a test
/// can run <c>dotnet publish</c> or <c>dotnet pack</c> without touching the real build output.</summary>
internal sealed class RepoCopy : IDisposable
{
    /// <summary>How long one <c>dotnet</c> run may take before the test fails.</summary>
    public static readonly TimeSpan DotnetTimeout = TimeSpan.FromMinutes(5);

    public RepoCopy()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "roomkit-repo-" + Guid.NewGuid().ToString("N"));
        string repo = RepoFolder();
        CopyFolder(System.IO.Path.Combine(repo, "src"), System.IO.Path.Combine(Path, "src"));
        CopyFolder(System.IO.Path.Combine(repo, "docs"), System.IO.Path.Combine(Path, "docs"));
        foreach (string file in new[] { "Directory.Build.props", ".editorconfig", "RoomKit.slnx" })
        {
            File.Copy(System.IO.Path.Combine(repo, file), System.IO.Path.Combine(Path, file));
        }

        Output = System.IO.Path.Combine(Path, "out");
    }

    /// <summary>The copy's root.</summary>
    public string Path { get; }

    /// <summary>An empty output folder inside the copy.</summary>
    public string Output { get; }

    /// <summary>The repo folder (the one holding <c>RoomKit.slnx</c>).</summary>
    public static string RepoFolder()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(System.IO.Path.Combine(current.FullName, "RoomKit.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current.FullName;
    }

    /// <summary>Runs <c>dotnet</c> in the copy with a fixed argument list, waits for it to exit (at most
    /// <see cref="DotnetTimeout"/>) and fails the test when it fails.</summary>
    public void Dotnet(params string[] arguments)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        bool exited = process.WaitForExit(DotnetTimeout);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        Assert.True(exited, "dotnet " + string.Join(" ", arguments) + " took longer than " + DotnetTimeout);
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0, "dotnet " + string.Join(" ", arguments) + ": " + output.Result + error.Result);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            string target = System.IO.Path.Combine(to, System.IO.Path.GetFileName(file));
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }

        foreach (string folder in Directory.GetDirectories(from))
        {
            string name = System.IO.Path.GetFileName(folder);
            if (name is "bin" or "obj")
            {
                continue;
            }

            CopyFolder(folder, System.IO.Path.Combine(to, name));
        }
    }
}
