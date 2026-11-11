# Tool reference

[繁體中文](README.zh-TW.md) · [VS MCP](../../README.md)

The server exposes **85 IDE tools** and the bridge adds **2 instance-management tools**. Choose a category for complete names, behavior and limitations. Counts below include the two bridge tools under connection.

| Category | Tools | Details |
| --- | --- | --- |
| [Connection and IDE control](connection.md) | 5 | Choose a VS instance, control its window, or run advanced C# automation. |
| [Projects and builds](projects-build.md) | 22 | Manage solutions, configurations, build settings, C++ properties, outputs and project files. |
| [Editing and navigation](editor-navigation.md) | 11 | Read unsaved buffers, apply verified edits and query semantic locations. |
| [Debugger workflows](debugging.md) | 31 | Launch/attach, select processes and frames, inspect variables, set breakpoints and exception policies. |
| [Diagnostics, dumps and symbols](diagnostics.md) | 14 | Read Error List/Output, inspect dumps/PDBs and read native memory, instructions and registers. |
| [Testing](testing.md) | 4 | Discover and run already-built tests, collect results and cancel owned runs. |

The official MCP SDK generates parameter definitions, defaults and result schemas, available through MCP `tools/list`. The generated IDE catalog in [src/VsMcp.Bridge/tools.json](../../src/VsMcp.Bridge/tools.json) lets the bridge expose stable tool definitions before VS starts. Connecting verifies that the live IDE tool catalog matches.

## Knowing when an operation is complete

| Operation | What the response means | What the agent should check next |
| --- | --- | --- |
| Build | Returns the final result, or `running` when the wait expires. A wait timeout does not cancel the build. | Poll `get_build_status` until a final status is available. |
| Debug start, stop, break, continue and step | The command/API request was accepted; the target process may still be changing state. | Check mode, request status and process data with `get_debug_status`; avoid blindly repeating commands. |
| Ctrl+F5 | `started` confirms observed target processes; `unconfirmed` means launch is still unresolved. | Read `get_debug_status.withoutDebuggingRequest`; do not retry while unconfirmed. |
| Document edit | The editor buffer changed and usually has not been saved to disk. | Check document state; call `document_save` or explicitly request `save` when writing if persistence is needed. |
| Semantic navigation | Returns locations or an explicit unsupported/query-failure result. | Use the returned paths and positions. |
| Tests | Returns a run ID and current status; completed runs include actual TRX results. | Collect results with `test_status`; `noTests` is not a pass, and runner errors or missing reports are not reported as success. |

## Agent workflows

| Task | Suggested sequence | Behavior and precautions |
| --- | --- | --- |
| Confirm the target | `list_vs_instances` → `connect_vs_instance` with the chosen PID and instanceId → `get_vs_info` → inspect version and solution path. | Each bridge binds to one instance. Changing the target requires an explicit connection request after active calls finish. Direct HTTP clients should check `get_vs_info` first. |
| Edit code | `document_open` → `document_read` → pass the returned `sha256` as `expectedSha256` to `editor_replace` or `document_write` → save if needed. | Read again when the hash is stale. Each edit is one undo transaction. Reads have line/character limits; positions are 1-based. |
| Build | Query projects, dependencies and active configuration → `build_projects` → `get_build_status` if needed. | Build/rebuild includes required dependencies; clean does not add dependencies. Building does not switch configuration. |
| Run tests | Finish building → `test_run` with absolute DLL/EXE paths and optional filter, settings, adapter path and architecture → `test_status`. | Disconnecting does not cancel tests. If a run ID is lost, omit `runId` from `test_status` to query the latest run. Cancellation requires a run ID. |
| Inspect execution | Set breakpoints → start debugging → confirm break mode → query locals, stack and expressions → step or continue → stop. | Expression evaluation may execute target code and produce side effects. |

Use the agent's filesystem tools for ordinary disk searches and file/folder creation. This MCP focuses on IDE state, unsaved content and development workflows.
