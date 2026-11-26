# Each invocation receives its own temporary directory and a fixed clock from the test.
param(
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory, ParameterSetName='Run')][string]$Scripts,
    [Parameter(Mandatory, ParameterSetName='Run')][string]$Scenario,
    [Parameter(Mandatory, ParameterSetName='Run')][long]$Epoch,
    [Parameter(Mandatory, ParameterSetName='Cleanup')][switch]$DetachRootJunction
)
$ErrorActionPreference = 'Stop'
# Kept before the scripts are dot-sourced, so failure messages can pick this file's frames from the call stack.
$fixtureScript = $PSCommandPath

if ($DetachRootJunction) {
    $targetPath = Join-Path $Fixture 'scan root'
    $sentinel = Join-Path $targetPath 'junction-target.txt'
    $entries = [IO.Directory]::GetFileSystemEntries($Fixture, 'factory junction')
    if (!$entries.Length) {
        if ([IO.File]::Exists($sentinel)) { throw 'The fixture junction disappeared before disposal.' }
        return 'No fixture junction was created.'
    }
    $link = Get-Item -LiteralPath $entries[0] -Force
    $target = Get-Item -LiteralPath $targetPath -Force
    if ($link -isnot [IO.DirectoryInfo] -or $link.Parent.FullName -ine $Fixture -or
        $link.LinkType -cne 'Junction' -or !($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @($link.Target).Count -ne 1 -or [IO.Path]::GetFullPath(@($link.Target)[0]) -ine $targetPath -or
        $target -isnot [IO.DirectoryInfo] -or ($target.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        [IO.File]::ReadAllText($sentinel) -cne $Fixture) { throw 'Unrecognized fixture junction or target.' }

    [IO.Directory]::Delete($link.FullName, $false)
    if ([IO.Directory]::GetFileSystemEntries($Fixture, 'factory junction').Length -or
        [IO.File]::ReadAllText($sentinel) -cne $Fixture) { throw 'Fixture junction detachment failed.' }
    return 'Detached the fixture junction and verified its target sentinel.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$inputs = Join-Path $Fixture 'script copies'
[void][IO.Directory]::CreateDirectory($inputs)
foreach ($scriptFile in 'launch-agent.ps1','updater.ps1') {
    $text = [IO.File]::ReadAllText((Join-Path $Scripts $scriptFile))
    if ($Scenario -eq 'sample' -and $scriptFile -eq 'updater.ps1') {
        $block = @'
# roomkit:app-begin
$DataFolder = 'SampleRoom'
$TaskPrefix = 'SampleRoom'
$ServerName = 'sampleroom.exe'
$ZipPrefix = 'sampleroom'
$AppPort = 8201
$Repository = 'example/sampleroom'
$JobsFolder = 'work/jobs'
$JobFile = 'job.json'
# roomkit:app-end
'@
        $text = [regex]::Replace($text, '(?s)# roomkit:app-begin.*?# roomkit:app-end', { $block })
    }
    [IO.File]::WriteAllText((Join-Path $inputs $scriptFile), $text)
}
. (Join-Path $inputs 'launch-agent.ps1')

$Support = Join-Path $Fixture $DataFolder
$factory = Join-Path $Fixture ("Factory Root & team's [local] %TEMP% " + [char]0x2019)
if ($Scenario -in 'lifecycle','sample') {
    $Support = Join-Path (Join-Path $Fixture 'profile %TEMP%') $DataFolder
    $factory = Join-Path (Join-Path $Fixture 'selected directory') ([IO.Path]::GetFileName($factory))
}
if ($Scenario -in 'linked-jobs','linked-job','linked-state','root-junction') { $factory = Join-Path $Fixture 'scan root' }
[void][IO.Directory]::CreateDirectory($factory)
$clock = [DateTimeOffset]::FromUnixTimeSeconds($Epoch)
$tasks = @{}
$history = [Collections.Generic.List[string]]::new()
$requests = [Collections.Generic.List[string]]::new()
$archiveProblem = ''
$offered = 'v1.3.0'
$badListener = $false
$failRecovery = $false
$failStart = $false
$machine = @{ Running = $false; InterruptAt = ''; FailRestore = $false }
$actionTimeoutMs = 30000
$writePrivate = (Get-Item function:Write-PrivateText).ScriptBlock
function Write-PrivateText($Name, $Value, $Folder = $Support) {
    if ($machine.InterruptAt -ceq 'marker' -and $Name -ceq 'installed-release') { break interruptedUpdate }
    if ($Scenario -eq 'swap' -and $Name -eq 'installed-release' -and $Value.Trim() -eq 'v1.3.0') {
        throw 'Fixture could not commit the new marker.'
    }
    & $writePrivate $Name $Value $Folder
    if ($Name -ceq 'installed-release' -and $machine.AfterMarker) { & $machine.AfterMarker }
}

# The fixture lines of the call that failed, innermost first, skipping this helper and the Expect that called it.
function Get-FixtureLines {
    $lines = foreach ($frame in @(Get-PSCallStack | Select-Object -Skip 2)) {
        if ($frame.ScriptName -eq $fixtureScript) { $frame.ScriptLineNumber }
    }
    return 'line ' + (@($lines) -join ' < ')
}
function Expect($Actual, $Wanted) {
    if ([string]$Actual -cne [string]$Wanted) { throw "Expected [$Wanted], got [$Actual] at $(Get-FixtureLines)." }
}
# Runs one update and, on a mismatch, also reports the inputs of the due decision (unchanged by a not_due run).
function Expect-Update($Wanted) {
    $actual = Invoke-RoomUpdate $factory
    if ([string]$actual -ceq [string]$Wanted) { return }
    $inputs = foreach ($name in 'first-check','last-check','settings.json') {
        try { $text = [IO.File]::ReadAllText((Join-Path $Support $name)).Replace([string][char]10, '\n') }
        catch { $text = '<' + $_.Exception.GetBaseException().GetType().Name + '>' }
        "$name [$text]"
    }
    throw "Expected [$Wanted], got [$actual] at $(Get-FixtureLines) (clock $($clock.ToUnixTimeSeconds()); $($inputs -join '; '))"
}
function Expect-Refusal([scriptblock]$Operation, $Reason) {
    $message = ''
    try { & $Operation | Out-Null } catch { $message = $_.Exception.ToString() }
    if (!$message.Contains($Reason)) { throw "Expected refusal [$Reason], got [$message]." }
}
if ($Scenario -eq 'expect-line') {
    $message = ''
    try { $at = @(Get-PSCallStack)[0].ScriptLineNumber; Expect 'got' 'wanted' } catch { $message = $_.Exception.Message }
    if ($message -cne "Expected [wanted], got [got] at line $at.") { throw "Unexpected Expect message [$message]." }
    return
}
function With-RefusedEntry($Path, [scriptblock]$Check) {
    $saved = $Path + '.original'
    [IO.File]::Move($Path, $saved)
    try {
        switch ($Scenario) {
            'entry-acl' {
                $acl = [Security.AccessControl.FileSecurity]::new()
                $acl.SetOwner($UserSid)
                $acl.SetAccessRuleProtection($true, $false)
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($UserSid, 'FullControl', 'Allow'))
                $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users, 'Write', 'Allow'))
                $out = [IO.FileStream]::new($Path, 'CreateNew', [Security.AccessControl.FileSystemRights]::Write, 'None', 4096, 'None', $acl)
                try {
                    $bytes = [IO.File]::ReadAllBytes($saved)
                    $out.Write($bytes, 0, $bytes.Length)
                } finally { $out.Dispose() }
            }
            'entry-link' { [IO.File]::Move((Join-Path $Fixture 'entry-link'), $Path) }
            'entry-directory' { New-PrivateDirectory $Path }
            'entry-size' {
                $out = New-PrivateFile $Path
                try { $out.SetLength($UpdaterLimit + 1) } finally { $out.Dispose() }
            }
        }
        & $Check
    } finally {
        if ($Scenario -eq 'entry-directory') { [IO.Directory]::Delete($Path) }
        elseif ($Scenario -eq 'entry-link') { [IO.File]::Move($Path, (Join-Path $Fixture 'entry-link')) }
        else { [IO.File]::Delete($Path) }
        [IO.File]::Move($saved, $Path)
    }
}
function Current-Code { [IO.File]::ReadAllText((Join-Path (Join-Path $Support 'server') $ServerName)) }
function Get-UpdateTime { return $clock }
function Wait-RoomPoll { }
function Move-ReleaseDirectory($Source, $Destination) {
    if ($machine.FailRestore -and [IO.Path]::GetFileName($Source) -ceq 'previous') { throw 'Fixture restore failed.' }
    if ($machine.BeforeBackup -and [IO.Path]::GetFileName($Destination) -ceq 'previous') {
        $beforeBackup = $machine.BeforeBackup
        $machine.BeforeBackup = $null
        & $beforeBackup $Source
    }
    [IO.Directory]::Move($Source, $Destination)
    # Exit this invocation at a durable boundary without entering its ordinary exception handler.
    if ([IO.Path]::GetFileName($Destination) -ceq $machine.InterruptAt) { break interruptedUpdate }
}
function Get-FixtureReleases {
    if ($Scenario -in 'stable','develop') {
        # Build metadata makes this 129-character tag ineligible on both channels.
        $unsupported = 'v9.0.0-develop.'.PadRight(127, '7') + '+1'
        @{ tag_name = $unsupported; draft = $false; prerelease = $true }
    }
    foreach ($name in @('v1.2.0', $offered, 'v1.4.0-develop.3', 'v1.4.0-rc.1', 'v9.0.0-rc.1-develop.3', 'v1.4.0-develop.12')) {
        @{ tag_name = $name; draft = $false; prerelease = $name.Contains('-') }
    }
    @{ tag_name = 'v22.0.0'; draft = $true; prerelease = $false }
}

