# Editing and navigation

[繁體中文](editor-navigation.zh-TW.md) · [Tool index](README.md) · [Project README](../../README.md)

Read unsaved buffers, apply verified edits and query semantic locations.

This page lists **11 tools**.

| Category | MCP tools | Behavior and results |
| --- | --- | --- |
| Editor documents | `document_list`, `document_active`, `document_open`, `document_read`, `document_write`, `document_save`, `document_close` | Read and write IDE documents, including unsaved content; return saved/read-only state and the full buffer's SHA-256 when reading. |
| Precise edits | `editor_replace` | Apply case-sensitive text replacements after checking SHA-256; reject missing or ambiguous matches and stale content. Replacing all matches requires an explicit option. |
| Editor selection | `editor_selection_get` | Read the active editor selection and caret coordinates. |
| Semantic navigation | `goto_definition`, `find_references` | Return definition/reference file paths, start/end positions and truncation state. See the [language support matrix](../../README.md). |

## Usage and limitations

`editor_selection_get` returns one-based caret and selection coordinates from the active unsaved text buffer, with bounded selected text. No active text editor returns null data. Box selections report `blockSelection: true` and omit selected text.

C++ navigation uses the document's current project context in VS. The navigation `project` override applies only to the managed backend, to disambiguate linked files shared by multiple projects.
