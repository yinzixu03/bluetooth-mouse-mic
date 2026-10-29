param([string]$SyntheticPacketPath = (Join-Path $PSScriptRoot 'codec-vectors.json'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -Path (Join-Path $PSScriptRoot '..\support\GattNotificationObserver.dll')
$codecType = [GattNotificationObserver].GetNestedType('PcmCodec',[Reflection.BindingFlags]::NonPublic)
$codec = [Activator]::CreateInstance($codecType,$true)
$decode = $codecType.GetMethod('Decode')
$groups = @{}
$count = 0
try {
    foreach ($packet in (Get-Content -LiteralPath $SyntheticPacketPath -Raw | ConvertFrom-Json)) {
        $parameters = [object[]]@([Convert]::FromBase64String($packet.Encoded),$null)
        if (-not $decode.Invoke($codec,$parameters)) { throw 'Decoder rejected a valid synthetic frame.' }
        $samples = [int16[]]$parameters[1]
        if ($samples.Length -ne 320) { throw 'Wrong sample count.' }
        $expectedBytes = [Convert]::FromBase64String($packet.ReferencePcm)
        $expected = New-Object int16[] 320
        [Buffer]::BlockCopy($expectedBytes,0,$expected,0,$expectedBytes.Length)
        if (-not $groups.ContainsKey($packet.Signal)) {
            $groups[$packet.Signal] = @{ xx=0.0; yy=0.0; xy=0.0; peak=0; frames=0 }
        }
        $group = $groups[$packet.Signal]
        for ($i=0; $i -lt 320; $i++) {
            $x = [double]$samples[$i]; $y = [double]$expected[$i]
            $group.xx += $x*$x; $group.yy += $y*$y; $group.xy += $x*$y
            $group.peak = [Math]::Max($group.peak,[Math]::Abs([int]$samples[$i]))
        }
        $group.frames++; $count++
    }
    foreach ($label in $groups.Keys) {
        $group = $groups[$label]
        if ($group.frames -ne 25) { throw ('Incomplete signal group: ' + $label) }
        if ($label -eq 'silence') {
            if ($group.peak -gt 8) { throw 'Silence became audible.' }
            continue
        }
        $correlation = $group.xy / [Math]::Sqrt($group.xx*$group.yy)
        $gain = [Math]::Sqrt($group.xx/$group.yy)
        $minimum = if ($label -eq 'deterministic-noise') { 0.90 } else { 0.999 }
        if ($correlation -lt $minimum -or $gain -lt 0.95 -or $gain -gt 1.05) {
            throw ('Waveform or volume check failed: ' + $label)
        }
        Write-Output ('PASS: {0}, correlation={1:F6}, volume ratio={2:F6}' -f $label,$correlation,$gain)
    }
    foreach ($length in @(0,1,39,41,80)) {
        $parameters = [object[]]@([byte[]]::new($length),$null)
        if ($decode.Invoke($codec,$parameters)) { throw 'Wrong-length packet was accepted.' }
    }
    $parameters = [object[]]@($null,$null)
    if ($decode.Invoke($codec,$parameters)) { throw 'Null packet was accepted.' }
} finally { $codec.Dispose() }
$parameters = [object[]]@([byte[]]::new(40),$null)
if ($decode.Invoke($codec,$parameters)) { throw 'Disposed codec accepted input.' }
if ($count -ne 100 -or $groups.Count -ne 4) { throw 'Expected 100 synthetic frames in four signal groups.' }
Write-Output 'PASS: 100 frames / 32000 samples. No Bluetooth, microphone, vendor DLL or keyboard input used.'
