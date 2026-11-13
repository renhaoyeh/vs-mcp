$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $projectRoot 'src/VsMcp.Bridge/VsMcp.Bridge.csproj') -Raw
$version = $project.Project.PropertyGroup.Version
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $projectRoot 'src/VsMcp.Vsix/bin/Release/net472/VsMcp.vsix'))
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.SelectSingleNode('//*[local-name()="Identity"]').GetAttribute('Version') -ne $version) {
        throw 'VSIX version does not match the release version.'
    }
} finally { $archive.Dispose() }
$release = Join-Path $projectRoot "artifacts/release/$version"
New-Item -ItemType Directory -Force -Path $release | Out-Null
foreach ($relative in 'src/VsMcp.Vsix/bin/Release/net472/VsMcp.vsix', 'artifacts/bridge/VsMcp.Bridge.Setup.exe') {
    $source = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing release asset: $source" }
    if ((Get-Item -LiteralPath $source).Extension -eq '.exe' -and (Get-Item -LiteralPath $source).VersionInfo.ProductVersion.Trim() -ne $version) {
        throw 'Bridge Setup version does not match the release version.'
    }
    Copy-Item -LiteralPath $source -Destination $release -Force
}
$checksums = foreach ($name in 'VsMcp.vsix', 'VsMcp.Bridge.Setup.exe') {
    '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $release $name)).Hash.ToLowerInvariant(), $name
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $projectRoot "docs/releases/$version.md") -Destination (Join-Path $release 'RELEASE-NOTES.md') -Force
Write-Output "Release assets: $release"
