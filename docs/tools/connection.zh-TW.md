# 連線與 IDE 控制

[English](connection.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

選擇 VS 執行個體、控制視窗，或執行進階 C# 自動化。

本頁列出 **6 個工具**。

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 橋接執行個體管理 | `list_vs_instances`、`connect_vs_instance` | 列出執行中的 VS、PID、instanceId、端點及即時方案資訊，並將這個橋接程式明確綁定到指定執行個體。VS 未開啟時，這兩個工具仍可使用。 |
| 尋找或啟動方案的 VS | `launch_vs_instance` | 依 `solutionPath` 絕對路徑尋找已開啟的方案，或以新的 Visual Studio 2026 行程開啟。回傳 PID、instanceId、端點、即時執行個體資訊與 `reused`。 |
| 執行個體識別 | `get_vs_info` | 綁定後確認 VS PID、版本與目前方案路徑。 |
| VS 主視窗 | `show_vs_window` | 用 `visible` 顯示／隱藏目前連線的 VS 主視窗，預設 true。只有明確指定 `activate: true` 才呼叫啟用，預設 false，且啟用時必須顯示。回傳 visible、activationRequested、標題及執行個體識別。 |
| 進階擴充 | `execute_code` | 在 VS UI thread 執行 C# method body，存取 DTE 與 VS services，回傳可序列化資料。此工具具有任意程式執行能力。 |

## 使用方式與限制

`launch_vs_instance` 會比對已登記且可連線的 VS 所回報的 `.sln` 或 `.slnx` 絕對路徑。找到唯一且符合條件的執行個體時，回傳 `reused: true`。若有多個符合方案的執行個體，或無法確認現有 VS 符合指定安裝或實驗設定檔，回傳 `VS_SOLUTION_ALREADY_OPEN` 和符合方案的清單。請明確選擇 PID，或在使用者要求另一個行程時傳入 `forceNew: true`。啟動時若有多套 VS，請以 `devenvPath` 指定安裝。明確要求實驗設定檔時可傳入 `rootSuffix`。橋接程式關閉後，新 VS 仍會繼續執行。若等待確認超過 120 秒或呼叫中斷，先檢查可取得的 PID 並呼叫 `list_vs_instances`，再判斷是否重試。

`show_vs_window` 傳入 `visible: false` 可隱藏 VS，不會關閉 VS 或停止建置／偵錯。顯示時預設不呼叫前景啟用；傳入 `visible: true, activate: true` 才會明確要求啟用。`activationRequested` 表示已提出要求，不代表作業系統已將焦點切過去。這不是持續背景模式：目前開始偵錯仍會顯示並啟用 VS，VS 也可能在命中斷點時自行顯示。
