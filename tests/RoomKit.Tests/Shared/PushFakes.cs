using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RoomKit.Tests;

/// <summary>A clock fixed at <see cref="Now"/>; its timers never fire on their own, so nothing runs by itself in a
/// test. Each timer's due time and period are recorded in <see cref="Timers"/>; <see cref="WaitForTimer"/> completes
/// once a timer with a given due time was created, and <see cref="Fire"/> runs the live timers with that due time
/// when the test says so.</summary>
internal sealed class FakeTime : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<(TimeSpan Due, TimeSpan Period)> timers = new();

    private readonly object gate = new();

    private readonly List<ManualTimer> live = [];

    private readonly List<(TimeSpan Due, TaskCompletionSource Created)> waiters = [];

    public IReadOnlyList<(TimeSpan Due, TimeSpan Period)> Timers => timers.ToArray();

    public DateTimeOffset Now { get; set; } = Start;

    /// <summary>The zone <c>GetLocalNow()</c> uses; UTC unless a test sets another, so dates never depend on the
    /// machine.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state, dueTime);
        lock (gate)
        {
            timers.Enqueue((dueTime, period));
            live.Add(timer);
            foreach ((TimeSpan due, TaskCompletionSource created) in waiters.Where(waiter => waiter.Due == dueTime).ToList())
            {
                created.TrySetResult();
                waiters.Remove((due, created));
            }
        }

        return timer;
    }

    /// <summary>Waits (bounded by <see cref="FakeUpdateHandler.WaitLimit"/>) until a timer due
    /// <paramref name="due"/> was created.</summary>
    public Task WaitForTimer(TimeSpan due)
    {
        lock (gate)
        {
            if (timers.Any(timer => timer.Due == due))
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((due, created));
            return created.Task.WaitAsync(FakeUpdateHandler.WaitLimit);
        }
    }

    /// <summary>Runs every live (not disposed) timer created with <paramref name="due"/> once; returns how many
    /// ran.</summary>
    public int Fire(TimeSpan due)
    {
        List<ManualTimer> firing;
        lock (gate)
        {
            firing = live.Where(timer => timer.Due == due).ToList();
        }

        foreach (ManualTimer timer in firing)
        {
            timer.Run();
        }

        return firing.Count;
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            live.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly FakeTime owner;

        private readonly TimerCallback callback;

        private readonly object? state;

        public ManualTimer(FakeTime owner, TimerCallback callback, object? state, TimeSpan due)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            Due = due;
        }

        public TimeSpan Due { get; }

        public void Run()
        {
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return true;
        }

        public void Dispose()
        {
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One request the fake push handler saw: URL, headers and body.</summary>
internal sealed record PushRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>A fake push service: records every request and answers with <see cref="Respond"/> (201 by default).
/// It never opens a connection.</summary>
internal sealed class FakePushHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<PushRequest> requests = new();

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.Created);

    public IReadOnlyList<PushRequest> Requests => requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        byte[] body = [];
        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        requests.Enqueue(new PushRequest(request.RequestUri!, headers, body));
        HttpResponseMessage response = Respond(request);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>One request the fake GitHub saw: method, URL and headers (request and content).</summary>
internal sealed record UpdateRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>A fake GitHub API: records every request and answers with <see cref="Respond"/> (404 by default). It
/// never opens a connection. <see cref="WaitForRequests"/> completes once that many requests were seen.</summary>
internal sealed class FakeUpdateHandler : HttpMessageHandler
{
    /// <summary>The bound for <see cref="WaitForRequests"/>, so a broken test fails instead of hanging.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<UpdateRequest> requests = new();

    private readonly object gate = new();

    private readonly List<(int Count, TaskCompletionSource Done)> waiters = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public IReadOnlyList<UpdateRequest> Requests => requests.ToArray();

    /// <summary>Answers <c>200</c> with <c>{"tag_name": tag, …}</c> plus fields that must never be kept.</summary>
    public void RespondWithTag(string tag)
    {
        Respond = (_, _) => Task.FromResult(Ok(
            "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://evil.example/x\",\"body\":\"BODY-MARKER\"}"));
    }

