namespace RoomKit;

/// <summary>The <c>GET /api/version</c> response.</summary>
internal sealed record VersionResponse(string Version);

/// <summary><c>GET /api/version</c>: the running version (<see cref="AppVersion.Current"/>). Read-only, no
/// parameters.</summary>
internal static class VersionEndpoint
{
    public const string Route = "/api/version";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new VersionResponse(AppVersion.Current));
        });
    }
}
