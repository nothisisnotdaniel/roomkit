using System.Diagnostics;
using System.Globalization;

namespace RoomKit.Tests;

/// <summary>Runs the two PowerShell scripts against a private release repository, clock and scheduler fixture.</summary>
internal static class WindowsReleaseFixture
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static void Run(string fixture, string scripts, string scenario, FakeTime clock)
    {
        if (scenario == "entry-link")
        {
            string target = Path.Combine(fixture, "link-target.ps1");
            File.WriteAllText(target, "throw 'Fixture link target was loaded.'");
            File.CreateSymbolicLink(Path.Combine(fixture, "entry-link"), target);
        }

        if (OperatingSystem.IsWindows() && scenario == "linked-stamp")
        {
            string support = Path.Combine(fixture, "ControlRoom");
            WindowsPrivateFiles.CreateDirectory(new DirectoryInfo(support));
            string target = Path.Combine(fixture, "stamp-target");
            File.WriteAllText(target, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            File.CreateSymbolicLink(Path.Combine(support, "last-check"), target);
        }

        if (scenario == "linked-jobs")
        {
            string root = Directory.CreateDirectory(Path.Combine(fixture, "scan root")).FullName;
            string target = Directory.CreateDirectory(Path.Combine(fixture, "real job store")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "factory"), target);
        }

        if (scenario is "linked-job" or "linked-state")
        {
            string jobs = Directory.CreateDirectory(Path.Combine(fixture, "scan root", "factory", "jobs")).FullName;
            string job = Path.Combine(jobs, "fixture");
            string target = Directory.CreateDirectory(Path.Combine(fixture, "real job store")).FullName;
            string state = Path.Combine(target, "state.json");
            File.WriteAllText(state, """{"stage":"approve"}""");
            if (scenario == "linked-job")
            {
                Directory.CreateSymbolicLink(job, target);
            }
            else
            {
                Directory.CreateDirectory(job);
                File.CreateSymbolicLink(Path.Combine(job, "state.json"), state);
            }
        }

        RunScript(fixture, scenario, "-Scripts", scripts, "-Scenario", scenario, "-Epoch",
            clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    public static string DetachRootJunction(string fixture) =>
        RunScript(fixture, "root-junction disposal", "-DetachRootJunction");

    private static string RunScript(string fixture, string operation, params string[] arguments)
    {
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo start = new(shell)
        {
            WorkingDirectory = fixture,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        string runner = Path.Combine(RepoCopy.RepoFolder(), "tests", "RoomKit.Tests", "Shared", "WindowsReleaseFixture.ps1");
        string[] command = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned",
            "-File", runner, "-Fixture", fixture, .. arguments];
        foreach (string argument in command)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail(operation + " exceeded " + Timeout);
        }

        Assert.True(process.ExitCode == 0, operation + Environment.NewLine + output.Result + error.Result);
        return output.Result.TrimEnd();
    }
}
