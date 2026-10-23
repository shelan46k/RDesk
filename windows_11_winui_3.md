# Windows 11 桌面管理助手 (RDesk) — 專案開發規格書 (PRD & Technical Spec)

> **給 AI 開發助手 (Cursor) 的核心指令**：
> 本文件為 `RDesk` 專案的唯一真實來源（Single Source of Truth）。在撰寫任何程式碼前，請嚴格遵守本文件第 2 節與第 3 節之 **WinUI 3 專屬開發規範與防呆守則**，切勿混用舊版 WPF 或 UWP 的過時語法，確保所有生成的程式碼皆可在 `.NET 8 + Windows App SDK (WinUI 3)` 環境下直接透過 `dotnet build` 編譯通過。

---

## 1. 專案概述與核心定位

開發一款基於 **C# + WinUI 3 (.NET 8 Windows App SDK)** 的現代化 Windows 桌面管理與效率輔助程式（專案名稱：**RDesk**）。
本程式採用 **「方案 C（混合式側邊欄架構）」**，兼具「整合式資訊面板」與「散佈式桌面小工具（類似 DeskBox / Fences）」的優點：

1. **主視窗（Primary Sidebar）**：平時作為固定在指定螢幕側邊（預設靠右）的「一體化半透明毛玻璃面板」，由上而下垂直排列多個功能卡片：
   * 頂部控制列（標題膠囊、深淺主題切換、設置、隱藏）
   * 時鐘與即時天氣卡片
   * 最近打開的文件卡片
   * 常用文件與資料夾卡片（支援外部拖放與真實資料夾映射）
   * 隨手記 / Todo 待辦清單卡片
   * 底部系統狀態與網路監控雙小卡
2. **完全隱藏模式（對齊 DeskBox）**：主側邊欄點擊「隱藏」後整個面板消失，桌面上**不留任何懸浮球或殘留物件**，只保留右下角系統匣圖示；側邊欄也不出現在工作列與 Alt+Tab。
3. **快捷喚起（DeskBox 風格 Reveal）**：收起後可透過 **系統匣圖示（右下角）** 或 **全域快捷鍵（預設 F7，可於設定變更）** 讓側邊欄再次浮現；快捷鍵在面板已顯示時可再次按下收起。
4. **獨立桌面格子（Detached Box Window）**：在「常用文件與資料夾」中，使用者可將特定的收藏群組或映射資料夾「一鍵釘選彈出」為獨立於桌面上的無邊框毛玻璃小視窗，自由擺放於任意螢幕桌面並支援雙擊標題收折為膠囊。
5. **本地優先（Local-First）**：不搬移使用者原始檔案實體路徑，所有設定、待辦事項與捷徑索引皆以本地 JSON 格式儲存，輕量且保護隱私。

---

## 2. 技術棧與環境規範（Cursor 嚴格遵守事項）

### 2.1 開發環境與標準 `.csproj` 範本
* **目標框架**：`net8.0-windows10.0.22621.0`
* **執行階段**：必須設定為 **Unpackaged（非封裝模式）**，免除 MSIX 憑證安裝繁瑣流程，並確保擁有完整的本機檔案系統與登錄檔讀寫權限。

請使用以下標準結構建立 `RDesk.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <RootNamespace>RDesk</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64;ARM64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.240627000" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.22621.3233" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
    <PackageReference Include="WinUIEx" Version="2.3.4" />
    <PackageReference Include="H.NotifyIcon.WinUI" Version="2.1.3" />
    <PackageReference Include="System.Drawing.Common" Version="8.0.7" />
    <PackageReference Include="System.Management" Version="8.0.0" />
  </ItemGroup>
</Project>
```

### 2.2 ⚠️ WinUI 3 專屬開發避坑指南（禁止混入 WPF 語法）
1. **禁止在 `<Window>` XAML 標籤使用的屬性**：
   * 嚴禁在 XAML `<Window>` 寫 `WindowStyle="None"`、`AllowsTransparency="True"`、`ShowInTaskbar="False"`、`WindowStartupLocation="Manual"`、`Width`、`Height`、`Topmost`。這些全部是 WPF 屬性，在 WinUI 3 會導致編譯錯誤。
