# VS MCP

[English](README.md) | 繁體中文

讓 MCP agent 直接使用 Visual Studio 2026 的 C++／C# 開發能力。MCP 伺服器在連線的 IDE 內執行，透過 Streamable HTTP 提供方案、建置、未儲存文件、偵錯、診斷、語意導覽與測試結果。隨附的 .NET stdio 橋接程式提供固定的用戶端入口，並可明確選擇要操作的 VS 執行個體。

## 功能與工具一覽

目前有 **44 個 IDE 工具**，橋接程式另提供 **2 個執行個體管理工具**。以下列出完整名稱、用途與回傳內容；只顯示視窗、終端或移動游標的工具已從 0.4.0 移除。

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 橋接執行個體管理 | `list_vs_instances`、`connect_vs_instance` | 列出執行中的 VS、PID、instanceId、端點及即時方案資訊，並將這個橋接程式明確綁定到指定執行個體。VS 未開啟時，這兩個工具仍可使用。 |
| 執行個體與方案查詢 | `get_vs_info`、`get_projects`、`get_solution_configurations`、`get_project_dependencies` | 確認服務中的 VS PID、版本及方案路徑；查詢專案／方案資料夾、組態映射與建置依賴。 |
| 方案開關 | `solution_open`、`solution_close` | 開啟／關閉方案；拒絕未儲存文件或方案，避免遺失修改。 |
| 啟動與組態設定 | `startup_project_get`、`startup_project_set`、`build_configuration_set` | 讀取／明確設定啟動專案，或切換指定組態與平台；不會猜測其他設定。 |
| 建置 | `build_projects`、`get_build_status`、`cancel_build` | 建置、清除或重建指定專案／方案資料夾；回傳依賴順序、各專案結果、錯誤及 Build Output，可等待、查詢及取消。 |
| 編輯器文件 | `document_list`、`document_active`、`document_open`、`document_read`、`document_write`、`document_save`、`document_close` | 讀寫 IDE 內的文件，包含未儲存內容；回傳儲存／唯讀狀態，讀取時提供完整 buffer 的 SHA-256。 |
| 精準修改 | `editor_replace` | 依原文進行區分大小寫的替換；核對 SHA-256，原文不存在、重複或版本過期時拒絕修改；支援明確指定全部替換。 |
| 偵錯啟停與狀態 | `start_debugging`、`stop_debugging`、`get_debug_status` | 啟停目前啟動專案，讀取 debugger mode、程序 PID／路徑及請求狀態；不會自行切換啟動專案或組態。 |
| 偵錯執行控制 | `debugger_break`、`debugger_continue`、`debugger_step_into`、`debugger_step_over`、`debugger_step_out` | 暫停、繼續與逐步執行；檢查目前偵錯模式後呼叫 debugger API。 |
| 斷點 | `debugger_add_breakpoint`、`debugger_list_breakpoints`、`breakpoint_enable`、`debugger_remove_breakpoint` | 新增檔案／行號斷點，可指定條件；列出、啟停及移除斷點。是否繫結仍取決於已載入的符號。 |
| 偵錯資料 | `debugger_get_locals`、`debugger_get_callstack`、`debugger_evaluate` | 暫停時讀取目前 frame 的區域變數、目前執行緒的呼叫堆疊，以及運算式結果；結果數量有上限。 |
| 診斷 | `errors_list`、`output_list_panes`、`output_read` | 依嚴重度、專案與文字篩選 Error List；列出 Output panes 並讀取有長度上限的尾端內容。 |
| 語意導覽 | `goto_definition`、`find_references` | 回傳符號定義／參考的檔案、起訖行列與截斷狀態；各語言的支援限制見下表。 |
| 測試 | `test_run`、`test_status`、`test_cancel` | 執行指定的已建置測試 DLL／EXE，回傳 run ID、執行狀態、TRX 統計、個別結果、失敗訊息與堆疊；支援篩選、等待及取消。 |
| 進階擴充 | `execute_code` | 在 VS UI thread 執行 C# method body，存取 DTE 與 VS services，回傳可序列化資料。此工具具有任意程式執行能力。 |

完整參數、預設值與結果 schema 由官方 MCP SDK 產生，透過 MCP 的 `tools/list` 提供。[src/VsMcp.Bridge/tools.json](src/VsMcp.Bridge/tools.json) 保存產生的 IDE 工具目錄，讓橋接程式在 VS 尚未啟動時仍能提供固定的工具定義；連接時會核對實際 IDE 的工具目錄。

## C++／C# 支援範圍

| 能力 | C# | C++ | 條件與限制 |
| --- | --- | --- | --- |
| 方案、專案與組態 | 支援 | 支援 | 只操作目前連線的 VS，不會改連其他執行個體。 |
| 建置、清除、重建 | 支援 | 支援原生專案 | 使用目前組態及平台；需安裝對應的工作負載、SDK 與 toolset。 |
| 未儲存文件讀寫 | 支援 | 支援 | 讀取 IDE buffer；修改需提供讀取時的 SHA-256。 |
| 偵錯與資料查詢 | Managed debug engine | Native debug engine | 變數、堆疊與求值需處於暫停狀態；符號與運算式能力取決於偵錯引擎。 |
| 定義位置 | Live Roslyn workspace，包含未儲存修改 | 原生 Peek Definitions provider | C++ 可能開啟文件，但不顯示 Peek UI；需等待 IntelliSense 就緒。 |
| 參考位置 | Live Roslyn workspace | **取決於原生 provider** | C++ 需要 Peek References provider；缺少時回傳 `NOT_SUPPORTED`，不以文字搜尋冒充語意參考。 |
| 測試結果 | VSTest 相容的 managed 測試 | VSTest 相容的 native 測試 | 需有相容 adapter；`platform` 要符合測試程式的 x64／x86／ARM64 架構。 |

