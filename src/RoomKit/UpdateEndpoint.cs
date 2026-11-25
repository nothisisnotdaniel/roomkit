using System.Text.Json;

namespace RoomKit;

/// <summary>The <c>GET /api/update</c> response; <c>latest</c>, <c>url</c> and <c>command</c> are <c>null</c> unless
/// <c>available</c>; <c>autoUpdate</c>, <c>channel</c> and <c>updateInterval</c> are <c>null</c> when
/// <c>settings.json</c> is refused.</summary>
internal sealed record UpdateResponse(
    string Current, bool Available, string? Latest, string? Url, string? Command, bool? AutoUpdate = false,
    string? Channel = UpdateChannel.Stable, int? UpdateInterval = RoomKit.UpdateInterval.Default);

/// <summary>The wanted change of a <c>POST /api/update/settings</c> body; a <c>null</c> value keeps the stored
/// one.</summary>
internal sealed record UpdateSettingsChange(bool? AutoUpdate, string? Channel, int? UpdateInterval);

/// <summary>The update routes, behind the token gate. <c>GET /api/update</c>: the running version and, when the last
/// check found a newer release on the current channel, its version, release page and the command that installs it
/// (with <c>--tailnet-host</c> when <c>TailnetHost</c> is set), plus the <c>autoUpdate</c>, <c>channel</c> and
/// <c>updateInterval</c> settings. <c>POST /api/update/settings</c> with an object of one or more of
/// <c>autoUpdate</c> (boolean), <c>channel</c> (<c>"stable"</c> or <c>"develop"</c>) and <c>updateInterval</c> (an
/// integer among the <see cref="UpdateInterval"/> choices) stores them (else <c>400 {"error":"invalid"}</c>, checked
/// before the store) and answers the three values; a refused <c>settings.json</c> gives <c>500
/// {"error":"settings_store"}</c>. A changed channel starts one update check without waiting for it (skipped while
/// one runs); an interval change starts none. Both <c>no-store</c>.</summary>
internal static class UpdateEndpoint
{
    public const string Route = "/api/update";

    public const string SettingsRoute = "/api/update/settings";

    public const string StoreError = "settings_store";

    public static void Map(WebApplication app, string? tailnetHost, string? factoryRoot)
    {
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], (
            UpdateState state, RunningVersion running, UpdateSettingsStore settings, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            UpdateSettings? stored;
            try
            {
                stored = settings.Settings();
            }
            catch (PrivateFileException)
            {
                stored = null;
            }

            // A refused file checks on stable (UpdateChecker.Channel), so only a stable result is shown then.
            string channel = stored?.Channel ?? UpdateChannel.Stable;
            return Results.Json(Describe(state.Latest, running.Value, tailnetHost, channel, factoryRoot)
                with { AutoUpdate = stored?.AutoUpdate, Channel = stored?.Channel, UpdateInterval = stored?.UpdateInterval });
        });

        app.MapPost(SettingsRoute, async (UpdateSettingsStore settings, UpdateChecker checker, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            byte[]? body = await PushEndpoint.ReadJsonBody(context.Request);
            UpdateSettingsChange? wanted = body is null ? null : ParseSettings(body);
            if (wanted is null)
            {
                return PushEndpoint.Error(StatusCodes.Status400BadRequest, PushEndpoint.Invalid);
            }

            UpdateSettings stored;
            bool channelChanged;
            try
            {
                stored = settings.Set(wanted.AutoUpdate, wanted.Channel, wanted.UpdateInterval, out channelChanged);
            }
            catch (PrivateFileException)
            {
                return PushEndpoint.Error(StatusCodes.Status500InternalServerError, StoreError);
            }

            if (channelChanged)
            {
                // Not awaited: the answer does not wait for GitHub. CheckSafely never throws and skips when a check
                // is already running.
                _ = Task.Run(checker.CheckSafely);
            }

            return Results.Json(new
            {
                autoUpdate = stored.AutoUpdate, channel = stored.Channel, updateInterval = stored.UpdateInterval,
            });
        });
    }

    /// <summary>The response for <paramref name="latest"/>: <c>available</c> only when it was found on
    /// <paramref name="channel"/> and is newer than <paramref name="current"/> by SemVer precedence.</summary>
    public static UpdateResponse Describe(
        UpdateRelease? latest, string current, string? tailnetHost, string channel = UpdateChannel.Stable,
        string? factoryRoot = null)
    {
        SemVer? running = SemVer.Parse(current);
        if (latest is null || running is null || latest.Channel != channel || !latest.Version.IsNewerThan(running))
        {
            return new UpdateResponse(current, false, null, null, null);
        }

        string command = OperatingSystem.IsWindows()
            ? @".\launch-agent.ps1 install -Release v" + latest.Version
                + (factoryRoot is null ? "" : " -FactoryRoot " + RoomContext.PowerShellLiteral(factoryRoot))
                + (tailnetHost is null ? "" : " -TailnetHost " + tailnetHost)
            : "scripts/launch-agent.sh install --release v" + latest.Version
                + (tailnetHost is null ? "" : " --tailnet-host " + tailnetHost);
        return new UpdateResponse(current, true, latest.Version.ToString(), latest.Url, command);
    }

    /// <summary>The change when the body is an object with one or more of <c>autoUpdate</c> (a boolean),
    /// <c>channel</c> (<c>"stable"</c> or <c>"develop"</c>) and <c>updateInterval</c> (an integer among the
    /// <see cref="UpdateInterval"/> choices), each at most once and nothing else; else <c>null</c>.</summary>
    private static UpdateSettingsChange? ParseSettings(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool? autoUpdate = null;
            string? channel = null;
            bool seenAutoUpdate = false;
            bool seenChannel = false;
            int? updateInterval = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("autoUpdate") && !seenAutoUpdate
                    && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    seenAutoUpdate = true;
                    autoUpdate = property.Value.GetBoolean();
                }
                else if (property.NameEquals("channel") && !seenChannel && property.Value.ValueKind == JsonValueKind.String
                         && UpdateChannel.Parse(property.Value.GetString()) is string known)
                {
                    seenChannel = true;
                    channel = known;
                }
                else if (property.NameEquals("updateInterval") && updateInterval is null
                         && property.Value.ValueKind == JsonValueKind.Number
                         && property.Value.TryGetInt32(out int minutes) && UpdateInterval.IsChoice(minutes))
                {
                    updateInterval = minutes;
                }
                else
                {
                    return null;
                }
            }

            return seenAutoUpdate || seenChannel || updateInterval is not null
                ? new UpdateSettingsChange(autoUpdate, channel, updateInterval)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
