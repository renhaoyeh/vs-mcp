# Diagnostics, dumps and symbols

[繁體中文](diagnostics.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Read Error List/Output, inspect dumps/PDBs and read native memory, instructions and registers.

This page lists **14 tools**.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| VS dump diagnostics | `debugger_open_dump`, `debugger_save_dump`, `debugger_list_modules`, `debugger_get_exception` | Debug an existing Windows minidump in VS, save a paused process dump without overwriting, and inspect engine-reported modules/symbols and observed exception events. Uses the connected VS debugger. |
| PDB preferences | `debugger_get_symbol_settings`, `debugger_set_symbol_settings` | Read or explicitly update persistent PDB search paths, enabled-path mask, cache and loading policy; optional immediate symbol loading requires a paused session. Returns previous/read-back settings. |
| Native engine inspection | `debugger_list_native_transports`, `debugger_load_module_symbols`, `debugger_read_native_memory`, `debugger_disassemble`, `debugger_read_registers` | Enumerate installed transports/engines, request symbols for one module, and read bounded memory, instructions and registers through the connected VS debugger. |
| Diagnostics | `errors_list`, `output_list_panes`, `output_read` | Filter Error List by severity, project, text, exact error code/source and producer; return VS-supplied build tool and source identifiers, provider stability and truncation. List Output panes and read a bounded tail. |

## Usage and limitations

`errors_list` preserves the Error List provider's `code`, `source`, `buildTool`, `dataSource` and `sourceType`. `source` is the native `Build`/`Other` classification: `Other` does not establish that a diagnostic came from IntelliSense. Positions are one-based; absent fields are null. `isStable` describes the subscribed providers and does not prove analysis is complete. A legacy DTE fallback advertises `extendedFieldsAvailable: false` and rejects extended filters.

Native inspection operates on the selected paused process/thread and a one-based frame index. Memory reads accept one exact local variable or hexadecimal address, up to 4,096 bytes; disassembly and register lists are bounded. These operations use VS debug-engine interfaces. Missing engine capabilities are reported explicitly. Per-module PDB loading returns the actual symbol file reported by VS; VS can ignore a requested PDB path when symbols are already on its search path.

For a dump already on disk, set PDB preferences with `debugger_set_symbol_settings` if needed, then call `debugger_open_dump(path)`. The default engine is VS native-code debugging; `engineGuid` can explicitly select another installed dump-capable engine. The tool validates the extension/header and keeps the file locked against replacement during launch. It invokes the debugger on that file directly, without F5 or replacing the solution/startup projects. Opening is asynchronous: inspect `get_debug_status.request` and `dumpRequest`; acceptance alone is not a successfully loaded dump. Once paused, inspect `debugger_get_exception`, `debugger_list_modules`, `debugger_get_callstack`, threads and locals, then end the session with `stop_debugging`. Dump/engine compatibility is host-dependent.

`debugger_save_dump(path, processId, kind: "normal")` captures the selected paused process using VS; `kind: "full"` includes more memory and can be large. The destination must be new. A failed save/move preserves its temporary dump and reports the path. Inspect files and debug status after interruption before issuing another mutation. No WER/event-log search, automatic dump discovery or external cdb dependency is included.

Module versions, timestamps and symbol locations come from VS, not a newer local DLL. The provider uses observed debug programs or the exact current process's automation module list; fields unavailable through that provider (including program IDs/timestamps in the automation view) are null, and module read errors are explicit. Exception information is the last engine event observed for the current paused process: it may be absent and is not proof of a crash or the current stop cause. Call stacks include `sourcePath` and one-based `sourceLine` when symbols provide them; those locations and timestamps do not prove that local source matches the build.

Symbol preferences persist until explicitly restored from `previous`. `symbolPath` is semicolon-separated and `symbolPathState` has one 0/1 character per path; for example a single local PDB directory uses mask `"1"`. Empty path/mask clears the search list. `loadSymbolsNow: true` requires a paused session and can use configured symbol servers; inspect module status for actual PDB loading. This may block during VS symbol loading and has no forced timeout or rollback.