# These fakes consume the actual action, principal, trigger and settings passed by the installer.
function New-ScheduledTaskAction($Execute, $Argument, $WorkingDirectory) { return $PSBoundParameters }
function New-ScheduledTaskPrincipal($UserId, $LogonType, $RunLevel) { return $PSBoundParameters }
function New-ScheduledTaskSettingsSet($MultipleInstances, $ExecutionTimeLimit, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries) { return $PSBoundParameters }
function New-ScheduledTaskTrigger([switch]$AtLogOn, $User, [switch]$Once, $At, $RepetitionInterval) { return $PSBoundParameters }
function Register-ScheduledTask($TaskPath, $TaskName, $Action, $Trigger, $Principal, $Settings, [switch]$Force, $Xml) {
    $tasks[$TaskName] = @{ State = 'Ready'; Name = $TaskName; Action = $Action; Trigger = $Trigger; Principal = $Principal }
}
function Get-ScheduledTask($TaskPath, $TaskName, $ErrorAction) { return $tasks[$TaskName] }
function Export-ScheduledTask($TaskPath, $TaskName) { return '<fixture/>' }
function Unregister-ScheduledTask($TaskPath, $TaskName, [switch]$Confirm) { $tasks.Remove($TaskName) }
function Start-ScheduledTask($TaskPath, $TaskName) {
    $version = Current-Code
    $history.Add("start $version")
    if (($failStart -and $version -ceq '1.3.0') -or ($failRecovery -and $version -ceq '1.2.0')) { throw 'Fixture start failure.' }
    if ($Scenario -eq 'live-server-install') {
        Expect $TaskName $ServerTask
        $machine.ServerAction = Start-FixtureAction $TaskName -HoldServer
        Wait-FixtureSignal $machine.ServerAction 'server ready'
    }
    $machine.Running = $true
    $tasks[$TaskName].State = 'Running'
}
function Stop-ScheduledTask($InputObject) {
    if ($InputObject.Name -ceq $UpdaterTask) {
        $history.Add('stop updater')
        $InputObject.State = 'Ready'
        return
    }
    $history.Add("stop $(Current-Code)")
    if ($machine.ServerAction) { Stop-FixtureServer }
    if ($Scenario -ne 'lifecycle') { $machine.Running = $false }
    $InputObject.State = 'Ready'
}
function Get-Process($Id, $ErrorAction) {
    Expect $Id 975
    $fake = [pscustomobject]@{ Handle = 1; MainModule = @{ FileName = [IO.Path]::Combine($Support, 'server', $ServerName) } }
    Add-Member -InputObject $fake -MemberType ScriptMethod -Name Kill -Value { $history.Add('terminated server child'); $machine.Running = $false }
    Add-Member -InputObject $fake -MemberType ScriptMethod -Name WaitForExit -Value { $history.Add("exit $(Current-Code)"); return !$machine.Running }
    Add-Member -InputObject $fake -MemberType ScriptMethod -Name Dispose -Value { }
    return $fake
}
function Get-TaskEngineId { if ($machine.Running) { return 812 } }
function Get-CimInstance($ClassName, $Filter) {
    $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $exe = Join-Path (Join-Path $Support 'server') $ServerName
    if ($Filter -ceq 'ProcessId=812 OR ParentProcessId=812') {
        if ($Scenario -eq 'lifecycle') {
            return @{ ProcessId = 975; ParentProcessId = 812; ExecutablePath = $exe }
        }
        return @{ ProcessId = 814; ParentProcessId = 812; ExecutablePath = $shell }
    }
    Expect $Filter 'ParentProcessId=814'
    return @{ ProcessId = 975; ParentProcessId = 814; ExecutablePath = $exe }
}
function Get-ListenerPid { if ($badListener -and (Current-Code) -ceq '1.3.0') { return 501 }; return 975 }

function Add-ZipText($Zip, $Name, $Value) {
    $entry = $Zip.CreateEntry($Name)
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write($Value) } finally { $writer.Dispose() }
    return $entry
}

function FixtureUpdater($Tag) {
    if ($Scenario -eq 'interrupted-candidate' -and $Tag -ceq 'v1.3.0') { return "throw 'Fixture candidate failed to start.'" }
    if ($Scenario -notin 'interrupted-swap','interrupted-candidate','interrupted-rollback','restore-result-retry','pending-active','concurrent-install','job-replace','root-junction') {
        return "updater $Tag"
    }
    # The registered action runs the packaged updater. Supply the same private data and OS/network
    # fakes on restart that the initial invocation received in the fixture process.
    $entry = 'if ($MyInvocation.InvocationName -ne ''.'') {'
    $hooks = @'
$Support = $fixtureSupport
foreach ($fakeName in $fixtureOverrides.Keys) { Set-Item -LiteralPath ("function:" + $fakeName) -Value $fixtureOverrides[$fakeName] }
$history.Add("entry $PSCommandPath")
'@
    return [IO.File]::ReadAllText((Join-Path $inputs 'updater.ps1')).Replace($entry, $hooks + [char]10 + $entry) + ([char]10 + "# $Tag")
}

function Use-Zip64Footer($Path) {
    $stream = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $offset = $stream.Length - 22
        $stream.Position = $offset
        $footer = [byte[]]::new(22)
        Expect ($stream.Read($footer, 0, 22)) 22
        $stream.Position = $offset
        $writer.Write([uint32]0x06064b50)
        $writer.Write([uint64]44)
        $writer.Write([uint16]45)
        $writer.Write([uint16]45)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)
        foreach ($at in 8,10) {
            $writer.Write([uint64][BitConverter]::ToUInt16($footer, $at))
            [BitConverter]::GetBytes([uint16]::MaxValue).CopyTo($footer, $at)
        }
        foreach ($at in 12,16) {
            $writer.Write([uint64][BitConverter]::ToUInt32($footer, $at))
            [BitConverter]::GetBytes([uint32]::MaxValue).CopyTo($footer, $at)
        }
        $writer.Write([uint32]0x07064b50)
        $writer.Write([uint32]0)
        $writer.Write([uint64]$offset)
        $writer.Write([uint32]1)
        $writer.Write($footer)
    } finally { $writer.Dispose() }
}

