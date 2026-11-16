# Checks that every locale file parses and that no key present in a reference commit (default: master) went missing,
# e.g. after taking upstream changes. Exit 1 when a file does not parse or keys are missing.
#
#   powershell -NoProfile -File scripts\check-locale-keys.ps1 [-Reference master]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Reference = 'master'
)

function Get-Keys($json) {
    $keys = New-Object System.Collections.Generic.HashSet[string]
    function Walk($node, $prefix) {
        foreach ($p in $node.PSObject.Properties) {
            $name = if ($prefix) { "$prefix.$($p.Name)" } else { $p.Name }
            if ($p.Value -is [System.Management.Automation.PSCustomObject]) { Walk $p.Value $name } else { [void]$keys.Add($name) }
        }
    }
    Walk $json ''
    return ,$keys
}

$bad = 0
Get-ChildItem (Join-Path $Root 'Locales') -Filter *.json | ForEach-Object {
    $file = $_
    try { $now = Get-Content -Raw -Encoding UTF8 $file.FullName | ConvertFrom-Json }
    catch { Write-Output "$($file.Name): does not parse ($($_.Exception.Message))"; $bad++; return }
    $refText = git -C $Root show "${Reference}:Locales/$($file.Name)" 2>$null
    if (-not $refText) { return }
    $ref = ($refText -join "`n") | ConvertFrom-Json
    $nowKeys = Get-Keys $now
    $missing = @((Get-Keys $ref) | Where-Object { -not $nowKeys.Contains($_) })
    if ($missing.Count -gt 0) { Write-Output "$($file.Name): $($missing.Count) keys missing: $(($missing | Select-Object -First 8) -join ', ')"; $bad++ }
}
if ($bad -gt 0) { exit 1 }
Write-Output "locales ok"
exit 0
