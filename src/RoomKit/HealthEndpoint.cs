namespace RoomKit;

/// <summary><c>GET</c>/<c>HEAD /health</c>: the one public route outside the app shell, so the updater can confirm a
/// swap without the access token. It returns only <c>{"version":"…"}</c> (<see cref="AppVersion.Current"/>) with
/// <c>no-store</c>, takes no parameters and still runs after the <c>Host</c> check and the method rule.</summary>
internal static class HealthEndpoint
{
    public const string Route = "/health";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new VersionResponse(AppVersion.Current));
        });
    }
}
