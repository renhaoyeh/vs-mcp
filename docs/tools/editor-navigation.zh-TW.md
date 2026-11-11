# 編輯與導覽

[English](editor-navigation.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

讀取未儲存 buffer、核對後修改內容，並查詢語意位置。

本頁列出 **11 個工具**。

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 編輯器文件 | `document_list`、`document_active`、`document_open`、`document_read`、`document_write`、`document_save`、`document_close` | 讀寫 IDE 內的文件，包含未儲存內容；回傳儲存／唯讀狀態，讀取時提供完整 buffer 的 SHA-256。 |
| 精準修改 | `editor_replace` | 依原文進行區分大小寫的替換；核對 SHA-256，原文不存在、重複或版本過期時拒絕修改；支援明確指定全部替換。 |
| 編輯器選取 | `editor_selection_get` | 讀取目前編輯器選取範圍與游標座標。 |
| 語意導覽 | `goto_definition`、`find_references` | 回傳符號定義／參考的檔案、起訖行列與截斷狀態；各語言限制見[首頁支援範圍](../../README.zh-TW.md)。 |

## 使用方式與限制

`editor_selection_get` 從目前未儲存的文字 buffer 讀取游標與選取座標（從 1 開始），並限制回傳文字長度。沒有作用中的文字編輯器時 data 為 null；方塊選取會回傳 `blockSelection: true` 並省略選取文字。

C++ 導覽使用該文件在 VS 中的目前專案 context。導覽的 `project` 覆寫參數僅用於 managed backend，以辨識同時屬於多個專案的 linked file。
