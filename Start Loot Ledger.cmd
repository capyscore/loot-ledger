@echo off
setlocal
start "Loot Ledger" /min "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0LootLedger.ps1" %*