2. **正確的無邊框與隱藏工作列做法**：
   * 繼承 `WinUIEx.WindowEx` 或使用原生 `Window` 搭配 `Microsoft.UI.Windowing.AppWindow`：
     ```csharp
     var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
     var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
     var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

     if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
     {
         presenter.SetBorderAndTitleBar(false, false);
         presenter.IsResizable = false;
         presenter.IsMaximizable = false;
         presenter.IsMinimizable = false;
     }
     appWindow.IsShownInSwitchers = false; // 隱藏工作列與 Alt+Tab 圖示
     ```
3. **命名空間規範**：
   * 控制項使用 `Microsoft.UI.Xaml.*`，而非 `System.Windows.*`。
   * 色彩使用 `Microsoft.UI.Colors` 與 `Windows.UI.Color`。
   * 計時器使用 `Microsoft.UI.Xaml.DispatcherTimer`。

---

## 3. 系統底層核心機制（Win32 P/Invoke 實作規範）

### 3.1 防 `Win + D` 最小化與釘選桌面底層 (`Helpers/DesktopPinHelper.cs`)
為了讓側邊欄與獨立桌面格子像桌面背景的一部分，按 `Win + D`（顯示桌面）時不會被縮小隱藏，且不佔用下方工作列：
1. **工具視窗屬性 (`WS_EX_TOOLWINDOW`)**：
   * 使用 `GetWindowLongPtr` / `SetWindowLongPtr` 修改 `GWL_EXSTYLE (-20)`，加上 `WS_EX_TOOLWINDOW (0x00000080)` 並移除 `WS_EX_APPWINDOW (0x00040000)`。
2. **防 `Win + D` 攔截機制**：
   * **已採用方法 A（與 DeskBox DesktopPinned 模式相同）**：將視窗的 Owner (`GWLP_HWNDPARENT = -8`) 設為 Explorer **已存在的** `SHELLDLL_DefView`（先找 `Progman`，再找 `WorkerW` 子視窗；絕不主動觸發建立 WorkerW，以免登入時打亂桌面圖示排列）。設定後回讀 Owner 驗證是否成功。
   * 不主動對視窗下 `HWND_BOTTOM`：同一個 Owner 群組會一起被推到底，導致使用者剛點開的側邊欄也被埋到其他程式後面；改為讓視窗在其他程式取得焦點時自然沉到後方。
   * 喚出（系統匣 / 快捷鍵 / 第二次啟動）時以 `SetWindowPos(HWND_TOP)` + `SetForegroundWindow` 暫時提到最前。
   * 提供 `SetPinToDesktop(IntPtr hWnd, bool isPinned)` 供設定頁即時切換；關閉時把 Owner 還原為 0。

### 3.2 多螢幕動態偵測與 DPI 自適應停靠 (`Services/MonitorService.cs`)
不可寫死主螢幕或僅支援雙螢幕，必須支援**任意數量的多螢幕（Multi-Monitor）**環境：
1. **列舉所有螢幕**：
   * 使用 `Microsoft.UI.Windowing.DisplayArea.FindAll()` 取得系統中所有顯示器清單。
   * 針對每個 `DisplayArea` 記錄其 `DisplayId.Value`、`IsPrimary`、`WorkArea`（已自動扣除該螢幕工作列的可用範圍：`X, Y, Width, Height`）以及 `OuterBounds`。
2. **DPI 縮放比例換算**：
   * 呼叫 Win32 `GetDpiForWindow(hWnd)` 取得目標螢幕的 DPI（例如 `96` 為 100%，`144` 為 150%）。
   * 計算縮放係數 `scale = dpi / 96.0`，將設定的邏輯寬度（如 `380px`）乘以 `scale` 轉換為實體像素，再透過 `appWindow.MoveAndResize(new RectInt32(x, y, physicalWidth, physicalHeight))` 精準定位於所選螢幕的右側或左側。
