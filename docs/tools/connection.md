# Connection and IDE control

[繁體中文](connection.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Choose a VS instance, control its window, or run advanced C# automation.

This page lists **5 tools**.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Bridge instance management | `list_vs_instances`, `connect_vs_instance` | List running VS instances with PID, instanceId, endpoint and live solution identity; explicitly bind this bridge to the chosen instance. These tools remain available when no VS is running. |
| Instance identity | `get_vs_info` | After binding, confirm the VS PID, version and current solution path. |
| VS main window | `show_vs_window` | Show or hide only the connected VS main window using `visible` (default true). Calls Activate only when `activate: true` is explicit; default false, and activation requires visibility. Returns visible state, activationRequested, title and instance identity. |
| Advanced execution | `execute_code` | Execute a C# method body on the VS UI thread with access to DTE and VS services; return serializable data. This tool can execute arbitrary code. |

## Usage and limitations

`show_vs_window` with `visible: false` hides VS without closing it or stopping builds/debugging. Showing defaults to no explicit activation; pass `visible: true, activate: true` to request foreground activation. `activationRequested` reports the request, not proof of OS foreground focus. This tool does not establish a persistent background mode: starting debugging currently shows and activates VS, and VS can show itself on a breakpoint.
