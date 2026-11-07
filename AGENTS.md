# VS MCP contributor rules

- Use the official ModelContextProtocol.Core SDK and attributed tools; do not implement JSON-RPC manually.
- All DTE access must run on the Visual Studio UI thread.
- Operate only on the connected instance. Include instance identity in results.
- Builds use the active configuration, enumerate dependencies, and never switch configuration implicitly. Configuration and startup project changes require their dedicated tools.
- Keep tools, README.md, README.zh-TW.md and the generated src/VsMcp.Bridge/tools.json catalog aligned.
- Run the protocol harness and build the VSIX. Run integration tests in the experimental hive when available; report environment blockers accurately.
- Do not retry interrupted execute_code or other mutations without checking their effects.
- Keep product names, identifiers, examples and fixtures generic.
