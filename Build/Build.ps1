param(
    [string]$Version = '0.0.0',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'dist'
$intermediate = Join-Path $root 'obj/package'

if ($PSVersionTable.PSEdition -ne 'Desktop' -or ![Environment]::Is64BitProcess) {
    throw 'Build with 64-bit Windows PowerShell 5.1.'
}
if ($Version -notmatch '^v?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw 'Version must look like 1.0.0, v1.0.0, or v1.0.0-beta.1.'
}
$patch = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
$numbers = @([int]$Matches[1], [int]$Matches[2], $patch, 0)
if (@($numbers | Where-Object { $_ -gt 65534 }).Count) { throw 'Version components must be at most 65534.' }
$assemblyVersion = $numbers -join '.'
if (!$InnoCompiler) { $InnoCompiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
if (!(Test-Path -LiteralPath $InnoCompiler)) { throw "Inno Setup compiler not found: $InnoCompiler" }

[void][IO.Directory]::CreateDirectory($output)
[void][IO.Directory]::CreateDirectory($intermediate)
$assemblyInfo = Join-Path $intermediate 'AssemblyInfo.cs'
$metadata = @"
using System.Reflection;
[assembly: AssemblyTitle("Loot Ledger")]
[assembly: AssemblyProduct("Loot Ledger")]
[assembly: AssemblyDescription("Fortnite chest tracker for Windows")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Loot Ledger contributors")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
[IO.File]::WriteAllText($assemblyInfo, $metadata, [Text.UTF8Encoding]::new($false))

$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is unavailable.' }
$exe = Join-Path $output 'LootLedger.exe'
$arguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/utf8output',
    '/reference:System.Windows.Forms.dll',
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
    "/out:$exe"
)
# Only these files enter the executable; never package the workspace or saved data.
foreach ($name in @('LootLedger.ps1', 'Tracking.cs', 'TrayUi.cs', 'Overlay.xaml', 'LICENSE')) {
    $path = Join-Path $root $name
    if (!(Test-Path -LiteralPath $path)) { throw "Missing application file: $name" }
    $arguments += "/resource:$path,LootLedger.Payload.$name"
}
$arguments += (Join-Path $PSScriptRoot 'Launcher.cs'), $assemblyInfo
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }

$installer = Join-Path $output 'LootLedger-Setup.exe'
& $InnoCompiler '/Q' "/DAppVersion=$($Version -replace '^v', '')" "/DAppFileVersion=$assemblyVersion" (Join-Path $PSScriptRoot 'Installer.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }

$zip = Join-Path $output 'LootLedger-win-x64.zip'
Compress-Archive -LiteralPath @($exe, (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md')) -DestinationPath $zip -Force
$checksums = foreach ($path in @($exe, $installer, $zip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($path)
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $exe, $installer, $zip | Select-Object Name, Length
