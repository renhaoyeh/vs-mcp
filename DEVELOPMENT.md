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

The `Release VSIX` GitHub Actions workflow builds the Release package, checks the embedded IDE tool catalog, runs unit and bridge tests and the protocol harness, and uploads a `VsMcp-vsix` artifact. Run it manually from Actions, or push a tag such as `v0.5.0` to publish the VSIX on GitHub Releases. The tag must match both the project version and VSIX manifest version; update those files before tagging a new version. Experimental-instance integration tests remain a local validation step.

## Validation

The build settings tools expose ten typed preferences through the host's registered Unified Settings API and C++ DTE automation, with UI-thread access, idle checks, support reasons, preflight validation and previous/read-back snapshots. Project parallelism falls back to the actual DTE property `ConcurrentBuilds` when Unified Settings is unavailable. The stop-after-first-failure preference is discovered only from a unique boolean registration in the running host's Build and Run schema; absent/ambiguous registrations are unsupported, never written under a guessed key. Native VS approval can remain pending, and updates across providers are not transactional. Tests restore preferences in `finally`.

Six diagnostic tools use only VS debugger APIs: opening/saving dumps, module and observed-exception queries, and PDB preference reads/updates. Dump launch goes directly through `IVsDebugger4`, with a verified minidump file lease held until launch returns; it never invokes F5 or changes startup projects. Dump requests share the existing pending-launch lifecycle and status. Module reads use observed AD7 programs with an exact current `Process3.Modules` fallback for dump engines; unavailable metadata remains null. The passive `IDebugEventCallback2` observer supports numeric and GUID process identities and never consumes stopping events, resumes execution or evaluates code. All DTE reads run on the UI thread. Stack frames include symbol-provided source paths and one-based lines. WER/event-log discovery and external cdb analysis are outside this scope.

On 2026-11-11, the Release VSIX, generated 64-tool IDE catalog/check (66 including bridge controls), protocol harness, 28 unit tests and 11 bridge tests passed on VS 2026 Professional 18.6.11822.322. The full 64-test integration run passed 62; a stale expected tool list was corrected and its failed test then passed in a targeted rerun, giving 63 passing cases. The remaining real-stdio deployment case is blocked: an existing bridge client holds the shared `.bridge.lock`, so VS correctly refuses to replace the older deployed framework-dependent bridge, which lacks `hostfxr.dll`. The packaged bridge contains its runtime and the standalone stdio tests pass. Existing clients and the normal VS session were preserved.

Live validation covers all supported setting updates/restoration, rejection of unsupported patches before writes, unchanged configuration/startup projects, native dump save/open with PDB/source locations and module inspection, and observing access violation `0xC0000005` both live and after reopening the saved dump. Existing managed/native debugger, navigation, editor and testing cases passed. VS 18.11-only settings, other dump-engine GUIDs, full-memory dumps and the native settings pending-approval path remain unverified. The original Exp hive was blocked by a pre-existing extension; validation used a separate `VsMcpTests` hive. The first launch of that new hive timed out during initialization; subsequent owned sessions loaded and verified the package successfully.

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

Integration tests install into the experimental hive, copy fixtures to isolated temporary directories, launch on port 18772 and verify the loaded DLL's version/hash before exercising tools. Installation reuse also compares the packaged redirects and bridge files, so a runtime-only packaging change cannot reuse a stale installation. They close only their owned test instances. `VS_MCP_VS_ROOT` selects the VS installation; `VS_MCP_TEST_PORT` overrides the endpoint port; `VS_MCP_TEST_ROOT_SUFFIX` selects an alphanumeric experimental suffix (default `Exp`, e.g. `VsMcpTests` to preserve an existing developer hive). Keep other experimental instances closed during installation. Normal VS profiles are not used.

Installers, configuration helpers and experimental VS instances start on a private Win32 desktop that is never switched to the user's input desktop. After checking the launched process, package and window desktop, the harness calls `show_vs_window(visible: false)`. Debugger activation, breakpoints and the visibility test stay on that private desktop; debuggee process desktops are also checked. Isolation failures stop the tests without falling back to the user's desktop. Ordinary IDE/F5 behavior is unchanged. Build the VSIX before running integration tests; a concurrent package/bridge build can invalidate the running package's hash checks.

The bridge integration test additionally starts two owned experimental instances with automatic port selection. It verifies distinct endpoints, stdio discovery and binding, solution-close isolation and continued bridge availability after one VS exits.

Coverage includes solution/configuration queries; dependency-aware builds, failure and cancellation; unsaved C++/C# buffer edits and stale-write rejection; managed unsaved semantic queries; native definition locations; native and managed VSTest results; filtering/no-tests/recovery/cancellation; and managed/native debugger inspection. The managed test fixture intentionally includes failing and slow tests for result/cancellation checks. The native fixture includes an intentionally failing test too.

