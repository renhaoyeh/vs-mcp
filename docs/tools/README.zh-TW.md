# 工具參考文件

[English](README.md) · [VS MCP](../../README.zh-TW.md)

伺服器提供 **85 個 IDE 工具**，橋接程式另提供 **2 個執行個體管理工具**。依任務選擇分類，查閱完整名稱、行為與限制；下表連線分類的數量包含兩個橋接工具。

| 分類 | 工具數 | 內容 |
| --- | --- | --- |
| [連線與 IDE 控制](connection.zh-TW.md) | 5 | 選擇 VS 執行個體、控制視窗，或執行進階 C# 自動化。 |
| [專案與建置](projects-build.zh-TW.md) | 22 | 管理方案、組態、建置設定、C++ 屬性、輸出與專案檔案。 |
| [編輯與導覽](editor-navigation.zh-TW.md) | 11 | 讀取未儲存 buffer、核對後修改內容，並查詢語意位置。 |
| [偵錯流程](debugging.zh-TW.md) | 31 | 啟動／附加、選取程序與框架、查詢變數、設定斷點與例外原則。 |
| [診斷、dump 與符號](diagnostics.zh-TW.md) | 14 | 讀取 Error List／Output、調查 dump／PDB，以及查詢原生記憶體、指令與暫存器。 |
| [測試](testing.zh-TW.md) | 4 | 探索與執行已建置測試、收集結果，並取消自有測試工作。 |

完整參數、預設值與結果 schema 由官方 MCP SDK 產生，透過 MCP 的 `tools/list` 提供。[src/VsMcp.Bridge/tools.json](../../src/VsMcp.Bridge/tools.json) 保存產生的 IDE 工具目錄，讓橋接程式在 VS 尚未啟動時仍能提供固定的工具定義；連接時會核對實際 IDE 的工具目錄。

## 如何判斷操作完成

| 操作 | 呼叫後代表什麼 | agent 接下來應檢查什麼 |
| --- | --- | --- |
| 建置 | 回傳實際結果，或等待逾時後回傳 `running`；逾時不會取消建置。 | 呼叫 `get_build_status`，直到取得最終狀態。 |
| 偵錯啟停、暫停、繼續、逐步 | 指令／API 請求已接受，不保證目標程序已完成狀態切換。 | 用 `get_debug_status` 檢查 mode、請求狀態及程序資料；不要盲目重送。 |
| Ctrl+F5 | `started` 表示已觀察到目標程序；`unconfirmed` 表示尚未確認。 | 查閱 `get_debug_status.withoutDebuggingRequest`；未確認前不要重試。 |
| 文件修改 | 編輯器 buffer 已修改，通常尚未儲存到磁碟。 | 檢查回傳文件狀態；需要落盤時呼叫 `document_save`，或在覆寫時明確指定 `save`。 |
| 語意導覽 | 回傳位置資料，或明確回報不支援／查詢失敗。 | 使用檔案及行列結果；不需從畫面猜測查詢是否成功。 |
| 測試 | 回傳 run ID 與目前狀態；結束後提供實際 TRX 結果。 | 用 `test_status` 收集結果；`noTests` 不代表通過，runner 錯誤或缺少報告也不會回報成功。 |

## Agent 操作流程

| 工作 | 建議順序 | 保護與注意事項 |
| --- | --- | --- |
| 確認目標 | `list_vs_instances` → 用選定的 PID 與 instanceId 呼叫 `connect_vs_instance` → `get_vs_info` → 檢查版本與方案路徑。 | 每個橋接程式綁定一個執行個體；必須等待執行中的呼叫結束，再明確要求更換目標。直接使用 HTTP 的用戶端應先檢查 `get_vs_info`。 |
| 修改程式 | `document_open` → `document_read` → 將回傳的 `sha256` 作為 `expectedSha256` 傳給 `editor_replace` 或 `document_write` → 視需要儲存。 | hash 過期時先重新讀取；一次修改是一個 undo transaction。讀取有行數／字數上限，行列從 1 開始。 |
| 建置 | 查詢專案、依賴及目前組態 → `build_projects` → 必要時 `get_build_status`。 | build／rebuild 納入必要依賴；clean 不額外加入依賴。建置本身不切換組態。 |
| 執行測試 | 先完成建置 → `test_run` 指定絕對 DLL／EXE 路徑、可選 filter／settings／adapter 路徑及架構 → `test_status`。 | 連線中斷不會取消測試；遺失 run ID 時，可省略 `test_status` 的 `runId` 查詢最近一次工作。取消必須指定 run ID。 |
| 調查執行狀態 | 設定斷點 → 啟動偵錯 → 確認暫停 → 查 locals／call stack／expression → 逐步或繼續 → 停止。 | expression evaluation 可能執行目標程式碼並產生副作用。 |

一般磁碟搜尋、建立檔案／資料夾等工作交由 agent 原有的檔案工具處理。此 MCP 專注於 IDE 狀態、未儲存內容與開發流程。
