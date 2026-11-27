using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RoomKit.Tests;

/// <summary>Dropping a <c>[?]</c> or <c>[ ]</c> line: <see cref="BacklogEditor.Drop"/> on in-memory text (AC-1, AC-2),
/// <see cref="BacklogEditor.WaitedOn"/>, and <c>POST /api/backlog/change</c> with <c>action: "drop"</c> on a temp
/// project repo on <c>develop</c> with no remote (AC-3 to AC-7). Each test has its own <see cref="TempFactoryRoot"/>,
/// deleted on dispose; the clock is the factory's <see cref="FakeTime"/>.</summary>
public sealed class BacklogDropTests : IDisposable
{
    private const string Project = "Alpha";

    private readonly TempFactoryRoot root = new();

    public void Dispose()
    {
        root.Dispose();
    }

    private static EditableText Parse(string text, bool bom = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return EditableText.Parse(bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes)!;
    }

    [Theory]
    [InlineData("?", "#2")]
    [InlineData(" ", "#2")]
    public void Drop_RemovesAnOpenOrProposedLine_AndKeepsEveryOtherLine(string mark, string subject)
    {
        EditableText text = Parse("# Backlog\n\n- [x] [#1](backlog/001-a.md) A\n- [" + mark
            + "] [#2](backlog/002-b.md) B\n- [ ] [#3](backlog/003-c.md) C\n");

        BacklogEdit? edit = BacklogEditor.Drop(text, 3);

        Assert.NotNull(edit);
        Assert.Equal(subject, edit.Subject);
        Assert.Equal("# Backlog\n\n- [x] [#1](backlog/001-a.md) A\n- [ ] [#3](backlog/003-c.md) C\n", edit.Text.Text);
    }

    [Theory]
    [InlineData("?")]
    [InlineData(" ")]
    public void Drop_UnnumberedLine_HasTheLineSubject(string mark)
    {
        EditableText text = Parse("# Backlog\n- [" + mark + "] Plain item\n");

        BacklogEdit? edit = BacklogEditor.Drop(text, 1);

        Assert.NotNull(edit);
        Assert.Equal("line 2", edit.Subject);
        Assert.Equal("# Backlog\n", edit.Text.Text);
    }

