# 測試

[English](testing.md) · [工具索引](README.zh-TW.md) · [專案首頁](../../README.zh-TW.md)

探索與執行已建置測試、收集結果，並取消自有測試工作。

本頁列出 **4 個工具**。

| 分類 | MCP 工具 | 功能與回傳內容 |
| --- | --- | --- |
| 測試探索 | `test_discover` | 探索已建置容器內的測試而不執行。 |
| 測試 | `test_run`、`test_status`、`test_cancel` | 執行指定的已建置測試 DLL／EXE，回傳 run ID、執行狀態、TRX 統計、個別結果、失敗訊息與堆疊；支援篩選、等待及取消。 |

## 使用方式與限制

`test_discover(sources)` 使用目前 VS 的 VSTest，列出已建置容器內的完整測試名稱而不執行測試。可指定 settings／adapter 路徑與架構，並限制結果數量及逾時；需要已安裝的 adapter，探索過程可能載入 adapter／容器程式碼。

測試工具執行已建置的二進位檔，不會自動 build／restore，也不會同步 Test Explorer 畫面。僅支援 Microsoft.Testing.Platform 的執行檔目前不適用此 VSTest runner。

測試取消只終止該工作的 runner 與其擁有的 test-host process job，不影響其他 Test Explorer 工作。記憶體保留最近十次測試，TRX 寫入隔離的暫存目錄。自有測試執行期間會拒絕建置、啟動偵錯與方案／組態切換。
