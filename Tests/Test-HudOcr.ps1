$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing, System.Runtime.WindowsRuntime
Add-Type -Path (Join-Path $root 'Tracking.cs') -ReferencedAssemblies System.Drawing
$language = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]::new('en-US')
$ocr = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]::TryCreateFromLanguage($language)
if (!$ocr) { throw 'English (US) Windows OCR is unavailable.' }
$decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
$bitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
$resultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
$decoderTask = $asTask.MakeGenericMethod($decoderType)
$bitmapTask = $asTask.MakeGenericMethod($bitmapType)
$ocrTask = $asTask.MakeGenericMethod($resultType)
$pixelFormat = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Foundation, ContentType=WindowsRuntime]::Bgra8
$alphaMode = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Foundation, ContentType=WindowsRuntime]::Ignore

# Dim, small gold lettering on dark scenery exercises the actual Windows OCR path.
$image = [Drawing.Bitmap]::new(600, 400)
$graphics = [Drawing.Graphics]::FromImage($image)
$font = [Drawing.Font]::new('Arial', 13, [Drawing.FontStyle]::Bold)
$gold = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(160, 130, 60))
$capture = [IO.MemoryStream]::new()
try {
    $graphics.Clear([Drawing.Color]::FromArgb(18, 20, 24))
    $graphics.DrawString('+250 XP', $font, $gold, 250, 175)
    $graphics.DrawString('CHEST', $font, $gold, 255, 200)
    $image.Save($capture, [Drawing.Imaging.ImageFormat]::Bmp)
    $bytes = [LootLedger.HudImage]::Enhance($capture.ToArray())
} finally {
    $capture.Dispose(); $gold.Dispose(); $font.Dispose(); $graphics.Dispose(); $image.Dispose()
}
$memory = [IO.MemoryStream]::new($bytes, $false)
$stream = $null
$bitmap = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $stream = [IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
    $task = $decoderTask.Invoke($null, @($decoderType::CreateAsync($stream)))
    $decoder = $task.GetAwaiter().GetResult()
    $task = $bitmapTask.Invoke($null, @($decoder.GetSoftwareBitmapAsync($pixelFormat, $alphaMode)))
    $bitmap = $task.GetAwaiter().GetResult()
    $task = $ocrTask.Invoke($null, @($ocr.RecognizeAsync($bitmap)))
    $result = $task.GetAwaiter().GetResult()
    if ($result.Text -notmatch 'CHEST' -or $result.Text -notmatch '250\s*XP') {
        throw "Dim chest text was not recognized: $($result.Text)"
    }
    "PASS: Windows OCR recognizes enhanced dim gold chest and XP text ($($clock.ElapsedMilliseconds) ms)"
} finally {
    if ($bitmap) { $bitmap.Dispose() }
    if ($stream) { $stream.Dispose() }
    $memory.Dispose()
}
