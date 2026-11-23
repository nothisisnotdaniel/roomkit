#requires -Version 5.1
param([Alias('FactoryRoot')][string]$UpdateFactoryRoot)

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

function Read-PrivateText($Name, $Limit) {
    $path = Join-Path $Support $Name
    if (-not [IO.File]::Exists($path)) { return }
    Assert-PrivatePath $path
    $stream = [IO.File]::OpenRead($path)
    try {
        if ($stream.Length -gt $Limit) { return }
        $reader = [IO.StreamReader]::new($stream, $Utf8, $false)
        return $reader.ReadToEnd()
    } finally { $stream.Dispose() }
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

function Write-PrivateText($Name, $Value) {
    $path = Join-Path $Support $Name
    $temp = Join-Path $Support ([IO.Path]::GetRandomFileName())
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
    catch [Text.DecoderFallbackException] { return $true }
    [long]$last = 0
    if ($stamp -cnotmatch '\A[0-9]+\n?\z' -or -not [long]::TryParse($stamp, [ref]$last)) { return $true }
    $elapsed = $Now.ToUnixTimeSeconds() - $last
    return $elapsed -lt 0 -or $elapsed -ge ($Interval * 60 - 60)
}

function Test-JobRunning($Root) {
    if (-not [IO.Directory]::Exists($Root)) { return $true }
    $jobs = Join-Path $Root $JobsFolder
    if (-not [IO.Directory]::Exists($jobs)) { return $false }
    foreach ($dir in (Get-ChildItem -LiteralPath $jobs -Directory)) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $true }
        $path = Join-Path $dir.FullName $JobFile
        if (-not [IO.File]::Exists($path)) { continue }
        $file = [IO.FileInfo]::new($path)
        if ($file.Length -gt 64KB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $true }
        $job = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($job.stage -cin 'spike','spec','build','review') { return $true }
    }
    return $false
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

function Get-ReleasePage($Page) {
    Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 -MaximumRedirection 0 -Headers @{ 'User-Agent' = $TaskPrefix } -Uri (
        "https://api.github.com/repos/$Repository/releases?per_page=100&page=$Page")
}

function Get-LatestTag($Channel) {
    $best = $null
    for ($page = 1; ; $page++) {
        $releases = @(Get-ReleasePage $page | ForEach-Object { $_ })
        foreach ($release in $releases) {
            $tag = $release.tag_name
            if ($release.draft -or $tag.Length -gt 129 -or $tag -cnotmatch $TagPattern) { continue }
            if ($tag.Contains('-')) {
                if ($Channel -cne 'develop' -or $tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+-develop\.[0-9]+\z') { continue }
            } elseif ($release.prerelease) { continue }
            if (!$best -or (Compare-Tags $tag $best) -gt 0) { $best = $tag }
        }
        if ($releases.Count -lt 100) { return $best }
    }
}

# All asset names come from the configured repository and a validated tag. Follow only HTTPS
# redirects to GitHub's release asset hosts. Enforce the cap even without Content-Length.
function Save-ReleaseAsset([uri]$Uri, $Path, [long]$Limit) {
    for ($redirect = 0; $redirect -lt 6; $redirect++) {
        if ($Uri.Scheme -ne 'https' -or $Uri.Host -notin 'github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') {
            throw 'Unexpected release URL.'
        }
        $request = [Net.HttpWebRequest]::Create($Uri)
        $request.AllowAutoRedirect = $false
        $request.UserAgent = $TaskPrefix
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $response = $request.GetResponse()
        try {
            if ([int]$response.StatusCode -in 301,302,303,307,308) {
                $Uri = [uri]::new($Uri, $response.Headers['Location'])
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
    throw 'Too many release redirects.'
}

function Copy-LimitedStream($Source, $Destination, [long]$Limit) {
    $buffer = [byte[]]::new(65536)
    [long]$count = 0
    while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
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
    $answer = Invoke-RestMethod -UseBasicParsing -Uri "http://127.0.0.1:$AppPort/health" -TimeoutSec 2 -MaximumRedirection 0
    return $answer.version
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
function Switch-RoomRelease($Work, $Tag, $Previous, $Register, $RestoreTasks) {
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
        Write-PrivateText 'installed-release' ($Tag + [char]10)
        return
    } catch {
        try {
            if ($movedNew) {
                Stop-RoomServer
                [IO.Directory]::Move($server, $staged)
            }
            if ($movedOld) { [IO.Directory]::Move($backup, $server) }
            if ($registrationAttempted) { & $RestoreTasks }
            if ($Previous) {
                Start-RoomServer
                if (!(Wait-ServerHealth $Previous.Substring(1))) { throw 'Previous server did not answer.' }
            }
        } catch { $reason = 'rollback_failed' }
        return $reason
    }
}

function Save-UpdateResult($Tag, $Previous, $Reason) {
    $now = Get-UpdateTime
    $record = @{ run = $now.UtcDateTime.ToString('yyyyMMddTHHmmssZ', [cultureinfo]::InvariantCulture); version = $Tag.Substring(1) }
    if ($Reason) {
        $record.result = 'update_failed'
        $record.reason = $Reason
    } else {
        $record.result = 'updated'
        $record.previous = $Previous.Substring(1)
    }
    Write-PrivateText 'update-result.json' (($record | ConvertTo-Json -Compress) + [char]10)
}

function Invoke-RoomUpdate($Root) {
    if (![IO.Directory]::Exists($Support)) { return 'not_release' }
    Assert-PrivatePath $Support
    $lock = Get-UpdateLock
    if (!$lock) { return 'busy' }
    try {
        $settings = Get-UpdateSettings
        if (!$settings.Enabled) { return 'off' }
        $installed = ('' + (Read-PrivateText 'installed-release' 200)).TrimEnd([char]10)
        if ($installed -cnotmatch $TagPattern) { return 'not_release' }
        $now = Get-UpdateTime
        if (!(Test-UpdateDue $now $settings.Interval)) { return 'not_due' }
        if (Test-JobRunning $Root) { return 'job_running' }
        Write-PrivateText 'last-check' ($now.ToUnixTimeSeconds().ToString() + [char]10)
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
                $reason = Switch-RoomRelease $work $tag $installed
            }
            Save-UpdateResult $tag $installed $reason
            if ($reason) { return $reason }
            return 'updated'
        } finally {
            if ($reason -ne 'rollback_failed' -or ![IO.Directory]::Exists((Join-Path $work 'previous'))) {
                Remove-CodeDirectory $work
            }
        }
    } finally { $lock.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (!$UpdateFactoryRoot) { throw 'Supply -FactoryRoot with the factory directory.' }
    Invoke-RoomUpdate $UpdateFactoryRoot
}
