# VS MCP

English | [繁體中文](README.zh-TW.md)

Give MCP agents access to Visual Studio 2026's C++ and C# development capabilities. The MCP server runs inside the connected IDE and exposes solutions, builds, unsaved documents, debugging, diagnostics, semantic navigation and test results over Streamable HTTP. A bundled .NET stdio bridge provides a fixed client entry point and explicit selection between running VS instances.

## Installation and connection

Using VS MCP requires Windows x64 and Visual Studio 2026. The VSIX includes the bridge's .NET runtime; no separate .NET 10 installation is required.

The extension uses the connected Visual Studio installation's shared .NET assemblies and binding policy, rather than requiring a fixed assembly patch version. The stdio bridge runs separately with its bundled runtime.

To have an agent download, install and configure VS MCP, paste this request:

```text
Please install VS MCP and configure it for my current MCP client on Windows.

1. Find the installed Visual Studio 2026 instance. If several instances are available, ask which one to use.
2. Download VsMcp.vsix from the latest stable release at https://github.com/renhaoyeh/vs-mcp/releases. If no release asset is available, report that instead of claiming installation succeeded.
3. Use the selected Visual Studio installation's VSIXInstaller to install the VSIX. The bridge runtime is bundled; do not install .NET separately. If Visual Studio must be closed, ask me to save my work and close it before continuing.
4. Start the selected Visual Studio instance and confirm that %LOCALAPPDATA%\VsMcp\Bridge\VsMcp.Bridge.exe exists. Resolve this to the actual full path for my account.
5. Add a server named visual-studio to my current MCP client's configuration, using that full executable path and the client's supported configuration format. Preserve other existing settings. If the client needs to reload or restart, tell me what to do.
6. Once the tools are available, call list_vs_instances, connect_vs_instance with the selected pid and instanceId, then get_vs_info to verify the intended Visual Studio instance. Report the installed version and actual verification result; if verification is blocked, explain the blocker.
```

Download `VsMcp.vsix` from GitHub Releases. The `Release VSIX` GitHub Actions workflow also supports manual runs; download the `VsMcp-vsix` artifact from the completed run.

Install the VSIX, then start VS once. The extension installs the self-contained bridge at `%LOCALAPPDATA%\VsMcp\Bridge\VsMcp.Bridge.exe`. Configure your MCP client with the full executable path for your account:

```json
{
  "mcpServers": {
    "visual-studio": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\Bridge\\VsMcp.Bridge.exe"
    }
  }
}
```

Call `list_vs_instances`, then `connect_vs_instance` with the selected `pid` and `instanceId`. The bridge reads registrations from `%LOCALAPPDATA%\VsMcp\instances`, verifies the process and queries live identity. It stays available with no VS open, but IDE operations require a bound, running instance. It does not launch VS. Closing and restarting VS requires selecting its new instance explicitly. A connection failure never causes an automatic retry of a tool call; later calls establish fresh connections to the same bound instance. If a call reports `outcomeUnknown`, inspect its effects before retrying. Close bridge clients before installing an updated bridge, then restart VS.

Direct HTTP clients can still use `http://127.0.0.1:18770/mcp`. Without `VS_MCP_PORT`, VS first tries port 18770 and chooses a free port on collision; the bridge discovers the assigned endpoint. Setting `VS_MCP_PORT` before starting VS fixes the port and disables fallback. The server listens only on loopback and validates Host/Origin; it currently has no authentication token. Bridge requests additionally identify the selected registration so a reused port cannot redirect a call to another instance.

`execute_code` runs arbitrary code with your permissions on the UI thread, without a sandbox, execution timeout or rollback. If a modifying operation is interrupted by a disconnect, inspect its effects before deciding whether to retry.

## Tools

