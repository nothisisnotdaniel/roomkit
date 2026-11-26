using System.Net.Http.Json;
using System.Text;

namespace RoomKit.Tests;

/// <summary>Shared helpers for the push tests.</summary>
internal static class PushTestData
{
    public const string AppleEndpoint = "https://web.push.apple.com/QGuQyavXutnMH-abc";

    public static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public static async Task<HttpResponseMessage> Subscribe(HttpClient client, PushReceiver receiver, string endpoint)
    {
        return await client.PostAsync(PushEndpoint.SubscribeRoute, Json(receiver.SubscriptionJson(endpoint)));
    }

    /// <summary>Every push route with a valid body, for the "all routes fail alike" checks.</summary>
    public static IEnumerable<Func<HttpClient, Task<HttpResponseMessage>>> EveryRoute(PushReceiver receiver)
    {
        yield return client => client.GetAsync(PushEndpoint.Route);
        yield return client => Subscribe(client, receiver, AppleEndpoint);
        yield return client => client.PostAsync(
            PushEndpoint.UnsubscribeRoute, Json("{\"endpoint\":\"" + AppleEndpoint + "\"}"));
        yield return client => client.PostAsync(PushEndpoint.SettingsRoute, Json("{\"notifyDone\":true}"));
        yield return client => client.PostAsync(PushEndpoint.TestRoute, null);
    }

    public static async Task<PushInfoBody> Info(HttpClient client)
    {
        return (await client.GetFromJsonAsync<PushInfoBody>(PushEndpoint.Route))!;
    }
}

internal sealed record PushInfoBody(string PublicKey, bool NotifyDone, bool NotifyUpdate, int Subscribed);
