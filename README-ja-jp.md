<a id="readme-top"></a>

<a href="https://github.com/Sylinko/Everywhere/blob/main/README.md">English version »</a>
<a href="https://github.com/Sylinko/Everywhere/blob/main/README-zh-cn.md">简体中文版本 »</a>

<div align="center">
  <img src="https://github.com/user-attachments/assets/fdc68ffd-9660-4185-a236-6ec985c00e88" alt="Banner"/>

  <h1>いつでも、どこでも。あなたのAI - <code>Everywhere</code></h1>

  <div>
    <a href="https://trendshift.io/repositories/15106" target="_blank"><img src="https://trendshift.io/api/badge/repositories/15106" alt="Sylinko%2FEverywhere | Trendshift" width="250"/></a>
    <a href="https://www.producthunt.com/products/everywhere?embed=true&utm_source=badge-featured&utm_medium=badge&utm_source=badge-everywhere" target="_blank"><img src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1034853&theme=light&t=1762403775174" alt="Product Hunt" width="250" /></a>
    <a href="https://hellogithub.com/repository/Sylinko/Everywhere" target="_blank"><img src="https://abroad.hellogithub.com/v1/widgets/recommend.svg?rid=0bd4328c24794902bd6097055cda6f36&claim_uid=LNYEf6O9Qv5JeR2" alt="Featured｜HelloGitHub" width="250" /></a>
  </div>

  <br/>

  [![.NET 10][.NET 10]][.NET-url][![Avalonia][Avalonia]][Avalonia-url]
  [![Join Discord](https://dcbadge.limes.pink/api/server/5fyg6nE3yn)](https://discord.gg/5fyg6nE3yn)
  [![Join QQ Group][QQ-Group]](https://qm.qq.com/cgi-bin/qm/qr?k=wp9aDBBnLc7pYATqT99tB-N2ZP2ETmJC&jump_from=webapi&authKey=97qUJfsQoI70dUNcgBZ0C3HCZeiEn8inLT7pzg8x+KinbQwfIrHFu3dB2+aHMbRD)

  <p align="center">
    <strong><a href="https://everywhere.sylinko.com">📖 公式ドキュメントを見る</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://youtu.be/BGujYa5hbXo">🎬 トレーラーを観る</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://github.com/Sylinko/Everywhere/issues/new?labels=bug&template=bug-report.md">🪲 バグを報告する</a></strong>
    &nbsp;&middot;&nbsp;
    <strong><a href="https://github.com/Sylinko/Everywhere/issues/new?labels=enhancement&template=feature-request.md">💡 機能をリクエストする</a></strong>
  </p>
</div>

<details>
<summary>📖 <b>目次</b></summary>

- [🦄 Everywhere について](#-everywhere-について)
  - [🌟 利用シーンの例](#-利用シーンの例)
  - [🛠️ 技術的特徴](#️-技術的特徴)
- [⚙️ コア技術とアーキテクチャ](#️-コア技術とアーキテクチャ)
- [📋 システム要件](#-システム要件)
- [🚀 はじめに](#-はじめに)
  - [入手とインストール](#入手とインストール)
  - [ヘルプとコミュニティ](#ヘルプとコミュニティ)
- [🤝 コントリビューション](#-コントリビューション)
- [📄 ライセンス](#-ライセンス)
- [💖 スポンサー](#-スポンサー)
- [🤩 スペシャルサンクス](#-スペシャルサンクス)
- [📈 Star History](#-star-history)

</details>

<br/>

## 🦄 Everywhere について

**Everywhere** は、コンテキスト認識能力を備えたインタラクティブな AI アシスタントです。洗練されたモダンな UI と強力な統合機能を特徴としています。従来の AI ツールとは異なり、Everywhere は画面上のあらゆるものを瞬時に認識して理解します。スクリーンショットもコピーもアプリの切り替えも不要——ショートカットキーを押すだけで、その場で必要なサポートが得られ、シームレスな AI アシスタント体験を提供します。

<img width="100%" alt="Strategy Engine" src="https://github.com/user-attachments/assets/2b45476b-ff8a-4b0f-8df0-1a16a47cc27b" />

### 🌟 利用シーンの例

<br/>

> **🩺 トラブルシューティングの専門家**
>
> PC の使用中にエラーメッセージに遭遇したものの、解決方法がわからない。そんなときはエラーメッセージの横で <kbd>Everywhere</kbd> を呼び出し、*「このエラーは何？どうやって解決すればいい？」*と入力するだけで、**Everywhere** がその場でメッセージのコンテキストを取得し、的確な解決策を提示します。

> **📰 Web ページの即時要約**
>
> 長い技術記事を読んでいて、要点だけ知りたいとき。Web ページ上で <kbd>Everywhere</kbd> を呼び出して*「短く要約して」*と尋ねるだけで、全文を読まずに主要な論点を即座に得られます。

> **🌐 即時翻訳**
>
> 文献調査中に外国語の単語に出くわしたら？テキストをハイライトするか、その上で <kbd>Everywhere</kbd> を呼び出して*「日本語に翻訳して」*と伝えるだけで、別の翻訳ツールを開くことなく即座に翻訳が表示されます。

> **✉️ メール下書きのサポート**
>
> 重要なビジネスメールのトーンに自信がないとき。下書きの上で <kbd>Everywhere</kbd> を呼び出し、*「このメールをもっとプロフェッショナルにして」*と入力すれば、カジュアルな文章が洗練されたビジネス文書に生まれ変わります。

> **❔ これって本当？**
>
> 真偽が不確かな情報に出会ったら？該当するテキストを選択して <kbd>Everywhere</kbd> を呼び出し、*「これは本当？」*と尋ねるだけで、**Everywhere** が素早く検索して情報の信頼性を検証します。

### 🛠️ 技術的特徴

<table align="center">
  <thead>
    <tr>
      <th style="width:30%">カテゴリ</th>
      <th style="width:45%">✅ 現在サポート中</th>
      <th style="width:25%">🚧 開発中</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><b>🤖 豊富なモデルエコシステム</b></td>
      <td>
        • Everywhere クラウドサービス<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="OpenAI" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/openai.svg"> OpenAI<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="Anthropic" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/anthropic.svg"> Anthropic (Claude)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="Google" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/gemini-color.svg"> Google (Gemini)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="DeepSeek" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/deepseek-color.svg"> DeepSeek<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="Moonshot" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/moonshot.svg"> Moonshot (Kimi)<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="MiniMax" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/minimax-color.svg"> MiniMax<br>
        • <img style="margin-top:3px;margin-bottom:-3px;" alt="Ollama" src="https://registry.npmmirror.com/@lobehub/icons-static-svg/latest/files/icons/ollama.svg"> ローカルデプロイ (Ollama)<br>
        • カスタム API エンドポイントとの互換性<br>
      </td>
    </tr>
    <tr>
      <td><b>⚙️ 強力なエージェントシステム</b></td>
      <td>
        • Web ブラウザ<br>
        • サブエージェントのディスパッチ<br>
        • ローカルファイルシステム<br>
        • ターミナルスクリプトの実行<br>
        • MCP ツール<br>
        • Everything 高速検索 <i>(Windows)</i><br>
        • システムアプリとの統合 <i>(macOS)</i>
      </td>
      <td>
        • メモリーシステム<br>
      </td>
    </tr>
    <tr>
      <td><b>🫧 シームレスなインタラクション</b></td>
      <td>
        • 究極のモダンなすりガラス UI<br>
        • インテリジェントなコンテキスト認識<br>
        • グローバルシステムホットキー<br>
        • テキスト選択インタラクション<br>
        • リッチな Markdown と数式のレンダリング
      </td>
      <td>
        • マウスショートカット<br>
        • 音声インタラクション<br>
      </td>
    </tr>
    <tr>
      <td><b>🖥️ マルチプラットフォーム</b></td>
      <td>
        • 🪟 Windows<br>
        • 🍎 macOS
      </td>
      <td>
        • 🐧 Linux
      </td>
    </tr>
    <tr>
      <td><b>🌐 多言語（i18n）サポート</b></td>
      <td>
        • 簡体字/繁体字中国語<br>
        • English、Deutsch、Español<br>
        • Français、Italiano、日本語<br>
        • 한국어、Русский、Türkçe<br>
      </td>
      <td>
        一部は AI 支援による翻訳です。<br>翻訳への貢献・修正を歓迎します！<br>
      </td>
    </tr>
  </tbody>
</table>

## ⚙️ コア技術とアーキテクチャ

- **👁️ 広範なコンテキスト認識**: 従来の画面マルチモーダルのサポートに加え、低レベルのアクセシビリティ API と UI オートメーション技術を深く統合しています。これにより、多種多様なソフトウェアからアクティブな構造化環境データを低侵襲かつ正確に抽出できます。
- **🧠 シナリオ呼び出しと戦略エンジン**: 🚧 作業のリズムを乱すワークフローを完全に終わらせたい——それが開発の原点です。これまで LLM を使うには、選択＆コピー → AI ウィンドウへの切り替え → 貼り付け → 意図を手入力、という手順が必要でした。戦略エンジンによる高度な機能により、ショートカットで呼び出された Everywhere は、閲覧中のシナリオやアプリを瞬時に認識します。要望を説明する必要なく、コンテキストにぴったり合ったショートカット実行戦略を提示し、真のフロー状態を実現します。
- **🧱 コア品質へのこだわり**: 私たちはソフトウェアのパフォーマンス、コード品質、セキュリティを重視しています。システムのコア基盤から、モダンなアーキテクチャ設計と緻密なシステムエンジニアリングを通じて、「Vibe Coding」への単純な依存や盲目的なアジャイルの積み重ねを根本から拒否します。すべてのコード行が丁寧に書かれ、細部まで磨き上げられています。

## 📋 システム要件

| プラットフォーム | 最小システム要件          |
| --------------- | ------------------------- |
| 🪟 Windows       | Windows 10 (10.0.19041.0) |
| 🍎 macOS         | Monterey 12.0             |
| 🐧 Linux         | 🚧 **開発中**              |

## 🚀 はじめに

### 入手とインストール

> [!TIP]
> [公式サイト](https://everywhere.sylinko.com/download)から、お使いのシステムに適したバージョンを直接ダウンロードすることをおすすめします。

**Windows**
- `Everywhere-Windows-x64-Setup-vx.x.x.exe`: ウィザード形式のフルインストーラーパッケージ *（推奨）*。
- `Everywhere-Windows-x64-vx.x.x.zip`: ポータブル版（インストール不要）の zip パッケージ。

**macOS**
- `Everywhere-macOS-arm64-vx.x.x.pkg`: Apple Silicon（M シリーズ）Mac デバイス向け。
- `Everywhere-macOS-x64-vx.x.x.pkg`: Intel Mac デバイス向け。

### ヘルプとコミュニティ

> [!NOTE]
> はじめての方や、MCP のような高度な統合機能をお探しの方は、まず公式ガイドをご覧になることを強くおすすめします！

- **📖 公式ドキュメント**: [https://everywhere.sylinko.com](https://everywhere.sylinko.com)
- **👾 Discord コミュニティ**: [チャンネルに参加してサポートを受ける](https://discord.gg/5fyg6nE3yn)
- **💬 中国語ユーザーグループ**: [クリックして QQ グループに参加](https://qm.qq.com/cgi-bin/qm/qr?k=wp9aDBBnLc7pYATqT99tB-N2ZP2ETmJC&jump_from=webapi&authKey=97qUJfsQoI70dUNcgBZ0C3HCZeiEn8inLT7pzg8x+KinbQwfIrHFu3dB2+aHMbRD)

## 🤝 コントリビューション

私たちはオープンソースを愛しており、皆さんの素晴らしいアイデアやコードの貢献（プルリクエスト）を歓迎します！コードスタイルのガイドラインとローカルでのコンパイル手順については [CONTRIBUTING.md](CONTRIBUTING.md) をご覧ください。

開発環境のセットアップとプロジェクトのローカルビルドの詳細な手順については、[ビルドガイド](docs/build.md)をご確認ください。

**プロジェクトの創成期から成長期にかけて貢献してくださった、すべての素晴らしいコントリビューターの皆さんに心から感謝します：**

<a href="https://github.com/Sylinko/Everywhere/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Sylinko/Everywhere" alt="Everywhere Contributors Image" />
</a>

## 📄 ライセンス

Copyright © 2026 Sylinko Inc. All rights reserved.

本プロジェクトは Business Source License 1.1 のもとで公開されています。詳細は [LICENSE](LICENSE) ファイルをご覧ください。
*サードパーティ依存関係とオープンソースコンポーネントのライセンスについては、[ThirdPartyNotices.txt](ThirdPartyNotices.txt) を参照してください。*

## 💖 スポンサー

<a href="https://share.302.ai/5rzmPr"><img src="https://raw.githubusercontent.com/Sylinko/Everywhere/refs/heads/main/img/Sponsors/302-ai-en.jpg" width="600" alt="302.ai Sponsor"/></a><br/>

[302.AI](https://share.302.ai/5rzmPr) は、従量課金制のエンタープライズ向け AI リソースハブです。市場で最新かつ最も包括的な AI モデルと API に加え、すぐに使える多彩なオンライン AI アプリケーションを提供しています。

## 🤩 スペシャルサンクス

本プロジェクトのコード署名証明書は [Certum China](https://www.certumcodesign.cn/) のご厚意によりスポンサーいただいており、オープンソースコミュニティへの多大な貢献を続けています。

バナーのかわいいロゴをデザインしてくれた [pasical](https://github.com/pasical) に感謝します。

## 📈 Star History

<br/>

<a href="https://www.star-history.com/?type=date&repos=Sylinko%2FEverywhere">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=Sylinko/Everywhere&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=Sylinko/Everywhere&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=Sylinko/Everywhere&type=date&legend=top-left" />
 </picture>
</a>

<br/>

<p align="right"><a href="#readme-top">⬆️ トップに戻る</a></p>

<!-- MARKDOWN LINKS & IMAGES -->

[.NET 10]: https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[.NET-url]: https://dotnet.microsoft.com/
[Avalonia]: https://img.shields.io/badge/Avalonia-1c2e5f?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB3aWR0aD0iODYiIGhlaWdodD0iODYiIHZpZXdCb3g9IjAgMCA4NiA4NiIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KPGcgY2xpcC1wYXRoPSJ1cmwoI2NsaXAwXzU5OV8xMTA3KSI+CjxwYXRoIGQ9Ik03NC44NTM1IDg1LjgyMzFDNzUuMDI2MyA4NS44MjMxIDc1LjE5NTQgODUuODIzMSA3NS4zNjc5IDg1LjgyMzFDODAuNzM0NyA4NS44MjMxIDg1LjE0MzkgODEuODAyNyA4NS43NjE0IDc2LjYwMTlMODUuODM1NyA0MS43NjA0Qzg1LjIyNTUgMTguNTkzMSA2Ni4yNTM3IDAgNDIuOTM5MyAwQzE5LjIzOTkgMCAwLjAyNzcxIDE5LjIxMjIgMC4wMjc3MSA0Mi45MTE2QzAuMDI3NzEgNjYuMzU3MyAxOC44MzA5IDg1LjQxOCA0Mi4xOCA4NS44MjMxSDc0Ljg1MzVaIiBmaWxsPSIjRjlGOUZCIi8+CjxwYXRoIGZpbGwtcnVsZT0iZXZlbm9kZCIgY2xpcC1ydWxlPSJldmVub2RkIiBkPSJNNDMuMDU4NSAxNC42MTQzQzI5LjU1MTMgMTQuNjE0MyAxOC4yNTU1IDI0LjA4MiAxNS40NDU0IDM2Ljc0MzJDMTguMTM1NyAzNy40OTc1IDIwLjEwODcgMzkuOTY3OSAyMC4xMDg3IDQyLjg5OTJDMjAuMTA4NyA0NS44MzA1IDE4LjEzNTcgNDguMzAxIDE1LjQ0NTQgNDkuMDU1MkMxOC4yNTU1IDYxLjcxNjQgMjkuNTUxMyA3MS4xODQyIDQzLjA1ODUgNzEuMTg0MkM0Ny45NzU0IDcxLjE4NDIgNTIuNTk5MyA2OS45Mjk2IDU2LjYyNzYgNjcuNzIzVjcwLjk5MjZINzEuMzQzNVY0NC4wNzE2QzcxLjM1NjkgNDMuNzEzOCA3MS4zNDM1IDQzLjI2MDMgNzEuMzQzNSA0Mi44OTkyQzcxLjM0MzUgMjcuMjc3OSA1OC42Nzk5IDE0LjYxNDMgNDMuMDU4NSAxNC42MTQzWk0yOS41MDk2IDQyLjg5OTJDMjkuNTA5NiAzNS40MTY0IDM1LjU3NTcgMjkuMzUwMyA0My4wNTg1IDI5LjM1MDNDNTAuNTQxNCAyOS4zNTAzIDU2LjYwNzQgMzUuNDE2NCA1Ni42MDc0IDQyLjg5OTJDNTYuNjA3NCA1MC4zODIxIDUwLjU0MTQgNTYuNDQ4MSA0My4wNTg1IDU2LjQ0ODFDMzUuNTc1NyA1Ni40NDgxIDI5LjUwOTYgNTAuMzgyMSAyOS41MDk2IDQyLjg5OTJaIiBmaWxsPSIjMTYxQzJEIi8+CjxwYXRoIGQ9Ik0xOC4xMDUgNDIuODgwNUMxOC4xMDUgNDUuMzgwMyAxNi4wNzg1IDQ3LjQwNjggMTMuNTc4NyA0Ny40MDY4QzExLjA3ODkgNDcuNDA2OCA5LjA1MjM3IDQ1LjM4MDMgOS4wNTIzNyA0Mi44ODA1QzkuMDUyMzcgNDAuMzgwNyAxMS4wNzg5IDM4LjM1NDIgMTMuNTc4NyAzOC4zNTQyQzE2LjA3ODUgMzguMzU0MiAxOC4xMDUgNDAuMzgwNyAxOC4xMDUgNDIuODgwNVoiIGZpbGw9IiMxNjFDMkQiLz4KPC9nPgo8ZGVmcz4KPGNsaXBQYXRoIGlkPSJjbGlwMF81OTlfMTEwNyI+CjxyZWN0IHdpZHRoPSI4NiIgaGVpZ2h0PSI4NiIgZmlsbD0id2hpdGUiLz4KPC9jbGlwUGF0aD4KPC9kZWZzPgo8L3N2Zz4K
[Avalonia-url]: https://avaloniaui.net/
[QQ-Group]: https://img.shields.io/badge/加入-QQ_群-EB1923?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIGhlaWdodD0iODYiIHdpZHRoPSI4NiIgdmlld0JveD0iMCAwIDEyMCAxNDUiPjxwYXRoIGZpbGw9IiNmYWFiMDciIGQ9Ik02MC41MDMgMTQyLjIzN2MtMTIuNTMzIDAtMjQuMDM4LTQuMTk1LTMxLjQ0NS0xMC40Ni0zLjc2MiAxLjEyNC04LjU3NCAyLjkzMi0xMS42MSA1LjE3NS0yLjYgMS45MTgtMi4yNzUgMy44NzQtMS44MDcgNC42NjMgMi4wNTYgMy40NyAzNS4yNzMgMi4yMTYgNDQuODYyIDEuMTM2em0wIDBjMTIuNTM1IDAgMjQuMDM5LTQuMTk1IDMxLjQ0Ny0xMC40NiAzLjc2IDEuMTI0IDguNTczIDIuOTMyIDExLjYxIDUuMTc1IDIuNTk4IDEuOTE4IDIuMjc0IDMuODc0IDEuODA1IDQuNjYzLTIuMDU2IDMuNDctMzUuMjcyIDIuMjE2LTQ0Ljg2MiAxLjEzNnptMCAwIi8+PHBhdGggZD0iTTYwLjU3NiA2Ny4xMTljMjAuNjk4LS4xNCAzNy4yODYtNC4xNDcgNDIuOTA3LTUuNjgzIDEuMzQtLjM2NyAyLjA1Ni0xLjAyNCAyLjA1Ni0xLjAyNC4wMDUtLjE4OS4wODUtMy4zNy4wODUtNS4wMUMxMDUuNjI0IDI3Ljc2OCA5Mi41OC4wMDEgNjAuNSAwIDI4LjQyLjAwMSAxNS4zNzUgMjcuNzY5IDE1LjM3NSA1NS40MDFjMCAxLjY0Mi4wOCA0LjgyMi4wODYgNS4wMSAwIDAgLjU4My42MTUgMS42NS45MTMgNS4xOSAxLjQ0NCAyMi4wOSA1LjY1IDQzLjMxMiA1Ljc5NXptNTYuMjQ1IDIzLjAyYy0xLjI4My00LjEyOS0zLjAzNC04Ljk0NC00LjgwOC0xMy41NjggMCAwLTEuMDItLjEyNi0xLjUzNy4wMjMtMTUuOTEzIDQuNjIzLTM1LjIwMiA3LjU3LTQ5LjkgNy4zOTJoLS4xNTNjLTE0LjYxNi4xNzUtMzMuNzc0LTIuNzM3LTQ5LjYzNC03LjMxNS0uNjA2LS4xNzUtMS44MDItLjEtMS44MDItLjEtMS43NzQgNC42MjQtMy41MjUgOS40NC00LjgwOCAxMy41NjgtNi4xMTkgMTkuNjktNC4xMzYgMjcuODM4LTIuNjI3IDI4LjAyIDMuMjM5LjM5MiAxMi42MDYtMTQuODIxIDEyLjYwNi0xNC44MjEgMCAxNS40NTkgMTMuOTU3IDM5LjE5NSA0NS45MTggMzkuNDEzaC44NDhjMzEuOTYtLjIxOCA0NS45MTctMjMuOTU0IDQ1LjkxNy0zOS40MTMgMCAwIDkuMzY4IDE1LjIxMyAxMi42MDcgMTQuODIyIDEuNTA4LS4xODMgMy40OTEtOC4zMzItMi42MjctMjguMDIxIi8+PHBhdGggZmlsbD0iI2ZmZiIgZD0iTTQ5LjA4NSA0MC44MjRjLTQuMzUyLjE5Ny04LjA3LTQuNzYtOC4zMDQtMTEuMDYzLS4yMzYtNi4zMDUgMy4wOTgtMTEuNTc2IDcuNDUtMTEuNzczIDQuMzQ3LS4xOTUgOC4wNjQgNC43NiA4LjMgMTEuMDY1LjIzOCA2LjMwNi0zLjA5NyAxMS41NzctNy40NDYgMTEuNzcxbTMxLjEzMy0xMS4wNjNjLS4yMzMgNi4zMDItMy45NTEgMTEuMjYtOC4zMDMgMTEuMDYzLTQuMzUtLjE5NS03LjY4NC01LjQ2NS03LjQ0Ni0xMS43Ny4yMzYtNi4zMDUgMy45NTItMTEuMjYgOC4zLTExLjA2NiA0LjM1Mi4xOTcgNy42ODYgNS40NjggNy40NDkgMTEuNzczIi8+PHBhdGggZmlsbD0iI2ZhYWIwNyIgZD0iTTg3Ljk1MiA0OS43MjVDODYuNzkgNDcuMTUgNzUuMDc3IDQ0LjI4IDYwLjU3OCA0NC4yOGgtLjE1NmMtMTQuNSAwLTI2LjIxMiAyLjg3LTI3LjM3NSA1LjQ0NmEuODYzLjg2MyAwIDAwLS4wODUuMzY3Ljg4Ljg4IDAgMDAuMTYuNDk2Yy45OCAxLjQyNyAxMy45ODUgOC40ODcgMjcuMyA4LjQ4N2guMTU2YzEzLjMxNCAwIDI2LjMxOS03LjA1OCAyNy4yOTktOC40ODdhLjg3My44NzMgMCAwMC4xNi0uNDk4Ljg1Ni44NTYgMCAwMC0uMDg1LS4zNjUiLz48cGF0aCBkPSJNNTQuNDM0IDI5Ljg1NGMuMTk5IDIuNDktMS4xNjcgNC43MDItMy4wNDYgNC45NDMtMS44ODMuMjQyLTMuNTY4LTEuNTgtMy43NjgtNC4wNy0uMTk3LTIuNDkyIDEuMTY3LTQuNzA0IDMuMDQzLTQuOTQ0IDEuODg2LS4yNDQgMy41NzQgMS41OCAzLjc3MSA0LjA3bTExLjk1Ni44MzNjLjM4NS0uNjg5IDMuMDA0LTQuMzEyIDguNDI3LTIuOTkzIDEuNDI1LjM0NyAyLjA4NC44NTcgMi4yMjMgMS4wNTcuMjA1LjI5Ni4yNjIuNzE4LjA1MyAxLjI4Ni0uNDEyIDEuMTI2LTEuMjYzIDEuMDk1LTEuNzM0Ljg3NS0uMzA1LS4xNDItNC4wODItMi42Ni03LjU2MiAxLjA5Ny0uMjQuMjU3LS42NjguMzQ2LTEuMDczLjA0LS40MDctLjMwOC0uNTc0LS45My0uMzM0LTEuMzYyIi8+PHBhdGggZmlsbD0iI2ZmZiIgZD0iTTYwLjU3NiA4My4wOGgtLjE1M2MtOS45OTYuMTItMjIuMTE2LTEuMjA0LTMzLjg1NC0zLjUxOC0xLjAwNCA1LjgxOC0xLjYxIDEzLjEzMi0xLjA5IDIxLjg1MyAxLjMxNiAyMi4wNDMgMTQuNDA3IDM1LjkgMzQuNjE0IDM2LjFoLjgyYzIwLjIwOC0uMiAzMy4yOTgtMTQuMDU3IDM0LjYxNi0zNi4xLjUyLTguNzIzLS4wODctMTYuMDM1LTEuMDkyLTIxLjg1NC0xMS43MzkgMi4zMTUtMjMuODYyIDMuNjQtMzMuODYgMy41MTgiLz48cGF0aCBmaWxsPSIjZWIxOTIzIiBkPSJNMzIuMTAyIDgxLjIzNXYyMS42OTNzOS45MzcgMi4wMDQgMTkuODkzLjYxNlY4My41MzVjLTYuMzA3LS4zNTctMTMuMTA5LTEuMTUyLTE5Ljg5My0yLjMiLz48cGF0aCBmaWxsPSIjZWIxOTIzIiBkPSJNMTA1LjUzOSA2MC40MTJzLTE5LjMzIDYuMTAyLTQ0Ljk2MyA2LjI3NWgtLjE1M2MtMjUuNTkxLS4xNzItNDQuODk2LTYuMjU1LTQ0Ljk2Mi02LjI3NUw4Ljk4NyA3Ni41N2MxNi4xOTMgNC44ODIgMzYuMjYxIDguMDI4IDUxLjQzNiA3Ljg0NWguMTUzYzE1LjE3NS4xODMgMzUuMjQyLTIuOTYzIDUxLjQzNy03Ljg0NXptMCAwIi8+PC9zdmc+
