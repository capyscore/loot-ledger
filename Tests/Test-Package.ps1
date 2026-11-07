param(
    [string]$Executable = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/LootLedger.exe')
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Loot Ledger package test ' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$imagePath = Join-Path $temporary 'preview output.png'
$process = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = '--render-preview "' + $imagePath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(30000)) {
        # Stop both the launcher and its PowerShell child if packaging regresses.
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw 'Packaged preview timed out.'
    }
    if ($process.ExitCode -ne 0) { throw "Packaged preview failed with exit code $($process.ExitCode)." }
    if (!(Test-Path -LiteralPath $imagePath)) { throw 'The executable did not render a preview.' }
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Image]::FromFile($imagePath)
    try {
        if ($image.Width -ne 528 -or $image.Height -ne 312) { throw 'Unexpected preview dimensions.' }
    } finally { $image.Dispose() }
    'PASS: Standalone EXE unpacks its embedded files and renders the overlay to a path with spaces'
} finally {
    if ($process) { $process.Dispose() }
    # Delete only the two known files/directory created by this test.
    if ([IO.File]::Exists($imagePath)) { [IO.File]::Delete($imagePath) }
    [IO.Directory]::Delete($temporary)
}
