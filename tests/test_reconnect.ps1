$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\monitor_key_events.ps1') -Raw
$tail = $source.Substring($source.IndexOf('if (-not $RunBridge) {'))
$script:statuses = New-Object 'System.Collections.Generic.List[string]'
$script:delays = New-Object 'System.Collections.Generic.List[int]'
function Set-BridgeStatus([string]$value) { $script:statuses.Add($value) }
function Test-BridgeStop { return $script:stop }
function Wait-BridgeChange([int]$milliseconds) {
    $script:delays.Add($milliseconds)
    if ($script:case -eq 'wake' -and -not $script:connected) {
        $script:connected = $true; $script:epoch++
    } else { if ($script:attempts -ge 4 -or $script:case -eq 'paused') { $script:stop = $true } }
}
function Invoke-M110Session {
    $script:attempts++
    if ($script:case -eq 'errors') { throw 'simulated unavailable GATT service' }
    if ($script:attempts -eq 1) {
        if ($script:case -eq 'coalesced') { $script:epoch++ } else { $script:connected = $false }
        throw 'simulated sleeping mouse'
    }
    $script:stop = $true
}
function New-Object([string]$TypeName) {
    if ($TypeName -ne 'M110ConnectionWatcher') { throw 'Unexpected hardware dependency.' }
    return $script:fake
}
foreach ($scenario in @('wake','errors','paused','coalesced')) {
    $script:case = $scenario; $script:stop = $false; $script:epoch = 0
    $script:attempts = 0; $script:disposed = 0
    $script:connected = $scenario -in @('errors','coalesced')
    $script:statuses.Clear(); $script:delays.Clear()
    $script:fake = [PSCustomObject]@{}
    $script:fake | Add-Member ScriptProperty IsConnected { $script:connected }
    $script:fake | Add-Member ScriptProperty ConnectionEpoch { $script:epoch }
    $script:fake | Add-Member ScriptMethod Start { }
    $script:fake | Add-Member ScriptMethod WaitForChange { param($ms) return $false }
    $script:fake | Add-Member ScriptMethod Dispose { $script:disposed++ }
    $RunBridge = $true; $DurationSeconds = 0
    Invoke-Expression $tail
    if ($script:disposed -ne 1) { throw 'Watcher cleanup failed.' }
    if ($scenario -eq 'errors') {
        if ($script:attempts -ne 4 -or ($script:delays -join ',') -ne '1000,3000,8000,1000') { throw 'Retry bound or backoff failed.' }
    } elseif ($scenario -eq 'paused') {
        if ($script:attempts -ne 0) { throw 'Offline mode attempted a connection.' }
    } elseif ($script:attempts -ne 2) { throw 'Wake did not establish a new session.' }
    Write-Output ('PASS: '+$scenario)
}
Write-Output 'Reconnect supervision passed; no Bluetooth, microphone or keyboard input used.'
