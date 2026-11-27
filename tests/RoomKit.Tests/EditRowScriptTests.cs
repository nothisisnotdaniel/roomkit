using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RoomKit.Tests;

/// <summary>The edit row of a backlog line in <c>edit.js</c> (<c>editRow</c>): which buttons a <c>[?]</c>, <c>[ ]</c>,
/// <c>[h]</c> and <c>[x]</c> line gets, the confirm text of Drop, the request it sends and the notice of a
/// <c>409 waited_on</c> answer (AC-8, AC-9). Runs <c>Shared/EditRowHarness.js</c> with <c>node</c>: it loads the real
/// <c>edit.js</c> into a <c>node:vm</c> context with stub globals, a scripted <c>confirm</c> and a faked
/// <c>authFetch</c>, and waits on the promise each click starts, never on a timer. The harness is copied into this
/// test's own temp folder (deleted on dispose) and node must exit within <see cref="NodeExitTimeout"/>.</summary>
public sealed class EditRowScriptTests : IDisposable
{
    private static readonly TimeSpan NodeExitTimeout = TimeSpan.FromSeconds(60);

    private readonly string folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roomkit-edit-row-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void EditRow_ShowsDropOnProposedAndApprovedLines_ApproveOnlyOnProposed()
    {
        using JsonDocument result = Run();
        JsonElement rows = result.RootElement.GetProperty("rows");

        Assert.Equal(["Approve", "Drop", "To top", "Edit title"], Texts(rows.GetProperty("proposed")));
        Assert.Equal(["Drop", "To top", "Edit title"], Texts(rows.GetProperty("approved")));
        Assert.DoesNotContain("Drop", Texts(rows.GetProperty("user")));
        Assert.DoesNotContain("Approve", Texts(rows.GetProperty("user")));
        Assert.Equal(JsonValueKind.Null, rows.GetProperty("done").ValueKind);

        JsonElement drop = rows.GetProperty("approved").EnumerateArray().First();
        Assert.Equal("Drop #7", drop.GetProperty("label").GetString());
        Assert.Contains("edit-danger", drop.GetProperty("className").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_ConfirmsWithItsOwnText_AndSendsOnlyWhenConfirmed()
    {
        using JsonDocument result = Run();
        JsonElement root = result.RootElement;
        JsonElement texts = root.GetProperty("texts");
        Assert.Equal(
            "Drop this approved item? Its line and story file are removed in one commit.",
            texts.GetProperty("confirmDropApproved").GetString());
        string approved = texts.GetProperty("confirmDropApproved").GetString()!;
        string proposed = texts.GetProperty("confirmDrop").GetString()!;

        foreach ((string name, string text, bool sent) in new[]
        {
            ("approvedNo", approved, false), ("proposedNo", proposed, false),
            ("proposedYes", proposed, true), ("approvedYes", approved, true),
        })
        {
            JsonElement click = root.GetProperty(name);
            Assert.Equal([text], Strings(click.GetProperty("confirms")));
            JsonElement[] requests = [.. click.GetProperty("requests").EnumerateArray()];
            Assert.Equal(sent ? 1 : 0, requests.Length);
            if (sent)
            {
                Assert.Equal("/api/backlog/change", requests[0].GetProperty("url").GetString());
                Assert.Equal("POST", requests[0].GetProperty("method").GetString());
                JsonElement body = requests[0].GetProperty("body");
                Assert.Equal("drop", body.GetProperty("action").GetString());
                Assert.Equal("Alpha", body.GetProperty("project").GetString());
                Assert.Equal("v1", body.GetProperty("version").GetString());
                Assert.Equal(4, body.GetProperty("line").GetInt32());
            }
        }
    }

    [Fact]
    public void WaitedOnAnswer_ShowsTheFixedText_NotTheServerText()
    {
        using JsonDocument result = Run();
        JsonElement root = result.RootElement;
        string expected = "Another item waits for this one (after:). Change or drop that item first. Nothing was changed.";

        Assert.Equal(expected, root.GetProperty("texts").GetProperty("waitedOn").GetString());
        Assert.Equal("Saved.", root.GetProperty("proposedYes").GetProperty("notice").GetString());
        Assert.Equal(expected, root.GetProperty("approvedYes").GetProperty("notice").GetString());
        Assert.True(root.GetProperty("noticeError").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string[] Texts(JsonElement buttons)
    {
        return [.. buttons.EnumerateArray().Select(button => button.GetProperty("text").GetString() ?? "")];
    }

    private static string[] Strings(JsonElement array)
    {
        return [.. array.EnumerateArray().Select(item => item.GetString() ?? "")];
    }

    /// <summary>Copies the harness into the temp folder, writes the scenario next to it, runs node and returns the
    /// JSON it printed; fails the test when node is missing, does not exit in time or fails.</summary>
    private JsonDocument Run()
    {
        string harness = Path.Combine(folder, "EditRowHarness.js");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Shared", "EditRowHarness.js"), harness);
        string file = Path.Combine(folder, "scenario.json");
        string wwwroot = Path.Combine(RepoCopy.RepoFolder(), "src", "RoomKit", "wwwroot");
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["wwwroot"] = wwwroot }));

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
            Assert.Fail("EditRowScriptTests needs node on PATH: " + error.Message);
            throw;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(NodeExitTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the edit row harness did not exit within " + NodeExitTimeout);
            }

            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "the edit row harness failed: " + errors.Result);
            return JsonDocument.Parse(output.Result);
        }
    }
}
