using Windows.Win32.UI.Input.KeyboardAndMouse;
using Avalonia;
using Avalonia.Input;

namespace Everywhere.Windows.Extensions;

internal static class AvaloniaExtensions
{
    public static Key ToAvaloniaKey(this VIRTUAL_KEY key) => key switch
    {
        // Alphanumeric keys
        >= VIRTUAL_KEY.VK_A and <= VIRTUAL_KEY.VK_Z => (Key)((int)Key.A + ((int)key - (int)VIRTUAL_KEY.VK_A)),

        // Function keys
        >= VIRTUAL_KEY.VK_F1 and <= VIRTUAL_KEY.VK_F24 => (Key)((int)Key.F1 + ((int)key - (int)VIRTUAL_KEY.VK_F1)),

        // Number keys (top row)
        >= VIRTUAL_KEY.VK_0 and <= VIRTUAL_KEY.VK_9 => (Key)((int)Key.D0 + ((int)key - (int)VIRTUAL_KEY.VK_0)),

        // Numpad keys
        >= VIRTUAL_KEY.VK_NUMPAD0 and <= VIRTUAL_KEY.VK_NUMPAD9 => (Key)((int)Key.NumPad0 + ((int)key - (int)VIRTUAL_KEY.VK_NUMPAD0)),

        // Special keys
        VIRTUAL_KEY.VK_BACK => Key.Back,
        VIRTUAL_KEY.VK_TAB => Key.Tab,
        VIRTUAL_KEY.VK_RETURN => Key.Return,
        VIRTUAL_KEY.VK_ESCAPE => Key.Escape,
        VIRTUAL_KEY.VK_SPACE => Key.Space,
        VIRTUAL_KEY.VK_PRIOR => Key.PageUp,
        VIRTUAL_KEY.VK_NEXT => Key.PageDown,
        VIRTUAL_KEY.VK_END => Key.End,
        VIRTUAL_KEY.VK_HOME => Key.Home,
        VIRTUAL_KEY.VK_LEFT => Key.Left,
        VIRTUAL_KEY.VK_UP => Key.Up,
        VIRTUAL_KEY.VK_RIGHT => Key.Right,
        VIRTUAL_KEY.VK_DOWN => Key.Down,
        VIRTUAL_KEY.VK_SNAPSHOT => Key.PrintScreen,
        VIRTUAL_KEY.VK_INSERT => Key.Insert,
        VIRTUAL_KEY.VK_DELETE => Key.Delete,
        VIRTUAL_KEY.VK_HELP => Key.Help,
        VIRTUAL_KEY.VK_LWIN => Key.LWin,
        VIRTUAL_KEY.VK_RWIN => Key.RWin,
        VIRTUAL_KEY.VK_APPS => Key.Apps,
        VIRTUAL_KEY.VK_SLEEP => Key.Sleep,
        VIRTUAL_KEY.VK_MULTIPLY => Key.Multiply,
        VIRTUAL_KEY.VK_ADD => Key.Add,
        VIRTUAL_KEY.VK_SEPARATOR => Key.Separator,
        VIRTUAL_KEY.VK_SUBTRACT => Key.Subtract,
        VIRTUAL_KEY.VK_DECIMAL => Key.Decimal,
        VIRTUAL_KEY.VK_DIVIDE => Key.Divide,
        VIRTUAL_KEY.VK_SHIFT => Key.LeftShift,
        VIRTUAL_KEY.VK_CONTROL => Key.LeftCtrl,
        VIRTUAL_KEY.VK_MENU => Key.LeftAlt,
        VIRTUAL_KEY.VK_PAUSE => Key.Pause,
        VIRTUAL_KEY.VK_CAPITAL => Key.CapsLock,
        VIRTUAL_KEY.VK_LSHIFT => Key.LeftShift,
        VIRTUAL_KEY.VK_RSHIFT => Key.RightShift,
        VIRTUAL_KEY.VK_LCONTROL => Key.LeftCtrl,
        VIRTUAL_KEY.VK_RCONTROL => Key.RightCtrl,
        VIRTUAL_KEY.VK_LMENU => Key.LeftAlt,
        VIRTUAL_KEY.VK_RMENU => Key.RightAlt,
        VIRTUAL_KEY.VK_NUMLOCK => Key.NumLock,
        VIRTUAL_KEY.VK_SCROLL => Key.Scroll,

        // Browser keys
        VIRTUAL_KEY.VK_BROWSER_BACK => Key.BrowserBack,
        VIRTUAL_KEY.VK_BROWSER_FORWARD => Key.BrowserForward,
        VIRTUAL_KEY.VK_BROWSER_REFRESH => Key.BrowserRefresh,
        VIRTUAL_KEY.VK_BROWSER_STOP => Key.BrowserStop,
        VIRTUAL_KEY.VK_BROWSER_SEARCH => Key.BrowserSearch,
        VIRTUAL_KEY.VK_BROWSER_FAVORITES => Key.BrowserFavorites,
        VIRTUAL_KEY.VK_BROWSER_HOME => Key.BrowserHome,

        // Media keys
        VIRTUAL_KEY.VK_VOLUME_MUTE => Key.VolumeMute,
        VIRTUAL_KEY.VK_VOLUME_DOWN => Key.VolumeDown,
        VIRTUAL_KEY.VK_VOLUME_UP => Key.VolumeUp,
        VIRTUAL_KEY.VK_MEDIA_NEXT_TRACK => Key.MediaNextTrack,
        VIRTUAL_KEY.VK_MEDIA_PREV_TRACK => Key.MediaPreviousTrack,
        VIRTUAL_KEY.VK_MEDIA_STOP => Key.MediaStop,
        VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE => Key.MediaPlayPause,
        VIRTUAL_KEY.VK_LAUNCH_MAIL => Key.LaunchMail,
        VIRTUAL_KEY.VK_LAUNCH_MEDIA_SELECT => Key.SelectMedia,
        VIRTUAL_KEY.VK_LAUNCH_APP1 => Key.LaunchApplication1,
        VIRTUAL_KEY.VK_LAUNCH_APP2 => Key.LaunchApplication2,

        // OEM keys
        VIRTUAL_KEY.VK_OEM_1 => Key.OemSemicolon,
        VIRTUAL_KEY.VK_OEM_PLUS => Key.OemPlus,
        VIRTUAL_KEY.VK_OEM_COMMA => Key.OemComma,
        VIRTUAL_KEY.VK_OEM_MINUS => Key.OemMinus,
        VIRTUAL_KEY.VK_OEM_PERIOD => Key.OemPeriod,
        VIRTUAL_KEY.VK_OEM_2 => Key.OemQuestion,
        VIRTUAL_KEY.VK_OEM_3 => Key.OemTilde,
        VIRTUAL_KEY.VK_ABNT_C1 => Key.AbntC1,
        VIRTUAL_KEY.VK_ABNT_C2 => Key.AbntC2,
        VIRTUAL_KEY.VK_OEM_4 => Key.OemOpenBrackets,
        VIRTUAL_KEY.VK_OEM_5 => Key.OemPipe,
        VIRTUAL_KEY.VK_OEM_6 => Key.OemCloseBrackets,
        VIRTUAL_KEY.VK_OEM_7 => Key.OemQuotes,
        VIRTUAL_KEY.VK_OEM_8 => Key.Oem8,
        VIRTUAL_KEY.VK_OEM_102 => Key.OemBackslash,
        VIRTUAL_KEY.VK_OEM_CLEAR => Key.OemClear,

        // Other special keys
        VIRTUAL_KEY.VK_CLEAR => Key.Clear,
        VIRTUAL_KEY.VK_CANCEL => Key.Cancel,
        VIRTUAL_KEY.VK_PRINT => Key.Print,
        VIRTUAL_KEY.VK_EXECUTE => Key.Execute,
        VIRTUAL_KEY.VK_SELECT => Key.Select,
        VIRTUAL_KEY.VK_ATTN => Key.Attn,
        VIRTUAL_KEY.VK_CRSEL => Key.CrSel,
        VIRTUAL_KEY.VK_EXSEL => Key.ExSel,
        VIRTUAL_KEY.VK_EREOF => Key.EraseEof,
        VIRTUAL_KEY.VK_PLAY => Key.Play,
        VIRTUAL_KEY.VK_ZOOM => Key.Zoom,
        VIRTUAL_KEY.VK_NONAME => Key.NoName,
        VIRTUAL_KEY.VK_PA1 => Key.Pa1,

        // IME keys
        VIRTUAL_KEY.VK_KANA => Key.KanaMode,
        VIRTUAL_KEY.VK_JUNJA => Key.JunjaMode,
        VIRTUAL_KEY.VK_FINAL => Key.FinalMode,
        VIRTUAL_KEY.VK_HANJA => Key.HanjaMode,
        VIRTUAL_KEY.VK_CONVERT => Key.ImeConvert,
        VIRTUAL_KEY.VK_NONCONVERT => Key.ImeNonConvert,
        VIRTUAL_KEY.VK_ACCEPT => Key.ImeAccept,
        VIRTUAL_KEY.VK_MODECHANGE => Key.ImeModeChange,
        VIRTUAL_KEY.VK_PROCESSKEY => Key.ImeProcessed,

        // DBCS keys
        VIRTUAL_KEY.VK_DBE_ALPHANUMERIC => Key.DbeAlphanumeric,
        VIRTUAL_KEY.VK_DBE_KATAKANA => Key.DbeKatakana,
        VIRTUAL_KEY.VK_DBE_HIRAGANA => Key.DbeHiragana,
        VIRTUAL_KEY.VK_DBE_SBCSCHAR => Key.DbeSbcsChar,
        VIRTUAL_KEY.VK_DBE_DBCSCHAR => Key.DbeDbcsChar,
        VIRTUAL_KEY.VK_DBE_ROMAN => Key.DbeRoman,

        _ => 0
    };

