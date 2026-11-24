#requires -Version 5.1
param(
    [ValidateSet('install','status','uninstall')][string]$Action,
    [string]$Release,
    [string]$FactoryRoot,
    [string]$TailnetHost
)

# The sibling updater supplies the app assignments and the shared verification and swap code.
. (Join-Path $PSScriptRoot 'updater.ps1')

function New-RoomAction($Command) {
    # Scheduler expands %NAME% in all three properties. Only the system shell path is expandable.
    # Decode the installed location inside PowerShell, after the scheduler has launched it.
    $location = [Convert]::ToBase64String($Utf8.GetBytes($Support))
    $script = "`$ErrorActionPreference='Stop'; Set-Location -LiteralPath ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$location'))); " + $Command
    $system = '%SystemRoot%\System32'
    New-ScheduledTaskAction -Execute "$system\WindowsPowerShell\v1.0\powershell.exe" -WorkingDirectory $system -Argument (
        '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy RemoteSigned -Command "' + $script + '"')
}

function Register-RoomTasks($Root, $Tailnet) {
    $encodedRoot = [Convert]::ToBase64String($Utf8.GetBytes($Root))
    $exe = [Convert]::ToBase64String($Utf8.GetBytes([IO.Path]::Combine($Support, 'server', $ServerName)))
    $serverArgs = "'--FactoryRootBase64','$encodedRoot'"
    if ($Tailnet) { $serverArgs += ",'--TailnetHost','$Tailnet'" }
    $path = "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$exe')))"
    $serverCommand = "`$server = Start-Process -FilePath $path -ArgumentList $serverArgs -WorkingDirectory `$PWD.ProviderPath -WindowStyle Hidden -Wait -PassThru"
    $serverAction = New-RoomAction ($serverCommand + '; exit $server.ExitCode')
    $updaterCommand = '$entry = ''.\server\updater.ps1''; if (![IO.File]::Exists((Join-Path $PWD $entry))) { $entry = ''.\recovery-updater.ps1'' }; '
    $updateAction = New-RoomAction ($updaterCommand + "& `$entry -FactoryRootBase64 '$encodedRoot'")
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
        while (Complete-StagedResults) { } # Finish each previously staged operation before a manual install.
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
