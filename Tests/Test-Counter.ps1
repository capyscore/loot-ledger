$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'LootLedger.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Save-Counts', 'Add-Chests', 'Receive-Detections', 'Reset-Counter')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
$temporary = Join-Path $root ('obj/counter-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$countPath = Join-Path $temporary 'counts.json'
$state = [ordered]@{ Total = [long]100; Match = [long]12; MatchId = 'same-match' }
$ui = @{ Dirty = $false; SaveRetry = [datetime]::MinValue; SaveError = ''; Preview = $false; LastDelta = 2; LastSource = 'HUD' }
$control = [hashtable]::Synchronized(@{ CounterVersion = 0 })
$messages = [Collections.Concurrent.ConcurrentQueue[object]]::new()
$matchText = @{ Text = '12' }
$totalText = @{ Text = '100' }
$eventText = @{ Text = '+2 chests' }
function Queue-Chest([int]$version) {
    $messages.Enqueue([pscustomobject]@{ HasLog = $true; MatchId = 'same-match'; Delta = 1; Source = 'HUD'; CounterVersion = $version })
}
try {
    Queue-Chest 0
    Reset-Counter
    if ($ui.SaveError) { throw $ui.SaveError }
    $saved = [IO.File]::ReadAllText($countPath) | ConvertFrom-Json
    if ($state.Match -ne 0 -or $state.Total -ne 101 -or $saved.Match -ne 0 -or $saved.Total -ne 101 -or $saved.MatchId -ne 'same-match') {
        throw 'Match reset failed to preserve lifetime total or match identity on disk.'
    }
    if ($matchText.Text -ne '0' -or $totalText.Text -ne '101' -or $eventText.Text -or $ui.LastDelta) { throw 'Match reset did not refresh live display.' }
    Queue-Chest 0 # Scan that was in progress before the reset.
    Receive-Detections
    if ($state.Match -ne 0 -or $state.Total -ne 101) { throw 'Stale scan undid reset.' }
    Queue-Chest 1
    Receive-Detections
    if ($state.Match -ne 1 -or $state.Total -ne 102) { throw 'Counting failed to resume after reset.' }
    Reset-Counter -AllTime
    $saved = [IO.File]::ReadAllText($countPath) | ConvertFrom-Json
    if ($saved.Match -ne 0 -or $saved.Total -ne 0 -or $state.MatchId -ne 'same-match' -or $totalText.Text -ne '0') { throw 'Full reset did not clear both totals.' }
    Queue-Chest 1
    Queue-Chest 2
    Receive-Detections
    Save-Counts
    $saved = [IO.File]::ReadAllText($countPath) | ConvertFrom-Json
    if ($saved.Match -ne 1 -or $saved.Total -ne 1) { throw 'Full reset accepted a stale scan or blocked a new scan.' }
    $countPath = Join-Path $temporary 'missing/counts.json'
    Reset-Counter -AllTime
    if (!$ui.Dirty -or !$ui.SaveError) { throw 'Failed reset save was not retained for retry.' }
    $countPath = Join-Path $temporary 'counts.json'
    $ui.SaveRetry = [datetime]::MinValue
    Save-Counts
    $saved = [IO.File]::ReadAllText($countPath) | ConvertFrom-Json
    if ($ui.Dirty -or $ui.SaveError -or $saved.Total -ne 0) { throw 'Reset save did not recover on retry.' }
    'PASS: Both reset scopes persist, refresh immediately, reject in-flight detections, resume counting, and retry failed saves'
} finally {
    foreach ($name in @('counts.json', 'counts.json.tmp', 'counts.json.bak')) { [IO.File]::Delete((Join-Path $temporary $name)) }
    [IO.Directory]::Delete($temporary)
}