function New-FixtureRelease($Tag, $Name) {
    $zipPath = Join-Path $Fixture 'fixture.zip'
    [IO.File]::Delete($zipPath)
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        [void](Add-ZipText $zip $ServerName $Tag.Substring(1))
        $updater = if ($archiveProblem -eq 'updater-size') { 'x' * ($UpdaterLimit + 1) } else { FixtureUpdater $Tag }
        [void](Add-ZipText $zip 'updater.ps1' $updater)
        [void](Add-ZipText $zip 'launch-agent.ps1' "installer $Tag")
        [void](Add-ZipText $zip 'wwwroot/index.html' 'home')
        [void](Add-ZipText $zip 'wwwroot/app.css' 'css')
        switch ($archiveProblem) {
            'escape' { [void](Add-ZipText $zip '../outside' 'refused') }
            'link' { (Add-ZipText $zip 'linked' 'refused').ExternalAttributes = -1577058304 }
            'reparse' { (Add-ZipText $zip 'linked' 'refused').ExternalAttributes = 1024 }
            { $_ -in 'entries','underreported-entries' } {
                for ($i = 0; $i -lt 9996; $i++) { [void]$zip.CreateEntry("file$i") }
            }
        }
    } finally { $zip.Dispose() }
    if ($Scenario -eq 'archive' -and !$archiveProblem) { Use-Zip64Footer $zipPath }
    if ($archiveProblem -eq 'underreported-entries') {
        $bytes = [IO.File]::ReadAllBytes($zipPath)
        [BitConverter]::GetBytes([uint16]5).CopyTo($bytes, $bytes.Length - 14)
        [BitConverter]::GetBytes([uint16]5).CopyTo($bytes, $bytes.Length - 12)
        [IO.File]::WriteAllBytes($zipPath, $bytes)
    }
    if ($archiveProblem -eq 'expanded') {
        # Inflate the advertised uncompressed size in the first central-directory record.
        $bytes = [IO.File]::ReadAllBytes($zipPath)
        $offset = [BitConverter]::ToInt32($bytes, $bytes.Length - 6)
        [BitConverter]::GetBytes([int](512MB + 1)).CopyTo($bytes, $offset + 24)
        [IO.File]::WriteAllBytes($zipPath, $bytes)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($zipPath))).Replace('-', '') }
    finally { $sha.Dispose() }
    $line = "$digest  $Name"
    $sum = switch ($archiveProblem) {
        'digest' { ('0' * 64) + "  $Name" }
        'malformed' { "broken  $Name" }
        'missing' { "$digest  elsewhere.zip" }
        'duplicate' { $line + [char]10 + $line }
        'manifest' { $line + [char]10 + ('x' * 4096) }
        default { $line }
    }
    [IO.File]::WriteAllText((Join-Path $Fixture 'fixture.sums'), $sum)
}

function New-FixtureResponse($Stream, $Status = 200, $Location = $null, $Length = -1) {
    $response = [pscustomobject]@{ Stream = $Stream; StatusCode = $Status; ContentLength = $Length; Headers = @{ Location = $Location } }
    Add-Member -InputObject $response -MemberType ScriptMethod -Name GetResponseStream -Value { return $this.Stream }
    Add-Member -InputObject $response -MemberType ScriptMethod -Name Dispose -Value { if ($this.Stream) { $this.Stream.Dispose() } }
    return $response
}

function Open-WebResponse([uri]$Uri, $TimeoutMs) {
    if ($Uri.Host -eq '127.0.0.1') {
        Expect $Uri.AbsoluteUri "http://127.0.0.1:$AppPort/health"
        $current = Current-Code
        $json = '{"version":"' + $current + '"}'
        if ($current -eq '1.3.0') {
            switch ($Scenario) {
                'health-array' { $json = '{"version":["1.3.0"]}' }
                'health-list' { $json = '[' + $json + ']' }
                'health-bytes' { $json += ' ' * 4KB }
            }
        }
        return New-FixtureResponse ([IO.MemoryStream]::new($Utf8.GetBytes($json)))
    }
    if ($Uri.Host -eq 'api.github.com') {
        if ($Uri.Query -cnotmatch '\A\?per_page=100&page=([0-9]+)\z') { throw 'Invalid release query.' }
        $page = $Matches[1]
        Expect $Uri.AbsoluteUri "https://api.github.com/repos/$Repository/releases?per_page=100&page=$page"
        $requests.Add("list $page")
        $releases = @(Get-FixtureReleases)
        if ($Scenario -eq 'release-pages') { $releases = @($releases[0]) * 100 }
        $json = ConvertTo-Json -InputObject $releases -Compress
        if ($Scenario -eq 'release-bytes') { $json += ' ' * 1MB }
        return New-FixtureResponse ([IO.MemoryStream]::new($Utf8.GetBytes($json)))
    }
    $requests.Add($Uri.AbsoluteUri)
    $tag = $Uri.Segments[-2].TrimEnd('/')
    $file = $Uri.Segments[-1]
    Expect $Uri.AbsoluteUri "https://github.com/$Repository/releases/download/$tag/$file"
    $name = "$ZipPrefix-$($tag.Substring(1))-win-x64.zip"
    if ($archiveProblem -eq 'redirect') { return New-FixtureResponse $null 302 'https://elsewhere.invalid/file' }
    if ($archiveProblem -eq 'repository') { return New-FixtureResponse $null 302 'https://github.com/elsewhere/releases/file' }
    if ($file -eq 'SHA256SUMS') {
        New-FixtureRelease $tag $name
        return New-FixtureResponse ([IO.File]::OpenRead((Join-Path $Fixture 'fixture.sums')))
    } else {
        Expect $file $name
        $length = if ($archiveProblem -eq 'zip') { 200MB + 1 } else { -1 }
        return New-FixtureResponse ([IO.File]::OpenRead((Join-Path $Fixture 'fixture.zip'))) 200 $null $length
    }
}

function Set-Options($Channel, $Interval) {
    Write-PrivateText 'settings.json' ('{"autoUpdate":true,"channel":"' + $Channel + '","updateInterval":' + $Interval + '}')
}
function Marker { return (Read-PrivateText 'installed-release' 200).Trim() }
function ListCount { return @($requests | Where-Object { $_ -like 'list *' }).Count }

function Expand-FixtureAction($TaskName) {
    $action = $tasks[$TaskName].Action
    $start = [Diagnostics.ProcessStartInfo]::new([Environment]::ExpandEnvironmentVariables($action.Execute),
        [Environment]::ExpandEnvironmentVariables($action.Argument))
    $start.WorkingDirectory = [Environment]::ExpandEnvironmentVariables($action.WorkingDirectory)
    Expect ($start.FileName -ieq (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe')) $true
    Expect ($start.WorkingDirectory -ieq [Environment]::SystemDirectory) $true
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    return $start
}

function Read-FixtureCommand($Start) {
    if ($Start.Arguments -cnotmatch ' -EncodedCommand ([A-Za-z0-9+/=]+)\z') { throw 'Missing task action.' }
    return [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1]))
}

