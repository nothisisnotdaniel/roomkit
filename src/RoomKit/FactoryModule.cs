using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace RoomKit;

/// <summary>The opt-in factory features of a RoomKit app: pass one to <see cref="RoomKitApp.Run"/> or
/// <see cref="RoomKitApp.Create(string[], TextWriter, RoomKitOptions, Action{RoomRoutes}, Action{ListenOptions}?, FactoryModule?, Action{RoomServices}?)"/>.
/// It finds the factory root (<c>--FactoryRoot</c> or the parent of the folder holding the app's solution file; the app
/// stops with a fixed text without one), adds <see cref="FactoryRoot"/>, <see cref="FactoryBacklog"/>,
/// <see cref="BacklogWorktree"/> and the backlog write lock to the services, maps the backlogs, backlog change
/// (<c>POST</c>), story, search, inbox, tasks, history and doctor endpoints, serves <c>edit.js</c>, <c>counts.js</c>,
/// <c>search.js</c>, <c>capture.js</c>, <c>history.js</c>, <c>doctor.js</c> and <c>live.js</c>, and adds the job and
/// backlog push source and the <see cref="ClaimWatcher"/> (other machines' item claims). It needs <see cref="RoomKitOptions.CaptureNote"/>. Without it none of these exist.</summary>
public sealed class FactoryModule
{
    /// <summary>The module's web files; an app without the module answers 404 for them.</summary>
    internal static readonly IReadOnlyList<string> WebFiles =
        ["/edit.js", "/counts.js", "/search.js", "/capture.js", "/history.js", "/doctor.js", "/live.js"];

    /// <summary>The module's <c>POST</c> routes.</summary>
    internal static readonly IReadOnlyList<string> PostRoutes = [BacklogChangeEndpoint.Route];

    /// <summary>The module for the factory's job format; a job format RoomKit can't use safely throws
    /// <see cref="ArgumentException"/>.</summary>
    public FactoryModule(IJobFormat jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        RoomContext.Check(jobs);
        Jobs = jobs;
    }

    /// <summary>How the factory keeps its jobs.</summary>
    public IJobFormat Jobs { get; }

    /// <summary>The factory root, or <c>null</c> after writing <see cref="FactoryRootLocator.NotFoundText"/> when there
    /// is none.</summary>
    internal static string? FindRoot(IConfiguration configuration, TextWriter error)
    {
        string? root = FactoryRootLocator.Resolve(configuration["FactoryRoot"], AppContext.BaseDirectory);
        if (root is null)
        {
            error.WriteLine(FactoryRootLocator.NotFoundText);
        }

        return root;
    }

    /// <summary>The module's services: the root, the factory backlog, the write lock, the backlog worktrees next to
    /// the token file, the job and backlog push source and the <see cref="ClaimWatcher"/>.</summary>
    internal static void AddServices(RoomServices services, string root, string dataFolder)
    {
        FactoryRoot factoryRoot = new(root);
        services.Services.AddSingleton(factoryRoot);
        services.Services.AddSingleton(new FactoryBacklog(root, services.Configuration[FactoryBacklog.ConfigKey]));
        services.Services.AddSingleton(new BacklogWriteLock());
        services.Services.AddSingleton(new BacklogWorktree(dataFolder, UnixOwner.CurrentUser()));
        services.AddPushSource(new FactoryPushSource(factoryRoot));
        services.Services.AddSingleton<ClaimWatcher>();
        services.Services.AddHostedService(provider => provider.GetRequiredService<ClaimWatcher>());
    }

    /// <summary>Maps the module's endpoints.</summary>
    internal static void Map(WebApplication app)
    {
        BacklogsEndpoint.Map(app);
        StoryEndpoint.Map(app);
        InboxEndpoint.Map(app);
        TasksEndpoint.Map(app);
        HistoryEndpoint.Map(app);
        DoctorEndpoint.Map(app);
        SearchEndpoint.Map(app);
        BacklogChangeEndpoint.Map(app);
    }
}
