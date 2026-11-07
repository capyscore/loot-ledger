$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'TrayUi.cs') -ReferencedAssemblies System.Drawing, System.Windows.Forms, System.Core
Add-Type -Path (Join-Path $root 'Tracking.cs') -ReferencedAssemblies System.Drawing

$cases = @{
    'CapsLock' = 'CapsLock'
    'PGDN' = 'PageDown'
    'Ctrl+Shift+K' = 'Ctrl+Shift+K'
    'Alt+F8' = 'Alt+F8'
    'J+K' = 'J+K'
    'Esc' = 'Esc'
    'Tab' = 'Tab'
    'Enter' = 'Enter'
    'Shift' = 'Shift'
    'Ctrl+Oemplus' = 'Ctrl+Oemplus'
}
foreach ($text in $cases.Keys) {
    $shortcut = [LootLedger.ChestShortcut]::Parse($text)
    if ($shortcut.Text -cne $cases[$text]) { throw "Unexpected display for $text : $($shortcut.Text)" }
    $restored = [LootLedger.ChestShortcut]::Parse($shortcut.Text)
    if (($restored.Keys -join ',') -ne ($shortcut.Keys -join ',')) { throw "Shortcut roundtrip failed: $text" }
}

# Exercise the same message path used by Windows without changing saved settings.
$down = [LootLedger.ChestHotkeyDialog].GetMethod('ProcessCmdKey', [Reflection.BindingFlags]'NonPublic,Instance')
$up = [LootLedger.ChestHotkeyDialog].GetMethod('ProcessKeyPreview', [Reflection.BindingFlags]'NonPublic,Instance')
$save = [LootLedger.ChestHotkeyDialog].GetMethod('SaveSelection', [Reflection.BindingFlags]'NonPublic,Instance')
$deactivate = [LootLedger.ChestHotkeyDialog].GetMethod('OnDeactivate', [Reflection.BindingFlags]'NonPublic,Instance')
$activate = [LootLedger.ChestHotkeyDialog].GetMethod('OnActivated', [Reflection.BindingFlags]'NonPublic,Instance')
foreach ($keys in @(@(0x14), @(0x11, 0x10, 0x4B), @(0x4A, 0x4B), @(0x0D), @(0x09), @(0x1B), @(0x12, 0x77))) {
    $dialog = [LootLedger.ChestHotkeyDialog]::new('CapsLock')
    try {
        foreach ($key in $keys) {
            $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x100, [IntPtr]$key, [IntPtr]::Zero)
            [void]$down.Invoke($dialog, @($message, [Windows.Forms.Keys]$key))
            # Auto-repeat must not finish capture or duplicate a key.
            [void]$down.Invoke($dialog, @($message, [Windows.Forms.Keys]$key))
        }
        for ($index = $keys.Count - 1; $index -ge 0; $index--) {
            $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x101, [IntPtr]$keys[$index], [IntPtr]::Zero)
            [void]$up.Invoke($dialog, @($message))
            if ($dialog.DialogResult -ne [Windows.Forms.DialogResult]::None) { throw 'Capture closed before Save was clicked.' }
        }
        if (($dialog.Shortcut.Keys -join ',') -ne (([LootLedger.ChestShortcut]::new([int[]]$keys)).Keys -join ',')) {
            throw "Captured the wrong keys: $keys"
        }
        [void]$deactivate.Invoke($dialog, @([EventArgs]::Empty))
        [void]$activate.Invoke($dialog, @([EventArgs]::Empty))
        if (($dialog.Shortcut.Keys -join ',') -ne (([LootLedger.ChestShortcut]::new([int[]]$keys)).Keys -join ',')) {
            throw 'Focus change lost the selected shortcut.'
        }
        [void]$save.Invoke($dialog, @())
        if ($dialog.DialogResult -ne [Windows.Forms.DialogResult]::OK) { throw 'Save did not close the dialog successfully.' }
    } finally { $dialog.Dispose() }
}
'PASS: capture, repeat handling, focus changes, and explicit Save'

