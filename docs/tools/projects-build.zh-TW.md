# 專案與建置

[English](projects-build.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

建置優先使用 VS MCP。目標專案看似不存在時，先用 `get_vs_info` 確認連線的執行個體與方案，再查 `get_projects(recursive: true)` 並使用精確的 unique name。`get_projects` 回報 `isLoaded: false` 時，使用該專案的 unique name 呼叫 `project_reload`，確認 `isLoaded: true` 後繼續用 VS MCP 建置。方案內已卸載的專案使用 `project_reload`；這個工具無法加入不存在的專案。先確認是否連到正確方案，再判斷是否需要 fallback。切換方案或加入專案須符合使用者授權範圍。檢查後 VS MCP 路徑仍受阻時，可以改用 MSBuild、`dotnet build` 或其他適合的建置方式，並說明具體阻礙與 fallback 原因，保留其他執行中的應用程式。

管理方案、組態、建置設定、C++ 屬性、輸出與專案檔案。

本頁列出 **22 個工具**。

## 方案與專案項目

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 專案查詢 | `get_projects`、`get_solution_configurations`、`get_project_dependencies` | 查詢專案／方案資料夾、組態映射與建置依賴。 |
| 方案開關 | `solution_open`、`solution_close` | 開啟／關閉方案；拒絕未儲存文件或方案，避免遺失修改。 |
| 卸載與重新載入專案 | `project_unload`、`project_reload` | 對應方案總管右鍵的「卸載專案」與「重新載入專案」，以完整專案名稱或唯一名稱指定，保留方案中的專案項目。要求 IDE 閒置，且文件、方案與目標專案皆已儲存。重新載入僅接受已卸載的專案。 |
| 專案階層 | `project_items` | 列舉專案檔案、filter 與可取得的實體路徑。 |
| 專案輸出與項目 | `project_outputs`、`project_item_add`、`project_item_remove` | 不經建置讀取專案系統提供的輸出路徑；把現有檔案加入專案／filter，或以精確階層路徑排除項目並保留實體檔案。 |

## 組態與啟動設定

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 啟動與組態設定 | `startup_project_get`、`startup_project_set`、`build_configuration_set` | 讀取／明確設定啟動專案，或切換指定組態與平台；不會猜測其他設定。 |
| C++ 設定與啟動 | `cpp_configuration_get`、`cpp_launch_settings_get`、`cpp_launch_settings_set` | 讀取專案／檔案設定的原始與展開值，並讀取或明確修改專案啟動設定。指定組態／平台不會啟用它；省略時使用 VS 目前的專案對應。 |

## 建置與建置偏好

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 建置 | `build_projects`、`get_build_status`、`cancel_build` | 建置、清除或重建指定專案／方案資料夾；回傳依賴順序、各專案結果、錯誤及 Build Output，可等待、查詢及取消。 |
| 建置設定 | `get_build_settings`、`set_build_settings` | 讀取或部分更新持續保存的平行數、輸出／記錄詳細度、執行／建置政策、低優先序及 C++ 計時／記錄。回傳支援狀態原因、執行個體身分與修改前／讀回值。修改需要閒置的 IDE。 |

## 使用方式與限制

`startup_project_set` 可用 `project` 指定單一專案，或用 `projects` 指定有順序的專案清單；明確傳入空清單可清除選取。寫入前會解析所有專案並拒絕重複項目，回傳原本清單與讀回結果，不會啟動或建置專案。

`cpp_launch_settings_set` 只修改明確提供的命令、參數、工作目錄、環境變數、合併旗標與偵錯引擎類型（`auto`、`nativeOnly`、`managedOnly`、`mixed`、`managedCore`、`nativeWithManagedCore`）。專案設定與檔案項目修改需要 IDE 閒置且狀態已儲存，寫入後會儲存目標專案。可保存原本啟動設定後明確還原。`project_item_remove` 排除單一檔案參照，不會刪除實體檔案。修改中斷後，應先檢查設定讀回結果與專案項目，再決定是否重試。

`get_build_settings` 不需開啟方案。`set_build_settings` 只更新指定欄位（至少一項）：`maxParallelProjectBuilds`、`outputVerbosity`、`logFileVerbosity`（C++）、`onlyBuildStartupProjectsAndDependencies`、`onRunWhenOutOfDate`、`onRunWhenBuildErrors`、`lowPriorityBuild`、`stopSolutionBuildAfterFirstFailure`、`cppBuildTiming`、`cppBuildLogging`。詳細度接受 `quiet`、`minimal`、`normal`、`detailed`、`diagnostic`。C++ 執行政策分別接受 `alwaysBuild`／`neverBuild`／`promptToBuild` 和 `launchOld`／`doNotLaunch`／`promptToLaunch`；執行舊版可能與目前原始碼不符。支援範圍依主機／工作負載而定：不可用欄位為 null，`unsupportedSettings` 提供原因；要求不可用欄位時，整個更新會在寫入前被拒絕。原生遇錯停止政策需能找到主機的設定註冊（VS 18.11+），與 `build_projects.stopOnFirstError` 分開。

這些偏好跨方案保存，不會自動還原；可保存 `previous` 快照後明確還原。例如 `set_build_settings(maxParallelProjectBuilds: 2, lowPriorityBuild: true, outputVerbosity: "minimal")` 降低專案同時建置數，並要求後續 IDE 建置使用低優先序。不限制 CPU／編譯器執行緒，也不直接修改既有程序優先權。跨設定提供者的更新不是交易，失敗時附修改前／目前快照。`updateStatus: "pendingApproval"` 表示 VS 原生設定要求尚未提交；C++ 計時／記錄不會在此時寫入，需等核准處理完成後再要求更新。原生要求處理完畢、發生錯誤或呼叫中斷後，應先讀取設定再決定是否重試。工具不會隱含更改目前組態或啟始專案。

`project_items(project)` 接受精確名稱或 unique name，回傳 unique name、IDE 項目階層及可取得的實體路徑，最多 2,000 個項目、64 層。
