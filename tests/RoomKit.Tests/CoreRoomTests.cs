using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace RoomKit.Tests;

/// <summary>The process-wide <see cref="RoomContext"/> is the core app's while these run, so they run alone.</summary>
// Why no parallelism: the other RoomKit tests read RoomContext and expect the SampleRoom one, so any of them running
// at the same time would see the core app's context and fail at random.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoreRoomCollection
{
    public const string Name = "Core room (process-wide context)";
}

/// <summary>RoomKit's core without the factory module (#153): the public API takes no job format (AC-1), a core app
/// builds with no factory root and serves the core routes like the factory app (AC-2), no factory endpoint (AC-3) or
/// web file (AC-4) and no factory service (AC-5), needs no capture note (AC-6) and sends its own push sources' events
/// (AC-7); with the module the job and backlog source is registered (AC-8). Scans are called directly, never waited
/// for.</summary>
[Collection(CoreRoomCollection.Name)]
public sealed class CoreRoomTests : IDisposable
{
    private static readonly string[] FactoryRoutes =
        ["/api/backlogs", "/api/story", "/api/search", "/api/inbox", "/api/tasks", "/api/history", "/api/doctor"];

    private static readonly string[] FactoryWebFiles =
        ["/edit.js", "/counts.js", "/search.js", "/capture.js", "/history.js", "/doctor.js", "/live.js"];

    public void Dispose()
    {
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
    }

    [Fact]
    public void RoomKitApp_HasNoPublicMethodWithAJobFormat()
    {
        MethodInfo[] methods = typeof(RoomKitApp).GetMethods(BindingFlags.Public | BindingFlags.Static);

        Assert.Contains(methods, method => method.Name == nameof(RoomKitApp.Run));
        Assert.Contains(methods, method => method.Name == nameof(RoomKitApp.Create));
        Assert.DoesNotContain(
            methods, method => method.GetParameters().Any(parameter => typeof(IJobFormat).IsAssignableFrom(parameter.ParameterType)));
    }

    [Fact]
    public void FactoryModule_RefusesAnUnsafeJobFormat()
    {
        Assert.Throws<ArgumentException>(() => new FactoryModule(new JobsFolder("../x")));
        Assert.Same(SampleRoom.Jobs, new FactoryModule(SampleRoom.Jobs).Jobs);
    }

    [Fact]
    public async Task Core_BuildsWithoutAFactoryRootAndServesHealth()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.Client();

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"version\":\"" + CoreRoom.Version + "\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(RoomKitApp.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Core_RefusesAWrongHostAndAnApiRequestWithoutTheToken()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.Client();
        using HttpRequestMessage other = new(HttpMethod.Get, "/health");
        other.Headers.Host = "127.0.0.1:8201";

