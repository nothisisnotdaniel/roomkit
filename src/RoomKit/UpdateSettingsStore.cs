using System.Text;

namespace RoomKit;

/// <summary>The stored update settings: <c>autoUpdate</c>, the update channel (<see cref="UpdateChannel"/>) and how
/// often the updater checks, in minutes (<see cref="UpdateInterval"/>).</summary>
internal sealed record UpdateSettings(bool AutoUpdate, string Channel, int UpdateInterval = RoomKit.UpdateInterval.Default);

/// <summary>The update interval choices in minutes: 30 (the default), 60, 120, 360 and 720.</summary>
internal static class UpdateInterval
{
    public const int Default = 30;

    /// <summary>The choices in order.</summary>
    public static readonly int[] Choices = [30, 60, 120, 360, 720];

    /// <summary>Whether <paramref name="minutes"/> is one of the choices.</summary>
    public static bool IsChoice(long minutes)
    {
        return Choices.Any(choice => choice == minutes);
    }
}

/// <summary>The two update channels: <c>stable</c> (proper releases only, the default) and <c>develop</c> (also
/// the <c>-develop</c> prereleases made for every push to <c>develop</c>).</summary>
internal static class UpdateChannel
{
    public const string Stable = "stable";

    public const string Develop = "develop";

    /// <summary>The channel itself when it is one of the two names (ordinal), else <c>null</c>.</summary>
    public static string? Parse(string? value)
    {
        return value switch
        {
            Stable => Stable,
            Develop => Develop,
            _ => null,
        };
    }
}

/// <summary><c>settings.json</c> next to <c>push.json</c>: exactly one of <c>{"autoUpdate":B}</c> (channel
/// <c>stable</c>), <c>{"autoUpdate":B,"channel":"stable"}</c>, <c>{"autoUpdate":B,"channel":"develop"}</c> (each
/// interval 30) or <c>{"autoUpdate":B,"channel":"stable|develop","updateInterval":N}</c>, with B <c>true</c> or
/// <c>false</c>, N 1-6 ASCII digits without a leading zero (as in JSON, so <c>0</c> too; a value that is not one of the
/// <see cref="UpdateInterval"/> choices reads as 30) and optionally one trailing line break; the store always writes
/// the four-key form with one. The same rules as <see cref="PushStore"/>: <c>0600</c>, written atomically, folder <c>0700</c> and
/// owned by the current user, no symlink; the file must also be owned by the user. A refused or invalid file throws
/// <see cref="PrivateFileException"/> and is never overwritten. The updater (<c>scripts/updater.sh</c>) reads the
/// same exact bytes, so both sides agree on what counts as on and which channel is set.</summary>
internal sealed class UpdateSettingsStore
{
    public const string FileName = "settings.json";

    public const long MaxFileBytes = 64;

    /// <summary>A missing file: off, <c>stable</c>.</summary>
    public static readonly UpdateSettings Default = new(false, UpdateChannel.Stable);

    private static readonly bool[] Flags = [true, false];

    private static readonly string[] Channels = [UpdateChannel.Stable, UpdateChannel.Develop];

    private readonly object gate = new();

    private readonly PrivateFolder folder;

    public UpdateSettingsStore(string folder, uint? owner)
    {
        this.folder = new PrivateFolder(folder, owner);
    }

    public string Path => System.IO.Path.Combine(folder.Path, FileName);

    /// <summary>The stored settings; <see cref="Default"/> when the file is missing.</summary>
    public UpdateSettings Settings()
    {
        lock (gate)
        {
            return Load();
        }
    }

    /// <summary>The stored <c>autoUpdate</c>; <c>false</c> when the file is missing.</summary>
    public bool AutoUpdate()
    {
        return Settings().AutoUpdate;
    }

    /// <summary>Stores <paramref name="autoUpdate"/> and keeps the channel.</summary>
    public bool SetAutoUpdate(bool autoUpdate)
    {
        return Set(autoUpdate, null).AutoUpdate;
    }

    /// <summary>Stores the given values after checking the current file (so a refused file is left as it is); a
    /// <c>null</c> value keeps the stored one. <paramref name="channel"/> must be one of
    /// <see cref="UpdateChannel"/>.</summary>
    public UpdateSettings Set(bool? autoUpdate, string? channel)
    {
        return Set(autoUpdate, channel, null, out _);
    }

