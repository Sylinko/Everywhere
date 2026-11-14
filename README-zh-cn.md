<a id="readme-top"></a>

<div align="center">
  <img src="https://github.com/user-attachments/assets/fdc68ffd-9660-4185-a236-6ec985c00e88" alt="Everywhere Banner" width="800" />

  <h1>Everywhere</h1>
  <p><strong>懂你眼前的内容，也能动手完成任务。</strong></p>
  <p>随处唤起的桌面 AI 助手。围绕当前屏幕、选中文本和文件开始对话，<br />借助工具与子代理，把问题推进到结果。</p>

  <p>
    <strong><a href="https://everywhere.sylinko.com/zh/download">下载 Everywhere</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="#quick-start">快速开始</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://www.bilibili.com/video/BV1mcxvzfESz">观看演示</a></strong>
  </p>
  <p>Windows &middot; macOS</p>
  <p><a href="README.md">English</a> &middot; <strong>简体中文</strong> &middot; <a href="README-ja-jp.md">日本語</a></p>
</div>

---

<img width="100%" alt="Everywhere 场景策略与交互界面" src="https://github.com/user-attachments/assets/2b45476b-ff8a-4b0f-8df0-1a16a47cc27b" />

<p align="center">从眼前的内容开始，不必先把工作搬进另一个聊天窗口。</p>

<p align="center">
  <a href="#capabilities">核心能力</a> &middot;
  <a href="#approval">审批控制</a> &middot;
  <a href="#examples">使用场景</a> &middot;
  <a href="#quick-start">快速开始</a> &middot;
  <a href="#community">文档与社区</a>
</p>

<a id="capabilities"></a>

## 从理解情境，到完成任务

### 就在当前应用中开始

按下呼出快捷键，让 Everywhere 围绕当前窗口、选中文本或附加的文件提供帮助。结合无障碍 API、结构化界面信息与屏幕截图，减少手动复制、粘贴和解释背景的步骤。

**场景策略**会根据浏览器、代码、文件和选中文本等上下文提供相应入口；**划词工具栏**让翻译、解释与改写更顺手。

### 不止给出答案，也能交付结果

- **检索与整理**：搜索网页、提取正文，查找本地文件并读取、搜索和修改内容。
- **执行与操作**：运行终端命令和脚本，查询桌面窗口与界面元素，执行点击、输入和快捷键等操作。
- **文档产出**：内置 OfficeCLI，可创建、分析和修改 Word、Excel、PowerPoint 文件，无需安装 Microsoft Office。
- **多步骤协作**：用待办清单组织任务，委派子代理处理子任务，并在需要补充信息时向你提问。
- **长对话继续工作**：自动压缩上下文，将较早内容整理为摘要，为后续任务腾出空间。

<a id="approval"></a>

### 少一些打断，保留你的控制

工具需要批准时，可以由你亲自确认，也可以交给审批助手。聊天窗口中提供三种模式：

| 模式 | 工具需要批准时 |
| --- | --- |
| **请求批准** · 默认 | 向你展示操作并请求确认，支持按需记住授权。 |
| **帮我批准** | AI 审批助手结合任务上下文、具体操作和风险决定是否放行；拒绝或审批失败时，阻止该操作并反馈原因。 |
| **完全访问权限** · 高风险 | 跳过审批，已启用的工具可直接执行操作，包括文件修改和终端命令。请谨慎开启。 |

自动审批会额外调用模型，也可能误判。对话上下文、操作信息，以及你允许审批助手读取的文件内容，会由所选审批模型处理；可在「系统助手」设置中单独指定审批助手。

### 模型、技能与工具，由你组合

- **自选模型**：使用 Everywhere 云服务，或连接 OpenAI、Claude、Gemini、DeepSeek、Kimi、MiniMax 等服务；支持兼容的自定义 API 端点和 Ollama 本地模型。
- **Skills**：发现并管理本地 `SKILL.md` 技能，让模型按需读取领域知识与操作说明。支持 Everywhere 自有技能目录，以及 Agents、Claude、Codex、Copilot、Cursor、Gemini 等约定目录。
- **MCP**：连接 MCP 服务器，把外部工具接入同一个对话工作流。
- **系统集成**：Windows 提供 Everything 文件搜索；macOS 提供提醒事项、日历等系统应用集成。

界面支持明暗主题、Markdown 与数学公式渲染，以及简体中文、繁体中文、英语、德语、西班牙语、法语、意大利语、日语、韩语、俄语和土耳其语。

<a id="examples"></a>

## 试着把这些任务交给 Everywhere

### 从一篇文章，到一份可分享的文档

> 总结当前文章，补充检索相关资料，把要点和来源整理成一份 Word 文档。

围绕当前页面获取上下文，结合网页检索与 OfficeCLI，将阅读延伸到资料整理和文档产出。

### 从一份表格，到清晰的分析结果

> 检查这份 Excel 中的缺失值和重复记录，在新工作表中汇总发现，保留原始数据。

