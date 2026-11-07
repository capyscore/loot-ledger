$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Keep the compiler private to obj; portable mode creates no system installation.
$version = '7.1.1'
$expectedHash = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
$toolsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'obj/tools'
$compilerDir = Join-Path $toolsDir "innosetup-$version"
$compiler = Join-Path $compilerDir 'ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    [void][IO.Directory]::CreateDirectory($toolsDir)
    $download = Join-Path $toolsDir "innosetup-$version-x64.exe"
    if (!(Test-Path -LiteralPath $download)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-$version-x64.exe" -OutFile $download
    }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Inno Setup download failed SHA-256 verification. Delete $download and retry."
    }
    $process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', '/TASKS=""', "/DIR=`"$compilerDir`"") -WindowStyle Hidden -PassThru -Wait
    try {
        if ($process.ExitCode -ne 0) { throw "Inno Setup preparation failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler is unavailable.' }
$compiler