Never put a solution argument after `devenv.exe /log`; it can be interpreted as the log output filename. Use `devenv.exe <solution> /rootsuffix Exp /log`.

On 2026-11-10 the compatibility and self-contained packaging changes passed a Release VSIX build, the protocol harness, the 51-tool catalog check, 25 unit tests and 11 bridge tests. The VSIX contains 195 published bridge/runtime files, including .NET 10.0.14. All 19 targeted experimental-hive tests passed on VS 2026 Community 18.11.12328.437: package identity, host assembly policy, live schemas, project unload/reload with unchanged project bytes, and stdio control of two real instances using the deployed self-contained bridge. An isolated .NET Framework probe also passed the catalog and protocol harness using actual 10.0.0.3 shared assemblies. VS 2026 Professional 18.6.11822.322 is unavailable here and remains unverified as a complete host.

The debugger fixes passed all 49 integration tests on that host, including managed/native thread and frame selection, stepping, resume/break and Ctrl+F5 reconciliation. Managed launch confirmation now resolves the apphost through the active configuration's `OutputPath`: SDK-style Built output groups can point into `obj`, not the runnable output directory. The debug fixture uses a non-default output directory to exercise this. Frame selection and `isCurrent` use `EnvDTE90a.StackFrame2.Depth` within the same paused process/thread context, since enumerated and selected frames can have different COM identities. Expected selection/reconciliation argument failures use `McpToolException` so the SDK returns actionable `INVALID_ARGUMENT` messages. Tests and descriptions use the actual camelCase result fields; observed test processes are cleaned up in `finally`, and the fixture breakpoint is disabled before the resume check to prevent another stop at that breakpoint.

The Release VSIX build, 51-tool catalog check, protocol harness, 25 unit tests and 11 bridge tests also passed. Local Debug MSBuild/VSSDK deployment and endpoint startup were verified on 18.11, but attaching a parent Visual Studio debugger through F5 and company-machine 18.6 development/build remain unverified.

The `show_vs_window` addition passed 13 targeted experimental-hive checks, the Release VSIX build, the 52-tool catalog check, the protocol harness, 25 unit tests and 11 bridge tests. The bridge exposes 54 tools including instance management. Visibility uses Win32 `ShowWindow` (`SW_HIDE` / `SW_SHOWNA`) after verifying the DTE main HWND belongs to the current VS process, with `IsWindowVisible` readback. DTE refuses to hide a user-controlled IDE; this implementation preserves `UserControl`. The live test independently checks native visibility and UI-thread responsiveness while hidden. Activation is explicit and `activationRequested` does not prove foreground focus. Automatic debug-start activation is unchanged; the full debugger suite was not rerun for this addition.

The private-desktop test harness subsequently passed all 50 experimental-hive tests on 18.11, including managed/native debugging and two real bridge instances, plus 26 unit tests, 11 bridge tests, the protocol harness, the 52-tool catalog check and a Release VSIX build. The added launch check verifies Unicode environment variables, the working directory, process desktop and exit-code preservation. An initial run overlapped a package build and failed; the final full run used the completed package without concurrent builds. Company-machine 18.6 remains unverified.

## Dependencies

The four-tool addition (`debugger_attach`, `editor_selection_get`, `project_items`, `test_discover`) passed all 58 background experimental-hive integration tests on VS 18.11, 28 unit tests, 11 bridge tests, the protocol harness, the 56-tool catalog check and a Release VSIX build. The bridge exposes 58 tools including instance selection. Live coverage includes managed automatic and native explicit-engine attach with pause/stack inspection, bounded forward/reversed unsaved selections, C# project items and C++ filters/physical paths, and managed/native VSTest discovery without running the discovered tests. Discovery uses VSTest's fully qualified test-name output file, never localized console parsing. Unit checks cover discovery truncation, runner failure and timeout. The first full run exposed four stale test expectations; after correction the complete suite passed. Company VS 18.6 remains unverified.

`debugger_attach` uses the connected DTE local process list and `Process2.Attach2` on the UI thread, after matching the target executable path. Interrupted attach requests require status inspection before retrying.

The extension targets net472 using Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 18.6.38345 and ModelContextProtocol.Core 2.3.0. Roslyn and VisualStudio.LanguageServices are supplied by the host and are not shipped. The VSIX also excludes the nine shared .NET 10 assemblies listed in `HostAssemblyResolver` and `ExcludeHostAssembliesFromVsix`; keep these lists aligned. The package registers the resolver before starting the SDK. For a failed bind, it validates the assembly name, token, culture and major version, then applies the running IDE's binding policy to the 10.0.0.0 identity. This lets VS choose its installed patch version without changing process-wide redirects. It does not retry failed host binds and rejects identifiable callers outside the extension directory. CLR JIT binds can omit the requesting assembly, so those requests are accepted for the allowlisted identities; this fallback is process-wide when the caller is unknown. [The .NET Framework resolution event supports returning a different assembly version.](https://learn.microsoft.com/en-us/dotnet/framework/app-domains/resolve-assembly-loads)