3. **動態監聽**：
   * 訂閱 `DisplayAreaWatcher` 事件，當外接螢幕插拔或解析度變更時，自動重新計算並校正視窗位置。

### 3.3 原生檔案圖示擷取與 `.lnk` 捷徑解析 (`Helpers/ShellHelper.cs`)
1. **解析 `.lnk` 捷徑目標路徑**：
   * 使用 COM 互動或 `Shell32` / `WScript.Shell`（或直接解析二進位/COM `IShellLinkW` 介面）讀取 `%AppData%\Microsoft\Windows\Recent` 內 `.lnk` 檔案指向的真實目標路徑 (`TargetPath`)。
2. **擷取高解析度系統圖示**：
   * 使用 Win32 `SHGetFileInfo`（搭配 `SHGFI_ICON | SHGFI_LARGEICON`）取得檔案或資料夾的 `HICON`。
   * 使用 `System.Drawing.Icon.FromHandle(hIcon).ToBitmap()` 轉換為 PNG `MemoryStream`，再載入至 WinUI 3 的 `Microsoft.UI.Xaml.Media.Imaging.BitmapImage` 供 UI 綁定，最後呼叫 `DestroyIcon(hIcon)` 釋放非託管記憶體。
   * 加入記憶體快取 (`ConcurrentDictionary<string, ImageSource>`) 避免重複擷取相同副檔名的圖示。

### 3.4 開機自動啟動 (`Services/StartupService.cs`)
* 讀寫登錄檔 `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`，鍵值名稱設為 `"RDesk"`，值為當前執行檔路徑 `Environment.ProcessPath`。

### 3.5 全域快捷鍵喚起 (`Services/HotkeyService.cs`)
* 使用 Win32 `RegisterHotKey` / `UnregisterHotKey` 註冊全域快捷鍵（預設 `F7`）。
* 觸發時呼叫 `AppHost.ToggleFromTrayOrHotkey()`：隱藏中則展開側邊欄；已展開則完全隱藏（與系統匣左鍵同一個動作）。
* 註冊失敗（格式錯誤或被其他程式占用）時，設定頁會顯示警告，底部提示文字也不再提到快捷鍵。
* 設定變更後須即時 `RefreshRegistration()`；程式結束時必須 `UnregisterHotKey`。

---

## 4. 視覺設計與雙主題系統（淺色 / 深色毛玻璃一鍵切換）

### 4.1 整體佈局與尺寸規範
* **主側邊欄尺寸**：預設邏輯寬度 `380px`（可在設定中調整 `340px ~ 460px`），高度為目標螢幕 `WorkArea.Height - 24`（上下各留 `12px` 呼吸邊距，靠邊留 `12px` 邊距）。
* **滾動容器**：外層為圓角主面板（`CornerRadius="16"`），內部使用單一垂直 `ScrollViewer`（`VerticalScrollBarVisibility="Auto"`），由上而下使用 `StackPanel`（`Spacing="12"`，內邊距 `Padding="14"`）排列各功能卡片。
* **卡片通用樣式**：圓角 `CornerRadius="12"`，邊框粗細 `BorderThickness="1"`，內邊距 `Padding="14"`。

### 4.2 雙主題色票定義 (`Services/ThemeService.cs`)
使用 `DesktopAcrylicBackdrop` 作為視窗底層模糊材質，並在 `ResourceDictionary` 中定義以下兩套主題色票，支援執行階段無縫切換（`ElementTheme.Light` / `ElementTheme.Dark`）：

