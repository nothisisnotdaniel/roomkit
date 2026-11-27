namespace RoomKit;

/// <summary>What an app's (or a module's) service hook gets before the host is built: the app's
/// <see cref="Services"/>, its <see cref="Configuration"/> and <see cref="AddPushSource(IPushSource)"/>, the way to add a
/// source of push events to RoomKit's push watcher, and <see cref="SetPushBadge(IPushBadgeSource)"/>, the way to put the
/// app badge on every push RoomKit sends.</summary>
public sealed class RoomServices
{
    internal RoomServices(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    /// <summary>The app's services (for example what its own endpoints get).</summary>
    public IServiceCollection Services { get; }

    /// <summary>The app's configuration (command line, environment, appsettings).</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Adds a push source; the watcher asks the sources in the order they were added.</summary>
    public RoomServices AddPushSource(IPushSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.AddSingleton(source);
        return this;
    }

    /// <summary>Adds a push source built from the app's services when the watcher is first needed.</summary>
    public RoomServices AddPushSource(Func<IServiceProvider, IPushSource> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        Services.AddSingleton(create);
        return this;
    }

    /// <summary>Sets the one badge source every push asks for its <c>badge</c> count. Throws
    /// <see cref="InvalidOperationException"/> when a badge source was already set.</summary>
    public RoomServices SetPushBadge(IPushBadgeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RefuseSecondBadge();
        Services.AddSingleton(source);
        return this;
    }

    /// <summary>Sets the one badge source, built from the app's services when the push sender is first needed. Throws
    /// <see cref="InvalidOperationException"/> when a badge source was already set.</summary>
    public RoomServices SetPushBadge(Func<IServiceProvider, IPushBadgeSource> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        RefuseSecondBadge();
        Services.AddSingleton(create);
        return this;
    }

    private void RefuseSecondBadge()
    {
        if (Services.Any(service => service.ServiceType == typeof(IPushBadgeSource)))
        {
            throw new InvalidOperationException("A push badge source is already set.");
        }
    }
}