There are **70 IDE tools**, plus **2 instance-management tools** exposed by the bridge. The table lists every tool and the data it returns.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Bridge instance management | `list_vs_instances`, `connect_vs_instance` | List running VS instances with PID, instanceId, endpoint and live solution identity; explicitly bind this bridge to the chosen instance. These tools remain available when no VS is running. |
| VS main window | `show_vs_window` | Show or hide only the connected VS main window using `visible` (default true). Calls Activate only when `activate: true` is explicit; default false, and activation requires visibility. Returns visible state, activationRequested, title and instance identity. |
| Instance and solution queries | `get_vs_info`, `get_projects`, `get_solution_configurations`, `get_project_dependencies` | Identify the serving VS process, version and solution path; query projects, solution folders, configuration mappings and build dependencies. |
| Open and close solutions | `solution_open`, `solution_close` | Open or close a solution; reject operations when documents or the solution have unsaved changes. |
| Unload and reload a project | `project_unload`, `project_reload` | Like Solution Explorer's Unload Project and Reload Project, by exact name or unique name. Retains the project in the solution. Requires an idle IDE and saved documents, solution and target project. Reload only accepts unloaded projects. |
| Startup project and configuration | `startup_project_get`, `startup_project_set`, `build_configuration_set` | Read or explicitly set startup projects, or select a configuration and platform. Settings are never inferred. |
| Builds | `build_projects`, `get_build_status`, `cancel_build` | Build, clean or rebuild selected projects or solution folders; return dependency order, per-project results, errors and Build Output, with waiting, polling and cancellation. |
| Build settings | `get_build_settings`, `set_build_settings` | Read or patch persistent parallelism, output/log verbosity, run/build policies, low priority and C++ timing/logging. Returns support reasons, instance identity and previous/read-back values. Changes require an idle IDE. |
| Editor documents | `document_list`, `document_active`, `document_open`, `document_read`, `document_write`, `document_save`, `document_close` | Read and write IDE documents, including unsaved content; return saved/read-only state and the full buffer's SHA-256 when reading. |
| Precise edits | `editor_replace` | Apply case-sensitive text replacements after checking SHA-256; reject missing or ambiguous matches and stale content. Replacing all matches requires an explicit option. |
| Debug lifecycle and status | `start_debugging`, `start_without_debugging`, `without_debugging_reconcile`, `stop_debugging`, `get_debug_status` | Start or stop debugging the current startup project; return debugger mode, process IDs/paths and request status without changing startup projects or configuration. |
| VS dump diagnostics | `debugger_open_dump`, `debugger_save_dump`, `debugger_list_modules`, `debugger_get_exception` | Debug an existing Windows minidump in VS, save a paused process dump without overwriting, and inspect engine-reported modules/symbols and observed exception events. Uses the connected VS debugger. |
| PDB preferences | `debugger_get_symbol_settings`, `debugger_set_symbol_settings` | Read or explicitly update persistent PDB search paths, enabled-path mask, cache and loading policy; optional immediate symbol loading requires a paused session. Returns previous/read-back settings. |
| Debug execution control | `debugger_break`, `debugger_continue`, `debugger_step_into`, `debugger_step_over`, `debugger_step_out` | Break, continue and step after checking the current debugger mode. |
| Attach debugger | `debugger_attach` | Attach to a local PID after checking its absolute executable path; optional debugger engine. Requires an idle IDE. |
| Additional IDE queries | `editor_selection_get`, `project_items`, `test_discover` | Read the active selection, enumerate project membership and discover tests from built containers without running them. |
| C++ configuration and launch | `cpp_configuration_get`, `cpp_launch_settings_get`, `cpp_launch_settings_set` | Read raw/evaluated project or file settings, and read or explicitly patch project launch settings. Exact configuration/platform selectors never activate a configuration; omitted selectors use VS's active project mapping. |
| Project outputs and membership | `project_outputs`, `project_item_add`, `project_item_remove` | Read project-system output paths without building; include an existing file in a project/filter, or exclude one exact hierarchy item while retaining the physical file. |
| Breakpoints | `debugger_add_breakpoint`, `debugger_list_breakpoints`, `breakpoint_enable`, `debugger_remove_breakpoint` | Add file/line breakpoints with optional conditions; list, enable, disable and remove them. Binding depends on loaded symbols. |
| Debug context | `debugger_list_threads`, `debugger_select_thread`, `debugger_select_frame` | List threads in the current debug program and select a thread or stack frame while paused; verify selection by reading it back. |
| Debug data | `debugger_get_locals`, `debugger_get_callstack`, `debugger_evaluate` | Read bounded results for the current frame's locals, the current thread's call stack and expression evaluation while paused. |
| Diagnostics | `errors_list`, `output_list_panes`, `output_read` | Filter Error List by severity, project, text, exact error code/source and producer; return VS-supplied build tool and source identifiers, provider stability and truncation. List Output panes and read a bounded tail. |
| Semantic navigation | `goto_definition`, `find_references` | Return definition/reference file paths, start/end positions and truncation state. Language limitations are listed below. |
| Tests | `test_run`, `test_status`, `test_cancel` | Run specified, already-built test DLLs/EXEs; return run ID, status, TRX counts, individual results, failure messages and stacks, with filtering, waiting and cancellation. |
| Advanced execution | `execute_code` | Execute a C# method body on the VS UI thread with access to DTE and VS services; return serializable data. This tool can execute arbitrary code. |