| 視覺元素 | 淺色晨霧模式 (Light Mode - 預設，仿參考圖) | 深色夜幕模式 (Dark Mode) |
| :--- | :--- | :--- |
| **主視窗底色 (`PanelBackgroundBrush`)** | `#B8DCE6F2` (半透明霧藍白) | `#C014171F` (半透明深曜黑) |
| **卡片背景 (`CardBackgroundBrush`)** | `#BFEDF2F7` (75% 柔白半透明) | `#99222632` (60% 岩石灰半透明) |
| **卡片邊框 (`CardBorderBrush`)** | `#66FFFFFF` (高光白邊框) | `#26FFFFFF` (微光淡邊框) |
| **子項目/膠囊底色 (`PillBackgroundBrush`)** | `#D9FFFFFF` (85% 純白膠囊) | `#B32D3342` (70% 深灰膠囊) |
| **主要文字 (`PrimaryTextBrush`)** | `#1A202C` (深炭黑) | `#F7FAFC` (冰雪白) |
| **次要文字 (`SecondaryTextBrush`)** | `#4A5568` (石板灰) | `#A0AEC0` (銀灰) |
| **強調色 (`AccentTextBrush`)** | `#2B6CB0` (科技藍) | `#63B3ED` (天空藍) |
| **警告/高負載色 (`WarningTextBrush`)** | `#DD6B20` (暖橘紅，CPU/RAM > 80%) | `#F6AD55` (亮橘黃，CPU/RAM > 80%) |
| **狀態正常色 (`SuccessTextBrush`)** | `#2F855A` (森林綠，`● 已連接`) | `#68D391` (翠綠，`● 已連接`) |

---

## 5. 功能模組詳細規格

### 模組 0：頂部控制列、隱藏與快捷喚起 (`MainSidebarWindow` & `TrayService` & `HotkeyService`)
1. **頂部控制列 (Header Bar)**：
   * 左側：圓角膠囊標籤顯示「RDesk」（字體大小 `13px`，粗體）。
   * 右側：排列三個膠囊形按鈕：
     * `主題`（圖示 ☀️/🌙，點擊直接切換 Light / Dark 主題並儲存，所有視窗與卡片即時套用）。
     * `設置`（點擊開啟設定視窗 `SettingsWindow`）。
     * `隱藏`（點擊將主面板完全隱藏，桌面不留任何物件）。
2. **隱藏後的喚起方式（對齊 DeskBox 整套邏輯，不使用懸浮球）**：
   * 系統匣左鍵：切換展開 / 隱藏。
   * 系統匣右鍵：開啟選單（見 6.2）。
   * 全域快捷鍵（預設 F7）：與系統匣左鍵相同的切換動作；設定中可開關並自訂組合（如 `Ctrl+Alt+R`）。
   * 再次執行 `RDesk.exe`：不會開第二份，而是透過具名事件 `RDesk_Activate_Event` 通知既有程序展開側邊欄。
   * 每次展開時自動重新整理「最近打開的文件」。

---

### 模組 1：時鐘與天氣卡片 (`WeatherClockControl`)
1. **數位時鐘區（卡片上半部，置中對齊）**：
   * **大字體時間**：格式 `HH:mm:ss`（`FontSize="38"`, `FontWeight="Bold"`），使用每秒觸發的 `DispatcherTimer` 即時更新。
   * **日期與星期**：格式 `yyyy年MM月dd日 星期X`（例如：`2026年10月23日 星期五`，`FontSize="13"`, 次要文字色）。
