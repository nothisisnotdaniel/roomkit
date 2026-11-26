using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RoomKit;

/// <summary>One project's fingerprints in the <c>GET /api/changes</c> response; <c>Claims</c> is over the project's
/// <see cref="ClaimState"/> in the <see cref="ClaimWatcher"/>'s memory.</summary>
public sealed record ProjectChanges(string Backlog, string Stories, string Claims);

/// <summary>The <c>GET /api/changes</c> response: the jobs fingerprint and one entry per discovered project.</summary>
public sealed record ChangesResponse(string Jobs, IReadOnlyDictionary<string, ProjectChanges> Projects);

/// <summary>Cheap change fingerprints from file metadata only. Each value is a lowercase SHA-256 hex over a sorted
/// list of (name, marker): a regular file adds its length and last-write UTC ticks, a symlink only the fixed
/// <see cref="LinkMarker"/> (its target is never followed or <c>stat</c>ed), a missing or unreadable path
/// <see cref="MissingMarker"/>, anything else <see cref="OtherMarker"/>. Files are only <c>lstat</c>ed (on Unix
/// through <c>lstat</c>/<c>fstatat(AT_SYMLINK_NOFOLLOW)</c>), never opened or read; the <c>backlog</c> folder is
/// opened with <c>O_NOFOLLOW | O_DIRECTORY</c> only to list its names. Paths come only from
/// <see cref="IJobFormat.JobFolders"/> and <see cref="ProjectFinder"/>.</summary>
public static class ChangeFingerprint
{
    public const string LinkMarker = "link";

    public const string MissingMarker = "missing";

    public const string OtherMarker = "other";

    public const string BacklogFileName = "backlog.md";

    public const string StoriesFolderName = "backlog";

    public const string StoryExtension = ".md";

    /// <summary>Every fingerprint. With a <paramref name="worktree"/> whose folder exists for a project, that
    /// project's <c>backlog</c> and <c>stories</c> also cover the worktree's <c>backlog.md</c> and
    /// <c>backlog/*.md</c> (<c>lstat</c> only, no git). With <paramref name="claims"/> each project's <c>claims</c> is over
    /// its claim state (memory only, no git), else over <see cref="ClaimState.None"/>.</summary>
    public static ChangesResponse Build(
        string factoryRoot, BacklogWorktree? worktree = null, ClaimWatcher? claims = null)
    {
        Dictionary<string, ProjectChanges> projects = new(StringComparer.Ordinal);
        foreach (ProjectFolder project in ProjectFinder.Find(factoryRoot))
        {
            ProjectFolder? copy = worktree is not null && worktree.Exists(project)
                ? new ProjectFolder(project.Name, worktree.PathFor(project))
                : null;
            string claimPrint = Claims(claims?.State(project.Name) ?? ClaimState.None);
            projects[project.Name] = copy is null
                ? new ProjectChanges(Backlog(project), Stories(project), claimPrint)
                : new ProjectChanges(
                    Combine(Backlog(project), Backlog(copy)), Combine(Stories(project), Stories(copy)), claimPrint);
        }

        return new ChangesResponse(Jobs(factoryRoot), projects);
    }

    /// <summary>Over each job folder's record files (<see cref="IJobFormat.RecordFileNames"/>), by job id (ordinal):
    /// the first under the job id, every other one under <c>&lt;id&gt;/&lt;name&gt;</c>.</summary>
    public static string Jobs(string factoryRoot)
    {
        IJobFormat format = RoomContext.Jobs;
        StringBuilder text = new();
        foreach (RoomJobFolder folder in format.JobFolders(factoryRoot))
        {
            for (int index = 0; index < format.RecordFileNames.Count; index++)
            {
                string name = format.RecordFileNames[index];
                Append(text, index == 0 ? folder.Id : folder.Id + "/" + name, NoFollowStat.Of(Path.Combine(folder.Path, name)));
            }
        }

        return Hash(text);
    }

