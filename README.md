# VS MCP

English | [繁體中文](README.zh-TW.md)

Give MCP agents access to Visual Studio 2026's C++ and C# development capabilities. The MCP server runs inside the connected IDE and exposes solutions, builds, unsaved documents, debugging, diagnostics, semantic navigation and test results over Streamable HTTP. A bundled .NET stdio bridge provides a fixed client entry point and explicit selection between running VS instances.

## Tools

There are **49 IDE tools**, plus **2 instance-management tools** exposed by the bridge. The table lists every tool and the data it returns. Tools that only displayed windows or terminals, or moved the cursor, were removed in 0.4.0.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Bridge instance management | `list_vs_instances`, `connect_vs_instance` | List running VS instances with PID, instanceId, endpoint and live solution identity; explicitly bind this bridge to the chosen instance. These tools remain available when no VS is running. |
| Instance and solution queries | `get_vs_info`, `get_projects`, `get_solution_configurations`, `get_project_dependencies` | Identify the serving VS process, version and solution path; query projects, solution folders, configuration mappings and build dependencies. |
| Open and close solutions | `solution_open`, `solution_close` | Open or close a solution; reject operations when documents or the solution have unsaved changes. |
| Startup project and configuration | `startup_project_get`, `startup_project_set`, `build_configuration_set` | Read or explicitly set startup projects, or select a configuration and platform. Settings are never inferred. |
| Builds | `build_projects`, `get_build_status`, `cancel_build` | Build, clean or rebuild selected projects or solution folders; return dependency order, per-project results, errors and Build Output, with waiting, polling and cancellation. |
| Editor documents | `document_list`, `document_active`, `document_open`, `document_read`, `document_write`, `document_save`, `document_close` | Read and write IDE documents, including unsaved content; return saved/read-only state and the full buffer's SHA-256 when reading. |
| Precise edits | `editor_replace` | Apply case-sensitive text replacements after checking SHA-256; reject missing or ambiguous matches and stale content. Replacing all matches requires an explicit option. |
| Debug lifecycle and status | `start_debugging`, `start_without_debugging`, `without_debugging_reconcile`, `stop_debugging`, `get_debug_status` | Start or stop debugging the current startup project; return debugger mode, process IDs/paths and request status without changing startup projects or configuration. |
| Debug execution control | `debugger_break`, `debugger_continue`, `debugger_step_into`, `debugger_step_over`, `debugger_step_out` | Break, continue and step after checking the current debugger mode. |
| Breakpoints | `debugger_add_breakpoint`, `debugger_list_breakpoints`, `breakpoint_enable`, `debugger_remove_breakpoint` | Add file/line breakpoints with optional conditions; list, enable, disable and remove them. Binding depends on loaded symbols. |
| Debug context | `debugger_list_threads`, `debugger_select_thread`, `debugger_select_frame` | List threads in the current debug program and select a thread or stack frame while paused; verify selection by reading it back. |
| Debug data | `debugger_get_locals`, `debugger_get_callstack`, `debugger_evaluate` | Read bounded results for the current frame's locals, the current thread's call stack and expression evaluation while paused. |
| Diagnostics | `errors_list`, `output_list_panes`, `output_read` | Filter Error List by severity, project and text; list Output panes and read a bounded tail of their contents. |
| Semantic navigation | `goto_definition`, `find_references` | Return definition/reference file paths, start/end positions and truncation state. Language limitations are listed below. |
| Tests | `test_run`, `test_status`, `test_cancel` | Run specified, already-built test DLLs/EXEs; return run ID, status, TRX counts, individual results, failure messages and stacks, with filtering, waiting and cancellation. |
| Advanced execution | `execute_code` | Execute a C# method body on the VS UI thread with access to DTE and VS services; return serializable data. This tool can execute arbitrary code. |

`start_without_debugging` posts Ctrl+F5 using the current configuration and startup projects. It supports projects exposing one EXE through the active Built output group (managed DLLs require an EXE apphost). It returns `started`, PIDs and paths only after observing all expected executables as new descendants of this VS; this does not prove application readiness. Otherwise it returns `unconfirmed`. Observation continues up to 120 seconds independently of caller cancellation. Read `get_debug_status.without_debugging_request` for results. Builds, tests, debugging and unresolved launches prevent duplicate requests. Unconfirmed requests keep blocking launches. Use `without_debugging_reconcile(requestId)` to take a fresh process snapshot even after background observation expires. After inspecting VS, explicitly passing `abandonTracking: true` and `acknowledgeLaunchMayStillOccur: true` marks an unresolved request `abandoned` and releases tracking. This does not cancel a queued command, prove launch failure or terminate processes; a late launch is still possible. Stale request IDs cannot change newer requests, and active builds/tests/debugging prevent abandonment. Short-lived processes and detached launchers may be missed; external programs, browsers and dotnet-hosted DLL launches are unsupported. `stop_debugging` does not terminate these processes.

The official MCP SDK generates parameter definitions, defaults and result schemas, available through MCP `tools/list`. The generated IDE catalog in [src/VsMcp.Bridge/tools.json](src/VsMcp.Bridge/tools.json) lets the bridge expose stable tool definitions before VS starts. Connecting verifies that the live IDE tool catalog matches.

## C++ and C# support

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
| Ctrl+F5 | `started` confirms observed target processes; `unconfirmed` means launch is still unresolved. | Read `get_debug_status.without_debugging_request`; do not retry while unconfirmed. |
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

## Installation and connection

Download `VsMcp.vsix` from GitHub Releases. The `Release VSIX` GitHub Actions workflow also supports manual runs; download the `VsMcp-vsix` artifact from the completed run.

Build requirements: Windows, Visual Studio 2026 with C#/.NET, Desktop C++ and extension-development workloads, the .NET 10 SDK and the .NET Framework 4.7.2 targeting pack.

Open `VsMcp.slnx` in Visual Studio, select Release and build the `VsMcp.Vsix` project. The package is written to `src/VsMcp.Vsix/bin/Release/net472/VsMcp.vsix`.

Install the VSIX and start VS once. The extension installs the bundled bridge at `%LOCALAPPDATA%\VsMcp\Bridge\VsMcp.Bridge.exe`. The bridge requires the .NET 10 runtime. Configure your MCP client with the full executable path for your account:

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

## Architecture and validation

| Directory under `src/VsMcp.Vsix` | Responsibility |
| --- | --- |
| `Mcp/` | HTTP transport, official MCP SDK, protocol errors and serialization. |
| `Tools/` | MCP tool names, descriptions, schemas and request adapters. |
| `Contracts/` | Shared typed results and JSON contracts. |
| `Services/` | Services for solutions, builds, documents, debugging, language navigation and tests. |
| `Infrastructure/` | VSTest processes, argument handling, output capture and Windows process jobs. |

Version 0.4.0 was installed and verified in the VS 2026 Community experimental instance: **45 integration tests, 11 bridge tests and 9 unit tests passed**, along with the HTTP harness and live tool schema comparison. C++ and C# builds, documents, tests and debugging were exercised, as was stdio control of two VS instances with automatic ports; C++ references remain subject to the provider limitations above.

See [DEVELOPMENT.md](DEVELOPMENT.md) for architecture details, dependency versions, test commands and experimental-instance workflows.

## License

This project is licensed under the [MIT License](LICENSE).
