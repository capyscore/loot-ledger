$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'Tracking.cs'), (Join-Path $PSScriptRoot 'TrackingTests.cs')) -ReferencedAssemblies System.Drawing
[LootLedger.Tests.TrackingTests]::Run()
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'LootLedger.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
'PASS: Overlay script parses'
