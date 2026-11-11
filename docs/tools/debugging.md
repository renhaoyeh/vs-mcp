# Debugger workflows

[繁體中文](debugging.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Launch/attach, select processes and frames, inspect variables, set breakpoints and exception policies.

This page lists **31 tools**.

## Lifecycle and processes

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Debug lifecycle and status | `start_debugging`, `start_without_debugging`, `without_debugging_reconcile`, `stop_debugging`, `get_debug_status` | Start or stop debugging the current startup project; return debugger mode, process IDs/paths and request status without changing startup projects or configuration. |
| Attach debugger | `debugger_attach` | Attach to a local PID after checking its absolute executable path; optional debugger engine. Requires an idle IDE. |
| Debug processes | `debugger_processes`, `debugger_select_process`, `debugger_detach_process` | List local and debugged processes, select one attached process in a paused context, or detach one exact PID/path while keeping the process alive. |
| Debug execution control | `debugger_break`, `debugger_continue`, `debugger_step_into`, `debugger_step_over`, `debugger_step_out` | Break, continue and step after checking the current debugger mode. |

## Frames, variables and evaluation

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Debug context | `debugger_list_threads`, `debugger_select_thread`, `debugger_select_frame` | List threads in the current debug program and select a thread or stack frame while paused; verify selection by reading it back. |
| Debug data | `debugger_get_locals`, `debugger_get_callstack`, `debugger_evaluate` | Read bounded results for the current frame's locals, the current thread's call stack and expression evaluation while paused. |
| Variable expansion | `debugger_variables` | Read bounded expression children in the selected frame, or arguments from an indexed frame without selecting it. Requires an exact paused context/process/thread. Evaluation and getters may execute debuggee code; the timeout applies to root expression evaluation, not the entire traversal. |

## Breakpoints and exception policy

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Breakpoints | `debugger_add_breakpoint`, `debugger_list_breakpoints`, `breakpoint_enable`, `debugger_remove_breakpoint` | Add file/line breakpoints with optional conditions; list, enable, disable and remove them. Binding depends on loaded symbols. |
| Advanced breakpoints | `debugger_breakpoint_add`, `debugger_breakpoint_list`, `debugger_breakpoint_update`, `debugger_breakpoint_remove` | Add file, function, data or address breakpoints with conditions/hit counts; inspect binding and update/remove one exact handle. Availability depends on the debugger engine. |
| Exception policy | `debugger_exception_policy_get`, `debugger_exception_policy_set` | Discover bounded exception groups/settings without assuming localized names; read or explicitly update one exact exception's break-when-thrown/user-unhandled policy. |

## Usage and limitations

`start_without_debugging` posts Ctrl+F5 using the current configuration and startup projects. It supports projects exposing one EXE through the active Built output group (managed DLLs require an EXE apphost). It returns `started`, PIDs and paths only after observing all expected executables as new descendants of this VS; this does not prove application readiness. Otherwise it returns `unconfirmed`. Observation continues up to 120 seconds independently of caller cancellation. Read `get_debug_status.withoutDebuggingRequest` for results. Builds, tests, debugging and unresolved launches prevent duplicate requests. Unconfirmed requests keep blocking launches. Use `without_debugging_reconcile(requestId)` to take a fresh process snapshot even after background observation expires. After inspecting VS, explicitly passing `abandonTracking: true` and `acknowledgeLaunchMayStillOccur: true` marks an unresolved request `abandoned` and releases tracking. This does not cancel a queued command, prove launch failure or terminate processes; a late launch is still possible. Stale request IDs cannot change newer requests, and active builds/tests/debugging prevent abandonment. Short-lived processes and detached launchers may be missed; external programs, browsers and dotnet-hosted DLL launches are unsupported. `stop_debugging` does not terminate these processes.

`debugger_attach(processId, expectedPath, engine?)` checks the local executable path before attaching through the connected debugger. Inspect `get_debug_status` after an interrupted attach before retrying.

`debugger_list_threads` and `debugger_get_callstack` return a `contextId` and `processId`; call stacks also return `threadId` and one-based `frameIndex`. Pass those values to `debugger_select_thread` or `debugger_select_frame`. Tokens expire on continue, step, debugger mode changes or a different debug program. Switching threads requires a fresh call stack before choosing a frame. Selections do not resume execution, move the instruction pointer, freeze threads or switch debug processes/programs. Locals and evaluation use the selected frame; thread/frame availability depends on the engine.
