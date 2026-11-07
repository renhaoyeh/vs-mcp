# VS MCP

English | [繁體中文](README.zh-TW.md)

Give MCP agents access to Visual Studio 2026's C++ and C# development capabilities. The MCP server runs inside the connected IDE and exposes solutions, builds, unsaved documents, debugging, diagnostics, semantic navigation and test results over Streamable HTTP. The default endpoint is `http://127.0.0.1:18770/mcp`.

## Tools

There are **44 tools**. The table lists every tool and the data it returns. Tools that only displayed windows or terminals, or moved the cursor, were removed in 0.4.0.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Instance and solution queries | `get_vs_info`, `get_projects`, `get_solution_configurations`, `get_project_dependencies` | Identify the serving VS process, version and solution path; query projects, solution folders, configuration mappings and build dependencies. |
| Open and close solutions | `solution_open`, `solution_close` | Open or close a solution; reject operations when documents or the solution have unsaved changes. |
| Startup project and configuration | `startup_project_get`, `startup_project_set`, `build_configuration_set` | Read or explicitly set startup projects, or select a configuration and platform. Settings are never inferred. |
| Builds | `build_projects`, `get_build_status`, `cancel_build` | Build, clean or rebuild selected projects or solution folders; return dependency order, per-project results, errors and Build Output, with waiting, polling and cancellation. |
| Editor documents | `document_list`, `document_active`, `document_open`, `document_read`, `document_write`, `document_save`, `document_close` | Read and write IDE documents, including unsaved content; return saved/read-only state and the full buffer's SHA-256 when reading. |
| Precise edits | `editor_replace` | Apply case-sensitive text replacements after checking SHA-256; reject missing or ambiguous matches and stale content. Replacing all matches requires an explicit option. |
| Debug lifecycle and status | `start_debugging`, `stop_debugging`, `get_debug_status` | Start or stop debugging the current startup project; return debugger mode, process IDs/paths and request status without changing startup projects or configuration. |
| Debug execution control | `debugger_break`, `debugger_continue`, `debugger_step_into`, `debugger_step_over`, `debugger_step_out` | Break, continue and step after checking the current debugger mode. |
| Breakpoints | `debugger_add_breakpoint`, `debugger_list_breakpoints`, `breakpoint_enable`, `debugger_remove_breakpoint` | Add file/line breakpoints with optional conditions; list, enable, disable and remove them. Binding depends on loaded symbols. |
| Debug data | `debugger_get_locals`, `debugger_get_callstack`, `debugger_evaluate` | Read bounded results for the current frame's locals, the current thread's call stack and expression evaluation while paused. |
| Diagnostics | `errors_list`, `output_list_panes`, `output_read` | Filter Error List by severity, project and text; list Output panes and read a bounded tail of their contents. |
| Semantic navigation | `goto_definition`, `find_references` | Return definition/reference file paths, start/end positions and truncation state. Language limitations are listed below. |
| Tests | `test_run`, `test_status`, `test_cancel` | Run specified, already-built test DLLs/EXEs; return run ID, status, TRX counts, individual results, failure messages and stacks, with filtering, waiting and cancellation. |
| Advanced execution | `execute_code` | Execute a C# method body on the VS UI thread with access to DTE and VS services; return serializable data. This tool can execute arbitrary code. |

The official MCP SDK generates parameter definitions, defaults and result schemas, available through MCP `tools/list`. A test snapshot is stored in [tests/VsMcp.TestProjects/tools.json](tests/VsMcp.TestProjects/tools.json).

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
| Document edit | The editor buffer changed and usually has not been saved to disk. | Check document state; call `document_save` or explicitly request `save` when writing if persistence is needed. |
| Semantic navigation | Returns locations or an explicit unsupported/query-failure result. | Use the returned paths and positions. |
| Tests | Returns a run ID and current status; completed runs include actual TRX results. | Collect results with `test_status`; `noTests` is not a pass, and runner errors or missing reports are not reported as success. |

## Agent workflows

| Task | Suggested sequence | Behavior and precautions |
| --- | --- | --- |
| Confirm the target | `get_vs_info` → inspect PID, version and solution path. | Each VS instance needs its own explicit port; collisions never redirect to another instance. |
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

Install the VSIX, restart VS and configure your MCP client:

```json
{
  "mcpServers": {
    "visual-studio": { "url": "http://127.0.0.1:18770/mcp" }
  }
}
```

Set `VS_MCP_PORT` before starting VS to choose a different port. The server listens only on loopback and validates Host/Origin; it currently has no authentication token.

`execute_code` runs arbitrary code with your permissions on the UI thread, without a sandbox, execution timeout or rollback. If a modifying operation is interrupted by a disconnect, inspect its effects before deciding whether to retry.

## Architecture and validation

| Directory under `src/VsMcp.Vsix` | Responsibility |
| --- | --- |
| `Mcp/` | HTTP transport, official MCP SDK, protocol errors and serialization. |
| `Tools/` | MCP tool names, descriptions, schemas and request adapters. |
| `Contracts/` | Shared typed results and JSON contracts. |
| `Services/` | Services for solutions, builds, documents, debugging, language navigation and tests. |
| `Infrastructure/` | VSTest processes, argument handling, output capture and Windows process jobs. |

Version 0.4.0 was installed and verified in the VS 2026 Community experimental instance: **44 integration tests and 9 unit tests passed**, along with the HTTP harness and live tool schema comparison. C++ and C# builds, documents, tests and debugging were exercised; C++ references remain subject to the provider limitations above.

See [DEVELOPMENT.md](DEVELOPMENT.md) for architecture details, dependency versions, test commands and experimental-instance workflows.
