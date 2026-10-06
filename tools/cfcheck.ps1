# Lists framework types/members an assembly uses that the phone's NETCF 3.5
# does not have (checked against tools/cfref, pulled from the device).
#   pwsh tools/cfcheck.ps1 app/WMAI.exe [more assemblies...]
# Exit code 1 if anything is missing.
param([Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Paths)

$ErrorActionPreference = 'Stop'
$ref = @(Get-ChildItem (Join-Path $PSScriptRoot 'cfref') -Filter *.dll -ErrorAction SilentlyContinue | % FullName)
if ($ref.Count -eq 0) {
    # The reference assemblies are Microsoft binaries from the phone, not in the repo.
    Write-Warning "cfcheck skipped: tools\cfref is empty. Connect the phone (WMDC) and run: python tools\pull_cfref.py"
    exit 0
}
Add-Type -Path (Join-Path $PSScriptRoot 'CfCheck.cs') -ReferencedAssemblies System.Reflection.Metadata, System.Collections.Immutable, System.Linq, System.Collections, System.Runtime, System.IO, System.Linq.Expressions
$checker = [CfCheck]::new([string[]]$ref)
$bad = 0
foreach ($p in $Paths) {
    $missing = $checker.Check((Resolve-Path $p).Path)
    "{0}: {1} missing" -f $p, $missing.Count
    $missing | % { "  $_" }
    $bad += $missing.Count
}
if ($bad) { exit 1 }