`start_without_debugging` posts Ctrl+F5 using the current configuration and startup projects. It supports projects exposing one EXE through the active Built output group (managed DLLs require an EXE apphost). It returns `started`, PIDs and paths only after observing all expected executables as new descendants of this VS; this does not prove application readiness. Otherwise it returns `unconfirmed`. Observation continues up to 120 seconds independently of caller cancellation. Read `get_debug_status.withoutDebuggingRequest` for results. Builds, tests, debugging and unresolved launches prevent duplicate requests. Unconfirmed requests keep blocking launches. Use `without_debugging_reconcile(requestId)` to take a fresh process snapshot even after background observation expires. After inspecting VS, explicitly passing `abandonTracking: true` and `acknowledgeLaunchMayStillOccur: true` marks an unresolved request `abandoned` and releases tracking. This does not cancel a queued command, prove launch failure or terminate processes; a late launch is still possible. Stale request IDs cannot change newer requests, and active builds/tests/debugging prevent abandonment. Short-lived processes and detached launchers may be missed; external programs, browsers and dotnet-hosted DLL launches are unsupported. `stop_debugging` does not terminate these processes.

The official MCP SDK generates parameter definitions, defaults and result schemas, available through MCP `tools/list`. The generated IDE catalog in [src/VsMcp.Bridge/tools.json](src/VsMcp.Bridge/tools.json) lets the bridge expose stable tool definitions before VS starts. Connecting verifies that the live IDE tool catalog matches.

`startup_project_set` accepts either `project` for one project or `projects` for an ordered list. An explicit empty list clears the selection. All projects are resolved and duplicate entries rejected before writing; the response includes the previous list and readback. It never launches or builds the projects.

`errors_list` preserves the Error List provider's `code`, `source`, `buildTool`, `dataSource` and `sourceType`. `source` is the native `Build`/`Other` classification: `Other` does not establish that a diagnostic came from IntelliSense. Positions are one-based; absent fields are null. `isStable` describes the subscribed providers and does not prove analysis is complete. A legacy DTE fallback advertises `extendedFieldsAvailable: false` and rejects extended filters.

`cpp_launch_settings_set` patches only supplied command, arguments, working directory, environment, merge flag and debugger type (`auto`, `nativeOnly`, `managedOnly`, `mixed`, `managedCore`, `nativeWithManagedCore`). Project settings and file-membership mutations require an idle IDE and saved state, and save the target project after writing. Save the previous launch values to restore them explicitly. `project_item_remove` excludes one file reference; it does not delete its physical file. Inspect readback and project membership after interrupted mutations before retrying.

`show_vs_window` with `visible: false` hides VS without closing it or stopping builds/debugging. Showing defaults to no explicit activation; pass `visible: true, activate: true` to request foreground activation. `activationRequested` reports the request, not proof of OS foreground focus. This tool does not establish a persistent background mode: starting debugging currently shows and activates VS, and VS can show itself on a breakpoint.

`get_build_settings` works without an open solution. `set_build_settings` patches only supplied fields (at least one): `maxParallelProjectBuilds`, `outputVerbosity`, `logFileVerbosity` (C++), `onlyBuildStartupProjectsAndDependencies`, `onRunWhenOutOfDate`, `onRunWhenBuildErrors`, `lowPriorityBuild`, `stopSolutionBuildAfterFirstFailure`, `cppBuildTiming` and `cppBuildLogging`. Verbosity is `quiet`, `minimal`, `normal`, `detailed` or `diagnostic`. The C++ run policies accept `alwaysBuild` / `neverBuild` / `promptToBuild` and `launchOld` / `doNotLaunch` / `promptToLaunch`; launching old binaries can mismatch current source. Support depends on the host/workloads: unavailable fields are null with reasons in `unsupportedSettings`, and requesting an unavailable field rejects the patch before writing. The native stop policy requires a discoverable host registration (VS 18.11+) and is separate from `build_projects.stopOnFirstError`.

