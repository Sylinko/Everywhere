# Everywhere Themes

Provides an extensible theme engine for Everywhere with independent Light/Dark theme selection, built-in community presets (`Catppuccin`, `Ayu`, `GitHub`, `Tokyo Night`, `Nord`, `Gruvbox`, `Everforest`, `Dracula`), and custom theme support for both JSON files and dependent mods.

## Features

- Independent **Light theme** and **Dark theme** slots that automatically switch with Everywhere's Light/Dark/System mode.
- Optional **Override theme accent color** switch to reuse the global accent color configured in Display Settings.
- Dynamic compilation from a normalized semantic `ThemePalette` to all ShadUI colors, AvaloniaEdit tokens, syntax highlighting `ThemeName`, and Markdown CSS (`BaseStylesheet`).
- Custom JSON theme loader reading `*.json` from the mod's data `themes/` folder.

## For Mod Authors (C# API)

Add a dependency on `everywhere.themes` in your `.csproj`:

```xml
<ItemGroup>
  <EverywhereModDependency Identity="everywhere.themes" Version="[1.0.0,)" Assemblies="Everywhere.Mods.Themes" />
</ItemGroup>
```

Then register your themes in `InitializeAsync` and pass the disposable handle to `context.Register(...)`:

```csharp
var handle = ThemeRegistry.RegisterTheme(new ThemeDefinition
{
    Id = "my.custom.theme",
    DisplayName = "My Custom Theme",
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

## Custom JSON Theme Format

Place one or more `*.json` files inside the `themes` folder opened from mod settings:

```json
{
  "id": "custom.midnight",
  "name": "Custom Midnight",
  "author": "You",
  "variant": "dark",
  "codeTheme": "DarkPlus",
  "palette": {
    "windowBackground": "#161822",
    "cardBackground": "#212434",
    "secondaryCardBackground": "#12141D",
    "dialogBackground": "#1C1E2B",
    "inputBackground": "#11131B",
    "foreground": "#E2E6F3",
    "mutedForeground": "#8E96B0",
    "border": "#30354D",
    "primary": "#7AA2F7",
    "secondary": "#272B3F",
    "destructive": "#F7768E",
    "info": "#7DCFFF",
    "success": "#9ECE6A",
    "warning": "#E0AF68"
  }
}
```
