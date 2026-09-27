namespace Reach.Agent.Input;

/// <summary>Key names allowed in key.combo (spec §4) → Windows virtual-key codes.</summary>
public static class KeyMap
{
    public const ushort Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B;
    public const ushort Backspace = 0x08, Enter = 0x0D;

    private static readonly Dictionary<string, ushort> Keys = Build();

    // Keys that need KEYEVENTF_EXTENDEDKEY so apps don't read them as numpad keys.
    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2E, Win];

    public static bool TryGet(string name, out ushort vk) => Keys.TryGetValue(name, out vk);

    public static bool IsExtended(ushort vk) => Extended.Contains(vk);

    private static Dictionary<string, ushort> Build()
    {
        var keys = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["ctrl"] = Ctrl, ["alt"] = Alt, ["shift"] = Shift, ["win"] = Win,
            ["esc"] = 0x1B, ["tab"] = 0x09, ["enter"] = Enter, ["backspace"] = Backspace, ["delete"] = 0x2E,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["space"] = 0x20,
        };
        for (var i = 1; i <= 12; i++) keys[$"f{i}"] = (ushort)(0x70 + i - 1);
        for (var c = 'a'; c <= 'z'; c++) keys[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (var c = '0'; c <= '9'; c++) keys[c.ToString()] = c;
        return keys;
    }
}