    [Fact]
    public void Drop_KeepsCrlfBomAndFinalNewline()
    {
        string original = "# Backlog\r\n- [ ] [#1](backlog/001-a.md) A\r\n- [ ] [#2](backlog/002-b.md) B\r\n";
        EditableText text = Parse(original, bom: true);

        BacklogEdit? edit = BacklogEditor.Drop(text, 1);

        Assert.NotNull(edit);
        Assert.Equal("#1", edit.Subject);
        byte[] expected = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Backlog\r\n- [ ] [#2](backlog/002-b.md) B\r\n")];
        Assert.Equal(expected, edit.Text.ToBytes());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(8)]
    public void Drop_RefusesDoneUserStopProseBlankAndOutOfRangeLines(int line)
    {
        EditableText text = Parse("# Backlog\n- [x] [#1](backlog/001-a.md) A\n- [X] [#2](backlog/002-b.md) B\n"
            + "- [h] [#3](backlog/003-c.md) C\n" + BacklogParser.StopLineText + "\nSome prose\n\n- [ ] [#4](backlog/004-d.md) D\n");

        Assert.Null(BacklogEditor.Drop(text, line));
    }

    [Theory]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: #2)", true)]
    [InlineData("- [?] [#5](backlog/005-e.md) E (after: #2)", true)]
    [InlineData("- [h] [#5](backlog/005-e.md) E (after: #2)", true)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: #1, #2)", true)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: #1; #2)", true)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: Alpha#2)", true)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: alpha#2) → job", true)]
    [InlineData("- [x] [#5](backlog/005-e.md) E (after: #2)", false)]
    [InlineData("- [X] [#5](backlog/005-e.md) E (after: #2)", false)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: Other#2)", false)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E (after: #20, #12)", false)]
    [InlineData("- [ ] [#5](backlog/005-e.md) E → after #2", false)]
    public void WaitedOn_CountsOnlyOpenWaitsOnThisProjectsNumber(string other, bool waited)
    {
        EditableText text = Parse("- [ ] [#2](backlog/002-b.md) B\n" + other + "\n");

        Assert.Equal(waited, BacklogEditor.WaitedOn(text, 0, Project));
    }

    [Fact]
    public void WaitedOn_IgnoresAnUnnumberedItemAndAnOwnWait_ButCountsAnUnnumberedWaiter()
    {
        EditableText own = Parse("- [ ] Plain\n- [ ] [#2](backlog/002-b.md) B (after: #2)\n");
        EditableText unnumbered = Parse("- [ ] Plain (after: #2)\n- [ ] [#2](backlog/002-b.md) B (after: #1)\n");

        Assert.False(BacklogEditor.WaitedOn(own, 0, Project));
        Assert.False(BacklogEditor.WaitedOn(own, 1, Project));
        Assert.False(BacklogEditor.WaitedOn(unnumbered, 0, Project));
        // An unnumbered open line that waits still blocks the drop of the item it waits for.
        Assert.True(BacklogEditor.WaitedOn(unnumbered, 1, Project));
    }

    [Theory]
    [InlineData(" ", 0)]
    [InlineData("?", 1)]
    public async Task Drop_OpenOrProposedLine_CommitsLineAndStoryRemoval(string mark, int line)
    {
        string backlog = "- [ ] [#1](backlog/001-one.md) One\n- [?] [#2](backlog/002-two.md) Two\n"
            + "- [x] [#3](backlog/003-three.md) Three (after: #1; #2)\n- [ ] [#4](backlog/004-four.md) Four (after: Other#1, Other#2)\n";
        string folder = Setup(backlog);
        int number = line + 1;
        string story = Path.Combine(folder, "backlog", number == 1 ? "001-one.md" : "002-two.md");
        Assert.StartsWith("- [" + mark + "] ", backlog.Split('\n')[line], StringComparison.Ordinal);
        int count = Count(folder);

        (HttpStatusCode status, JsonElement answer) = await Post(line);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(answer.GetProperty("committed").GetBoolean());
        Assert.Equal(count + 1, Count(folder));
        Assert.Equal("backlog: drop #" + number, TempFactoryRoot.Git(folder, "log", "-1", "--format=%s"));
        Assert.Equal("", TempFactoryRoot.Git(folder, "status", "--porcelain"));
        Assert.False(File.Exists(story));
        List<string> lines = [.. backlog.Split('\n')];
        lines.RemoveAt(line);
        Assert.Equal(string.Join("\n", lines), File.ReadAllText(Path.Combine(folder, "backlog.md")));
        Assert.Contains("backlog/" + Path.GetFileName(story),
            TempFactoryRoot.Git(folder, "show", "--name-only", "--format=", "HEAD"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Drop_DoneUserOrStopLine_IsInvalidAndChangesNothing(int line)
    {
        string backlog = "- [ ] [#1](backlog/001-one.md) One\n- [?] [#2](backlog/002-two.md) Two\n"
            + "- [x] [#3](backlog/003-three.md) Three\n- [h] [#4](backlog/004-four.md) Four\n" + BacklogParser.StopLineText + "\n";
        string folder = Setup(backlog);

        await AssertRefused(folder, line, HttpStatusCode.BadRequest, BacklogChangeEndpoint.Invalid);
    }

    [Theory]
    [InlineData(" ", "- [ ] [#5](backlog/005-five.md) Five (after: #1)")]
    [InlineData("?", "- [?] [#5](backlog/005-five.md) Five (after: #1)")]
    [InlineData(" ", "- [h] [#5](backlog/005-five.md) Five (after: #1)")]
    [InlineData("?", "- [ ] [#5](backlog/005-five.md) Five (after: #2, #1)")]
    [InlineData(" ", "- [ ] [#5](backlog/005-five.md) Five (after: Alpha#1)")]
    public async Task Drop_WaitedOnItem_IsRefusedAndChangesNothing(string mark, string waiting)
    {
        string backlog = "- [" + mark + "] [#1](backlog/001-one.md) One\n- [ ] [#2](backlog/002-two.md) Two\n" + waiting + "\n";
        string folder = Setup(backlog);

        await AssertRefused(folder, 0, HttpStatusCode.Conflict, BacklogChangeEndpoint.WaitedOn);
    }

    /// <summary>Creates <c>Alpha</c> with the backlog and a story per numbered line (1 to 5), as a git repo on
    /// <c>develop</c> without a remote.</summary>
    private string Setup(string backlog)
    {
        string folder = root.AddProject(Project, backlog);
        foreach (string name in new[] { "001-one.md", "002-two.md", "003-three.md", "004-four.md", "005-five.md" })
        {
            TempFactoryRoot.AddStory(folder, name, "# " + name + "\n\n## What\nx\n\n## Why\ny\n");
        }

        TempFactoryRoot.InitGit(folder, ProjectGit.Branch);
        return folder;
    }

    private static int Count(string folder)
    {
        return int.Parse(TempFactoryRoot.Git(folder, "rev-list", "--count", "HEAD"), CultureInfo.InvariantCulture);
    }

    private async Task AssertRefused(string folder, int line, HttpStatusCode expected, string error)
    {
        string head = TempFactoryRoot.Git(folder, "rev-parse", "HEAD");
        byte[] backlog = File.ReadAllBytes(Path.Combine(folder, "backlog.md"));
        byte[] story = File.ReadAllBytes(Path.Combine(folder, "backlog", "001-one.md"));

        (HttpStatusCode status, JsonElement answer) = await Post(line);

        Assert.Equal(expected, status);
        Assert.Equal(error, answer.GetProperty("error").GetString());
        Assert.Equal(head, TempFactoryRoot.Git(folder, "rev-parse", "HEAD"));
        Assert.Equal(backlog, File.ReadAllBytes(Path.Combine(folder, "backlog.md")));
        Assert.Equal(story, File.ReadAllBytes(Path.Combine(folder, "backlog", "001-one.md")));
        Assert.Equal("", TempFactoryRoot.Git(folder, "status", "--porcelain"));
    }

    /// <summary>Reads the version from <c>GET /api/backlogs</c>, posts one drop of <paramref name="line"/> with the
    /// token client and returns the status and JSON answer.</summary>
    private async Task<(HttpStatusCode Status, JsonElement Answer)> Post(int line)
    {
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.TokenClient();
        using HttpResponseMessage listed = await client.GetAsync("/api/backlogs");
        using JsonDocument backlogs = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        string version = backlogs.RootElement.GetProperty("projects").EnumerateArray()
            .Single(project => project.GetProperty("name").GetString() == Project)
            .GetProperty("version").GetString()!;

        string body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["project"] = Project,
            ["version"] = version,
            ["action"] = "drop",
            ["line"] = line,
        });
        using HttpResponseMessage response = await client.PostAsync(BacklogChangeEndpoint.Route, PushTestData.Json(body));
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, answer.RootElement.Clone());
    }
}
