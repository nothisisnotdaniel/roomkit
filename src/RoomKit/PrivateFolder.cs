namespace RoomKit;

/// <summary>A file or folder in a <see cref="PrivateFolder"/> is a symlink, not a regular file, readable or writable
/// by group or others, owned by someone else or can't be read or written; it is never overwritten then.</summary>
internal sealed class PrivateFileException : Exception
{
    public PrivateFileException()
        : base("private file refused")
    {
    }

    public PrivateFileException(string message)
        : base(message)
    {
    }

    public PrivateFileException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The folder next to the token file that holds the server's private files (<c>push-keys.json</c>,
/// <c>push.json</c>, <c>settings.json</c>) and the updater's <c>update-result.json</c>. The folder must be a real
/// folder with mode <c>0700</c>, owned by <see cref="Owner"/>; files are <c>0600</c> regular files read without
/// following a symlink and written atomically (a <c>0600</c> temp file in the folder, then rename). Anything else
/// throws <see cref="PrivateFileException"/>.</summary>
internal sealed class PrivateFolder
{
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public PrivateFolder(string folder, uint? owner)
    {
        Path = System.IO.Path.GetFullPath(folder);
        Owner = owner;
    }

    public string Path { get; }

    /// <summary>The uid the folder (and, when asked, a file) must be owned by; <c>null</c> refuses everything on
    /// Unix.</summary>
    public uint? Owner { get; }

    /// <summary>The bytes of <paramref name="name"/> in the folder, <c>null</c> when there is nothing at the path.
    /// Throws when the folder or file breaks a rule, the file is larger than <paramref name="maxBytes"/> or can't be
    /// read. A missing folder is created <c>0700</c> only when <paramref name="createFolder"/> is set (else nothing
    /// is there to read).</summary>
    public byte[]? Read(string name, long maxBytes, bool checkFileOwner, bool createFolder)
    {
        string path = System.IO.Path.Combine(Path, name);
        try
        {
            if (!CheckFolder(createFolder))
            {
                return null;
            }

            FileInfo file = new(path);
            if (file.LinkTarget is not null || Directory.Exists(path))
            {
                throw new PrivateFileException();
            }

            if (!file.Exists)
            {
                return null;
            }

            if (!OperatingSystem.IsWindows()
                && ((File.GetUnixFileMode(path) & GroupOrOther) != 0
                    || (checkFileOwner && (Owner is null || UnixOwner.OwnerOf(path) != Owner))))
            {
                throw new PrivateFileException();
            }

            return SafeFileReader.ReadBytes(path, maxBytes) ?? throw new PrivateFileException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PrivateFileException("private file unreadable", exception);
        }
    }

    /// <summary>Writes a new <c>0600</c> temp file in the folder, then renames it over <paramref name="name"/>
    /// (re-checked first, so a symlink or loose-mode file that appeared is still refused).</summary>
    public void Write(string name, byte[] bytes)
    {
        string path = System.IO.Path.Combine(Path, name);
        string temp = System.IO.Path.Combine(Path, "." + name + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CheckFolder(create: true);
            FileInfo target = new(path);
            if (target.LinkTarget is not null || Directory.Exists(path)
                || (target.Exists && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & GroupOrOther) != 0))
            {
                throw new PrivateFileException();
            }

            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = AccessToken.FileMode;
            }

            using (FileStream stream = new(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, AccessToken.FileMode);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new PrivateFileException("private file unwritable", exception);
        }
        catch (PrivateFileException)
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Makes sure the folder itself follows the rules, creating it <c>0700</c> when missing. Throws
    /// <see cref="PrivateFileException"/> otherwise.</summary>
    public void EnsureFolder()
    {
        try
        {
            CheckFolder(create: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PrivateFileException("private folder refused", exception);
        }
    }

    /// <summary>True when the folder is there and follows the rules; false when it is missing or breaks one.
    /// Never creates it.</summary>
    public bool IsReady()
    {
        try
        {
            return CheckFolder(create: false);
        }
        catch (Exception exception) when (exception is PrivateFileException or IOException
                                              or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the folder is a real folder (not a symlink) with mode <c>0700</c>, owned by
    /// <see cref="Owner"/>; false when it is missing and <paramref name="create"/> is not set (a missing folder is
    /// created <c>0700</c> otherwise). Throws for anything else.</summary>
    private bool CheckFolder(bool create)
    {
        DirectoryInfo folder = new(Path);
        if (folder.LinkTarget is not null || File.Exists(Path))
        {
            throw new PrivateFileException();
        }

        if (!folder.Exists)
        {
            if (!create)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path);
                return true;
            }

            Directory.CreateDirectory(Path, AccessToken.FolderMode);
            File.SetUnixFileMode(Path, AccessToken.FolderMode);
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode Permissions = AccessToken.FolderMode | GroupOrOther;
        if ((File.GetUnixFileMode(Path) & Permissions) != AccessToken.FolderMode
            || Owner is null
            || UnixOwner.OwnerOf(Path) != Owner)
        {
            throw new PrivateFileException();
        }

        return true;
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
