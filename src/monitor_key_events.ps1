param(
    [int]$DurationSeconds = 60,
    [switch]$IncludeHeaders,
    [switch]$DecodeKeyEvents,
    [switch]$QueryProtocolVersion,
    [switch]$TestAudioStream,
    [switch]$DecodeAudio,
    [string]$OutputDeviceName = '',
    [switch]$SilentOutput,
    [switch]$RunBridge,
    [ValidateSet('toggle', 'hold')][string]$KeyMode = 'toggle',
    [int]$KeyId = -1,
    [ValidateSet('none', 'hold', 'toggle')][string]$TargetHotkeyMode = 'none',
    [string]$StopFile = '',
    [string]$StatusFile = ''
)
$ErrorActionPreference = 'Stop'
function Get-M110KeyAction {
    param(
        [int]$ObservedId,
        [int]$State,
        [string]$Mode,
        [int]$TargetId,
        [bool]$Recording
    )
    if ($TargetId -ge 0 -and $TargetId -ne $ObservedId) { return 'ignore' }
    if ($State -eq 1 -and -not $Recording) { return 'start' }
    if ($Recording -and (($Mode -eq 'hold' -and $State -eq 0) -or ($Mode -eq 'toggle' -and $State -eq 1))) { return 'stop' }
    return 'ignore'
}


if ($RunBridge) {
    if (-not $OutputDeviceName) { throw 'Bridge mode needs a specific virtual audio output name.' }
    $DecodeAudio = $true
    $DecodeKeyEvents = $true
}
if ($RunBridge -and $TestAudioStream) { throw 'Choose either bridge mode or the timed audio test.' }
if ($TargetHotkeyMode -ne 'none' -and -not $RunBridge) { throw 'Hotkey linking requires bridge mode.' }

# Temporary, bounded notification probe for the M110's standard vendor service.
# Only notification timestamps and byte lengths are displayed by default. The
# optional header mode displays three bytes per notification. The optional
# version query sends a device-information command. The audio test explicitly
# starts the microphone for three seconds and then sends the stop command.
# No audio is saved and no speech recognition runs.
# The Bluetooth notification subscription is removed on exit.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -Path (Join-Path $PSScriptRoot 'GattNotificationObserver.dll')
$bleType = [Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$infoType = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
$collectionType = [Windows.Devices.Enumeration.DeviceInformationCollection, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
$cacheType = [Windows.Devices.Bluetooth.BluetoothCacheMode, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$servicesType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType=WindowsRuntime]
$characteristicsType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType=WindowsRuntime]
$statusType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType=WindowsRuntime]
$cccdType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattClientCharacteristicConfigurationDescriptorValue, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType=WindowsRuntime]
$writeOptionType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteOption, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType=WindowsRuntime]
$writerType = [Windows.Storage.Streams.DataWriter, Windows.Storage.Streams, ContentType=WindowsRuntime]
$sessionType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Wait-WinRt($Operation, $ResultType) {
    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (-not $task.Wait(10000)) { throw 'Bluetooth operation timed out.' }
    return $task.Result
}

