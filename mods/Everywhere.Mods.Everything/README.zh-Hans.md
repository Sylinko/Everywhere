# Everything 搜索模组

[English](README.md)

在 Everywhere 的聊天工具中，通过 Everything 搜索本地文件和文件夹。
模组支持 **Windows x64**；其他平台显示 `Skipped` / `UnsupportedPlatform`，不执行模组代码。

## 安装

退出 Everywhere，将 `everywhere.everything-1.0.0.zip` 放到 `~/.everywhere/mods`，
然后启动支持模组的 Everywhere。请保留原始 ZIP，程序会在启动时将它导入独立的运行目录。
更新或移除 ZIP 前先退出 Everywhere。模组数据保留在
`~/.everywhere/mods-data/everywhere.everything`。

## 连接哪个 Everything

模组在第一次查询时才会连接或启动 Everything。自动选择顺序为：

1. 已运行的默认实例，包括 Everything 1.4。
2. 已运行的 Everything 1.5 `1.5a` 实例。
3. 电脑上已经安装的 Everything，必要时正常启动它。
4. 没有找到用户安装时，使用模组自带的 Everything 1.5.0.1424b 启动独立实例。

用户自己的 Everything 在模组停止后继续运行。只有模组启动的独立实例会交给 Watchdog
管理，并在模组释放时停止；它的配置和数据库保存在模组数据目录。

自带实例需要已经运行的兼容 Everything 1.5 索引服务，或当前进程已经具有管理员权限。
Everything 1.4 的服务不兼容。模组不会安装或升级服务、请求提权或修改用户的 Everything 安装。

## 设置

打开聊天工具页，选择 Everything，再打开设置。两个字段均可留空：

- **可执行文件路径**：选定实例不可用时，用于启动用户安装的 Everything。
  便携版如果不在常规安装目录，需要手动填写路径。
- **实例名称**：指定某个 Everything 1.5 索引。填写后不会悄悄改用其他索引。
  两个字段都留空时自动选择。

修改会自动保存，并在下一次查询时生效，已有聊天发起的查询也一样。
修改不会打断正在进行的查询，也不会停止用户自己的进程。请完成编辑后再查询。
设置文件位于 `~/.everywhere/mods-data/everywhere.everything/settings.json`，
仅在 Everywhere 退出后直接修改文件。

Everything 1.4 使用 SDK 2，同步查询无法中断，卸载时必须等待查询结束。
如果查询无响应，可能需要重启 Everywhere。Everything 1.5 在相应接口可用时使用 SDK 3，
支持取消查询和具名实例。

## 从源码构建

在 Everywhere 仓库根目录执行：

```powershell
dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj -c Debug
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -c Debug -t:EverywherePack
```

默认通过 ProjectReference 引用 Windows 宿主项目，会执行其正常构建前置步骤，包括发布 Watchdog。
如需使用其他宿主，将 `Everywhere.local.props.example` 复制为 Git 忽略的 `Everywhere.local.props`
并填写路径。声明的宿主兼容范围必须包含该宿主；项目当前使用 `[1.0.0]`。

输出为 `bin/Debug/net10.0-windows10.0.19041.0/win-x64/everywhere.everything-1.0.0.zip`。
SDK 打包 EverythingNetCore 和 x64 原生运行文件，排除宿主提供的程序集。
宿主引用、兼容范围和打包规则见[开发者文档](../../docs/Modding/02-ModDeveloperGuide.md)；
集成测试与尚待桌面验收的项目见[验证记录](../../docs/Modding/03-Validation.md)。

## 运行打包集成测试

构建模组包后，使用完整名称单独选择这项显式测试：

```powershell
$env:EVERYWHERE_EVERYTHING_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_EVERYTHING_PACKAGE = (Resolve-Path 'mods/Everywhere.Mods.Everything/bin/Debug/net10.0-windows10.0.19041.0/win-x64/everywhere.everything-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName=Everywhere.Core.Tests.Modding.EverythingModTests.PackagedEverything_QueryAndUnload_PreservesUserProcessAndRevokesTools'
```

测试使用两个隔离的临时 Everything 实例，检查嵌入图标、英中文设置、下一次查询采用新设置、
最终保存、旧调用撤销，以及托管程序集和原生模块卸载；不会安装服务或修改用户已有的 Everything 实例。

自带运行文件来自 [voidtools](https://www.voidtools.com/downloads/)。
EverythingNetCore、Everything 和 SDK 的许可证声明位于模组包的 `Licenses` 目录。