2. **即時天氣區（卡片下半部）**：
   * **API 串接（免 API Key）**：
     * 城市搜尋 Geocoding API：`https://geocoding-api.open-meteo.com/v1/search?name={cityName}&count=5&language=zh&format=json`
     * 天氣預報 Forecast API：`https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=3`
     * 預設地區：台灣台中市南區（`Latitude = 24.12, Longitude = 120.67`，顯示名稱：`台中市 · 南區`）。
   * **UI 佈局**：
     * **第一列（地區與狀態）**：居中顯示「`{地區名稱} · {WMO天氣描述}`」（如 `台中市 · 南區 · 晴朗`），右側附帶一個小型「`刷新`」膠囊按鈕。
     * **第二列（主溫度）**：左側顯示對應天氣 Emoji/SVG 圖示（☀️/⛅/🌧️），右側顯示當前溫度 `18.7°C`（`FontSize="28"`, `FontWeight="Bold"`）。
     * **第三列（三顆指標膠囊）**：水平排列三個圓角小膠囊：
       * `體感 20.0°C`
       * `濕度 88%`
       * `風速 6.8km/h`
     * **第四列（今明後三天預報）**：單行水平均分顯示三天的高低溫範圍：
       * `今天 ⛅ 18.3~29.6°C`  |  `明天 ☀️ 22.2~29.6°C`  |  `後天 ☁️ 22.6~28.7°C`

---

### 模組 2：最近打開的文件卡片 (`RecentFilesControl`)
1. **標題列**：左側「最近打開的文件」（粗體），右側「刷新」膠囊按鈕。
2. **資料讀取與篩選邏輯 (`RecentFilesService`)**：
   * 掃描 `%AppData%\Microsoft\Windows\Recent` 資料夾，按 `.lnk` 的 `LastWriteTime` 由新到舊排序。
   * 解析真實目標路徑，排除不存在的檔案、排除純資料夾，並依設定的常用辦公/開發副檔名白名單篩選（預設包含：`.xlsx`, `.xls`, `.docx`, `.doc`, `.pptx`, `.ppt`, `.pdf`, `.txt`, `.md`, `.csv`, `.jpg`, `.png`, `.zip`, `.rar`, `.sln`, `.cs`, `.py`, `.json`），最多取前 12 筆（加上已置頂項目）。
3. **雙欄網格佈局 (`Grid` / `ItemsRepeater` 2 Columns)**：
   * 列表最大高度設為 `230px`（超出時內部可微幅滾動），每列顯示 2 個檔案小卡。
   * 每個檔案小卡包含：
     * 左側：原生檔案圖示（`28x28`），若為「已收藏置頂」項目則在角落顯示📌小標記。
     * 右上：檔案名稱（單行，`TextTrimming="CharacterEllipsis"`）。
     * 右下：副檔名/關聯類型 + 最後修改或開啟時間（例如 `DOCX · 10-23 09:15`，`FontSize="11"`）。
   * 卡片底部顯示小字操作提示：「`雙擊打開，右鍵可收藏置頂 / 取消收藏 / 定位文件`」。
4. **互動行為**：
   * **左鍵雙擊 (`DoubleTapped`)**：呼叫 `Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true })` 開啟該文件。
   * **右鍵選單 (`ContextFlyout`)**：
     * `📌 收藏置頂 / 取消收藏`：將該路徑加入或移出 `PinnedRecentFiles`，置頂項目永遠排在網格最前面。
     * `⭐ 添加至常用文件`：一鍵將該檔案複製到下方的「常用文件與資料夾」卡片中。
     * `📂 定位文件`：執行 `Process.Start("explorer.exe", $"/select,\"{filePath}\"")` 在檔案總管中打開並高亮選取該檔案。

---

### 模組 3：常用文件與資料夾 — 支援彈出獨立桌面格子 (`FavoritesControl` & `DetachedBoxWindow`)
1. **標題列**：
   * 左側：「常用文件與文件夾」標題 + 分組下拉/頁籤切換（預設提供「預設收藏」，可新增多個自訂群組或「資料夾映射」）。
   * 右側：「`📌 彈出桌面格`」按鈕（將當前分組彈出為獨立桌面小視窗）與「`添加`」膠囊按鈕（點擊可選擇新增檔案、新增資料夾或新增真實資料夾映射）。
