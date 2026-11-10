# Development

## Architecture

```text
VsMcpPackage                         composition and extension lifetime
  Mcp/                              HTTP transport, official MCP SDK, protocol errors/serialization
  Tools/                            MCP names, descriptions, schemas and request adapters
  Contracts/                        typed JSON inputs/results shared with tests
  Services/
    VisualStudio/                   connected DTE context, instance identity and shared solution queries
    Build/                          build orchestration, dependency ordering and build events
    Documents/                      unsaved buffer reads, hash checks and undo transactions
    Debugging/                      asynchronous launch/stop lifecycle and session identity
    Navigation/                     language routing; Roslyn managed queries; native Peek queries
    Testing/                        run IDs, bounded history, waiting/cancellation, TRX interpretation
    Execution/                      advanced C# snippet execution
  Infrastructure/                   VSTest process arguments, output capture and owned Windows jobs
```

The provider is the composition root for tools/services. New document/navigation/test tools return typed `ToolResponse<T>` values with host identity and SDK-generated output schemas. Testing contracts, orchestration and TRX parsing do not depend on DTE or MCP. `ITestExecutor` isolates process execution so lifecycle/error/cancellation behavior is testable without an IDE. The production executor resolves VSTest from the hosting VS installation, never another instance.

DTE and native editor access enter the UI thread. Managed semantic work runs against immutable workspace snapshots off the UI thread. Native queries call content-type-specific Peek providers without starting a displayed Peek session. No language backend substitutes textual matches for semantic results. Unsupported native relationships return `NOT_SUPPORTED`.

Existing solution/build/debug services retain their established instance/error contracts. Lightweight diagnostic and settings adapters directly call DTE on the UI thread. Build tools delegate orchestration to `BuildService`; build/debug event runners live under their service domains rather than in the MCP adapter folder.

## Build and package

Requirements: Windows, Visual Studio 2026 with C#/.NET, desktop C++ and extension-development workloads; .NET 10 SDK and .NET Framework 4.7.2 targeting pack.

Open `VsMcp.slnx` in Visual Studio, select Release and build `VsMcp.Vsix`, or run in Developer PowerShell for VS 2026:

```powershell
MSBuild src/VsMcp.Vsix/VsMcp.Vsix.csproj -restore -p:Configuration=Release
```

The VSIX is written to `src/VsMcp.Vsix/bin/Release/net472/VsMcp.vsix`. Packaging requires Visual Studio MSBuild; `dotnet build` cannot create the VSIX container. Command-line builds do not install. F5 inside Visual Studio deploys Debug to the experimental hive. The native test fixtures target v145/x64.

`src/VsMcp.Bridge` is a net10.0 stdio server using ModelContextProtocol.Core. The VSIX builds it as an executable dependency without referencing its net10 assembly, and packages its files under `Bridge/`. On startup the extension deploys them to `%LOCALAPPDATA%\VsMcp\Bridge`; a shared process lease prevents updates while a deployed bridge is running. The SDK-generated `src/VsMcp.Bridge/tools.json` catalog is embedded for offline tool discovery. New bridge control tools are attributed methods; IDE tools preserve their original schemas and forward SDK request/result objects.

Each VS publishes an atomic per-process registration under `%LOCALAPPDATA%\VsMcp\instances`, containing PID, process start time, endpoint port and a unique instanceId. Discovery checks process lifetime and queries `get_vs_info` on the UI thread through MCP. A bridge binds only through `connect_vs_instance(pid, instanceId)` after verifying live identity and catalog compatibility. It prevents rebinding while calls are active and attaches the instanceId to HTTP requests; the IDE rejects mismatches before dispatch. Each subsequent tool call creates a fresh SDK HTTP connection to the pinned instance. Calls are never retried automatically, and transport failures after dispatch report an unknown outcome. A restarted process requires explicit rebinding. Default-port collisions choose a free port; an explicit `VS_MCP_PORT` remains strict.

The `Release VSIX` GitHub Actions workflow builds the Release package, checks the embedded IDE tool catalog, runs unit and bridge tests and the protocol harness, and uploads a `VsMcp-vsix` artifact. Run it manually from Actions, or push a tag such as `v0.4.0` to publish the VSIX on GitHub Releases. The tag must match both the project version and VSIX manifest version; update those files before tagging a new version. Interactive experimental-instance integration tests remain a local validation step.

## Validation

```powershell
dotnet test tests/VsMcp.UnitTests
dotnet test tests/VsMcp.BridgeTests
dotnet build tests/VsMcp.Harness
& tests/VsMcp.Harness/bin/Debug/net472/VsMcp.Harness.exe
```

