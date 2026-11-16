# Checks that every function the C# code imports from VNotch.Native.dll exists in the committed DLL. Upstream ships
# the prebuilt DLL with each commit; if C# and DLL get out of step, the app still builds and only throws
# EntryPointNotFoundException when the feature is used. Runs in Windows PowerShell 5.1.
#
#   powershell -NoProfile -File scripts\check-native-exports.ps1
#
# Prints "N missing" (and the names); exit code 1 when anything is missing.
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Dll = ''
)

$ErrorActionPreference = 'Stop'
if ($Dll -eq '') { $Dll = Join-Path $Root 'Native\out\VNotch.Native.dll' }
if (-not (Test-Path $Dll)) { Write-Output "DLL not found: $Dll"; exit 1 }

$libName = 'VNotch.Native.dll'
$attribute = [regex]'\[(?:LibraryImport|DllImport)\(\s*(?<lib>"[^"]*"|[\w\.]+)(?<args>[^\]]*?)\)\]\s*(?:\[[^\]]*\]\s*)*(?<sig>[^(;{}]*)\('
$entryPoint = [regex]'EntryPoint\s*=\s*"(?<ep>[^"]+)"'

$wanted = @{}
$skip = '\\(obj|bin|Tests|Tools|Native|\.dotnet)\\'
Get-ChildItem -Path $Root -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch $skip } | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName)
    if ($text.IndexOf($libName, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return }
    # Constants in this file that name the native DLL, e.g. private const string NativeDll = "VNotch.Native.dll";
    $constants = @{}
    foreach ($m in [regex]::Matches($text, 'const\s+string\s+(?<id>\w+)\s*=\s*"VNotch\.Native\.dll"')) { $constants[$m.Groups['id'].Value] = $true }
    foreach ($m in $attribute.Matches($text)) {
        $lib = $m.Groups['lib'].Value
        $isNative = ($lib -eq "`"$libName`"") -or $constants.ContainsKey(($lib -split '\.')[-1])
        if (-not $isNative) { continue }
        $ep = $entryPoint.Match($m.Groups['args'].Value)
        $name = if ($ep.Success) { $ep.Groups['ep'].Value } else { ([regex]::Matches($m.Groups['sig'].Value, '\w+') | Select-Object -Last 1).Value }
        if ($name) { $wanted[$name] = $_.FullName.Substring($Root.Length).TrimStart('\') }
    }
}

Add-Type -Namespace VNotchCheck -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
public static extern IntPtr GetProcAddress(IntPtr module, string name);
[DllImport("kernel32.dll")]
public static extern bool FreeLibrary(IntPtr module);
'@
# DONT_RESOLVE_DLL_REFERENCES: map the exports without running DllMain or loading dependencies.
$module = [VNotchCheck.Native]::LoadLibraryEx((Resolve-Path $Dll).Path, [IntPtr]::Zero, 0x1)
if ($module -eq [IntPtr]::Zero) { Write-Output "Cannot load $Dll"; exit 1 }
$missing = @($wanted.Keys | Sort-Object | Where-Object { [VNotchCheck.Native]::GetProcAddress($module, $_) -eq [IntPtr]::Zero })
[void][VNotchCheck.Native]::FreeLibrary($module)

Write-Output "$($wanted.Count) imports checked, $($missing.Count) missing"
foreach ($name in $missing) { Write-Output "  $name  ($($wanted[$name]))" }
if ($missing.Count -gt 0) { exit 1 }
exit 0
