# VS MCP

[English](README.md) | 繁體中文

建置優先使用 VS MCP。目標專案看似不存在時，先用 `get_vs_info` 確認連線的執行個體與方案，再查 `get_projects(recursive: true)` 並使用精確的 unique name。`get_projects` 回報 `isLoaded: false` 時，使用該專案的 unique name 呼叫 `project_reload`，確認 `isLoaded: true` 後繼續用 VS MCP 建置。方案內已卸載的專案使用 `project_reload`；這個工具無法加入不存在的專案。先確認是否連到正確方案，再判斷是否需要 fallback。切換方案或加入專案須符合使用者授權範圍。檢查後 VS MCP 路徑仍受阻時，可以改用 MSBuild、`dotnet build` 或其他適合的建置方式，並說明具體阻礙與 fallback 原因，保留其他執行中的應用程式。

讓 MCP agent 直接使用 Visual Studio 2026 的 C++／C# 開發能力。MCP 伺服器在連線的 IDE 內執行，透過 Streamable HTTP 提供方案、建置、未儲存文件、偵錯、診斷、語意導覽與測試結果。獨立安裝的 stdio 橋接程式提供固定的用戶端入口，並可明確選擇要操作的 VS 執行個體。

## 安裝與連線

使用 VS MCP 需要 Windows x64 與 Visual Studio 2026。請安裝同一版本的 `VsMcp.vsix` 與 `VsMcp.Bridge.Setup.exe`。Bridge Setup 包含所需的 .NET runtime，不必另外安裝 .NET 10，也不需要系統管理員權限。

擴充套件使用連線之 Visual Studio 安裝所提供的共用 .NET 組件與繫結原則，不要求固定的組件修補版本。stdio 橋接程式則使用隨附的 runtime 獨立執行。

可以將以下指令直接貼給 agent，請它協助下載、安裝與設定：

```text
請幫我在 Windows 安裝 VS MCP，並設定目前使用的 MCP 用戶端。

1. 找到已安裝的 Visual Studio 2026；若有多個執行個體，先詢問我要使用哪一個。
2. 從 https://github.com/renhaoyeh/vs-mcp/releases 的同一個最新穩定版本下載 VsMcp.vsix 與 VsMcp.Bridge.Setup.exe。若缺少其中一份，請明確回報，不要宣稱安裝成功。
3. 使用選定 Visual Studio 安裝目錄中的 VSIXInstaller 安裝 VSIX。若需要關閉 Visual Studio，請先通知我儲存工作並關閉，再繼續。執行 Bridge Setup，為目前帳號安裝橋接程式；runtime 已隨附，不必另外安裝 .NET。
4. 確認 %LOCALAPPDATA%\Programs\VsMcp\VsMcp.Launcher.exe 與 mcp-config.json 已產生，並解析成目前帳號的實際完整路徑。安裝 Bridge 不需要先啟動 Visual Studio。
5. 依目前 MCP 用戶端支援的設定格式，新增名為 visual-studio 的伺服器，以該完整路徑作為執行命令，保留其他既有設定。若用戶端需要重新載入或重啟，請告訴我要怎麼操作。
6. 工具可用後，依序呼叫 list_vs_instances、以選定的 pid 與 instanceId 呼叫 connect_vs_instance，再呼叫 get_vs_info，確認連到正確的 Visual Studio。最後回報安裝版本與實際驗證結果；若驗證受阻，請說明原因。
```

從 GitHub Releases 下載 `VsMcp.vsix` 與 `VsMcp.Bridge.Setup.exe`。也可以手動執行 GitHub Actions 的 `Release VSIX and Bridge` 工作流程，完成後下載 `VsMcp-release` artifact。

安裝 VSIX 並執行 Bridge Setup。Setup 會安裝到 `%LOCALAPPDATA%\Programs\VsMcp`、在 Windows「已安裝的應用程式」登記解除安裝程式，並開啟 `mcp-config.json` 供複製，不會直接修改用戶端設定。請使用自己帳號的完整 Launcher 路徑設定 MCP 用戶端：

```json
{
  "mcpServers": {
    "visual-studio": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\Programs\\VsMcp\\VsMcp.Launcher.exe"
    }
  }
}
```

