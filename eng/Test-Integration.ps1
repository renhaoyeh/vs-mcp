param(
    [string]$RootSuffix = 'VsMcpTests',
    [string]$Filter = 'FullyQualifiedName~ServerTests|FullyQualifiedName~BridgeMultiInstanceTests'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$setup = Join-Path $projectRoot 'artifacts/bridge/VsMcp.Bridge.Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw 'Build Bridge Setup first.' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('vs-mcp-release-test-' + [guid]::NewGuid().ToString('N'))
$previousBridgeRoot = $env:VS_MCP_TEST_BRIDGE_ROOT
$previousSuffix = $env:VS_MCP_TEST_ROOT_SUFFIX
try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/TestInstall=1', "/DIR=`"$testRoot`"", "/LOG=`"$testRoot.log`"")
    $installation = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($installation.ExitCode -ne 0) { throw "Isolated Bridge Setup failed ($($installation.ExitCode)); see $testRoot.log" }
    $env:VS_MCP_TEST_BRIDGE_ROOT = $testRoot
    $env:VS_MCP_TEST_ROOT_SUFFIX = $RootSuffix
    & dotnet test tests/VsMcp.IntegrationTests -c Release --filter $Filter --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Experimental-hive integration tests failed.' }
} finally {
    $env:VS_MCP_TEST_BRIDGE_ROOT = $previousBridgeRoot
    $env:VS_MCP_TEST_ROOT_SUFFIX = $previousSuffix
    # Resolve and verify the exact owned temporary root before recursive cleanup.
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $resolvedTestRoot -Parent) -ne $expectedParent -or (Split-Path $resolvedTestRoot -Leaf) -notlike 'vs-mcp-release-test-*') {
        throw "Refusing cleanup outside the owned test root: $resolvedTestRoot"
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
