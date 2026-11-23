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

function Assert-PrivatePath($Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked private path.' }
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $acl = if ($PSVersionTable.PSEdition -eq 'Core') {
        [IO.FileSystemAclExtensions]::GetAccessControl($item, $sections)
    } else {
        $item.GetAccessControl($sections)
    }
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($raw.Owner -ne $UserSid -or $null -eq $raw.DiscretionaryAcl) { throw 'Unsafe private owner or DACL.' }
    foreach ($rule in $raw.DiscretionaryAcl) {
        if ($rule -isnot [Security.AccessControl.CommonAce] -or
            ($rule.AceQualifier -ne 'AccessDenied' -and
             ($rule.AceQualifier -ne 'AccessAllowed' -or $rule.SecurityIdentifier -ne $UserSid))) {
            throw 'Unsafe private permissions.'
        }
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

function Test-JobRunning($Root) {
    $parents = [Collections.Generic.Stack[IDisposable]]::new()
    try {
        Initialize-FileReader
        $rootHandle = [RoomKit.UpdateEntry]::Open($Root, $true)
        if (!$rootHandle) { return $true }
        $parents.Push($rootHandle)
        $jobs = [RoomKit.UpdateEntry]::FinalPath($rootHandle)
        foreach ($part in $JobsFolder.Split('/')) {
            $jobs = [IO.Path]::Combine($jobs, $part)
            $handle = [RoomKit.UpdateEntry]::Open($jobs, $true)
            if (!$handle) { return $false }
            $parents.Push($handle)
        }
        # Directory enumeration includes hidden jobs. Keep every directory handle until its children are checked.
        foreach ($path in [IO.Directory]::EnumerateDirectories($jobs)) {
            $folder = [RoomKit.UpdateEntry]::Open($path, $true)
            if (!$folder) { return $true }
            try {
                $file = [RoomKit.UpdateEntry]::Open([IO.Path]::Combine($path, $JobFile), $false)
                if (!$file) { continue }
                try {
                    $stream = [IO.FileStream]::new($file, [IO.FileAccess]::Read)
                    try { $json = Read-LimitedText $stream 64KB -DetectEncoding } finally { $stream.Dispose() }
                } finally { $file.Dispose() }
                if (!$json.TrimStart().StartsWith('{')) { return $true }
                $job = ConvertFrom-Json -InputObject $json
                if ($job.stage -isnot [string] -or !$job.stage -or $job.stage -cin 'spike','spec','build','review') { return $true }
            } finally { $folder.Dispose() }
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
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($actual -ne $digests[0]) { throw 'ZIP digest refused.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        if ($archive.Entries.Count -gt 10000) { throw 'Too many ZIP entries.' }
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
        foreach ($entry in $archive.Entries) {
            $path = [IO.Path]::Combine($Destination, $entry.FullName)
            if ($entry.FullName.EndsWith('/')) {
                [void][IO.Directory]::CreateDirectory($path)
                continue
            }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $out = [IO.File]::Open($path, 'CreateNew', 'Write', 'None')
            try {
                $source = $entry.Open()
                try { Copy-LimitedStream $source $out $entry.Length } finally { $source.Dispose() }
                if ($out.Length -ne $entry.Length) { throw 'Incomplete ZIP entry.' }
            } finally { $out.Dispose() }
        }
    } finally { $archive.Dispose() }
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

function Get-ServerProcessId {
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $task = $service.GetFolder('\').GetTask($ServerTask)
    $instances = $task.GetInstances(0)
    if ($instances.Count -ne 1) { return }
    $engine = [int]$instances.Item(1).EnginePID
    if ($engine -le 0) { return }
    $exe = Join-Path (Join-Path $Support 'server') $ServerName
    # Task Scheduler can host the Exec action in an engine or report the action itself.
    $processes = @(Get-CimInstance Win32_Process -Filter "ProcessId=$engine OR ParentProcessId=$engine" |
        Where-Object { $_.ExecutablePath -eq $exe })
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
        if ($process -and !$process.WaitForExit(15000)) { throw 'Server did not exit.' }
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
function Restore-RoomRelease($Work, $Previous, $MovedNew, $MovedOld, $RestoreTasks) {
    $server = Join-Path $Support 'server'
    if ($MovedNew) {
        Stop-RoomServer
        [IO.Directory]::Move($server, (Join-Path $Work 'new'))
    }
    if ($MovedOld) { [IO.Directory]::Move((Join-Path $Work 'previous'), $server) }
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
        if ([IO.Directory]::Exists($server)) { Assert-PrivatePath $server }
        Stop-RoomServer
        if ([IO.Directory]::Exists($server)) {
            [IO.Directory]::Move($server, $backup)
            $movedOld = $true
        }
        [IO.Directory]::Move($staged, $server)
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

function Save-UpdateResult($Work, $Tag, $Previous, $Reason) {
    $now = Get-UpdateTime
    $record = @{ run = $now.UtcDateTime.ToString('yyyyMMddTHHmmssZ', [cultureinfo]::InvariantCulture); version = $Tag.Substring(1) }
    if ($Reason) {
        $record.result = 'update_failed'
        $record.reason = $Reason
    } else {
        $record.result = 'updated'
        $record.previous = $Previous.Substring(1)
    }
    Write-PrivateText 'pending-result.json' (($record | ConvertTo-Json -Compress) + [char]10) $Work
}

# The pending record and the previous code survive a sharing error on the public result.
# Replaying the same bytes preserves the run identifier used for notification deduplication.
function Publish-StagedResult($Work) {
    $text = Read-PrivateText 'pending-result.json' 4KB $Work
    $record = ConvertFrom-Json -InputObject $text
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
                $failure = 'no_answer'
                try {
                    $backup = [IO.Directory]::Exists((Join-Path $Work 'previous'))
                    Restore-RoomRelease $Work $previous $backup $backup
                } catch { $failure = 'rollback_failed' }
                Save-UpdateResult $Work $target $previous $failure
                $text = Read-PrivateText 'pending-result.json' 4KB $Work
            }
        }
    }
    Write-PrivateText 'update-result.json' $text
    [IO.File]::Delete((Join-Path $Work 'pending-result.json'))
}

function Complete-StagedResults {
    foreach ($stage in (Get-ChildItem -LiteralPath $Support -Force -Directory -Filter 'stage-*')) {
        if ($stage.Name -cnotmatch '\Astage-[0-9a-f]{32}\z') { continue }
        Assert-PrivatePath $stage.FullName
        if ([IO.File]::Exists((Join-Path $stage.FullName 'pending-result.json'))) {
            Publish-StagedResult $stage.FullName
            $saved = ConvertFrom-Json -InputObject (Read-PrivateText 'update-result.json' 4KB)
            if ($saved.reason -cne 'rollback_failed') { Remove-CodeDirectory $stage.FullName }
        }
    }
}

function Invoke-RoomUpdate($Root) {
    if (![IO.Directory]::Exists($Support)) { return 'not_release' }
    Assert-PrivatePath $Support
    $lock = Get-UpdateLock
    if (!$lock) { return 'busy' }
    try {
        Complete-StagedResults
        $settings = Get-UpdateSettings
        if (!$settings.Enabled) { return 'off' }
        $installed = ('' + (Read-PrivateText 'installed-release' 200)).TrimEnd([char]10)
        if ($installed -cnotmatch $TagPattern) { return 'not_release' }
        $now = Get-UpdateTime
        if (!(Test-UpdateDue $now $settings.Interval)) { return 'not_due' }
        if (Test-JobRunning $Root) { return 'job_running' }
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
            Publish-StagedResult $work
            if ($reason) { return $reason }
            return 'updated'
        } finally {
            if (![IO.File]::Exists((Join-Path $work 'pending-result.json')) -and
                ($reason -ne 'rollback_failed' -or ![IO.Directory]::Exists((Join-Path $work 'previous')))) {
                Remove-CodeDirectory $work
            }
        }
    } finally { $lock.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($FactoryRootBase64) { $UpdateFactoryRoot = $Utf8.GetString([Convert]::FromBase64String($FactoryRootBase64)) }
    if (!$UpdateFactoryRoot) { throw 'Supply -FactoryRoot with the factory directory.' }
    Invoke-RoomUpdate $UpdateFactoryRoot
}
