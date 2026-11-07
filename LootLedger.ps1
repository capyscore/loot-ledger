param(
    [switch]$Preview,
    [string]$RenderPreview,
    [string]$DataDirectory
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
    Add-Type -Path (Join-Path $PSScriptRoot 'Tracking.cs') -ReferencedAssemblies System.Drawing
    Add-Type -Path (Join-Path $PSScriptRoot 'TrayUi.cs') -ReferencedAssemblies System.Drawing, System.Windows.Forms, System.Core

    $reader = [System.Xml.XmlReader]::Create((Join-Path $PSScriptRoot 'Overlay.xaml'))
    try { $window = [Windows.Markup.XamlReader]::Load($reader) }
    finally { $reader.Dispose() }

    $card = $window.FindName('Card')
    $matchText = $window.FindName('MatchCount')
    $totalText = $window.FindName('TotalCount')
    $matchLabel = $window.FindName('MatchLabel')
    $statusText = $window.FindName('StatusText')
    $statusDot = $window.FindName('StatusDot')
    $eventText = $window.FindName('EventText')
    $modeText = $window.FindName('Mode')
    $brush = [Windows.Media.BrushConverter]::new()

    function Set-Preview {
        $matchText.Text = '12'
        $totalText.Text = '1,284'
        $matchLabel.Text = 'THIS MATCH'
        $statusText.Text = 'Preview'
        $statusDot.Fill = $brush.ConvertFromString('#F5C582')
        $eventText.Text = '+2 chests'
        $modeText.Text = 'DEMO'
    }

    if ($RenderPreview) {
        Set-Preview
        $size = [Windows.Size]::new($window.Width, $window.Height)
        $window.Content = $null
        $card.Measure($size)
        $card.Arrange([Windows.Rect]::new($size))
        $card.UpdateLayout()
        $image = [Windows.Media.Imaging.RenderTargetBitmap]::new(528, 312, 192, 192, [Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($card)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
        $output = [IO.File]::Create($RenderPreview)
        try { $encoder.Save($output) }
        finally { $output.Dispose() }
        return
    }

    $mutex = [Threading.Mutex]::new($false, 'Local\LootLedger-Overlay')
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) {
        [void][Windows.MessageBox]::Show('Loot Ledger is already running. Open it from the gold chest icon in your system tray.', 'Loot Ledger')
        return
    }

    $dataDir = if ($DataDirectory) { [IO.Path]::GetFullPath($DataDirectory) }
        else { Join-Path $PSScriptRoot '.loot-ledger' }
    $countPath = Join-Path $dataDir 'counts.json'
    $positionSetting = Join-Path $dataDir 'overlay-position'
    $breakSetting = Join-Path $dataDir 'broken-chest-hotkey'
    $shortcutSetting = Join-Path $dataDir 'broken-chest-shortcut'
    $logPath = Join-Path $env:LOCALAPPDATA 'FortniteGame\Saved\Logs\FortniteGame.log'
    $state = [ordered]@{ Total = [long]0; Match = [long]0; MatchId = '' }
    $loaded = $false
    $foundSave = $false
    foreach ($path in @($countPath, "$countPath.bak")) {
        if (!(Test-Path -LiteralPath $path)) { continue }
        $foundSave = $true
        try {
            $saved = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($null -eq $saved.Total -or $null -eq $saved.Match -or
                $saved.Total -lt 0 -or $saved.Match -lt 0 -or $saved.Match -gt $saved.Total) {
                throw 'Invalid counts file.'
            }
            $state.Total = [long]$saved.Total
            $state.Match = [long]$saved.Match
            $state.MatchId = [string]$saved.MatchId
            $loaded = $true
            break
        } catch { }
    }
    if ($foundSave -and !$loaded) {
        throw "Loot Ledger could not read its saved counts. Check counts.json and counts.json.bak in $dataDir."
    }
    [void][IO.Directory]::CreateDirectory($dataDir)

    $corner = [LootLedger.OverlayCorner]::TopLeft
    if ([IO.File]::Exists($positionSetting)) {
        try {
            $savedCorner = [IO.File]::ReadAllText($positionSetting).Trim()
            if ($savedCorner -cin [Enum]::GetNames([LootLedger.OverlayCorner])) {
                $corner = [LootLedger.OverlayCorner]$savedCorner
            }
        } catch [IO.IOException] { }
          catch [UnauthorizedAccessException] { }
    }

    function Read-BreakShortcut([string]$text) {
        return [LootLedger.ChestShortcut]::Parse($text)
    }

    $breakShortcut = Read-BreakShortcut 'CapsLock'
    if ([IO.File]::Exists($shortcutSetting)) {
        try { $breakShortcut = Read-BreakShortcut ([IO.File]::ReadAllText($shortcutSetting)) }
        catch { }
    }

    $control = [hashtable]::Synchronized(@{
        Stop = $false
        CounterVersion = 0
    })
    $messages = [Collections.Concurrent.ConcurrentQueue[object]]::new()

    $scanner = {
        param($control, $messages, $logPath)
        $ErrorActionPreference = 'Stop'
        $matchLog = [LootLedger.MatchLog]::new($logPath)
        $gate = [LootLedger.ChestGate]::new()
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $lastMatch = ''

        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $language = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]::new('en-US')
            $ocr = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]::TryCreateFromLanguage($language)
            if (!$ocr) { throw 'English (US) Windows OCR is unavailable. Install its language pack in Windows Settings.' }
            $decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
            $bitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
            $ocrResultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
            $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
                $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
            } | Select-Object -First 1
            if (!$asTask) { throw 'Windows OCR async support is unavailable.' }
            $decoderTask = $asTask.MakeGenericMethod($decoderType)
            $bitmapTask = $asTask.MakeGenericMethod($bitmapType)
            $ocrTask = $asTask.MakeGenericMethod($ocrResultType)
            $pixelFormat = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Foundation, ContentType=WindowsRuntime]::Bgra8
            $alphaMode = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Foundation, ContentType=WindowsRuntime]::Ignore

            while (!$control.Stop) {
                $counterVersion = $control.CounterVersion
                $started = $clock.ElapsedMilliseconds
                $processId = 0
                $game = [LootLedger.GameWindow]::FocusedClient([ref]$processId)
                $gameFocused = !$game.IsEmpty
                $hasLog = $matchLog.Poll()
                $isPlaying = $hasLog -and $matchLog.Phase -eq [LootLedger.MatchPhase]::Playing
                $scanHud = $hasLog -and ($isPlaying -or $matchLog.Phase -eq [LootLedger.MatchPhase]::Aircraft)
                if ($lastMatch -ne $matchLog.MatchId) {
                    $gate.Reset()
                    $lastMatch = $matchLog.MatchId
                }

                $warning = ''
                $signals = $null
                $delta = 0
                if ($gameFocused -and $scanHud) {
                    $frameTime = $clock.ElapsedMilliseconds
                    try {
                        $bytes = [LootLedger.GameWindow]::CaptureHud($game)
                        $captures = [Collections.Generic.List[byte[]]]::new()
                        $captures.Add($bytes)
                        $captures.Add([LootLedger.HudImage]::Enhance($bytes))
                        $lines = [Collections.Generic.List[LootLedger.HudLine]]::new()
                        foreach ($capture in $captures) {
                            $memory = [IO.MemoryStream]::new($capture, $false)
                            $stream = $null
                            $bitmap = $null
                            try {
                                $stream = [IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
                                $task = $decoderTask.Invoke($null, @($decoderType::CreateAsync($stream)))
                                $decoder = $task.GetAwaiter().GetResult()
                                $task = $bitmapTask.Invoke($null, @($decoder.GetSoftwareBitmapAsync($pixelFormat, $alphaMode)))
                                $bitmap = $task.GetAwaiter().GetResult()
                                $task = $ocrTask.Invoke($null, @($ocr.RecognizeAsync($bitmap)))
                                $result = $task.GetAwaiter().GetResult()
                                foreach ($line in $result.Lines) {
                                    if ($line.Words.Count -eq 0) { continue }
                                    $left = [double]::PositiveInfinity
                                    $top = [double]::PositiveInfinity
                                    $right = 0.0
                                    $bottom = 0.0
                                    foreach ($word in $line.Words) {
                                        $rect = $word.BoundingRect
                                        $left = [Math]::Min($left, $rect.X)
                                        $top = [Math]::Min($top, $rect.Y)
                                        $right = [Math]::Max($right, $rect.X + $rect.Width)
                                        $bottom = [Math]::Max($bottom, $rect.Y + $rect.Height)
                                    }
                                    $lines.Add([LootLedger.HudLine]::new($line.Text,
                                        ($left + $right) / (2 * $bitmap.PixelWidth),
                                        ($top + $bottom) / (2 * $bitmap.PixelHeight),
                                        ($bottom - $top) / $bitmap.PixelHeight))
                                }
                            } finally {
                                if ($bitmap) { $bitmap.Dispose() }
                                if ($stream) { $stream.Dispose() }
                                $memory.Dispose()
                            }
                        }
                        $signals = [LootLedger.HudSignals]::Read($lines)
                    } catch {
                        $warning = 'HUD capture unavailable'
                        $captureError = $_.Exception.Message
                    }

                    $checkPid = 0
                    $stillFocused = [LootLedger.GameWindow]::FocusedClient([ref]$checkPid)
                    if ($checkPid -eq $processId -and !$stillFocused.IsEmpty) {
                        $delta = $gate.Observe($frameTime, $signals, $matchLog.Phase)
                    }
                } else {
                    if ($scanHud) { $gate.Suspend() } else { $gate.Reset() }
                }

                $messages.Enqueue([pscustomobject]@{
                    Game = $game; HasLog = $hasLog; Phase = $matchLog.Phase; MatchId = $matchLog.MatchId
                    Delta = $delta; Source = $gate.Source; Spectating = $signals -and $signals.Spectating
                    Tracking = $scanHud -and $gate.TrackingAllowed
                    CounterVersion = $counterVersion
                    Warning = $warning; Detail = if ($warning) { $captureError } else { '' }
                })
                $delay = if ($gameFocused -and $scanHud) { 135 } else { 550 }
                Start-Sleep -Milliseconds ([Math]::Max(20, $delay - [int]($clock.ElapsedMilliseconds - $started)))
            }
        } catch {
            $messages.Enqueue([pscustomobject]@{ Fatal = $_.Exception.Message })
        }
    }

    $app = [Windows.Application]::new()
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    $handle = [Windows.Interop.WindowInteropHelper]::new($window).EnsureHandle()
    [LootLedger.GameWindow]::MakeClickThrough($handle)

    $tray = [Windows.Forms.NotifyIcon]::new()
    $trayIcon = [Drawing.Bitmap]::new(32, 32)
    $graphic = [Drawing.Graphics]::FromImage($trayIcon)
    $gold = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 203, 122))
    $dark = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(41, 29, 22))
    try {
        $graphic.FillEllipse($dark, 1, 1, 30, 30)
        $graphic.FillPie($gold, 6, 5, 20, 17, 180, 180)
        $graphic.FillRectangle($gold, 6, 15, 20, 11)
        $graphic.FillRectangle($dark, 6, 14, 20, 2)
        $graphic.FillRectangle($dark, 14, 13, 4, 7)
    } finally { $graphic.Dispose(); $gold.Dispose(); $dark.Dispose() }
    $iconHandle = $trayIcon.GetHicon()
    $tray.Icon = [Drawing.Icon]::FromHandle($iconHandle)
    $tray.Text = 'Loot Ledger'
    $tray.Visible = $true
    $menu = [Windows.Forms.ContextMenuStrip]::new()
    [LootLedger.LedgerMenuRenderer]::Style($menu)
    $previewItem = $menu.Items.Add('Show preview')
    $overlayItem = $menu.Items.Add('Show in-game overlay')
    $overlayItem.CheckOnClick = $true
    $overlayItem.Checked = $true
    $positionMenu = $menu.Items.Add('Position')
    [LootLedger.LedgerMenuRenderer]::Style($positionMenu.DropDown)
    $corners = [ordered]@{
        'Top left' = [LootLedger.OverlayCorner]::TopLeft
        'Bottom left' = [LootLedger.OverlayCorner]::BottomLeft
        'Top right' = [LootLedger.OverlayCorner]::TopRight
        'Bottom right' = [LootLedger.OverlayCorner]::BottomRight
    }
    foreach ($label in $corners.Keys) {
        $item = $positionMenu.DropDownItems.Add($label)
        $item.Tag = $corners[$label]
        $item.Checked = $item.Tag -eq $corner
    }
    [void]$menu.Items.Add([Windows.Forms.ToolStripSeparator]::new())
    $resetMenu = $menu.Items.Add('Reset counter')
    [LootLedger.LedgerMenuRenderer]::Style($resetMenu.DropDown)
    $resetMatchItem = $resetMenu.DropDownItems.Add('Current match')
    $resetAllItem = $resetMenu.DropDownItems.Add('Match + all-time totals')
    [void]$menu.Items.Add([Windows.Forms.ToolStripSeparator]::new())
    $breakItem = $menu.Items.Add('Enable chest hotkey')
    $breakItem.CheckOnClick = $true
    $changeBreakItem = $menu.Items.Add('Chest hotkey')
    $changeBreakItem.ShortcutKeyDisplayString = $breakShortcut.Text
    [void]$menu.Items.Add([Windows.Forms.ToolStripSeparator]::new())
    $quitItem = $menu.Items.Add('Exit Loot Ledger')
    foreach ($item in $menu.Items) {
        if ($item -is [Windows.Forms.ToolStripMenuItem]) { $item.Padding = [Windows.Forms.Padding]::new(4, 7, 8, 7) }
    }
    foreach ($item in $positionMenu.DropDownItems) { $item.Padding = [Windows.Forms.Padding]::new(4, 7, 8, 7) }
    foreach ($item in $resetMenu.DropDownItems) { $item.Padding = [Windows.Forms.Padding]::new(4, 7, 8, 7) }
    $tray.ContextMenuStrip = $menu

    $ui = @{ Preview = [bool]$Preview; Latest = $null; Dirty = $false; SaveRetry = [datetime]::MinValue
        SaveError = ''; Fatal = ''; LastDelta = 0; LastSource = ''; EventTime = [datetime]::MinValue
        Game = [Drawing.Rectangle]::Empty; Corner = $corner
        BreakShortcut = $breakShortcut; BreakDown = $false; CapturingHotkey = $false }

    function Update-ChestShortcutDisplay($shortcut) {
        $changeBreakItem.ShortcutKeyDisplayString = $shortcut.Text
        $menu.PerformLayout()
        $menu.Invalidate()
        $tooltip = 'Loot Ledger | Chest: ' + $shortcut.Text
        $tray.Text = $tooltip.Substring(0, [Math]::Min(63, $tooltip.Length))
    }

    function Save-BreakShortcut($shortcut) {
        [IO.File]::WriteAllText("$shortcutSetting.tmp", $shortcut.Text)
        if ([IO.File]::Exists($shortcutSetting)) {
            [IO.File]::Replace("$shortcutSetting.tmp", $shortcutSetting, "$shortcutSetting.bak")
        } else {
            [IO.File]::Move("$shortcutSetting.tmp", $shortcutSetting)
        }
        $ui.BreakShortcut = $shortcut
        $ui.BreakDown = [LootLedger.GameWindow]::ShortcutDown($shortcut.Keys)
        Update-ChestShortcutDisplay $shortcut
    }

    $breakItem.Checked = [IO.File]::Exists($breakSetting)
    Update-ChestShortcutDisplay $breakShortcut

    function Show-DesktopPreview {
        Set-Preview
        $window.Show()
        [LootLedger.GameWindow]::Position($handle, [Windows.Forms.Screen]::PrimaryScreen.WorkingArea, $ui.Corner)
    }

    function Save-Counts {
        if (!$ui.Dirty -or [datetime]::UtcNow -lt $ui.SaveRetry) { return }
        try {
            [IO.File]::WriteAllText("$countPath.tmp", ($state | ConvertTo-Json -Compress))
            if ([IO.File]::Exists($countPath)) {
                [IO.File]::Replace("$countPath.tmp", $countPath, "$countPath.bak")
            } else {
                [IO.File]::Move("$countPath.tmp", $countPath)
            }
            $ui.Dirty = $false
            $ui.SaveError = ''
        } catch {
            $ui.SaveError = $_.Exception.Message
            $ui.SaveRetry = [datetime]::UtcNow.AddSeconds(3)
        }
    }

    function Add-Chests([int]$number, [string]$source) {
        $state.Match += $number
        $state.Total += $number
        $ui.EventTime = [datetime]::UtcNow
        $ui.LastDelta = $number
        $ui.LastSource = $source
        $ui.Dirty = $true
    }

    function Receive-Detections {
        $message = $null
        while ($messages.TryDequeue([ref]$message)) {
            if ($message.Fatal) {
                $ui.Fatal = $message.Fatal
                $tray.ShowBalloonTip(4500, 'Loot Ledger', $message.Fatal, [Windows.Forms.ToolTipIcon]::Warning)
                continue
            }
            $ui.Latest = $message
            if ($message.HasLog -and $message.MatchId -and $state.MatchId -ne $message.MatchId) {
                $state.MatchId = $message.MatchId
                $state.Match = 0
                $ui.LastDelta = 0
                $ui.LastSource = ''
                $ui.Dirty = $true
            }
            if ($message.CounterVersion -eq $control.CounterVersion -and $message.Delta -gt 0) {
                Add-Chests $message.Delta $message.Source
            }
        }
    }

    function Reset-Counter([switch]$AllTime) {
        Receive-Detections
        # Ignore a scan already in progress, while preserving notice deduplication.
        $control.CounterVersion++
        $state.Match = 0
        if ($AllTime) { $state.Total = 0 }
        $ui.LastDelta = 0
        $ui.LastSource = ''
        $ui.Dirty = $true
        $ui.SaveRetry = [datetime]::MinValue
        Save-Counts
        if (!$ui.Preview) {
            $matchText.Text = '0'
            $totalText.Text = $state.Total.ToString('N0')
            $eventText.Text = ''
        }
    }

    function Test-ActiveMatch {
        $latest = $ui.Latest
        return $latest.HasLog -and $latest.MatchId -and !$latest.Spectating -and
            $latest.Tracking
    }

    function Add-BrokenChest {
        Receive-Detections
        if (Test-ActiveMatch) {
            Add-Chests 1 'Manual broken chest'
            Save-Counts
        }
    }

    $togglePreview = {
        $ui.Preview = !$ui.Preview
        if ($ui.Preview) {
            $previewItem.Text = 'Hide preview'
            Show-DesktopPreview
        } else {
            $previewItem.Text = 'Show preview'
            $ui.Game = [Drawing.Rectangle]::Empty
            $window.Hide()
        }
    }
    $previewItem.Add_Click($togglePreview)
    $tray.Add_DoubleClick($togglePreview)
    $selectCorner = {
        param($sender, $eventArgs)
        if ($sender.Tag -eq $ui.Corner) { return }
        try {
            [IO.File]::WriteAllText($positionSetting, $sender.Tag.ToString())
        } catch {
            [void][Windows.MessageBox]::Show($_.Exception.Message, 'Loot Ledger position')
            return
        }
        $ui.Corner = $sender.Tag
        foreach ($item in $positionMenu.DropDownItems) {
            $item.Checked = $item.Tag -eq $ui.Corner
        }
        $ui.Game = [Drawing.Rectangle]::Empty
        if ($ui.Preview) { Show-DesktopPreview }
    }
    foreach ($item in $positionMenu.DropDownItems) { $item.Add_Click($selectCorner) }
    $resetMatchItem.Add_Click({ Reset-Counter })
    $resetAllItem.Add_Click({ Reset-Counter -AllTime })
    $menu.Add_Opening({
        Receive-Detections
    })
    $shortcutTimer = [Windows.Threading.DispatcherTimer]::new()
    $shortcutTimer.Interval = [timespan]::FromMilliseconds(20)
    $shortcutTimer.Add_Tick({
        if ($ui.CapturingHotkey) { return }
        $shortcut = $ui.BreakShortcut
        $down = [LootLedger.GameWindow]::ShortcutDown($shortcut.Keys)
        $pressed = $down -and !$ui.BreakDown
        # Latch the press until the entire chord is released, including modifiers.
        if ($down) { $ui.BreakDown = $true }
        elseif (![LootLedger.GameWindow]::AnyShortcutKeyDown($shortcut.Keys)) { $ui.BreakDown = $false }
        if (!$pressed) { return }
        $processId = 0
        $game = [LootLedger.GameWindow]::FocusedClient([ref]$processId)
        if (!$game.IsEmpty) { Add-BrokenChest }
    })
    if ($breakItem.Checked) { $shortcutTimer.Start() }
    $breakItem.Add_Click({
        $enable = $breakItem.Checked
        try {
            if ($enable) { [IO.File]::WriteAllText($breakSetting, 'enabled') }
            else { [IO.File]::Delete($breakSetting) }
        } catch {
            $breakItem.Checked = !$enable
            [void][Windows.MessageBox]::Show($_.Exception.Message, 'Loot Ledger')
            return
        }
        $ui.BreakDown = [LootLedger.GameWindow]::AnyShortcutKeyDown($ui.BreakShortcut.Keys)
        if ($enable) { $shortcutTimer.Start() }
        else { $shortcutTimer.Stop() }
    })
    $changeBreakItem.Add_Click({
        $menu.Close()
        $dialog = [LootLedger.ChestHotkeyDialog]::new($ui.BreakShortcut.Text)
        $dialog.SaveShortcut = [Action[LootLedger.ChestShortcut]] { param($shortcut) Save-BreakShortcut $shortcut }
        $ui.CapturingHotkey = $true
        try {
            [void]$dialog.ShowDialog()
        } catch { [void][Windows.MessageBox]::Show($_.Exception.Message, 'Loot Ledger') }
        finally {
            $dialog.Dispose()
            $ui.BreakDown = [LootLedger.GameWindow]::AnyShortcutKeyDown($ui.BreakShortcut.Keys)
            $ui.CapturingHotkey = $false
        }
    })
    $quitItem.Add_Click({
        Receive-Detections
        $ui.SaveRetry = [datetime]::MinValue
        Save-Counts
        if ($ui.Dirty) {
            $choice = [Windows.MessageBox]::Show('The newest counts could not be saved. Exit anyway?',
                'Loot Ledger', [Windows.MessageBoxButton]::YesNo)
            if ($choice -ne [Windows.MessageBoxResult]::Yes) { return }
        }
        $app.Shutdown()
    })

    $worker = [powershell]::Create()
    [void]$worker.AddScript($scanner.ToString()).AddArgument($control).AddArgument($messages).AddArgument($logPath)
    $running = $worker.BeginInvoke()

    $timer = [Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [timespan]::FromMilliseconds(100)
    $timer.Add_Tick({
        Receive-Detections
        Save-Counts
        if ($ui.Preview) { return }
        $latest = $ui.Latest
        if (!$latest -or $latest.Game.IsEmpty -or !$overlayItem.Checked) {
            if ($window.IsVisible) { $window.Hide() }
            return
        }
        if (!$window.IsVisible -or $ui.Game -ne $latest.Game) {
            if (!$window.IsVisible) { $window.Show() }
            [LootLedger.GameWindow]::Position($handle, $latest.Game, $ui.Corner)
            $ui.Game = $latest.Game
        }
        $matchText.Text = $state.Match.ToString('N0')
        $totalText.Text = $state.Total.ToString('N0')
        $isLive = $latest.Tracking
        $matchLabel.Text = if ($latest.Phase -in @([LootLedger.MatchPhase]::Lobby, [LootLedger.MatchPhase]::Finished) -and $state.MatchId) { 'LAST MATCH' } else { 'THIS MATCH' }
        $modeText.Text = 'AUTO'
        $statusText.Text = if ($ui.SaveError) { 'Save pending' }
            elseif (!$latest.HasLog) { 'Waiting for game log' }
            elseif ($latest.Warning) { $latest.Warning }
            elseif ($latest.Spectating) { 'Spectating' }
            elseif ($isLive) { 'Tracking live' }
            elseif ($latest.Phase -eq [LootLedger.MatchPhase]::Warmup) { 'Match starting' }
            elseif ($latest.Phase -eq [LootLedger.MatchPhase]::Aircraft) { 'On the battle bus' }
            elseif ($latest.Phase -eq [LootLedger.MatchPhase]::Finished) { 'Match complete' }
            else { 'Waiting for a match' }
        $statusDot.Fill = $brush.ConvertFromString($(if ($ui.SaveError -or $latest.Warning) { '#F5A465' }
            elseif ($isLive -and !$latest.Spectating) { '#9EE6AB' } else { '#A8A1B2' }))
        $eventText.Text = if ($ui.LastDelta -and ([datetime]::UtcNow - $ui.EventTime).TotalSeconds -lt 2.3) {
            if ($ui.LastDelta -eq 1) { '+1 chest' } else { "+$($ui.LastDelta) chests" }
        } else { '' }
    })
    if ($ui.Preview) {
        $previewItem.Text = 'Hide preview'
        Show-DesktopPreview
    }
    $timer.Start()
    [void]$app.Run()
} catch {
    [Console]::Error.WriteLine($_.ToString())
    if (!$RenderPreview) {
        [void][Windows.MessageBox]::Show($_.Exception.Message, 'Loot Ledger')
    }
    exit 1
} finally {
    if ($timer) { $timer.Stop() }
    if ($shortcutTimer) { $shortcutTimer.Stop() }
    if ($control) { $control.Stop = $true }
    if ($worker) {
        if ($running) { [void]$running.AsyncWaitHandle.WaitOne(3500) }
        $worker.Dispose()
    }
    if ($tray) { $tray.Dispose() }
    if ($menu) { $menu.Dispose() }
    if ($trayIcon) { $trayIcon.Dispose() }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