These preferences persist across solutions and are not restored automatically. Save the `previous` snapshot to restore explicitly. For example, `set_build_settings(maxParallelProjectBuilds: 2, lowPriorityBuild: true, outputVerbosity: "minimal")` reduces project concurrency and requests low priority for subsequent IDE builds. It does not cap CPU/compiler threads or directly change existing process priorities. Updates across settings providers are not transactional; failures include previous/current snapshots. `updateStatus: "pendingApproval"` means VS has not committed the native settings request; C++ timing/logging writes are deferred and must be requested again after approval is resolved. Read the settings after resolving the native request, an error or an interrupted call before retrying. No tool implicitly changes the active configuration or startup projects.

## C++ and C# support

For a dump already on disk, set PDB preferences with `debugger_set_symbol_settings` if needed, then call `debugger_open_dump(path)`. The default engine is VS native-code debugging; `engineGuid` can explicitly select another installed dump-capable engine. The tool validates the extension/header and keeps the file locked against replacement during launch. It invokes the debugger on that file directly, without F5 or replacing the solution/startup projects. Opening is asynchronous: inspect `get_debug_status.request` and `dumpRequest`; acceptance alone is not a successfully loaded dump. Once paused, inspect `debugger_get_exception`, `debugger_list_modules`, `debugger_get_callstack`, threads and locals, then end the session with `stop_debugging`. Dump/engine compatibility is host-dependent.

`debugger_save_dump(path, processId, kind: "normal")` captures the selected paused process using VS; `kind: "full"` includes more memory and can be large. The destination must be new. A failed save/move preserves its temporary dump and reports the path. Inspect files and debug status after interruption before issuing another mutation. No WER/event-log search, automatic dump discovery or external cdb dependency is included.

Module versions, timestamps and symbol locations come from VS, not a newer local DLL. The provider uses observed debug programs or the exact current process's automation module list; fields unavailable through that provider (including program IDs/timestamps in the automation view) are null, and module read errors are explicit. Exception information is the last engine event observed for the current paused process: it may be absent and is not proof of a crash or the current stop cause. Call stacks include `sourcePath` and one-based `sourceLine` when symbols provide them; those locations and timestamps do not prove that local source matches the build.

Symbol preferences persist until explicitly restored from `previous`. `symbolPath` is semicolon-separated and `symbolPathState` has one 0/1 character per path; for example a single local PDB directory uses mask `"1"`. Empty path/mask clears the search list. `loadSymbolsNow: true` requires a paused session and can use configured symbol servers; inspect module status for actual PDB loading. This may block during VS symbol loading and has no forced timeout or rollback.

`editor_selection_get` returns one-based caret and selection coordinates from the active unsaved text buffer, with bounded selected text. No active text editor returns null data. Box selections report `blockSelection: true` and omit selected text. `project_items(project)` accepts an exact name or unique name and returns that unique name, IDE item hierarchy and available physical paths, bounded to 2,000 items and 64 levels.

`test_discover(sources)` uses the connected VS VSTest runner to list fully qualified test names from already-built containers without executing tests. It accepts optional settings/adapter paths and architecture, with bounded results and a timeout. Adapters must be installed; discovery can load adapter/container code. `debugger_attach(processId, expectedPath, engine?)` checks the local executable path before attaching through the connected debugger. Inspect `get_debug_status` after an interrupted attach before retrying.

| Capability | C# | C++ | Conditions and limitations |
| --- | --- | --- | --- |
| Solutions, projects and configurations | Supported | Supported | Operates only on the connected VS instance. |
| Build, clean and rebuild | Supported | Native projects supported | Uses the active configuration and platform; requires the corresponding workloads, SDKs and toolsets. |
| Unsaved document reads and writes | Supported | Supported | Reads the IDE buffer; edits require the SHA-256 returned by a previous read. |
| Debugging and inspection | Managed debug engine | Native debug engine | Variables, call stacks and evaluation require break mode; symbols and expression support depend on the engine. |
| Definitions | Live Roslyn workspace, including unsaved edits | Native Peek Definitions provider | C++ may open documents without showing Peek UI; IntelliSense must be ready. |
| References | Live Roslyn workspace | **Depends on the native provider** | C++ requires a Peek References provider. If unavailable, returns `NOT_SUPPORTED`; text search is never substituted for semantic references. |
| Test results | VSTest-compatible managed tests | VSTest-compatible native tests | Requires a compatible adapter; `platform` must match the test binary's x64/x86/ARM64 architecture. |