function Invoke-M110Session {
$device = $null; $service = $null; $characteristic = $null; $session = $null
$observer = New-Object GattNotificationObserver
$hotkeyLink = New-Object M110HotkeyLink($TargetHotkeyMode)
$observer.IncludeHeaders = $IncludeHeaders.IsPresent
$observer.DecodeKeyEvents = $DecodeKeyEvents.IsPresent
$observer.DecodeVersionReply = $QueryProtocolVersion.IsPresent
$observer.DecodeCommandReplies = $TestAudioStream.IsPresent
$observer.DecodeAudioHeaders = $TestAudioStream.IsPresent
$observer.OutputSilence = $SilentOutput.IsPresent
$subscribed = $false
$recordingAttempted = $false
$tx = $null
try {
    $selector = $bleType::GetDeviceSelectorFromPairingState($true)
    $devices = Wait-WinRt ($infoType::FindAllAsync($selector)) $collectionType
    $match = @($devices | Where-Object { $_.Name -match '^M110' }) | Select-Object -First 1
    if ($null -eq $match) { throw 'No paired M110 record was found.' }
    $device = Wait-WinRt ($bleType::FromIdAsync($match.Id)) $bleType
    if ($null -eq $device) { throw 'Windows could not open the M110 Bluetooth device.' }
    $session = Wait-WinRt ($sessionType::FromDeviceIdAsync($device.BluetoothDeviceId)) $sessionType
    if ($session.CanMaintainConnection) { $session.MaintainConnection = $true }
    $services = Wait-WinRt ($device.GetGattServicesForUuidAsync([Guid]'0000eae0-0000-1000-8000-00805f9b34fb', $cacheType::Cached)) $servicesType
    if ($services.Status -ne 'Success' -or $services.Services.Count -lt 1) { throw ('M110 service unavailable: ' + $services.Status) }
    $service = $services.Services | Select-Object -First 1
    $chars = Wait-WinRt ($service.GetCharacteristicsForUuidAsync([Guid]'0000eae2-0000-1000-8000-00805f9b34fb', $cacheType::Cached)) $characteristicsType
    if ($chars.Status -ne 'Success' -or $chars.Characteristics.Count -lt 1) { throw ('M110 notification characteristic unavailable: ' + $chars.Status) }
    $characteristic = $chars.Characteristics | Select-Object -First 1
    if ($DecodeAudio) {
        $observer.EnableAudioDecoding()
        if ($OutputDeviceName) { $observer.EnableAudioOutput($OutputDeviceName) }
    }
    $observer.Attach($characteristic)
    $status = Wait-WinRt ($characteristic.WriteClientCharacteristicConfigurationDescriptorAsync($cccdType::Notify)) $statusType
    if ($status -ne 'Success') { throw ('Cannot subscribe to M110 notifications: ' + $status) }
    $subscribed = $true
    Write-Output ('READY: collecting notification lengths for ' + $DurationSeconds + ' seconds')
    if ($QueryProtocolVersion -or $TestAudioStream -or $RunBridge) {
        $writeChars = Wait-WinRt ($service.GetCharacteristicsForUuidAsync([Guid]'0000eae1-0000-1000-8000-00805f9b34fb', $cacheType::Cached)) $characteristicsType
        if ($writeChars.Status -ne 'Success' -or $writeChars.Characteristics.Count -lt 1) { throw ('M110 write characteristic unavailable: ' + $writeChars.Status) }
        $tx = $writeChars.Characteristics | Select-Object -First 1
    }
    if ($QueryProtocolVersion) {
        # The request and CRC32C were verified against the installed assistant.
        $writeStatus = Wait-WinRt ($observer.QueryProtocolVersion($tx)) $statusType
        Write-Output ('Protocol-version query write: ' + $writeStatus)
    }
    if ($TestAudioStream -or $RunBridge) {
        Write-Output 'Connecting: completing the local mouse handshake'
        $null = $observer.InitializeSessionAsync($tx).GetAwaiter().GetResult()
        Write-Output 'Connection handshake verified'
        $observer.AttachDevice($device)
    }
    if ($TestAudioStream) {
        $recordingAttempted = $true
        $null = $observer.StartAudioCheckedAsync($tx).GetAwaiter().GetResult()
        Write-Output 'Audio start acknowledged by the mouse'
        $stopAt = [DateTime]::UtcNow.AddSeconds(3)
    }
    if ($RunBridge) {
        Write-Output ('BRIDGE: key_mode=' + $KeyMode + '; key_id=' + $KeyId + '; target_hotkey=' + $TargetHotkeyMode)
        $script:failures = 0
        if ($StatusFile) { [IO.File]::WriteAllText($StatusFile, 'ready') }
    }
    $end = if ($RunBridge) { $bridgeDeadline } else { [DateTime]::UtcNow.AddSeconds($DurationSeconds) }; $count = 0
    while ([DateTime]::UtcNow -lt $end) {
        if ($RunBridge -and -not $connectionWatcher.IsConnected) { throw 'Mouse disconnected.' }
        $null = $observer.WaitForEvent(1000)
        if ($StopFile -and (Test-Path -LiteralPath $StopFile)) {
            Write-Output 'BRIDGE: stop requested'
            break
        }
        if ($recordingAttempted -and [DateTime]::UtcNow -ge $stopAt) {
            $null = $observer.StopAudioCheckedAsync($tx).GetAwaiter().GetResult()
            if ($RunBridge) {
                Start-Sleep -Milliseconds 180
                $hotkeyLink.Stop()
            }
            Write-Output 'Audio stop acknowledged by the mouse'
            $recordingAttempted = $false
        }
        foreach ($event in $observer.Drain()) {
            $count++
            if ($event -eq 'connection=disconnected') {
                # The UI's device watcher will restart the worker after wake.
                throw 'Mouse disconnected; waiting for the next Bluetooth connection.'
            }
            if ($RunBridge -and $event -match 'key=(\d+) state=(\d+)') {
                $observedId = [int]$Matches[1]; $keyState = [int]$Matches[2]
                $action = Get-M110KeyAction -ObservedId $observedId -State $keyState -Mode $KeyMode -TargetId $KeyId -Recording $recordingAttempted
                if ($action -eq 'start') {
                        $recordingAttempted = $true
                        $hotkeyLink.Start()
                        $null = $observer.StartAudioCheckedAsync($tx).GetAwaiter().GetResult()
                        $stopAt = [DateTime]::UtcNow.AddSeconds(60)
                        Write-Output ('BRIDGE: audio started; key=' + $observedId)
                } elseif ($action -eq 'stop') {
                        $null = $observer.StopAudioCheckedAsync($tx).GetAwaiter().GetResult()
                        Start-Sleep -Milliseconds 180
                        $hotkeyLink.Stop()
                        $recordingAttempted = $false
                        Write-Output ('BRIDGE: audio stopped; key=' + $observedId)
                }
            }
            if ((-not $TestAudioStream -and -not $RunBridge) -or ($TestAudioStream -and ($count -le 8 -or $event -match 'reply_sequence')) -or ($RunBridge -and $event -match 'key=|audio_output_error=')) {
                Write-Output ('EVENT ' + $event)
            }
        }
    }
    Write-Output ('DONE: notifications=' + $count)
    if ($DecodeAudio) { Write-Output ('AUDIO: ' + $observer.AudioSummary()) }
} catch {
    throw
} finally {
    if ($recordingAttempted -and $null -ne $tx) {
        try { Write-Output ('Cleanup audio stop: ' + (Wait-WinRt ($observer.StopAudio($tx)) $statusType)) }
        catch { [Console]::Error.WriteLine('Cleanup audio stop failed: ' + $_.Exception.Message) }
    }
    try { $hotkeyLink.Dispose() } catch { [Console]::Error.WriteLine('Cleanup hotkey release failed: ' + $_.Exception.Message) }
    if ($subscribed -and $null -ne $characteristic) {
        try { $null = Wait-WinRt ($characteristic.WriteClientCharacteristicConfigurationDescriptorAsync($cccdType::None)) $statusType } catch {}
    }
    if ($null -ne $characteristic) { try { $observer.Detach($characteristic) } catch {} }
    if ($DecodeAudio) { try { $observer.CloseAudioDecoding() } catch {} }
    if ($null -ne $service) { $service.Dispose() }
    if ($null -ne $session) { $session.MaintainConnection = $false; $session.Dispose() }
    if ($null -ne $device) { try { $observer.DetachDevice($device) } catch {}; $device.Dispose() }
    $observer.Dispose()
}

}