`VsMcp.Redirects.pkgdef` only redirects `System.Net.ServerSentEvents`, which VS does not supply, to the copy bundled with the SDK. The separately running .NET 10 bridge retains its own dependencies. After dependency or host upgrades, run the harness, catalog check and experimental-hive tests; the standalone harness cannot establish host compatibility. Resolver unit cases cover policies selecting 10.0.0.3 and 10.0.0.10, and the live server test compares loaded shared identities with the IDE's actual policy. These checks do not prove that all APIs used by a newer SDK exist in every older VS patch.

Compiler/analyzer dependency warnings inherited from the SDK/host combination remain visible; passing compilation alone does not establish runtime compatibility.

## VS-native workflow coverage

On 2026-11-11, the 85-tool IDE catalog/check (87 with bridge controls), Release VSIX, protocol harness, 28 unit tests and 11 bridge tests passed on VS 2026 Professional 18.6.11822.322. The complete experimental-hive run passed 82 of 83 integration cases, including all 19 added workflow cases. The remaining real-stdio deployment case is blocked by an existing client holding the shared bridge deployment lease: the older deployed bridge lacks `hostfxr.dll`. The new VSIX contains `hostfxr.dll`, `coreclr.dll` and 195 bridge/runtime files, and standalone bridge tests pass. The normal IDE and existing clients were preserved; validation used the isolated `VsMcpTests` hive and private desktop.

Live coverage includes C++ project/file raw and evaluated settings with macro restoration, local debugger-rule settings and engine restoration, C++/managed output paths, empty filters and file-preserving exclusion, multiple startup projects, Error List codes/source, exact process selection/detach, indexed-frame arguments without selection, advanced breakpoint handles, Win32/CLR exception inventory and policy restoration, per-module symbol readback, counter bytes, instruction-address bytes, disassembly and scalar x64 registers. Modern VS shims expose several legacy methods that throw or return `E_NOTIMPL`; the tools use verified rule storage, indexed COM collections and exact-frame expression contexts where necessary. Register/local fallbacks request `EVAL_NOFUNCEVAL | EVAL_NOSIDEEFFECTS`; these flags are engine constraints, not a sandbox. Breakpoint updates support enabling/disabling; conditions and hit policies are supplied when adding a breakpoint. Other architectures/engines and the new inspection tools against dump sessions remain unverified.

Keep this checklist aligned with the tool catalog and live integration tests when extending workflows. A tool declaration or successful standalone build alone does not establish host/engine support.

| Workflow | Native API boundary | Live verification |
| --- | --- | --- |
| C++ project/file configuration | Visual C++ project engine; exact configuration/platform, no activation | `NativeProjectWorkflowTests` |
| C++ launch settings | Native debug settings; omitted fields preserved, previous/readback | `NativeProjectWorkflowTests` |
| Project outputs and file membership | Project-system outputs, DTE hierarchy, exclude without disk deletion | `ProjectWorkflowTests.cs` |
| Multiple startup projects | One DTE startup array write after resolving every project | `WorkflowSupportTests` |
| Error codes and producers | Error Table sources/snapshots; DTE fallback explicitly identified | `WorkflowSupportTests` |
| Processes, variables and advanced breakpoints | Connected debugger; exact PID/path, frame context and breakpoint handles | `DebuggerWorkflowTests` |
| Exception break policy | Debugger exception groups and snapshot setters | `DebuggerWorkflowTests` |
| Transports and per-module PDB requests | Debugger2 transports and Process3 modules | `NativeInspectionTests` |
| Memory, registers and disassembly | Observed AD7 program, matching current thread and indexed frame | `NativeInspectionTests` |

These tools do not use WER, external dump debuggers or OS process-memory APIs. Workload- and engine-specific failures must stay explicit. A module symbol-load request is not evidence that VS accepted a particular PDB's identity; preserve actual symbol-path readback and the native API's search-path caveat.

## Earlier release validation

An earlier 0.4.0 build was installed into Visual Studio 2026 Community's experimental hive (18.11.12328.437). At that baseline, all 45 integration tests, 11 bridge tests and 9 unit tests passed, along with the HTTP harness and live SDK catalog comparison. Both native C++ and managed C# debugger inspection were exercised against real processes, along with explicit stdio selection between two running VS instances using automatic ports. The managed debug fixture targets .NET 10 and requires the development SDK/runtime, independently of the bridge's self-contained deployment.

C++ source-definition queries were verified with the installed native provider. C++ References remains provider-dependent and is not claimed as universally available; missing capabilities fail explicitly.