    public static HttpResponseMessage Ok(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Waits (bounded) until at least <paramref name="count"/> requests were recorded.</summary>
    public Task WaitForRequests(int count)
    {
        lock (gate)
        {
            if (requests.Count >= count)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, done));
            return done.Task.WaitAsync(WaitLimit);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        lock (gate)
        {
            requests.Enqueue(new UpdateRequest(request.Method, request.RequestUri!, headers));
            foreach ((int count, TaskCompletionSource done) in waiters.Where(waiter => requests.Count >= waiter.Count).ToList())
            {
                done.TrySetResult();
                waiters.Remove((count, done));
            }
        }

        HttpResponseMessage response = await Respond(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>A test browser: its own P-256 key and auth secret, and an RFC 8291 decrypt.</summary>
internal sealed class PushReceiver : IDisposable
{
    private readonly ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public PushReceiver()
    {
        Auth = RandomNumberGenerator.GetBytes(16);
        PublicKey = PushCrypto.PublicPoint(key.ExportParameters(false));
    }

    public PushReceiver(byte[] publicKey, byte[] privateKey, byte[] auth)
    {
        key = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] },
        });
        Auth = auth;
        PublicKey = publicKey;
    }

    public byte[] PublicKey { get; }

    public byte[] Auth { get; }

    /// <summary>The subscription JSON a browser would send.</summary>
    public string SubscriptionJson(string endpoint)
    {
        return "{\"endpoint\":\"" + endpoint + "\",\"expirationTime\":null,\"keys\":{\"p256dh\":\""
            + Base64Url.Encode(PublicKey) + "\",\"auth\":\"" + Base64Url.Encode(Auth) + "\"}}";
    }

    /// <summary>The plaintext of one <c>aes128gcm</c> record, the padding delimiter removed.</summary>
    public byte[] Decrypt(byte[] body)
    {
        byte[] salt = body[..16];
        int recordSize = (body[16] << 24) | (body[17] << 16) | (body[18] << 8) | body[19];
        Assert.Equal(4096, recordSize);
        int idLength = body[20];
        Assert.Equal(65, idLength);
        byte[] senderPublic = body[21..(21 + idLength)];
        byte[] record = body[(21 + idLength)..];
        using ECDiffieHellman sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..] },
        });
        byte[] shared = key.DeriveRawSecretAgreement(sender.PublicKey);
        byte[] info = [.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. PublicKey, .. senderPublic];
        byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Auth, info);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        byte[] cipher = record[..^16];
        byte[] tag = record[^16..];
        byte[] plain = new byte[cipher.Length];
        using (AesGcm aes = new(cek, 16))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        int end = plain.Length - 1;
        while (end >= 0 && plain[end] == 0)
        {
            end--;
        }

        Assert.True(end >= 0 && plain[end] == 0x02);
        return plain[..end];
    }

    public string DecryptText(byte[] body)
    {
        return Encoding.UTF8.GetString(Decrypt(body));
    }

    public void Dispose()
    {
        key.Dispose();
    }
}

/// <summary>Collects every log line (with its formatted message and its arguments) for checks.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => lines.ToArray();

    public ILogger CreateLogger(string categoryName)
    {
        return new Logger(this);
    }

    public ILogger<T> For<T>()
    {
        return new LoggerFactory([this]).CreateLogger<T>();
    }

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly LogCapture capture;

        public Logger(LogCapture capture)
        {
            this.capture = capture;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                line += " | " + string.Join(" ", values.Select(value => value.Key + "=" + value.Value));
            }

            if (exception is not null)
            {
                line += " | " + exception;
            }

            capture.lines.Enqueue(line);
        }
    }
}

/// <summary>How a <see cref="FakePushBadge"/> hangs before it answers.</summary>
internal enum BadgeHang
{
    /// <summary>Answers at once.</summary>
    None,

    /// <summary>Returns a task that completes only when the fake is disposed.</summary>
    Async,

    /// <summary>Blocks inside <see cref="FakePushBadge.Current"/> until the fake is disposed.</summary>
    Sync,
}

/// <summary>A host badge source a test sets: <see cref="Current"/> returns <see cref="Value"/>, counts its calls and
/// throws (or returns a faulted task) when <see cref="Fail"/> is set. With <see cref="Hang"/> it does not answer
/// until it is disposed; <see cref="Token"/> is the token it was given and <see cref="WaitForCall"/> completes once it
/// was called.</summary>
internal sealed class FakePushBadge : IPushBadgeSource, IDisposable
{
    private readonly ManualResetEventSlim release = new(false);

    private readonly TaskCompletionSource<int?> hung = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource called = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int calls;

    public FakePushBadge(int? value)
    {
        Value = value;
    }

    public int? Value { get; set; }

    /// <summary>When set, <see cref="Current"/> fails with an exception with this message.</summary>
    public string? Fail { get; set; }

    /// <summary>Fail with a faulted task instead of a synchronous throw.</summary>
    public bool Faulted { get; set; }

    /// <summary>Whether (and how) <see cref="Current"/> hangs until the fake is disposed.</summary>
    public BadgeHang Hang { get; set; }

    /// <summary>The services the factory overload built this source from, when it did.</summary>
    public IServiceProvider? BuiltFrom { get; set; }

    public int Calls => Volatile.Read(ref calls);

    /// <summary>The token the last <see cref="Current"/> call was given.</summary>
    public CancellationToken Token { get; private set; }

    /// <summary>Waits (bounded by <see cref="FakeUpdateHandler.WaitLimit"/>) until <see cref="Current"/> was
    /// called.</summary>
    public Task WaitForCall()
    {
        return called.Task.WaitAsync(FakeUpdateHandler.WaitLimit);
    }

    public Task<int?> Current(CancellationToken cancellation)
    {
        Interlocked.Increment(ref calls);
        Token = cancellation;
        called.TrySetResult();
        if (Hang == BadgeHang.Async)
        {
            return hung.Task;
        }

        if (Hang == BadgeHang.Sync)
        {
            release.Wait(CancellationToken.None);
        }

        return Answer();
    }

    /// <summary>Releases a hanging <see cref="Current"/>: it then answers (or fails) as it would have at once.</summary>
    public void Dispose()
    {
        if (Fail is not null)
        {
            hung.TrySetException(new InvalidOperationException(Fail));
        }
        else
        {
            hung.TrySetResult(Value);
        }

        release.Set();
    }

    private Task<int?> Answer()
    {
        if (Fail is not null)
        {
            if (Faulted)
            {
                return Task.FromException<int?>(new InvalidOperationException(Fail));
            }

            throw new InvalidOperationException(Fail);
        }

        return Task.FromResult(Value);
    }
}
