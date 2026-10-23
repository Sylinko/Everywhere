<a id="readme-top"></a>

<a href="README.md">English Version »</a>
<a href="README-zh-cn.md">简体中文版本 »</a>
<a href="README-ja-jp.md">日本語バージョン »</a>

<div align="center">

  <img src="img/Everywhere.png" alt="Everywhere" width="120" height="120">

  <h1>隨時隨地，智慧相伴 - <code>Everywhere</code></h1>

  <div>
    <a href="https://trendshift.io/repositories/15106" target="_blank"><img src="https://trendshift.io/api/badge/repositories/15106" alt="Sylinko%2FEverywhere | Trendshift" width="250"/></a>
    <a href="https://www.producthunt.com/products/everywhere?embed=true&utm_source=badge-featured&utm_medium=badge&utm_source=badge-everywhere" target="_blank"><img src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1034853&theme=light&t=1762403775174" alt="Product Hunt" width="250" /></a>
    <a href="https://hellogithub.com/repository/Sylinko/Everywhere" target="_blank"><img src="https://abroad.hellogithub.com/v1/widgets/recommend.svg?rid=0bd4328c24794902bd6097055cda6f36&claim_uid=LNYEf6O9Qv5JeR2" alt="Featured｜HelloGitHub" width="250" /></a>
  </div>

  <br/>

  [![.NET 10][.NET 10]][.NET-url][![Avalonia][Avalonia]][Avalonia-url]
  [![加入 Discord](https://dcbadge.limes.pink/api/server/5fyg6nE3yn)](https://discord.gg/5fyg6nE3yn)
  [![加入 QQ 群][QQ-Group]](https://qm.qq.com/cgi-bin/qm/qr?k=wp9aDBBnLc7pYATqT99tB-N2ZP2ETmJC&jump_from=webapi&authKey=97qUJfsQoI70dUNcgBZ0C3HCZeiEn8inLT7pzg8x+KinbQwfIrHFu3dB2+aHMbRD)

  <p align="center">
    <strong><a href="https://everywhere.sylinko.com">📖 查閱官方文件</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://youtu.be/BGujYa5hbXo">🎬 觀看宣傳片</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://github.com/Sylinko/Everywhere/issues/new?labels=bug&template=bug-report.md">🪲 回報錯誤</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://github.com/Sylinko/Everywhere/issues/new?labels=enhancement&template=feature-request.md">💡 功能請求</a></strong>
  </p>
</div>

<details>
<summary>📖 <b>目錄 (Table of Contents)</b></summary>

- [關於 Everywhere](#-關於-everywhere)
  - [使用情境示例](#-使用情境示例)
  - [技術特點](#️-技術特點)
- [核心技術與架構](#️-核心技術與架構)
- [系統需求](#-系統需求)
- [快速開始](#-快速開始)
  - [取得與安裝](#取得與安裝)
  - [說明與社群](#說明與社群)
- [貢獻方式](#-貢獻方式)
- [贊助我們](#-贊助我們)
- [特別鳴謝](#-特別鳴謝)
- [星標歷史](#-星標歷史)
- [授權條款](#-授權條款)

</details>

<br/>

## 🦄 關於 Everywhere

**Everywhere** 是一款具備情境感知能力的互動式 AI 助理，擁有簡潔現代的使用者介面與強大的整合功能。與傳統 AI 工具不同，Everywhere 能即時感知並理解您螢幕上的任何內容。無需截圖、複製或切換應用程式——只需按下快捷鍵，即可在當下位置獲得所需協助，實現無縫的 AI 助理支援。

<img width="100%" alt="策略引擎" src="https://github.com/user-attachments/assets/2b45476b-ff8a-4b0f-8df0-1a16a47cc27b" />

### 🌟 使用情境示例

<br/>

> **🩺 疑難排解專家**
>
> 您在使用電腦時遇到錯誤訊息，但不確定如何解決。在錯誤訊息旁呼出 <kbd>Everywhere</kbd>，直接輸入 *「這是什麼錯誤？如何解決？」*，**Everywhere** 將就地擷取訊息情境，並提供精確的解決方案。

> **📰 快速網頁摘要**
>
> 瀏覽冗長的技術文章，但只需要重點？在網頁上呼出 <kbd>Everywhere</kbd>，詢問 *「給我一段簡短摘要」*，即可立即取得主要論點，無需閱讀整篇內容。

> **🌐 即時翻譯**
>
> 查閱文獻時遇到外語生詞？選取它，或直接對著文字呼出 <kbd>Everywhere</kbd>，告訴它 *「翻譯成中文」*，即可立即看到翻譯，無需開啟第二個翻譯工具。

> **✉️ 郵件草稿協助**
>
> 不確定重要商務郵件的語氣？在草稿上呼出 <kbd>Everywhere</kbd>，輸入 *「讓這封郵件更專業一些」*，您的隨性文字便會轉化為得體、可直接用於商務的溝通內容。

> **❔ 這是真的嗎？**
>
> 遇到真實性不確定的資訊？選取相關文字，呼出 <kbd>Everywhere</kbd>，詢問 *「這是真的嗎？」*，**Everywhere** 將快速檢索並驗證資訊的可靠性。

### 🛠️ 技術特點

<table align="center">
  <thead>
    <tr>
      <th style="width:30%">類別</th>
      <th style="width:45%">✅ 目前支援</th>
      <th style="width:25%">🚧 進行中計畫</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><b>豐富模型生態</b></td>
      <td>
        • <img style="margin-top:3px;margin-bottom:-3px" alt="Everywhere 雲端服務" src="https://everywhere.sylinko.com/favicon.ico" width="20" height="20" > Everywhere 雲端服務<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="OpenAI" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/openai.svg"> OpenAI (ChatGPT)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="Anthropic" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/anthropic.svg"> Anthropic (Claude)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="Google" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/gemini-color.svg"> Google (Gemini)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="DeepSeek" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/deepseek-color.svg"> DeepSeek<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="Moonshot" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/moonshot.svg"> Moonshot (Kimi)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="MiniMax" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/minimax-color.svg"> MiniMax<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="Mistral AI" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/1.95.0/files/icons/mistral-color.svg"> Mistral AI<br>
        • <img style="margin-top:3px;margin-bottom:-3px;background-color:#fff;border-radius:4px;padding:2px;box-sizing:border-box;" alt="Ollama" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/ollama.svg"> 本機部署 (Ollama)<br>
        • 相容於自訂 API 端點<br>
      </td>
    </tr>
    <tr>
      <td><b>強大的 Agent 系統</b></td>
      <td>
        • 網頁瀏覽器<br>
        • 派發子代理<br>
        • 本機檔案系統<br>
        • 終端機指令碼執行<br>
        • MCP 工具<br>
        • Everything 極速搜尋 <i>(Windows)</i><br>
        • 整合系統應用程式 <i>(macOS)</i>
      </td>
      <td>
        • 記憶系統<br>
      </td>
    </tr>
    <tr>
      <td><b>無縫互動體驗</b></td>
      <td>
        • 極致現代的磨砂玻璃 UI<br>
        • 智慧情境感知<br>
        • 全域系統熱鍵<br>
        • 選取文字互動<br>
        • 豐富的 Markdown 與數學公式轉譯
      </td>
      <td>
        • 滑鼠快捷鍵<br>
        • 語音互動<br>
      </td>
    </tr>
    <tr>
      <td><b>多平台</b></td>
      <td>
        • 🪟 Windows<br>
        • 🍎 macOS
      </td>
      <td>
        • 🐧 Linux
      </td>
    </tr>
    <tr>
      <td><b>國際化支援</b></td>
      <td>
        • 簡體中文/繁體中文<br>
        • English, Deutsch, Español<br>
        • Français, Italiano, 日本語<br>
        • 한국어, Русский, Türkçe<br>
      </td>
      <td>
        部分由 AI 輔助翻譯。<br>歡迎貢獻與校正！<br>
      </td>
    </tr>
  </tbody>
</table>

## ⚙️ 核心技術與架構

- **👁️ 廣譜的情境感知**：除了支援傳統螢幕多模態外，我們更深度融合底層無障礙 API 與 UI 自動化技術。這讓應用程式能在跨度極大的各類軟體中，以低侵入的方式精準擷取正在作用中的結構化環境資料。
- **🧠 情境喚醒與策略引擎**：🚧 初衷在於徹底終結打斷工作節奏的流程。以往使用大型語言模型服務時，常常需要：選取並複製 -> 切換到 AI 視窗 -> 貼上 -> 手動輸入以補全執行意圖。而在由策略引擎驅動的進階功能中，透過快捷鍵呼出的 Everywhere 會即時感知您正在瀏覽的情境或應用程式，無需贅述需求，便直接推送相應情境的快捷執行策略，達成真正的心流層操作體感。
- **🧱 堅守底層品質**：我們非常重視軟體效能、程式碼品質與安全性，從系統底層出發，透過現代化架構設計與精心打磨的系統工程，從根本上拒絕純粹依賴「Vibe Coding」以及盲目的敏捷堆砌，每一行程式碼都精心撰寫並用心打磨。

## 📋 系統需求

| 平台      | 最低系統需求              |
| --------- | ------------------------- |
| 🪟 Windows | Windows 10 (10.0.19041.0) |
| 🍎 macOS   | Monterey 12.0             |
| 🐧 Linux   | 🚧 **開發中**              |

## 🚀 快速開始

### 取得與安裝

> [!TIP]
> 建議直接前往我們的 [官方網站](https://everywhere.sylinko.com/download) 下載並取得您系統對應的版本，或從 [Release 頁面](https://github.com/Sylinko/Everywhere/releases/latest) 下載。

**Windows**
- `Everywhere-Windows-x64-Setup-vx.x.x.exe`: 包含完整精靈的安裝程式套件 *(推薦)*。
- `Everywhere-Windows-x64-vx.x.x.zip`: 免安裝可攜式壓縮檔。

**macOS**
- `Everywhere-macOS-arm64-vx.x.x.pkg`: 適用於 Apple Silicon（M 系列）Mac 裝置。
- `Everywhere-macOS-x64-vx.x.x.pkg`: 適用於 Intel Mac 裝置。

### 說明與社群

> [!NOTE]
> 初次使用？或者想了解 MCP 等深度整合功能？建議先參閱我們的官方指南！

- **官方文件**: [https://everywhere.sylinko.com](https://everywhere.sylinko.com)
- **Discord 社群**: [加入我們的頻道取得支援](https://discord.gg/5fyg6nE3yn)
- **中文使用者群**: [點擊加入我們的 QQ 群](https://qm.qq.com/cgi-bin/qm/qr?k=wp9aDBBnLc7pYATqT99tB-N2ZP2ETmJC&jump_from=webapi&authKey=97qUJfsQoI70dUNcgBZ0C3HCZeiEn8inLT7pzg8x+KinbQwfIrHFu3dB2+aHMbRD)

## 🤝 貢獻方式

我們熱愛開源，歡迎提供您的奇思妙想與程式碼修改（Pull Requests）！請參閱 [CONTRIBUTING.md](.github/CONTRIBUTING.md) 查看具體的程式碼貢獻規範與本機編譯指南。

可以查看我們的 [Build Guide](docs/build.md) 取得關於如何設定開發環境與本機建置專案的詳細說明。

**非常感謝所有在草創期與成長期做出卓越貢獻的貢獻者：**

<a href="https://openomy.com/Sylinko/Everywhere" target="_blank" style="display: block; width: 100%;" align="center">
  <img src="https://openomy.com/svg?repo=Sylinko/Everywhere&chart=bubble&latestMonth=12" target="_blank" alt="貢獻排行榜" style="display: block; width: 100%;" />
</a>

## 💖 贊助我們

<a href="https://share.302.ai/5rzmPr"><img src="https://raw.githubusercontent.com/Sylinko/Everywhere/refs/heads/main/img/Sponsors/302-ai-en.jpg" width="600" alt="302.ai 贊助商"/></a><br/>

[302.AI](https://share.302.ai/5rzmPr) 是一個按用量付費的企業級 AI 資源平台，提供市場上最新、最全面的 AI 模型與 API，以及多種開箱即用的線上 AI 應用。

## 🤩 特別鳴謝

<a href="https://www.certumcodesign.cn"><img src="img/Sponsors/certum-cn.svg" width="300" alt="Certum China" style=";background-color:#fff;"/></a><br/>

本專案的程式碼簽章憑證（Code Signing Certificate）由 [Certum 中國](https://www.certumcodesign.cn/) 慷慨贊助，感謝其持續為開源社群做出重大貢獻。

感謝 [pasical](https://github.com/pasical) 設計 banner 中的 kawaii logo。

## 📈 星標歷史

<br/>

<a href="https://star-history.dera.page/#Sylinko/Everywhere&type=date">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&legend=top-left" />
 </picture>
</a>

<br/>

## 📄 授權條款

本專案依 [LICENSE](LICENSE) 檔案發布。
*第三方相依性與開源元件授權請參閱 [ThirdPartyNotices.txt](ThirdPartyNotices.txt)。*

<p align="right"><a href="#readme-top">⬆️ 返回頂部</a></p>

<!-- MARKDOWN LINKS & IMAGES -->

[.NET 10]: https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[.NET-url]: https://dotnet.microsoft.com/
[Avalonia]: https://img.shields.io/badge/Avalonia-1c2e5f?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB3aWR0aD0iODYiIGhlaWdodD0iODYiIHZpZXdCb3g9IjAgMCA4NiA4NiIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KPGcgY2xpcC1wYXRoPSJ1cmwoI2NsaXAwXzU5OV8xMTA3KSI+CjxwYXRoIGQ9Ik03NC44NTM1IDg1LjgyMzFDNzUuMDI2MyA4NS44MjMxIDc1LjE5NTQgODUuODIzMSA3NS4zNjc5IDg1LjgyMzFDODAuNzM0NyA4NS44MjMxIDg1LjE0MzkgODEuODAyNyA4NS43NjE0IDc2LjYwMTlMODUuODM1NyA0MS43NjA0Qzg1LjIyNTUgMTguNTkzMSA2Ni4yNTM3IDAgNDIuOTM5MyAwQzE5LjIzOTkgMCAwLjAyNzcxIDE5LjIxMjIgMC4wMjc3MSA0Mi45MTE2QzAuMDI3NzEgNjYuMzU3MyAxOC44MzA5IDg1LjQxOCA0Mi4xOCA4NS44MjMxSDc0Ljg1MzVaIiBmaWxsPSIjRjlGOUZCIi8+CjxwYXRoIGZpbGwtcnVsZT0iZXZlbm9kZCIgY2xpcC1ydWxlPSJldmVub2RkIiBkPSJNNDMuMDU4NSAxNC42MTQzQzI5LjU1MTMgMTQuNjE0MyAxOC4yNTU1IDI0LjA4MiAxNS40NDU0IDM2Ljc0MzJDMTguMTM1NyAzNy40OTc1IDIwLjEwODcgMzkuOTY3OSAyMC4xMDg3IDQyLjg5OTJDMjAuMTA4NyA0NS44MzA1IDE4LjEzNTcgNDguMzAxIDE1LjQ0NTQgNDkuMDU1MkMxOC4yNTU1IDYxLjcxNjQgMjkuNTUxMyA3MS4xODQyIDQzLjA1ODUgNzEuMTg0MkM0Ny45NzU0IDcxLjE4NDIgNTIuNTk5MyA2OS45Mjk2IDU2LjYyNzYgNjcuNzIzVjcwLjk5MjZINzEuMzQzNVY0NC4wNzE2QzcxLjM1NjkgNDMuNzEzOCA3MS4zNDM1IDQzLjI2MDMgNzEuMzQzNSA0Mi44OTkyQzcxLjM0MzUgMjcuMjc3OSA1OC42Nzk5IDE0LjYxNDMgNDMuMDU4NSAxNC42MTQzWk0yOS41MDk2IDQyLjg5OTJDMjkuNTA5NiAzNS40MTY0IDM1LjU3NTcgMjkuMzUwMyA0My4wNTg1IDI5LjM1MDNDNTAuNTQxNCAyOS4zNTAzIDU2LjYwNzQgMzUuNDE2NCA1Ni42MDc0IDQyLjg5OTJDNTYuNjA3NCA1MC4zODIxIDUwLjU0MTQgNTYuNDQ4MSA0My4wNTg1IDU2LjQ0ODFDMzUuNTc1NyA1Ni40NDgxIDI5LjUwOTYgNTAuMzgyMSAyOS41MDk2IDQyLjg5OTJaIiBmaWxsPSIjMTYxQzJEIi8+CjxwYXRoIGQ9Ik0xOC4xMDUgNDIuODgwNUMxOC4xMDUgNDUuMzgwMyAxNi4wNzg1IDQ3LjQwNjggMTMuNTc4NyA0Ny40MDY4QzExLjA3ODkgNDcuNDA2OCA5LjA1MjM3IDQ1LjM4MDMgOS4wNTIzNyA0Mi44ODA1QzkuMDUyMzcgNDAuMzgwNyAxMS4wNzg5IDM4LjM1NDIgMTMuNTc4NyAzOC4zNTQyQzE2LjA3ODUgMzguMzU0MiAxOC4xMDUgNDAuMzgwNyAxOC4xMDUgNDIuODgwNVoiIGZpbGw9IiMxNjFDMkQiLz4KPC9nPgo8ZGVmcz4KPGNsaXBQYXRoIGlkPSJjbGlwMF81OTlfMTEwNyI+CjxyZWN0IHdpZHRoPSI4NiIgaGVpZ2h0PSI4NiIgZmlsbD0id2hpdGUiLz4KPC9jbGlwUGF0aD4KPC9kZWZzPgo8L3N2Zz4K
[Avalonia-url]: https://avaloniaui.net/
[QQ-Group]: https://img.shields.io/badge/加入-QQ_群-EB1923?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIGhlaWdodD0iODYiIHdpZHRoPSI4NiIgdmlld0JveD0iMCAwIDEyMCAxNDUiPjxwYXRoIGZpbGw9IiNmYWFiMDciIGQ9Ik02MC41MDMgMTQyLjIzN2MtMTIuNTMzIDAtMjQuMDM4LTQuMTk1LTMxLjQ0NS0xMC40Ni0zLjc2MiAxLjEyNC04LjU3NCAyLjkzMi0xMS42MSA1LjE3NS0yLjYgMS45MTgtMi4yNzUgMy44NzQtMS44MDcgNC42NjMgMi4wNTYgMy40NyAzNS4yNzMgMi4yMTYgNDQuODYyIDEuMTM2em0wIDBjMTIuNTM1IDAgMjQuMDM5LTQuMTk1IDMxLjQ0Ny0xMC40NiAzLjc2IDEuMTI0IDguNTczIDIuOTMyIDExLjYxIDUuMTc1IDIuNTk4IDEuOTE4IDIuMjc0IDMuODc0IDEuODA1IDQuNjYzLTIuMDU2IDMuNDctMzUuMjcyIDIuMjE2LTQ0Ljg2MiAxLjEzNnptMCAwIi8+PHBhdGggZD0iTTYwLjU3NiA2Ny4xMTljMjAuNjk4LS4xNCAzNy4yODYtNC4xNDcgNDIuOTA3LTUuNjgzIDEuMzQtLjM2NyAyLjA1Ni0xLjAyNCAyLjA1Ni0xLjAyNC4wMDUtLjE4OS4wODUtMy4zNy4wODUtNS4wMUMxMDUuNjI0IDI3Ljc2OCA5Mi41OC4wMDEgNjAuNSAwIDI4LjQyLjAwMSAxNS4zNzUgMjcuNzY5IDE1LjM3NSA1NS40MDFjMCAxLjY0Mi4wOCA0LjgyMi4wODYgNS4wMSAwIDAgLjU4My42MTUgMS42NS45MTMgNS4xOSAxLjQ0NCAyMi4wOSA1LjY1IDQzLjMxMiA1Ljc5NXptNTYuMjQ1IDIzLjAyYy0xLjI4My00LjEyOS0zLjAzNC04Ljk0NC00LjgwOC0xMy41NjggMCAwLTEuMDItLjEyNi0xLjUzNy4wMjMtMTUuOTEzIDQuNjIzLTM1LjIwMiA3LjU3LTQ5LjkgNy4zOTJoLS4xNTNjLTE0LjYxNi4xNzUtMzMuNzc0LTIuNzM3LTQ5LjYzNC03LjMxNS0uNjA2LS4xNzUtMS44MDItLjEtMS44MDItLjEtMS43NzQgNC42MjQtMy41MjUgOS40NC00LjgwOCAxMy41NjgtNi4xMTkgMTkuNjktNC4xMzYgMjcuODM4LTIuNjI3IDI4LjAyIDMuMjM5LjM5MiAxMi42MDYtMTQuODIxIDEyLjYwNi0xNC44MjEgMCAxNS40NTkgMTMuOTU3IDM5LjE5NSA0NS45MTggMzkuNDEzaC44NDhjMzEuOTYtLjIxOCA0NS45MTctMjMuOTU0IDQ1LjkxNy0zOS40MTMgMCAwIDkuMzY4IDE1LjIxMyAxMi42MDcgMTQuODIyIDEuNTA4LS4xODMgMy40OTEtOC4zMzItMi42MjctMjguMDIxIi8+PHBhdGggZmlsbD0iI2ZmZiIgZD0iTTQ5LjA4NSA0MC44MjRjLTQuMzUyLjE5Ny04LjA3LTQuNzYtOC4zMDQtMTEuMDYzLS4yMzYtNi4zMDUgMy4wOTgtMTEuNTc2IDcuNDUtMTEuNzczIDQuMzQ3LS4xOTUgOC4wNjQgNC43NiA4LjMgMTEuMDY1LjIzOCA2LjMwNi0zLjA5NyAxMS41NzctNy40NDYgMTEuNzcxbTMxLjEzMy0xMS4wNjNjLS4yMzMgNi4zMDItMy45NTEgMTEuMjYtOC4zMDMgMTEuMDYzLTQuMzUtLjE5NS03LjY4NC01LjQ2NS03LjQ0Ni0xMS43Ny4yMzYtNi4zMDUgMy45NTItMTEuMjYgOC4zLTExLjA2NiA0LjM1Mi4xOTcgNy42ODYgNS40NjggNy40NDkgMTEuNzczIi8+PHBhdGggZmlsbD0iI2ZhYWIwNyIgZD0iTTg3Ljk1MiA0OS43MjVDODYuNzkgNDcuMTUgNzUuMDc3IDQ0LjI4IDYwLjU3OCA0NC4yOGgtLjE1NmMtMTQuNSAwLTI2LjIxMiAyLjg3LTI3LjM3NSA1LjQ0NmEuODYzLjg2MyAwIDAwLS4wODUuMzY3Ljg4Ljg4IDAgMDAuMTYuNDk2Yy45OCAxLjQyNyAxMy45ODUgOC40ODcgMjcuMyA4LjQ4N2guMTU2YzEzLjMxNCAwIDI2LjMxOS03LjA1OCAyNy4yOTktOC40ODdhLjg3My44NzMgMCAwMC4xNi0uNDk4Ljg1Ni44NTYgMCAwMC0uMDg1LS4zNjUiLz48cGF0aCBkPSJNNTQuNDM0IDI5Ljg1NGMuMTk5IDIuNDktMS4xNjcgNC43MDItMy4wNDYgNC45NDMtMS44ODMuMjQyLTMuNTY4LTEuNTgtMy43NjgtNC4wNy0uMTk3LTIuNDkyIDEuMTY3LTQuNzA0IDMuMDQzLTQuOTQ0IDEuODg2LS4yNDQgMy41NzQgMS41OCAzLjc3MSA0LjA3bTExLjk1Ni44MzNjLjM4NS0uNjg5IDMuMDA0LTQuMzEyIDguNDI3LTIuOTkzIDEuNDI1LjM0NyAyLjA4NC44NTcgMi4yMjMgMS4wNTcuMjA1LjI5Ni4yNjIuNzE4LjA1MyAxLjI4Ni0uNDEyIDEuMTI2LTEuMjYzIDEuMDk1LTEuNzM0Ljg3NS0uMzA1LS4xNDItNC4wODItMi42Ni03LjU2MiAxLjA5Ny0uMjQuMjU3LS42NjguMzQ2LTEuMDczLjA0LS40MDctLjMwOC0uNTc0LS45My0uMzM0LTEuMzYyIi8+PHBhdGggZmlsbD0iI2ZmZiIgZD0iTTYwLjU3NiA4My4wOGgtLjE1M2MtOS45OTYuMTItMjIuMTE2LTEuMjA0LTMzLjg1NC0zLjUxOC0xLjAwNCA1LjgxOC0xLjYxIDEzLjEzMi0xLjA5IDIxLjg1MyAxLjMxNiAyMi4wNDMgMTQuNDA3IDM1LjkgMzQuNjE0IDM2LjFoLjgyYzIwLjIwOC0uMiAzMy4yOTgtMTQuMDU3IDM0LjYxNi0zNi4xLjUyLTguNzIzLS4wODctMTYuMDM1LTEuMDkyLTIxLjg1NC0xMS43MzkgMi4zMTUtMjMuODYyIDMuNjQtMzMuODYgMy41MTgiLz48cGF0aCBmaWxsPSIjZWIxOTIzIiBkPSJNMzIuMTAyIDgxLjIzNXYyMS42OTNzOS45MzcgMi4wMDQgMTkuODkzLjYxNlY4My41MzVjLTYuMzA3LS4zNTctMTMuMTA5LTEuMTUyLTE5Ljg5My0yLjMiLz48cGF0aCBmaWxsPSIjZWIxOTIzIiBkPSJNMTA1LjUzOSA2MC40MTJzLTE5LjMzIDYuMTAyLTQ0Ljk2MyA2LjI3NWgtLjE1M2MtMjUuNTkxLS4xNzItNDQuODk2LTYuMjU1LTQ0Ljk2Mi02LjI3NUw4Ljk4NyA3Ni41N2MxNi4xOTMgNC44ODIgMzYuMjYxIDguMDI4IDUxLjQzNiA3Ljg0NWguMTUzYzE1LjE3NS4xODMgMzUuMjQyLTIuOTYzIDUxLjQzNy03Ljg0NXptMCAwIi8+PC9zdmc+
