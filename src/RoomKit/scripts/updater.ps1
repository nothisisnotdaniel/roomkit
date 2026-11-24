#requires -Version 5.1
param([Alias('FactoryRoot')][string]$UpdateFactoryRoot, [string]$FactoryRootBase64)

# roomkit:app-begin
$DataFolder = 'RoomKit'
$TaskPrefix = 'RoomKit'
$ServerName = 'roomkit.exe'
$ZipPrefix = 'roomkit'
$AppPort = 8000
$Repository = 'example/roomkit'
$JobsFolder = 'work/jobs'
$JobFile = 'job.json'
# roomkit:app-end

$ErrorActionPreference = 'Stop'
$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$Support = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $DataFolder
$ServerTask = "$TaskPrefix-server-$($UserSid.Value)"
$UpdaterTask = "$TaskPrefix-updater-$($UserSid.Value)"
$identifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
$TagPattern = "\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-($identifier(?:\.$identifier)*))?\z"
$Utf8 = [Text.UTF8Encoding]::new($false, $true)
$UpdaterLimit = 256KB

function Assert-PrivatePath($Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked private path.' }
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $acl = if ($PSVersionTable.PSEdition -eq 'Core') {
        [IO.FileSystemAclExtensions]::GetAccessControl($item, $sections)
    } else {
        $item.GetAccessControl($sections)
    }
    Assert-PrivateAcl $acl
}

function Assert-PrivateAcl($Acl) {
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($Acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($raw.Owner -ne $UserSid -or $null -eq $raw.DiscretionaryAcl) { throw 'Unsafe private owner or DACL.' }
    foreach ($rule in $raw.DiscretionaryAcl) {
        if ($rule -isnot [Security.AccessControl.CommonAce] -or
            ($rule.AceQualifier -ne 'AccessDenied' -and
             ($rule.AceQualifier -ne 'AccessAllowed' -or $rule.SecurityIdentifier -ne $UserSid))) {
            throw 'Unsafe private permissions.'
        }
    }
}

# Keep the no-follow handle open through copying or loading code. Its sharing mode
# prevents the checked entry from being written, renamed or replaced in that interval.
function Open-PrivateCodeEntry($Path, [switch]$Directory, [long]$Limit = [long]::MaxValue) {
    Initialize-FileReader
    $handle = [RoomKit.UpdateEntry]::Open($Path, $Directory.IsPresent)
    if (!$handle) { throw 'Missing installed code.' }
    try {
        $stream = [IO.FileStream]::new($handle, [IO.FileAccess]::Read)
        $acl = if ($PSVersionTable.PSEdition -eq 'Core') {
            [IO.FileSystemAclExtensions]::GetAccessControl($stream)
        } else { $stream.GetAccessControl() }
        Assert-PrivateAcl $acl
        if (!$Directory -and ($stream.Length -eq 0 -or $stream.Length -gt $Limit)) { throw 'Invalid installed code size.' }
        return $stream
    } catch {
        if ($stream) { $stream.Dispose() } else { $handle.Dispose() }
        throw
    }
}

function New-PrivateDirectory($Path) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($UserSid)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $UserSid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    $dir = [IO.DirectoryInfo]::new($Path)
    if ($PSVersionTable.PSEdition -eq 'Core') {
        [IO.FileSystemAclExtensions]::Create($dir, $acl)
    } else {
        $dir.Create($acl)
    }
}

function Initialize-Support {
    if (-not [IO.Directory]::Exists($Support)) { New-PrivateDirectory $Support }
    Assert-PrivatePath $Support
}

function Read-PrivateText($Name, $Limit, $Folder = $Support) {
    $path = Join-Path $Folder $Name
    Initialize-FileReader
    $file = [RoomKit.UpdateEntry]::Open($path, $false)
    if (!$file) { return }
    try {
        Assert-PrivatePath $path
        $stream = [IO.FileStream]::new($file, [IO.FileAccess]::Read)
        try {
            if ($stream.Length -le $Limit) { return Read-LimitedText $stream $Limit }
        } finally { $stream.Dispose() }
    } finally { $file.Dispose() }
}

function New-PrivateFile($Path) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($UserSid)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($UserSid, 'FullControl', 'Allow'))
    if ($PSVersionTable.PSEdition -eq 'Core') {
        return [IO.FileSystemAclExtensions]::Create([IO.FileInfo]::new($Path), 'CreateNew', 'Write', 'None', 4096, 'None', $acl)
    }
    return [IO.FileStream]::new($Path, 'CreateNew', [Security.AccessControl.FileSystemRights]::Write, 'None', 4096, 'None', $acl)
}

function Write-PrivateText($Name, $Value, $Folder = $Support) {
    $path = Join-Path $Folder $Name
    $temp = Join-Path $Folder ([IO.Path]::GetRandomFileName())
    try {
        $bytes = $Utf8.GetBytes($Value)
        $stream = New-PrivateFile $temp
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        if ([IO.File]::Exists($path)) {
            Assert-PrivatePath $path
            [IO.File]::Replace($temp, $path, [NullString]::Value)
        } else {
            [IO.File]::Move($temp, $path)
        }
    } finally { [IO.File]::Delete($temp) }
}

function Get-UpdateLock {
    $path = Join-Path $Support '.update.lock'
    if (![IO.File]::Exists($path)) {
        try { return New-PrivateFile $path }
        catch [IO.IOException] { if (![IO.File]::Exists($path)) { throw } }
    }
    Assert-PrivatePath $path
    try { return [IO.File]::Open($path, 'Open', 'Write', 'None') }
    catch [IO.IOException] { return }
}

function Get-UpdateTime { return [DateTimeOffset]::UtcNow }

