using System.Diagnostics;
using System.Runtime.Versioning;

namespace RoomKit.Tests;

/// <summary>The run: script of the release job's "Check the GitHub Packages login" step, taken from
/// <c>release.yml</c> and run with <c>/bin/sh</c>, a fake <c>dotnet</c> first on <c>PATH</c> (it prints a fixed
/// <c>dotnet package search --format json</c> result and records what it was given), a dummy token and its own temp
/// <c>RUNNER_TEMP</c> (deleted on dispose). The JSON fixtures are the shapes NuGet 7 printed against a loopback feed.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class PushLoginCheckScriptTests : IDisposable
{
    private const string Token = "dummy-token-4f2a";

    private const string Accepted =
        "{\"version\": 2, \"problems\": [], \"searchResult\": [{\"sourceName\": \"github\", \"packages\": []}]}";

    private const string Refused =
        "{\"version\": 2, \"problems\": [], \"searchResult\": [{\"sourceName\": \"github\", \"problems\": "
        + "[{\"text\": \"Unable to load the service index for source github.\", \"problemType\": \"Error\"}], \"packages\": []}]}";

    private const string NoSearch =
        "{\"version\": 2, \"problems\": [], \"searchResult\": [{\"sourceName\": \"github\", \"problems\": "
        + "[{\"text\": \"The source does not have a search service.\", \"problemType\": \"Error\"}], \"packages\": []}]}";

    private const string TopLevelProblem =
        "{\"version\": 2, \"problems\": [{\"text\": \"No source.\", \"problemType\": \"Error\"}], \"searchResult\": []}";

    private const string OtherSource =
        "{\"version\": 2, \"problems\": [], \"searchResult\": [{\"sourceName\": \"nuget.org\", \"packages\": []}]}";

    private const string ExtraTextFields =
        "{\"version\": 2, \"text\": \"leak-top\", \"problems\": [{\"text\": \"Top problem.\", \"problemType\": \"Error\", "
        + "\"detail\": {\"text\": \"leak-nested\"}}], \"searchResult\": [{\"sourceName\": \"github\", \"text\": \"leak-result\", "
        + "\"problems\": [{\"text\": \"Result problem.\", \"problemType\": \"Error\"}], "
        + "\"packages\": [{\"id\": \"RoomKit\", \"text\": \"leak-package\"}]}]}";

    private const string OddProblemTexts =
        "{\"version\": 2, \"problems\": [{\"problemType\": \"Error\"}, {\"text\": null}, {\"text\": 7}, "
        + "{\"text\": \"Real problem.\"}], \"searchResult\": []}";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly string folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roomkit-push-check-" + Guid.NewGuid().ToString("N"))).FullName;

    [ShellScriptFact]
    public void AcceptedLogin_ExitsZeroAndRemovesTheConfig()
    {
        (int code, string output, _) = Run(Accepted, 0);

        Assert.Equal(0, code);
        Assert.Equal("GitHub Packages accepted the login\n", output);
        Assert.Empty(Directory.GetFileSystemEntries(RunnerTemp()));
    }

    [ShellScriptTheory]
    [InlineData(Refused, "Unable to load the service index for source github.")]
    [InlineData(NoSearch, "The source does not have a search service.")]
    [InlineData(TopLevelProblem, "No source.")]
    [InlineData(OtherSource, "")]
    [InlineData("", "")]
    [InlineData("not json", "")]
    public void RefusedLoginOrAnyProblem_ExitsOneNamesTheProblemAndRemovesTheConfig(string json, string problem)
    {
        (int code, string output, string error) = Run(json, 0);

        Assert.Equal(1, code);
        Assert.DoesNotContain("accepted", output, StringComparison.Ordinal);
        Assert.Contains("GitHub Packages refused the login; nothing was released or pushed\n", error, StringComparison.Ordinal);
        Assert.Contains(problem, error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(RunnerTemp()));
    }

    [ShellScriptFact]
    public void RefusedLogin_PrintsOnlyTheProblemTextsAndNoOtherTextField()
    {
        (int code, string output, string error) = Run(ExtraTextFields, 0);

        Assert.Equal(1, code);
        Assert.Contains("GitHub Packages refused the login; nothing was released or pushed\n", error, StringComparison.Ordinal);
        Assert.Contains("Top problem.\n", error, StringComparison.Ordinal);
        Assert.Contains("Result problem.\n", error, StringComparison.Ordinal);
        foreach (string leak in new[] { "leak-top", "leak-result", "leak-package", "leak-nested" })
        {
            Assert.DoesNotContain(leak, output, StringComparison.Ordinal);
            Assert.DoesNotContain(leak, error, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFileSystemEntries(RunnerTemp()));
    }

    [ShellScriptFact]
    public void ProblemWithoutAStringText_AddsNothingButTheOtherTextsArePrinted()
    {
        (int code, _, string error) = Run(OddProblemTexts, 0);

        Assert.Equal(1, code);
        Assert.Contains("Real problem.\n", error, StringComparison.Ordinal);
        string[] lines = error.Split('\n');
        Assert.DoesNotContain("null", lines);
        Assert.DoesNotContain("7", lines);
        Assert.Equal(["GitHub Packages refused the login; nothing was released or pushed", "Real problem.", ""], lines);
        Assert.Empty(Directory.GetFileSystemEntries(RunnerTemp()));
    }

    [ShellScriptFact]
    public void FailingSearch_ExitsOne()
    {
        (int code, _, _) = Run(Accepted, 3);

        Assert.NotEqual(0, code);
        Assert.Empty(Directory.GetFileSystemEntries(RunnerTemp()));
    }

    [ShellScriptFact]
    public void Search_GetsTheTempConfigWithThePlaceholderAndNeverTheToken()
    {
        (int code, string output, string error) = Run(Accepted, 0);

        Assert.Equal(0, code);
        string arguments = File.ReadAllText(Path.Combine(folder, "arguments"));
        string configFile = Path.Combine(RunnerTemp(), "roomkit-nuget-check", "nuget.config");
        Assert.Equal(
            "package\nsearch\nRoomKit\n--source\ngithub\n--configfile\n" + configFile + "\n--take\n1\n--format\njson\n",
            arguments);
        string config = File.ReadAllText(Path.Combine(folder, "config"));
        Assert.Contains("<add key=\"ClearTextPassword\" value=\"%GITHUB_TOKEN%\" />", config, StringComparison.Ordinal);
        Assert.Equal("700 600\n", File.ReadAllText(Path.Combine(folder, "modes")));
        Assert.DoesNotContain(Token, arguments + config + output + error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private string RunnerTemp()
    {
        return Path.Combine(folder, "runner-temp");
    }

    /// <summary>Runs the step's script with a fake <c>dotnet</c> that records its arguments, a copy of the config
    /// and the modes of the config folder and file, prints <paramref name="json"/> and exits with
    /// <paramref name="exitCode"/>.</summary>
    private (int Code, string Output, string Error) Run(string json, int exitCode)
    {
        string bin = Directory.CreateDirectory(Path.Combine(folder, "bin")).FullName;
        Directory.CreateDirectory(RunnerTemp());
        File.WriteAllText(Path.Combine(folder, "search.json"), json);
        string fake = Path.Combine(bin, "dotnet");
        File.WriteAllText(
            fake,
            "#!/bin/sh\n"
            + "for argument in \"$@\"; do printf '%s\\n' \"$argument\"; done > \"$FAKE_DIR/arguments\"\n"
            + "config=\"$RUNNER_TEMP/roomkit-nuget-check/nuget.config\"\n"
            + "cp \"$config\" \"$FAKE_DIR/config\"\n"
            + "mode() { ls -ld \"$1\" | cut -c2-10 | sed 's/rwx/7/g; s/rw-/6/g; s/---/0/g'; }\n"
            + "echo \"$(mode \"$RUNNER_TEMP/roomkit-nuget-check\") $(mode \"$config\")\" > \"$FAKE_DIR/modes\"\n"
            + "cat \"$FAKE_DIR/search.json\"\n"
            + "exit " + exitCode + "\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string script = Path.Combine(folder, "check.sh");
        File.WriteAllText(script, CheckScript());

        ProcessStartInfo start = new("/bin/sh")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        // jq is in /usr/bin on macOS and Linux, and in Homebrew's folders on some runners.
        start.Environment["PATH"] = bin + ":/usr/bin:/bin:/opt/homebrew/bin:/usr/local/bin";
        start.Environment["RUNNER_TEMP"] = RunnerTemp();
        start.Environment["GITHUB_TOKEN"] = Token;
        start.Environment["FAKE_DIR"] = folder;

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the login check script did not exit within " + ProcessTimeout);
        }

        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>The step's <c>run: |</c> block, with its ten-space YAML indent removed.</summary>
    private static string CheckScript()
    {
        string yaml = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), ".github", "workflows", "release.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = yaml.Split('\n');
        int step = Array.IndexOf(lines, "      - name: Check the GitHub Packages login");
        Assert.True(step >= 0, "missing step Check the GitHub Packages login");
        int run = Array.IndexOf(lines, "        run: |", step);
        Assert.True(run > step, "missing run: | in the login check step");
        IEnumerable<string> body = lines.Skip(run + 1)
            .TakeWhile(line => line.Length == 0 || line.StartsWith("          ", StringComparison.Ordinal))
            .Select(line => line.Length == 0 ? line : line[10..]);
        return string.Join("\n", body).TrimEnd('\n') + "\n";
    }
}
