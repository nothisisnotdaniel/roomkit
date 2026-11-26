using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RoomKit.Tests;

/// <summary>The host hook registry (<c>roomKitHooks</c> in <c>counts.js</c>, run by <c>loadJobCounts</c> and by
/// <c>checkChanges</c> in <c>live.js</c>), checked by running <c>Shared/HostHooksHarness.js</c> with <c>node</c>. The
/// harness loads the real scripts into a <c>node:vm</c> context with stub globals, a faked network and no timer, runs
/// one named scenario and prints its ordered call log as JSON. Each test copies the harness into its own temp folder
/// (deleted on dispose) and waits for node to exit within <see cref="NodeExitTimeout"/>.</summary>
public sealed class HostHooksScriptTests : IDisposable
{
    private static readonly TimeSpan NodeExitTimeout = TimeSpan.FromSeconds(60);

    private const string Hook = "keys=firstCheck+jobsChanged+recovered";

    private readonly string folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roomkit-host-hooks-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void On_TakesOnlyTheTwoNamesAndFunctions_AndTheRegistryIsFrozenAndConst()
    {
        using JsonDocument result = Run("registry");
        JsonElement root = result.RootElement;

        foreach (JsonProperty attempt in root.GetProperty("errors").EnumerateObject())
        {
            Assert.True(string.Equals("TypeError", attempt.Value.GetString(), StringComparison.Ordinal), attempt.Name + " did not throw a TypeError");
        }

        Assert.Equal(10, root.GetProperty("errors").EnumerateObject().Count());
        Assert.True(root.GetProperty("frozen").GetBoolean());
        Assert.Equal(["on"], Strings(root.GetProperty("keys")));
        Assert.True(root.GetProperty("sameOn").GetBoolean());
        Assert.True(root.GetProperty("extra").GetBoolean());
        Assert.False(root.GetProperty("onGlobalObject").GetBoolean());
        Assert.Equal("TypeError", root.GetProperty("assignOn").GetString());
        Assert.Equal("ignored", root.GetProperty("sloppyAssign").GetString());
        Assert.Equal("TypeError", root.GetProperty("reassign").GetString());
        Assert.True(root.GetProperty("loaded").GetBoolean());
        // Only the two valid hooks ran; none of the refused ones ("bad") was registered.
        Assert.Equal(
            ["fetchBoard", "jobCounts:valid", "authFetch", "noteChanges", "liveChecked:valid", "refreshShownViews", "setBoardStatus", "updateRunningTimes"],
            Strings(root.GetProperty("log")));
    }