    public static VIRTUAL_KEY ToVirtualKey(this Key key) => key switch
    {
        // Alphanumeric keys
        >= Key.A and <= Key.Z => (VIRTUAL_KEY)((int)VIRTUAL_KEY.VK_A + (key - Key.A)),

        // Function keys
        >= Key.F1 and <= Key.F24 => (VIRTUAL_KEY)((int)VIRTUAL_KEY.VK_F1 + (key - Key.F1)),

        // Number keys (top row)
        >= Key.D0 and <= Key.D9 => (VIRTUAL_KEY)((int)VIRTUAL_KEY.VK_0 + (key - Key.D0)),

        // Numpad keys
        >= Key.NumPad0 and <= Key.NumPad9 => (VIRTUAL_KEY)((int)VIRTUAL_KEY.VK_NUMPAD0 + (key - Key.NumPad0)),

        // Special keys
        Key.Back => VIRTUAL_KEY.VK_BACK,
        Key.Tab => VIRTUAL_KEY.VK_TAB,
        Key.Return => VIRTUAL_KEY.VK_RETURN,
        Key.Escape => VIRTUAL_KEY.VK_ESCAPE,
        Key.Space => VIRTUAL_KEY.VK_SPACE,
        Key.PageUp => VIRTUAL_KEY.VK_PRIOR,
        Key.PageDown => VIRTUAL_KEY.VK_NEXT,
        Key.End => VIRTUAL_KEY.VK_END,
        Key.Home => VIRTUAL_KEY.VK_HOME,
        Key.Left => VIRTUAL_KEY.VK_LEFT,
        Key.Up => VIRTUAL_KEY.VK_UP,
        Key.Right => VIRTUAL_KEY.VK_RIGHT,
        Key.Down => VIRTUAL_KEY.VK_DOWN,
        Key.PrintScreen => VIRTUAL_KEY.VK_SNAPSHOT,
        Key.Insert => VIRTUAL_KEY.VK_INSERT,
        Key.Delete => VIRTUAL_KEY.VK_DELETE,
        Key.Help => VIRTUAL_KEY.VK_HELP,
        Key.LWin => VIRTUAL_KEY.VK_LWIN,
        Key.RWin => VIRTUAL_KEY.VK_RWIN,
        Key.Apps => VIRTUAL_KEY.VK_APPS,
        Key.Sleep => VIRTUAL_KEY.VK_SLEEP,
        Key.Multiply => VIRTUAL_KEY.VK_MULTIPLY,
        Key.Add => VIRTUAL_KEY.VK_ADD,
        Key.Separator => VIRTUAL_KEY.VK_SEPARATOR,
        Key.Subtract => VIRTUAL_KEY.VK_SUBTRACT,
        Key.Decimal => VIRTUAL_KEY.VK_DECIMAL,
        Key.Divide => VIRTUAL_KEY.VK_DIVIDE,
        Key.LeftShift => VIRTUAL_KEY.VK_LSHIFT,
        Key.RightShift => VIRTUAL_KEY.VK_RSHIFT,
        Key.LeftCtrl => VIRTUAL_KEY.VK_LCONTROL,
        Key.RightCtrl => VIRTUAL_KEY.VK_RCONTROL,
        Key.LeftAlt => VIRTUAL_KEY.VK_LMENU,
        Key.RightAlt => VIRTUAL_KEY.VK_RMENU,
        Key.Pause => VIRTUAL_KEY.VK_PAUSE,
        Key.CapsLock => VIRTUAL_KEY.VK_CAPITAL,
        Key.NumLock => VIRTUAL_KEY.VK_NUMLOCK,
        Key.Scroll => VIRTUAL_KEY.VK_SCROLL,

        // Browser keys
        Key.BrowserBack => VIRTUAL_KEY.VK_BROWSER_BACK,
        Key.BrowserForward => VIRTUAL_KEY.VK_BROWSER_FORWARD,
        Key.BrowserRefresh => VIRTUAL_KEY.VK_BROWSER_REFRESH,
        Key.BrowserStop => VIRTUAL_KEY.VK_BROWSER_STOP,
        Key.BrowserSearch => VIRTUAL_KEY.VK_BROWSER_SEARCH,
        Key.BrowserFavorites => VIRTUAL_KEY.VK_BROWSER_FAVORITES,
        Key.BrowserHome => VIRTUAL_KEY.VK_BROWSER_HOME,

        // Media keys
        Key.VolumeMute => VIRTUAL_KEY.VK_VOLUME_MUTE,
        Key.VolumeDown => VIRTUAL_KEY.VK_VOLUME_DOWN,
        Key.VolumeUp => VIRTUAL_KEY.VK_VOLUME_UP,
        Key.MediaNextTrack => VIRTUAL_KEY.VK_MEDIA_NEXT_TRACK,
        Key.MediaPreviousTrack => VIRTUAL_KEY.VK_MEDIA_PREV_TRACK,
        Key.MediaStop => VIRTUAL_KEY.VK_MEDIA_STOP,
        Key.MediaPlayPause => VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE,
        Key.LaunchMail => VIRTUAL_KEY.VK_LAUNCH_MAIL,
        Key.SelectMedia => VIRTUAL_KEY.VK_LAUNCH_MEDIA_SELECT,
        Key.LaunchApplication1 => VIRTUAL_KEY.VK_LAUNCH_APP1,
        Key.LaunchApplication2 => VIRTUAL_KEY.VK_LAUNCH_APP2,

        // OEM keys
        Key.OemSemicolon => VIRTUAL_KEY.VK_OEM_1,
        Key.OemPlus => VIRTUAL_KEY.VK_OEM_PLUS,
        Key.OemComma => VIRTUAL_KEY.VK_OEM_COMMA,
        Key.OemMinus => VIRTUAL_KEY.VK_OEM_MINUS,
        Key.OemPeriod => VIRTUAL_KEY.VK_OEM_PERIOD,
        Key.OemQuestion => VIRTUAL_KEY.VK_OEM_2,
        Key.OemTilde => VIRTUAL_KEY.VK_OEM_3,
        Key.AbntC1 => VIRTUAL_KEY.VK_ABNT_C1,
        Key.AbntC2 => VIRTUAL_KEY.VK_ABNT_C2,
        Key.OemOpenBrackets => VIRTUAL_KEY.VK_OEM_4,
        Key.OemPipe => VIRTUAL_KEY.VK_OEM_5,
        Key.OemCloseBrackets => VIRTUAL_KEY.VK_OEM_6,
        Key.OemQuotes => VIRTUAL_KEY.VK_OEM_7,
        Key.Oem8 => VIRTUAL_KEY.VK_OEM_8,
        Key.OemBackslash => VIRTUAL_KEY.VK_OEM_102,
        Key.OemClear => VIRTUAL_KEY.VK_OEM_CLEAR,

        // Other special keys
        Key.Clear => VIRTUAL_KEY.VK_CLEAR,
        Key.Cancel => VIRTUAL_KEY.VK_CANCEL,
        Key.Print => VIRTUAL_KEY.VK_PRINT,
        Key.Execute => VIRTUAL_KEY.VK_EXECUTE,
        Key.Select => VIRTUAL_KEY.VK_SELECT,
        Key.Attn => VIRTUAL_KEY.VK_ATTN,
        Key.CrSel => VIRTUAL_KEY.VK_CRSEL,
        Key.ExSel => VIRTUAL_KEY.VK_EXSEL,
        Key.EraseEof => VIRTUAL_KEY.VK_EREOF,
        Key.Play => VIRTUAL_KEY.VK_PLAY,
        Key.Zoom => VIRTUAL_KEY.VK_ZOOM,
        Key.NoName => VIRTUAL_KEY.VK_NONAME,
        Key.Pa1 => VIRTUAL_KEY.VK_PA1,

        // IME keys
        Key.KanaMode => VIRTUAL_KEY.VK_KANA,
        Key.JunjaMode => VIRTUAL_KEY.VK_JUNJA,
        Key.FinalMode => VIRTUAL_KEY.VK_FINAL,
        Key.HanjaMode => VIRTUAL_KEY.VK_HANJA,
        Key.ImeConvert => VIRTUAL_KEY.VK_CONVERT,
        Key.ImeNonConvert => VIRTUAL_KEY.VK_NONCONVERT,
        Key.ImeAccept => VIRTUAL_KEY.VK_ACCEPT,
        Key.ImeModeChange => VIRTUAL_KEY.VK_MODECHANGE,
        Key.ImeProcessed => VIRTUAL_KEY.VK_PROCESSKEY,

        // DBCS keys
        Key.DbeAlphanumeric => VIRTUAL_KEY.VK_DBE_ALPHANUMERIC,
        Key.DbeKatakana => VIRTUAL_KEY.VK_DBE_KATAKANA,
        Key.DbeHiragana => VIRTUAL_KEY.VK_DBE_HIRAGANA,
        Key.DbeSbcsChar => VIRTUAL_KEY.VK_DBE_SBCSCHAR,
        Key.DbeDbcsChar => VIRTUAL_KEY.VK_DBE_DBCSCHAR,
        Key.DbeRoman => VIRTUAL_KEY.VK_DBE_ROMAN,

        // Return 0 for unsupported keys
        _ => 0
    };

    public static KeyModifiers ToKeyModifiers(this VIRTUAL_KEY key) => key switch
    {
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT => KeyModifiers.Shift,
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL => KeyModifiers.Control,
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU => KeyModifiers.Alt,
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN => KeyModifiers.Meta,
        _ => KeyModifiers.None
    };
}