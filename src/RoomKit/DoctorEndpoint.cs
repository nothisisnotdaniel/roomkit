namespace RoomKit;

/// <summary><c>GET|HEAD /api/doctor</c>: the factory's health problems from <see cref="DoctorCheck"/>. Read-only,
/// <c>no-store</c>, no parameters (a query string is ignored and never becomes a path or an argument).</summary>
internal static class DoctorEndpoint
{
    public const string Route = "/api/doctor";

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], async (FactoryRoot root, TimeProvider time, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(await DoctorCheck.Build(root.Path, time.GetUtcNow()));
        });
    }
}