    /// <summary><see cref="Set(bool?, string?)"/>, also telling whether the stored channel changed.</summary>
    public UpdateSettings Set(bool? autoUpdate, string? channel, out bool channelChanged)
    {
        return Set(autoUpdate, channel, null, out channelChanged);
    }

    /// <summary><see cref="Set(bool?, string?, out bool)"/> with the interval too; <paramref name="updateInterval"/>
    /// must be one of the <see cref="UpdateInterval"/> choices.</summary>
    public UpdateSettings Set(bool? autoUpdate, string? channel, int? updateInterval, out bool channelChanged)
    {
        if (channel is not null && UpdateChannel.Parse(channel) is null)
        {
            throw new ArgumentException("unknown update channel", nameof(channel));
        }

        if (updateInterval is int minutes && !UpdateInterval.IsChoice(minutes))
        {
            throw new ArgumentException("unknown update interval", nameof(updateInterval));
        }

        lock (gate)
        {
            UpdateSettings current = Load();
            UpdateSettings wanted = new(
                autoUpdate ?? current.AutoUpdate, channel ?? current.Channel, updateInterval ?? current.UpdateInterval);
            folder.Write(FileName, Canonical(wanted));
            channelChanged = wanted.Channel != current.Channel;
            return wanted;
        }
    }

    private UpdateSettings Load()
    {
        byte[]? bytes = folder.Read(FileName, MaxFileBytes, checkFileOwner: true, createFolder: true);
        if (bytes is null)
        {
            return Default;
        }

        return Parse(bytes) ?? throw new PrivateFileException("settings file not valid");
    }

    /// <summary>The settings for the exact allowed contents, else <c>null</c>.</summary>
    public static UpdateSettings? Parse(byte[] bytes)
    {
        foreach (bool flag in Flags)
        {
            if (Matches(bytes, Bytes("{\"autoUpdate\":" + Flag(flag) + "}\n")))
            {
                return new UpdateSettings(flag, UpdateChannel.Stable);
            }

            foreach (string channel in Channels)
            {
                byte[] threeKey = Bytes("{\"autoUpdate\":" + Flag(flag) + ",\"channel\":\"" + channel + "\"}\n");
                if (Matches(bytes, threeKey))
                {
                    return new UpdateSettings(flag, channel);
                }

                byte[] prefix = Bytes("{\"autoUpdate\":" + Flag(flag) + ",\"channel\":\"" + channel + "\",\"updateInterval\":");
                if (bytes.AsSpan().StartsWith(prefix) && ParseInterval(bytes.AsSpan(prefix.Length)) is int minutes)
                {
                    return new UpdateSettings(flag, channel, UpdateInterval.IsChoice(minutes) ? minutes : UpdateInterval.Default);
                }
            }
        }

        return null;
    }

    /// <summary>The number of <c>N}</c> plus at most one line break, N 1-6 ASCII digits without a leading zero
    /// (<c>0</c> itself is allowed, as in JSON); else <c>null</c>.</summary>
    private static int? ParseInterval(ReadOnlySpan<byte> rest)
    {
        if (rest.Length > 0 && rest[^1] == (byte)'\n')
        {
            rest = rest[..^1];
        }

        if (rest.Length < 2 || rest[^1] != (byte)'}')
        {
            return null;
        }

        ReadOnlySpan<byte> digits = rest[..^1];
        // As in JSON: 0, or no leading zero.
        if (digits.Length is < 1 or > 6 || (digits[0] == (byte)'0' && digits.Length > 1))
        {
            return null;
        }

        int value = 0;
        foreach (byte digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                return null;
            }

            value = (value * 10) + (digit - '0');
        }

        return value;
    }

    /// <summary>The four-key form the store writes, with its trailing line break.</summary>
    public static byte[] Canonical(UpdateSettings settings)
    {
        return Bytes("{\"autoUpdate\":" + Flag(settings.AutoUpdate) + ",\"channel\":\"" + settings.Channel
            + "\",\"updateInterval\":" + settings.UpdateInterval + "}\n");
    }

    private static string Flag(bool value)
    {
        return value ? "true" : "false";
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Equal to <paramref name="canonical"/>, with or without its trailing line break.</summary>
    private static bool Matches(byte[] bytes, byte[] canonical)
    {
        return bytes.AsSpan().SequenceEqual(canonical) || bytes.AsSpan().SequenceEqual(canonical.AsSpan(0, canonical.Length - 1));
    }
}