function Start-FixtureAction($TaskName, [switch]$HoldServer, [switch]$PauseBeforeLock) {
    $start = Expand-FixtureAction $TaskName
    $prefix = ''
    if ($TaskName -ceq $ServerTask) {
        # Capture the native process launch inside the registered shell. The ZIP smoke test runs the real EXE.
        $prefix = @'
function Start-Process($FilePath, [string[]]$ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$Wait, [switch]$PassThru) {
    if (!$Wait -or !$PassThru -or $WindowStyle -ne 'Hidden') { throw 'Invalid server process options.' }
    [IO.File]::WriteAllLines((Join-Path $PWD 'received-args.txt'), [string[]](@($FilePath,$WorkingDirectory) + $ArgumentList))
    if ($holdServer) {
        [Console]::WriteLine('server ready')
        [Console]::Out.Flush()
        [void][Console]::ReadLine()
    }
    return [pscustomobject]@{ ExitCode = 0 }
};
'@
        if ($HoldServer) { $prefix = '$holdServer = $true' + [Environment]::NewLine + $prefix }
    } elseif ($PauseBeforeLock) {
        # Pause the registered action at the call. On release it takes the real lock path.
        $prefix = @'
$null = Set-PSBreakpoint -Command Get-UpdateLock -Action {
    [Console]::WriteLine('updater at lock')
    [Console]::Out.Flush()
    [void][Console]::ReadLine()
}
'@
    }
    if ($prefix) {
        $command = $prefix + [Environment]::NewLine + (Read-FixtureCommand $start)
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $start.Arguments = $start.Arguments -replace '(?<= -EncodedCommand ).+\z', $encoded
    }
    $start.RedirectStandardInput = $HoldServer -or $PauseBeforeLock
    $start.RedirectStandardError = $true
    $start.RedirectStandardOutput = $true
    return [Diagnostics.Process]::Start($start)
}

function Wait-FixtureSignal($Process, $Signal) {
    $line = $Process.StandardOutput.ReadLineAsync()
    if (!$line.Wait($actionTimeoutMs)) { throw "Timed out waiting for '$Signal' from the registered action." }
    Expect $line.Result $Signal
}

function Wait-FixtureAction($process) {
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEndAsync()
        if (!$process.WaitForExit($actionTimeoutMs)) { $process.Kill(); $process.WaitForExit(); throw 'Scheduled action timed out.' }
        if ($process.ExitCode -ne 0) { throw "Scheduled action failed: $($errors.Result) $($output.Result)" }
        if ($output.Result) { $output.Result.TrimEnd() }
    } finally { $process.Dispose() }
}

function Run-FixtureAction($TaskName) { Wait-FixtureAction (Start-FixtureAction $TaskName) }

function Stop-FixtureServer {
    $process = $machine.ServerAction
    $machine.ServerAction = $null
    $process.StandardInput.Close()
    Wait-FixtureAction $process
}

function Expect-SharingViolation([scriptblock]$Open, $Opened = 'The registered action did not pin the checked entry.') {
    try { (& $Open).Dispose() }
    catch {
        $failure = $_.Exception.GetBaseException()
        $code = if ($failure -is [ComponentModel.Win32Exception]) { $failure.NativeErrorCode } else { $failure.HResult -band 0xffff }
        Expect $code 32
        return
    }
    throw $Opened
}

function Invoke-FixtureUpdaterTask {
    $start = Expand-FixtureAction $UpdaterTask
    $command = [scriptblock]::Create((Read-FixtureCommand $start))
    $fixtureSupport = $Support
    $fixtureOverrides = @{}
    foreach ($fakeName in 'Get-UpdateTime','Wait-RoomPoll','Get-TaskEngineId','Get-CimInstance','Get-ListenerPid',
        'Get-Process','Open-WebResponse','Move-ReleaseDirectory') {
        $fixtureOverrides[$fakeName] = (Get-Item -LiteralPath "function:$fakeName").ScriptBlock
    }
    if ($Scenario -eq 'job-replace') {
        $fixtureOverrides['Read-JobState'] = (Get-Item function:Read-JobState).ScriptBlock
    }
    Push-Location -LiteralPath $start.WorkingDirectory
    try { & $command } finally { Pop-Location }
}

