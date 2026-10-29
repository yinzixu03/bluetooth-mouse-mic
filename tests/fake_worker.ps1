param([int]$DurationSeconds,[switch]$RunBridge,[string]$KeyMode,[int]$KeyId,[string]$OutputDeviceName,
    [string]$StopFile,[string]$StatusFile,[string]$TargetHotkeyMode='none')
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'fail.local')) { exit 2 }
@{ KeyMode=$KeyMode; TargetHotkeyMode=$TargetHotkeyMode; OutputDeviceName=$OutputDeviceName } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'parameters.local.json') -Encoding UTF8
[IO.File]::WriteAllText($StatusFile,'ready')
$end = [DateTime]::UtcNow.AddSeconds(25)
while (-not (Test-Path -LiteralPath $StopFile) -and [DateTime]::UtcNow -lt $end) { Start-Sleep -Milliseconds 100 }
