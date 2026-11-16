# Installs a published V-Notch build into %LOCALAPPDATA%\V-Notch-Release without ever leaving a broken notch behind:
# the running build is copied to V-Notch-Release.prev first, the new one is started and watched, and if it exits or
# hangs within the watch time the previous build is put back and started again.
#
#   powershell -NoProfile -File scripts\safe-install.ps1 -PublishDir "$env:LOCALAPPDATA\V-Notch-Staging\publish"
#
# Exit code 0 = new build runs, 1 = rolled back (old build runs), 2 = nothing installed (bad input).
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [int]$WatchSeconds = 20
)

$ErrorActionPreference = 'Stop'
$release = Join-Path $env:LOCALAPPDATA 'V-Notch-Release'
$backup = Join-Path $env:LOCALAPPDATA 'V-Notch-Release.prev'
$exe = Join-Path $release 'V-Notch.exe'

if (-not (Test-Path (Join-Path $PublishDir 'V-Notch.exe'))) {
    Write-Output "No V-Notch.exe in $PublishDir; nothing installed."
    exit 2
}

function Stop-Notch {
    Get-Process V-Notch -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 20 -and (Get-Process V-Notch -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
}

function Start-Notch {
    $env:VNOTCH_SKIP_INTEGRITY_CHECK = '1'
    return Start-Process -FilePath $exe -WorkingDirectory $release -PassThru
}

function Copy-Tree([string]$from, [string]$to, [switch]$Mirror) {
    # /MIR makes the target an exact copy (backup and restore); /E only adds and overwrites, so the logs next to
    # the exe survive an install. robocopy exit codes below 8 mean success.
    $mode = if ($Mirror) { '/MIR' } else { '/E' }
    robocopy $from $to $mode /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed ($LASTEXITCODE)" }
}

Stop-Notch
if (Test-Path $exe) { Copy-Tree $release $backup -Mirror }
Copy-Tree $PublishDir $release

$proc = Start-Notch
$healthy = $true
for ($t = 0; $t -lt $WatchSeconds; $t++) {
    Start-Sleep -Seconds 1
    $proc.Refresh()
    if ($proc.HasExited) { $healthy = $false; break }
}
if ($healthy) {
    $proc.Refresh()
    if (-not $proc.Responding) { $healthy = $false }
}

if ($healthy) {
    Write-Output "Installed and running (pid $($proc.Id))."
    exit 0
}

Write-Output "New build exited or hangs (exit code: $(if ($proc.HasExited) { $proc.ExitCode } else { 'hung' })); rolling back."
Stop-Notch
if (Test-Path (Join-Path $backup 'V-Notch.exe')) {
    Copy-Tree $backup $release -Mirror
    $old = Start-Notch
    Start-Sleep -Seconds 3
    Write-Output "Previous build restored and running (pid $($old.Id))."
}
exit 1
