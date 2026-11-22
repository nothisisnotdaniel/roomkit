using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RoomKit;

/// <summary>What a notification is about. Only these fields go into the payload; the reason comes from a fixed
/// table keyed by kind, never from record text.</summary>
internal sealed record PushPayload(
    string Kind,
    string? Project,
    string? Job,
    string? Stage,
    int? Count = null,
    string? Version = null,
    string? Previous = null,
    string? Code = null)
{
    public const string NeedsHuman = "needs-human";

    public const string MergeConflict = "merge-conflict";

    public const string CiFailed = "ci-failed";

    public const string Done = "done";

    public const string Approval = "approval";

    public const string Test = "test";

    public const string Update = "update";

    public const string Updated = "updated";

    public const string UpdateFailed = "update_failed";

    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [NeedsHuman] = "Needs you",
        [MergeConflict] = "Merge conflict",
        [CiFailed] = "CI failed after merge",
        [Done] = "Done",
        [Approval] = "New items to approve",
        [Test] = "Test notification",
        [Update] = "Update available",
        [Updated] = "Updated",
    };

    /// <summary>The payload for an updater result: <c>updated</c> with both versions, or <c>update_failed</c> with
    /// the result's fixed code as its reason.</summary>
    public static PushPayload From(UpdateResult result)
    {
        return result.Result == UpdateResult.Updated
            ? new PushPayload(Updated, null, null, null, Version: result.Version, Previous: result.Previous)
            : new PushPayload(UpdateFailed, null, null, null, Version: result.Version, Code: result.Reason);
    }

    /// <summary>The kinds that need the user: sent with <c>Urgency: high</c>.</summary>
    public bool NeedsYou => Kind is NeedsHuman or MergeConflict or CiFailed;

    /// <summary>The JSON body: <c>kind</c>, <c>project</c>, <c>job</c>, <c>stage</c> (each left out when
    /// unknown), <c>reason</c> and, for approvals, <c>count</c>. An update is exactly <c>kind</c>, <c>version</c> and
    /// <c>reason</c>; <c>updated</c> is exactly <c>kind</c>, <c>version</c>, <c>previous</c> and <c>reason</c>;
    /// <c>update_failed</c> is exactly <c>kind</c>, <c>version</c> and the code as <c>reason</c>.</summary>
    public byte[] ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            if (Kind == Updated || Kind == UpdateFailed)
            {
                writer.WriteString("version", Version);
                if (Kind == Updated)
                {
                    writer.WriteString("previous", Previous);
                    writer.WriteString("reason", Reasons[Kind]);
                }
                else
                {
                    writer.WriteString("reason", Code);
                }

                writer.WriteEndObject();
                writer.Flush();
                return stream.ToArray();
            }

            if (Kind == Update)
            {
                writer.WriteString("version", Version);
                writer.WriteString("reason", Reasons[Kind]);
                writer.WriteEndObject();
                writer.Flush();
                return stream.ToArray();
            }

            if (Project is not null)
            {
                writer.WriteString("project", Project);
            }

            if (Job is not null)
            {
                writer.WriteString("job", Job);
            }

            if (Stage is not null)
            {
                writer.WriteString("stage", Stage);
            }

            writer.WriteString("reason", Reasons[Kind]);
            if (Kind == Approval && Count is not null)
            {
                writer.WriteNumber("count", Count.Value);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

/// <summary>The HTTP handler push requests go through (tests put a fake one in).</summary>
internal sealed class PushTransport
{
    public PushTransport(HttpMessageHandler handler)
    {
        Handler = handler;
    }

    public HttpMessageHandler Handler { get; }

    /// <summary>The real handler: no redirects, no cookies, no proxy (the endpoint is a capability URL).</summary>
    public static PushTransport CreateDefault()
    {
        return new PushTransport(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = PushSender.Timeout,
        });
    }
}

/// <summary>Sends one encrypted POST per stored subscription with <c>TTL</c>, <c>Urgency</c>,
/// <c>Content-Encoding: aes128gcm</c> and a VAPID <c>Authorization</c>. Redirects are not followed and a request
/// times out after 10 s. <c>404</c>/<c>410</c> removes the subscription; anything else keeps it. Only status codes
/// are logged, never an endpoint or a key.</summary>
internal sealed class PushSender
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const string Ttl = "86400";

    private readonly PushStore store;

    private readonly PushCrypto crypto;

    private readonly HttpClient client;

    private readonly ILogger<PushSender> logger;

    public PushSender(PushStore store, PushCrypto crypto, PushTransport transport, ILogger<PushSender> logger)
    {
        this.store = store;
        this.crypto = crypto;
        this.logger = logger;
        client = new HttpClient(transport.Handler, disposeHandler: false) { Timeout = Timeout };
    }

    /// <summary>Sends the payload to every subscription; returns how many push services accepted it (2xx).
    /// Throws <see cref="PushStoreException"/> when the push files are refused.</summary>
    public async Task<int> Send(PushPayload payload, CancellationToken cancellation)
    {
        (PushKeyPair keys, PushSettings settings) = store.Load();
        byte[] body = payload.ToJson();
        int sent = 0;
        List<string> gone = [];
        foreach (PushSubscription subscription in settings.Subscriptions)
        {
            SendResult result = await SendOne(subscription, keys, payload, body, cancellation);
            if (result == SendResult.Sent)
            {
                sent++;
            }
            else if (result == SendResult.Gone)
            {
                gone.Add(subscription.Endpoint);
            }
        }

        if (gone.Count > 0)
        {
            store.Remove(gone);
        }

        return sent;
    }

    private async Task<SendResult> SendOne(
        PushSubscription subscription, PushKeyPair keys, PushPayload payload, byte[] body, CancellationToken cancellation)
    {
        Uri? endpoint = PushSubscriptionRules.CheckEndpoint(subscription.Endpoint);
        byte[]? point = Base64Url.Decode(subscription.P256dh);
        byte[]? auth = Base64Url.Decode(subscription.Auth);
        if (endpoint is null || point is null || auth is null)
        {
            return SendResult.Kept;
        }

        try
        {
            byte[] encrypted = crypto.Encrypt(body, point, auth);
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("TTL", Ttl);
            request.Headers.TryAddWithoutValidation("Urgency", payload.NeedsYou ? "high" : "normal");
            request.Headers.TryAddWithoutValidation("Authorization", crypto.Authorization(endpoint, keys));
            request.Content = new ByteArrayContent(encrypted);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            int status = (int)response.StatusCode;
            logger.LogInformation("Push send returned {Status}", status);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return SendResult.Gone;
            }

            return response.IsSuccessStatusCode ? SendResult.Sent : SendResult.Kept;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            logger.LogWarning("Push send timed out");
            return SendResult.Kept;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or CryptographicException)
        {
            logger.LogWarning("Push send failed");
            return SendResult.Kept;
        }
    }

    private enum SendResult
    {
        Sent,
        Kept,
        Gone,
    }
}
