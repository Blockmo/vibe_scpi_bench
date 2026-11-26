# ATE Bench — ATE 程式 SCPI 指令分析工具

在 ATE 程式執行期間，記錄它對所有儀器下達的 SCPI 指令，並產生統計報告：
每台儀器用了哪些指令、各幾次、參數與回應、耗時、錯誤 / 逾時。

不需要修改 ATE 程式，也不需要指定 GPIB 位址 — 本機所有程序、所有介面
(GPIB / USB / LAN / Serial) 經由 NI-VISA 或 NI-488.2 的讀寫都會被記錄。

程式為 C# WPF，目標 .NET Framework 4.8 (Windows 10 / 11 內建，測試站不需另外安裝執行環境)。
只要複製 `dist\AteBench.exe` 一個檔案即可在其他電腦使用。

## 使用方式

1. 執行 `run_bench.bat` (或直接執行 `dist\AteBench.exe`)。
2. 按左上角的圓形按鈕開始擷取，等狀態顯示「擷取中」。
3. 啟動 ATE 程式並執行測試。儀表板會即時顯示：指令 / 讀取次數統計、最近 60 秒的匯流排活動波形、
   每台儀器的卡片 (有通訊時燈號亮起、最常用的三個指令)、以及即時指令流。
4. 測試結束後再按一次圓形按鈕，報告會自動在瀏覽器開啟；下方橫幅可重新開啟報告或輸出資料夾。

右上角的 **Dark / Light** 可切換深色 / 淺色主題，擷取中也能即時切換；選擇會記住，第一次啟動時依 Windows 的應用程式色彩設定。

「分析既有紀錄檔…」可載入先前的 `trace.xml`，儀表板會顯示該次擷取的結果並重新產生報告。

