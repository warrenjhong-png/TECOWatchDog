# Changelog

本專案版本紀錄遵循 [Semantic Versioning](https://semver.org/lang/zh-TW/)。

## [Unreleased]

## [1.1.0] - 2026-10-08

### 新增

- Parser 監控分頁：檢查指定 `.venv` Python 程序、顯示 PID／啟動時間、Parser Log 更新時間，並讀取 `logs/bridge.log` 最後 200 行。
- Parser 監控檢查間隔可設定，程序狀態變更同步寫入 WatchDog 每日事件紀錄。
- Parser 可在程序連續遺失或 Log 出現嚴重執行錯誤時，自動執行既有 `start_parser.bat` 重啟；支援手動重啟、冷卻時間及每日次數上限。
- Parser Log 分類：網路／資料庫重連及資料／SQL 錯誤僅記錄，不做無效重啟；`CRITICAL`、Traceback、Fatal Python error 或未處理例外才觸發重啟判斷。
- Parser Log 遇到檔案輪替、占用或短暫讀取失敗時不終止監控，下一輪自動重試。
- Parser 資料夾路徑會保存至執行檔旁的 `parser-settings.json`，下次啟動自動載入。
- Parser 的連續異常次數可在介面設定並保存；冷卻時間 60 秒與每日最多重啟 5 次維持內部安全預設。

## [1.0.0] - 2026-10-08

### 新增

- CDB `VM_RESULT_CONTROL` 監控，透過 `MAX(TIMETAG)` 判斷是否有新資料。
- 可設定查詢間隔、無新資料門檻及單次 SQL 查詢逾時。
- 從 `system.ini` 自動載入 SQL Server、資料庫、Schema、驗證及憑證設定。
- 連線東元 MQTT Broker，預設使用 `ncku/watchdog/alert` 與 `ncku/watchdog/recovery`。
- 無新資料警報與資料恢復通知，支援自訂文字或 JSON，以及動態變數替換。
- 警報與恢復手動推送按鈕，使用 QoS 1 並顯示發布結果。
- MQTT 訂閱回收驗證，Log 顯示實際接收的 Topic 與 Payload。
- 查詢完成後逐秒顯示下一次 CDB 查詢倒數。
- 每日事件紀錄，儲存於執行檔旁的 `Log/YYYY-MM-DD.txt`。
- 離線檢查程式，涵蓋輪詢、警報、恢復、INI、安全驗證及 WinForms 建構。

### 調整

- 將 CDB 與通知設定整合為單頁橫向版面，隱藏不再使用的通用 MQTT 手動發布頁。
- CDB 查詢完整 `await`，避免查詢重疊；每輪完成後等待完整查詢間隔。
- 執行監控時鎖定 CDB 與通知設定，但保留警報及恢復手動推送功能。

### 安全性

- SQL 查詢固定為唯讀目標並安全引用 Schema。
- 密碼不寫入畫面 Log 或每日事件檔。
- `system.ini` 排除於 Git，僅提供無密碼的設定範例。