C++ navigation uses the document's current project context in VS. The navigation `project` override applies only to the managed backend, to disambiguate linked files shared by multiple projects.

Test tools run already-built binaries. They do not build, restore or synchronize the Test Explorer UI. Executables that support only Microsoft.Testing.Platform are currently incompatible with this VSTest runner.

## Knowing when an operation is complete

| Operation | What the response means | What the agent should check next |
| --- | --- | --- |
| Build | Returns the final result, or `running` when the wait expires. A wait timeout does not cancel the build. | Poll `get_build_status` until a final status is available. |
| Debug start, stop, break, continue and step | The command/API request was accepted; the target process may still be changing state. | Check mode, request status and process data with `get_debug_status`; avoid blindly repeating commands. |
| Ctrl+F5 | `started` confirms observed target processes; `unconfirmed` means launch is still unresolved. | Read `get_debug_status.withoutDebuggingRequest`; do not retry while unconfirmed. |
| Document edit | The editor buffer changed and usually has not been saved to disk. | Check document state; call `document_save` or explicitly request `save` when writing if persistence is needed. |
| Semantic navigation | Returns locations or an explicit unsupported/query-failure result. | Use the returned paths and positions. |
| Tests | Returns a run ID and current status; completed runs include actual TRX results. | Collect results with `test_status`; `noTests` is not a pass, and runner errors or missing reports are not reported as success. |

`debugger_list_threads` and `debugger_get_callstack` return a `contextId` and `processId`; call stacks also return `threadId` and one-based `frameIndex`. Pass those values to `debugger_select_thread` or `debugger_select_frame`. Tokens expire on continue, step, debugger mode changes or a different debug program. Switching threads requires a fresh call stack before choosing a frame. Selections do not resume execution, move the instruction pointer, freeze threads or switch debug processes/programs. Locals and evaluation use the selected frame; thread/frame availability depends on the engine.

## Agent workflows

| Task | Suggested sequence | Behavior and precautions |
| --- | --- | --- |
| Confirm the target | `list_vs_instances` → `connect_vs_instance` with the chosen PID and instanceId → `get_vs_info` → inspect version and solution path. | Each bridge binds to one instance. Changing the target requires an explicit connection request after active calls finish. Direct HTTP clients should check `get_vs_info` first. |
| Edit code | `document_open` → `document_read` → pass the returned `sha256` as `expectedSha256` to `editor_replace` or `document_write` → save if needed. | Read again when the hash is stale. Each edit is one undo transaction. Reads have line/character limits; positions are 1-based. |
| Build | Query projects, dependencies and active configuration → `build_projects` → `get_build_status` if needed. | Build/rebuild includes required dependencies; clean does not add dependencies. Building does not switch configuration. |
| Run tests | Finish building → `test_run` with absolute DLL/EXE paths and optional filter, settings, adapter path and architecture → `test_status`. | Disconnecting does not cancel tests. If a run ID is lost, omit `runId` from `test_status` to query the latest run. Cancellation requires a run ID. |
| Inspect execution | Set breakpoints → start debugging → confirm break mode → query locals, stack and expressions → step or continue → stop. | Expression evaluation may execute target code and produce side effects. |

Test cancellation terminates only that run's runner and owned test-host process job, leaving other Test Explorer runs alone. The latest ten runs are retained in memory, with TRX reports in isolated temporary directories. While an owned test run is active, builds, debug launches and solution/configuration changes are rejected.

Use the agent's filesystem tools for ordinary disk searches and file/folder creation. This MCP focuses on IDE state, unsaved content and development workflows.

## Architecture

| Directory under `src/VsMcp.Vsix` | Responsibility |
| --- | --- |
| `Mcp/` | HTTP transport, official MCP SDK, protocol errors and serialization. |
| `Tools/` | MCP tool names, descriptions, schemas and request adapters. |
| `Contracts/` | Shared typed results and JSON contracts. |
| `Services/` | Services for solutions, builds, documents, debugging, language navigation and tests. |
| `Infrastructure/` | VSTest processes, argument handling, output capture and Windows process jobs. |

See [DEVELOPMENT.md](DEVELOPMENT.md) for architecture details, dependency versions, test commands and experimental-instance workflows.

## License

This project is licensed under the [MIT License](LICENSE).
