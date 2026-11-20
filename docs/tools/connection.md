# Connection and IDE control

[繁體中文](connection.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Choose a VS instance, control its window, or run advanced C# automation.

This page lists **6 tools**.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Bridge instance management | `list_vs_instances`, `connect_vs_instance` | List running VS instances with PID, instanceId, endpoint and live solution identity; explicitly bind this bridge to the chosen instance. These tools remain available when no VS is running. |
| Find or launch VS for a solution | `launch_vs_instance` | Find a running VS with the authorized absolute `solutionPath` or open it in a new Visual Studio 2026 process. Returns PID, instanceId, endpoint, live identity and `reused`. |
| Instance identity | `get_vs_info` | After binding, confirm the VS PID, version and current solution path. |
| VS main window | `show_vs_window` | Show or hide only the connected VS main window using `visible` (default true). Calls Activate only when `activate: true` is explicit; default false, and activation requires visibility. Returns visible state, activationRequested, title and instance identity. |
| Advanced execution | `execute_code` | Execute a C# method body on the VS UI thread with access to DTE and VS services; return serializable data. This tool can execute arbitrary code. |

## Usage and limitations

`launch_vs_instance` checks reachable registered instances for the absolute path of the requested `.sln` or `.slnx`. It returns `reused: true` when it finds one compatible match. Multiple matches or a match whose requested installation or experimental hive cannot be verified return `VS_SOLUTION_ALREADY_OPEN` with the matching instances. Select a PID explicitly or pass `forceNew: true` when the user requests a new process. For a new launch, use `devenvPath` to disambiguate multiple installations and `rootSuffix` for an explicitly requested experimental hive. The new IDE stays open when the bridge exits. If confirmation times out after 120 seconds or the call is interrupted, inspect the PID if available and call `list_vs_instances` before retrying.

`show_vs_window` with `visible: false` hides VS without closing it or stopping builds/debugging. Showing defaults to no explicit activation; pass `visible: true, activate: true` to request foreground activation. `activationRequested` reports the request, not proof of OS foreground focus. This tool does not establish a persistent background mode: starting debugging currently shows and activates VS, and VS can show itself on a breakpoint.
