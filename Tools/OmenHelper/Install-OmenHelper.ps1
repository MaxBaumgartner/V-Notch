# Installs the OMEN helper for V-Notch: builds it, copies it to Program Files (only admins can change it there)
# and registers a logon task that starts it elevated for you. Asks for admin rights once (UAC).
#   powershell -ExecutionPolicy Bypass -File Tools\OmenHelper\Install-OmenHelper.ps1
#   -Uninstall   stops it and removes the task and the files
param(
    [switch]$Uninstall,
    [switch]$Elevated,
    [string]$Source = '',
    [string]$User = '',
    [string]$ResultFile = ''
)

$ErrorActionPreference = 'Stop'
$taskName = 'V-Notch OMEN Helper'
$target = Join-Path $env:ProgramFiles 'V-Notch OMEN Helper'

if (-not $Elevated) {
    $result = Join-Path $env:TEMP 'VNotchOmenHelper-install.txt'
    Remove-Item $result -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated',
        '-User', "`"$([Security.Principal.WindowsIdentity]::GetCurrent().Name)`"", '-ResultFile', "`"$result`"")
    if ($Uninstall) {
        $argList += '-Uninstall'
    }
    else {
        $publish = Join-Path $env:TEMP 'VNotchOmenHelper'
        if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
        & dotnet publish (Join-Path $PSScriptRoot 'OmenHelper.csproj') -c Release -o $publish -nologo -v:q
        if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }
        $argList += @('-Source', "`"$publish`"")
    }
    Write-Host 'Asking for admin rights (UAC)...'
    try { Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -WindowStyle Hidden }
    catch { Write-Host 'Cancelled: admin rights were not granted.' -ForegroundColor Yellow; exit 1 }
    if (Test-Path $result) { Get-Content $result } else { Write-Host 'No result from the elevated step.' -ForegroundColor Red; exit 1 }
    exit 0
}

# Elevated from here on.
function Report([string]$text) { Add-Content -Path $ResultFile -Value $text }
try {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    Get-Process -Name 'OmenHelper' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    if ($Uninstall) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Report 'Removed: task and files.'
        exit 0
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $target -Recurse -Force

    $action = New-ScheduledTaskAction -Execute (Join-Path $target 'OmenHelper.exe') -WorkingDirectory $target
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
    $running = [bool](Get-Process -Name 'OmenHelper' -ErrorAction SilentlyContinue)
    Report "Installed to $target; task '$taskName' for $User; running: $running"
}
catch {
    Report "FAILED: $($_.Exception.Message)"
    exit 1
}