附加文件后，让文件工具与 OfficeCLI 协作处理，明确告诉助手要保留哪些内容、把结果放在哪里。

### 从一个报错，到可执行的排查

> 解释当前报错，检查我提供的日志，先给出排查结论；需要修改文件或运行修复命令时，先问我。

从屏幕上的问题开始，逐步转向文件和终端工具。需要亲自确认操作时，使用「请求批准」模式。

<a id="quick-start"></a>

## 快速开始

1. **下载安装**：前往[下载页面](https://everywhere.sylinko.com/zh/download)，选择对应平台的安装包。Windows 也提供便携版；macOS 提供 Apple Silicon 与 Intel 版本。
2. **连接模型**：按照首次启动向导，选择 Everywhere 云服务、自己的 API 服务或本地模型。
3. **呼出并开始**：设置呼出快捷键，在正在使用的应用中打开 Everywhere，选取上下文或附加文件，描述你想得到的结果。
4. **启用所需工具**：执行 Agent 任务时，选择支持工具调用的模型，并启用相关聊天工具。初次使用建议保留默认的「请求批准」模式。

详细配置见[快速开始指南](https://everywhere.sylinko.com/zh/docs/quick-start)。macOS 用户请按引导授予所需的辅助功能与屏幕录制权限。

### 系统要求

| 平台 | 最低系统要求 | 安装选项 |
| --- | --- | --- |
| Windows | Windows 10（10.0.19041.0） | x64 安装包 / 便携版 |
| macOS | macOS Sonoma 14.0 | Apple Silicon / Intel 安装包 |
| Linux | 开发中 | — |

<a id="community"></a>

## 文档与社区

- [官方文档](https://everywhere.sylinko.com/zh/docs)：安装、配置与使用指南。
- [Discord](https://discord.gg/5fyg6nE3yn) · [QQ 用户群](https://qm.qq.com/cgi-bin/qm/qr?k=wp9aDBBnLc7pYATqT99tB-N2ZP2ETmJC&jump_from=webapi&authKey=97qUJfsQoI70dUNcgBZ0C3HCZeiEn8inLT7pzg8x+KinbQwfIrHFu3dB2+aHMbRD)：交流用法，分享反馈。
- [报告问题](https://github.com/Sylinko/Everywhere/issues/new?labels=bug&template=bug-report.md) · [提出功能建议](https://github.com/Sylinko/Everywhere/issues/new?labels=enhancement&template=feature-request.md)。

## 参与贡献

欢迎提交问题反馈、翻译校对、文档改进和 Pull Request。开发前请阅读[贡献指南](CONTRIBUTING.md)与[本地构建指南](docs/build.md)。

[![.NET 11][.NET 11]][.NET-url] [![Avalonia][Avalonia]][Avalonia-url]

感谢每一位参与 Everywhere 的贡献者：

<a href="https://github.com/Sylinko/Everywhere/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Sylinko/Everywhere" alt="Everywhere 贡献者" />
</a>

## 赞助与鸣谢

<a href="https://share.302.ai/5rzmPr"><img src="img/Sponsors/302-ai-zh.jpg" width="600" alt="302.ai 赞助商" /></a>

[302.AI](https://share.302.ai/5rzmPr) 是按用量付费的企业级 AI 资源平台，提供模型 API 与在线 AI 应用。

感谢 [Certum 中国](https://www.certumcodesign.cn/) 赞助本项目的代码签名证书。

感谢 [pasical](https://github.com/pasical) 设计 Banner 中的可爱 Logo，也感谢社区的关注与推荐。

<div align="center">
  <div>
    <a href="https://trendshift.io/repositories/15106" target="_blank"><img src="https://trendshift.io/api/badge/repositories/15106" alt="Sylinko%2FEverywhere | Trendshift" width="250"/></a>
    <a href="https://www.producthunt.com/products/everywhere?embed=true&utm_source=badge-featured&utm_medium=badge&utm_source=badge-everywhere" target="_blank"><img src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1034853&theme=light&t=1762403775174" alt="Product Hunt" width="250" /></a>
    <a href="https://hellogithub.com/repository/Sylinko/Everywhere" target="_blank"><img src="https://abroad.hellogithub.com/v1/widgets/recommend.svg?rid=0bd4328c24794902bd6097055cda6f36&claim_uid=LNYEf6O9Qv5JeR2" alt="Featured｜HelloGitHub" width="250" /></a>
  </div>
</div>

## 许可证

使用与分发请遵循 [LICENSE](LICENSE)。第三方依赖与组件许可证见 [ThirdPartyNotices.txt](ThirdPartyNotices.txt)。

<details>
<summary>星标历史</summary>

<a href="https://star-history.dera.page/#Sylinko/Everywhere&type=date">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://star-history.dera.page/svg?repos=Sylinko/Everywhere&type=date&legend=top-left" />
 </picture>
</a>

</details>

<p align="right"><a href="#readme-top">返回顶部 ↑</a></p>

<!-- MARKDOWN LINKS & IMAGES -->

[.NET 11]: https://img.shields.io/badge/.NET_11-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[.NET-url]: https://dotnet.microsoft.com/
[Avalonia]: https://img.shields.io/badge/Avalonia-1c2e5f?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB3aWR0aD0iODYiIGhlaWdodD0iODYiIHZpZXdCb3g9IjAgMCA4NiA4NiIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KPGcgY2xpcC1wYXRoPSJ1cmwoI2NsaXAwXzU5OV8xMTA3KSI+CjxwYXRoIGQ9Ik03NC44NTM1IDg1LjgyMzFDNzUuMDI2MyA4NS44MjMxIDc1LjE5NTQgODUuODIzMSA3NS4zNjc5IDg1LjgyMzFDODAuNzM0NyA4NS44MjMxIDg1LjE0MzkgODEuODAyNyA4NS43NjE0IDc2LjYwMTlMODUuODM1NyA0MS43NjA0Qzg1LjIyNTUgMTguNTkzMSA2Ni4yNTM3IDAgNDIuOTM5MyAwQzE5LjIzOTkgMCAwLjAyNzcxIDE5LjIxMjIgMC4wMjc3MSA0Mi45MTE2QzAuMDI3NzEgNjYuMzU3MyAxOC44MzA5IDg1LjQxOCA0Mi4xOCA4NS44MjMxSDc0Ljg1MzVaIiBmaWxsPSIjRjlGOUZCIi8+CjxwYXRoIGZpbGwtcnVsZT0iZXZlbm9kZCIgY2xpcC1ydWxlPSJldmVub2RkIiBkPSJNNDMuMDU4NSAxNC42MTQzQzI5LjU1MTMgMTQuNjE0MyAxOC4yNTU1IDI0LjA4MiAxNS40NDU0IDM2Ljc0MzJDMTguMTM1NyAzNy40OTc1IDIwLjEwODcgMzkuOTY3OSAyMC4xMDg3IDQyLjg5OTJDMjAuMTA4NyA0NS44MzA1IDE4LjEzNTcgNDguMzAxIDE1LjQ0NTQgNDkuMDU1MkMxOC4yNTU1IDYxLjcxNjQgMjkuNTUxMyA3MS4xODQyIDQzLjA1ODUgNzEuMTg0MkM0Ny45NzU0IDcxLjE4NDIgNTIuNTk5MyA2OS45Mjk2IDU2LjYyNzYgNjcuNzIzVjcwLjk5MjZINzEuMzQzNVY0NC4wNzE2QzcxLjM1NjkgNDMuNzEzOCA3MS4zNDM1IDQzLjI2MDMgNzEuMzQzNSA0Mi44OTkyQzcxLjM0MzUgMjcuMjc3OSA1OC42Nzk5IDE0LjYxNDMgNDMuMDU4NSAxNC42MTQzWk0yOS41MDk2IDQyLjg5OTJDMjkuNTA5NiAzNS40MTY0IDM1LjU3NTcgMjkuMzUwMyA0My4wNTg1IDI5LjM1MDNDNTAuNTQxNCAyOS4zNTAzIDU2LjYwNzQgMzUuNDE2NCA1Ni42MDc0IDQyLjg5OTJDNTYuNjA3NCA1MC4zODIxIDUwLjU0MTQgNTYuNDQ4MSA0My4wNTg1IDU2LjQ0ODFDMzUuNTc1NyA1Ni40NDgxIDI5LjUwOTYgNTAuMzgyMSAyOS41MDk2IDQyLjg5OTJaIiBmaWxsPSIjMTYxQzJEIi8+CjxwYXRoIGQ9Ik0xOC4xMDUgNDIuODgwNUMxOC4xMDUgNDUuMzgwMyAxNi4wNzg1IDQ3LjQwNjggMTMuNTc4NyA0Ny40MDY4QzExLjA3ODkgNDcuNDA2OCA5LjA1MjM3IDQ1LjM4MDMgOS4wNTIzNyA0Mi44ODA1QzkuMDUyMzcgNDAuMzgwNyAxMS4wNzg5IDM4LjM1NDIgMTMuNTc4NyAzOC4zNTQyQzE2LjA3ODUgMzguMzU0MiAxOC4xMDUgNDAuMzgwNyAxOC4xMDUgNDIuODgwNVoiIGZpbGw9IiMxNjFDMkQiLz4KPC9nPgo8ZGVmcz4KPGNsaXBQYXRoIGlkPSJjbGlwMF81OTlfMTEwNyI+CjxyZWN0IHdpZHRoPSI4NiIgaGVpZ2h0PSI4NiIgZmlsbD0id2hpdGUiLz4KPC9jbGlwUGF0aD4KPC9kZWZzPgo8L3N2Zz4K
[Avalonia-url]: https://avaloniaui.net/