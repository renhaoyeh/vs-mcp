# Projects and builds

[繁體中文](projects-build.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Manage solutions, configurations, build settings, C++ properties, outputs and project files.

This page lists **22 tools**.

## Solutions and project membership

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Project queries | `get_projects`, `get_solution_configurations`, `get_project_dependencies` | Query projects, solution folders, configuration mappings and build dependencies. |
| Open and close solutions | `solution_open`, `solution_close` | Open or close a solution; reject operations when documents or the solution have unsaved changes. |
| Unload and reload a project | `project_unload`, `project_reload` | Like Solution Explorer's Unload Project and Reload Project, by exact name or unique name. Retains the project in the solution. Requires an idle IDE and saved documents, solution and target project. Reload only accepts unloaded projects. |
| Project hierarchy | `project_items` | Enumerate project files, filters and available physical paths. |
| Project outputs and membership | `project_outputs`, `project_item_add`, `project_item_remove` | Read project-system output paths without building; include an existing file in a project/filter, or exclude one exact hierarchy item while retaining the physical file. |

## Configuration and launch settings

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Startup project and configuration | `startup_project_get`, `startup_project_set`, `build_configuration_set` | Read or explicitly set startup projects, or select a configuration and platform. Settings are never inferred. |
| C++ configuration and launch | `cpp_configuration_get`, `cpp_launch_settings_get`, `cpp_launch_settings_set` | Read raw/evaluated project or file settings, and read or explicitly patch project launch settings. Exact configuration/platform selectors never activate a configuration; omitted selectors use VS's active project mapping. |

## Builds and build preferences

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Builds | `build_projects`, `get_build_status`, `cancel_build` | Build, clean or rebuild selected projects or solution folders; return dependency order, per-project results, errors and Build Output, with waiting, polling and cancellation. |
| Build settings | `get_build_settings`, `set_build_settings` | Read or patch persistent parallelism, output/log verbosity, run/build policies, low priority and C++ timing/logging. Returns support reasons, instance identity and previous/read-back values. Changes require an idle IDE. |

## Usage and limitations

`startup_project_set` accepts either `project` for one project or `projects` for an ordered list. An explicit empty list clears the selection. All projects are resolved and duplicate entries rejected before writing; the response includes the previous list and readback. It never launches or builds the projects.

`cpp_launch_settings_set` patches only supplied command, arguments, working directory, environment, merge flag and debugger type (`auto`, `nativeOnly`, `managedOnly`, `mixed`, `managedCore`, `nativeWithManagedCore`). Project settings and file-membership mutations require an idle IDE and saved state, and save the target project after writing. Save the previous launch values to restore them explicitly. `project_item_remove` excludes one file reference; it does not delete its physical file. Inspect readback and project membership after interrupted mutations before retrying.

`get_build_settings` works without an open solution. `set_build_settings` patches only supplied fields (at least one): `maxParallelProjectBuilds`, `outputVerbosity`, `logFileVerbosity` (C++), `onlyBuildStartupProjectsAndDependencies`, `onRunWhenOutOfDate`, `onRunWhenBuildErrors`, `lowPriorityBuild`, `stopSolutionBuildAfterFirstFailure`, `cppBuildTiming` and `cppBuildLogging`. Verbosity is `quiet`, `minimal`, `normal`, `detailed` or `diagnostic`. The C++ run policies accept `alwaysBuild` / `neverBuild` / `promptToBuild` and `launchOld` / `doNotLaunch` / `promptToLaunch`; launching old binaries can mismatch current source. Support depends on the host/workloads: unavailable fields are null with reasons in `unsupportedSettings`, and requesting an unavailable field rejects the patch before writing. The native stop policy requires a discoverable host registration (VS 18.11+) and is separate from `build_projects.stopOnFirstError`.

These preferences persist across solutions and are not restored automatically. Save the `previous` snapshot to restore explicitly. For example, `set_build_settings(maxParallelProjectBuilds: 2, lowPriorityBuild: true, outputVerbosity: "minimal")` reduces project concurrency and requests low priority for subsequent IDE builds. It does not cap CPU/compiler threads or directly change existing process priorities. Updates across settings providers are not transactional; failures include previous/current snapshots. `updateStatus: "pendingApproval"` means VS has not committed the native settings request; C++ timing/logging writes are deferred and must be requested again after approval is resolved. Read the settings after resolving the native request, an error or an interrupted call before retrying. No tool implicitly changes the active configuration or startup projects.

`project_items(project)` accepts an exact name or unique name and returns that unique name, IDE item hierarchy and available physical paths, bounded to 2,000 items and 64 levels.
