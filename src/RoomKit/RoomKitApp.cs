using System.Collections.Frozen;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;

namespace RoomKit;

/// <summary>The entry point of a RoomKit app. <see cref="Run"/> handles the <c>version</c> and <c>token</c> commands,
/// then builds and runs the server; <see cref="Create"/> builds it: loopback-only Kestrel on the app's port,
/// security headers, the <c>Host</c> check, GET/HEAD only (plus POST on RoomKit's backlog change, update settings
/// and push routes and on the routes the app maps through <see cref="RoomRoutes.MapPost"/>), static files from the
/// app's and RoomKit's <c>wwwroot</c>, an access token on every <c>/api/</c> request, RoomKit's APIs, the public
/// <c>/health</c> route, the push watcher, the update check, and then the app's own endpoints.</summary>
public static class RoomKitApp
{
    /// <summary>The policy every response carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; "
        + "manifest-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>The service worker, served with <c>Cache-Control: no-cache</c> so a changed worker is picked
    /// up.</summary>
    public const string ServiceWorkerPath = "/sw.js";

    /// <summary>The fixed body of a refused API request.</summary>
    public const string UnauthorizedBody = "{\"error\":\"unauthorized\"}";

    /// <summary>The ASP.NET category that logs every request with its URL (and so its <c>Host</c>).</summary>
    private const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>The POST routes of RoomKit itself.</summary>
    private static readonly string[] KitPostRoutes =
    [
        BacklogChangeEndpoint.Route,
        UpdateEndpoint.SettingsRoute,
        .. PushEndpoint.PostRoutes,
    ];

    /// <summary>Runs the app: <c>version</c> and <c>token</c> commands print and return; otherwise the server runs
    /// until it is stopped. Returns the process exit code.</summary>
    public static int Run(string[] args, RoomKitOptions options, IJobFormat jobs, Action<RoomRoutes> mapApp)
    {
        RoomContext.Use(options, jobs);
        if (VersionCommand.IsCommand(args))
        {
            return VersionCommand.Run(args, Console.Out, Console.Error);
        }

        if (TokenCommand.IsCommand(args))
        {
            return TokenCommand.Run(args, Console.Out, Console.Error);
        }

        WebApplication? app = Create(args, Console.Error, options, jobs, mapApp);
        if (app is null)
        {
            return 1;
        }

        app.Run();
        return 0;
    }

    /// <summary>Builds the app, or returns <c>null</c> after writing a fixed error text when there is no factory
    /// root, the <c>TailnetHost</c> name is not valid or the access token file is not safe. A missing token file is
    /// created (the token itself is never written to <paramref name="error"/>). <paramref name="onListen"/> is a test
    /// seam called for every endpoint Kestrel listens on (through the public <c>ConfigureEndpointDefaults</c>);
    /// <c>null</c> in production.</summary>
    public static WebApplication? Create(
        string[] args,
        TextWriter error,
        RoomKitOptions options,
        IJobFormat jobs,
        Action<RoomRoutes> mapApp,
        Action<ListenOptions>? onListen = null)
    {
        return Create(args, error, options, jobs, mapApp, onListen, configure: null);
    }

