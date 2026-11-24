#requires -Version 5.1
param(
    [ValidateSet('install','status','uninstall')][string]$Action,
    [string]$Release,
    [string]$FactoryRoot,
    [string]$TailnetHost
)

# The sibling updater supplies the app assignments and the shared verification and swap code.
. (Join-Path $PSScriptRoot 'updater.ps1')

function Invoke-InstalledTask($Config) {
    $Support = $Config.Support
    $UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $held = [Collections.Generic.Stack[IDisposable]]::new()
    $lock = $null
    try {
        try {
            $held.Push((Open-PrivateCodeEntry $Support -Directory))
            if ($Config.Updater) {
                $lock = Get-UpdateLock
                if (!$lock) { return 'busy' }
            }
            Set-Location -LiteralPath $Support
            $server = Join-Path $Support 'server'
            if ($Config.Updater -and ((Get-PendingStages) -or ![IO.Directory]::Exists($server))) {
                $entry = Join-Path $Support 'recovery-updater.ps1'
            } else {
                $held.Push((Open-PrivateCodeEntry $server -Directory))
                $entry = Join-Path $server $Config.Name
            }
            if ($Config.Updater) {
                $held.Push((Open-PrivateCodeEntry $entry -Limit $Config.Limit))
                . $entry
                $Support = $Config.Support
            } else {
                $held.Push((Open-PrivateCodeEntry $entry))
                $arguments = @('--FactoryRootBase64', $Config.Root64)
                if ($Config.Tailnet) { $arguments += '--TailnetHost', $Config.Tailnet }
                $process = Start-Process -FilePath $entry -ArgumentList $arguments -WorkingDirectory $Support -WindowStyle Hidden -Wait -PassThru
                exit $process.ExitCode
            }
        } finally { while ($held.Count) { $held.Pop().Dispose() } }
        # Release the code handles for renames and atomic file replacement. Keep the lock through both.
        Invoke-LockedRoomUpdate ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Config.Root64)))
    } finally { if ($lock) { $lock.Dispose() } }
}

function New-RoomAction($Root, $Tailnet, [switch]$Updater) {
    $name = if ($Updater) { 'updater.ps1' } else { $ServerName }
    $config = @{
        Support = $Support; Name = $name; Limit = $UpdaterLimit; Updater = $Updater.IsPresent
        Root64 = [Convert]::ToBase64String($Utf8.GetBytes($Root)); Tailnet = $Tailnet
    }
    $data = [Convert]::ToBase64String($Utf8.GetBytes(($config | ConvertTo-Json -Compress)))
    # Register the checks themselves in the action. No installed script executes before its checks.
    $functions = 'Initialize-FileReader','Assert-PrivateAcl','Open-PrivateCodeEntry','Get-PendingStages','Invoke-InstalledTask'
    if ($Updater) { $functions += 'Assert-PrivatePath','New-PrivateFile','Get-UpdateLock' }
    $script = @(
        '$ErrorActionPreference = ''Stop'''
        foreach ($name in $functions) {
            (Get-Command -Name $name -CommandType Function).ScriptBlock.Ast.Extent.Text
        }
        "Invoke-InstalledTask ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$data')) | ConvertFrom-Json)"
    ) -join [Environment]::NewLine
    # The scheduler expands environment variables even inside quoted action properties.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $system = '%SystemRoot%\System32'
    New-ScheduledTaskAction -Execute "$system\WindowsPowerShell\v1.0\powershell.exe" -WorkingDirectory $system -Argument (
        "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy RemoteSigned -EncodedCommand $encoded")
}

function Register-RoomTasks($Root, $Tailnet) {
    $serverAction = New-RoomAction $Root $Tailnet
    $updateAction = New-RoomAction $Root $Tailnet -Updater
    $principal = New-ScheduledTaskPrincipal -UserId $UserSid.Value -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $UserSid.Value
    $timer = New-ScheduledTaskTrigger -Once -At (Get-UpdateTime).LocalDateTime.AddMinutes(1) -RepetitionInterval ([TimeSpan]::FromMinutes(15))
    Register-ScheduledTask -TaskPath '\' -TaskName $ServerTask -Action $serverAction -Trigger $logon -Principal $principal -Settings $settings -Force | Out-Null
    Write-PrivateText 'first-check' '1'
    Register-ScheduledTask -TaskPath '\' -TaskName $UpdaterTask -Action $updateAction -Trigger $timer -Principal $principal -Settings $settings -Force | Out-Null
}

