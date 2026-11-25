using System.Collections.Frozen;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace RoomKit;

/// <summary>The entry point of a RoomKit app. <see cref="Run"/> handles the <c>version</c> and <c>token</c> commands,
/// then builds and runs the server; <see cref="Create(string[], TextWriter, RoomKitOptions, Action{RoomRoutes},
/// Action{ListenOptions}?, FactoryModule?, Action{RoomServices}?)"/> builds it. The core is always there: loopback-only
/// Kestrel on the app's port, security headers, the <c>Host</c> check, GET/HEAD only (plus POST on the update settings
/// and push routes, the module's routes and the routes the app maps through <see cref="RoomRoutes.MapPost"/>), static
/// files from the app's and RoomKit's <c>wwwroot</c>, an access token on every <c>/api/</c> request, the public
/// <c>/health</c> route, <c>/api/version</c>, the update check and its routes, push and its routes, and then the app's
/// own endpoints. The factory features (backlogs, stories, search, inbox, tasks, history, doctor, their web files and
/// the job and backlog push source) come only with a <see cref="FactoryModule"/>.</summary>
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

    /// <summary>The POST routes of RoomKit's core.</summary>
    private static readonly string[] CorePostRoutes =
    [
        UpdateEndpoint.SettingsRoute,
        .. PushEndpoint.PostRoutes,
    ];

    /// <summary>Runs the app: <c>version</c> and <c>token</c> commands print and return; otherwise the server runs
    /// until it is stopped. Returns the process exit code. <paramref name="factory"/> adds the factory features;
    /// <paramref name="addServices"/> runs before the host is built (for example to add push sources).</summary>
    public static int Run(
        string[] args,
        RoomKitOptions options,
        Action<RoomRoutes> mapApp,
        FactoryModule? factory = null,
        Action<RoomServices>? addServices = null)
    {
        UseContext(options, factory);
        if (VersionCommand.IsCommand(args))
        {
            return VersionCommand.Run(args, Console.Out, Console.Error);
        }

        if (TokenCommand.IsCommand(args))
        {
            return TokenCommand.Run(args, Console.Out, Console.Error);
        }

        WebApplication? app = Create(args, Console.Error, options, mapApp, null, factory, addServices);
        if (app is null)
        {
            return 1;
        }

        app.Run();
        return 0;
    }

    /// <summary>Builds the app, or returns <c>null</c> after writing a fixed error text when the
    /// <c>TailnetHost</c> name is not valid, the access token file is not safe or, with <paramref name="factory"/>,
    /// there is no factory root. A missing token file is created (the token itself is never written to
    /// <paramref name="error"/>). <paramref name="onListen"/> is a test seam called for every endpoint Kestrel listens
    /// on (through the public <c>ConfigureEndpointDefaults</c>); <c>null</c> in production. <paramref name="factory"/>
    /// adds the factory features; <paramref name="addServices"/> runs before the host is built.</summary>
    public static WebApplication? Create(
        string[] args,
        TextWriter error,
        RoomKitOptions options,
        Action<RoomRoutes> mapApp,
        Action<ListenOptions>? onListen = null,
        FactoryModule? factory = null,
        Action<RoomServices>? addServices = null)
    {
        return Create(args, error, options, mapApp, onListen, factory, addServices, configure: null);
    }

    /// <summary>As the public <see cref="Create(string[], TextWriter, RoomKitOptions, Action{RoomRoutes},
    /// Action{ListenOptions}?, FactoryModule?, Action{RoomServices}?)"/>; <paramref name="configure"/> is a test seam run
    /// on the builder after RoomKit's services are added and before the static web assets are loaded (for example an
    /// in-memory server, fakes and the environment).</summary>
    internal static WebApplication? Create(
        string[] args,
        TextWriter error,
        RoomKitOptions options,
        Action<RoomRoutes> mapApp,
        Action<ListenOptions>? onListen,
        FactoryModule? factory,
        Action<RoomServices>? addServices,
        Action<WebApplicationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(mapApp);
        UseContext(options, factory);

        // The scheduler's factory root argument is the module's; a core-only app takes its arguments as they are.
        string[]? launchArgs = factory is null ? args : FactoryRootLocator.DecodeScheduledArguments(args);
        if (launchArgs is null)
        {
            error.WriteLine(FactoryRootLocator.NotFoundText);
            return null;
        }

        // Content root (and so wwwroot) is the server's own output folder, never the current directory.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = launchArgs,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        string? root = null;
        if (factory is not null)
        {
            root = FactoryModule.FindRoot(builder.Configuration, error);
            if (root is null)
            {
                return null;
            }
        }

        string? tailnetHost = builder.Configuration[HostCheck.ConfigKey];
        if (tailnetHost is not null && !HostCheck.IsValidTailnetHost(tailnetHost))
        {
            error.WriteLine(HostCheck.InvalidTailnetText);
            return null;
        }

        string? configuredToken = builder.Configuration[AccessToken.ConfigKey];
        string tokenPath = AccessToken.ResolvePath(configuredToken);
        TokenFileRead token = AccessToken.Read(tokenPath);
        if (token.State == TokenFileState.Invalid)
        {
            error.WriteLine(AccessToken.InvalidText(configuredToken));
            return null;
        }

        if (token.State == TokenFileState.Missing)
        {
            if (AccessToken.Replace(tokenPath, AccessToken.SystemRandom) is null)
            {
                error.WriteLine(AccessToken.WriteFailedText(configuredToken));
                return null;
            }

            error.WriteLine(AccessToken.CreatedText(configuredToken));
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
        string dataFolder = Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;
        RoomServices services = new(builder.Services, builder.Configuration);
        if (factory is not null && root is not null)
        {
            FactoryModule.AddServices(services, root, dataFolder);
        }

        AddPush(builder, dataFolder);
        addServices?.Invoke(services);
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
        if (factory is null)
        {
            // Without the module its web files are not served, in every environment and from every web root.
            HiddenFileProvider webRoot = new(app.Environment.WebRootFileProvider, FactoryModule.WebFiles);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
            app.UseStaticFiles(CreateStaticFileOptions(webRoot));
        }
        else
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(CreateStaticFileOptions(null));
            FactoryModule.Map(app);
        }

        PushEndpoint.Map(app);
        VersionEndpoint.Map(app);
        UpdateEndpoint.Map(app, tailnetHost, root);
        HealthEndpoint.Map(app);
        mapApp(new RoomRoutes(app, appPostRoutes));
        IEnumerable<string> kitPostRoutes = factory is null ? CorePostRoutes : [.. FactoryModule.PostRoutes, .. CorePostRoutes];
        postRoutes = kitPostRoutes.Concat(appPostRoutes).ToFrozenSet(StringComparer.Ordinal);
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

    /// <summary>Makes the options (and the module's job format) the process's app.</summary>
    private static void UseContext(RoomKitOptions options, FactoryModule? factory)
    {
        if (factory is null)
        {
            RoomContext.Use(options);
        }
        else
        {
            RoomContext.Use(options, factory.Jobs);
        }
    }

    /// <summary>The push and update stores in the data folder next to the token file, the crypto (clock from the
    /// container, subject from <c>PushSubject</c>), the real no-redirect transport, the watcher over every registered
    /// <see cref="IPushSource"/> and the update check.</summary>
    private static void AddPush(WebApplicationBuilder builder, string folder)
    {
        string? subject = builder.Configuration["PushSubject"];
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new PushStore(folder));
        builder.Services.AddSingleton(new UpdateSettingsStore(folder, UnixOwner.CurrentUser()));
        builder.Services.AddSingleton(_ => PushTransport.CreateDefault());
        builder.Services.AddSingleton(services => new PushCrypto(
            services.GetRequiredService<TimeProvider>(), PushCrypto.SystemEphemeralKey, AccessToken.SystemRandom, subject));
        builder.Services.AddSingleton<PushSender>();
        builder.Services.AddSingleton<PushTestLimiter>();
        builder.Services.AddSingleton(services => new PushWatcher(
            services.GetServices<IPushSource>(),
            services.GetRequiredService<PushStore>(),
            services.GetRequiredService<PushSender>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<PushWatcher>>()));
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
    private static StaticFileOptions CreateStaticFileOptions(IFileProvider? webRoot)
    {
        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        return new StaticFileOptions
        {
            FileProvider = webRoot,
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
