namespace RoomKit;

/// <summary>One read of <c>backlog.md</c>: the parsed lines, the raw bytes and their version.</summary>
public sealed record BacklogFile(Backlog Backlog, byte[] Bytes, string Version);

/// <summary>Reads a project's <c>backlog.md</c> through <see cref="SafeFileReader"/>, refusing a missing file, a
/// symlink, anything that is not a regular file (FIFO, socket, device), a file over 1 MiB and any read error
/// (returns <c>null</c> then).</summary>
public static class BacklogReader
{
    public const string FileName = "backlog.md";

    public const long MaxBytes = 1024 * 1024;

    public static Backlog? Read(ProjectFolder project)
    {
        return ReadFile(project)?.Backlog;
    }

    /// <summary>The parsed backlog with the file's raw bytes and its version, from one read.</summary>
    public static BacklogFile? ReadFile(ProjectFolder project)
    {
        byte[]? bytes = SafeFileReader.ReadBytes(Path.Combine(project.Path, FileName), MaxBytes);
        if (bytes is null)
        {
            return null;
        }

        string text = SafeFileReader.Decode(bytes);
        Backlog backlog = BacklogParser.Parse(text.Split('\n').Select(line => line.TrimEnd('\r')));
        return new BacklogFile(backlog, bytes, SafeFileReader.Version(bytes));
    }
}
