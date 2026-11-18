---
name: visual-studio
description: Use VS MCP to work in a running Visual Studio instance for solution management, C++ or C# builds, unsaved editor buffers, debugging, diagnostics, and tests. Use when the task calls for Visual Studio or depends on live IDE state.
---

# Visual Studio via VS MCP

## Verify the instance

- Use the `visual-studio` MCP server. If it is unavailable, check the client's MCP connection and `%LOCALAPPDATA%\Programs\VsMcp\mcp-config.json` for the installed launcher path. Change client configuration only when the user asks to configure it.
- Call `list_vs_instances` and match the live solution path or other identity supplied by the user. If multiple instances fit and the intended target is unclear, ask which to use. Bind using `connect_vs_instance` with the chosen `pid` and `instanceId`, then confirm the solution with `get_vs_info`. Work only against the verified instance and include its PID and solution path in the result when available. A restarted IDE requires fresh discovery and explicit binding.

## Work in the IDE

- Prefer `build_projects` for builds. Resolve the exact project unique names with `get_projects(recursive: true)` and inspect dependencies with `get_project_dependencies`. If a project in the confirmed solution is unloaded, use `project_reload` within the requested build scope and verify it loaded. If the IDE path remains blocked after inspection, report the blocker and use a suitable build fallback when possible.
- Builds use the active solution configuration. Use `get_solution_configurations` or `startup_project_get` to inspect settings. Change configuration or startup projects only when requested, using `build_configuration_set` or `startup_project_set`. If a build reports `running`, collect its result with `get_build_status`.
- Use `document_read` to inspect the live editor buffer before editing. Use the dedicated editor, test, and debugger tools for their operations. An accepted launch from `start_debugging` is a request. Check `get_debug_status` to confirm that debugging actually started.
- Prefer dedicated tools over `execute_code`, which runs with the user's permissions in the IDE. If a mutation is interrupted or reports `outcomeUnknown`, inspect the verified instance and affected state before deciding whether to retry. If the effect cannot be verified, report the uncertain outcome.
