# MCP for Unity Launcher

[English](../../README.md) | [日本語](README-ja.md) | [한국어](README-ko.md) | **繁體中文** | [简体中文](README-zh.md)

這是搭配 [MCP for Unity](https://github.com/VRCLearn/unity-mcp) 使用的 Unity 編輯器擴充套件，可透過 VPM 或 UPM 安裝，讓本機 MCP 伺服器隨 Unity 專案自動啟動，並在多個編輯器之間持續運作。

- **自動啟動**：開啟專案後，自動啟動伺服器並連接 Unity 編輯器橋接功能。
- **多編輯器共用**：A 和 B 使用同一個伺服器時，關閉 A 後，B 仍能繼續使用。
- **異常復原**：受管理的伺服器結束或停止回應後會自動重新啟動；監督程序結束後也會重新啟動。

**每個需要這些功能的專案都必須安裝 Launcher。** 只安裝 MCP for Unity 的專案仍沿用原本的啟動與關閉方式。

## 安裝

需要 Unity 2021.3 或更新版本，以及 [uv](https://docs.astral.sh/uv/getting-started/installation/)。Launcher 相依於 MCP for Unity 10.3.x，目標平台為 Windows、macOS 與 Linux 桌面版 Editor。

### VPM

1. 如果尚未安裝 uv，請先依照官方說明安裝。安裝後重新啟動 Unity，讓編輯器能夠找到 `uv` 和 `uvx`。
2. 開啟 [VPM 安裝頁面](https://vrclearn.github.io/MCP-For-Unity-Launcher/)，點選 **Add to VCC/ALCOMD**，或手動新增套件存放庫網址：

   ```text
   https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json
   ```

   這個存放庫同時提供 **MCP for Unity** 和 **MCP for Unity Launcher**。
3. 在每個專案的套件管理頁面安裝 **MCP for Unity Launcher**（`com.vrclearn.mcp-for-unity-launcher`）。VPM 也會從同一個存放庫安裝相依套件 MCP for Unity。
4. 開啟專案，在 **Window → MCP for Unity** 中使用 **HTTP Local**，並完成一次 AI 用戶端的 HTTP 連線設定。預設端點為 `http://127.0.0.1:8080/mcp`。

此後，Launcher 會自動啟動本機伺服器並連接編輯器。首次執行可能需要等待 uv 下載 Python 和伺服器的相依套件；AI 用戶端仍需另外設定 MCP 連線。

### UPM（任何 Unity 專案）

透過 UPM 安裝不需要 VRChat、VCC 或 ALCOMD。

1. 安裝 uv 並重新啟動 Unity，讓編輯器能夠找到 `uv` 和 `uvx`。
2. 依照 [MCP for Unity 安裝說明](https://github.com/VRCLearn/unity-mcp)，在同一個專案中安裝 **MCP for Unity 10.3.x**。UPM 不會解析本套件的 `vpmDependencies`，因此請先單獨安裝相依套件，再安裝 Launcher。
3. 開啟 **Window → Package Manager**，選擇 **+ → Add package from git URL**，輸入：

   ```text
   https://github.com/VRCLearn/MCP-For-Unity-Launcher.git?path=/Packages/com.vrclearn.mcp-for-unity-launcher#main
   ```

4. 在 **Window → MCP for Unity** 中使用 **HTTP Local**，並設定 AI 用戶端的 HTTP 連線。預設端點為 `http://127.0.0.1:8080/mcp`。

每個參與的專案都需要安裝 Launcher。此 Git 網址指向 `main` 的套件子目錄；同一個專案請只使用一種安裝方式。

如果 MCP for Unity 尚未安裝或版本不相容，Launcher 會開啟提供 VPM 和 UPM 安裝說明的視窗，等待相容的相依套件，而不會因缺少相依套件造成編譯錯誤。可從 **Window → MCP for Unity Launcher** 重新開啟說明；安裝並重新編譯完成後，Launcher 會自動啟用。

## 同時使用多個 Unity 編輯器

在專案 A 和 B 中都安裝這兩個套件，並使用相同的本機伺服器位址。開啟兩個專案後，即使關閉 A，共用伺服器仍可供 B 使用。最後一個受管理的編輯器關閉後，Launcher 會等待 10 秒，再結束由它啟動的伺服器。

開啟 **Window → MCP for Unity Launcher**，可以查看服務狀態、參與的編輯器和日誌。視窗中的自動管理開關只影響目前的專案。

兩個 Launcher 視窗都可透過 **Language / 語言** 選擇英語、日語、韓語、繁體中文或簡體中文。初次使用會依照系統語言，不支援的語言則使用英語。語言選擇會獨立於 MCP for Unity 儲存，並套用至其他專案。診斷記錄與執行時的錯誤訊息維持英文。

## 使用範圍與驗證情況

Launcher 管理本機 HTTP 服務。遠端 HTTP 設定沿用原本的管理方式；現有的外部伺服器可以繼續使用，但 Launcher 不會接管其程序。

0.3.0 新增 macOS 原生程序身分檢查，以及監督程序結束時的 Linux/macOS 子程序清理。CI 在三個系統上檢查程序管理、C#/Python 身分資訊相容性與發佈封裝。Unity Editor 的驗證環境是 Windows 與 Unity 2022.3.22f1；多個 Editor 的互動操作及 macOS/Linux 上的 Unity 行為尚未驗證。詳情請見[驗證記錄](../verification.md)。

## 下載與文件

- [版本下載](https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases)：包含 VPM ZIP、UnityPackage 和套件資訊清單。建議使用 VPM；匯入 UnityPackage 時，需要另外安裝 MCP for Unity。同一個專案請只使用一種安裝格式。
- [開發與發布](../development.md)
- [架構](../architecture.md)
- [回報問題](https://github.com/VRCLearn/MCP-For-Unity-Launcher/issues)

## 授權條款

[MIT](../../LICENSE)。MCP for Unity 是獨立的相依套件，由其貢獻者維護。
