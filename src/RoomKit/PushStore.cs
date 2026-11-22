using System.Text.Json;

namespace RoomKit;

/// <summary>One stored push subscription: the push service endpoint (a capability URL, never logged) and the
/// receiver keys.</summary>
internal sealed record PushSubscription(string Endpoint, string P256dh, string Auth);

/// <summary>The content of <c>push.json</c>. <c>notifyUpdate</c> (default on), <c>updateNotified</c> (the last
/// announced release version, default <c>null</c>) and <c>updateResultNotified</c> (the <c>run</c> of the last
/// updater result seen, default <c>null</c>) may be missing in an older file.</summary>
internal sealed record PushSettings(
    bool NotifyDone,
    IReadOnlyList<PushSubscription> Subscriptions,
    bool NotifyUpdate = true,
    string? UpdateNotified = null,
    string? UpdateResultNotified = null)
{
    public static readonly PushSettings Default = new(false, []);
}

/// <summary>A push file is a symlink, not a regular file, readable or writable by group or others, or not valid; it
/// is never overwritten then.</summary>
internal sealed class PushStoreException : Exception
{
    public PushStoreException()
        : base("push store refused")
    {
    }

    public PushStoreException(string message)
        : base(message)
    {
    }

    public PushStoreException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The push files next to the token file (outside the factory root): <c>push-keys.json</c> (the VAPID key
/// pair, created on first use) and <c>push.json</c> (subscriptions, <c>notifyDone</c>, <c>notifyUpdate</c> and
/// <c>updateNotified</c>). Both are <c>0600</c> and
/// written atomically (a <c>0600</c> temp file, then rename). The folder must be <c>0700</c> and owned by the current
/// user (it is created so when missing). A symlinked, loose-mode or invalid file or folder throws
/// <see cref="PushStoreException"/> and is left as it is. All access is serialized.</summary>
internal sealed class PushStore
{
    public const string KeysFileName = "push-keys.json";

    public const string SettingsFileName = "push.json";

    public const int MaxSubscriptions = 10;

    public const long MaxFileBytes = 64 * 1024;

    private readonly object gate = new();

    private readonly PrivateFolder folder;

    public PushStore(string folder)
        : this(folder, UnixOwner.CurrentUser())
    {
    }

    /// <summary>A store whose folder must be owned by <paramref name="owner"/> (tests pass another uid to check the
    /// refusal).</summary>
    public PushStore(string folder, uint? owner)
    {
        this.folder = new PrivateFolder(folder, owner);
    }

    public string Folder => folder.Path;

    /// <summary>The uid the folder must be owned by.</summary>
    public uint? Owner => folder.Owner;

    public string KeysPath => Path.Combine(Folder, KeysFileName);

    public string SettingsPath => Path.Combine(Folder, SettingsFileName);

    /// <summary>The VAPID key pair, created when the file is missing.</summary>
    public PushKeyPair Keys()
    {
        lock (gate)
        {
            byte[]? bytes = ReadChecked(KeysPath);
            if (bytes is null)
            {
                PushKeyPair created = PushCrypto.GenerateKeyPair();
                WriteAtomic(KeysPath, SerializeKeys(created));
                return created;
            }

            return ParseKeys(bytes) ?? throw new PushStoreException();
        }
    }

    /// <summary>The settings, with <c>push.json</c> created (empty, <c>notifyDone</c> off) when missing.</summary>
    public PushSettings Settings()
    {
        lock (gate)
        {
            return LoadSettings();
        }
    }

    /// <summary>Checks both files (creating missing ones), so every push route fails the same way.</summary>
    public (PushKeyPair Keys, PushSettings Settings) Load()
    {
        lock (gate)
        {
            PushKeyPair keys = Keys();
            return (keys, LoadSettings());
        }
    }

    /// <summary>Stores a subscription, replacing one with the same endpoint. False when there are already
    /// <see cref="MaxSubscriptions"/> others.</summary>
    public bool Subscribe(PushSubscription subscription)
    {
        lock (gate)
        {
            PushSettings settings = LoadSettings();
            List<PushSubscription> others = settings.Subscriptions
                .Where(stored => !string.Equals(stored.Endpoint, subscription.Endpoint, StringComparison.Ordinal))
                .ToList();
            if (others.Count >= MaxSubscriptions)
            {
                return false;
            }

            others.Add(subscription);
            SaveSettings(settings with { Subscriptions = others });
            return true;
        }
    }

    /// <summary>Removes the subscriptions with these endpoints; returns how many were removed.</summary>
    public int Remove(IReadOnlyCollection<string> endpoints)
    {
        lock (gate)
        {
            PushSettings settings = LoadSettings();
            List<PushSubscription> kept = settings.Subscriptions
                .Where(stored => !endpoints.Contains(stored.Endpoint, StringComparer.Ordinal))
                .ToList();
            int removed = settings.Subscriptions.Count - kept.Count;
            if (removed > 0)
            {
                SaveSettings(settings with { Subscriptions = kept });
            }

            return removed;
        }
    }

    public void SetNotifyDone(bool notifyDone)
    {
        SetFlags(notifyDone, null);
    }

    /// <summary>Sets the flags that are not <c>null</c> in one write; returns the stored settings.</summary>
    public PushSettings SetFlags(bool? notifyDone, bool? notifyUpdate)
    {
        lock (gate)
        {
            PushSettings settings = LoadSettings();
            PushSettings changed = settings with
            {
                NotifyDone = notifyDone ?? settings.NotifyDone,
                NotifyUpdate = notifyUpdate ?? settings.NotifyUpdate,
            };
            SaveSettings(changed);
            return changed;
        }
    }

    /// <summary>Stores the release version the update notice was last given for.</summary>
    public void SetUpdateNotified(string version)
    {
        lock (gate)
        {
            PushSettings settings = LoadSettings();
            SaveSettings(settings with { UpdateNotified = version });
        }
    }

    /// <summary>Stores the <c>run</c> of the updater result the watcher last handled.</summary>
    public void SetUpdateResultNotified(string run)
    {
        lock (gate)
        {
            PushSettings settings = LoadSettings();
            SaveSettings(settings with { UpdateResultNotified = run });
        }
    }

    private PushSettings LoadSettings()
    {
        byte[]? bytes = ReadChecked(SettingsPath);
        if (bytes is null)
        {
            SaveSettings(PushSettings.Default);
            return PushSettings.Default;
        }

        return ParseSettings(bytes) ?? throw new PushStoreException();
    }

    private void SaveSettings(PushSettings settings)
    {
        WriteAtomic(SettingsPath, SerializeSettings(settings));
    }

    /// <summary>The file's bytes, <c>null</c> when there is nothing at the path; throws when the path is a symlink,
    /// a folder, a non-regular or loose-mode file, or can't be read (the folder is created when missing).</summary>
    private byte[]? ReadChecked(string path)
    {
        try
        {
            return folder.Read(Path.GetFileName(path), MaxFileBytes, checkFileOwner: false, createFolder: true);
        }
        catch (PrivateFileException exception)
        {
            throw new PushStoreException("push store refused", exception);
        }
    }

    /// <summary>Writes the file atomically through <see cref="PrivateFolder.Write"/>.</summary>
    private void WriteAtomic(string path, byte[] bytes)
    {
        try
        {
            folder.Write(Path.GetFileName(path), bytes);
        }
        catch (PrivateFileException exception)
        {
            throw new PushStoreException("push store unwritable", exception);
        }
    }

    private static byte[] SerializeKeys(PushKeyPair keys)
    {
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("publicKey", Base64Url.Encode(keys.PublicKey));
            writer.WriteString("privateKey", Base64Url.Encode(keys.PrivateKey));
            writer.WriteEndObject();
        });
    }

