using System.Text.RegularExpressions;

namespace RoomKit.Tests;

/// <summary><c>.github/workflows/ci.yml</c> and <c>release.yml</c>, read as text with a line and indent parser: the
/// triggers, ci.yml's change check, the <c>if:</c> of release.yml's version job and ci.yml's test job, the jobs and
/// their <c>needs:</c>, every permission block, the SHA pins, the checkouts, PR fields and tokens only through
/// <c>env:</c>, and the release job's steps in order.</summary>
public sealed partial class WorkflowTests
{
    private const string Feed = "https://nuget.pkg.github.com/nothisisnotdaniel/index.json";

    /// <summary>The pinned SHAs, the same as ControlRoom's release workflow.</summary>
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        ["actions/checkout"] = "11d5960a326750d5838078e36cf38b85af677262",
        ["actions/setup-dotnet"] = "67a3573c9a986a3f9c594539f4ab511d57bb3ce9",
        ["actions/attest-build-provenance"] = "4d101475d8b20a2381f78447822ac1eab6504dd8",
    };

    [Fact]
    public void Release_Trigger_IsOnlyAClosedPullRequestIntoMain()
    {
        string yaml = Workflow("release.yml");

        Assert.Equal(
            "on:\n  pull_request:\n    types:\n      - \"closed\"\n    branches:\n      - \"main\"\n",
            TopLevelBlock(yaml, "on:"));
        Assert.Single(TopLevelKey().Matches(yaml), match => match.Value == "on:");
        Assert.DoesNotContain("\n  push:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_VersionJob_RunsOnlyForAMergedDevelopPullRequestFromThisRepositoryAndReadsTheTitleFromEnv()
    {
        string yaml = Workflow("release.yml");
        string version = JobBlock(yaml, "version");

        string condition = Assert.Single(yaml.Split('\n'), line => line.TrimStart().StartsWith("if:", StringComparison.Ordinal));
        Assert.Contains("\n" + condition + "\n", version, StringComparison.Ordinal);
        Assert.Equal(
            "    if: github.event.pull_request.merged == true "
            + "&& github.event.pull_request.head.ref == 'develop' "
            + "&& github.event.pull_request.head.repo.full_name == github.repository",
            condition);
        Assert.Contains("\n    env:\n      PR_TITLE: ${{ github.event.pull_request.title }}\n", version, StringComparison.Ordinal);
        string step = Step(version, "Version");
        Assert.Contains(
            "RELEASE_VERSION=\"$(git tag --list 'v*' | sh .github/scripts/release-version.sh \"$PR_TITLE\")\"",
            step,
            StringComparison.Ordinal);
        Assert.Single(RunBlocks(yaml), run => run.Contains("release-version.sh", StringComparison.Ordinal));
    }

    [Fact]
    public void Release_Jobs_AreVersionThenTestThenRelease()
    {
        string yaml = Workflow("release.yml");

        Assert.Equal(["version", "test", "release"], JobNames(yaml));
        Assert.Equal([], NeededJobs(yaml, "version"));
        Assert.Equal(["version"], NeededJobs(yaml, "test"));
        Assert.Equal(["version", "test"], NeededJobs(yaml, "release"));

        string test = JobBlock(yaml, "test");
        Assert.Contains("\n        os: [macos-15, windows-latest]\n", test, StringComparison.Ordinal);
        Assert.Contains("\n    runs-on: ${{ matrix.os }}\n", test, StringComparison.Ordinal);
        Assert.Contains("\n        run: dotnet test RoomKit.slnx -c Release\n", test, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_ReleaseJob_ChecksOutPacksSumsAttestsReleasesAndPushesInOrder()
    {
        string release = JobBlock(Workflow("release.yml"), "release");

        Assert.Equal(
            ["Check out the release commit", "Set up .NET", "Pack", "Checksums", "Attest build provenance",
                "Check the GitHub Packages login", "Create GitHub release", "Push to GitHub Packages"],
            StepNames(release));
        AssertInOrder(release, [
            "uses: actions/checkout@",
            "run: dotnet pack src/RoomKit -c Release -p:Version=\"$RELEASE_VERSION\" -o dist\n",
            "shasum -a 256 \"RoomKit.$RELEASE_VERSION.nupkg\" > SHA256SUMS\n",
            "uses: actions/attest-build-provenance@",
            "dotnet package search RoomKit --source github --configfile ",
            "gh release create \"v$RELEASE_VERSION\" \"dist/RoomKit.$RELEASE_VERSION.nupkg\" dist/SHA256SUMS ",
            "dotnet nuget push \"dist/RoomKit.$RELEASE_VERSION.nupkg\" --source github --configfile ",
        ]);
        Assert.Contains("--target \"$RELEASE_TARGET\"", Step(release, "Create GitHub release"), StringComparison.Ordinal);
        Assert.Contains(
            "subject-path: dist/RoomKit.${{ env.RELEASE_VERSION }}.nupkg\n", Step(release, "Attest build provenance"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Release_Push_PutsNoApiKeyOptionInTheFile()
    {
        string yaml = Workflow("release.yml");

        Assert.DoesNotContain("--api-key", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(" -k ", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_Push_GetsTheTokenOnlyFromEnvAndNamesItOnlyAsTheConfigPlaceholder()
    {
        string yaml = Workflow("release.yml");
        string step = Step(JobBlock(yaml, "release"), "Push to GitHub Packages");

        string[] env = [.. step.Split('\n').SkipWhile(line => line != "        env:").Skip(1)
            .TakeWhile(line => line.StartsWith("          ", StringComparison.Ordinal))];
        Assert.Contains("        env:\n", step, StringComparison.Ordinal);
        Assert.Equal(["          GITHUB_TOKEN: ${{ github.token }}"], env);

        string run = PushRun(yaml);
        Assert.DoesNotContain("$GITHUB_TOKEN", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${GITHUB_TOKEN}", run, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(run, "GITHUB_TOKEN"));
        Assert.Single(Regex.Matches(run, "%GITHUB_TOKEN%"));
        Assert.DoesNotContain("secrets.", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_Push_WritesAClearedConfigWithTheFeedAndThePlaceholderUnderRunnerTemp()
    {
        string run = PushRun(Workflow("release.yml"));

        Assert.Contains("CONFIG_DIR=\"$RUNNER_TEMP/roomkit-nuget\"\n", run, StringComparison.Ordinal);
        Assert.Contains("cat > \"$CONFIG_DIR/nuget.config\" <<'EOF'\n", run, StringComparison.Ordinal);
        Assert.Contains("\n          EOF\n", run, StringComparison.Ordinal);
        Assert.DoesNotContain("$HOME", run, StringComparison.Ordinal);
        Assert.DoesNotContain("~/", run, StringComparison.Ordinal);
        Assert.DoesNotContain("$GITHUB_WORKSPACE", run, StringComparison.Ordinal);
        AssertInOrder(run, [
            "<<'EOF'\n",
            "<packageSources>\n",
            "<clear />\n",
            "<add key=\"github\" value=\"" + Feed + "\" />\n",
            "</packageSources>\n",
            "<packageSourceCredentials>\n",
            "<github>\n",
            "<add key=\"Username\" value=\"x-access-token\" />\n",
            "<add key=\"ClearTextPassword\" value=\"%GITHUB_TOKEN%\" />\n",
            "</github>\n",
            "</packageSourceCredentials>\n",
            "\n          EOF\n",
        ]);
    }

    [Fact]
    public void Release_Push_SendsExactlyThePackageWithTheTempConfigOnly()
    {
        string yaml = Workflow("release.yml");
        string run = PushRun(yaml);

        Assert.Contains(
            "\n          dotnet nuget push \"dist/RoomKit.$RELEASE_VERSION.nupkg\" --source github "
            + "--configfile \"$CONFIG_DIR/nuget.config\"",
            run,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(yaml, "dotnet nuget push"));
        Assert.DoesNotContain("nuget add source", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget update source", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_Push_RemovesTheTempConfigOnEveryExitAndWritesItPrivately()
    {
        string run = PushRun(Workflow("release.yml"));

        AssertInOrder(run, ["umask 077\n", "trap '", "mkdir \"$CONFIG_DIR\"\n", "<<'EOF'\n", "dotnet nuget push "]);
        string trap = Assert.Single(run.Split('\n'), line => line.TrimStart().StartsWith("trap ", StringComparison.Ordinal));
        Assert.Equal("          trap 'rm -rf \"$CONFIG_DIR\"' EXIT", trap);
    }

    [Fact]
    public void Release_LoginCheck_GetsTheTokenOnlyFromEnvAndUsesTheSameConfigAsThePush()
    {
        string yaml = Workflow("release.yml");
        string step = Step(JobBlock(yaml, "release"), "Check the GitHub Packages login");

        string[] env = [.. step.Split('\n').SkipWhile(line => line != "        env:").Skip(1)
            .TakeWhile(line => line.StartsWith("          ", StringComparison.Ordinal))];
        Assert.Equal(["          GITHUB_TOKEN: ${{ github.token }}"], env);

        string run = CheckRun(yaml);
        Assert.DoesNotContain("$GITHUB_TOKEN", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${GITHUB_TOKEN}", run, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(run, "GITHUB_TOKEN"));
        Assert.Equal(Heredoc(PushRun(yaml)), Heredoc(run));
        Assert.Equal(2, Regex.Matches(yaml, "GITHUB_TOKEN: \\$\\{\\{ github\\.token \\}\\}").Count);
    }

    [Fact]
    public void Release_LoginCheck_SearchesTheFeedWithTheTempConfigAndFailsOnAnyProblemBeforeTheRelease()
    {
        string yaml = Workflow("release.yml");
        string run = CheckRun(yaml);

        Assert.Contains("CONFIG_DIR=\"$RUNNER_TEMP/roomkit-nuget-check\"\n", run, StringComparison.Ordinal);
        AssertInOrder(run, [
            "umask 077\n",
            "trap 'rm -rf \"$CONFIG_DIR\"' EXIT\n",
            "mkdir \"$CONFIG_DIR\"\n",
            "<<'EOF'\n",
            "\n          dotnet package search RoomKit --source github --configfile \"$CONFIG_DIR/nuget.config\" "
                + "--take 1 --format json > \"$CONFIG_DIR/search.json\"\n",
            "if ! jq -e '",
            "exit 1\n",
        ]);
        Assert.Single(Regex.Matches(yaml, "dotnet package search"));
        Assert.DoesNotContain("--api-key", run, StringComparison.Ordinal);
        AssertInOrder(JobBlock(yaml, "release"), [
            "- name: Check the GitHub Packages login\n", "- name: Create GitHub release\n", "- name: Push to GitHub Packages\n"]);
    }

    [Fact]
    public void Release_LoginCheck_PrintsOnlyTheProblemTextsWithAFixedJqPath()
    {
        string run = CheckRun(Workflow("release.yml"));

        Assert.DoesNotContain("jq -r '.. ", run, StringComparison.Ordinal);
        string print = Assert.Single(run.Split('\n'), line => line.TrimStart().StartsWith("jq -r ", StringComparison.Ordinal));
        Assert.Contains(".problems[]", print, StringComparison.Ordinal);
        Assert.Contains(".searchResult[]", print, StringComparison.Ordinal);
        Assert.EndsWith(">&2 || true", print, StringComparison.Ordinal);
    }

    /// <summary>The run: block of the release job's login check step.</summary>
    private static string CheckRun(string yaml)
    {
        return Assert.Single(RunBlocks(yaml), run => run.Contains("dotnet package search", StringComparison.Ordinal)) + "\n";
    }

    /// <summary>The text from the <c>&lt;&lt;'EOF'</c> line to its <c>EOF</c> line in a run block.</summary>
    private static string Heredoc(string run)
    {
        int start = run.IndexOf("<<'EOF'\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing heredoc");
        int end = run.IndexOf("\n          EOF\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "missing heredoc end");
        return run[start..end];
    }

    /// <summary>The run: block of the release job's push step (the one run block that pushes).</summary>
    private static string PushRun(string yaml)
    {
        return Assert.Single(RunBlocks(yaml), run => run.Contains("dotnet nuget push", StringComparison.Ordinal)) + "\n";
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void Permissions_AreEmptyAtTheTopAndOnlyTheReleaseJobWrites(string file)
    {
        string yaml = Workflow(file);

        Assert.Equal("permissions: {}\n", TopLevelBlock(yaml, "permissions: {}"));
        string[] jobs = JobNames(yaml);
        Assert.Equal(jobs.Length + 1, Regex.Matches(yaml, "permissions:").Count);
        foreach (string job in jobs)
        {
            string[] expected = file == "release.yml" && job == "release"
                ? ["contents: write", "packages: write", "id-token: write", "attestations: write"]
                : ["contents: read"];
            Assert.Equal(expected, Grants(JobBlock(yaml, job)));
        }

        string[] writes = [.. yaml.Split('\n').Where(line => line.Contains("write", StringComparison.Ordinal)
            && !line.TrimStart().StartsWith('#'))];
        Assert.Equal(file == "release.yml" ? 4 : 0, writes.Length);
    }

    [Theory]
    [InlineData("ci.yml", 3)]
    [InlineData("release.yml", 6)]
    public void EveryAction_IsPinnedToTheSameCommitShaAsControlRoom(string file, int count)
    {
        string[] uses = [.. Workflow(file).Split('\n').Where(line => line.Contains("uses:", StringComparison.Ordinal))];

        Assert.Equal(count, uses.Length);
        foreach (string line in uses)
        {
            Match match = PinnedUse().Match(line);
            Assert.True(match.Success, "not pinned: " + line.Trim());
            Assert.Equal(Pins[match.Groups["action"].Value], match.Groups["sha"].Value);
        }
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void EveryCheckout_KeepsNoCredentialsAndNoExpressionReachesARunScript(string file)
    {
        string yaml = Workflow(file);

        string[] checkouts = [.. StepBodies(yaml).Where(body => body.Contains("uses: actions/checkout@", StringComparison.Ordinal))];
        Assert.NotEmpty(checkouts);
        Assert.All(checkouts, body => Assert.Contains("\n          persist-credentials: false\n", body, StringComparison.Ordinal));
        Assert.Equal(checkouts.Length, Regex.Matches(yaml, "persist-credentials: false").Count);

        List<string> runs = RunBlocks(yaml);
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.DoesNotContain("${{", run, StringComparison.Ordinal));
        Assert.All(runs, run => Assert.DoesNotContain("github.event", run, StringComparison.Ordinal));
    }

    [Fact]
    public void Ci_Trigger_IsAPullRequestIntoOrAPushToDevelopOnly()
    {
        string yaml = Workflow("ci.yml");

        Assert.Equal(
            "on:\n  pull_request:\n    branches:\n      - \"develop\"\n  push:\n    branches:\n      - \"develop\"\n",
            TopLevelBlock(yaml, "on:"));
        Assert.Single(TopLevelKey().Matches(yaml), match => match.Value == "on:");
        Assert.DoesNotContain("paths-ignore", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("main", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_Jobs_AreChangesThenTest()
    {
        string yaml = Workflow("ci.yml");

        Assert.Equal(["changes", "test"], JobNames(yaml));
        Assert.Equal([], NeededJobs(yaml, "changes"));
        Assert.Equal(["changes"], NeededJobs(yaml, "test"));
    }

    [Fact]
    public void Ci_ChangesJob_ChecksOutTheFullHistoryAndRunsTheChangeCheckOnUbuntu()
    {
        string changes = JobBlock(Workflow("ci.yml"), "changes");

        Assert.Contains("\n    runs-on: ubuntu-latest\n", changes, StringComparison.Ordinal);
        Assert.Equal(["contents: read"], Grants(changes));
        Assert.Contains("\n    outputs:\n      tests: ${{ steps.changes.outputs.tests }}\n", changes, StringComparison.Ordinal);
        Assert.Equal(["Check out", "Check what changed"], StepNames(changes));
        Assert.Equal(
            "        uses: actions/checkout@" + Pins["actions/checkout"] + " # v4.4.0\n        with:\n"
            + "          persist-credentials: false\n          fetch-depth: 0\n",
            Step(changes, "Check out"));
        Assert.Equal("        id: changes\n        run: python3 .github/scripts/ci_changes.py\n", Step(changes, "Check what changed"));
        Assert.DoesNotContain("\n    if:", changes, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_TestJob_RunsOnlyWhenTheChangesNeedItAndRunsTheTestsInReleaseOnMacOsAndWindows()
    {
        string test = JobBlock(Workflow("ci.yml"), "test");

        Assert.Contains("\n    needs: changes\n    if: needs.changes.outputs.tests == 'true'\n", test, StringComparison.Ordinal);
        Assert.Contains("\n        os: [macos-15, windows-latest]\n", test, StringComparison.Ordinal);
        Assert.Contains("\n    runs-on: ${{ matrix.os }}\n", test, StringComparison.Ordinal);
        Assert.Contains("\n      fail-fast: false\n", test, StringComparison.Ordinal);
        Assert.Equal(["Check out", "Set up .NET", "Test"], StepNames(test));
        Assert.Equal(
            "        uses: actions/checkout@" + Pins["actions/checkout"] + " # v4.4.0\n        with:\n"
            + "          persist-credentials: false\n",
            Step(test, "Check out"));
        Assert.Equal("        run: dotnet test RoomKit.slnx -c Release\n", Step(test, "Test"));
    }

    private static string Workflow(string name)
    {
        return File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), ".github", "workflows", name))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The top-level line <paramref name="start"/> and every indented line after it, up to the next
    /// top-level key.</summary>
    private static string TopLevelBlock(string yaml, string start)
    {
        int index = yaml.IndexOf("\n" + start + "\n", StringComparison.Ordinal);
        Assert.True(index >= 0, "missing top-level " + start);
        string rest = yaml[(index + 1)..];
        List<string> lines = [rest.Split('\n')[0]];
        lines.AddRange(rest.Split('\n').Skip(1).TakeWhile(line => line.StartsWith(' ')));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The lines after the top-level <c>jobs:</c> line, up to the next top-level key (blank lines kept).</summary>
    private static string[] JobsLines(string yaml)
    {
        string[] lines = yaml.Split('\n');
        int start = Array.IndexOf(lines, "jobs:");
        Assert.True(start >= 0, "missing top-level jobs:");
        return [.. lines.Skip(start + 1).TakeWhile(line => line.Length == 0 || line.StartsWith(' '))];
    }

    private static string[] JobNames(string yaml)
    {
        return [.. JobsLines(yaml).Select(line => JobLine().Match(line))
            .Where(match => match.Success).Select(match => match.Groups["name"].Value)];
    }

    /// <summary>The lines of job <paramref name="job"/>, from its <c>  job:</c> line up to the next job, each ending in
    /// a newline and the block starting with one.</summary>
    private static string JobBlock(string yaml, string job)
    {
        string[] lines = JobsLines(yaml);
        int start = Array.IndexOf(lines, "  " + job + ":");
        Assert.True(start >= 0, "missing job " + job);
        IEnumerable<string> block = lines.Skip(start + 1).TakeWhile(line => !JobLine().IsMatch(line));
        return "\n" + string.Join("\n", [lines[start], .. block]).TrimEnd('\n') + "\n";
    }

    /// <summary>The jobs in job <paramref name="job"/>'s <c>needs:</c> (one name or a <c>[a, b]</c> list).</summary>
    private static string[] NeededJobs(string yaml, string job)
    {
        string[] lines = [.. JobBlock(yaml, job).Split('\n').Where(line => line.StartsWith("    needs:", StringComparison.Ordinal))];
        if (lines.Length == 0)
        {
            return [];
        }

        string value = Assert.Single(lines)["    needs:".Length..].Trim().TrimStart('[').TrimEnd(']');
        return [.. value.Split(',').Select(name => name.Trim())];
    }

    /// <summary>The grants under the job's own <c>permissions:</c> line (comment and blank lines left out).</summary>
    private static string[] Grants(string job)
    {
        string[] lines = [.. job.Split('\n').Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))];
        Assert.Single(lines, line => line == "    permissions:");
        Assert.Single(lines, line => line.TrimStart().StartsWith("permissions:", StringComparison.Ordinal));
        return [.. lines.SkipWhile(line => line != "    permissions:").Skip(1)
            .TakeWhile(line => line.StartsWith("      ", StringComparison.Ordinal)).Select(line => line.Trim())];
    }

    private static string[] StepNames(string text)
    {
        return [.. StepName().Matches(text).Select(match => match.Groups["name"].Value)];
    }

    /// <summary>The lines of step <paramref name="name"/> after its <c>- name:</c> line, up to the next step or job.</summary>
    private static string Step(string text, string name)
    {
        string header = "\n      - name: " + name + "\n";
        int start = text.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing step " + name);
        Assert.Equal(-1, text.IndexOf(header, start + 1, StringComparison.Ordinal));
        start += header.Length;
        return StepBody(text, start);
    }

    /// <summary>The body of every step in <paramref name="yaml"/>.</summary>
    private static List<string> StepBodies(string yaml)
    {
        List<string> bodies = [];
        foreach (Match match in StepName().Matches(yaml))
        {
            bodies.Add(StepBody(yaml, match.Index + match.Length + 1));
        }

        return bodies;
    }

    private static string StepBody(string text, int start)
    {
        string[] lines = text[start..].Split('\n');
        IEnumerable<string> body = lines.TakeWhile(line => line.Length == 0 || line.StartsWith("        ", StringComparison.Ordinal));
        // Up to the last non-blank line, each line ending in a newline.
        return string.Join("\n", body).TrimEnd('\n') + "\n";
    }

    private static void AssertInOrder(string text, string[] parts)
    {
        int last = -1;
        foreach (string part in parts)
        {
            int index = text.IndexOf(part, last + 1, StringComparison.Ordinal);
            Assert.True(index > last, "missing or out of order: " + part);
            last = index;
        }
    }

    /// <summary>The text of every <c>run:</c> script: the inline value, or the indented lines of a block.</summary>
    private static List<string> RunBlocks(string yaml)
    {
        string[] lines = yaml.Split('\n');
        List<string> blocks = [];
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = RunLine().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            string value = match.Groups["value"].Value;
            if (value != "|")
            {
                blocks.Add(value);
                continue;
            }

            int indent = match.Groups["indent"].Value.Length;
            List<string> body = [];
            while (index + 1 < lines.Length
                   && (lines[index + 1].Trim().Length == 0 || lines[index + 1].Length - lines[index + 1].TrimStart().Length > indent))
            {
                index++;
                body.Add(lines[index]);
            }

            blocks.Add(string.Join("\n", body));
        }

        return blocks;
    }

    [GeneratedRegex(@"^  (?<name>[a-z][a-z0-9_-]*):$")]
    private static partial Regex JobLine();

    [GeneratedRegex(@"^      - name: (?<name>.+)$", RegexOptions.Multiline)]
    private static partial Regex StepName();

    [GeneratedRegex(@"^[a-z]+:", RegexOptions.Multiline)]
    private static partial Regex TopLevelKey();

    [GeneratedRegex(@"^\s*(- )?uses: (?<action>[a-z0-9-]+/[a-z0-9-]+)@(?<sha>[0-9a-f]{40}) # v[0-9]+(\.[0-9]+)*$")]
    private static partial Regex PinnedUse();

    [GeneratedRegex(@"^(?<indent>\s*)(- )?run: (?<value>.*)$")]
    private static partial Regex RunLine();
}
