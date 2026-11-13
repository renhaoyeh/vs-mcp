param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Iscc = $env:ISCC_EXE,
    [switch]$IncludeUpgradeTestFixture
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 7\ISCC.exe') |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) { throw 'Install Inno Setup 6.7+ and set ISCC_EXE to its ISCC.exe.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -version '[18.0,19.0)' -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio 2026 MSBuild with desktop C++ tools is required.' }
[xml]$bridgeProject = Get-Content src/VsMcp.Bridge/VsMcp.Bridge.csproj -Raw
$version = $bridgeProject.Project.PropertyGroup.Version
& $msbuild src/VsMcp.Launcher/VsMcp.Launcher.vcxproj "-p:Configuration=$Configuration" -p:Platform=x64 -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
# Use a fresh publish directory so a framework-dependent or stale file can never enter Setup.
$output = Join-Path $projectRoot 'artifacts/bridge'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$stage = Join-Path $output ([guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
& dotnet publish src/VsMcp.Bridge/VsMcp.Bridge.csproj "-c:$Configuration" -r win-x64 --self-contained true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Self-contained Bridge publish failed.' }
Get-ChildItem -LiteralPath $payload -Filter '*.pdb' | Remove-Item
$runtime = Get-Content (Join-Path $payload 'VsMcp.Bridge.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or -not $runtime.runtimeOptions.includedFrameworks) { throw 'Bridge publish is not self-contained.' }
foreach ($file in 'VsMcp.Bridge.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Missing self-contained payload file: $file" }
}
$manifest = Join-Path $stage 'payload.sha256'
$lines = Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1)
    '{0}|{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllLines($manifest, [string[]]$lines, [Text.UTF8Encoding]::new($false))
$buildId = "$version-$((Get-FileHash -LiteralPath $manifest).Hash.Substring(0,16).ToLowerInvariant())"
$launcher = Join-Path $projectRoot "src/VsMcp.Launcher/bin/$Configuration/VsMcp.Launcher.exe"
$launcherHash = (Get-FileHash -LiteralPath $launcher).Hash.ToLowerInvariant()
& $Iscc /Q "/DAppVersion=$version" "/DBuildId=$buildId" "/DLauncher=$launcher" "/DLauncherSha256=$launcherHash" "/DPayload=$payload" "/DManifest=$manifest" "/DOutputDir=$output" installer/Bridge.iss
if ($LASTEXITCODE -ne 0) { throw 'Bridge Setup compilation failed.' }
if ($IncludeUpgradeTestFixture) {
    & $Iscc /Q "/DAppVersion=$version" "/DBuildId=$buildId-testupgrade" "/DLauncher=$launcher" "/DLauncherSha256=$launcherHash" "/DPayload=$payload" "/DManifest=$manifest" "/DOutputDir=$output/test-upgrade" installer/Bridge.iss
    if ($LASTEXITCODE -ne 0) { throw 'Upgrade test fixture compilation failed.' }
}
Write-Output "Bridge Setup: $output/VsMcp.Bridge.Setup.exe"
