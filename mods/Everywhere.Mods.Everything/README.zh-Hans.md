# Everything 搜索模组

[English](README.md)

在 Everywhere 的聊天工具中通过 Everything 搜索本地文件与文件夹。
该模组作为 Windows x64 发行版的内嵌内置模组随宿主一同发布（通过 `EmbeddedModSource` 加载），仅支持 **Windows x64**；其他平台会显示 `Skipped` / `UnsupportedPlatform`。

## 连接规则

模组仅在首次查询时按以下顺序连接或启动 Everything：

1. 已运行的默认实例（含 Everything 1.4）。
2. 已运行的 Everything 1.5 `1.5a` 实例。
3. 用户通过配置或常规安装目录已安装的 Everything。
4. 若未找到用户安装，则使用内置的 Everything 1.5 在 `~/.everywhere/mods-data/everywhere.everything` 下启动独立实例。

用户自己的 Everything 进程不会在模组停用或卸载时被停止。只有模组自启动的独立实例会交由 Watchdog 监管并在停用时退出。

自启动独立实例需要系统已有兼容的 Everything 1.5 索引服务，或当前宿主以管理员权限运行；模组不会私自安装系统服务或请求提权。

## 设置

在聊天插件或模组管理页打开设置（两项均可选填）：

- **Everything 程序路径**：当指定实例未运行时用于启动便携版 `Everything.exe` 的绝对路径；留空自动查找。
- **Everything 实例名称**：精确指定 IPC 实例名；留空自动尝试默认实例与 `1.5a`。

设置会自动保存至 `~/.everywhere/mods-data/everywhere.everything/settings.json`，并在下一次查询时立即生效。

## 从源码构建

在仓库根目录运行：

```powershell
dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj -c Debug
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -c Debug -t:EverywherePack
```

默认通过 `src/Everywhere.Core/Everywhere.Core.csproj` 导入宿主合同，宿主兼容范围为 `[0.9.0-0,)`。如需指向本地已安装的宿主目录，可将 `Everywhere.local.props.example` 复制为 `Everywhere.local.props`。

随附的 Everything 二进制来自 [voidtools](https://www.voidtools.com/downloads/)，第三方许可声明位于 `Licenses/` 目录。
