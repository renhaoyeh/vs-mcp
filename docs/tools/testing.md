# Testing

[繁體中文](testing.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Discover and run already-built tests, collect results and cancel owned runs.

This page lists **4 tools**.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Test discovery | `test_discover` | Discover tests from built containers without running them. |
| Tests | `test_run`, `test_status`, `test_cancel` | Run specified, already-built test DLLs/EXEs; return run ID, status, TRX counts, individual results, failure messages and stacks, with filtering, waiting and cancellation. |

## Usage and limitations

`test_discover(sources)` uses the connected VS VSTest runner to list fully qualified test names from already-built containers without executing tests. It accepts optional settings/adapter paths and architecture, with bounded results and a timeout. Adapters must be installed; discovery can load adapter/container code.

Test tools run already-built binaries. They do not build, restore or synchronize the Test Explorer UI. Executables that support only Microsoft.Testing.Platform are currently incompatible with this VSTest runner.

Test cancellation terminates only that run's runner and owned test-host process job, leaving other Test Explorer runs alone. The latest ten runs are retained in memory, with TRX reports in isolated temporary directories. While an owned test run is active, builds, debug launches and solution/configuration changes are rejected.
