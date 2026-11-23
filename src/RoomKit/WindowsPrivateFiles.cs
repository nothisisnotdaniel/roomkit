using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoomKit;

/// <summary>Supply the DACL at creation so even the first handle to a new token writes into a user-only file.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPrivateFiles
{
    private static SecurityIdentifier User
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new UnauthorizedAccessException();
        }
    }

    public static void CreateDirectory(DirectoryInfo directory)
    {
        DirectorySecurity security = new();
        SecurityIdentifier user = User;
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(security);
        Check(directory);
    }

    public static FileStream CreateFile(string path)
    {
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, FilePermissions());
    }

    private static FileSecurity FilePermissions()
    {
        FileSecurity security = new();
        SecurityIdentifier user = User;
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static FileStream OpenDirectory(DirectoryInfo directory)
    {
        const uint OpenReparsePoint = 0x00200000;
        const uint BackupSemantics = 0x02000000;
        const uint OpenExisting = 3;
        // ListDirectory participates in delete-sharing checks. A metadata-only handle does not.
        SafeFileHandle parent = CreateFileW(directory.FullName,
            (uint)(FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions),
            FileShare.ReadWrite, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        try
        {
            if (parent.IsInvalid || (File.GetAttributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory)
            {
                throw new UnauthorizedAccessException();
            }

            FileStream directoryHandle = new(parent, FileAccess.Read);
            CheckHandle(directoryHandle);
            return directoryHandle;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    public static byte[]? Read(FileInfo file, long maxBytes)
    {
        using FileStream directory = OpenDirectory(file.Directory!);
        const uint GenericRead = 0x80000000;
        using SafeFileHandle handle = OpenChild(directory, file.Name, GenericRead, FileShare.Read | FileShare.Delete);
        if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException();
        }

        using FileStream stream = new(handle, FileAccess.Read);
        CheckHandle(stream);
        return SafeFileReader.ReadCapped(stream, maxBytes);
    }

    public static void Replace(FileStream directory, string name, byte[] bytes)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new UnauthorizedAccessException();
        }

        const uint WriteAndDelete = 0x40000000 | (uint)FileSystemRights.Delete;
        using SafeFileHandle handle = OpenChild(directory, "." + Guid.NewGuid().ToString("N") + ".tmp",
            WriteAndDelete, FileShare.None, FilePermissions().GetSecurityDescriptorBinaryForm());
        using FileStream stream = new(handle, FileAccess.Write);
        try
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            RenameChild(handle, name);
        }
        catch
        {
            // Delete the original temporary file by its handle, even if an ancestor was redirected.
            byte delete = 1;
            _ = SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref delete, 1);
            throw;
        }
    }

    private static void CheckHandle(FileStream stream) =>
        CheckDescriptor(new RawSecurityDescriptor(stream.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0), User);

    // NT relative names are resolved from the verified directory object, independent of how the
    // caller reached it (including through a junction higher up in a custom token path).
    private static unsafe SafeFileHandle OpenChild(FileStream directory, string name, uint access, FileShare share,
        byte[]? descriptor = null)
    {
        fixed (char* chars = name)
        fixed (byte* security = descriptor)
        {
            ushort length = checked((ushort)(name.Length * sizeof(char)));
            UnicodeString relativeName = new() { Length = length, MaximumLength = length, Buffer = chars };
            ObjectAttributes attributes = new()
            {
                Length = sizeof(ObjectAttributes),
                RootDirectory = directory.SafeFileHandle.DangerousGetHandle(),
                ObjectName = &relativeName,
                Attributes = 0x40, // OBJ_CASE_INSENSITIVE
                SecurityDescriptor = security,
            };
            const uint Options = 0x00200000 | 0x40 | 0x20; // OPEN_REPARSE_POINT, NON_DIRECTORY_FILE, SYNCHRONOUS_IO_NONALERT
            int status = NtCreateFile(out SafeFileHandle handle, access | (uint)FileSystemRights.Synchronize,
                ref attributes, out _, 0, (uint)FileAttributes.Normal, share, descriptor is null ? 1u : 2u, Options, 0, 0);
            GC.KeepAlive(directory);
            if (status < 0)
            {
                handle.Dispose();
                throw NativeError(status);
            }

            return handle;
        }
    }

    private static unsafe void RenameChild(SafeFileHandle child, string name)
    {
        // A null RootDirectory and a single name rename within the open file's own directory.
        // FILE_RENAME_INFORMATION has a pointer-aligned root handle followed by the counted UTF-16 name.
        int nameOffset = 2 * IntPtr.Size + sizeof(uint);
        byte[] info = new byte[3 * IntPtr.Size + Encoding.Unicode.GetByteCount(name)];
        int nameLength = Encoding.Unicode.GetBytes(name, info.AsSpan(nameOffset));
        fixed (byte* buffer = info)
        {
            // Readers sharing delete keep the old bytes while new opens see the replacement.
            const uint ReplaceWithPosixSemantics = 0x01 | 0x02;
            *(uint*)buffer = ReplaceWithPosixSemantics;
            *(int*)(buffer + 2 * IntPtr.Size) = nameLength;
            int status = NtSetInformationFile(child, out _, buffer, (uint)info.Length, 65 /* FileRenameInformationEx */);
            if (status < 0)
            {
                throw NativeError(status);
            }
        }
    }

    private static IOException NativeError(int status) =>
        new("Private file operation failed.", new System.ComponentModel.Win32Exception(RtlNtStatusToDosError(status)));

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public byte* SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock result, nint allocation, uint fileAttributes, FileShare share, uint disposition,
        uint options, nint extendedAttributes, uint extendedLength);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock result,
        byte* buffer, uint length, int informationClass);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll")]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte buffer, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);

    public static void Check(FileSystemInfo entry)
    {
        entry.Refresh();
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException();
        }

        const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(Sections)
            : ((FileInfo)entry).GetAccessControl(Sections);
        CheckDescriptor(new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0), User);
    }

    internal static void CheckDescriptor(RawSecurityDescriptor descriptor, SecurityIdentifier user)
    {
        if (descriptor.Owner != user || descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException();
        }

        // Inherit-only grants matter too. They affect the privacy of new children.
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not CommonAce rule || (rule.AceQualifier != AceQualifier.AccessDenied
                && (rule.AceQualifier != AceQualifier.AccessAllowed || rule.SecurityIdentifier != user)))
            {
                throw new UnauthorizedAccessException();
            }
        }
    }
}