    /// <summary>Over a claim state: each claim's item, job, machine and claimed time, the stale flag and, while stale,
    /// the checked time (the badge shows its age then). A successful read that finds the same claims gives the same
    /// value.</summary>
    public static string Claims(ClaimState state)
    {
        StringBuilder text = new();
        foreach (ItemClaim claim in state.Claims)
        {
            text.Append(claim.Item.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\0');
            foreach (string? value in new[] { claim.Job, claim.Machine, claim.Claimed })
            {
                text.Append(value is null ? "-" : value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value)
                    .Append('\0');
            }

            text.Append('\n');
        }

        text.Append(state.Stale ? "stale" : "fresh");
        if (state.Stale && state.Checked is DateTimeOffset checkedAt)
        {
            text.Append('\0').Append(checkedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Hash(text);
    }

    /// <summary>Over the project's <c>backlog.md</c>.</summary>
    public static string Backlog(ProjectFolder project)
    {
        StringBuilder text = new();
        Append(text, BacklogFileName, NoFollowStat.Of(Path.Combine(project.Path, BacklogFileName)));
        return Hash(text);
    }

    /// <summary>Over every <c>backlog/*.md</c> of the project, by name (ordinal). A <c>backlog</c> that is not a
    /// folder, or one with more than <see cref="NoFollowStat.MaxEntries"/> such names, adds one marker for the folder
    /// itself.</summary>
    public static string Stories(ProjectFolder project)
    {
        StringBuilder text = new();
        string folder = Path.Combine(project.Path, StoriesFolderName);
        PathStat stat = NoFollowStat.Of(folder);
        if (stat.Kind != PathKind.Directory)
        {
            Append(text, StoriesFolderName, stat);
            return Hash(text);
        }

        IReadOnlyList<(string Name, PathStat Stat)>? entries =
            NoFollowStat.ListFolder(folder, StoryExtension, NoFollowStat.MaxEntries, out bool tooMany);
        if (entries is null || tooMany)
        {
            Append(text, StoriesFolderName, tooMany ? new PathStat(PathKind.Other, 0, 0) : PathStat.Missing);
            return Hash(text);
        }

        foreach ((string name, PathStat entry) in entries)
        {
            Append(text, name, entry);
        }

        return Hash(text);
    }

    /// <summary>One fingerprint over the checkout's and the worktree's.</summary>
    private static string Combine(string checkout, string worktree)
    {
        StringBuilder text = new();
        text.Append("checkout:").Append(checkout).Append('\n').Append("worktree:").Append(worktree).Append('\n');
        return Hash(text);
    }

    /// <summary>One line per name; the name is length-prefixed so no name can imitate a separator.</summary>
    private static void Append(StringBuilder text, string name, PathStat stat)
    {
        text.Append(name.Length).Append(':').Append(name).Append('\0');
        switch (stat.Kind)
        {
            case PathKind.File:
                text.Append(stat.Length).Append('\0').Append(stat.LastWriteTicks);
                break;
            case PathKind.Link:
                text.Append(LinkMarker);
                break;
            case PathKind.Missing:
                text.Append(MissingMarker);
                break;
            default:
                text.Append(OtherMarker);
                break;
        }

        text.Append('\n');
    }

    private static string Hash(StringBuilder text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>What <see cref="NoFollowStat"/> found at a path.</summary>
public enum PathKind
{
    Missing,
    Link,
    File,
    Directory,
    Other,
}

/// <summary>The kind of a path and, for a regular file, its length and last-write UTC ticks.</summary>
public readonly record struct PathStat(PathKind Kind, long Length, long LastWriteTicks)
{
    public static PathStat Missing => new(PathKind.Missing, 0, 0);
}

/// <summary><c>lstat</c> and a no-follow folder listing through P/Invoke (macOS and Linux), so a symlink is never
/// followed and its target never <c>stat</c>ed (the managed <see cref="FileInfo"/> and folder enumeration do stat a
/// symlink's target). On Windows a managed fallback is used.</summary>
public static class NoFollowStat
{
    // struct stat is at most 144 bytes on the supported platforms; the buffer leaves room.
    private const int StatBufferBytes = 512;

    private const uint TypeMask = 0xF000;
    private const uint TypeLink = 0xA000;
    private const uint TypeFile = 0x8000;
    private const uint TypeDirectory = 0x4000;

    /// <summary>At most this many names are listed in one folder; a folder with more counts as one marker.</summary>
    public const int MaxEntries = 10000;

    /// <summary>The path itself, without following a final symlink. A libc without the expected entry points (older
    /// glibc, musl) counts as missing.</summary>
    public static PathStat Of(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return ManagedOf(path);
        }

        try
        {
            return NativeOf(path);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return PathStat.Missing;
        }
    }

    private static PathStat NativeOf(string path)
    {

        byte[] buffer = new byte[StatBufferBytes];
        int result;
        if (OperatingSystem.IsMacOS())
        {
            result = RuntimeInformation.OSArchitecture == Architecture.X64
                ? MacLstatInode64(path, buffer)
                : MacLstat(path, buffer);
        }
        else
        {
            result = LinuxLstat(path, buffer);
        }

        return result == 0 ? Parse(buffer) : PathStat.Missing;
    }

    /// <summary>The names in the folder that end with <paramref name="extension"/> (ordinal), sorted ordinally,
    /// each with its own no-follow stat; <c>null</c> when the folder can't be opened (missing, a symlink, not a
    /// folder, or no usable libc). With more than <paramref name="maxEntries"/> names, <paramref name="tooMany"/> is
    /// set and the list is empty (nothing is <c>stat</c>ed).</summary>
    public static IReadOnlyList<(string Name, PathStat Stat)>? ListFolder(
        string folder, string extension, int maxEntries, out bool tooMany)
    {
        tooMany = false;
        if (OperatingSystem.IsWindows())
        {
            return ManagedList(folder, extension, maxEntries, out tooMany);
        }

        try
        {
            return NativeList(folder, extension, maxEntries, out tooMany);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static List<(string Name, PathStat Stat)>? NativeList(
        string folder, string extension, int maxEntries, out bool tooMany)
    {
        tooMany = false;

        bool mac = OperatingSystem.IsMacOS();
        bool macX64 = mac && RuntimeInformation.OSArchitecture == Architecture.X64;
        bool linuxArm = !mac && RuntimeInformation.OSArchitecture == Architecture.Arm64;
        int flags = mac
            ? 0x100 | 0x100000 | 0x1000000 // O_NOFOLLOW | O_DIRECTORY | O_CLOEXEC
            : linuxArm
                ? 0x8000 | 0x4000 | 0x80000
                : 0x20000 | 0x10000 | 0x80000;
        int descriptor = mac ? MacOpen(folder, flags) : LinuxOpen(folder, flags);
        if (descriptor < 0)
        {
            return null;
        }

        IntPtr directory = macX64 ? MacFdOpenDirInode64(descriptor) : mac ? MacFdOpenDir(descriptor) : LinuxFdOpenDir(descriptor);
        if (directory == IntPtr.Zero)
        {
            _ = mac ? MacClose(descriptor) : LinuxClose(descriptor);
            return null;
        }

        try
        {
            // dirent: d_name at 21 on Darwin (64-bit inodes), at 19 in Linux dirent64.
            int nameOffset = mac ? 21 : 19;
            List<string> names = [];
            while (true)
            {
                IntPtr entry = macX64 ? MacReadDirInode64(directory) : mac ? MacReadDir(directory) : LinuxReadDir64(directory);
                if (entry == IntPtr.Zero)
                {
                    break;
                }

                string? name = Marshal.PtrToStringUTF8(entry + nameOffset);
                if (name is not null && name.Length > extension.Length && name.EndsWith(extension, StringComparison.Ordinal))
                {
                    names.Add(name);
                    if (names.Count > maxEntries)
                    {
                        tooMany = true;
                        return [];
                    }
                }
            }

            names.Sort(string.CompareOrdinal);
            List<(string Name, PathStat Stat)> entries = [];
            int noFollow = mac ? 0x20 : 0x100; // AT_SYMLINK_NOFOLLOW
            foreach (string name in names)
            {
                byte[] buffer = new byte[StatBufferBytes];
                int result = macX64
                    ? MacFstatAtInode64(descriptor, name, buffer, noFollow)
                    : mac ? MacFstatAt(descriptor, name, buffer, noFollow) : LinuxFstatAt(descriptor, name, buffer, noFollow);
                entries.Add((name, result == 0 ? Parse(buffer) : PathStat.Missing));
            }

            return entries;
        }
        finally
        {
            // closedir also closes the descriptor.
            _ = mac ? MacCloseDir(directory) : LinuxCloseDir(directory);
        }
    }

    /// <summary>Reads mode, size and modification time from a <c>struct stat</c>: Darwin (64-bit inodes) mode at 4
    /// (16 bit), mtime at 48, size at 96; glibc mode at 24 (x64) or 16 (arm64/generic), size at 48, mtime at
    /// 88.</summary>
    private static PathStat Parse(byte[] buffer)
    {
        uint mode;
        long seconds;
        long nanoseconds;
        long size;
        if (OperatingSystem.IsMacOS())
        {
            mode = BitConverter.ToUInt16(buffer, 4);
            seconds = BitConverter.ToInt64(buffer, 48);
            nanoseconds = BitConverter.ToInt64(buffer, 56);
            size = BitConverter.ToInt64(buffer, 96);
        }
        else
        {
            mode = BitConverter.ToUInt32(buffer, RuntimeInformation.OSArchitecture == Architecture.X64 ? 24 : 16);
            size = BitConverter.ToInt64(buffer, 48);
            seconds = BitConverter.ToInt64(buffer, 88);
            nanoseconds = BitConverter.ToInt64(buffer, 96);
        }

        return (mode & TypeMask) switch
        {
            TypeLink => new PathStat(PathKind.Link, 0, 0),
            TypeDirectory => new PathStat(PathKind.Directory, 0, 0),
            TypeFile => new PathStat(
                PathKind.File,
                size,
                unchecked(DateTime.UnixEpoch.Ticks + (seconds * TimeSpan.TicksPerSecond) + (nanoseconds / 100))),
            _ => new PathStat(PathKind.Other, 0, 0),
        };
    }

    private static PathStat ManagedOf(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (file.LinkTarget is not null)
            {
                return new PathStat(PathKind.Link, 0, 0);
            }

            if (file.Exists)
            {
                return new PathStat(PathKind.File, file.Length, file.LastWriteTimeUtc.Ticks);
            }

            return Directory.Exists(path) ? new PathStat(PathKind.Directory, 0, 0) : PathStat.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PathStat.Missing;
        }
    }

    private static List<(string Name, PathStat Stat)>? ManagedList(
        string folder, string extension, int maxEntries, out bool tooMany)
    {
        tooMany = false;
        try
        {
            List<string> names = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Select(entry => entry.Name)
                .Where(name => name.Length > extension.Length && name.EndsWith(extension, StringComparison.Ordinal))
                .ToList();
            if (names.Count > maxEntries)
            {
                tooMany = true;
                return [];
            }

            names.Sort(string.CompareOrdinal);
            return names.Select(name => (name, ManagedOf(Path.Combine(folder, name)))).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("libSystem.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int MacLstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libSystem.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int MacLstatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libSystem.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libSystem.dylib", EntryPoint = "close", SetLastError = true)]
    private static extern int MacClose(int descriptor);

    [DllImport("libSystem.dylib", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr MacFdOpenDir(int descriptor);

    [DllImport("libSystem.dylib", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static extern IntPtr MacFdOpenDirInode64(int descriptor);

    [DllImport("libSystem.dylib", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr MacReadDir(IntPtr directory);

    [DllImport("libSystem.dylib", EntryPoint = "readdir$INODE64", SetLastError = true)]
    private static extern IntPtr MacReadDirInode64(IntPtr directory);

    [DllImport("libSystem.dylib", EntryPoint = "closedir", SetLastError = true)]
    private static extern int MacCloseDir(IntPtr directory);

    [DllImport("libSystem.dylib", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int MacFstatAt(
        int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buffer, int flags);

    [DllImport("libSystem.dylib", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int MacFstatAtInode64(
        int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buffer, int flags);

    [DllImport("libc.so.6", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LinuxLstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    private static extern int LinuxClose(int descriptor);

    [DllImport("libc.so.6", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr LinuxFdOpenDir(int descriptor);

    [DllImport("libc.so.6", EntryPoint = "readdir64", SetLastError = true)]
    private static extern IntPtr LinuxReadDir64(IntPtr directory);

    [DllImport("libc.so.6", EntryPoint = "closedir", SetLastError = true)]
    private static extern int LinuxCloseDir(IntPtr directory);

    [DllImport("libc.so.6", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int LinuxFstatAt(
        int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buffer, int flags);
}
