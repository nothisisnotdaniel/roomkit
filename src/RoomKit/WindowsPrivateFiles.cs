using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
        FileSecurity security = new();
        SecurityIdentifier user = User;
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, security);
    }

    public static byte[]? Read(FileInfo file, long maxBytes)
    {
        // Pin the parent against rename and open the file itself without following a reparse point.
        // Sharing delete on the file lets a rotation rename the next complete token over this one.
        const uint OpenReparsePoint = 0x00200000;
        const uint BackupSemantics = 0x02000000;
        const uint OpenExisting = 3;
        DirectoryInfo directory = file.Directory!;
        using SafeFileHandle parent = CreateFileW(directory.FullName, (uint)FileSystemRights.ReadAttributes,
            FileShare.ReadWrite, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (parent.IsInvalid || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException();
        }

        Check(directory);
        const uint GenericRead = 0x80000000;
        using SafeFileHandle handle = CreateFileW(file.FullName, GenericRead, FileShare.Read | FileShare.Delete,
            0, OpenExisting, OpenReparsePoint, 0);
        if (handle.IsInvalid || (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException();
        }

        using FileStream stream = new(handle, FileAccess.Read);
        CheckDescriptor(new RawSecurityDescriptor(stream.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0), User);
        return SafeFileReader.ReadCapped(stream, maxBytes);
    }

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
