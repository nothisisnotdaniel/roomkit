using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoomKit;

/// <summary>Reads a UTF-8 text file safely: refuses a missing file, a symlink, anything that is not a regular file
/// (FIFO, socket, device), a file over the size cap and any read error (returns <c>null</c> then). Strips a
/// leading BOM.</summary>
public static class SafeFileReader
{
    public static string? ReadText(string path, long maxBytes)
    {
        byte[]? bytes = ReadBytes(path, maxBytes);
        return bytes is null ? null : Decode(bytes);
    }

    /// <summary>The file's raw bytes with the same refusals as <see cref="ReadText"/>, or <c>null</c>.</summary>
    public static byte[]? ReadBytes(string path, long maxBytes)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > maxBytes)
            {
                return null;
            }

            using FileStream? stream = OpenRegularFile(file.FullName);
            return stream is null ? null : ReadCapped(stream, maxBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The bytes of the file <paramref name="name"/> directly inside <paramref name="folder"/>, read with the
    /// same refusals as <see cref="ReadBytes"/>; the folder itself must not be a symlink. On Unix the folder is opened
    /// once (<see cref="OpenFolder"/>) and the file relative to it (<see cref="ReadBytesAt(SafeFileHandle, string, long)"/>),
    /// so a symlink swapped in after the checks can't redirect the read. On Windows the folder is checked with
    /// <see cref="FileSystemInfo.LinkTarget"/> and the file then opened by full path (check-then-open; the race
    /// remains there).</summary>
    public static byte[]? ReadBytesAt(string folder, string name, long maxBytes)
    {
        if (!IsSingleName(name))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                DirectoryInfo directory = new(folder);
                if (!directory.Exists || directory.LinkTarget is not null)
                {
                    return null;
                }

                return ReadBytes(Path.Combine(directory.FullName, name), maxBytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        using SafeFileHandle handle = OpenFolder(folder);
        return handle.IsInvalid ? null : ReadBytesAt(handle, name, maxBytes);
    }

    /// <summary>Opens the folder with <c>open(path, O_RDONLY | O_NOFOLLOW | O_DIRECTORY | O_CLOEXEC)</c>: the handle
    /// is invalid when the path is missing, a symlink, not a folder, on Windows, or without a usable libc. The caller
    /// disposes it.</summary>
    public static SafeFileHandle OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows() || !HasKnownFlags())
        {
            return new SafeFileHandle(-1, ownsHandle: true);
        }

        try
        {
            bool mac = OperatingSystem.IsMacOS();
            int flags = mac
                ? 0x0000 | 0x0100 | 0x100000 | 0x01000000 // O_RDONLY | O_NOFOLLOW | O_DIRECTORY | O_CLOEXEC
                : RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? 0x0000 | 0x8000 | 0x4000 | 0x80000
                    : 0x0000 | 0x20000 | 0x10000 | 0x80000;
            int descriptor = mac ? MacOpen(path, flags) : LinuxOpen(path, flags);
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new SafeFileHandle(-1, ownsHandle: true);
        }
    }

    /// <summary>The bytes of the file <paramref name="name"/> inside the opened folder, opened with
    /// <c>openat(folder, name, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)</c>; <c>null</c> when the name is not a
    /// single path component, or the entry is missing, a symlink, not a regular file (folder, FIFO, socket, device),
    /// over <paramref name="maxBytes"/>, unreadable, or the folder handle is invalid. Always <c>null</c> on
    /// Windows.</summary>
    public static byte[]? ReadBytesAt(SafeFileHandle folder, string name, long maxBytes)
    {
        if (OperatingSystem.IsWindows() || !HasKnownFlags() || folder.IsInvalid || folder.IsClosed
            || !IsSingleName(name))
        {
            return null;
        }

        try
        {
            using SafeFileHandle handle = OpenAtNoFollowNonBlocking(folder, name);
            if (handle.IsInvalid || !IsRegularFile(handle))
            {
                return null;
            }

            using FileStream stream = new(handle, FileAccess.Read, bufferSize: 0);
            if (!stream.CanSeek)
            {
                return null;
            }

            return ReadCapped(stream, maxBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>UTF-8 text of the bytes (invalid sequences become U+FFFD) without a leading BOM.</summary>
    public static string Decode(byte[] bytes)
    {
        string text = new UTF8Encoding(false).GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text;
    }

    /// <summary>The version of a file's content: lowercase hex SHA-256 of its bytes.</summary>
    public static string Version(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Reads the whole stream when it is at most <paramref name="maxBytes"/> long and does not grow while it
    /// is read; else <c>null</c>.</summary>
    internal static byte[]? ReadCapped(FileStream stream, long maxBytes)
    {
        if (stream.Length > maxBytes)
        {
            return null;
        }

        byte[] buffer = new byte[stream.Length + 1];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        if (total > stream.Length)
        {
            return null;
        }

        return buffer[..total];
    }

    /// <summary>True for one path component: not empty, not <c>.</c> or <c>..</c>, without <c>/</c>, <c>\</c> or
    /// NUL.</summary>
    private static bool IsSingleName(string name)
    {
        return name.Length > 0
            && name != "."
            && name != ".."
            && name.IndexOfAny(['/', '\\', '\0']) < 0;
    }

    /// <summary>True on macOS and on Linux x64 or arm64, the only targets whose open flags and <c>struct stat</c>
    /// layout are known here; the folder-relative read fails closed everywhere else.</summary>
    private static bool HasKnownFlags()
    {
        return OperatingSystem.IsMacOS()
            || (OperatingSystem.IsLinux()
                && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64);
    }

    private static SafeFileHandle OpenAtNoFollowNonBlocking(SafeFileHandle folder, string name)
    {
        // Flags: O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, as in OpenNoFollowNonBlocking.
        int linuxNoFollow = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
        int descriptor;
        bool added = false;
        try
        {
            folder.DangerousAddRef(ref added);
            int folderDescriptor = folder.DangerousGetHandle().ToInt32();
            descriptor = OperatingSystem.IsMacOS()
                ? MacOpenAt(folderDescriptor, name, 0x0000 | 0x0004 | 0x0100 | 0x01000000)
                : LinuxOpenAt(folderDescriptor, name, 0x0000 | 0x0800 | linuxNoFollow | 0x80000);
        }
        finally
        {
            if (added)
            {
                folder.DangerousRelease();
            }
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    /// <summary>True when <c>fstat</c> on the open descriptor says regular file (mode at offset 4, 16 bit, on Darwin;
    /// at 24 on glibc x64 and 16 on arm64).</summary>
    private static bool IsRegularFile(SafeFileHandle handle)
    {
        byte[] buffer = new byte[512];
        bool added = false;
        int result;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = handle.DangerousGetHandle().ToInt32();
            result = OperatingSystem.IsMacOS()
                ? RuntimeInformation.OSArchitecture == Architecture.X64
                    ? MacFstatInode64(descriptor, buffer)
                    : MacFstat(descriptor, buffer)
                : LinuxFstat(descriptor, buffer);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        if (result != 0)
        {
            return false;
        }

        uint mode = OperatingSystem.IsMacOS()
            ? BitConverter.ToUInt16(buffer, 4)
            : BitConverter.ToUInt32(buffer, RuntimeInformation.OSArchitecture == Architecture.X64 ? 24 : 16);
        return (mode & 0xF000) == 0x8000;
    }

    /// <summary>Opens the file for reading without following a symlink and without blocking (so a FIFO can't
    /// hang the request), and returns it only when it is a regular (seekable) file.</summary>
    private static FileStream? OpenRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            FileStream windowsStream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return KeepIfRegular(windowsStream);
        }

        SafeFileHandle handle = OpenNoFollowNonBlocking(path);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return KeepIfRegular(stream);
    }

    private static FileStream? KeepIfRegular(FileStream stream)
    {
        if (stream.CanSeek)
        {
            return stream;
        }

        stream.Dispose();
        return null;
    }

    private static SafeFileHandle OpenNoFollowNonBlocking(string path)
    {
        // Flags: O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC (the values differ per platform; on Linux,
        // O_NOFOLLOW is 0x8000 on arm64 and 0x20000 on x64). Linux needs glibc (libc.so.6).
        int linuxNoFollow = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
        int descriptor = OperatingSystem.IsMacOS()
            ? MacOpen(path, 0x0000 | 0x0004 | 0x0100 | 0x01000000)
            : LinuxOpen(path, 0x0000 | 0x0800 | linuxNoFollow | 0x80000);
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [DllImport("libSystem.dylib", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libSystem.dylib", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MacOpenAt(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libc.so.6", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxOpenAt(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libSystem.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFstat(int descriptor, byte[] buffer);

    [DllImport("libSystem.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFstatInode64(int descriptor, byte[] buffer);

    [DllImport("libc.so.6", EntryPoint = "fstat", SetLastError = true)]
    private static extern int LinuxFstat(int descriptor, byte[] buffer);
}
