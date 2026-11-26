using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>One updater run from <c>update-result.json</c>: <c>run</c> (<c>YYYYMMDDTHHMMSSZ</c>), <c>result</c>
/// (<c>updated</c> or <c>update_failed</c>), <c>version</c>, and <c>previous</c> (updated only) or <c>reason</c>
/// (failed only, one of <see cref="Reasons"/>).</summary>
internal sealed partial record UpdateResult(string Run, string Result, string Version, string? Previous, string? Reason)
{
    public const string Updated = "updated";

    public const string Failed = "update_failed";

    /// <summary>The fixed failure codes the updater writes.</summary>
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "check", "no_answer", "swap", "bootstrap_failed", "rollback_failed",
    };

    /// <summary>True for a run id like <c>20261118T201500Z</c>.</summary>
    public static bool IsRun(string? value)
    {
        return value is not null && RunPattern().IsMatch(value);
    }

    /// <summary>The result when <paramref name="bytes"/> is exactly one JSON object with the schema's keys (no
    /// others, none missing, each of the right type and pattern), else <c>null</c>.</summary>
    public static UpdateResult? Parse(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(property.Name, property.Value.GetString()!))
                {
                    return null;
                }
            }

            if (!fields.TryGetValue("run", out string? run) || !IsRun(run)
                || !fields.TryGetValue("result", out string? result)
                || !fields.TryGetValue("version", out string? version) || SemVer.Parse(version) is null)
            {
                return null;
            }

            if (result == Updated)
            {
                return fields.Count == 4 && fields.TryGetValue("previous", out string? previous)
                    && SemVer.Parse(previous) is not null
                    ? new UpdateResult(run, result, version, previous, null)
                    : null;
            }

            if (result == Failed)
            {
                return fields.Count == 4 && fields.TryGetValue("reason", out string? reason) && Reasons.Contains(reason)
                    ? new UpdateResult(run, result, version, null, reason)
                    : null;
            }

            return null;
        }
    }

    [GeneratedRegex("^[0-9]{8}T[0-9]{6}Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunPattern();
}

/// <summary>Reads the updater's <c>update-result.json</c> in the folder next to the token file: without following a
/// symlink, only as a regular <c>0600</c> file of at most 4 KiB owned by the current user, in a <c>0700</c> folder
/// owned by the user. A file that breaks a rule or the schema reads as <c>null</c>. The server never writes or
/// deletes it, and never creates the folder for it.</summary>
internal sealed class UpdateResultReader
{
    public const string FileName = "update-result.json";

    public const long MaxFileBytes = 4096;

    private readonly PrivateFolder folder;

    public UpdateResultReader(string folder, uint? owner)
    {
        this.folder = new PrivateFolder(folder, owner);
    }

    public UpdateResult? Read()
    {
        try
        {
            byte[]? bytes = folder.Read(FileName, MaxFileBytes, checkFileOwner: true, createFolder: false);
            return bytes is null ? null : UpdateResult.Parse(bytes);
        }
        catch (PrivateFileException)
        {
            return null;
        }
    }
}
