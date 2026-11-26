using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>What an app's callback gets to add its own endpoints: the built <see cref="App"/> (for <c>GET</c> and
/// <c>HEAD</c> routes) and <see cref="MapPost"/>, the only way to add a route that takes <c>POST</c>. Every request
/// already went through the <c>Host</c> check, the method rule and, under <c>/api/</c>, the token gate.</summary>
public sealed partial class RoomRoutes
{
    private readonly HashSet<string> postRoutes;

    internal RoomRoutes(WebApplication app, HashSet<string> postRoutes)
    {
        App = app;
        this.postRoutes = postRoutes;
    }

    /// <summary>The app being built; its middleware runs after RoomKit's checks.</summary>
    public WebApplication App { get; }

    /// <summary>Maps a <c>POST</c> route and lets <c>POST</c> through the method rule for exactly that path. The
    /// route must be a fixed path under <c>/api/</c> (lower-case letters, digits, <c>-</c>, <c>_</c> and
    /// <c>/</c>, no parameters), so the token gate always covers it; anything else throws
    /// <see cref="ArgumentException"/>.</summary>
    public RouteHandlerBuilder MapPost(string route, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(handler);
        if (!PostRoutePattern().IsMatch(route))
        {
            throw new ArgumentException("A POST route must be a fixed path under /api/.", nameof(route));
        }

        postRoutes.Add(route);
        return App.MapPost(route, handler);
    }

    [GeneratedRegex(@"\A/api(/[a-z0-9_-]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PostRoutePattern();
}