if ($Scenario -in 'concurrent-install','live-server-install') {
    Add-Type -Namespace Fixture -Name Rename -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access,
    System.IO.FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

public static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path) {
    var handle = CreateFile(path, 0x10000, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete,
        IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
    if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    return handle;
}
'@
}
if ($Scenario -eq 'root-junction') {
    $junction = Join-Path $Fixture 'factory junction'
    [void](New-Item -ItemType Junction -Path $junction -Target $factory)
    [IO.File]::WriteAllText((Join-Path $factory 'junction-target.txt'), $Fixture)
    Expect-Refusal { Install-Room $junction 'v1.2.0' '' } 'physical directory'
    Expect ([IO.Directory]::Exists($Support)) $false
    Expect $tasks.Count 0
    Expect $requests.Count 0
}
if ($Scenario -eq 'live-server-install') {
    $installError = $null
    try {
        Expect ([IO.Directory]::Exists($Support)) $false
        Expect (Install-Room $factory 'v1.2.0' 'desk.example.ts.net') 'Installed v1.2.0. Server running.'
        Expect $machine.ServerAction.HasExited $false
        Expect (Read-PrivateText 'installed-release' 200) ("v1.2.0" + [char]10)
        Expect (Get-RoomStatus) 'Installed=True Running=True'
        Expect (Wait-ServerHealth '1.2.0') $true
        $server = Join-Path $Support 'server'
        Expect-SharingViolation { [Fixture.Rename]::OpenDirectory($Support) }
        Expect-SharingViolation { [Fixture.Rename]::OpenDirectory($server) }
        Expect-SharingViolation { [IO.File]::Open((Join-Path $server $ServerName), 'Open', 'Write', 'ReadWrite') }
        Expect $tasks.Count 2
        Expect ($history -join ',') 'start 1.2.0'
        Expect ([IO.File]::Exists((Join-Path $Support 'update-result.json'))) $false
    } catch {
        $installError = $_
        throw
    } finally {
        if ($machine.ServerAction) {
            try { Stop-FixtureServer } catch {
                if (!$installError) { throw }
                [Console]::Error.WriteLine("Server action cleanup also failed: $_")
            }
        }
    }
    return
}
Expect (Install-Room $factory 'v1.2.0' 'desk.example.ts.net') 'Installed v1.2.0. Server running.'
$old = 'v1.2.0'
Set-Options stable 30
$resultPath = Join-Path $Support 'update-result.json'
if ([IO.File]::Exists($resultPath)) { throw 'Installer wrote an updater result.' }
if ($Scenario -in 'lifecycle','sample') {
    [IO.File]::WriteAllText((Join-Path $Support 'access-token'), 'fixture sentinel')
    Push-Location -LiteralPath ([IO.Path]::GetDirectoryName($factory))
    try {
        $relative = '.\' + [IO.Path]::GetFileName($factory)
        Expect ([IO.Directory]::Exists($relative)) $false
        Expect (Install-Room $relative $old 'desk.example.ts.net') 'Installed v1.2.0. Server running.'
    } finally { Pop-Location }
    Expect $tasks.Count 2
    $serverDefinition = $tasks[$ServerTask]
    $serverPath = Join-Path (Join-Path $Support 'server') $ServerName
    Run-FixtureAction $ServerTask
    $received = [IO.File]::ReadAllLines((Join-Path $Support 'received-args.txt'))
    Expect $received.Count 6
    Expect ([IO.Path]::GetFullPath($received[0])) $serverPath
    Expect $received[1] $Support
    Expect $received[2] '--FactoryRootBase64'
    Expect ($Utf8.GetString([Convert]::FromBase64String($received[3]))) $factory
    Expect $received[4] '--TailnetHost'
    Expect $received[5] 'desk.example.ts.net'
    [IO.File]::WriteAllLines((Join-Path $Fixture 'server-root.txt'), [string[]]($received[3], $factory))
    Expect $serverDefinition.Principal.UserId $UserSid.Value
    Expect $serverDefinition.Principal.LogonType 'Interactive'
    Expect $serverDefinition.Principal.RunLevel 'Limited'
    Expect $serverDefinition.Trigger.AtLogOn $true
    Expect $tasks[$UpdaterTask].Trigger.RepetitionInterval.TotalMinutes 15
    Expect $tasks[$UpdaterTask].Principal.RunLevel 'Limited'
    # Run the registered updater arguments with an isolated entry point that only reports its decoded root.
    $updaterPath = Join-Path (Join-Path $Support 'server') 'updater.ps1'
    $entry = 'if ($MyInvocation.InvocationName -ne ''.'') {'
    $probe = [IO.File]::ReadAllText((Join-Path $inputs 'updater.ps1')).Replace($entry,
        'function Invoke-LockedRoomUpdate($Root) { [IO.File]::WriteAllText((Join-Path $PSScriptRoot "received-root.txt"), $Root) }' + [char]10 + $entry)
    [IO.File]::WriteAllText($updaterPath, $probe)
    Run-FixtureAction $UpdaterTask
    Expect ([IO.File]::ReadAllText((Join-Path (Join-Path $Support 'server') 'received-root.txt'))) $factory
    Expect (Get-RoomStatus) 'Installed=True Running=True'
    if ($Scenario -eq 'sample') {
        Expect $DataFolder 'SampleRoom'
        Expect $ServerTask "SampleRoom-server-$($UserSid.Value)"
        Expect $ServerName 'sampleroom.exe'
        Expect $AppPort 8201
        Expect $Repository 'example/sampleroom'
        Expect $requests[1] 'https://github.com/example/sampleroom/releases/download/v1.2.0/sampleroom-1.2.0-win-x64.zip'
    }
    Expect (Uninstall-Room) 'Uninstalled.'
    if ($Scenario -eq 'lifecycle') { Expect ($history -ccontains 'terminated server child') $true }
    Expect (Get-RoomStatus) 'Installed=False Running=False'
    Expect $tasks.Count 0
    Expect ([IO.Directory]::Exists((Join-Path $Support 'server'))) $false
    Expect ([IO.File]::ReadAllText((Join-Path $Support 'access-token'))) 'fixture sentinel'
    Expect (Get-UpdateSettings).Enabled $true
} elseif ($Scenario -eq 'concurrent-install') {
    # Consume the first-check marker as an earlier scheduled invocation would.
    $offered = $old
    $clock = $clock.AddMinutes(-1)
    Expect (Invoke-FixtureUpdaterTask) 'current'
    $priorStamp = Read-PrivateText 'last-check' 32
    Expect $priorStamp ($clock.ToUnixTimeSeconds().ToString() + [char]10)
    Expect ([IO.File]::Exists((Join-Path $Support 'first-check'))) $false
    $clock = $clock.AddMinutes(1)
    $offered = 'v1.3.0'
    Write-PrivateText 'update-result.json' '{"prior":true}'
    $machine.BeforeBackup = {
        param($Server)
        # Outstanding DELETE access detects a premature pin on the old server directory.
        $rename = [Fixture.Rename]::OpenDirectory($Server)
        try {
            $machine.UpdaterAction = Start-FixtureAction $UpdaterTask -PauseBeforeLock
            Wait-FixtureSignal $machine.UpdaterAction 'updater at lock'
            Expect-SharingViolation { [Fixture.Rename]::OpenDirectory($Support) }
        } finally { $rename.Dispose() }
    }
    $machine.AfterMarker = {
        # The installer still owns the lock after the swap, first-check creation and release marker write.
        Expect (Read-PrivateText 'first-check' 2) '1'
        Expect (Read-PrivateText 'installed-release' 200) ($old + [char]10)
        Expect-SharingViolation { [Fixture.Rename]::OpenDirectory($Support) }
        $process = $machine.UpdaterAction
        $process.StandardInput.Close()
        $machine.UpdaterAction = $null
        $machine.ScheduledResult = Wait-FixtureAction $process
    }
    try {
        Expect (Install-Room $factory $old '') 'Installed v1.2.0. Server running.'
    } finally {
        $machine.AfterMarker = $null
        # If reinstall failed before the marker write, discard the paused action inside its fixture.
        $process = $machine.UpdaterAction
        if ($process) {
            try { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } }
            finally { $process.Dispose() }
        }
    }
    Expect $machine.ScheduledResult 'busy'
    Expect (Marker) $old
    Expect $tasks.Count 2
    Expect (Get-RoomStatus) 'Installed=True Running=True'
    Expect (Wait-ServerHealth '1.2.0') $true
    Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
    Expect (Read-PrivateText 'last-check' 32) $priorStamp
    Expect (ListCount) 1

    $machine.BeforeBackup = {
        Expect-Refusal { Install-Room $factory $old '' } 'An installation or update is already running.'
        $machine.InstallRefused = $true
    }
    Expect (Invoke-FixtureUpdaterTask) 'updated'
    Expect $machine.InstallRefused $true
    Expect (ListCount) 2
    Expect (Current-Code) '1.3.0'
    Expect (Marker) $offered
    Expect (Get-RoomStatus) 'Installed=True Running=True'
} elseif ($Scenario -eq 'interval') {
    $offered = $old
    # The first check is due only through the installer's marker, since the seeded stamp is fresh.
    Expect (Read-PrivateText 'first-check' 2) '1'
    Write-PrivateText 'last-check' "$Epoch"
    Expect-Update 'current'
    Expect (ListCount) 1
    Expect (Read-PrivateText 'last-check' 32) ("$Epoch" + [char]10)
    $clock = $clock.AddSeconds(1739)
    Expect-Update 'not_due'
    Expect (ListCount) 1
    Expect (Read-PrivateText 'last-check' 32) ("$Epoch" + [char]10)
    $clock = $clock.AddSeconds(1)
    Expect-Update 'current'
    Expect (ListCount) 2
    Expect (Read-PrivateText 'last-check' 32) ([string]($Epoch + 1740) + [char]10)
    Set-Options stable 60
    $clock = $clock.AddMinutes(30)
    Expect-Update 'not_due'
    $count = 2
    foreach ($bad in 'missing','broken',([string]($clock.ToUnixTimeSeconds() + 1))) {
        if ($bad -eq 'missing') { [IO.File]::Delete((Join-Path $Support 'last-check')) }
        else { Write-PrivateText 'last-check' $bad }
        Expect-Update 'current'
        Expect (ListCount) (++$count)
        Expect (Read-PrivateText 'last-check' 32) ([string]$clock.ToUnixTimeSeconds() + [char]10)
    }
    $stamp = Join-Path $Support 'last-check'
    # Git Bash (the CI test shell) enables the backup privilege; enable it here too when the token holds it, so the
    # lock is tested the same from any parent shell. A backup-semantics open could otherwise read the locked stamp.
    Add-Type -Namespace Fixture -Name Privilege -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
private struct OnePrivilege { public uint Count; public long Luid; public uint Attributes; }
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
private static extern bool LookupPrivilegeValue(string system, string name, out long luid);
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
private static extern bool OpenProcessToken(System.IntPtr process, uint access, out System.IntPtr token);
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
private static extern bool AdjustTokenPrivileges(System.IntPtr token, bool disableAll, ref OnePrivilege state, uint length,
    System.IntPtr previous, System.IntPtr returned);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
private static extern System.IntPtr GetCurrentProcess();
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
private static extern bool CloseHandle(System.IntPtr handle);

public static void TryEnable(string name) {
    System.IntPtr token;
    // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY; a token without the privilege leaves it off (ERROR_NOT_ALL_ASSIGNED).
    if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) return;
    try {
        OnePrivilege state = new OnePrivilege { Count = 1, Attributes = 2 };
        if (LookupPrivilegeValue(null, name, out state.Luid)) {
            AdjustTokenPrivileges(token, false, ref state, 0, System.IntPtr.Zero, System.IntPtr.Zero);
        }
    } finally { CloseHandle(token); }
}
'@
    [Fixture.Privilege]::TryEnable('SeBackupPrivilege')
    $held = [IO.File]::Open($stamp, 'Open', 'Read', 'None')
    try {
        # The updater's own open meets the lock: a sharing violation (32), not a read of the fresh stamp.
        Expect-SharingViolation { [RoomKit.UpdateEntry]::Open($stamp, $false) } 'The updater opened the locked stamp.'
        Expect-Update 'current'
        Expect (ListCount) (++$count)
    } finally { $held.Dispose() }
    [IO.File]::Delete($stamp)
    [void][IO.Directory]::CreateDirectory($stamp)
    Expect-Update 'current'
    Expect (ListCount) (++$count)
    [IO.Directory]::Delete($stamp)
    $message = ''
    try { $at = @(Get-PSCallStack)[0].ScriptLineNumber; Expect-Update 'updated' } catch { $message = $_.Exception.Message }
    Expect (ListCount) (++$count)
    $now = $clock.ToUnixTimeSeconds()
    Expect $message ("Expected [updated], got [current] at line $at (clock $now; first-check [<FileNotFoundException>]; " +
        "last-check [$now\n]; settings.json [" + '{"autoUpdate":true,"channel":"stable","updateInterval":60}])')
    Expect ([IO.File]::Exists($resultPath)) $false
} elseif ($Scenario -eq 'linked-stamp') {
    $offered = $old
    [IO.File]::Delete((Join-Path $Support 'first-check'))
    Expect (Invoke-RoomUpdate $factory) 'current'
    Expect (ListCount) 1
    $stamp = Join-Path $Support 'last-check'
    $outside = Join-Path $Fixture 'stamp-target'
    Expect ([IO.File]::ReadAllText($outside)) ([string]$Epoch)
    Expect ([bool]([IO.File]::GetAttributes($stamp) -band [IO.FileAttributes]::ReparsePoint)) $true
    Expect ([IO.File]::Exists($resultPath)) $false
} elseif ($Scenario -in 'release-pages','release-bytes') {
    $refused = $false
    try { Invoke-RoomUpdate $factory | Out-Null } catch { $refused = $true }
    Expect $refused $true
    if ($Scenario -eq 'release-pages') { Expect (ListCount) 10 } else { Expect (ListCount) 1 }
    Expect (Marker) $old
    Expect ([IO.File]::Exists($resultPath)) $false
} elseif ($Scenario -eq 'root-junction') {
    $server = $tasks[$ServerTask]
    $updater = $tasks[$UpdaterTask]
    $requestCount = $requests.Count
    $historyCount = $history.Count
    Expect-Refusal { Install-Room $junction $offered '' } 'physical directory'
    Expect $requests.Count $requestCount
    Expect $history.Count $historyCount
    Expect ([object]::ReferenceEquals($tasks[$ServerTask], $server)) $true
    Expect ([object]::ReferenceEquals($tasks[$UpdaterTask], $updater)) $true
    Expect (Current-Code) '1.2.0'
    Expect (Marker) $old
    Expect (Get-RoomStatus) 'Installed=True Running=True'
    $offered = $old
    Expect (Invoke-FixtureUpdaterTask) 'current'
    Expect (ListCount) 1
    Expect ([IO.Directory]::Exists($junction)) $true
    Expect ([IO.File]::ReadAllText((Join-Path $factory 'junction-target.txt'))) $Fixture
} elseif ($Scenario -eq 'job-replace') {
    $job = [IO.Directory]::CreateDirectory((Join-Path (Join-Path $factory $JobsFolder) 'fixture'))
    $fixtureState = Join-Path $job.FullName $JobFile
    $nextState = $fixtureState + '.tmp'
    [IO.File]::WriteAllText($fixtureState, '{"stage":"approve"}')
    [IO.File]::WriteAllText($nextState, '{"stage":"build"}')
    # Windows os.replace publishes the worker's closed temporary file with MOVEFILE_REPLACE_EXISTING.
    Add-Type -Namespace Fixture -Name JobWriter -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
private static extern bool MoveFileEx(string source, string destination, uint flags);

public static System.Threading.Tasks.Task<int> Publish(string source, string destination) {
    return System.Threading.Tasks.Task.Run(() =>
        MoveFileEx(source, destination, 1) ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error());
}
'@
    $readState = (Get-Item function:Read-JobState).ScriptBlock
    $transitionTimeout = [TimeSpan]::FromSeconds(15)
    function Read-JobState($Record) {
        if (!$machine.Transition) {
            $machine.Transition = [Fixture.JobWriter]::Publish($nextState, $fixtureState)
            $machine.BreakSeen = $Record.Changed.WaitOne($transitionTimeout)
        }
        & $readState $Record
    }
    Expect (Invoke-FixtureUpdaterTask) 'job_running'
    Expect $machine.BreakSeen $true
    Expect ($machine.Transition.Wait($transitionTimeout)) $true
    Expect $machine.Transition.Result 0
    Expect ([IO.File]::ReadAllText($fixtureState)) '{"stage":"build"}'
    Expect (Invoke-FixtureUpdaterTask) 'job_running'
    Expect (ListCount) 0
    Expect (Marker) $old
    Expect ([IO.File]::Exists((Join-Path $Support 'last-check'))) $false
    Expect ([IO.File]::Exists($resultPath)) $false
} elseif ($Scenario -eq 'job-scan') {
    $jobs = Join-Path $factory $JobsFolder
    $job = [IO.Directory]::CreateDirectory((Join-Path $jobs 'hidden'))
    $job.Attributes = $job.Attributes -bor [IO.FileAttributes]::Hidden
    $state = Join-Path $job.FullName $JobFile
    foreach ($json in '{"stage":"build"}', ('{"stage":"approve"}' + ' ' * 64KB), '{', '{}') {
        [IO.File]::WriteAllText($state, $json)
        Expect (Invoke-RoomUpdate $factory) 'job_running'
    }
    [IO.File]::WriteAllText($state, '{"stage":"approve"}')
    $held = [IO.File]::Open($state, 'Open', 'Read', 'None')
    try { Expect (Invoke-RoomUpdate $factory) 'job_running' } finally { $held.Dispose() }
    Expect (ListCount) 0
    Expect ([IO.File]::Exists($resultPath)) $false
    [IO.File]::Delete($state)
    [void][IO.Directory]::CreateDirectory($state)
    Expect (Invoke-RoomUpdate $factory) 'job_running'
    [IO.Directory]::Delete($state)
    $offered = $old
    Expect (Invoke-RoomUpdate $factory) 'current'
    Expect (ListCount) 1
} elseif ($Scenario -in 'linked-jobs','linked-job','linked-state') {
    Expect (Invoke-RoomUpdate $factory) 'job_running'
    $target = Join-Path $Fixture 'real job store'
    if ($Scenario -eq 'linked-jobs') { Expect ([IO.Directory]::GetFileSystemEntries($target).Count) 0 }
    else { Expect ([IO.File]::ReadAllText((Join-Path $target $JobFile))) '{"stage":"approve"}' }
    Expect (ListCount) 0
    Expect ([IO.File]::Exists($resultPath)) $false
} elseif ($Scenario -eq 'defer') {
    foreach ($keepResult in $false,$true) {
        if ($keepResult) { [IO.File]::WriteAllBytes($resultPath, [byte[]](0,127,255)) }
        foreach ($stage in 'spike','spec','build','review') {
            $job = Join-Path (Join-Path $factory $JobsFolder) 'fixture'
            [void][IO.Directory]::CreateDirectory($job)
            [IO.File]::WriteAllText((Join-Path $job $JobFile), ('{"stage":"' + $stage + '"}'))
            Expect (Invoke-RoomUpdate $factory) 'job_running'
        }
        $held = Get-UpdateLock
        try { Expect (Invoke-RoomUpdate $factory) 'busy' } finally { $held.Dispose() }
        Write-PrivateText 'settings.json' '{"autoUpdate":false}'
        Expect (Invoke-RoomUpdate $factory) 'off'
        Set-Options stable 30
        Expect (ListCount) 0
        Expect (Marker) $old
        Expect ([IO.File]::Exists($resultPath)) $keepResult
        if ($keepResult) { Expect ([Convert]::ToBase64String([IO.File]::ReadAllBytes($resultPath))) 'AH//' }
    }
} elseif ($Scenario -in 'entry-acl','entry-link','entry-directory','entry-size') {
    $reason = switch ($Scenario) {
        'entry-acl' { 'Unsafe private permissions.' }
        'entry-size' { 'Invalid installed code size.' }
        default { 'Unsafe file or directory.' }
    }
    $server = Join-Path $Support 'server'
    $recovery = Join-Path $Support 'recovery-updater.ps1'
    $original = [IO.File]::ReadAllText($recovery)
    $historyBefore = $history.Count
    if ($Scenario -ne 'entry-size') {
        With-RefusedEntry (Join-Path $server $ServerName) {
            Expect-Refusal { Run-FixtureAction $ServerTask } $reason
            Expect ([IO.File]::Exists((Join-Path $Support 'received-args.txt'))) $false
        }
    }
    With-RefusedEntry (Join-Path $server 'updater.ps1') {
        Expect-Refusal { Invoke-FixtureUpdaterTask } $reason
        Expect-Refusal { Install-Room $factory $offered '' } 'Installation failed (swap).'
        Expect (Invoke-RoomUpdate $factory) 'swap'
        Expect ([IO.File]::ReadAllText($recovery)) $original
    }
    $stage = New-ReleaseWork
    Write-PrivateText 'pending-swap' ($old + [char]10 + $offered + [char]10 + (Get-RunStamp)) $stage
    With-RefusedEntry $recovery { Expect-Refusal { Invoke-FixtureUpdaterTask } $reason }
    Expect $history.Count $historyBefore
    Expect $machine.Running $true
    Expect (Current-Code) '1.2.0'
    Expect (Marker) $old
} elseif ($Scenario -eq 'archive') {
    foreach ($bad in 'digest','missing','malformed','duplicate','manifest','zip','entries','underreported-entries','expanded','escape','link','reparse','updater-size','redirect','repository') {
        $archiveProblem = $bad
        $failed = $false
        try { Install-Room $factory 'v1.3.0' '' | Out-Null }
        catch [IO.InvalidDataException] {
            if ($bad -in 'entries','underreported-entries') { Expect $_.Exception.GetBaseException().Message 'Too many ZIP entries.' }
            $failed = $true
        }
        Expect $failed $true
        Write-PrivateText 'first-check' '1'
        Expect (Invoke-RoomUpdate $factory) 'check'
        Expect (Current-Code) '1.2.0'
        Expect (Marker) $old
        Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) 'updater v1.2.0'
        Expect $history.Count 1
    }
} elseif ($Scenario -eq 'invalid-pending') {
    $stage = New-ReleaseWork
    Write-PrivateText 'update-result.json' '{"prior":true}'
    $valid = '{"run":"20261123T120000Z","result":"update_failed","version":"1.3.0","reason":"check"}'
    foreach ($bad in '{}', ('[' + $valid + ']'), $valid.Replace('1.3.0','bad'), $valid.Replace('check','bad'),
        $valid.Replace('"reason"', '"Reason"'), $valid.Replace('"check"', '"swap","reason":"check"')) {
        $pending = $bad + [char]10
        Write-PrivateText 'pending-result.json' $pending $stage
        $refused = $false
        try { Invoke-RoomUpdate $factory | Out-Null } catch { $refused = $_.Exception.Message -ceq 'Invalid staged update result.' }
        Expect $refused $true
        Expect (Read-PrivateText 'pending-result.json' 4KB $stage) $pending
        Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
        Expect (Marker) $old
        Expect (ListCount) 0
    }
} elseif ($Scenario -in 'interrupted-swap','interrupted-candidate','restore-result-retry') {
    Write-PrivateText 'update-result.json' '{"prior":true}'
    $held = [IO.File]::Open($resultPath, 'Open', 'Read', 'Read')
    try {
        if ($Scenario -ne 'restore-result-retry') {
            $machine.InterruptAt = if ($Scenario -eq 'interrupted-swap') { 'previous' } else { 'server' }
            :interruptedUpdate do {
                Invoke-RoomUpdate $factory | Out-Null
                throw 'Fixture missed the requested rename.'
            } while ($false)
            $machine.InterruptAt = ''
        } else {
            $badListener = $true
            $machine.FailRestore = $true
            $refused = $false
            try { Invoke-RoomUpdate $factory | Out-Null } catch [IO.IOException] { $refused = $true }
            Expect $refused $true
        }
        Expect $machine.Running $false
        Expect ([IO.Directory]::Exists((Join-Path $Support 'server'))) ($Scenario -eq 'interrupted-candidate')
        if ($Scenario -eq 'interrupted-candidate') {
            Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) (FixtureUpdater $offered)
        }
        Expect (Marker) $old
        $stage = @(Get-ChildItem -LiteralPath $Support -Directory -Filter 'stage-*')
        Expect $stage.Count 1
        Expect ([IO.File]::ReadAllText((Join-Path $stage[0].FullName "previous/$ServerName"))) '1.2.0'
        Expect ([IO.File]::Exists((Join-Path $stage[0].FullName 'pending-swap'))) $true
        Expect ([IO.File]::Exists((Join-Path $stage[0].FullName 'pending-result.json'))) ($Scenario -eq 'restore-result-retry')
        Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
    } finally { $held.Dispose() }
    $clock = $clock.AddMinutes(30)
    $expected = if ($Scenario -eq 'restore-result-retry') { 'rollback_failed' } else { 'swap' }
    Expect (Invoke-FixtureUpdaterTask) $expected
    Expect ($history -ccontains ("entry " + (Join-Path $Support 'recovery-updater.ps1'))) $true
    Expect (ListCount) 1
    Expect (Marker) $old
    if ($Scenario -ne 'restore-result-retry') {
        Expect $machine.Running $true
        Expect $history[$history.Count - 1] 'start 1.2.0'
        Expect (Current-Code) '1.2.0'
        Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) (FixtureUpdater $old)
        Expect ([IO.Directory]::Exists($stage[0].FullName)) $false
    } else {
        Expect $machine.Running $false
        Expect ([IO.Directory]::Exists((Join-Path $stage[0].FullName 'previous'))) $true
    }
} elseif ($Scenario -in 'interrupted-rollback','pending-install-digest') {
    Write-PrivateText 'update-result.json' '{"prior":true}'
    $machine.InterruptAt = 'marker'
    :interruptedUpdate do {
        Invoke-RoomUpdate $factory | Out-Null
        throw 'Fixture missed the marker write.'
    } while ($false)
    $machine.InterruptAt = ''
    $stage = @(Get-PendingStages)
    Expect $stage.Count 1
    $pending = Read-PrivateText 'pending-result.json' 4KB $stage[0]
    Expect (Read-StagedRecord $pending).result 'updated'
    Expect (Marker) $old
    Expect (Current-Code) '1.3.0'
    if ($Scenario -eq 'pending-install-digest') {
        $before = $history.Count
        $archiveProblem = 'digest'
        foreach ($badListener in $false,$true) {
            $rejected = $false
            try { Install-Room $factory 'v1.4.0' '' | Out-Null } catch [IO.InvalidDataException] { $rejected = $true }
            Expect $rejected $true
            Expect (Read-PrivateText 'pending-result.json' 4KB $stage[0]) $pending
            Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
            Expect $machine.Running $true
            Expect $tasks[$ServerTask].State 'Running'
            Expect $history.Count $before
            Expect (Marker) $old
            Expect (Current-Code) '1.3.0'
            Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) (FixtureUpdater $offered)
        }
    } else {
        $clock = $clock.AddMinutes(30)
        $badListener = $true
        $machine.InterruptAt = 'new'
        :interruptedUpdate do {
            Invoke-FixtureUpdaterTask | Out-Null
            throw 'Fixture missed the rollback rename.'
        } while ($false)
        $machine.InterruptAt = ''
        Expect $machine.Running $false
        Expect ([IO.Directory]::Exists((Join-Path $Support 'server'))) $false
        Expect ([IO.File]::ReadAllText((Join-Path $stage[0] "previous/$ServerName"))) '1.2.0'
        Expect (Read-PrivateText 'pending-result.json' 4KB $stage[0]) $pending
        Expect (Invoke-FixtureUpdaterTask) 'no_answer'
        $recovery = 'entry ' + (Join-Path $Support 'recovery-updater.ps1')
        Expect @($history | Where-Object { $_ -ceq $recovery }).Count 2
        Expect (ListCount) 1
        Expect $machine.Running $true
        Expect $history[$history.Count - 1] 'start 1.2.0'
        Expect (Marker) $old
        Expect (Current-Code) '1.2.0'
        Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) (FixtureUpdater $old)
        Expect ([IO.Directory]::Exists($stage[0])) $false
    }
} elseif ($Scenario -in 'result-retry','result-before-marker','failed-result-retry','pending-active','uninstall-pending') {
    Write-PrivateText 'update-result.json' '{"prior":true}'
    $held = [IO.File]::Open($resultPath, 'Open', 'Read', 'Read')
    if ($Scenario -eq 'failed-result-retry') { $archiveProblem = 'redirect' }
    $refused = $false
    try {
        try { Invoke-RoomUpdate $factory | Out-Null } catch [IO.IOException] { $refused = $true }
        if ($Scenario -eq 'uninstall-pending') {
            Expect $refused $true
            Write-PrivateText 'access-token' 'fixture sentinel'
            $settings = Read-PrivateText 'settings.json' 64
            $tasks[$UpdaterTask].State = 'Running'
            Expect (Uninstall-Room) 'Uninstalled.'
            Expect ($history -ccontains 'stop updater') $true
            Expect $tasks.Count 0
            Expect $machine.Running $false
            Expect ([IO.File]::Exists((Join-Path $Support 'recovery-updater.ps1'))) $false
            Expect ([IO.Directory]::Exists((Join-Path $Support 'server'))) $false
            Expect @(Get-ChildItem -LiteralPath $Support -Directory -Filter 'stage-*').Count 0
            Expect (Read-PrivateText 'access-token' 64) 'fixture sentinel'
            Expect (Read-PrivateText 'settings.json' 64) $settings
            Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
            return
        }
    } finally { $held.Dispose() }
    Expect $refused $true
    Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
    $stage = @(Get-ChildItem -LiteralPath $Support -Directory -Filter 'stage-*')
    Expect $stage.Count 1
    Expect ([IO.File]::Exists((Join-Path $stage[0].FullName 'pending-result.json'))) $true
    $pending = Read-PrivateText 'pending-result.json' 4KB $stage[0].FullName
    if ($Scenario -ne 'failed-result-retry') {
        Expect ([IO.File]::ReadAllText((Join-Path $stage[0].FullName 'previous/updater.ps1'))) (FixtureUpdater $old)
    }
    if ($Scenario -in 'result-before-marker','pending-active') { Write-PrivateText 'installed-release' ($old + [char]10) }
    $clock = $clock.AddMinutes(1)
    $markerBefore = Marker
    $codeBefore = Current-Code
    $historyBefore = $history.Count
    Expect (Invoke-RoomUpdate $factory) 'not_due'
    $clock = $clock.AddMinutes(29)
    Write-PrivateText 'settings.json' '{"autoUpdate":false}'
    Expect (Invoke-RoomUpdate $factory) 'off'
    Set-Options stable 30
    $job = [IO.Directory]::CreateDirectory((Join-Path (Join-Path $factory $JobsFolder) 'busy'))
    $state = Join-Path $job.FullName $JobFile
    [IO.File]::WriteAllText($state, '{"stage":"build"}')
    if ($Scenario -eq 'pending-active') { $badListener = $true }
    Expect (Invoke-RoomUpdate $factory) 'job_running'
    $busy = Get-UpdateLock
    try { Expect (Invoke-RoomUpdate $factory) 'busy' } finally { $busy.Dispose() }
    Expect $history.Count $historyBefore
    Expect $machine.Running $true
    Expect (Current-Code) $codeBefore
    Expect (Marker) $markerBefore
    Expect (Read-PrivateText 'pending-result.json' 4KB $stage[0].FullName) $pending
    Expect (Read-PrivateText 'update-result.json' 4KB) '{"prior":true}'
    [IO.File]::WriteAllText($state, '{"stage":"approve"}')
    $expected = if ($Scenario -eq 'failed-result-retry') { 'check' } elseif ($Scenario -eq 'pending-active') { 'no_answer' } else { 'updated' }
    if ($Scenario -eq 'pending-active') { Expect (Invoke-FixtureUpdaterTask) $expected }
    else { Expect (Invoke-RoomUpdate $factory) $expected }
    Expect (ListCount) 1
    if ($Scenario -eq 'pending-active') {
        Expect (Current-Code) '1.2.0'
        Expect $machine.Running $true
    } else { Expect (Read-PrivateText 'update-result.json' 4KB) $pending }
    $expectedMarker = if ($expected -ceq 'updated') { $offered } else { $old }
    Expect (Marker) $expectedMarker
    Expect @(Get-ChildItem -LiteralPath $Support -Filter 'stage-*').Count 0
} else {
    $job = Join-Path (Join-Path $factory $JobsFolder) 'fixture'
    [void][IO.Directory]::CreateDirectory($job)
    [IO.File]::WriteAllText((Join-Path $job $JobFile), '{"stage":"approve"}', [Text.UTF8Encoding]::new($true))
    $badListener = $Scenario -in 'wrong-listener','rollback'
    $failRecovery = $Scenario -eq 'rollback'
    $failStart = $Scenario -eq 'start'
    if ($Scenario -eq 'develop') { Set-Options develop 30 }
    $target = if ($Scenario -eq 'develop') { '1.4.0-develop.12' } else { '1.3.0' }
    $expected = switch ($Scenario) {
        'wrong-listener' { 'no_answer' }
        'health-array' { 'no_answer' }
        'health-list' { 'no_answer' }
        'health-bytes' { 'no_answer' }
        'rollback' { 'rollback_failed' }
        'start' { 'bootstrap_failed' }
        'swap' { 'swap' }
        'stable' { 'updated' }
        'develop' { 'updated' }
        default { throw 'Unknown fixture scenario.' }
    }
    Expect (Invoke-RoomUpdate $factory) $expected
    Expect $history[1] 'stop 1.2.0'
    Expect $history[2] 'exit 1.2.0'
    Expect $history[3] "start $target"
    if ($expected -eq 'updated') {
        Expect (Marker) "v$target"
        Expect (Current-Code) $target
        Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) "updater v$target"
    } else {
        Expect (Marker) $old
        Expect (Current-Code) '1.2.0'
        Expect $history[$history.Count - 1] 'start 1.2.0'
        Expect $machine.Running (!$failRecovery)
        Expect ([IO.File]::ReadAllText((Join-Path $Support 'server/updater.ps1'))) 'updater v1.2.0'
    }
}
