$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\support\GattNotificationObserver.dll')
# Load only the pure key function, without running the Bluetooth script.
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\src\monitor_key_events.ps1'),[ref]$null,[ref]$null)
$function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-M110KeyAction'},$true)
Invoke-Expression $function.Extent.Text

# Event packets carry an event type plus a little-endian key/state value.
$down = [byte[]]@(0xFE, 1, 9, 5, 0, 0, 0, 1, 1, 0, 0, 0)
$up = [byte[]]@(0xFE, 2, 9, 5, 0, 0, 0, 1, 0, 0, 0, 0)
$battery = [byte[]]@(0xFE, 3, 9, 5, 0, 0, 0, 2, 1, 0, 0, 0)
$audio = [byte[]]@(0xFE, 4, 0xA0, 5, 0, 0, 0, 1, 1, 0, 0, 0)

if ([GattNotificationObserver]::DescribeKeyEvent($down) -ne 'key=0 state=1') { throw 'Key-down decoding failed.' }
if ([GattNotificationObserver]::DescribeKeyEvent($up) -ne 'key=0 state=0') { throw 'Key-up decoding failed.' }
if ($null -ne [GattNotificationObserver]::DescribeKeyEvent($battery)) { throw 'Battery event was misclassified.' }
if ($null -ne [GattNotificationObserver]::DescribeKeyEvent($audio)) { throw 'Audio data was misclassified.' }
if ((Get-M110KeyAction -ObservedId 0 -State 1 -Mode hold -TargetId 0 -Recording $false) -ne 'start') { throw 'Hold mode did not start on key down.' }
if ((Get-M110KeyAction -ObservedId 0 -State 0 -Mode hold -TargetId 0 -Recording $true) -ne 'stop') { throw 'Hold mode did not stop on key up.' }
if ((Get-M110KeyAction -ObservedId 0 -State 0 -Mode toggle -TargetId 0 -Recording $true) -ne 'ignore') { throw 'Toggle mode stopped on release.' }
if ((Get-M110KeyAction -ObservedId 0 -State 1 -Mode toggle -TargetId 0 -Recording $true) -ne 'stop') { throw 'Toggle mode did not stop on a second press.' }
if ((Get-M110KeyAction -ObservedId 1 -State 1 -Mode hold -TargetId 0 -Recording $false) -ne 'ignore') { throw 'Another key was not ignored.' }
Write-Output 'Key-event parser tests passed.'
