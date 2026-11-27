using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RoomKit.Tests;

/// <summary>Hosts <see cref="CoreRoom"/> in memory (no real port) like <see cref="SampleServerFactory"/>, without the
/// factory module and without any <c>--FactoryRoot</c>: the token file sits in its own temp folder (deleted on
/// dispose), pushes go to <see cref="Push"/>, the update check to <see cref="Update"/>, and the clock is
/// <see cref="Time"/>, whose timers never fire. <paramref name="sources"/> are registered through the public service
/// hook, then <paramref name="addServices"/> (for example a badge source). Clients send <c>Host: 127.0.0.1:8202</c>.</summary>
internal sealed class CoreServerFactory : IAsyncDisposable
{
    private readonly TempTokenFolder tokenFolder = new();

    private readonly WebApplication app;

    public CoreServerFactory(string environment = "Production", params IPushSource[] sources)
        : this(null, environment, sources)
    {
    }

    public CoreServerFactory(Action<RoomServices>? addServices, string environment = "Production", params IPushSource[] sources)
    {
        TokenFile = Path.Combine(tokenFolder.Path, "CoreRoom", AccessToken.FileName);
        StringWriter error = new();
        app = RoomKitApp.Create(
            [
                "--" + AccessToken.ConfigKey + "=" + TokenFile,
                "--environment=" + environment,
                "--applicationName=" + typeof(CoreServerFactory).Assembly.GetName().Name,
            ],
            error,
            CoreRoom.Options,
            CoreRoom.Map,
            null,
            null,
            services =>
            {
                foreach (IPushSource source in sources)
                {
                    services.AddPushSource(source);
                }

                addServices?.Invoke(services);
            },
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<TimeProvider>(Time);
                builder.Services.AddSingleton(new PushTransport(Push));
                builder.Services.AddSingleton(new UpdateTransport(Update));
                builder.Services.AddSingleton(new RunningVersion(AppVersion.Dev));
                builder.Services.AddSingleton<ILoggerProvider>(Logs);
            }) ?? throw new InvalidOperationException("The core app did not build: " + error);
    }

    public FakeTime Time { get; } = new();

    public FakePushHandler Push { get; } = new();

    public FakeUpdateHandler Update { get; } = new();

    public LogCapture Logs { get; } = new();

    public string TokenFile { get; }

    /// <summary>The data folder next to the token file (push, update and, in a factory app, worktree files).</summary>
    public string DataFolder => Path.GetDirectoryName(TokenFile)!;

    public IServiceProvider Services => app.Services;

    public async Task<HttpClient> Client()
    {
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("http://127.0.0.1:8202/");
        return client;
    }

    public async Task<HttpClient> TokenClient()
    {
        HttpClient client = await Client();
        TokenFileRead read = AccessToken.Read(TokenFile);
        Assert.Equal(TokenFileState.Valid, read.State);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", read.Token);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        tokenFolder.Dispose();
    }
}