每次擷取的輸出在執行檔旁的 `captures\<日期_時間>\`
(該位置不可寫入時改用「文件\ATE Bench\captures」)：

| 檔案 | 內容 |
|---|---|
| `report.html` | 互動式報告 (可排序、搜尋、依儀器篩選；右上角可切換 Dark / Light，預設跟隨 ATE Bench 的主題) |
| `report.xlsx` | 同樣內容的 Excel (儀器 / 指令 / 時間軸三個工作表) |
| `commands.csv`、`instruments.csv`、`timeline.csv` | 原始統計資料 |
| `trace.xml` | NI I/O Trace 原始紀錄，可重新分析 |
| `session.json` | 擷取時間、程序名稱 |

### 命令列

```
AteBench.exe capture [-o 資料夾]             主控台擷取；按 Enter 停止
AteBench.exe capture --run "ATE.exe 參數"    啟動 ATE 程式，程式結束時自動停止並產生報告
AteBench.exe analyze trace.xml [-o 資料夾]   重新分析既有紀錄檔
AteBench.exe selftest                        SCPI 拆解自我測試
```

選項：`--no-normalize` 不歸併 SCPI 長短格式；`--no-open` 不自動開啟報告。

## 建置

需要 .NET SDK (已用 9.0 驗證) 與 .NET Framework 4.8 targeting pack：

```
build.bat          等同 dotnet build AteBench\AteBench.csproj -c Release -o dist
```

| 原始檔 | 內容 |
|---|---|
| `AteBench\IoTrace.cs` | 透過 `NiSpyLog.dll` 控制 NI I/O Trace 開始 / 停止擷取 |
| `AteBench\Trace.cs` | 增量解析 I/O Trace 的 XML 紀錄檔 |
| `AteBench\Scpi.cs` | SCPI 訊息拆解與長短格式歸併 |
| `AteBench\Analyzer.cs` | 辨識儀器、統計指令、查詢與回應配對 |
| `AteBench\Report.cs`、`Xlsx.cs`、`report_template.html` | 輸出 HTML / CSV / XLSX 報告 |
| `AteBench\Session.cs` | 擷取工作階段 (GUI 與命令列共用) |
| `AteBench\MainWindow.xaml`、`MainWindow.xaml.cs` | 主視窗 (儀表板) 與操作流程 |
| `AteBench\Theme.xaml` | 按鈕、輸入框、開關、捲軸等元件樣式 |
| `AteBench\Palette.Dark.xaml`、`Palette.Light.xaml` | 深色 / 淺色調色盤 (兩者的鍵一一對應) |
| `AteBench\ThemeManager.cs` | 切換主題、記住選擇 |
| `AteBench\ScopeChart.cs` | 匯流排活動波形圖 (自繪控制項) |
| `AteBench\ViewModels.cs` | 儀器卡片與指令流的顯示資料 |
| `AteBench\Program.cs` | 進入點與命令列模式 |
| `AteBench\app.ico` | 應用程式圖示 (16–256 px)，由 `tools\make_icon.ps1` 產生；報告的分頁圖示是同一圖案的內嵌 SVG |

## 運作原理

擷取由 **NI I/O Trace** (隨 NI-VISA / NI-488.2 安裝) 負責，本工具透過 `NiSpyLog.dll`
控制它開始 / 停止，並把紀錄寫成 XML；之後解析 XML 中的 `viWrite` / `viRead` /
`ibwrt` / `ibrd` / `Send` / `Receive` 等呼叫的資料緩衝區，拆解成個別 SCPI 指令。

- 一次 write 內以 `;` 串接的指令會分開計算，相對路徑會展開
  (`SOUR:VOLT 1;CURR 2` → `SOUR:VOLT`、`SOUR:CURR`)。
- 長短格式歸併成短格式 (`VOLTage` = `VOLT`)；報告會同時列出實際送出的寫法。
- 查詢指令會與下一次讀取配對，顯示回應範例；`*IDN?` 的回應會顯示在儀器總覽。
- `viPrintf` / `viQueryf` 等格式化 I/O 以實際送上匯流排的資料計算，不會重複計數。

## 限制與注意事項

- **需要 NI I/O Trace**。ATE 程式必須透過 NI-VISA 或 NI-488.2 與儀器通訊；
  若使用 Keysight VISA、直接開 socket / COM port，則擷取不到。
- **擷取會拖慢 ATE**：在本機實測每個 VISA 呼叫約增加 2 ms (假 ATE 約一萬次呼叫：未擷取 1.4 秒、
  擷取中 20.4 秒)。指令「次數」不受影響，但測試總時間會變長，請勿用擷取時的測試時間當作效能基準。
- 報告中的耗時取自 I/O Trace 的時間戳記，解析度有限，很快的呼叫會顯示為 0 ms。
- 由本工具啟動 I/O Trace 時，會把它的緩衝區設定改為「Large buffer」(登錄檔
  `HKCU\Software\National Instruments\NI Spy\Options\Buffer Is Brief = 0`)，
  否則超過 64 bytes 的指令會被截斷。
- 短格式歸併依 SCPI 規則推算，非 SCPI 語法的舊式儀器指令會原樣列出。
- NI-488.2 介面卡層級 I/O (`ibcmd` 定址後 `ibwrt`) 的位址解析尚未以實機驗證。

## 已移除的功能：測項標記 (暫時不用)

**狀態**：2026-11-24 從介面與命令列移除，目前不需要，日後可能依需求重新設計。

**原本的用途**：把一次擷取依測項切成幾段，報告分別統計每個測項用了哪些 SCPI 指令。

**原本的做法**：

- 圖形介面左側有「測項標記」輸入框與「標記測項開始」按鈕，命令列擷取時則是輸入測項名稱後按 Enter。
- 依**時間**切分：按下標記的那一刻起送出的指令都算在該測項，直到下一個標記為止；
  第一個標記之前的指令歸在「(第一個標記之前)」。
- 標記的時間與名稱存在 `session.json` 的 `markers` 欄位。
- 報告會多出「測項 (標記) 統計」區塊 (每個測項的歷時、儀器數、指令數、讀寫、錯誤與指令明細)，
  時間軸也可以依測項篩選。

**移除原因 (限制)**：

- 必須手動、即時地按下標記；ATE 程式不會通知目前跑到哪個測項。
- 執行很快的測項來不及標記，它的指令會被算進前一段。
- 標記只能往後加，標錯無法刪除或修改。

**仍保留的部分**：分析與報告端的程式 (`Analyzer.Markers`、`Report.cs` 的測項區段、
`report_template.html` 的測項統計表) 沒有刪除。因此分析舊擷取時，若 `session.json` 內有標記，
報告仍會顯示測項統計；要恢復此功能，只需重新加上設定標記的介面。

**日後可考慮的改進**：依指令自動切段 (例如每個測項開頭都會送的 `*RST`)，
或讀取 ATE 本身的測試日誌來對時間，以取代手動標記。

## 測試

沒有儀器時可用假儀器做端對端測試 (需要 Python 與 `pyvisa`)：

```
python tests\fake_instruments.py                              啟動兩台假儀器 (TCP 5025 / 5026)
dist\AteBench.exe capture --run "python tests\fake_ate.py 5"  擷取假 ATE 程式
```

`python\` 資料夾是原先的 Python 版本 (分析結果相同，在該資料夾內執行 `python -m ate_bench`)，保留作為對照，
不需要可直接刪除。注意 Python 版擷取時每個呼叫約慢 50 ms (原因未查明)，實際擷取請用 C# 版。