function Get-UpdateSettings {
    $settings = @{ Enabled = $false; Channel = 'stable'; Interval = 30 }
    $text = Read-PrivateText 'settings.json' 64
    if ($text -cmatch '\A\{"autoUpdate":(true|false)(?:,"channel":"(stable|develop)"(?:,"updateInterval":(0|[1-9][0-9]{0,5}))?)?\}\n?\z') {
        $settings.Enabled = $Matches[1] -ceq 'true'
        if ($Matches[2]) { $settings.Channel = $Matches[2] }
        if ([int]$Matches[3] -in 30,60,120,360,720) { $settings.Interval = [int]$Matches[3] }
    }
    return $settings
}

function Test-UpdateDue($Now, $Interval) {
    if ([IO.File]::Exists((Join-Path $Support 'first-check'))) { return $true }
    try { $stamp = Read-PrivateText 'last-check' 32 }
    catch { return $true } # An unreadable or unsafe stamp cannot postpone a check.
    [long]$last = 0
    if ($stamp -cnotmatch '\A[0-9]+\n?\z' -or -not [long]::TryParse($stamp, [ref]$last)) { return $true }
    $elapsed = $Now.ToUnixTimeSeconds() - $last
    return $elapsed -lt 0 -or $elapsed -ge ($Interval * 60 - 60)
}

function Initialize-ZipReader {
    if ('RoomKit.ReleaseZip' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;

namespace RoomKit {
    public static class ReleaseZip {
        // PKWARE APPNOTE 4.3.12-16. ZipArchive.Entries materializes every central header at once.
        // Walk the same directory first, using fixed-size reads and seeks over variable fields.
        public static void CheckDirectory(Stream stream) {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                int tailSize = (int)Math.Min(stream.Length, 22 + ushort.MaxValue);
                long tailStart = stream.Seek(-tailSize, SeekOrigin.End);
                byte[] tail = reader.ReadBytes(tailSize);
                int i = tail.Length - 22;
                while (i >= 0 && BitConverter.ToUInt32(tail, i) != 0x06054b50) i--;
                if (i < 0 || i + 22 + BitConverter.ToUInt16(tail, i + 20) != tail.Length)
                    throw new InvalidDataException("Invalid ZIP footer.");
                long end = tailStart + i;
                uint disk = BitConverter.ToUInt16(tail, i + 4), startDisk = BitConverter.ToUInt16(tail, i + 6);
                ulong onDisk = BitConverter.ToUInt16(tail, i + 8), count = BitConverter.ToUInt16(tail, i + 10);
                ulong size = BitConverter.ToUInt32(tail, i + 12), start = BitConverter.ToUInt32(tail, i + 16);
                if (end >= 20) {
                    stream.Position = end - 20;
                    if (reader.ReadUInt32() == 0x07064b50) {
                        if (reader.ReadUInt32() != 0) throw new InvalidDataException("Split ZIP.");
                        ulong footer = reader.ReadUInt64();
                        if (reader.ReadUInt32() != 1 || footer > (ulong)(end - 20) || (ulong)(end - 20) - footer < 56)
                            throw new InvalidDataException("Invalid ZIP64 locator.");
                        stream.Position = (long)footer;
                        if (reader.ReadUInt32() != 0x06064b50 || reader.ReadUInt64() != (ulong)(end - 20) - footer - 12)
                            throw new InvalidDataException("Invalid ZIP64 footer.");
                        stream.Position += 4;
                        disk = (uint)Zip64Value(disk, ushort.MaxValue, reader.ReadUInt32());
                        startDisk = (uint)Zip64Value(startDisk, ushort.MaxValue, reader.ReadUInt32());
                        onDisk = Zip64Value(onDisk, ushort.MaxValue, reader.ReadUInt64());
                        count = Zip64Value(count, ushort.MaxValue, reader.ReadUInt64());
                        size = Zip64Value(size, uint.MaxValue, reader.ReadUInt64());
                        start = Zip64Value(start, uint.MaxValue, reader.ReadUInt64());
                        end = (long)footer;
                    }
                }
                if (count > 10000) throw new InvalidDataException("Too many ZIP entries.");
                if (disk != 0 || startDisk != 0 || count != onDisk || start > (ulong)end || size != (ulong)end - start)
                    throw new InvalidDataException("Invalid ZIP directory.");
                stream.Position = (long)start;
                int seen = 0;
                while (stream.Position < end) {
                    uint signature = reader.ReadUInt32();
                    if (signature == 0x05054b50 && (ulong)seen == count) {
                        int signatureSize = reader.ReadUInt16();
                        if (signatureSize != end - stream.Position) throw new InvalidDataException("Invalid ZIP signature size.");
                        break;
                    }
                    if (++seen > 10000) throw new InvalidDataException("Too many ZIP entries.");
                    if (signature != 0x02014b50 || end - stream.Position < 42) throw new InvalidDataException("Invalid ZIP entry.");
                    stream.Position += 24;
                    int variableSize = reader.ReadUInt16() + reader.ReadUInt16() + reader.ReadUInt16();
                    stream.Position += 12 + variableSize;
                    if (stream.Position > end) throw new InvalidDataException("Invalid ZIP entry size.");
                }
                if ((ulong)seen != count) throw new InvalidDataException("Invalid ZIP entry count.");
                stream.Position = 0;
            }
        }

        private static ulong Zip64Value(ulong value, ulong sentinel, ulong extended) {
            if (value != sentinel && value != extended) throw new InvalidDataException("Inconsistent ZIP64 footer.");
            return extended;
        }
    }
}
'@
}

