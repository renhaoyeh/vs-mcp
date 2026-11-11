# 診斷、dump 與符號

[English](diagnostics.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

讀取 Error List／Output、調查 dump／PDB，以及查詢原生記憶體、指令與暫存器。

本頁列出 **14 個工具**。

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| VS dump 診斷 | `debugger_open_dump`、`debugger_save_dump`、`debugger_list_modules`、`debugger_get_exception` | 使用已連線 VS 偵錯現有 Windows minidump、不覆寫地儲存暫停程序的 dump，讀取引擎提供的模組／符號資訊及已觀察的例外事件。 |
| PDB 偏好 | `debugger_get_symbol_settings`、`debugger_set_symbol_settings` | 讀取或明確更新持續保存的 PDB 搜尋路徑、啟用遮罩、快取及載入政策；立即載入符號需暫停的偵錯工作階段。回傳修改前／讀回值。 |
| 診斷 | `errors_list`、`output_list_panes`、`output_read` | 依嚴重度、專案、文字、精確錯誤碼／來源與產生工具篩選 Error List；回傳 VS 提供的工具與來源識別、提供者穩定狀態及截斷狀態。列出 Output panes 並讀取有長度上限的尾端內容。 |
| 原生引擎檢查 | `debugger_list_native_transports`、`debugger_load_module_symbols`、`debugger_read_native_memory`、`debugger_disassemble`、`debugger_read_registers` | 列出已安裝的 transport／引擎，要求載入單一模組符號，並透過目前連接的 VS 偵錯器讀取有上限的記憶體、指令與暫存器。 |

## 使用方式與限制

`errors_list` 保留 Error List 提供者的 `code`、`source`、`buildTool`、`dataSource` 與 `sourceType`。`source` 是原生 `Build`／`Other` 分類；`Other` 不能證明診斷來自 IntelliSense。位置從 1 開始，缺少的欄位為 null。`isStable` 描述已訂閱提供者的狀態，不能證明分析已完成。若改用舊式 DTE，會回報 `extendedFieldsAvailable: false` 並拒絕延伸篩選條件。

原生檢查使用目前選取的暫停程序／執行緒，以及從 1 開始的堆疊框架索引。記憶體可指定精確區域變數或十六進位位址，最多讀取 4,096 bytes；反組譯與暫存器清單也有上限。這些操作使用 VS 偵錯引擎介面，缺少引擎能力會明確回報。單一模組 PDB 載入會回傳 VS 回報的實際符號檔；符號已在搜尋路徑上時，VS 可能忽略指定的 PDB 路徑。

已有 dump 時，可先視需要用 `debugger_set_symbol_settings` 設定 PDB，再呼叫 `debugger_open_dump(path)`。預設使用 VS 原生碼偵錯引擎；`engineGuid` 可明確指定其他已安裝且支援 dump 的引擎。工具檢查副檔名／標頭，並在啟動期間鎖住檔案避免被替換，直接將指定檔案交給 VS 偵錯器，不執行 F5、不替換方案或啟始專案。開啟是非同步要求：用 `get_debug_status.request` 與 `dumpRequest` 查詢；接受要求不代表 dump 已載入。暫停後可用 `debugger_get_exception`、`debugger_list_modules`、`debugger_get_callstack`、執行緒與區域變數工具檢查，最後用 `stop_debugging` 結束。Dump／引擎相容性依主機而定。

`debugger_save_dump(path, processId, kind: "normal")` 透過 VS 擷取目前選取的暫停程序；`kind: "full"` 包含更多記憶體，檔案可能很大。目的檔案必須不存在；儲存／搬移失敗時保留暫存 dump 並回報路徑。呼叫中斷後，先檢查檔案與偵錯狀態再決定是否重試。本功能不查 WER／事件記錄、不自動搜尋 dump，也不依賴外部 cdb。

模組版本、時間戳與符號位置取自 VS，不以較新的本機 DLL 代替。提供者使用已觀察的偵錯程序，或目前程序的自動化模組清單；該介面無法提供的欄位（包括自動化清單中的 program ID／時間戳）為 null，模組讀取錯誤會明確回報。例外資訊是目前暫停程序最後被觀察到的引擎事件，可能不存在，也不代表已證明 crash 或目前停止原因。堆疊在符號可用時提供 `sourcePath` 與從 1 起算的 `sourceLine`，這些位置與時間戳不保證本機原始碼與建置相符。

符號偏好會持續保存，需從 `previous` 明確還原。`symbolPath` 以分號分隔，`symbolPathState` 每個路徑對應一個 0／1 字元，例如單一本機 PDB 目錄使用 `"1"`；空路徑／遮罩會清空搜尋清單。`loadSymbolsNow: true` 需要暫停的工作階段，可能使用已設定的符號伺服器，實際 PDB 載入結果應查模組狀態。VS 載入符號期間可能阻塞，沒有強制逾時或自動還原。
