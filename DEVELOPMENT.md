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

`src/VsMcp.Bridge` is a net10.0 stdio server using ModelContextProtocol.Core, published self-contained for win-x64. The VSIX publishes it without referencing its net10 assembly and packages the publish output, including the .NET runtime, under `Bridge/`. End users need Windows x64 and VS 2026; they do not need to install .NET 10 separately. Building still requires the .NET 10 SDK. On startup the extension deploys the bridge files to `%LOCALAPPDATA%\VsMcp\Bridge`; a shared process lease prevents updates while a deployed bridge is running. The SDK-generated `src/VsMcp.Bridge/tools.json` catalog is embedded for offline tool discovery. New bridge control tools are attributed methods; IDE tools preserve their original schemas and forward SDK request/result objects. Rebuild and release with a serviced SDK to update the bundled runtime; machine-wide .NET updates do not replace it. See [self-contained deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

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

The catalog instantiates the production provider without automating an IDE. It checks unique tool names, descriptions/annotations and camelCase parameters. The live `ToolReferenceTests` checks `src/VsMcp.Bridge/tools.json` against `tools/list`; set `VS_MCP_UPDATE_TOOL_CATALOG=1` to regenerate it from the IDE, then rebuild the bridge to embed the updated catalog. Keep the English `README.md` and Traditional Chinese `README.zh-TW.md` aligned with the tools. Bridge tests cover offline stdio discovery, explicit selection between instances, PID/registration and endpoint reuse, schema and identity mismatches, in-flight binding protection, interrupted mutation recovery and deployment leases. The stdio test launches the actual apphost with invalid `DOTNET_ROOT` settings and checks host tracing for its app-local `coreclr.dll`. The live bridge test additionally checks the deployed runtime files and self-contained runtime configuration.

After building the Release package:

```powershell
dotnet test tests/VsMcp.IntegrationTests
```

Integration tests install into the experimental hive, copy fixtures to isolated temporary directories, launch on port 18772 and verify the loaded DLL's version/hash before exercising tools. Installation reuse also compares the packaged redirects and bridge files, so a runtime-only packaging change cannot reuse a stale installation. They close only their owned test instances. `VS_MCP_VS_ROOT` selects the VS installation; `VS_MCP_TEST_PORT` overrides the endpoint port. Keep other experimental instances closed during installation. Normal VS profiles are not used.

The bridge integration test additionally starts two owned experimental instances with automatic port selection. It verifies distinct endpoints, stdio discovery and binding, solution-close isolation and continued bridge availability after one VS exits.

Coverage includes solution/configuration queries; dependency-aware builds, failure and cancellation; unsaved C++/C# buffer edits and stale-write rejection; managed unsaved semantic queries; native definition locations; native and managed VSTest results; filtering/no-tests/recovery/cancellation; and managed/native debugger inspection. The managed test fixture intentionally includes failing and slow tests for result/cancellation checks. The native fixture includes an intentionally failing test too.

Never put a solution argument after `devenv.exe /log`; it can be interpreted as the log output filename. Use `devenv.exe <solution> /rootsuffix Exp /log`.

On 2026-11-10 the compatibility and self-contained packaging changes passed a Release VSIX build, the protocol harness, the 51-tool catalog check, 25 unit tests and 11 bridge tests. The VSIX contains 195 published bridge/runtime files, including .NET 10.0.14. All 19 targeted experimental-hive tests passed on VS 2026 Community 18.11.12328.437: package identity, host assembly policy, live schemas, project unload/reload with unchanged project bytes, and stdio control of two real instances using the deployed self-contained bridge. An isolated .NET Framework probe also passed the catalog and protocol harness using actual 10.0.0.3 shared assemblies. VS 2026 Professional 18.6.11822.322 is unavailable here and remains unverified as a complete host.

The full final integration run passed 46 of 49 tests, not the entire suite. Three debugger tests remain failing: Ctrl+F5 inspection reads snake_case properties although results are camelCase; managed debugger inspection fails at `startup_project_set` after the Ctrl+F5 failure; native inspection expects the text `Thread ID` but receives the SDK's generic tool error. These debugger failures were left outside this installation/assembly-loading change. Company-machine development/build compatibility is also deferred at the user's request.

## Dependencies

The extension targets net472 using Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 18.6.38345 and ModelContextProtocol.Core 2.3.0. Roslyn and VisualStudio.LanguageServices are supplied by the host and are not shipped. The VSIX also excludes the nine shared .NET 10 assemblies listed in `HostAssemblyResolver` and `ExcludeHostAssembliesFromVsix`; keep these lists aligned. The package registers the resolver before starting the SDK. For a failed bind, it validates the assembly name, token, culture and major version, then applies the running IDE's binding policy to the 10.0.0.0 identity. This lets VS choose its installed patch version without changing process-wide redirects. It does not retry failed host binds and rejects identifiable callers outside the extension directory. CLR JIT binds can omit the requesting assembly, so those requests are accepted for the allowlisted identities; this fallback is process-wide when the caller is unknown. [The .NET Framework resolution event supports returning a different assembly version.](https://learn.microsoft.com/en-us/dotnet/framework/app-domains/resolve-assembly-loads)

`VsMcp.Redirects.pkgdef` only redirects `System.Net.ServerSentEvents`, which VS does not supply, to the copy bundled with the SDK. The separately running .NET 10 bridge retains its own dependencies. After dependency or host upgrades, run the harness, catalog check and experimental-hive tests; the standalone harness cannot establish host compatibility. Resolver unit cases cover policies selecting 10.0.0.3 and 10.0.0.10, and the live server test compares loaded shared identities with the IDE's actual policy. These checks do not prove that all APIs used by a newer SDK exist in every older VS patch.

Compiler/analyzer dependency warnings inherited from the SDK/host combination remain visible; passing compilation alone does not establish runtime compatibility.

## Earlier release validation

An earlier 0.4.0 build was installed into Visual Studio 2026 Community's experimental hive (18.11.12328.437). At that baseline, all 45 integration tests, 11 bridge tests and 9 unit tests passed, along with the HTTP harness and live SDK catalog comparison. Both native C++ and managed C# debugger inspection were exercised against real processes, along with explicit stdio selection between two running VS instances using automatic ports. The managed debug fixture targets .NET 10 and requires the development SDK/runtime, independently of the bridge's self-contained deployment.

C++ source-definition queries were verified with the installed native provider. C++ References remains provider-dependent and is not claimed as universally available; missing capabilities fail explicitly.
