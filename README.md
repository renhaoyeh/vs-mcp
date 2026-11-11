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

There are **85 IDE tools**, plus **2 bridge instance-management tools**. Start with a workflow category; each reference page lists the complete tool names, behavior and limitations.

| Workflow | Capabilities |
| --- | --- |
| [Connection and IDE control](docs/tools/connection.md) | Choose a VS instance, control its window, or run advanced C# automation. |
| [Projects and builds](docs/tools/projects-build.md) | Manage solutions, configurations, build settings, C++ properties, outputs and project files. |
| [Editing and navigation](docs/tools/editor-navigation.md) | Read unsaved buffers, apply verified edits and query semantic locations. |
| [Debugger workflows](docs/tools/debugging.md) | Launch/attach, select processes and frames, inspect variables, set breakpoints and exception policies. |
| [Diagnostics, dumps and symbols](docs/tools/diagnostics.md) | Read Error List/Output, inspect dumps/PDBs and read native memory, instructions and registers. |
| [Testing](docs/tools/testing.md) | Discover and run already-built tests, collect results and cancel owned runs. |

See the [tool index and agent workflows](docs/tools/README.md) for completion checks and common sequences. Parameter defaults and schemas are available through MCP `tools/list` and the generated [SDK catalog](src/VsMcp.Bridge/tools.json).

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