    private static PushKeyPair? ParseKeys(byte[] bytes)
    {
        using JsonDocument? document = Parse(bytes);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        byte[]? publicKey = Base64Url.Decode(Text(document.RootElement, "publicKey"));
        byte[]? privateKey = Base64Url.Decode(Text(document.RootElement, "privateKey"));
        if (publicKey is null || privateKey is null)
        {
            return null;
        }

        PushKeyPair keys = new(publicKey, privateKey);
        return PushCrypto.IsValidKeyPair(keys) ? keys : null;
    }

    private static byte[] SerializeSettings(PushSettings settings)
    {
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("notifyDone", settings.NotifyDone);
            writer.WriteBoolean("notifyUpdate", settings.NotifyUpdate);
            if (settings.UpdateNotified is null)
            {
                writer.WriteNull("updateNotified");
            }
            else
            {
                writer.WriteString("updateNotified", settings.UpdateNotified);
            }

            if (settings.UpdateResultNotified is null)
            {
                writer.WriteNull("updateResultNotified");
            }
            else
            {
                writer.WriteString("updateResultNotified", settings.UpdateResultNotified);
            }

            writer.WriteStartArray("subscriptions");
            foreach (PushSubscription subscription in settings.Subscriptions)
            {
                writer.WriteStartObject();
                writer.WriteString("endpoint", subscription.Endpoint);
                writer.WriteStartObject("keys");
                writer.WriteString("p256dh", subscription.P256dh);
                writer.WriteString("auth", subscription.Auth);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static PushSettings? ParseSettings(byte[] bytes)
    {
        using JsonDocument? document = Parse(bytes);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("notifyDone", out JsonElement done)
            || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("subscriptions", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        bool notifyUpdate = true;
        if (root.TryGetProperty("notifyUpdate", out JsonElement update))
        {
            if (update.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            notifyUpdate = update.GetBoolean();
        }

        string? updateNotified = null;
        if (root.TryGetProperty("updateNotified", out JsonElement notified) && notified.ValueKind != JsonValueKind.Null)
        {
            if (notified.ValueKind != JsonValueKind.String || SemVer.Parse(notified.GetString()) is null)
            {
                return null;
            }

            updateNotified = notified.GetString();
        }

        string? updateResultNotified = null;
        if (root.TryGetProperty("updateResultNotified", out JsonElement resultRun)
            && resultRun.ValueKind != JsonValueKind.Null)
        {
            if (resultRun.ValueKind != JsonValueKind.String || !UpdateResult.IsRun(resultRun.GetString()))
            {
                return null;
            }

            updateResultNotified = resultRun.GetString();
        }

        List<PushSubscription> subscriptions = [];
        foreach (JsonElement item in list.EnumerateArray())
        {
            PushSubscription? subscription = PushSubscriptionRules.FromJson(item);
            if (subscription is null)
            {
                return null;
            }

            subscriptions.Add(subscription);
        }

        return new PushSettings(done.GetBoolean(), subscriptions, notifyUpdate, updateNotified, updateResultNotified);
    }

    private static JsonDocument? Parse(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}

/// <summary>The checks a subscription must pass: an <c>https</c> endpoint on port 443 without userinfo on an
/// allowed push service host, a valid 65-byte P-256 <c>p256dh</c> and a 16-byte <c>auth</c>.</summary>
internal static class PushSubscriptionRules
{
    public const int MaxEndpointLength = 2048;

    private static readonly string[] ExactHosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com"];

    private const string AppleSuffix = ".push.apple.com";

    /// <summary>The endpoint as a URI when it passes, else <c>null</c>.</summary>
    public static Uri? CheckEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || uri.Port != 443
            || uri.UserInfo.Length != 0
            || uri.HostNameType != UriHostNameType.Dns
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        string host = uri.IdnHost;
        bool allowed = ExactHosts.Contains(host, StringComparer.Ordinal)
            || (host.EndsWith(AppleSuffix, StringComparison.Ordinal) && host.Length > AppleSuffix.Length
                && IsLabel(host[..^AppleSuffix.Length]));
        return allowed ? uri : null;
    }

    /// <summary>A checked subscription from <c>{endpoint, keys:{p256dh, auth}}</c>, or <c>null</c>.</summary>
    public static PushSubscription? FromJson(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("endpoint", out JsonElement endpoint) || endpoint.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("keys", out JsonElement keys) || keys.ValueKind != JsonValueKind.Object
            || !keys.TryGetProperty("p256dh", out JsonElement p256dh) || p256dh.ValueKind != JsonValueKind.String
            || !keys.TryGetProperty("auth", out JsonElement auth) || auth.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string endpointText = endpoint.GetString()!;
        if (CheckEndpoint(endpointText) is null)
        {
            return null;
        }

        byte[]? point = Base64Url.Decode(p256dh.GetString());
        byte[]? secret = Base64Url.Decode(auth.GetString());
        if (!PushCrypto.IsValidPublicKey(point) || secret is null || secret.Length != PushCrypto.AuthLength)
        {
            return null;
        }

        return new PushSubscription(endpointText, Base64Url.Encode(point!), Base64Url.Encode(secret));
    }

    /// <summary>One or more DNS labels of letters, digits and hyphens (no empty label).</summary>
    private static bool IsLabel(string text)
    {
        foreach (string label in text.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