    /// <summary>As the public <see cref="Create(string[], TextWriter, RoomKitOptions, IJobFormat, Action{RoomRoutes},
    /// Action{ListenOptions}?)"/>; <paramref name="configure"/> is a test seam run on the builder after RoomKit's
    /// services are added and before the static web assets are loaded (for example an in-memory server, fakes and the
    /// environment).</summary>
    internal static WebApplication? Create(
        string[] args,
        TextWriter error,
        RoomKitOptions options,
        IJobFormat jobs,
        Action<RoomRoutes> mapApp,
        Action<ListenOptions>? onListen,
        Action<WebApplicationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(mapApp);
        RoomContext.Use(options, jobs);

        // Content root (and so wwwroot) is the server's own output folder, never the current directory.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        string? root = FactoryRootLocator.Resolve(builder.Configuration["FactoryRoot"], AppContext.BaseDirectory);
        if (root is null)
        {
            error.WriteLine(FactoryRootLocator.NotFoundText);
            return null;
        }

        string? tailnetHost = builder.Configuration[HostCheck.ConfigKey];
        if (tailnetHost is not null && !HostCheck.IsValidTailnetHost(tailnetHost))
        {
            error.WriteLine(HostCheck.InvalidTailnetText);
            return null;
        }

        string tokenPath = AccessToken.ResolvePath(builder.Configuration[AccessToken.ConfigKey]);
        TokenFileRead token = AccessToken.Read(tokenPath);
        if (token.State == TokenFileState.Invalid)
        {
            error.WriteLine(AccessToken.InvalidText);
            return null;
        }

        if (token.State == TokenFileState.Missing)
        {
            if (AccessToken.Replace(tokenPath, AccessToken.SystemRandom) is null)
            {
                error.WriteLine(AccessToken.WriteFailedText);
                return null;
            }

            error.WriteLine(AccessToken.CreatedText);
        }

        // The per-request "Request starting/finished" lines hold the Host header; a refused Host is never logged.
        // Set in code on purpose: this filter wins over any Logging:LogLevel setting for the same category.
        builder.Logging.AddFilter(RequestLogCategory, LogLevel.Warning);
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (onListen is not null)
            {
                kestrel.ConfigureEndpointDefaults(onListen);
            }

            ConfigureKestrel(kestrel);
        });
        builder.Services.AddSingleton(new FactoryRoot(root));
        builder.Services.AddSingleton(new BacklogWriteLock());
        AddPush(builder, tokenPath);
        configure?.Invoke(builder);

        // RoomKit's web files are static web assets: a published app has them in its wwwroot, a built one finds them
        // through the build's manifest in every environment, not only in Development (which loads it by itself).
        if (!builder.Environment.IsDevelopment())
        {
            StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
        }

        WebApplication app = builder.Build();
        HashSet<string> appPostRoutes = new(StringComparer.Ordinal);
        FrozenSet<string>? postRoutes = null;
        app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!IsAllowedHost(context.Request, tailnetHost))
            {
                await HostCheck.WriteRefused(context.Response);
                return;
            }

            if (!IsAllowedMethod(context.Request, postRoutes))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !IsAuthorized(context.Request, tokenPath))
            {
                await WriteUnauthorized(context.Response);
                return;
            }

            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(CreateStaticFileOptions());
        BacklogsEndpoint.Map(app);
        StoryEndpoint.Map(app);
        InboxEndpoint.Map(app);
        TasksEndpoint.Map(app);
        HistoryEndpoint.Map(app);
        DoctorEndpoint.Map(app);
        SearchEndpoint.Map(app);
        BacklogChangeEndpoint.Map(app);
        PushEndpoint.Map(app);
        VersionEndpoint.Map(app);
        UpdateEndpoint.Map(app, tailnetHost);
        HealthEndpoint.Map(app);
        mapApp(new RoomRoutes(app, appPostRoutes));
        postRoutes = KitPostRoutes.Concat(appPostRoutes).ToFrozenSet(StringComparer.Ordinal);
        return app;
    }

    /// <summary>Listens only on <c>127.0.0.1</c> at the app's port, and drops any <c>Kestrel</c> config section so
    /// <c>Kestrel:Endpoints</c> can't add addresses; <c>--urls</c> is overridden by the explicit endpoint.</summary>
    public static void ConfigureKestrel(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
        options.Listen(ServerEndpoint.Address);
    }

    /// <summary>The push store and the backlog worktrees folder next to the token file, the crypto (clock from the
    /// container, subject from <c>PushSubject</c>), the real no-redirect transport, the watcher and the update
    /// check.</summary>
    private static void AddPush(WebApplicationBuilder builder, string tokenPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;
        string? subject = builder.Configuration["PushSubject"];
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new PushStore(folder));
        builder.Services.AddSingleton(new UpdateSettingsStore(folder, UnixOwner.CurrentUser()));
        builder.Services.AddSingleton(new BacklogWorktree(folder, UnixOwner.CurrentUser()));
        builder.Services.AddSingleton(_ => PushTransport.CreateDefault());
        builder.Services.AddSingleton(services => new PushCrypto(
            services.GetRequiredService<TimeProvider>(), PushCrypto.SystemEphemeralKey, AccessToken.SystemRandom, subject));
        builder.Services.AddSingleton<PushSender>();
        builder.Services.AddSingleton<PushTestLimiter>();
        builder.Services.AddSingleton<PushWatcher>();
        builder.Services.AddHostedService(services => services.GetRequiredService<PushWatcher>());
        builder.Services.AddSingleton(RunningVersion.Current);
        builder.Services.AddSingleton(new UpdateState());
        builder.Services.AddSingleton(_ => UpdateTransport.CreateDefault());
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddHostedService(services => services.GetRequiredService<UpdateChecker>());
    }

    /// <summary>True when the request has exactly one <c>Host</c> value and <see cref="HostCheck.IsAllowed"/>
    /// accepts it. The value is never logged.</summary>
    private static bool IsAllowedHost(HttpRequest request, string? tailnetHost)
    {
        Microsoft.Extensions.Primitives.StringValues values = request.Headers.Host;
        return values.Count == 1 && HostCheck.IsAllowed(values[0], tailnetHost);
    }

    /// <summary>GET and HEAD everywhere; POST only on exactly one of <paramref name="postRoutes"/> (RoomKit's and
    /// the app's); nothing but GET and HEAD before the app's routes are known.</summary>
    private static bool IsAllowedMethod(HttpRequest request, FrozenSet<string>? postRoutes)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return true;
        }

        return HttpMethods.IsPost(request.Method)
            && postRoutes is not null
            && request.Path.Value is string path
            && postRoutes.Contains(path);
    }

    /// <summary>True when the request carries exactly one <c>Authorization: Bearer &lt;token&gt;</c> header that
    /// matches the stored token. The file is read on every request, so a replaced token counts at once; an
    /// unsafe or missing file refuses everything. A token in the query string is never looked at.</summary>
    private static bool IsAuthorized(HttpRequest request, string tokenPath)
    {
        Microsoft.Extensions.Primitives.StringValues values = request.Headers.Authorization;
        if (values.Count != 1)
        {
            return false;
        }

        string value = values[0] ?? "";
        const string Scheme = "Bearer ";
        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        TokenFileRead stored = AccessToken.Read(tokenPath);
        if (stored.State != TokenFileState.Valid || stored.Token is null)
        {
            return false;
        }

        return AccessToken.Matches(stored.Token, value[Scheme.Length..]);
    }

    private static async Task WriteUnauthorized(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = "Bearer";
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(UnauthorizedBody);
    }

    /// <summary>Static files: <c>.webmanifest</c> as <c>application/manifest+json</c>, and the service worker
    /// revalidated on every load.</summary>
    private static StaticFileOptions CreateStaticFileOptions()
    {
        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        return new StaticFileOptions
        {
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = context =>
            {
                if (string.Equals(context.Context.Request.Path.Value, ServiceWorkerPath, StringComparison.Ordinal))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            },
        };
    }
}