    [Fact]
    public void LoadJobCounts_CallsEachHookOnceInOrder_WithTheBoardJobs_AfterTheCountsAreSet()
    {
        using JsonDocument result = Run("jobCountsSuccess");
        JsonElement root = result.RootElement;

        Assert.True(root.GetProperty("loaded").GetBoolean());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("running").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("blocked").GetInt32());
        Assert.True(root.GetProperty("sameArray").GetBoolean());
        Assert.Equal(4, root.GetProperty("jobs").GetArrayLength());
        // Each hook logs the counts it saw: the new ones, so they were set before it ran; b is async and awaited.
        Assert.Equal(["fetchBoard", "jobCounts:a:2/1", "jobCounts:b:2/1", "jobCounts:c:2/1"], Strings(root.GetProperty("log")));
    }

    [Fact]
    public void LoadJobCounts_DoesNotCallHooks_OnAFailedFetchNoJobsOrADroppedAnswer()
    {
        using JsonDocument result = Run("jobCountsFailures");
        JsonElement root = result.RootElement;

        foreach (string name in new[] { "failedFetch", "noJobs", "notArray", "notJson" })
        {
            Assert.False(root.GetProperty(name).GetBoolean(), name);
        }

        Assert.Equal(JsonValueKind.Null, root.GetProperty("countsAfterFailures").ValueKind);
        Assert.True(root.GetProperty("newer").GetBoolean());
        Assert.True(root.GetProperty("older").GetBoolean());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("running").GetInt32());
        // Only the newer answer (one job) reached the hook; the older one, answered last, was dropped.
        Assert.Equal([1], root.GetProperty("seenLengths").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(
            ["fetchBoard", "fetchBoard", "fetchBoard", "fetchBoard", "-- overlap", "fetchBoard", "fetchBoard", "jobCounts:a:1/0"],
            Strings(root.GetProperty("log")));
    }

    [Fact]
    public void CheckChanges_AwaitsEachHookInOrder_AfterNoteChangesAndBeforeTheRefresh_WithTheRightPayload()
    {
        using JsonDocument result = Run("liveSequence");
        JsonElement root = result.RootElement;
        string[][] logs = [.. root.GetProperty("logs").EnumerateArray().Select(Strings)];

        Assert.Equal(7, logs.Length);
        // 1: failed check, no hook.
        Assert.Equal(["authFetch"], logs[0]);
        // 2: the first answer, after a lost connection: firstCheck and recovered.
        Assert.Equal(
            ["authFetch", "setBoardStatus", "noteChanges", Live("a", true, false, true, "j1"), Live("b", true, false, true, "j1"),
                "refreshShownViews", "pollBoard", "updateRunningTimes", "refreshSearch"],
            logs[1]);
        // 3: the same jobs.
        Assert.Equal(
            ["authFetch", "noteChanges", Live("a", false, false, false, "j1"), Live("b", false, false, false, "j1"),
                "refreshShownViews", "pollBoard", "updateRunningTimes"],
            logs[2]);
        // 4: changed jobs.
        Assert.Equal(
            ["authFetch", "noteChanges", Live("a", false, true, false, "j2"), Live("b", false, true, false, "j2"),
                "refreshShownViews", "pollBoard", "updateRunningTimes"],
            logs[3]);
        // 5: failed again, no hook.
        Assert.Equal(["authFetch"], logs[4]);
        // 6: recovered with the same jobs.
        Assert.Equal(
            ["authFetch", "setBoardStatus", "noteChanges", Live("a", false, false, true, "j2"), Live("b", false, false, true, "j2"),
                "refreshShownViews", "pollBoard", "updateRunningTimes", "refreshSearch"],
            logs[5]);
        // 7: changed jobs.
        Assert.Equal(
            ["authFetch", "noteChanges", Live("a", false, true, false, "j3"), Live("b", false, true, false, "j3"),
                "refreshShownViews", "pollBoard", "updateRunningTimes"],
            logs[6]);
        Assert.False(root.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void CheckChanges_APendingHookHoldsBackTheRefreshUntilItSettles()
    {
        using JsonDocument result = Run("livePendingHook");
        JsonElement root = result.RootElement;

        Assert.Equal(["authFetch", "noteChanges", "liveChecked:held"], Strings(root.GetProperty("whileHeld")));
        Assert.True(root.GetProperty("pendingWhileHeld").GetBoolean());
        Assert.Equal(
            ["authFetch", "noteChanges", "liveChecked:held", "liveChecked:after", "refreshShownViews", "setBoardStatus", "updateRunningTimes"],
            Strings(root.GetProperty("log")));
        Assert.False(root.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void CheckChanges_DoesNotCallHooks_WhenTheBoardIsHiddenTheCheckFailsOrIsSkipped()
    {
        using JsonDocument result = Run("liveSkipped");
        JsonElement logs = result.RootElement.GetProperty("logs");

        Assert.Equal(["authFetch", "noteChanges", "refreshShownViews", "setBoardStatus"], Strings(logs.GetProperty("boardHidden")));
        Assert.Equal(["authFetch", "noteChanges", "refreshShownViews", "setBoardStatus", "fetchBoard"], Strings(logs.GetProperty("boardHiddenChanged")));
        Assert.Equal(["authFetch"], Strings(logs.GetProperty("failed")));
        Assert.Equal(["authFetch"], Strings(logs.GetProperty("notOk")));
        Assert.Equal(["authFetch"], Strings(logs.GetProperty("badData")));
        Assert.Empty(Strings(logs.GetProperty("pageHidden")));
        Assert.Empty(Strings(logs.GetProperty("tokenView")));
        Assert.Equal(["authFetch"], Strings(logs.GetProperty("checkPending")));
        Assert.DoesNotContain("liveChecked", result.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.False(result.RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void FailingHooks_AreLoggedOnceEach_AndStopNeitherTheNextHooksTheCountsNorTheChecks()
    {
        using JsonDocument result = Run("errors");
        JsonElement root = result.RootElement;

        Assert.True(root.GetProperty("loaded").GetBoolean());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("running").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("blocked").GetInt32());
        Assert.Equal(
            ["fetchBoard", "jobCounts:throws", "console.error", "jobCounts:rejects", "console.error", "jobCounts:after:2/1"],
            Strings(root.GetProperty("countsLog")));
        JsonElement[] checks = [.. root.GetProperty("checks").EnumerateArray()];
        Assert.Equal(2, checks.Length);
        Assert.Equal(
            ["authFetch", "noteChanges", "liveChecked:throws", "console.error", "liveChecked:rejects", "console.error",
                Live("after", true, false, false, "j1"), "refreshShownViews", "setBoardStatus", "updateRunningTimes"],
            Strings(checks[0].GetProperty("log")));
        Assert.Equal(
            ["authFetch", "noteChanges", "liveChecked:throws", "console.error", "liveChecked:rejects", "console.error",
                Live("after", false, true, false, "j2"), "refreshShownViews", "pollBoard", "updateRunningTimes"],
            Strings(checks[1].GetProperty("log")));
        Assert.All(checks, check => Assert.False(check.GetProperty("pending").GetBoolean()));
    }

    [Fact]
    public void NoHooks_CountsAndChecksRunAsBefore()
    {
        using JsonDocument result = Run("noHooks");
        JsonElement root = result.RootElement;

        Assert.True(root.GetProperty("loaded").GetBoolean());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("running").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("blocked").GetInt32());
        Assert.Equal(["fetchBoard"], Strings(root.GetProperty("countsLog")));
        string[][] logs = [.. root.GetProperty("logs").EnumerateArray().Select(Strings)];
        Assert.Equal(["authFetch", "noteChanges", "refreshShownViews", "setBoardStatus", "updateRunningTimes"], logs[0]);
        Assert.Equal(["authFetch", "noteChanges", "refreshShownViews", "pollBoard", "updateRunningTimes"], logs[1]);
        Assert.False(root.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public void Docs_HostHooksSection_NamesTheApi()
    {
        string docs = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), "docs", "roomkit.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = docs.IndexOf("\n## Host hooks\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing ## Host hooks");
        int end = docs.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = end < 0 ? docs[start..] : docs[start..end];

        foreach (string name in new[] { "roomKitHooks.on", "`jobCounts`", "data.jobs", "`liveChecked`", "jobsChanged", "recovered", "firstCheck", "console.error", "caught" })
        {
            Assert.Contains(name, section, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Live(string label, bool first, bool jobsChanged, bool recovered, string last)
    {
        return "liveChecked:" + label + ":first=" + Bool(first) + ",jobs=" + Bool(jobsChanged) + ",recovered=" + Bool(recovered)
            + ",last=" + last + "," + Hook;
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string[] Strings(JsonElement array)
    {
        return [.. array.EnumerateArray().Select(item => item.GetString() ?? "")];
    }

    /// <summary>Copies the harness into the temp folder, writes the scenario next to it, runs node and returns the
    /// JSON it printed; fails the test when node is missing, does not exit in time or fails.</summary>
    private JsonDocument Run(string scenario)
    {
        string harness = Path.Combine(folder, "HostHooksHarness.js");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Shared", "HostHooksHarness.js"), harness);
        string file = Path.Combine(folder, "scenario.json");
        string wwwroot = Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit", "wwwroot");
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = scenario, ["wwwroot"] = wwwroot }));

        ProcessStartInfo start = new("node")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(file);

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception error)
        {
            Assert.Fail("HostHooksScriptTests needs node on PATH: " + error.Message);
            throw;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(NodeExitTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the host hooks harness did not exit within " + NodeExitTimeout);
            }

            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "the host hooks harness failed: " + errors.Result);
            return JsonDocument.Parse(output.Result);
        }
    }
}
