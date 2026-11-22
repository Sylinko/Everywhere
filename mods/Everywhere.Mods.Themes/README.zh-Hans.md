# Everywhere 主题系统

为 Everywhere 提供可扩展的主题引擎，支持浅色/深色模式分别指定主题、内置流行社区预设（`Catppuccin`、`Ayu`、`GitHub`、`Tokyo Night`、`Nord`、`Gruvbox`、`Everforest`、`Dracula`），并同时支持 JSON 自定义主题与第三方 Mod 扩展注册。

## 核心特性

- **浅色 / 深色模式独立配置**：分别选择浅色与深色模式下的主题，跟随 Everywhere 外观切换自动生效。
- **可选全局强调色覆盖**：默认采用主题自带主色，也可打开开关沿用显示设置中的全局强调色。
- **标准语义色板自动派生**：从标准 `ThemePalette` 自动展开 ShadUI 全套色值与透明梯度、AvaloniaEdit 兼容色、`ColorTheme` 代码高亮及 Markdown HTML 样式表。
- **支持 JSON 自定义主题**：自动从模组数据目录 `themes/` 加载 `*.json` 主题配置。

## 给其他 Mod 开发者（C# 扩展 API）

在你的 `.csproj` 中声明对 `everywhere.themes` 的依赖并导入其程序集：

```xml
<ItemGroup>
  <EverywhereModDependency Identity="everywhere.themes" Version="[1.0.0,)" Assemblies="Everywhere.Mods.Themes" />
</ItemGroup>
```

在 `InitializeAsync` 中调用 `ThemeRegistry.RegisterTheme(...)`，并将返回的释放句柄交给 `context.Register(...)`：

```csharp
var handle = ThemeRegistry.RegisterTheme(new ThemeDefinition
{
    Id = "my.custom.theme",
    DisplayName = "我的自定义主题",
    Variant = ThemeBaseVariant.Dark,
    Palette = new ThemePalette
    {
        WindowBackground = Color.Parse("#1E1E2E"),
        CardBackground = Color.Parse("#313244"),
        SecondaryCardBackground = Color.Parse("#181825"),
        DialogBackground = Color.Parse("#181825"),
        InputBackground = Color.Parse("#11111B"),
        Foreground = Color.Parse("#CDD6F4"),
        MutedForeground = Color.Parse("#A6ADC8"),
        Border = Color.Parse("#45475A"),
        Primary = Color.Parse("#CBA6F7"),
        Secondary = Color.Parse("#313244"),
        Destructive = Color.Parse("#F38BA8"),
        Info = Color.Parse("#89B4FA"),
        Success = Color.Parse("#A6E3A1"),
        Warning = Color.Parse("#F9E2AF"),
    }
});
context.Register(handle);
```
