$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('.build\ui-lifecycle-'+[Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $fixture 'support') -Force
Copy-Item -LiteralPath (Join-Path $root 'M110Mic.exe') -Destination (Join-Path $fixture 'M110Mic.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fake_worker.ps1') -Destination (Join-Path $fixture 'support\monitor_key_events.ps1')
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /reference:System.Windows.Forms.dll ('/out:'+(Join-Path $fixture 'UiLifecycleSmoke.exe')) (Join-Path $PSScriptRoot 'UiLifecycleSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& (Join-Path $fixture 'UiLifecycleSmoke.exe') (Join-Path $fixture 'M110Mic.exe')
if ($LASTEXITCODE -ne 0) { throw 'UI lifecycle check failed.' }
