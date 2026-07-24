namespace Everywhere.Chat.Permissions;

[Flags]
public enum ChatFunctionPermissions : uint
{
    /// <summary>
    /// The minimal permissions that can be auto-granted to a function without prompting the user.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_FileRead)]
    BypassApproval = FileRead,

    /// <summary>
    /// No permissions granted. This is the default state.
    /// </summary>
    None = 0,

    /// <summary>
    /// Allows reading content from the screen (e.g., screenshots, UI element text).
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ScreenRead)]
    ScreenRead = 1 << 0, // 1

    /// <summary>
    /// Allows displaying information on the screen (e.g., notifications, UI changes) or interacting with the screen.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ScreenAccess)]
    ScreenAccess = ScreenRead | 1 << 1, // 1 | 2 = 3

    /// <summary>
    /// Allows accessing the network. This covers both sending and receiving data.
    /// </summary>
    /// <remarks>
    /// We cannot distinguish between read and write operations for network access,
    /// so this permission encompasses both.
    /// </remarks>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_NetworkAccess)]
    NetworkAccess = 1 << 2, // 4

    /// <summary>
    /// Allows reading from the system clipboard.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ClipboardRead)]
    ClipboardRead = 1 << 3, // 8

    /// <summary>
    /// Allows reading and writing to the system clipboard.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ClipboardAccess)]
    ClipboardAccess = ClipboardRead | 1 << 4, // 8 | 16 = 24

    /// <summary>
    /// Allows reading from the local file system.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_FileRead)]
    FileRead = 1 << 5, // 32

    /// <summary>
    /// Allows reading, writing or modifying files on the local file system.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_FileAccess)]
    FileAccess = FileRead | 1 << 6, // 32 | 64 = 96

    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ProcessAccess)]
    ProcessAccess = 1 << 7, // 128

    /// <summary>
    /// Allows executing local shell commands. This is a high-risk permission
    /// that can potentially perform any system-level action.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatFunctionPermissions_ShellExecute)]
    ShellExecute = ScreenAccess | NetworkAccess | ClipboardAccess | FileAccess | ProcessAccess | 1 << 8,

    /// <summary>
    /// MCP tool permission, equivalent to ShellExecute (all permissions).
    /// </summary>
    MCP = ShellExecute | 1 << 9,

    AllAccess = uint.MaxValue,
}