# Exercise the production save functions, including the live tray display and
# persistence-before-close callback, using an isolated temporary settings file.
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'LootLedger.ps1'), [ref]$tokens, [ref]$errors)
foreach ($name in @('Update-ChestShortcutDisplay', 'Save-BreakShortcut')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
$shortcutSetting = Join-Path $root ('.loot-ledger/hotkey-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Split-Path $shortcutSetting -Parent))
[IO.File]::WriteAllText($shortcutSetting, '')
$menu = [Windows.Forms.ContextMenuStrip]::new()
$changeBreakItem = $menu.Items.Add('Chest hotkey')
$tray = [Windows.Forms.NotifyIcon]::new()
$ui = @{ BreakShortcut = [LootLedger.ChestShortcut]::Parse('CapsLock'); BreakDown = $false }
try {
    $dialog = [LootLedger.ChestHotkeyDialog]::new('CapsLock')
    $dialog.SaveShortcut = [Action[LootLedger.ChestShortcut]] { param($shortcut) Save-BreakShortcut $shortcut }
    $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x100, [IntPtr]0x77, [IntPtr]::Zero)
    [void]$down.Invoke($dialog, @($message, [Windows.Forms.Keys]::F8))
    $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x101, [IntPtr]0x77, [IntPtr]::Zero)
    [void]$up.Invoke($dialog, @($message))
    if ([IO.File]::ReadAllText($shortcutSetting)) { throw 'Captured shortcut persisted before Save.' }
    [void]$save.Invoke($dialog, @())
    if ($dialog.DialogResult -ne [Windows.Forms.DialogResult]::OK) { throw "Save callback did not complete: $($dialog.SaveError)" }
    if ([LootLedger.ChestShortcut]::Parse([IO.File]::ReadAllText($shortcutSetting)).Text -ne 'F8') { throw 'Saved shortcut did not reload.' }
    if ($changeBreakItem.ShortcutKeyDisplayString -ne 'F8' -or $tray.Text -ne 'Loot Ledger | Chest: F8' -or $ui.BreakShortcut.Text -ne 'F8') {
        throw 'Tray shortcut or active binding did not refresh.'
    }
    $dialog.Dispose()

    # A different press replaces the previous chord, and errors keep it editable.
    $dialog = [LootLedger.ChestHotkeyDialog]::new('F8')
    foreach ($key in @(0x77, 0x78)) {
        $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x100, [IntPtr]$key, [IntPtr]::Zero)
        [void]$down.Invoke($dialog, @($message, [Windows.Forms.Keys]$key))
        $message = [Windows.Forms.Message]::Create([IntPtr]::Zero, 0x101, [IntPtr]$key, [IntPtr]::Zero)
        [void]$up.Invoke($dialog, @($message))
    }
    if ($dialog.Shortcut.Text -ne 'F9') { throw 'Second press did not replace the previous chord.' }
    $dialog.SaveShortcut = [Action[LootLedger.ChestShortcut]] { param($shortcut) throw 'Simulated write failure' }
    [void]$save.Invoke($dialog, @())
    if ($dialog.DialogResult -ne [Windows.Forms.DialogResult]::None -or $dialog.Shortcut.Text -ne 'F9') { throw 'Failed save discarded the selection or closed the dialog.' }
    if ([IO.File]::ReadAllText($shortcutSetting) -ne 'F8') { throw 'Failed save changed persisted shortcut.' }
} finally {
    if ($dialog) { $dialog.Dispose() }
    $tray.Dispose()
    $menu.Dispose()
    [IO.File]::Delete($shortcutSetting)
    [IO.File]::Delete("$shortcutSetting.tmp")
    [IO.File]::Delete("$shortcutSetting.bak")
}
'PASS: Save callback persists and reloads shortcut, updates tray immediately, and retains failed saves'