        using HttpResponseMessage refused = await client.SendAsync(other);
        using HttpResponseMessage anonymous = await client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, refused.StatusCode);
        Assert.Equal("{\"error\":\"bad_host\"}", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(RoomKitApp.UnauthorizedBody, await anonymous.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", Header(anonymous, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Core_ServesVersionUpdateAndPushLikeTheFactoryApp()
    {
        IReadOnlyList<Answer> core;
        await using (CoreServerFactory factory = new())
        {
            core = await CoreAnswers(await factory.TokenClient(), CoreRoom.Version);
        }

        IReadOnlyList<Answer> sample;
        using TempFactoryRoot root = new();
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
        await using (SampleServerFactory factory = new(root.Path))
        {
            sample = await CoreAnswers(await factory.TokenClient(), SampleRoom.Version);
        }

        Assert.Equal(3, core.Count);
        Assert.Equal(sample, core);
        Assert.All(core, answer => Assert.Equal(HttpStatusCode.OK, answer.Status));
    }

    [Fact]
    public async Task Core_HasNoFactoryEndpoints()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();

        foreach (string route in FactoryRoutes)
        {
            using HttpResponseMessage get = await client.GetAsync(route);
            using HttpResponseMessage head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, route));
            Assert.True(get.StatusCode == HttpStatusCode.NotFound, "GET " + route + " gave " + get.StatusCode);
            Assert.True(head.StatusCode == HttpStatusCode.NotFound, "HEAD " + route + " gave " + head.StatusCode);
        }

        using HttpResponseMessage change = await client.PostAsync(
            "/api/backlog/change", PushTestData.Json("{\"project\":\"Alpha\",\"action\":\"approve\"}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, change.StatusCode);
    }

    [Fact]
    public async Task Core_StillAllowsTheUpdateSettingsAndPushPostRoutes()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        using PushReceiver receiver = new();

        using HttpResponseMessage settings = await client.PostAsync(
            UpdateEndpoint.SettingsRoute, PushTestData.Json("{\"autoUpdate\":true}"));
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        foreach (Func<HttpClient, Task<HttpResponseMessage>> call in PushTestData.EveryRoute(receiver))
        {
            using HttpResponseMessage response = await call(client);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Core_ServesTheShellFilesButNoFactoryWebFile(string environment)
    {
        await using CoreServerFactory factory = new(environment);
        HttpClient client = await factory.Client();

        foreach (string path in FactoryWebFiles.Concat(FactoryWebFiles.Select(path => path.ToUpperInvariant())))
        {
            using HttpResponseMessage hidden = await client.GetAsync(path);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, path + " gave " + hidden.StatusCode);
        }

        foreach ((string path, string type) in new[]
        {
            ("/app.css", "text/css"), ("/sw.js", "text/javascript"), ("/token.js", "text/javascript"),
            ("/push.js", "text/javascript"),
        })
        {
            using HttpResponseMessage served = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal(type, served.Content.Headers.ContentType?.MediaType);
            string kitFile = Path.Combine(SampleRoomTests.RepoFolder(), "src", "RoomKit", "wwwroot", path.TrimStart('/'));
            Assert.Equal(await File.ReadAllBytesAsync(kitFile), await served.Content.ReadAsByteArrayAsync());
            if (path == RoomKitApp.ServiceWorkerPath)
            {
                Assert.Equal("no-cache", served.Headers.CacheControl?.ToString());
            }
        }
    }

    [Fact]
    public async Task Core_HoldsNoFactoryServiceAndRunsNoGit()
    {
        await using CoreServerFactory factory = new();
        await factory.Client();
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();

        await watcher.Scan();

        Assert.Null(factory.Services.GetService<BacklogWorktree>());
        Assert.Null(factory.Services.GetService<FactoryBacklog>());
        Assert.Null(factory.Services.GetService<BacklogWriteLock>());
        Assert.Null(factory.Services.GetService<FactoryRoot>());
        Assert.Empty(factory.Services.GetServices<IPushSource>());
        Assert.Empty(watcher.Sources);
        Assert.True(Directory.Exists(factory.DataFolder));
        Assert.False(Directory.Exists(Path.Combine(factory.DataFolder, BacklogWorktree.FolderName)));
        Assert.Throws<InvalidOperationException>(() => RoomContext.Jobs);
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Core_BuildsWithoutACaptureNote()
    {
        Assert.Null(CoreRoom.Options.CaptureNote);
        RoomContext.Check(CoreRoom.Options);

        await using CoreServerFactory factory = new();

        Assert.NotNull(factory.Services.GetService<PushWatcher>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Draft\nfrom")]
    [InlineData(" ")]
    public void FactoryModule_RefusesAMissingOrMultiLineCaptureNote(string? note)
    {
        RoomKitOptions options = SampleRoom.Options with { CaptureNote = note };
        using TempFactoryRoot root = new();

        Assert.Throws<ArgumentException>(() => RoomKitApp.Create(
            ["--FactoryRoot=" + root.Path], new StringWriter(), options, SampleRoom.Map, null, new FactoryModule(SampleRoom.Jobs)));
        Assert.Throws<ArgumentException>(() => RoomContext.Use(options, SampleRoom.Jobs));
    }

    [Fact]
    public async Task PushSource_SendsOnePushPerSubscriptionWithTheTitleAndTag()
    {
        QueuedPushSource source = new();
        await using CoreServerFactory factory = new(sources: source);
        HttpClient client = await factory.TokenClient();
        using PushReceiver first = new();
        using PushReceiver second = new();
        (await PushTestData.Subscribe(client, first, PushTestData.AppleEndpoint)).Dispose();
        (await PushTestData.Subscribe(client, second, PushTestData.AppleEndpoint + "-2")).Dispose();
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();
        source.Add(new PushEvent("pr-review", "Review wanted", "pr:7"));

        IReadOnlyList<PushPayload> sent = await watcher.Scan();

        Assert.Single(sent);
        Assert.Equal(2, factory.Push.Requests.Count);
        Assert.Equal(
            "{\"kind\":\"pr-review\",\"title\":\"Review wanted\",\"tag\":\"pr:7\"}",
            first.DecryptText(factory.Push.Requests[0].Body));
        Assert.Equal(
            "{\"kind\":\"pr-review\",\"title\":\"Review wanted\",\"tag\":\"pr:7\"}",
            second.DecryptText(factory.Push.Requests[1].Body));
        Assert.Empty(await watcher.Scan());
        Assert.Equal(2, factory.Push.Requests.Count);
    }

    [Fact]
    public async Task PushSource_SendsNothingWithoutASubscription()
    {
        QueuedPushSource source = new();
        await using CoreServerFactory factory = new(sources: source);
        await factory.Client();
        source.Add(new PushEvent("pr-review", "Review wanted", "pr:7"));

        await factory.Services.GetRequiredService<PushWatcher>().Scan();

        Assert.Equal(2, source.Calls);
        Assert.Empty(factory.Push.Requests);
    }

    [Fact]
    public async Task PushSource_AFailingSourceDoesNotStopTheOthersAndLogsOnlyAFixedText()
    {
        QueuedPushSource failing = new();
        QueuedPushSource working = new();
        await using CoreServerFactory factory = new(sources: [failing, working]);
        HttpClient client = await factory.TokenClient();
        using PushReceiver receiver = new();
        (await PushTestData.Subscribe(client, receiver, PushTestData.AppleEndpoint)).Dispose();
        failing.Add(new PushEvent("lost", "Lost one", "lost:1"));
        failing.Fail = "secret /Users/someone/path";
        working.Add(new PushEvent("kept", "Kept one", "kept:1"));

        await factory.Services.GetRequiredService<PushWatcher>().Scan();

        PushRequest request = Assert.Single(factory.Push.Requests);
        Assert.Equal("{\"kind\":\"kept\",\"title\":\"Kept one\",\"tag\":\"kept:1\"}", receiver.DecryptText(request.Body));
        Assert.Contains(
            factory.Logs.Lines, line => line.StartsWith("Push source failed; its events are dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "Title", "tag")]
    [InlineData("Upper", "Title", "tag")]
    [InlineData("done", "Title", "tag")]
    [InlineData("updated", "Title", "tag")]
    [InlineData("kind", "", "tag")]
    [InlineData("kind", "Two\nlines", "tag")]
    [InlineData("kind", "Title", "")]
    [InlineData("kind", "Title", "tab\there")]
    public void PushEvent_RefusesABadKindTitleOrTag(string kind, string title, string tag)
    {
        Assert.Throws<ArgumentException>(() => new PushEvent(kind, title, tag));
        Assert.Throws<ArgumentException>(() => new PushEvent("kind", new string('a', PushEvent.MaxTextLength + 1), "tag"));
    }

    [Fact]
    public async Task UpdaterResult_IsPushedInTheCoreApp()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        using PushReceiver receiver = new();
        (await PushTestData.Subscribe(client, receiver, PushTestData.AppleEndpoint)).Dispose();
        TempTokenFolder.WritePrivateText(
            Path.Combine(factory.DataFolder, UpdateResultReader.FileName),
            "{\"run\":\"20261118T201500Z\",\"result\":\"updated\",\"version\":\"1.3.0\",\"previous\":\"1.2.0\"}\n");
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();

        await watcher.Scan();
        await watcher.Scan();

        PushRequest request = Assert.Single(factory.Push.Requests);
        Assert.Equal(
            "{\"kind\":\"updated\",\"version\":\"1.3.0\",\"previous\":\"1.2.0\",\"reason\":\"Updated\"}",
            receiver.DecryptText(request.Body));
    }

    [Fact]
    public async Task FactoryModule_RegistersTheJobAndBacklogSource()
    {
        using TempFactoryRoot root = new();
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
        await using SampleServerFactory factory = new(root.Path);

        IPushSource source = Assert.Single(factory.Services.GetServices<IPushSource>());
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();

        Assert.IsType<FactoryPushSource>(source);
        Assert.Same(source, Assert.Single(watcher.Sources));
        Assert.NotNull(factory.Services.GetService<FactoryRoot>());
        Assert.NotNull(factory.Services.GetService<BacklogWorktree>());
    }

    [Fact]
    public async Task FactoryModule_SendsTheJobAndApprovalPushesInTheSampleApp()
    {
        using TempFactoryRoot root = new();
        root.AddProject("Alpha", "- [ ] [#1](backlog/001-a.md) Open one\n");
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
        SampleJobFormat.Write(root.Path, "4-feature", "plan", FakeTime.Start);
        SampleJobFormat.Write(root.Path, "5-other", "make", FakeTime.Start);
        await using SampleServerFactory factory = new(root.Path);
        HttpClient client = await factory.TokenClient();
        using PushReceiver receiver = new();
        (await PushTestData.Subscribe(client, receiver, PushTestData.AppleEndpoint)).Dispose();
        using HttpResponseMessage done = await client.PostAsync(PushEndpoint.SettingsRoute, PushTestData.Json("{\"notifyDone\":true}"));
        PushWatcher watcher = factory.Services.GetRequiredService<PushWatcher>();

        SampleJobFormat.Write(root.Path, "4-feature", "wait", FakeTime.Start);
        SampleJobFormat.Write(root.Path, "5-other", "done", FakeTime.Start);
        root.AddProject("Alpha", "- [ ] [#1](backlog/001-a.md) Open one\n- [?] [#2](backlog/002-b.md) New one\n");
        IReadOnlyList<PushPayload> sent = await watcher.Scan();

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(
            ["needs-human:4-feature", "done:5-other", "approval:Alpha"],
            sent.Select(payload => payload.Kind + ":" + (payload.Job ?? payload.Project)));
        Assert.Equal(3, factory.Push.Requests.Count);
    }

    private static async Task<IReadOnlyList<Answer>> CoreAnswers(HttpClient client, string version)
    {
        List<Answer> answers = [];
        foreach (string route in new[] { VersionEndpoint.Route, UpdateEndpoint.Route, PushEndpoint.Route })
        {
            using HttpResponseMessage response = await client.GetAsync(route);
            string body = await response.Content.ReadAsStringAsync();
            JsonObject json = JsonNode.Parse(body)!.AsObject();
            if (json["version"] is JsonValue value && value.GetValue<string>() == version)
            {
                json["version"] = "<version>";
            }

            if (json.ContainsKey("publicKey"))
            {
                json["publicKey"] = "<key>";
            }

            answers.Add(new Answer(
                route,
                response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                response.Headers.CacheControl?.ToString(),
                Header(response, "Content-Security-Policy"),
                Header(response, "X-Content-Type-Options"),
                json.ToJsonString()));
        }

        return answers;
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
    }

    private sealed record Answer(
        string Route, HttpStatusCode Status, string? Type, string? Cache, string? Policy, string? Sniff, string Body);

    /// <summary>The sample job format with another jobs folder.</summary>
    private sealed class JobsFolder : IJobFormat
    {
        private readonly IJobFormat inner = SampleRoom.Jobs;

        public JobsFolder(string jobsFolderPath)
        {
            JobsFolderPath = jobsFolderPath;
        }

        public string JobsFolderPath { get; }

        public IReadOnlyList<string> RecordFileNames => inner.RecordFileNames;

        public IReadOnlyList<string> Stages => inner.Stages;

        public IReadOnlyList<string> ActiveStages => inner.ActiveStages;

        public IReadOnlyList<string> BlockedStages => inner.BlockedStages;

        public string DoneStage => inner.DoneStage;

        public IReadOnlyList<string> ReviewKeys => inner.ReviewKeys;

        public System.Text.RegularExpressions.Regex IdPattern => inner.IdPattern;

        public IReadOnlyList<RoomJobFolder> JobFolders(string factoryRoot) => inner.JobFolders(factoryRoot);

        public IReadOnlyList<RoomJob> ReadAll(string factoryRoot) => inner.ReadAll(factoryRoot);
    }
}
