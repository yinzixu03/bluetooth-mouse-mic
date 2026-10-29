$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'dist'
$null = New-Item -ItemType Directory -Path $output -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Zip([string]$Path,[System.Collections.IDictionary]$Entries) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Create)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($entry in $Entries.GetEnumerator()) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$entry.Value,$entry.Key,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
$sources = [ordered]@{}
foreach ($name in @('README.md','LICENSE','.gitignore')) { $sources[$name] = Join-Path $root $name }
foreach ($folder in @('src','tools','tests','licenses')) {
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse | Sort-Object FullName)) {
        if ($file.Extension -in @('.cs','.c','.h','.ps1','.md','.json') -or $file.Name -in @('COPYING','AUTHORS')) {
            $sources[$file.FullName.Substring($root.Length+1).Replace('\','/')] = $file.FullName
        }
    }
}
$sourceZip = Join-Path $output 'm110-mic-source.zip'
Write-Zip $sourceZip $sources
$runtime = [ordered]@{
    'M110Mic.exe' = (Join-Path $root 'M110Mic.exe')
    'README.md' = (Join-Path $root 'README.md')
    'support/GattNotificationObserver.dll' = (Join-Path $root 'support\GattNotificationObserver.dll')
    'support/m110_g7221.dll' = (Join-Path $root 'support\m110_g7221.dll')
    'support/monitor_key_events.ps1' = (Join-Path $root 'support\monitor_key_events.ps1')
    'support/LICENSE.MIT' = (Join-Path $root 'LICENSE')
    'support/LICENSE.LGPL' = (Join-Path $root 'src\codec\COPYING')
    'support/THIRD_PARTY_NOTICES.md' = (Join-Path $root 'licenses\THIRD_PARTY_NOTICES.md')
    'support/source.zip' = $sourceZip
}
Write-Zip (Join-Path $output 'm110-mic-windows-x64.zip') $runtime
Get-ChildItem -LiteralPath $output -Filter '*.zip' | Select-Object Name,Length
