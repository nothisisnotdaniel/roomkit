using System.Net.Http.Headers;
using System.Text.Json;

namespace RoomKit;

/// <summary>The push routes, all behind the token gate: <c>GET /api/push</c> (<c>{publicKey, notifyDone,
/// notifyUpdate, subscribed}</c>, <c>subscribed</c> being the number of stored subscriptions), <c>POST /api/push/subscribe</c>,
/// <c>/unsubscribe</c>, <c>/settings</c> and <c>/test</c>. Refused push files give
/// <c>500 {"error":"push_store"}</c>.</summary>
internal static class PushEndpoint
{
    public const string Route = "/api/push";

    public const string SubscribeRoute = "/api/push/subscribe";

    public const string UnsubscribeRoute = "/api/push/unsubscribe";

    public const string SettingsRoute = "/api/push/settings";

    public const string TestRoute = "/api/push/test";

    public const int MaxBodyBytes = 4096;

    public const string StoreError = "push_store";

    public const string InvalidSubscription = "invalid_subscription";

    public const string TooMany = "too_many";

    public const string TooManyRequests = "too_many_requests";

    public const string Invalid = "invalid";

    /// <summary>The routes that take POST.</summary>
    public static readonly IReadOnlySet<string> PostRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        SubscribeRoute, UnsubscribeRoute, SettingsRoute, TestRoute,
    };

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], (PushStore store, HttpContext context) =>
            Guarded(context, () =>
            {
                (PushKeyPair keys, PushSettings settings) = store.Load();
                return Results.Json(new PushInfo(
                    Base64Url.Encode(keys.PublicKey), settings.NotifyDone, settings.NotifyUpdate, settings.Subscriptions.Count));
            }));

        app.MapPost(SubscribeRoute, async (PushStore store, HttpContext context) =>
        {
            byte[]? body = await ReadJsonBody(context.Request);
            return Guarded(context, () =>
            {
                store.Load();
                PushSubscription? subscription = body is null ? null : ParseSubscription(body);
                if (subscription is null)
                {
                    return Error(StatusCodes.Status400BadRequest, InvalidSubscription);
                }

                return store.Subscribe(subscription)
                    ? Results.Json(new { subscribed = true })
                    : Error(StatusCodes.Status400BadRequest, TooMany);
            });
        });

        app.MapPost(UnsubscribeRoute, async (PushStore store, HttpContext context) =>
        {
            byte[]? body = await ReadJsonBody(context.Request);
            return Guarded(context, () =>
            {
                store.Load();
                string? endpoint = body is null ? null : ParseText(body, "endpoint");
                if (endpoint is null || endpoint.Length > PushSubscriptionRules.MaxEndpointLength)
                {
                    return Error(StatusCodes.Status400BadRequest, InvalidSubscription);
                }

                return Results.Json(new { removed = store.Remove([endpoint]) > 0 });
            });
        });

        app.MapPost(SettingsRoute, async (PushStore store, HttpContext context) =>
        {
            byte[]? body = await ReadJsonBody(context.Request);
            return Guarded(context, () =>
            {
                store.Load();
                (bool Valid, bool? NotifyDone, bool? NotifyUpdate) flags = body is null
                    ? (false, null, null)
                    : ParseFlags(body);
                if (!flags.Valid)
                {
                    return Error(StatusCodes.Status400BadRequest, Invalid);
                }

                PushSettings settings = store.SetFlags(flags.NotifyDone, flags.NotifyUpdate);
                return Results.Json(new { notifyDone = settings.NotifyDone, notifyUpdate = settings.NotifyUpdate });
            });
        });

        app.MapPost(TestRoute, async (PushStore store, PushSender sender, PushTestLimiter limiter, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                store.Load();
                if (!limiter.TryStart())
                {
                    return Error(StatusCodes.Status429TooManyRequests, TooManyRequests);
                }

                int sent = await sender.Send(new PushPayload(PushPayload.Test, null, null, null), context.RequestAborted);
                return Results.Json(new { sent });
            }
            catch (PushStoreException)
            {
                return Error(StatusCodes.Status500InternalServerError, StoreError);
            }
        });
    }

    private static IResult Guarded(HttpContext context, Func<IResult> handle)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return handle();
        }
        catch (PushStoreException)
        {
            return Error(StatusCodes.Status500InternalServerError, StoreError);
        }
    }

    /// <summary>The body when it is JSON (UTF-8) and at most <see cref="MaxBodyBytes"/>, else <c>null</c>.</summary>
    internal static async Task<byte[]?> ReadJsonBody(HttpRequest request)
    {
        if (request.ContentType is null
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? media)
            || !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || (media.CharSet is not null && !string.Equals(media.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase))
            || request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        byte[] buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total))) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : buffer[..total];
    }

    private static PushSubscription? ParseSubscription(byte[] body)
    {
        using JsonDocument? document = Parse(body);
        return document is null ? null : PushSubscriptionRules.FromJson(document.RootElement);
    }

    private static string? ParseText(byte[] body, string name)
    {
        using JsonDocument? document = Parse(body);
        return document is { RootElement.ValueKind: JsonValueKind.Object }
            && document.RootElement.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary><c>notifyDone</c> and <c>notifyUpdate</c>, each optional; valid only when the body is an object with
    /// at least one of them and every one given is a boolean.</summary>
    private static (bool Valid, bool? NotifyDone, bool? NotifyUpdate) ParseFlags(byte[] body)
    {
        using JsonDocument? document = Parse(body);
        if (document is not { RootElement.ValueKind: JsonValueKind.Object })
        {
            return (false, null, null);
        }

        (bool doneValid, bool? done) = Flag(document.RootElement, "notifyDone");
        (bool updateValid, bool? update) = Flag(document.RootElement, "notifyUpdate");
        bool valid = doneValid && updateValid && (done is not null || update is not null);
        return (valid, done, update);
    }

    private static (bool Valid, bool? Value) Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return (true, null);
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => (true, true),
            JsonValueKind.False => (true, false),
            _ => (false, null),
        };
    }

    private static JsonDocument? Parse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IResult Error(int status, string error)
    {
        return Results.Content("{\"error\":\"" + error + "\"}", "application/json", statusCode: status);
    }

    private sealed record PushInfo(string PublicKey, bool NotifyDone, bool NotifyUpdate, int Subscribed);
}
