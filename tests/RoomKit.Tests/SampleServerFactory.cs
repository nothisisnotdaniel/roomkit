using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RoomKit.Tests;

/// <summary>Hosts the sample app in memory (no real port) like <c>ServerFactory</c>: <c>FactoryRoot</c> is the given
/// temp root, the token file sits in its own temp folder (deleted on dispose), pushes go to <see cref="Push"/>, the
/// update check to <see cref="Update"/>, and the clock is <see cref="Time"/>, whose timers never fire. Clients send
/// <c>Host: 127.0.0.1:8201</c>.</summary>
internal sealed class SampleServerFactory : IAsyncDisposable
{
    private readonly TempTokenFolder tokenFolder = new();

    private readonly WebApplication app;

    public SampleServerFactory(string factoryRoot, string environment = "Production")
    {
        TokenFile = Path.Combine(tokenFolder.Path, "SampleRoom", AccessToken.FileName);
        StringWriter error = new();
        app = RoomKitApp.Create(
            // Environment and application name come in as arguments, as the host reads them before it builds (so in
            // Development it loads the static web assets manifest of this test assembly itself).
            [
                "--FactoryRoot=" + factoryRoot,
                "--" + AccessToken.ConfigKey + "=" + TokenFile,
                "--environment=" + environment,
                "--applicationName=" + typeof(SampleServerFactory).Assembly.GetName().Name,
            ],
            error,
            SampleRoom.Options,
            SampleRoom.Jobs,
            SampleRoom.Map,
            null,
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<TimeProvider>(Time);
                builder.Services.AddSingleton(new PushTransport(Push));
                builder.Services.AddSingleton(new UpdateTransport(Update));
                builder.Services.AddSingleton(new RunningVersion(AppVersion.Dev));
                builder.Services.AddSingleton<ILoggerProvider>(Logs);
            }) ?? throw new InvalidOperationException("The sample app did not build: " + error);
    }

    public FakeTime Time { get; } = new();

    public FakePushHandler Push { get; } = new();

    public FakeUpdateHandler Update { get; } = new();

    public LogCapture Logs { get; } = new();

    public string TokenFile { get; }

    public IServiceProvider Services => app.Services;

    public async Task<HttpClient> Client()
    {
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("http://127.0.0.1:8201/");
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
