# 偵錯流程

[English](debugging.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

啟動／附加、選取程序與框架、查詢變數、設定斷點與例外原則。

本頁列出 **31 個工具**。

## 啟停與程序

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 偵錯啟停與狀態 | `start_debugging`、`start_without_debugging`、`without_debugging_reconcile`、`stop_debugging`、`get_debug_status` | 啟停目前啟動專案，讀取 debugger mode、程序 PID／路徑及請求狀態；不會自行切換啟動專案或組態。 |
| 附加偵錯工具 | `debugger_attach` | 驗證執行檔絕對路徑後附加至本機 PID，可指定偵錯引擎；需要閒置的 IDE。 |
| 偵錯程序 | `debugger_processes`、`debugger_select_process`、`debugger_detach_process` | 列出本機與已偵錯程序，在暫停內容中選取已附加程序，或以精確 PID／路徑卸離並保留程序。 |
| 偵錯執行控制 | `debugger_break`、`debugger_continue`、`debugger_step_into`、`debugger_step_over`、`debugger_step_out` | 暫停、繼續與逐步執行；檢查目前偵錯模式後呼叫 debugger API。 |

## 框架、變數與求值

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 偵錯環境選取 | `debugger_list_threads`、`debugger_select_thread`、`debugger_select_frame` | 列舉目前偵錯程式的執行緒；暫停時切換執行緒或堆疊 frame，並讀回確認選取結果。 |
| 偵錯資料 | `debugger_get_locals`、`debugger_get_callstack`、`debugger_evaluate` | 暫停時讀取目前 frame 的區域變數、目前執行緒的呼叫堆疊，以及運算式結果；結果數量有上限。 |
| 變數展開 | `debugger_variables` | 在目前框架展開有上限的運算式子項目，或直接讀取指定框架的參數而不切換選取。需要精確的暫停內容／程序／執行緒；求值與 getter 可能執行受偵錯程式碼，逾時只限制最初運算式求值，不保證整段展開的總時間。 |

## 斷點與例外原則

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 斷點 | `debugger_add_breakpoint`、`debugger_list_breakpoints`、`breakpoint_enable`、`debugger_remove_breakpoint` | 新增檔案／行號斷點，可指定條件；列出、啟停及移除斷點。是否繫結仍取決於已載入的符號。 |
| 進階斷點 | `debugger_breakpoint_add`、`debugger_breakpoint_list`、`debugger_breakpoint_update`、`debugger_breakpoint_remove` | 新增檔案、函式、資料或位址斷點及條件／命中次數；查詢綁定狀態，並以精確 handle 更新／移除單一斷點。支援程度依偵錯引擎而異。 |
| 例外中斷原則 | `debugger_exception_policy_get`、`debugger_exception_policy_set` | 先查詢有上限的例外群組／設定，避免猜測本地化名稱；讀取或明確修改單一例外的擲回／使用者未處理時中斷原則。 |

## 使用方式與限制

`start_without_debugging` 沿用目前組態與啟始專案執行 Ctrl+F5。僅支援 Built 輸出群組提供單一 EXE 的專案（受控 DLL 需要 EXE apphost）。觀察到請求後建立、符合預期路徑且為此 VS 子孫的所有目標程序，才回傳 `started`、PID 與路徑；這不代表應用程式已就緒。否則回傳 `unconfirmed`，背景確認最多持續 120 秒，取消呼叫不會取消啟動。使用 `get_debug_status.withoutDebuggingRequest` 查閱結果。建置、測試、偵錯及未確認的啟動請求會阻擋重複啟動。未確認狀態會繼續阻擋啟動。可用 `without_debugging_reconcile(requestId)` 重新核對程序，背景確認逾時後仍可使用。檢查 VS 後，明確傳入 `abandonTracking: true` 與 `acknowledgeLaunchMayStillOccur: true`，會將未確認請求標記為 `abandoned` 並解除追蹤鎖定；這不會取消已排入的命令、不代表啟動失敗，也不會終止程序，之後仍可能發生延遲啟動。過期 request ID 不會影響新請求；建置、測試或偵錯中禁止放棄追蹤。短命程序及分離的啟動器可能無法確認；不支援外部程式、瀏覽器及 dotnet 託管 DLL 啟動。`stop_debugging` 不會終止這些程序。

`debugger_attach(processId, expectedPath, engine?)` 比對本機執行檔路徑後透過已連線的偵錯工具附加；附加中斷後應先讀取 `get_debug_status`，再決定是否重試。

`debugger_list_threads` 與 `debugger_get_callstack` 回傳 `contextId`、`processId`；call stack 另含 `threadId` 與從 1 開始的 `frameIndex`。選取工具要求帶入這些值；繼續、逐步執行、偵錯模式變更或切換偵錯 program 後，舊 token 失效。切換執行緒後，先重新取得 call stack 再選 frame。選取不會繼續執行、移動指令指標、凍結執行緒或切換偵錯程序／program；後續 locals 與運算式求值使用選取的 frame，支援程度取決於偵錯引擎。