Unit tests cover TRX parsing and failure details, bounded result selection, missing/malformed reports, DTD rejection, no-tests classification, interrupted waits, recovery of run identity, cancellation isolation and Windows argument quoting. The protocol harness uses production HTTP/SDK code with fake tools; it cannot validate VS assembly loading.

Build `tests/VsMcp.ToolCatalog/VsMcp.ToolCatalog.csproj` using VS MSBuild, then:

```powershell
& tests/VsMcp.ToolCatalog/bin/Release/net472/VsMcp.ToolCatalog.exe src/VsMcp.Bridge/tools.json
& tests/VsMcp.ToolCatalog/bin/Release/net472/VsMcp.ToolCatalog.exe src/VsMcp.Bridge/tools.json --check
```

The catalog instantiates the production provider without automating an IDE. It checks unique tool names, descriptions/annotations and camelCase parameters. The live `ToolReferenceTests` checks `src/VsMcp.Bridge/tools.json` against `tools/list`; set `VS_MCP_UPDATE_TOOL_CATALOG=1` to regenerate it from the IDE, then rebuild the bridge to embed the updated catalog. Keep the English `README.md` and Traditional Chinese `README.zh-TW.md` aligned with the tools. Bridge tests cover offline stdio discovery, explicit selection between instances, PID/registration and endpoint reuse, schema and identity mismatches, in-flight binding protection, interrupted mutation recovery and deployment leases.

After building the Release package:

```powershell
dotnet test tests/VsMcp.IntegrationTests
```

Integration tests install into the experimental hive, copy fixtures to isolated temporary directories, launch on port 18772 and verify the loaded DLL's version/hash before exercising tools. They close only their owned test instances. `VS_MCP_VS_ROOT` selects the VS installation; `VS_MCP_TEST_PORT` overrides the endpoint port. Keep other experimental instances closed during installation. Normal VS profiles are not used.

The bridge integration test additionally starts two owned experimental instances with automatic port selection. It verifies distinct endpoints, stdio discovery and binding, solution-close isolation and continued bridge availability after one VS exits.

Coverage includes solution/configuration queries; dependency-aware builds, failure and cancellation; unsaved C++/C# buffer edits and stale-write rejection; managed unsaved semantic queries; native definition locations; native and managed VSTest results; filtering/no-tests/recovery/cancellation; and managed/native debugger inspection. The managed test fixture intentionally includes failing and slow tests for result/cancellation checks. The native fixture includes an intentionally failing test too.

Never put a solution argument after `devenv.exe /log`; it can be interpreted as the log output filename. Use `devenv.exe <solution> /rootsuffix Exp /log`.

The Ctrl+F5 reconciliation and debugger thread/frame selection changes were validated locally with 13 unit tests, 11 bridge tests, the protocol harness, the 49-tool SDK catalog check and a Release VSIX build. Both updated C# and C++ debug fixtures compiled. Experimental-hive integration was attempted on 2026-11-10 but failed during fixture initialization: VS reported `SetSite failed for package [VsMcpPackage]` (`0x80070002`) and no MCP endpoint appeared within 120 seconds. Thread/frame selection and reconciliation therefore remain unverified in a live IDE in this environment; this is not an integration pass.

## Dependencies

The extension targets net472 using Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 18.6.38345 and ModelContextProtocol.Core 2.3.0. Roslyn and VisualStudio.LanguageServices are supplied by the host and are not shipped. `VsMcp.Redirects.pkgdef` reconciles the SDK's 10.0.0.13 dependencies with this VS host's 10.0.0.10 assemblies. Recheck actual versions/tokens in devenv.exe.config after either dependency or host upgrades, then run the harness and real experimental-instance tests.

Compiler/analyzer dependency warnings inherited from the SDK/host combination remain visible; passing compilation alone does not establish runtime compatibility.

## Verified release

Version 0.4.0 was built and installed into Visual Studio 2026 Community's experimental hive (18.11.12328.437). All 45 integration tests, 11 bridge tests and 9 unit tests passed, along with the HTTP harness and live SDK catalog comparison. Both native C++ and managed C# debugger inspection were exercised against real processes, along with explicit stdio selection between two running VS instances using automatic ports. The managed debug fixture targets .NET 10 so it runs with the declared SDK/runtime prerequisites.

C++ source-definition queries were verified with the installed native provider. C++ References remains provider-dependent and is not claimed as universally available; missing capabilities fail explicitly.