function Set-BridgeStatus([string]$value) {
    if ($StatusFile) { [IO.File]::WriteAllText($StatusFile, $value) }
}
function Test-BridgeStop {
    return (($StopFile -and (Test-Path -LiteralPath $StopFile)) -or [DateTime]::UtcNow -ge $bridgeDeadline)
}
function Wait-BridgeChange([int]$milliseconds) {
    $until = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    do {
        if (Test-BridgeStop) { return }
        $remaining = [Math]::Max(1,[Math]::Min(1000,($until - [DateTime]::UtcNow).TotalMilliseconds))
        if ($connectionWatcher.WaitForChange([int]$remaining)) { return }
    } while ([DateTime]::UtcNow -lt $until)
}

if (-not $RunBridge) {
    try { Invoke-M110Session } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
    return
}
$bridgeDeadline = if ($DurationSeconds -le 0) { [DateTime]::MaxValue } else { [DateTime]::UtcNow.AddSeconds($DurationSeconds) }
$connectionWatcher = $null
try {
    $connectionWatcher = New-Object M110ConnectionWatcher
    $connectionWatcher.Start()
    $failures = 0
    $connectionEpoch = -1
    Set-BridgeStatus 'waiting'
    while (-not (Test-BridgeStop)) {
        if ($connectionWatcher.ConnectionEpoch -ne $connectionEpoch) {
            $connectionEpoch = $connectionWatcher.ConnectionEpoch
            $failures = 0
        }
        if (-not $connectionWatcher.IsConnected) {
            $failures = 0
            Set-BridgeStatus 'waiting'
            Wait-BridgeChange 1000
            continue
        }
        if ($failures -ge 4) {
            # After three bounded retries, wait for a disconnect/reconnect event.
            Set-BridgeStatus 'waiting'
            Wait-BridgeChange 1000
            continue
        }
        Set-BridgeStatus 'connecting'
        while ($connectionWatcher.WaitForChange(0)) { }
        try {
            Invoke-M110Session
            $failures = 0
        } catch {
            [Console]::Error.WriteLine('Connection ended: ' + $_.Exception.Message)
            $failures++
        }
        if (Test-BridgeStop) { break }
        Set-BridgeStatus 'waiting'
        if ($connectionWatcher.IsConnected -and $failures -gt 0 -and $failures -lt 4) {
            Wait-BridgeChange (@(1000,3000,8000)[$failures-1])
        }
    }
} catch {
    Set-BridgeStatus 'error'
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($null -ne $connectionWatcher) { $connectionWatcher.Dispose() }
}