function Remove-RoomTask($Name) {
    if (Get-RoomTask $Name) { Unregister-ScheduledTask -TaskPath '\' -TaskName $Name -Confirm:$false }
}

function Install-Room($Root, $Tag, $Tailnet) {
    $directory = if ($Root) { Get-Item -LiteralPath $Root -Force -ErrorAction SilentlyContinue }
    if ($directory -isnot [IO.DirectoryInfo]) { throw 'Supply -FactoryRoot with an existing directory.' }
    $rootPath = $directory.FullName
    if ($Tailnet -and ($Tailnet.Length -gt 253 -or $Tailnet -cnotmatch '\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.){2,}ts\.net\z')) {
        throw 'Invalid tailnet host.'
    }
    Initialize-Support
    $lock = Get-UpdateLock
    if (!$lock) { throw 'An installation or update is already running.' }
    try {
        if (!$Tag) { $Tag = Get-LatestTag (Get-UpdateSettings).Channel }
        $work = New-ReleaseWork
        $reason = $null
        try {
            Get-ReleaseFiles $Tag $work
            while (Complete-StagedResults) { }
            $previous = ('' + (Read-PrivateText 'installed-release' 200)).TrimEnd([char]10)
            if ($previous -and $previous -cnotmatch $TagPattern) { throw 'Invalid installed release.' }
            $oldTasks = @{}
            foreach ($name in $ServerTask,$UpdaterTask) {
                if (Get-RoomTask $name) { $oldTasks[$name] = Export-ScheduledTask -TaskPath '\' -TaskName $name }
            }
            $register = { Register-RoomTasks $rootPath $Tailnet }
            $restore = {
                foreach ($name in $ServerTask,$UpdaterTask) {
                    if ($oldTasks.ContainsKey($name)) {
                        Register-ScheduledTask -TaskPath '\' -TaskName $name -Xml $oldTasks[$name] -Force | Out-Null
                    } else { Remove-RoomTask $name }
                }
            }
            $reason = Switch-RoomRelease $work $Tag $previous $register $restore
            if ($reason) { throw "Installation failed ($reason)." }
        } finally {
            if ($reason -ne 'rollback_failed' -or ![IO.Directory]::Exists((Join-Path $work 'previous'))) {
                Remove-CodeDirectory $work
            }
        }
    } finally { $lock.Dispose() }
    return "Installed $Tag. Server running."
}

function Uninstall-Room {
    if (![IO.Directory]::Exists($Support)) {
        Remove-RoomTask $UpdaterTask
        Remove-RoomTask $ServerTask
        return 'Uninstalled.'
    }
    Assert-PrivatePath $Support
    $lock = Get-UpdateLock
    if (!$lock) { throw 'An installation or update is already running.' }
    try {
        $updater = Get-RoomTask $UpdaterTask
        if ($updater -and $updater.State -in 'Running','Queued') { Stop-ScheduledTask -InputObject $updater }
        Stop-RoomServer
        Remove-RoomTask $UpdaterTask
        Remove-RoomTask $ServerTask
        Remove-CodeDirectory (Join-Path $Support 'server')
        foreach ($dir in (Get-ChildItem -LiteralPath $Support -Directory -Filter 'stage-*')) {
            if ($dir.Name -match '\Astage-[0-9a-f]{32}\z') { Remove-CodeDirectory $dir.FullName }
        }
        foreach ($name in 'installed-release','first-check','last-check','recovery-updater.ps1') { [IO.File]::Delete((Join-Path $Support $name)) }
    } finally { $lock.Dispose() }
    return 'Uninstalled.'
}

function Get-RoomStatus {
    $server = Get-RoomTask $ServerTask
    $installed = [bool]($server -and (Get-RoomTask $UpdaterTask) -and [IO.File]::Exists((Join-Path (Join-Path $Support 'server') $ServerName)))
    $running = [bool]($server -and $server.State -eq 'Running' -and (Get-ServerProcessId))
    return "Installed=$installed Running=$running"
}

if ($MyInvocation.InvocationName -ne '.') {
    switch ($Action) {
        'install' { Install-Room $FactoryRoot $Release $TailnetHost }
        'status' { Get-RoomStatus }
        'uninstall' { Uninstall-Room }
        default { throw 'Choose install, status or uninstall.' }
    }
}
