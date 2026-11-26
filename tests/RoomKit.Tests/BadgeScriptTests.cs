using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RoomKit.Tests;

/// <summary>The app badge: the <c>badge</c> field of a push in <c>sw.js</c> and <c>setRoomKitBadge</c> in
/// <c>push.js</c>, checked by running <c>Shared/BadgeHarness.js</c> with <c>node</c>. The harness loads the real scripts
/// into a <c>node:vm</c> context with stub globals and a fake Badging API, runs one named scenario and prints its
/// result as JSON. Each test copies the harness into its own temp folder (deleted on dispose) and waits for node to
/// exit within <see cref="NodeExitTimeout"/>. The docs and the payload comment are read as text.</summary>
public sealed class BadgeScriptTests : IDisposable
{
    private static readonly TimeSpan NodeExitTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] InvalidLabels =
    [
        "missing", "negative", "tooBig", "fraction", "string", "stringZero", "boolean", "null", "object", "array",
        "notANumber", "infinity",
    ];

    private readonly string folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roomkit-badge-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Push_WithBadgeOneTo999_SetsTheBadgeOnceAndNeverClears()
    {
        using JsonDocument result = Run("workerValues");
        JsonElement valid = result.RootElement.GetProperty("valid");

        foreach ((string label, int badge) in new[] { ("one", 1), ("seven", 7), ("max", 999) })
        {
            JsonElement push = valid.GetProperty(label);
            Assert.Equal(["set:" + badge], Strings(push.GetProperty("calls")));
            AssertHandled(push);
        }
    }

    [Fact]
    public void Push_WaitUntilCoversTheBadgeCallAndTheNotification()
    {
        using JsonDocument result = Run("workerCovers");

        foreach (string first in new[] { "badge", "notification" })
        {
            JsonElement order = result.RootElement.GetProperty(first);
            Assert.Equal(1, order.GetProperty("waits").GetInt32());
            Assert.Equal(["pending", "pending", "resolved"], Strings(order.GetProperty("steps")));
            Assert.Equal(["set:4"], Strings(order.GetProperty("calls")));
            Assert.Equal(1, order.GetProperty("notifications").GetInt32());
        }
    }

    [Fact]
    public void Push_WithBadgeZero_ClearsTheBadgeOnceAndNeverSets()
    {
        using JsonDocument result = Run("workerValues");

        foreach (string label in new[] { "zero", "negativeZero" })
        {
            JsonElement push = result.RootElement.GetProperty(label);
            Assert.Equal(["clear"], Strings(push.GetProperty("calls")));
            AssertHandled(push);
        }
    }

    [Fact]
    public void Push_WithoutBadgeOrWithAnInvalidOne_MakesNoBadgingCall()
    {
        using JsonDocument result = Run("workerValues");
        JsonElement invalid = result.RootElement.GetProperty("invalid");

        Assert.Equal(InvalidLabels, invalid.EnumerateObject().Select(item => item.Name).ToArray());
        foreach (string label in InvalidLabels)
        {
            JsonElement push = invalid.GetProperty(label);
            Assert.True(push.GetProperty("calls").GetArrayLength() == 0, label + " made a Badging call");
            AssertHandled(push);
        }
    }

    [Fact]
    public void Push_ShowsTheSameNotificationWithOrWithoutBadge_ForEveryKind()
    {
        using JsonDocument result = Run("workerKinds");
        JsonElement kinds = result.RootElement;

        Assert.Equal(["needsHuman", "approval", "update", "test", "unknown"], kinds.EnumerateObject().Select(item => item.Name).ToArray());
        foreach (JsonProperty kind in kinds.EnumerateObject())
        {
            JsonElement plain = kind.Value.GetProperty("plain");
            JsonElement badged = kind.Value.GetProperty("badged");
            AssertHandled(plain);
            AssertHandled(badged);
            Assert.Empty(Strings(plain.GetProperty("calls")));
            Assert.Equal(["set:7"], Strings(badged.GetProperty("calls")));
            Assert.Equal(plain.GetProperty("notifications").GetRawText(), badged.GetProperty("notifications").GetRawText());
        }

        JsonElement approval = kinds.GetProperty("approval").GetProperty("badged").GetProperty("notifications")[0];
        Assert.Equal("New items to approve", approval.GetProperty("title").GetString());
        Assert.Equal("3 new items in Room", approval.GetProperty("options").GetProperty("body").GetString());
        Assert.Equal("Needs you", kinds.GetProperty("needsHuman").GetProperty("plain").GetProperty("notifications")[0].GetProperty("title").GetString());
        Assert.Equal("Harness", kinds.GetProperty("unknown").GetProperty("plain").GetProperty("notifications")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Push_WithAMissingThrowingOrRejectingBadgingApi_StillShowsTheNotification()
    {
        using JsonDocument result = Run("workerBroken");
        string usual = result.RootElement.GetProperty("plain").GetProperty("notifications").GetRawText();

        foreach (string mode in new[] { "missing", "throws", "rejects" })
        {
            JsonElement variant = result.RootElement.GetProperty("modes").GetProperty(mode);
            foreach ((string action, string call) in new[] { ("set", "set:5"), ("clear", "clear") })
            {
                JsonElement push = variant.GetProperty(action);
                AssertHandled(push);
                Assert.Equal(usual, push.GetProperty("notifications").GetRawText());
                string[] expected = string.Equals(mode, "missing", StringComparison.Ordinal) ? [] : [call];
                Assert.Equal(expected, Strings(push.GetProperty("calls")));
            }
        }
    }

    [Fact]
    public void SetRoomKitBadge_SetsOrClearsForValidCounts_AndIgnoresEveryOtherValue()
    {
        using JsonDocument result = Run("page");
        JsonElement root = result.RootElement;

        foreach ((string label, string call) in new[] { ("one", "set:1"), ("seven", "set:7"), ("max", "set:999"), ("zero", "clear") })
        {
            JsonElement answer = root.GetProperty("valid").GetProperty(label);
            Assert.True(answer.GetProperty("promise").GetBoolean());
            Assert.True(answer.GetProperty("answer").GetBoolean(), label);
            Assert.Equal([call], Strings(answer.GetProperty("calls")));
        }

        foreach (string label in InvalidLabels)
        {
            JsonElement answer = root.GetProperty("invalid").GetProperty(label);
            Assert.True(answer.GetProperty("promise").GetBoolean(), label);
            Assert.True(answer.GetProperty("answer").ValueKind == JsonValueKind.False, label + " did not resolve to false");
            Assert.Empty(Strings(answer.GetProperty("calls")));
        }
    }

    [Fact]
    public void SetRoomKitBadge_ResolvesFalseWhenTheBadgingApiIsMissingThrowsOrRejects()
    {
        using JsonDocument result = Run("page");

        foreach (string mode in new[] { "missing", "throws", "rejects" })
        {
            JsonElement variant = result.RootElement.GetProperty("modes").GetProperty(mode);
            foreach ((string action, string call) in new[] { ("set", "set:5"), ("clear", "clear") })
            {
                JsonElement answer = variant.GetProperty(action);
                Assert.True(answer.GetProperty("promise").GetBoolean(), mode + " " + action);
                Assert.True(answer.GetProperty("answer").ValueKind == JsonValueKind.False, mode + " " + action + " did not resolve to false");
                string[] expected = string.Equals(mode, "missing", StringComparison.Ordinal) ? [] : [call];
                Assert.Equal(expected, Strings(answer.GetProperty("calls")));
            }
        }
    }

    [Fact]
    public void SetRoomKitBadge_ResolvesTrueOnlyAfterTheBadgingCallSettles()
    {
        using JsonDocument result = Run("pageSettles");

        Assert.Equal("pending", result.RootElement.GetProperty("before").GetString());
        Assert.Equal("resolved:true", result.RootElement.GetProperty("after").GetString());
        Assert.Equal(["set:9"], Strings(result.RootElement.GetProperty("calls")));
    }

    [Fact]
    public void LoadingPushJs_MakesNoBadgingCall()
    {
        using JsonDocument result = Run("page");

        foreach (JsonProperty load in result.RootElement.GetProperty("loads").EnumerateObject())
        {
            Assert.Empty(Strings(load.Value.GetProperty("calls")));
            Assert.Equal("function", load.Value.GetProperty("helper").GetString());
        }

        Assert.Equal(4, result.RootElement.GetProperty("loads").EnumerateObject().Count());
    }

    [Fact]
    public void Docs_AndThePayloadComment_NameTheBadgeFieldAndTheHelper()
    {
        string docs = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), "docs", "roomkit.md"));
        Assert.Contains("`badge`", docs, StringComparison.Ordinal);
        Assert.Contains("`setRoomKitBadge(count)`", docs, StringComparison.Ordinal);
        Assert.Contains("0 to 999", docs, StringComparison.Ordinal);
        Assert.Contains("separate from `count`", docs, StringComparison.Ordinal);
        Assert.Contains("`0` clears it", docs, StringComparison.Ordinal);
        Assert.Contains("leaves the badge as it is", docs, StringComparison.Ordinal);

        string worker = File.ReadAllText(Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit", "wwwroot", "sw.js"));
        Assert.Contains("// Push: the payload holds only kind, project, job, stage, version, previous, reason, count and badge.", worker, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // One push, handled: the handler threw nothing, gave one waitUntil promise that resolved, and showed one notification.
    private static void AssertHandled(JsonElement push)
    {
        Assert.Equal("", push.GetProperty("threw").GetString());
        Assert.Equal(1, push.GetProperty("waits").GetInt32());
        Assert.Equal("resolved", push.GetProperty("state").GetString());
        Assert.Equal(1, push.GetProperty("notifications").GetArrayLength());
    }

    private static string[] Strings(JsonElement array)
    {
        return [.. array.EnumerateArray().Select(item => item.GetString() ?? "")];
    }

    /// <summary>Copies the harness into the temp folder, writes the scenario next to it, runs node and returns the
    /// JSON it printed; fails the test when node is missing, does not exit in time or fails.</summary>
    private JsonDocument Run(string scenario)
    {
        string harness = Path.Combine(folder, "BadgeHarness.js");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Shared", "BadgeHarness.js"), harness, overwrite: true);
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
            Assert.Fail("BadgeScriptTests needs node on PATH: " + error.Message);
            throw;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(NodeExitTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the badge harness did not exit within " + NodeExitTimeout);
            }

            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "the badge harness failed: " + errors.Result);
            return JsonDocument.Parse(output.Result);
        }
    }
}
