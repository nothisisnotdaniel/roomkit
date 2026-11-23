#requires -Version 5.1
param(
    [ValidateSet('install','status','uninstall')][string]$Action,
    [string]$Release,
    [string]$FactoryRoot,
    [string]$TailnetHost
)

# The sibling updater supplies the app assignments and the shared verification and swap code.
. (Join-Path $PSScriptRoot 'updater.ps1')

function Quote-TaskArgument([string]$Value) {
    if ($Value -match '["\x00-\x1f]') { throw 'Invalid task argument.' }
    # A closing quote in a native Windows command line consumes half the preceding backslashes.
    return '"' + [regex]::Replace($Value, '\\+$', '$0$0') + '"'
}

function Register-RoomTasks($Root, $Tailnet) {
    $server = Join-Path $Support 'server'
    $serverArgs = '--FactoryRoot ' + (Quote-TaskArgument $Root)
    if ($Tailnet) { $serverArgs += ' --TailnetHost ' + (Quote-TaskArgument $Tailnet) }
    $serverAction = New-ScheduledTaskAction -Execute (Join-Path $server $ServerName) -Argument $serverArgs -WorkingDirectory $Support
    $updaterArgs = '-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File ' + (Quote-TaskArgument (Join-Path $server 'updater.ps1')) +
        ' -FactoryRoot ' + (Quote-TaskArgument $Root)
    $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $updateAction = New-ScheduledTaskAction -Execute $shell -Argument $updaterArgs -WorkingDirectory $Support
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
    if (!$Root -or ![IO.Directory]::Exists($Root)) { throw 'Supply -FactoryRoot with an existing directory.' }
    $rootPath = [IO.Path]::GetFullPath($Root)
    [void](Quote-TaskArgument $rootPath)
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
        Stop-RoomServer
        Remove-RoomTask $UpdaterTask
        Remove-RoomTask $ServerTask
        Remove-CodeDirectory (Join-Path $Support 'server')
        foreach ($dir in (Get-ChildItem -LiteralPath $Support -Directory -Filter 'stage-*')) {
            if ($dir.Name -match '\Astage-[0-9a-f]{32}\z') { Remove-CodeDirectory $dir.FullName }
        }
        foreach ($name in 'installed-release','first-check','last-check') { [IO.File]::Delete((Join-Path $Support $name)) }
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
