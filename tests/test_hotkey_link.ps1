$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\support\GattNotificationObserver.dll')
$type = [M110HotkeyLink]
$constructor = $type.GetConstructor([Reflection.BindingFlags]'Instance,NonPublic', $null,
    [type[]]@([string],[Action[bool]],[Action[int]]), $null)
$script:edges = New-Object 'System.Collections.Generic.List[string]'
$script:delays = New-Object 'System.Collections.Generic.List[int]'
$sender = [Action[bool]] { param($down) $script:edges.Add($(if ($down) { 'down' } else { 'up' })) }
$wait = [Action[int]] { param($ms) $script:delays.Add($ms) }
function New-TestLink([string]$mode) {
    $script:edges.Clear(); $script:delays.Clear()
    return $constructor.Invoke(@($mode, $sender, $wait))
}
$link = New-TestLink 'hold'
$link.Start(); $link.Start(); $link.Dispose(); $link.Dispose()
if (($script:edges -join ',') -ne 'down,up') { throw 'Hold cleanup or duplicate-edge handling failed.' }
if ($script:delays.Count -ne 1 -or $script:delays[0] -le 0) { throw 'Short press was not extended to a hold.' }
$link = New-TestLink 'toggle'
$link.Start(); $link.Start()
if (($script:edges -join ',') -ne 'down,up') { throw 'Start must send a complete tap and release Alt before speaking.' }
$link.Stop(); $link.Stop(); $link.Dispose()
if (($script:edges -join ',') -ne 'down,up,down,up') { throw 'Toggle mode did not emit exactly two taps.' }
$link = New-TestLink 'toggle'
$link.Start(); $link.Dispose(); $link.Dispose()
if (($script:edges -join ',') -ne 'down,up,down,up') { throw 'Toggle cleanup must finish recording with exactly one additional tap.' }
$link = New-TestLink 'none'
$link.Start(); $link.Stop(); $link.Dispose()
if ($script:edges.Count) { throw 'Disabled link emitted keyboard events.' }
$link = New-TestLink 'hold'
$link.Start()
# Simulate failure of the recording work after key down: finally must release.
try { throw 'simulated recording failure' } catch {} finally { $link.Dispose() }
if (($script:edges -join ',') -ne 'down,up') { throw 'Failure cleanup left a key held.' }
'Hotkey lifecycle tests passed; no keyboard input was injected.'
