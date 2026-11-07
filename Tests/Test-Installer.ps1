param(
    [string]$InnoCompiler = (Join-Path (Split-Path $PSScriptRoot -Parent) 'obj/tools/innosetup-7.1.1/ISCC.exe')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path $root ('obj/installer-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$installDir = Join-Path $temporary 'Chosen install folder'
$desktop = Join-Path $temporary 'Desktop/Loot Ledger.lnk'
$startup = Join-Path $temporary 'Startup/Loot Ledger.lnk'
$startMenu = Join-Path $temporary 'Start Menu/Loot Ledger.lnk'
$uninstaller = Join-Path $installDir 'unins000.exe'
$testId = [Guid]::NewGuid().ToString().ToUpperInvariant()
$registryKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$testId}_is1"

function Invoke-Setup([string]$file, [string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(60000)) { throw "Installer test timed out: $file" }
        if ($process.ExitCode -ne 0) { throw "Installer test failed with exit code $($process.ExitCode). See logs in $temporary" }
    } finally { $process.Dispose() }
}

try {
    # Compile the production recipe with only its external destinations, identity,
    # and mutex isolated. No real Desktop/Startup shortcuts or app data are touched.
    $recipe = [IO.File]::ReadAllText((Join-Path $root 'Build/Installer.iss'))
    if ($recipe -notmatch '(?m)^DefaultGroupName=Loot Ledger\r?$') { throw 'Start Menu folder must default to Loot Ledger.' }
    if ($recipe -notmatch '(?m)^DisableDirPage=no\r?$') { throw 'Install folder picker must be visible.' }
    if ($recipe -notmatch '(?m)^Name: startup;.*Flags: unchecked\r?$') { throw 'Startup must be opt-in.' }
    $recipe = $recipe.Replace('[Setup]', "[Setup]`r`nSourceDir=$root\Build")
    $recipe = $recipe.Replace('0F1C1EBD-644E-4562-9E5C-8354F4A8F5B1', $testId)
    $recipe = $recipe.Replace('AppName=Loot Ledger', 'AppName=Loot Ledger installer test')
    $recipe = $recipe.Replace('Local\LootLedger-Overlay', "Local\LootLedger-InstallerTest-$testId")
    $recipe = $recipe.Replace('OutputDir=..\dist', "OutputDir=$temporary")
    $recipe = $recipe.Replace('{userdesktop}', (Join-Path $temporary 'Desktop'))
    $recipe = $recipe.Replace('{userstartup}', (Join-Path $temporary 'Startup'))
    $recipe = $recipe.Replace('{group}', (Join-Path $temporary 'Start Menu'))
    $scriptPath = Join-Path $temporary 'Test.iss'
    [IO.File]::WriteAllText($scriptPath, $recipe)
    & $InnoCompiler '/Q' $scriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
    $setup = Join-Path $temporary 'LootLedger-Setup.exe'
    $common = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installDir`"")
    Invoke-Setup $setup ($common + @('/TASKS="desktopicon,startup"', "/LOG=`"$temporary\install.log`""))
    $installed = Join-Path $installDir 'LootLedger.exe'
    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root 'dist/LootLedger.exe')).Hash) { throw 'Installer did not copy the current executable into the selected folder.' }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($desktop, $startup, $startMenu)) {
        if (!(Test-Path -LiteralPath $path)) { throw "Missing shortcut: $path" }
        $shortcut = $shell.CreateShortcut($path)
        if ($shortcut.TargetPath -ne $installed -or $shortcut.WorkingDirectory -ne $installDir) { throw "Wrong shortcut destination: $path" }
    }
    if (!(Test-Path -LiteralPath $registryKey)) { throw 'Installer did not register its uninstaller.' }

    Invoke-Setup $setup ($common + @('/TASKS=""', "/LOG=`"$temporary\options-off.log`""))
    if ((Test-Path -LiteralPath $desktop) -or (Test-Path -LiteralPath $startup)) { throw 'Unchecked shortcut/startup options were not removed on reinstall.' }
    Invoke-Setup $setup ($common + @('/TASKS="desktopicon,startup"', "/LOG=`"$temporary\options-on.log`""))
    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$temporary\uninstall.log`"")
    if ((Test-Path -LiteralPath $installed) -or (Test-Path -LiteralPath $desktop) -or
        (Test-Path -LiteralPath $startup) -or (Test-Path -LiteralPath $startMenu) -or (Test-Path -LiteralPath $registryKey)) {
        throw 'Uninstall left application files, shortcuts, or registration behind.'
    }
    'PASS: Selected install folder, desktop/Start Menu/startup shortcut targets, changing options on reinstall, and uninstall cleanup'
} finally {
    if ((Test-Path -LiteralPath $registryKey) -and (Test-Path -LiteralPath $uninstaller)) {
        Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
    # Only remove the unique test directory, after verifying its absolute boundary.
    $resolved = [IO.Path]::GetFullPath($temporary)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'obj')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (!(Test-Path -LiteralPath $registryKey)) {
        # Inno's temporary uninstall process can finish deleting itself shortly
        # after the original process exits.
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try { Remove-Item -LiteralPath $resolved -Recurse -Force; break }
            catch {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