2. **拖放收藏與拖拽排序 (`Drag & Drop`)**：
   * 卡片容器設定 `AllowDrop="True"`。
   * 當清單為空時，中央顯示虛線圓角引導膠囊：「`可將文件/文件夾拖放到此處收藏`」。
   * 處理 `DragOver`（設定 `e.AcceptedOperation = DataPackageOperation.Link`）與 `Drop` 事件：從 `e.DataView.GetStorageItemsAsync()` 取得拖入的一或多個檔案/資料夾路徑，擷取圖示後加入當前群組並儲存設定。
   * 使用 `GridView`（2 欄或圖示網格）並啟用 `CanDragItems="True"`、`CanReorderItems="True"`，允許使用者直接用滑鼠拖拽調整常用項目的順序。
   * 底部顯示小字提示：「`將文件直接拖入此卡片，拖拽可自由排序`」。
3. **互動行為**：
   * 左鍵雙擊開啟檔案或資料夾；右鍵選單支援「重新命名顯示名稱」、「定位位置」、「從常用中移除」。
4. **方案 C 專屬功能：獨立桌面格子視窗 (`DetachedBoxWindow.xaml`)**：
   * 當使用者在某個常用分組（或映射的實體資料夾）點擊「`📌 彈出桌面格`」時，建立一個獨立的 `DetachedBoxWindow` 實例。
   * **視窗特性**：
     * 同樣採用無邊框 Acrylic 毛玻璃材質、支援深淺色主題、且套用 `DesktopPinHelper`（防 `Win + D` 最小化、不顯示於工作列）。
     * 頂部標題列顯示群組名稱、項目數量、「收折膠囊按鈕」與「收回至側邊欄（關閉）按鈕」。
     * 支援按住標題列自由拖拉至**任意螢幕**的任何桌面座標，並支援拖拉右下角調整視窗大小。
     * **雙擊標題列收折（Capsule Mode）**：雙擊標題列時，視窗高度平滑收折為僅剩 `38px` 高的精緻桌面膠囊標籤；再次雙擊展開恢復原高度。
     * 所有獨立格子的螢幕座標 `(X, Y, Width, Height)`、展開狀態與內容皆即時同步儲存至 `AppConfig.json`，下次開機自動還原於桌面上。

---

### 模組 4：隨手記 / Todo 待辦清單卡片 (`TodoNotesControl`)
1. **標題列**：
   * 左側為切換式膠囊標籤：`[ 待辦事項 ]` | `[ 隨手記 ]`。
   * 右側：在待辦模式下顯示「已完成 X/Y」統計與「清除已完成」小按鈕；在隨手記模式下顯示「複製全部」小按鈕。
2. **待辦事項視圖 (Todo View)**：
   * 頂部提供一個圓角快速輸入框（`PlaceholderText="輸入待辦事項後按 Enter 新增..."`）。
   * 下方清單顯示待辦項目（含 `CheckBox`、事項文字、刪除按鈕 `✕`）。勾選完成後文字自動套用刪除線並降低透明度，排至未完成項目下方。
3. **隨手記視圖 (Quick Note View)**：
   * 提供一個高度約 `110px` 的無邊框半透明 `TextBox`（`AcceptsReturn="True"`, `TextWrapping="Wrap"`），支援隨時貼上臨時文字、會議記錄或指令片段，文字變更後透過 500ms Debounce 自動寫入本地設定檔。

---

### 模組 5：底部系統與網路狀態雙小卡 (`SystemNetworkControl`)
位於側邊欄最底部，採用 `Grid`（`ColumnDefinitions="*, 10, *"`）左右等寬並排兩張精緻小卡，每 2 秒透過背景計時器更新數據：

1. **左卡：系統狀態 (`System Status Card`)**：
   * 第一列標題：`系統狀態`（`FontSize="12"`, 粗體）。
   * 第二列核心數據：`CPU {cpu}% · 記憶體 {ram}%`（`FontSize="13"`, `FontWeight="SemiBold"`）。
     * **計算方式**：使用 Win32 `GetSystemTimes` 差值計算全機總 CPU 使用率（免管理員權限、不卡頓），使用 `GlobalMemoryStatusEx` 計算實體記憶體使用率。當數值 $\ge 80\%$ 時文字顯示為橘紅色警示色。
   * 第三列次要資訊：`系統碟 {disk}% · 運行 {days}天{hours}小時`（`FontSize="11"`, 次要文字色，透過 `Environment.TickCount64` 換算開機時間）。
