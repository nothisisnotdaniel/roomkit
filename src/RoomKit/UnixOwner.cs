using System.Runtime.InteropServices;

namespace RoomKit;

/// <summary>The owner (uid) of a path, read with <c>lstat</c> (no symlink followed), and the current effective user.
/// Unix only; <c>null</c> when it can't be read.</summary>
internal static class UnixOwner
{
    // struct stat is at most 144 bytes on the supported platforms; the buffer leaves room.
    private const int StatBufferBytes = 512;

    /// <summary>The current effective user id, or <c>null</c> on Windows.</summary>
    public static uint? CurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return OperatingSystem.IsMacOS() ? MacGetEuid() : LinuxGetEuid();
    }

    /// <summary>The uid that owns the path itself, or <c>null</c> when it can't be read (or on Windows).</summary>
    public static uint? OwnerOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        byte[] buffer = new byte[StatBufferBytes];
        int result;
        int offset;
        if (OperatingSystem.IsMacOS())
        {
            // Darwin 64-bit-inode struct stat: dev (4), mode (2), nlink (2), ino (8), then uid.
            result = RuntimeInformation.OSArchitecture == Architecture.X64
                ? MacLstatInode64(path, buffer)
                : MacLstat(path, buffer);
            offset = 16;
        }
        else
        {
            // glibc: x64 dev, ino, nlink (8 each), mode (4), then uid; arm64/generic dev, ino (8), mode, nlink (4).
            result = LinuxLstat(path, buffer);
            offset = RuntimeInformation.OSArchitecture == Architecture.X64 ? 28 : 24;
        }

        return result == 0 ? BitConverter.ToUInt32(buffer, offset) : null;
    }

    [DllImport("libSystem.dylib", EntryPoint = "geteuid")]
    private static extern uint MacGetEuid();

    [DllImport("libc.so.6", EntryPoint = "geteuid")]
    private static extern uint LinuxGetEuid();

    [DllImport("libSystem.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int MacLstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libSystem.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int MacLstatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc.so.6", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LinuxLstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
}