C++ 導覽使用該文件在 VS 中的目前專案 context。導覽的 `project` 覆寫參數僅用於 managed backend，以辨識同時屬於多個專案的 linked file。

測試工具執行已建置的二進位檔，不會自動 build／restore，也不會同步 Test Explorer 畫面。僅支援 Microsoft.Testing.Platform 的執行檔目前不適用此 VSTest runner。

## 如何判斷操作完成

| 操作 | 呼叫後代表什麼 | agent 接下來應檢查什麼 |
| --- | --- | --- |
| 建置 | 回傳實際結果，或等待逾時後回傳 `running`；逾時不會取消建置。 | 呼叫 `get_build_status`，直到取得最終狀態。 |
| 偵錯啟停、暫停、繼續、逐步 | 指令／API 請求已接受，不保證目標程序已完成狀態切換。 | 用 `get_debug_status` 檢查 mode、請求狀態及程序資料；不要盲目重送。 |
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

測試取消只終止該工作的 runner 與其擁有的 test-host process job，不影響其他 Test Explorer 工作。記憶體保留最近十次測試，TRX 寫入隔離的暫存目錄。自有測試執行期間會拒絕建置、啟動偵錯與方案／組態切換。

一般磁碟搜尋、建立檔案／資料夾等工作交由 agent 原有的檔案工具處理。此 MCP 專注於 IDE 狀態、未儲存內容與開發流程。

## 安裝與連線

從 GitHub Releases 下載 `VsMcp.vsix`。也可以手動執行 GitHub Actions 的 `Release VSIX` 工作流程，完成後下載 `VsMcp-vsix` artifact。

建置需求：Windows、Visual Studio 2026 的 C#／.NET、Desktop C++ 與 extension-development 工作負載、.NET 10 SDK，以及 .NET Framework 4.7.2 targeting pack。

在 Visual Studio 開啟 `VsMcp.slnx`，選擇 Release 並建置 `VsMcp.Vsix` 專案。安裝包會產生在 `src/VsMcp.Vsix/bin/Release/net472/VsMcp.vsix`。

安裝 VSIX 後先啟動 VS 一次，擴充套件會將隨附的橋接程式安裝到 `%LOCALAPPDATA%\VsMcp\Bridge\VsMcp.Bridge.exe`。橋接程式需要 .NET 10 runtime。請使用自己帳號的完整執行檔路徑設定 MCP 用戶端：

```json
{
  "mcpServers": {
    "visual-studio": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\Bridge\\VsMcp.Bridge.exe"
    }
  }
}
```

先呼叫 `list_vs_instances`，再用選定的 `pid`、`instanceId` 呼叫 `connect_vs_instance`。橋接程式從 `%LOCALAPPDATA%\VsMcp\instances` 讀取登記資料，核對程序並查詢即時身分資訊。VS 未開啟時橋接仍可使用，但 IDE 操作需要綁定執行中的 VS；橋接不會啟動 VS。關閉並重啟 VS 後，必須明確選擇新的執行個體。連線失敗不會自動重送工具呼叫；後續呼叫會重新連接同一個綁定目標。若回傳 `outcomeUnknown`，應先檢查操作效果再決定是否重試。更新橋接程式前請先關閉橋接用戶端，再重新啟動 VS。

直接使用 HTTP 的用戶端仍可連接 `http://127.0.0.1:18770/mcp`。未設定 `VS_MCP_PORT` 時，VS 會先嘗試 18770，碰撞時選擇空閒連接埠，由橋接程式探索實際端點。啟動 VS 前設定 `VS_MCP_PORT` 則會固定連接埠，不會自動改用其他埠。伺服器只監聽 loopback，驗證 Host／Origin，目前沒有認證 token。橋接請求另會識別選定的登記資料，避免連接埠被重用時把呼叫送到另一個執行個體。

`execute_code` 以使用者權限在 UI thread 執行任意程式碼，沒有 sandbox、執行逾時或 rollback。任何修改操作若遇到連線中斷，都應先檢查效果，再決定是否重試。

## 架構與驗證

| 目錄 | 職責 |
| --- | --- |
| `Mcp/` | HTTP transport、官方 MCP SDK、協定錯誤與序列化。 |
| `Tools/` | MCP 工具名稱、描述、schema 與請求介面。 |
| `Contracts/` | 共用的型別化結果與 JSON 資料契約。 |
| `Services/` | 依方案、建置、文件、偵錯、語言導覽及測試拆分的服務。 |
| `Infrastructure/` | VSTest 子程序、參數處理、輸出擷取與 Windows process job。 |

0.4.0 已安裝至 VS 2026 Community 實驗執行個體驗證：**45 項整合測試、11 項橋接測試、9 項單元測試全部通過**，另通過 HTTP harness 與 live 工具 schema 比對。C++／C# 的建置、文件、測試及偵錯，以及透過 stdio 控制兩個自動分配連接埠的 VS 執行個體皆有實測；C++ 參考查詢仍受上述 provider 能力限制。

更完整的架構、依賴版本、測試指令與實驗執行個體流程請見 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 授權

本專案採用 [MIT License](LICENSE)。