先呼叫 `list_vs_instances`，再用選定的 `pid`、`instanceId` 呼叫 `connect_vs_instance`。橋接程式從 `%LOCALAPPDATA%\VsMcp\instances` 讀取登記資料，核對程序並查詢即時身分資訊。VS 未開啟時橋接仍可使用，但 IDE 操作需要綁定執行中的 VS；橋接不會啟動 VS。關閉並重啟 VS 後，必須明確選擇新的執行個體。連線失敗不會自動重送工具呼叫；後續呼叫會重新連接同一個綁定目標。若回傳 `outcomeUnknown`，應先檢查操作效果再決定是否重試。

Setup 將完整 Bridge 放入 `versions/` 下的獨立目錄，逐檔驗證 SHA-256 後才原子更新 `current.txt`。既有連線繼續使用原版本；升級後重新啟動 MCP 連線即可使用新版，入口路徑不變。若原生 Launcher 本身需要更新且 Windows 回報檔案使用中，必須先關閉 MCP 連線。舊版本保留到解除安裝；解除安裝前請先關閉 MCP 用戶端。

**從 0.5.0 或更早版本升級：**執行 Bridge Setup，將原本 `%LOCALAPPDATA%\VsMcp\Bridge\VsMcp.Bridge.exe` 的 command 改成上方 Launcher 路徑，再重新啟動 MCP 連線。新版 VSIX 不再於啟動時複製 Bridge。Setup 不會刪除舊目錄或終止舊程序；所有用戶端完成遷移並停止使用後，才可移除舊目錄。請同時確認 MCP 初始化回應的 `serverInfo.version` 與 VS 擴充版本；僅顯示「已安裝」不代表連線正常。

直接使用 HTTP 的用戶端仍可連接 `http://127.0.0.1:18770/mcp`。未設定 `VS_MCP_PORT` 時，VS 會先嘗試 18770，碰撞時選擇空閒連接埠，由橋接程式探索實際端點。啟動 VS 前設定 `VS_MCP_PORT` 則會固定連接埠，不會自動改用其他埠。伺服器只監聽 loopback，驗證 Host／Origin，目前沒有認證 token。橋接請求另會識別選定的登記資料，避免連接埠被重用時把呼叫送到另一個執行個體。

`execute_code` 以使用者權限在 UI thread 執行任意程式碼，沒有 sandbox、執行逾時或 rollback。任何修改操作若遇到連線中斷，都應先檢查效果，再決定是否重試。

## 功能與工具一覽

目前有 **85 個 IDE 工具**，橋接程式另提供 **2 個執行個體管理工具**。首頁依工作分類；完整工具名稱、行為與限制放在各分類文件。

| 工作分類 | 能力摘要 |
| --- | --- |
| [連線與 IDE 控制](docs/tools/connection.zh-TW.md) | 選擇 VS 執行個體、控制視窗，或執行進階 C# 自動化。 |
| [專案與建置](docs/tools/projects-build.zh-TW.md) | 管理方案、組態、建置設定、C++ 屬性、輸出與專案檔案。 |
| [編輯與導覽](docs/tools/editor-navigation.zh-TW.md) | 讀取未儲存 buffer、核對後修改內容，並查詢語意位置。 |
| [偵錯流程](docs/tools/debugging.zh-TW.md) | 啟動／附加、選取程序與框架、查詢變數、設定斷點與例外原則。 |
| [診斷、dump 與符號](docs/tools/diagnostics.zh-TW.md) | 讀取 Error List／Output、調查 dump／PDB，以及查詢原生記憶體、指令與暫存器。 |
| [測試](docs/tools/testing.zh-TW.md) | 探索與執行已建置測試、收集結果，並取消自有測試工作。 |

[工具索引與 Agent 操作流程](docs/tools/README.zh-TW.md)另列完成判斷與常用順序。參數、預設值及 schema 由 MCP `tools/list` 和產生的 [SDK 工具目錄](src/VsMcp.Bridge/tools.json)提供。

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

## 架構

| 目錄 | 職責 |
| --- | --- |
| `Mcp/` | HTTP transport、官方 MCP SDK、協定錯誤與序列化。 |
| `Tools/` | MCP 工具名稱、描述、schema 與請求介面。 |
| `Contracts/` | 共用的型別化結果與 JSON 資料契約。 |
| `Services/` | 依方案、建置、文件、偵錯、語言導覽及測試拆分的服務。 |
| `Infrastructure/` | VSTest 子程序、參數處理、輸出擷取與 Windows process job。 |

更完整的架構、依賴版本、測試指令與實驗執行個體流程請見 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 授權

本專案採用 [MIT License](LICENSE)。