2. **右卡：網路狀態 (`Network Status Card`)**：
   * 第一列標題：左側 `網路狀態`，右側動態顯示 `● 已連接`（綠字）或 `● 未連接`（紅字）。
   * 第二列即時網速：`↓ {downSpeed} · ↑ {upSpeed}`（`FontSize="13"`, `FontWeight="Bold"`）。
     * **計算方式**：透過 `NetworkInterface.GetAllNetworkInterfaces()` 篩選狀態為 `OperationalStatus.Up` 且非 Loopback/Tunnel 的實體網卡，每秒讀取 `GetIPStatistics().BytesReceived` 與 `BytesSent` 的差值。
     * **單位自動格式化**：$< 1024$ 顯示 `B/s`；$< 1048576$ 顯示 `KB/s`（保留 1 位小數）；$\ge 1048576$ 顯示 `MB/s`（保留 1 位小數）。
   * 第三列網路資訊：顯示連線類型與本機區網 IPv4，例如 `Wi-Fi · 192.168.1.105` 或 `Ethernet · 192.168.50.12`（`FontSize="11"`, 次要文字色）。

---

## 6. 設定對話框與系統匣整合 (`SettingsDialog` & System Tray)

### 6.1 設定介面 (`Views/SettingsDialog.xaml` 或獨立設定視窗)
點擊頂部控制列的「設置」按鈕時彈出，提供以下設定項並即時生效：
1. **顯示器選擇（多螢幕支援）**：下拉選單列出所有偵測到的螢幕（如 `螢幕 1 (主螢幕) - 2560x1440`、`螢幕 2 - 1920x1080`...）及「重新偵測螢幕」按鈕。
2. **停靠位置與寬度**：選擇停靠於所選螢幕的「右側（預設）」或「左側」，以及面板寬度滑桿（`340px ~ 460px`）。
3. **外觀與透明度**：主題模式選擇（淺色晨霧 / 深色夜幕 / 跟隨系統）、背景不透明度滑桿（`40% ~ 100%`）。
4. **天氣地區設定**：輸入城市/行政區名稱（如「台北市」、「台中市」、「高雄市」），點擊搜尋後從 Open-Meteo Geocoding 結果選擇並綁定經緯度。
5. **快捷喚起**：`啟用全域快捷鍵` ToggleSwitch（預設開）、快捷鍵字串（預設 `F7`）。
6. **系統行為開關**：
   * `釘選於桌面底層（防 Win + D 最小化）`：`ToggleSwitch`（預設開啟）。
   * `開機自動啟動`：`ToggleSwitch`（預設關閉，切換時呼叫 `StartupService`）。

### 6.2 系統匣常駐圖示 (System Tray Icon)
使用 `H.NotifyIcon.WinUI` 在右下角系統匣建立常駐圖示：
* **左鍵點擊系統匣圖示**：切換展開 / 隱藏主側邊欄面板（與快捷鍵相同）。
* **右鍵選單**：
  * `展開 / 隱藏主面板`
  * `顯示所有獨立桌面格子`
  * `切換深淺色主題`
  * `偏好設定...`
  * `退出 RDesk`（完全關閉所有視窗並釋放程序）。

---

## 7. 資料持久化模型 (`Models/AppConfig.cs`)

設定檔固定儲存於 `%LocalAppData%\RDesk\config.json`，讀寫皆採用 `System.Text.Json`（啟用 `WriteIndented = true`）：

