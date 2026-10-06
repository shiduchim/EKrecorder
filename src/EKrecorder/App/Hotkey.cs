using System.Globalization;

namespace EKrecorder.App;

/// <summary>Modifier keys of a global shortcut. The values are RegisterHotKey's MOD_ flags.</summary>
[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global start/stop shortcut: modifiers plus one key (a Windows virtual-key code). Saved as text like
/// "Ctrl+Alt+R" or "Shift+Up", shown as "Ctrl + Alt + R" or "Shift + Up Arrow".
/// </summary>
internal readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    private static readonly (int Key, string Name, string Display)[] Named =
    [
        (0x25, "Left", "Left Arrow"),
        (0x26, "Up", "Up Arrow"),
        (0x27, "Right", "Right Arrow"),
        (0x28, "Down", "Down Arrow"),
        (0x24, "Home", "Home"),
        (0x23, "End", "End"),
        (0x21, "PageUp", "Page Up"),
        (0x22, "PageDown", "Page Down"),
        (0x2D, "Insert", "Insert"),
        (0x2E, "Delete", "Delete"),
        (0x20, "Space", "Space"),
        (0x13, "Pause", "Pause"),
        (0x2C, "PrintScreen", "Print Screen"),
    ];

    private static readonly (HotkeyModifiers Flag, string Name)[] ModifierNames =
    [
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Windows, "Win"),
    ];

    /// <summary>Ctrl + Alt + R: R for record, and rarely used by other programs.</summary>
    public static Hotkey Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'R');

    public bool IsEmpty => Key == 0;

    /// <summary>True for the keys a shortcut can use: letters, digits, F1-F24, arrows, navigation keys, Space, Pause, Print Screen, number pad digits.</summary>
    public static bool IsSupportedKey(int key) =>
        IsLetterOrDigit(key) || IsFunctionKey(key) || IsNumberPad(key) || Named.Any(n => n.Key == key);

    /// <summary>The text saved in settings.json ("" for no shortcut).</summary>
    public string ToSetting() => IsEmpty ? "" : string.Join("+", ModifierList(Modifiers).Append(KeyName(Key)));

    /// <summary>The text shown in the Settings window.</summary>
    public string ToDisplay() => IsEmpty ? "None" : string.Join(" + ", ModifierList(Modifiers).Append(KeyDisplay(Key)));

    private static IEnumerable<string> ModifierList(HotkeyModifiers modifiers) =>
        ModifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name);

    public override string ToString() => ToDisplay();

    /// <summary>Reads a saved shortcut. An empty text is "no shortcut" (true, empty).</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int key = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Replace(" ", "", StringComparison.Ordinal);
            HotkeyModifiers? modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => null,
            };
            if (modifier is { } flag)
            {
                modifiers |= flag;
                continue;
            }

            if (key != 0 || ParseKey(part) is not { } parsed)
            {
                return false;
            }

            key = parsed;
        }

        if (key == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>Why this shortcut is not a sensible choice, or null when it is.</summary>
    public string? Problem()
    {
        if (IsEmpty)
        {
            return null;
        }

        if (!IsSupportedKey(Key))
        {
            return "That key cannot be used. Use a letter, a number, an F key or an arrow key.";
        }

        if (IsFunctionKey(Key) || Key is 0x13 or 0x2C)
        {
            return null;
        }

        bool strong = (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0;
        if ((IsLetterOrDigit(Key) || IsNumberPad(Key) || Key == 0x20) && !strong)
        {
            return "Add Ctrl, Alt or Win to a letter, number or Space, so normal typing still works.";
        }

        return Modifiers == HotkeyModifiers.None ? "Add Ctrl, Alt, Shift or Win to that key." : null;
    }

    public static string KeyName(int key)
    {
        if (IsLetterOrDigit(key))
        {
            return ((char)key).ToString();
        }

        if (IsFunctionKey(key))
        {
            return string.Create(CultureInfo.InvariantCulture, $"F{key - 0x70 + 1}");
        }

        if (IsNumberPad(key))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Num{key - 0x60}");
        }

        return Named.FirstOrDefault(n => n.Key == key).Name ?? string.Create(CultureInfo.InvariantCulture, $"Key{key}");
    }

    public static string KeyDisplay(int key)
    {
        if (IsNumberPad(key))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Num {key - 0x60}");
        }

        return Named.FirstOrDefault(n => n.Key == key).Display ?? KeyName(key);
    }

    private static int? ParseKey(string part)
    {
        string upper = part.ToUpperInvariant();
        if (upper.Length == 1 && IsLetterOrDigit(upper[0]))
        {
            return upper[0];
        }

        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        if (upper.StartsWith("NUM", StringComparison.Ordinal) && int.TryParse(upper.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 9)
        {
            return 0x60 + n;
        }

        foreach ((int key, string name, string display) in Named)
        {
            if (string.Equals(upper, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(upper, display.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    private static bool IsLetterOrDigit(int key) => key is >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsFunctionKey(int key) => key is >= 0x70 and <= 0x87;

    private static bool IsNumberPad(int key) => key is >= 0x60 and <= 0x69;
}
