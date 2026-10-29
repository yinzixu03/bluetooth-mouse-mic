param([string]$ZigPath = 'zig', [switch]$CodecOnly, [switch]$SkipCodec)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src'
$support = Join-Path $root 'support'
$cache = Join-Path $root '.build'
$null = New-Item -ItemType Directory -Path $support,$cache -Force
$previousGlobal = $env:ZIG_GLOBAL_CACHE_DIR
$previousLocal = $env:ZIG_LOCAL_CACHE_DIR
try {
    if (-not $SkipCodec) {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-global-cache'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-local-cache'
    $files = @('common.c','huffman.c','dct4.c','rmlt.c','decoder.c') | ForEach-Object { Join-Path $source ('codec\'+$_) }
    & $ZigPath @(@('cc','-shared','-target','x86_64-windows-gnu','-O2','-fwrapv',
        '-Wl,--export-all-symbols','-o',(Join-Path $support 'm110_g7221.dll')) + $files)
    if ($LASTEXITCODE -ne 0) { throw 'Codec compilation failed.' }
    }
} finally {
    $env:ZIG_GLOBAL_CACHE_DIR = $previousGlobal
    $env:ZIG_LOCAL_CACHE_DIR = $previousLocal
}
if ($CodecOnly) { return }
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$metadata = Join-Path $env:SystemRoot 'System32\WinMetadata'
$compiler = Join-Path $framework 'csc.exe'
$arguments = @('/nologo','/target:library',('/out:'+(Join-Path $support 'GattNotificationObserver.dll')))
foreach ($name in @('Windows.Foundation.winmd','Windows.Devices.winmd','Windows.Storage.winmd')) { $arguments += '/reference:'+(Join-Path $metadata $name) }
foreach ($name in @('System.Runtime.dll','System.Runtime.InteropServices.WindowsRuntime.dll')) { $arguments += '/reference:'+(Join-Path $framework $name) }
$arguments += @((Join-Path $source 'GattNotificationObserver.cs'),(Join-Path $source 'M110HotkeyLink.cs'),(Join-Path $source 'M110ConnectionWatcher.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    ('/win32icon:'+(Join-Path $source 'm110.ico')) `
    ('/out:'+(Join-Path $root 'M110Mic.exe')) (Join-Path $source 'M110MicApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI compilation failed.' }
Copy-Item -LiteralPath (Join-Path $source 'monitor_key_events.ps1') -Destination (Join-Path $support 'monitor_key_events.ps1') -Force
Write-Output 'Build complete. Run M110Mic.exe.'