```csharp
namespace RDesk.Models;

public class AppConfig
{
    public string TargetMonitorDeviceId { get; set; } = string.Empty;
    public int TargetMonitorIndex { get; set; } = 0;
    public string DockSide { get; set; } = "Right";
    public int SidebarWidth { get; set; } = 380;
    public string ThemeMode { get; set; } = "Light";
    public double PanelOpacity { get; set; } = 0.85;
    public bool PinToDesktop { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;

    public bool EnableRevealHotkey { get; set; } = true;
    public string RevealHotkey { get; set; } = "F7";

    public string LocationName { get; set; } = "台中市 · 南區";
    public double Latitude { get; set; } = 24.1235;
    public double Longitude { get; set; } = 120.6750;

    public List<string> PinnedRecentPaths { get; set; } = new();
    public List<FavoriteGroupModel> FavoriteGroups { get; set; } = new();
    public List<TodoItemModel> TodoItems { get; set; } = new();
    public string QuickNoteContent { get; set; } = string.Empty;
}

public class FavoriteGroupModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "預設收藏";
    public bool IsFolderMapping { get; set; } = false;
    public string? MappedFolderPath { get; set; }
    public List<FavoriteItemModel> Items { get; set; } = new();

    public bool IsDetachedToDesktop { get; set; } = false;
    public bool IsCollapsedToCapsule { get; set; } = false;
    public int DetachedX { get; set; } = 200;
    public int DetachedY { get; set; } = 200;
    public int DetachedWidth { get; set; } = 320;
    public int DetachedHeight { get; set; } = 260;
}

public class FavoriteItemModel
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; } = false;
}

public class TodoItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public bool IsCompleted { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
```

---

## 8. 專案標準目錄結構

```text
RDesk/
├── RDesk.sln                          # Visual Studio 可直接開啟
├── windows_11_winui_3.md              # 本規格書（Single Source of Truth）
└── src/RDesk/
    ├── RDesk.csproj                   # .NET 8 + WinUI 3 (Unpackaged)
    ├── app.manifest
    ├── App.xaml / App.xaml.cs
    ├── Models/
    ├── Helpers/
    │   ├── DesktopPinHelper.cs        # 防 Win+D（Owner = SHELLDLL_DefView）
    │   └── ShellHelper.cs             # .lnk 解析、系統圖示
    ├── Services/
    │   ├── AppHost.cs                 # 視窗協調、喚起 / 收起
    │   ├── ConfigService.cs
    │   ├── MonitorService.cs
    │   ├── ThemeService.cs
    │   ├── HotkeyService.cs           # 全域快捷鍵喚起
    │   ├── TrayService.cs             # 系統匣
    │   ├── StartupService.cs
    │   ├── WeatherService.cs          # Open-Meteo 天氣 / 城市搜尋
    │   ├── RecentFilesService.cs
    │   └── HardwareMonitorService.cs
    └── Views/
        ├── MainSidebarWindow.xaml
        ├── DetachedBoxWindow.xaml     # 獨立桌面格
        ├── SettingsWindow.xaml
        └── Controls/                  # 天氣時鐘、最近文件、常用、Todo、系統網路
```

---

## 9. 實作進度

| 階段 | 內容 | 狀態 |
| :--- | :--- | :--- |
| 1 | 專案骨架、雙主題（含跟隨系統、即時切換）、多螢幕停靠、防 Win+D、系統匣、F7 快捷喚起、單一實例喚起、中文設定窗 | ✅ 完成 |
| 2 | 時鐘天氣卡（每 30 分鐘自動刷新）、系統/網路監控雙小卡 | ✅ 完成 |
| 3 | 最近文件（展開時自動刷新、空狀態提示）、Todo（完成刪除線並排到最後）/ 隨手記 | ✅ 完成 |
| 4 | 常用收藏（拖曳排序、重新命名、群組管理、資料夾映射）、獨立桌面格（可縮放、收折膠囊、拖入檔案、與主面板同步）、完整系統匣選單 | ✅ 完成 |

### 建置指令

```powershell
dotnet build .\src\RDesk\RDesk.csproj -c Debug -p:Platform=x64
dotnet run --project .\src\RDesk\RDesk.csproj -c Debug -p:Platform=x64
```
