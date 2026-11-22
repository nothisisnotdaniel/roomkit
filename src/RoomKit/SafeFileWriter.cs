namespace RoomKit;

/// <summary>Writes and deletes files atomically and without following symlinks: a write goes to a new temp file in
/// the same folder, which is then renamed over the target. A symlinked target or folder, or a folder at the target,
/// is refused with an <see cref="IOException"/>.</summary>
public static class SafeFileWriter
{
    private const UnixFileMode DefaultMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static void Write(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string folder = CheckedFolder(full);
        FileInfo target = new(full);
        if (target.LinkTarget is not null || Directory.Exists(full))
        {
            throw new IOException("unsafe target");
        }

        UnixFileMode mode = DefaultMode;
        if (!OperatingSystem.IsWindows() && target.Exists)
        {
            mode = File.GetUnixFileMode(full);
        }

        string temp = Path.Combine(folder, "." + target.Name + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = mode;
            }

            using (FileStream stream = new(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, mode);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes a regular file; a missing file is fine, a symlink or folder is refused.</summary>
    public static void Delete(string path)
    {
        string full = Path.GetFullPath(path);
        CheckedFolder(full);
        FileInfo target = new(full);
        if (target.LinkTarget is not null || Directory.Exists(full))
        {
            throw new IOException("unsafe target");
        }

        if (target.Exists)
        {
            File.Delete(full);
        }
    }

    private static string CheckedFolder(string full)
    {
        string folder = Path.GetDirectoryName(full) ?? throw new IOException("no folder");
        DirectoryInfo info = new(folder);
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new IOException("unsafe folder");
        }

        return folder;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left over only when the folder itself went away.
        }
    }
}
