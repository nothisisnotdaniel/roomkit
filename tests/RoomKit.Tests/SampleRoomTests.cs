using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RoomKit.Tests;

/// <summary>The sample app on RoomKit: its own port in the Host check, the token gate, the public health route and
/// RoomKit's web files (AC-7), its names in the token path, version, update check and capture note (AC-8), and its
/// job format in the health view, history, push watcher and change fingerprint (AC-9).</summary>
public sealed class SampleRoomTests : IDisposable
{
    private static readonly DateTimeOffset Start = FakeTime.Start;

    private readonly TempFactoryRoot root = new();

    public SampleRoomTests()
    {
        root.AddProject("Alpha", "- [ ] [#1](backlog/001-a.md) Open one\n");
    }

    public void Dispose()
    {
        root.Dispose();
    }

    [Fact]
    public async Task Host_ServesTheSamplePortAndRefusesControlRoomsPort()
    {
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.Client();

        using HttpResponseMessage served = await client.GetAsync("/health");
        using HttpRequestMessage other = new(HttpMethod.Get, "/health");
        other.Headers.Host = "127.0.0.1:8100";
        using HttpResponseMessage refused = await client.SendAsync(other);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, refused.StatusCode);
        Assert.Equal("{\"error\":\"bad_host\"}", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Backlogs_NeedTheTokenAndListTheTempProject()
    {
        await using SampleServerFactory factory = new(root.Path);
        HttpClient anonymous = await factory.Client();
        using HttpResponseMessage refused = await anonymous.GetAsync("/api/backlogs");
        HttpClient client = await factory.TokenClient();

        using HttpResponseMessage listed = await client.GetAsync("/api/backlogs");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Contains(
            json.RootElement.GetProperty("projects").EnumerateArray(),
            project => project.GetProperty("name").GetString() == "Alpha");
    }

    [Fact]
    public async Task Health_ReportsTheSampleVersion()
    {
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.Client();

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal("{\"version\":\"" + SampleRoom.Version + "\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Production", "/token.js", "text/javascript")]
    [InlineData("Production", "/app.css", "text/css")]
    [InlineData("Development", "/token.js", "text/javascript")]
    [InlineData("Development", "/app.css", "text/css")]
    public async Task KitWebFiles_AreServedAtTheirRootPath(string environment, string path, string type)
    {
        await using SampleServerFactory factory = new(root.Path, environment);
        HttpClient client = await factory.Client();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        string kitFile = Path.Combine(RepoFolder(), "src", "RoomKit", "wwwroot", path.TrimStart('/'));
        Assert.Equal(await File.ReadAllBytesAsync(kitFile), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void TokenPath_UsesTheSampleDataFolder()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SampleRoom", "access-token"),
            AccessToken.ResolvePath(null));
    }

    [Fact]
    public void VersionCommand_PrintsTheSampleName()
    {
        StringWriter output = new();
        StringWriter error = new();

        int code = VersionCommand.Run(["version"], output, error);

        Assert.Equal(0, code);
        Assert.Equal("SampleRoom " + SampleRoom.Version + "\n", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task UpdateCheck_AsksTheSampleRepositoryAsSampleRoom()
    {
        using TempTokenFolder folder = new();
        FakeUpdateHandler github = new();
        github.RespondWithTag("v1.0.0");
        LogCapture logs = new();
        PushStore store = new(Path.Combine(folder.Path, "push"));
        UpdateSettingsStore settings = new(Path.Combine(folder.Path, "push"), UnixOwner.CurrentUser());
        PushCrypto crypto = new(new FakeTime(), PushCrypto.SystemEphemeralKey, AccessToken.SystemRandom, null);
        PushSender sender = new(store, crypto, new PushTransport(new FakePushHandler()), logs.For<PushSender>());
        using UpdateChecker checker = new(
            new UpdateState(), new RunningVersion("1.0.0"), new UpdateTransport(github), settings, store, sender,
            new FakeTime(), logs.For<UpdateChecker>());

        await checker.Check();

        UpdateRequest request = Assert.Single(github.Requests);
        Assert.Equal("https://api.github.com/repos/example/sampleroom/releases/latest", request.Uri.AbsoluteUri);
        Assert.Equal("SampleRoom", request.Headers["User-Agent"]);
    }

    [Fact]
    public void Capture_WritesTheSampleNote()
    {
        string draft = BacklogEditor.RenderDraft(7, "Idea", "Idea. With more.", new DateOnly(2026, 3, 2));

        Assert.EndsWith("## Notes\n\n- 2026-03-02: Draft from SampleRoom\n", draft, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_ListsTheStuckMakeJobButNotTheDoneOne()
    {
        SampleJobFormat.Write(root.Path, "1-stuck", "make", Start);
        SampleJobFormat.Write(root.Path, "2-done", "done", Start);
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.TokenClient();
        factory.Time.Now = Start + TimeSpan.FromHours(26);

        using HttpResponseMessage response = await client.GetAsync("/api/doctor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement stuck = Assert.Single(json.RootElement.GetProperty("stuck").EnumerateArray());
        Assert.Equal("1-stuck", stuck.GetProperty("id").GetString());
        Assert.Equal("make", stuck.GetProperty("stage").GetString());
        Assert.Equal(26, stuck.GetProperty("hours").GetInt64());
    }

    [Fact]
    public async Task History_ListsTheFinishedSampleJobsWithTotals()
    {
        SampleJobFormat.Write(root.Path, "1-done", "done", Start - TimeSpan.FromHours(2), 1200);
        SampleJobFormat.Write(root.Path, "2-wait", "wait", Start - TimeSpan.FromHours(1), 300);
        SampleJobFormat.Write(root.Path, "3-make", "make", Start, 50);
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.TokenClient();

        using HttpResponseMessage response = await client.GetAsync("/api/history");

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["2-wait:escalated", "1-done:merged"],
            json.RootElement.GetProperty("jobs").EnumerateArray()
                .Select(job => job.GetProperty("id").GetString() + ":" + job.GetProperty("outcome").GetString()));
        JsonElement alpha = Assert.Single(json.RootElement.GetProperty("totals").GetProperty("projects").EnumerateArray());
        Assert.Equal("Alpha", alpha.GetProperty("name").GetString());
        Assert.Equal(2, alpha.GetProperty("last7").GetProperty("jobs").GetInt32());
        Assert.Equal(1500, alpha.GetProperty("last7").GetProperty("tokens").GetInt64());
    }

    [Fact]
    public async Task PushWatcher_NotifiesOnAMoveIntoWaitAndIntoDone()
    {
        using TempTokenFolder folder = new();
        using PushReceiver receiver = new();
        PushStore store = new(Path.Combine(folder.Path, "push"));
        Assert.True(store.Subscribe(new PushSubscription(
            PushTestData.AppleEndpoint, Base64Url.Encode(receiver.PublicKey), Base64Url.Encode(receiver.Auth))));
        store.SetNotifyDone(true);
        LogCapture logs = new();
        FakePushHandler handler = new();
        PushCrypto crypto = new(new FakeTime(), PushCrypto.SystemEphemeralKey, AccessToken.SystemRandom, null);
        PushSender sender = new(store, crypto, new PushTransport(handler), logs.For<PushSender>());
        using PushWatcher watcher = new(new FactoryRoot(root.Path), store, sender, new FakeTime(), logs.For<PushWatcher>());
        SampleJobFormat.Write(root.Path, "4-feature", "plan", Start);
        SampleJobFormat.Write(root.Path, "5-other", "make", Start);
        await watcher.Scan();

        SampleJobFormat.Write(root.Path, "4-feature", "wait", Start);
        SampleJobFormat.Write(root.Path, "5-other", "done", Start);
        IReadOnlyList<PushPayload> sent = await watcher.Scan();

        Assert.Equal(
            ["needs-human:4-feature:wait", "done:5-other:done"],
            sent.Select(payload => payload.Kind + ":" + payload.Job + ":" + payload.Stage));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(await watcher.Scan());
    }

    [Fact]
    public async Task PushBadge_IsAddedLastToTheFactoryNeedsHumanPush()
    {
        FakePushBadge badge = new(2);
        SampleJobFormat.Write(root.Path, "4-feature", "plan", Start);
        await using SampleServerFactory factory = new(root.Path, badge: badge);
        HttpClient client = await factory.TokenClient();
        using PushReceiver receiver = new();
        (await PushTestData.Subscribe(client, receiver, PushTestData.AppleEndpoint)).Dispose();
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();
        await watcher.Scan();

        SampleJobFormat.Write(root.Path, "4-feature", "wait", Start);
        IReadOnlyList<PushPayload> sent = await watcher.Scan();

        PushPayload payload = Assert.Single(sent);
        Assert.Equal(PushPayload.NeedsHuman, payload.Kind);
        string usual = Encoding.UTF8.GetString(payload.ToJson());
        PushRequest request = Assert.Single(factory.Push.Requests);
        Assert.Equal(usual[..^1] + ",\"badge\":2}", receiver.DecryptText(request.Body));
        Assert.StartsWith("{\"kind\":\"needs-human\",", usual, StringComparison.Ordinal);
        Assert.Contains("\"job\":\"4-feature\",\"stage\":\"wait\",\"reason\":\"Needs you\"}", usual, StringComparison.Ordinal);
        Assert.Equal(1, badge.Calls);
    }

    [Fact]
    public void Fingerprint_ChangesWhenASampleRecordFileChanges()
    {
        string folder = SampleJobFormat.Write(root.Path, "6-job", "plan", Start);
        string before = ChangeFingerprint.Jobs(root.Path);
        string same = ChangeFingerprint.Jobs(root.Path);

        File.AppendAllText(Path.Combine(folder, SampleJobFormat.FileName), "title=Longer title\n");
        string after = ChangeFingerprint.Jobs(root.Path);

        Assert.Equal(before, same);
        Assert.NotEqual(before, after);
    }

    internal static string RepoFolder()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RoomKit.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("RoomKit.slnx not found");
    }
}