function Initialize-FileReader {
    if ('RoomKit.UpdateEntry' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoomKit {
    public static class UpdateEntry {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind,
            out AttributeTag info, uint size);
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

        public static SafeFileHandle Open(string path, bool directory) {
            // OPEN_REPARSE_POINT | BACKUP_SEMANTICS. Deny write/delete sharing until children have been read.
            SafeFileHandle handle = CreateFile(path, 0x80000000, FileShare.Read, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 2 || error == 3) return null;
                throw new Win32Exception(error);
            }
            try {
                AttributeTag info;
                if (!GetFileInformationByHandleEx(handle, 9, out info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
                FileAttributes kind = info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device);
                if (kind != (directory ? FileAttributes.Directory : 0)) throw new IOException("Unsafe file or directory.");
                return handle;
            } catch { handle.Dispose(); throw; }
        }

        public static string FinalPath(SafeFileHandle handle) {
            StringBuilder path = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity) throw new IOException("Cannot resolve the factory root.");
            return path.ToString();
        }
    }
}
'@
}

function Open-FactoryRoot($Root) {
    Initialize-FileReader
    try { $handle = [RoomKit.UpdateEntry]::Open($Root, $true) }
    catch [IO.IOException] { throw 'Supply -FactoryRoot with the physical directory. For a link or junction, use its target path.' }
    if (!$handle) { throw 'Supply -FactoryRoot with an existing directory.' }
    return $handle
}

