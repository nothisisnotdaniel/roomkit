using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>Reads the story file a numbered backlog line links to. The target must be
/// <c>backlog/NNN-slug.md</c> with the line's own number; the file is read only from the project's
/// <c>backlog</c> folder through <see cref="SafeFileReader.ReadBytesAt(string, string, long)"/>: the folder is opened
/// once without following a symlink and the file relative to it (no-follow, regular file, 1 MiB cap), so a symlink
/// swapped in between check and read can't redirect it. Returns <c>null</c> for any refusal or read error.</summary>
internal static partial class StoryReader
{
    public const string FolderName = "backlog";

    public const long MaxBytes = 1024 * 1024;

    public static string? Read(ProjectFolder project, BacklogEntry entry)
    {
        byte[]? bytes = ReadBytes(project, entry);
        return bytes is null ? null : SafeFileReader.Decode(bytes);
    }

    /// <summary>The story file's raw bytes, with the same rules and refusals as <see cref="Read"/>.</summary>
    public static byte[]? ReadBytes(ProjectFolder project, BacklogEntry entry)
    {
        string? name = FileName(entry);
        return name is null
            ? null
            : SafeFileReader.ReadBytesAt(Path.Combine(project.Path, FolderName), name, MaxBytes);
    }

    /// <summary>The full path of the story file a numbered line links to, or <c>null</c> when the target is not
    /// <c>backlog/NNN-slug.md</c> with the line's own number, or the <c>backlog</c> folder is missing or a symlink.
    /// The file itself is not checked.</summary>
    public static string? ResolvePath(ProjectFolder project, BacklogEntry entry)
    {
        string? name = FileName(entry);
        if (name is null)
        {
            return null;
        }

        try
        {
            DirectoryInfo folder = new(Path.Combine(project.Path, FolderName));
            if (!folder.Exists || folder.LinkTarget is not null)
            {
                return null;
            }

            string folderPath = Path.GetFullPath(folder.FullName);
            string filePath = Path.GetFullPath(Path.Combine(folderPath, name));
            if (!string.Equals(Path.GetDirectoryName(filePath), folderPath, StringComparison.Ordinal))
            {
                return null;
            }

            return filePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The story's file name (<c>NNN-slug.md</c>, one path component) when the line's target is
    /// <c>backlog/NNN-slug.md</c> with the line's own number, else <c>null</c>.</summary>
    private static string? FileName(BacklogEntry entry)
    {
        if (entry.Number is null || entry.Story is null)
        {
            return null;
        }

        Match match = StoryTarget().Match(entry.Story);
        if (!match.Success
            || !BacklogParser.TryParseNumber(match.Groups["number"].Value, out int number)
            || number != entry.Number.Value)
        {
            return null;
        }

        return match.Groups["file"].Value;
    }

    [GeneratedRegex(@"\Abacklog/(?<file>(?<number>[0-9]{3,})-[A-Za-z0-9-]+\.md)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoryTarget();
}