function Initialize-JobReader {
    if ('RoomKit.JobStateFile' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RoomKit {
    public sealed class JobStateFile : IDisposable {
        [StructLayout(LayoutKind.Sequential)]
        private struct CountedName { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes {
            public int Length; public IntPtr Root, Name; public uint Flags; public IntPtr Security, QualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatus { public IntPtr Status, Information; }
        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes,
            out IoStatus io, IntPtr allocation, uint fileAttributes, FileShare share, uint disposition, uint options,
            IntPtr extendedAttributes, uint extendedLength);
        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind,
            out AttributeTag info, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct OplockInput { public ushort Version, Length; public uint Level, Flags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct OplockOutput {
            public ushort Version, Length; public uint Original, Current, Flags, Access; public ushort Share;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Request { public NativeOverlapped Io; public OplockInput Input; public OplockOutput Output; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref OplockInput input,
            uint inputSize, ref OplockOutput output, uint outputSize, out uint returned, ref NativeOverlapped io);

        private readonly SafeFileHandle file;
        private readonly Request[] request = new Request[1];
        private GCHandle pin;
        private bool pending;
        public readonly ManualResetEvent Changed = new ManualResetEvent(false);
        public FileStream Stream { get; private set; }

        private JobStateFile(SafeFileHandle file) { this.file = file; }

        public static JobStateFile Open(SafeFileHandle jobs, string name) {
            ushort bytes = checked((ushort)(name.Length * 2));
            GCHandle buffer = GCHandle.Alloc(name, GCHandleType.Pinned);
            try {
                GCHandle counted = GCHandle.Alloc(new CountedName { Buffer = buffer.AddrOfPinnedObject(),
                    Length = bytes, MaximumLength = bytes }, GCHandleType.Pinned);
                try {
                    // OBJ_DONT_REPARSE covers both components relative to the pinned jobs directory.
                    ObjectAttributes attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                        Root = jobs.DangerousGetHandle(), Name = counted.AddrOfPinnedObject(), Flags = 0x1000 | 0x40 };
                    SafeFileHandle handle;
                    IoStatus io;
                    // FILE_OPEN_REQUIRING_OPLOCK | FILE_NON_DIRECTORY_FILE reserves the oplock at open.
                    int status = NtCreateFile(out handle, 0x80000000, ref attributes, out io, IntPtr.Zero, 0,
                        FileShare.Read | FileShare.Delete, 1, 0x10000 | 0x40, IntPtr.Zero, 0);
                    GC.KeepAlive(jobs);
                    if (status < 0) {
                        if (handle != null) handle.Dispose();
                        if (status == unchecked((int)0xc0000034)) return null; // state file does not exist
                        throw new Win32Exception((int)RtlNtStatusToDosError(status));
                    }
                    JobStateFile record = new JobStateFile(handle);
                    try { record.StartRead(); return record; }
                    catch { record.Dispose(); throw; }
                } finally { counted.Free(); }
            } finally { buffer.Free(); }
        }

        private void StartRead() {
            // FSCTL_REQUEST_OPLOCK, read and handle caching. A conflicting rename waits until close.
            // Issue this before any other I/O on the reserved handle.
            pin = GCHandle.Alloc(request, GCHandleType.Pinned);
            request[0].Input = new OplockInput { Version = 1, Length = (ushort)Marshal.SizeOf(typeof(OplockInput)),
                Level = 3, Flags = 1 };
            // Use the event only for this request, even if FileStream binds the handle to a completion port.
            request[0].Io.EventHandle = new IntPtr(Changed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1);
            uint returned;
            bool completed = DeviceIoControl(file, 0x90240, ref request[0].Input, request[0].Input.Length,
                ref request[0].Output, (uint)Marshal.SizeOf(typeof(OplockOutput)), out returned, ref request[0].Io);
            int error = Marshal.GetLastWin32Error();
            if (completed || error != 997) throw new IOException("Cannot reserve the job state for reading.", new Win32Exception(error));
            pending = true;
            AttributeTag info;
            if (!GetFileInformationByHandleEx(file, 9, out info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Unsafe job state file.");
            Stream = new FileStream(file, FileAccess.Read, 4096, true);
        }

        public void Dispose() {
            if (Stream != null) Stream.Dispose();
            file.Dispose();
            // Closing cancels an outstanding request and acknowledges a break. Keep its storage until completion.
            if (pending) Changed.WaitOne();
            if (pin.IsAllocated) pin.Free();
            Changed.Dispose();
        }
    }
}
'@
}

function Read-JobState($Record) {
    $json = Read-LimitedText $Record.Stream 64KB -DetectEncoding
    if (!$Record.Changed.WaitOne(0)) { return $json }
}

function Test-JobRunning($Root) {
    $parents = [Collections.Generic.Stack[IDisposable]]::new()
    try {
        $rootHandle = Open-FactoryRoot $Root
        $parents.Push($rootHandle)
        $jobs = [RoomKit.UpdateEntry]::FinalPath($rootHandle)
        foreach ($part in $JobsFolder.Split('/')) {
            $jobs = [IO.Path]::Combine($jobs, $part)
            $handle = [RoomKit.UpdateEntry]::Open($jobs, $true)
            if (!$handle) { return $false }
            $parents.Push($handle)
        }
        # Open relative to jobs so the worker can write the job folder while its state is being read.
        Initialize-JobReader
        foreach ($path in [IO.Directory]::EnumerateDirectories($jobs)) {
            $name = [IO.Path]::Combine([IO.Path]::GetFileName($path), $JobFile)
            $record = [RoomKit.JobStateFile]::Open($parents.Peek(), $name)
            if (!$record) { continue }
            try { $json = Read-JobState $record } finally { $record.Dispose() }
            if (!$json -or !$json.TrimStart().StartsWith('{')) { return $true }
            $job = ConvertFrom-Json -InputObject $json
            if ($job.stage -isnot [string] -or !$job.stage -or $job.stage -cin 'spike','spec','build','review') { return $true }
        }
        return $false
    } catch { return $true }
    finally { while ($parents.Count) { $parents.Pop().Dispose() } }
}

# Only stable and develop.N are release channels. Compare numeric identifiers as digit strings
# so a valid SemVer component is never rounded or limited to a machine integer.
function Compare-Tags($Left, $Right) {
    $a = [regex]::Match($Left, $TagPattern)
    $b = [regex]::Match($Right, $TagPattern)
    for ($i = 1; $i -le 3; $i++) {
        $x = $a.Groups[$i].Value
        $y = $b.Groups[$i].Value
        if ($x.Length -ne $y.Length) { return $x.Length.CompareTo($y.Length) }
        $cmp = [string]::CompareOrdinal($x, $y)
        if ($cmp -ne 0) { return $cmp }
    }
    $preA = $a.Groups[4]
    $preB = $b.Groups[4]
    if (!$preA.Success -or !$preB.Success) { return $preB.Success.CompareTo($preA.Success) }
    $leftIds = $preA.Value.Split('.')
    $rightIds = $preB.Value.Split('.')
    for ($i = 0; $i -lt [Math]::Min($leftIds.Count, $rightIds.Count); $i++) {
        $x = $leftIds[$i]
        $y = $rightIds[$i]
        $numericX = $x -match '^[0-9]+$'
        $numericY = $y -match '^[0-9]+$'
        if ($numericX -ne $numericY) { return $numericY.CompareTo($numericX) }
        if ($numericX -and $x.Length -ne $y.Length) { return $x.Length.CompareTo($y.Length) }
        $cmp = [string]::CompareOrdinal($x, $y)
        if ($cmp -ne 0) { return $cmp }
    }
    return $leftIds.Count.CompareTo($rightIds.Count)
}

function Open-WebResponse([uri]$Uri, [int]$TimeoutMs) {
    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.AllowAutoRedirect = $false
    $request.UserAgent = $TaskPrefix
    $request.Timeout = $TimeoutMs
    $request.ReadWriteTimeout = $TimeoutMs
    return $request.GetResponse()
}

function Read-LimitedText($Stream, $Limit, [switch]$DetectEncoding) {
    $output = [IO.MemoryStream]::new()
    try {
        Copy-LimitedStream $Stream $output $Limit
        if ($DetectEncoding) {
            # Keep the BOM handling of ReadAllText for existing job records, after the byte cap.
            $output.Position = 0
            $reader = [IO.StreamReader]::new($output, $Utf8, $true)
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        return $Utf8.GetString($output.ToArray())
    } finally { $output.Dispose() }
}

function Get-ResponseText([uri]$Uri, $Limit, $TimeoutMs) {
    $response = Open-WebResponse $Uri $TimeoutMs
    try {
        if ([int]$response.StatusCode -ne 200 -or $response.ContentLength -gt $Limit) { throw 'Invalid response.' }
        return Read-LimitedText $response.GetResponseStream() $Limit
    } finally { $response.Dispose() }
}

function Get-ReleasePage($Page) {
    $json = Get-ResponseText "https://api.github.com/repos/$Repository/releases?per_page=100&page=$Page" 1MB 30000
    if (!$json.TrimStart().StartsWith('[')) { throw 'Expected a release list.' }
    ConvertFrom-Json -InputObject $json | ForEach-Object { $_ }
}

function Get-LatestTag($Channel) {
    $best = $null
    for ($page = 1; $page -le 10; $page++) {
        $releases = @(Get-ReleasePage $page | ForEach-Object { $_ })
        if ($releases.Count -gt 100) { throw 'Invalid release page size.' }
        foreach ($release in $releases) {
            $tag = $release.tag_name
            if ($release.draft -or $tag -isnot [string] -or $tag.Length -gt 129 -or $tag -cnotmatch $TagPattern) { continue }
            if ($tag.Contains('-')) {
                if ($Channel -cne 'develop' -or $tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+-develop\.[0-9]+\z') { continue }
            } elseif ($release.prerelease) { continue }
            if (!$best -or (Compare-Tags $tag $best) -gt 0) { $best = $tag }
        }
        if ($releases.Count -lt 100) { return $best }
    }
    throw 'Release list exceeds the scan limit.'
}

# All asset names come from the configured repository and a validated tag. Follow only HTTPS
# redirects to GitHub's release asset hosts. Enforce the cap even without Content-Length.
function Save-ReleaseAsset([uri]$Uri, $Path, [long]$Limit) {
    $repositoryPath = $Uri.AbsolutePath
    for ($redirect = 0; $redirect -lt 6; $redirect++) {
        if ($Uri.Scheme -ne 'https' -or $Uri.Port -ne 443 -or $Uri.UserInfo -or
            ($Uri.Host -eq 'github.com' -and $Uri.AbsolutePath -cne $repositoryPath) -or
            $Uri.Host -notin 'github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') {
            throw [IO.InvalidDataException]::new('Unexpected release URL.')
        }
        $response = Open-WebResponse $Uri 30000
        try {
            if ([int]$response.StatusCode -in 301,302,303,307,308) {
                $next = $null
                if (!$response.Headers['Location'] -or ![uri]::TryCreate($Uri, $response.Headers['Location'], [ref]$next)) {
                    throw [IO.InvalidDataException]::new('Invalid release redirect.')
                }
                $Uri = $next
                continue
            }
            if ([int]$response.StatusCode -ne 200) { throw 'Download failed.' }
            if ($response.ContentLength -gt $Limit) { throw [IO.InvalidDataException]::new('Download exceeds limit.') }
            $output = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
            try { Copy-LimitedStream $response.GetResponseStream() $output $Limit }
            finally { $output.Dispose() }
            return
        } finally { $response.Dispose() }
    }
    throw [IO.InvalidDataException]::new('Too many release redirects.')
}

function Copy-LimitedStream($Source, $Destination, [long]$Limit) {
    $buffer = [byte[]]::new(65536)
    [long]$count = 0
    while (($read = $Source.Read($buffer, 0, [int][Math]::Min($buffer.Length, $Limit - $count + 1))) -gt 0) {
        $count += $read
        if ($count -gt $Limit) { throw [IO.InvalidDataException]::new('Content exceeds limit.') }
        $Destination.Write($buffer, 0, $read)
    }
}

function Expand-VerifiedZip($Zip, $Manifest, $Name, $Destination) {
    if ([IO.FileInfo]::new($Manifest).Length -gt 4KB -or [IO.FileInfo]::new($Zip).Length -gt 200MB) {
        throw 'Release exceeds download limit.'
    }
    $pattern = '\A(\S+)[ \t]+\*?' + [regex]::Escape($Name) + '\z'
    $digests = @([IO.File]::ReadAllLines($Manifest) | ForEach-Object {
        if ($_ -cmatch $pattern) { $Matches[1] }
    })
    if ($digests.Count -ne 1 -or $digests[0] -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'ZIP digest refused.' }
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Zip)
    try {
        $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
        if ($actual -ne $digests[0]) { throw 'ZIP digest refused.' }
        Initialize-ZipReader
        [RoomKit.ReleaseZip]::CheckDirectory($stream)
        Add-Type -AssemblyName System.IO.Compression
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        foreach ($entry in $archive.Entries) {
            $nameInZip = $entry.FullName
            $directory = $nameInZip.EndsWith('/')
            if ($directory) { $nameInZip = $nameInZip.Substring(0, $nameInZip.Length - 1) }
            foreach ($part in $nameInZip.Split('/')) {
                if (!$part -or $part -match '[<>:"\\|?*\x00-\x1f]|[ .]$|^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9\u00b9\u00b2\u00b3])(?:\.|$)') {
                    throw 'Unsafe ZIP name.'
                }
            }
            $type = ($entry.ExternalAttributes -shr 16) -band 0xf000
            if (($entry.ExternalAttributes -band 0x400) -or $type -notin 0,0x4000,0x8000 -or
                ($type -eq 0x4000 -and !$directory) -or ($directory -and $entry.Length -ne 0) -or
                !$seen.Add($nameInZip)) { throw 'Linked or conflicting ZIP entry.' }
            $total += $entry.Length
            if ($total -gt 512MB) { throw 'ZIP exceeds expanded limit.' }
        }
        foreach ($required in $ServerName,'updater.ps1','launch-agent.ps1','wwwroot/index.html','wwwroot/app.css') {
            if (!$archive.GetEntry($required) -or $archive.GetEntry($required).Length -eq 0) { throw 'Incomplete release.' }
        }
        if ($archive.GetEntry('updater.ps1').Length -gt $UpdaterLimit) { throw 'Release updater exceeds limit.' }
        New-PrivateDirectory $Destination
        foreach ($entry in $archive.Entries) {
            $path = [IO.Path]::Combine($Destination, $entry.FullName)
            if ($entry.FullName.EndsWith('/')) {
                [void][IO.Directory]::CreateDirectory($path)
                continue
            }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $out = New-PrivateFile $path
            try {
                $source = $entry.Open()
                try { Copy-LimitedStream $source $out $entry.Length } finally { $source.Dispose() }
                if ($out.Length -ne $entry.Length) { throw 'Incomplete ZIP entry.' }
            } finally { $out.Dispose() }
        }
    } finally {
        if ($archive) { $archive.Dispose() }
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Get-ReleaseFiles($Tag, $Work) {
    if ($Tag.Length -gt 129 -or $Tag -cnotmatch $TagPattern) { throw 'Invalid release tag.' }
    $name = "$ZipPrefix-$($Tag.Substring(1))-win-x64.zip"
    $base = "https://github.com/$Repository/releases/download/$Tag"
    $manifest = Join-Path $Work 'SHA256SUMS'
    $zip = Join-Path $Work $name
    Save-ReleaseAsset "$base/SHA256SUMS" $manifest 4KB
    Save-ReleaseAsset "$base/$name" $zip 200MB
    try { Expand-VerifiedZip $zip $manifest $name (Join-Path $Work 'new') }
    catch { throw [IO.InvalidDataException]::new('Release verification failed.', $_.Exception) }
}

function Get-RoomTask($Name) {
    Get-ScheduledTask -TaskPath '\' -TaskName $Name -ErrorAction SilentlyContinue
}

function Get-TaskEngineId {
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $task = $service.GetFolder('\').GetTask($ServerTask)
    $instances = $task.GetInstances(0)
    if ($instances.Count -ne 1) { return }
    return [int]$instances.Item(1).EnginePID
}

function Get-ServerProcessId {
    $engine = Get-TaskEngineId
    if ($engine -le 0) { return }
    $exe = Join-Path (Join-Path $Support 'server') $ServerName
    $tree = @(Get-CimInstance Win32_Process -Filter "ProcessId=$engine OR ParentProcessId=$engine")
    # The engine can be the action itself or a scheduler host with the PowerShell action as its child.
    $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    foreach ($child in @($tree | Where-Object { $_.ParentProcessId -eq $engine -and $_.ExecutablePath -eq $shell })) {
        $tree += @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$([int]$child.ProcessId)")
    }
    $processes = @($tree | Where-Object { $_.ExecutablePath -eq $exe })
    if ($processes.Count -eq 1) { return [int]$processes[0].ProcessId }
}

function Get-ListenerPid {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalAddress '127.0.0.1' -LocalPort $AppPort -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 1) { return [int]$listeners[0].OwningProcess }
}

function Get-HealthVersion {
    $json = Get-ResponseText "http://127.0.0.1:$AppPort/health" 4KB 2000
    if ($json.TrimStart().StartsWith('{')) {
        $answer = ConvertFrom-Json -InputObject $json
        if ($answer.PSObject.Properties.Name -ccontains 'version' -and $answer.version -is [string]) { return $answer.version }
    }
}

function Wait-RoomPoll { Start-Sleep -Milliseconds 250 }

function Wait-ServerHealth($Version) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $serverId = Get-ServerProcessId
            if ($serverId -gt 0 -and (Get-ListenerPid) -eq $serverId -and (Get-HealthVersion) -ceq $Version -and
                (Get-ListenerPid) -eq $serverId -and (Get-ServerProcessId) -eq $serverId) { return $true }
        } catch { } # The task or its listener can still be starting.
        Wait-RoomPoll
    }
    return $false
}

function Stop-RoomServer {
    $task = Get-RoomTask $ServerTask
    if (!$task) { return }
    $serverId = Get-ServerProcessId
    $process = if ($serverId) { Get-Process -Id $serverId -ErrorAction SilentlyContinue }
    try {
        if ($process) { [void]$process.Handle } # Keep a handle to this instance across the stop.
        Stop-ScheduledTask -InputObject $task
        if ($process -and !$process.WaitForExit(15000)) {
            # Some scheduler hosts stop the PowerShell action before its child. Use the held process handle.
            try {
                if ($process.MainModule.FileName -ine [IO.Path]::Combine($Support, 'server', $ServerName)) {
                    throw 'Unexpected server process.'
                }
                $process.Kill()
            } catch { if (!$process.HasExited) { throw } }
            if (!$process.WaitForExit(15000)) { throw 'Server did not exit.' }
        }
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ((Get-RoomTask $ServerTask).State -notin 'Running','Queued' -and !(Get-ServerProcessId)) { return }
            Wait-RoomPoll
        }
        throw 'Server task did not stop.'
    } finally { if ($process) { $process.Dispose() } }
}

function Start-RoomServer { Start-ScheduledTask -TaskPath '\' -TaskName $ServerTask }

function New-ReleaseWork {
    $path = Join-Path $Support ('stage-' + [guid]::NewGuid().ToString('N'))
    New-PrivateDirectory $path
    return $path
}

function Remove-CodeDirectory($Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($full) -ne [IO.Path]::GetFullPath($Support) -or
        [IO.Path]::GetFileName($full) -notmatch '\A(server|stage-[0-9a-f]{32})\z') { throw 'Unexpected code directory.' }
    if ([IO.Directory]::Exists($full)) {
        Assert-PrivatePath $full
        [IO.Directory]::Delete($full, $true)
    }
}

# The updater and server live in one directory. Each rename changes both, including on rollback.
# On an incomplete recovery leave the previous directory on disk for the operator.
function Move-ReleaseDirectory($Source, $Destination) { [IO.Directory]::Move($Source, $Destination) }

function Save-RecoveryUpdater($CodeFolder) {
    $path = Join-Path $Support 'recovery-updater.ps1'
    $temp = Join-Path $Support ([IO.Path]::GetRandomFileName())
    $folder = Open-PrivateCodeEntry $CodeFolder -Directory
    try {
        $source = Open-PrivateCodeEntry (Join-Path $CodeFolder 'updater.ps1') -Limit $UpdaterLimit
        try {
            $out = New-PrivateFile $temp
            try { Copy-LimitedStream $source $out $UpdaterLimit; $out.Flush($true) }
            finally { $out.Dispose() }
        } finally { $source.Dispose() }
        if ([IO.File]::Exists($path)) {
            (Open-PrivateCodeEntry $path -Limit $UpdaterLimit).Dispose()
            [IO.File]::Replace($temp, $path, [NullString]::Value)
        } else { [IO.File]::Move($temp, $path) }
    } finally { $folder.Dispose(); [IO.File]::Delete($temp) }
}

function Restore-RoomRelease($Work, $Previous, $MovedNew, $MovedOld, $RestoreTasks) {
    $server = Join-Path $Support 'server'
    if ($MovedNew) {
        Stop-RoomServer
        Move-ReleaseDirectory $server (Join-Path $Work 'new')
    }
    if ($MovedOld) { Move-ReleaseDirectory (Join-Path $Work 'previous') $server }
    if ($RestoreTasks) { & $RestoreTasks }
    if ($Previous) {
        Start-RoomServer
        if (!(Wait-ServerHealth $Previous.Substring(1))) { throw 'Previous server did not answer.' }
    }
}

function Switch-RoomRelease($Work, $Tag, $Previous, $Register, $RestoreTasks, [switch]$RecordResult) {
    $server = Join-Path $Support 'server'
    $backup = Join-Path $Work 'previous'
    $staged = Join-Path $Work 'new'
    $reason = 'swap'
    $movedOld = $false
    $movedNew = $false
    $registrationAttempted = $false
    try {
        if ([IO.Directory]::Exists($server)) {
            Save-RecoveryUpdater $server
        } elseif (![IO.File]::Exists((Join-Path $Support 'recovery-updater.ps1'))) {
            Save-RecoveryUpdater $staged
        }
        if ($RecordResult) {
            Write-PrivateText 'pending-swap' ($Previous + [char]10 + $Tag + [char]10 + (Get-RunStamp)) $Work
        }
    } catch { return $reason } # The running installation has not been touched.
    try {
        Stop-RoomServer
        if ([IO.Directory]::Exists($server)) {
            Move-ReleaseDirectory $server $backup
            $movedOld = $true
        }
        Move-ReleaseDirectory $staged $server
        $movedNew = $true
        $reason = 'bootstrap_failed'
        if ($Register) {
            $registrationAttempted = $true
            & $Register
        }
        Start-RoomServer
        $reason = 'no_answer'
        if (!(Wait-ServerHealth $Tag.Substring(1))) { throw 'New server did not answer.' }
        $reason = 'swap'
        if ($RecordResult) { Save-UpdateResult $Work $Tag $Previous }
        Write-PrivateText 'installed-release' ($Tag + [char]10)
        return
    } catch {
        try {
            if (!$registrationAttempted) { $RestoreTasks = $null }
            Restore-RoomRelease $Work $Previous $movedNew $movedOld $RestoreTasks
        } catch { $reason = 'rollback_failed' }
        return $reason
    }
}

function Get-RunStamp {
    return (Get-UpdateTime).UtcDateTime.ToString('yyyyMMddTHHmmssZ', [cultureinfo]::InvariantCulture)
}

function Save-UpdateResult($Work, $Tag, $Previous, $Reason, $Run = (Get-RunStamp)) {
    $record = @{ run = $Run; version = $Tag.Substring(1) }
    if ($Reason) {
        $record.result = 'update_failed'
        $record.reason = $Reason
    } else {
        $record.result = 'updated'
        $record.previous = $Previous.Substring(1)
    }
    Write-PrivateText 'pending-result.json' (($record | ConvertTo-Json -Compress) + [char]10) $Work
}

function Read-StagedRecord($Text) {
    # These private records are written by Save-UpdateResult. Round-trip its compact encoding
    # so a duplicate property or a top-level array cannot be flattened by Windows PowerShell.
    if (!$Text -or !$Text.StartsWith('{')) { throw 'Invalid staged update result.' }
    $record = ConvertFrom-Json -InputObject $Text
    $detail = if ($record.result -ceq 'updated') { 'previous' } else { 'reason' }
    $fields = @($record.PSObject.Properties)
    if ($fields.Count -ne 4 -or $Text -cne (($record | ConvertTo-Json -Compress) + [char]10)) {
        throw 'Invalid staged update result.'
    }
    foreach ($field in $fields) {
        if ($field.Name -cnotin 'run','result','version',$detail -or $field.Value -isnot [string]) {
            throw 'Invalid staged update result.'
        }
    }
    if ($record.run -cnotmatch '\A[0-9]{8}T[0-9]{6}Z\z' -or $record.version.Length -gt 128 -or
        ('v' + $record.version) -cnotmatch $TagPattern) { throw 'Invalid staged update result.' }
    if ($record.result -ceq 'updated') {
        if ($record.previous.Length -gt 128 -or ('v' + $record.previous) -cnotmatch $TagPattern -or
            (Compare-Tags ('v' + $record.version) ('v' + $record.previous)) -le 0) { throw 'Invalid staged update result.' }
    } elseif ($record.result -cne 'update_failed' -or $record.reason -cnotin 'check','swap','bootstrap_failed','no_answer','rollback_failed') {
        throw 'Invalid staged update result.'
    }
    return $record
}

# A stopped process can leave either rename of a rollback complete. Observe each
# directory independently, including when the previous code is already back in place.
function Restore-StagedRelease($Work, $Previous) {
    $server = [IO.Directory]::Exists((Join-Path $Support 'server'))
    $backup = [IO.Directory]::Exists((Join-Path $Work 'previous'))
    if ($backup) { Restore-RoomRelease $Work $Previous $server $true }
    elseif (!$server) { throw 'Previous release is missing.' }
    elseif (!(Get-ServerProcessId) -or !(Wait-ServerHealth $Previous.Substring(1))) {
        Stop-RoomServer
        Restore-RoomRelease $Work $Previous $false $false
    }
}

# The pending record and the previous code survive a sharing error on the public result.
# Replaying the same bytes preserves the run identifier used for notification deduplication.
function Publish-StagedResult($Work) {
    $text = Read-PrivateText 'pending-result.json' 4KB $Work
    $record = Read-StagedRecord $text
    $reason = $record.reason
    if ($record.result -ceq 'updated') {
        $target = 'v' + $record.version
        $previous = 'v' + $record.previous
        $installed = (Read-PrivateText 'installed-release' 200).Trim()
        if ($installed -cne $target) {
            # Interrupted after confirming health and staging the record, before committing the marker.
            if ($installed -cne $previous) { throw 'Unexpected installed release during recovery.' }
            if (Wait-ServerHealth $record.version) {
                Write-PrivateText 'installed-release' ($target + [char]10)
            } else {
                $reason = 'no_answer'
                try { Restore-StagedRelease $Work $previous } catch { $reason = 'rollback_failed' }
                Save-UpdateResult $Work $target $previous $reason $record.run
                $text = Read-PrivateText 'pending-result.json' 4KB $Work
            }
        }
    }
    Write-PrivateText 'update-result.json' $text
    [IO.File]::Delete((Join-Path $Work 'pending-swap'))
    [IO.File]::Delete((Join-Path $Work 'pending-result.json'))
    return $reason
}

function Recover-StagedSwap($Work) {
    $intent = (Read-PrivateText 'pending-swap' 512 $Work).Split([char]10)
    if ($intent.Count -ne 3 -or $intent[0].Length -gt 129 -or $intent[0] -cnotmatch $TagPattern -or
        $intent[1].Length -gt 129 -or $intent[1] -cnotmatch $TagPattern -or (Compare-Tags $intent[1] $intent[0]) -le 0 -or
        $intent[2] -cnotmatch '\A[0-9]{8}T[0-9]{6}Z\z' -or (Read-PrivateText 'installed-release' 200).Trim() -cne $intent[0]) {
        throw 'Invalid staged swap.'
    }
    $previous = $intent[0]
    $reason = 'swap'
    try { Restore-StagedRelease $Work $previous } catch { $reason = 'rollback_failed' }
    Save-UpdateResult $Work $intent[1] $previous $reason $intent[2]
}

function Get-PendingStages {
    foreach ($stage in (Get-ChildItem -LiteralPath $Support -Force -Directory -Filter 'stage-*')) {
        if ($stage.Name -cnotmatch '\Astage-[0-9a-f]{32}\z') { continue }
        $folder = Open-PrivateCodeEntry $stage.FullName -Directory
        try {
            if ([IO.File]::Exists((Join-Path $stage.FullName 'pending-swap')) -or
                [IO.File]::Exists((Join-Path $stage.FullName 'pending-result.json'))) { $stage.FullName }
        } finally { $folder.Dispose() }
    }
}

function Complete-StagedResults {
    foreach ($work in (Get-PendingStages)) {
        if (![IO.File]::Exists((Join-Path $work 'pending-result.json'))) { Recover-StagedSwap $work }
        $reason = Publish-StagedResult $work
        if ($reason -cne 'rollback_failed') { Remove-CodeDirectory $work }
        if ($reason) { return $reason }
        return 'updated'
    }
}

function Invoke-RoomUpdate($Root) {
    if (![IO.Directory]::Exists($Support)) { return 'not_release' }
    Assert-PrivatePath $Support
    $lock = Get-UpdateLock
    if (!$lock) { return 'busy' }
    try { Invoke-LockedRoomUpdate $Root } finally { $lock.Dispose() }
}

function Invoke-LockedRoomUpdate($Root) {
    $settings = Get-UpdateSettings
    if (!$settings.Enabled) { return 'off' }
    $installed = ('' + (Read-PrivateText 'installed-release' 200)).TrimEnd([char]10)
    if ($installed.Length -gt 129 -or $installed -cnotmatch $TagPattern) { return 'not_release' }
    $now = Get-UpdateTime
    if (!(Test-UpdateDue $now $settings.Interval)) { return 'not_due' }
    if (Test-JobRunning $Root) { return 'job_running' }
    $recovered = Complete-StagedResults
    if ($recovered) { return $recovered }
    # Linked, locked or non-file stamps stay untouched. Release discovery is still due.
    try { Write-PrivateText 'last-check' ($now.ToUnixTimeSeconds().ToString() + [char]10) } catch { }
    [IO.File]::Delete((Join-Path $Support 'first-check'))
    $tag = Get-LatestTag $settings.Channel
    if (!$tag -or (Compare-Tags $tag $installed) -le 0) { return 'current' }

    $work = New-ReleaseWork
    $reason = $null
    try {
        try { Get-ReleaseFiles $tag $work }
        catch [IO.InvalidDataException] { $reason = 'check' }
        if (!$reason) {
            if (Test-JobRunning $Root) { return 'job_running' }
            $reason = Switch-RoomRelease $work $tag $installed -RecordResult
        }
        if ($reason) { Save-UpdateResult $work $tag $installed $reason }
        $reason = Publish-StagedResult $work
        if ($reason) { return $reason }
        return 'updated'
    } finally {
        if (![IO.File]::Exists((Join-Path $work 'pending-swap')) -and ![IO.File]::Exists((Join-Path $work 'pending-result.json')) -and
            ($reason -ne 'rollback_failed' -or ![IO.Directory]::Exists((Join-Path $work 'previous')))) {
            Remove-CodeDirectory $work
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($FactoryRootBase64) { $UpdateFactoryRoot = $Utf8.GetString([Convert]::FromBase64String($FactoryRootBase64)) }
    if (!$UpdateFactoryRoot) { throw 'Supply -FactoryRoot with the factory directory.' }
    Invoke-RoomUpdate $UpdateFactoryRoot
}
