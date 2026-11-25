using System.Collections.Frozen;

namespace Pablo_Switcher.Models;

public sealed record KeyStrokeEvent(
    char? Char,
    int VirtualKeyCode,
    long TimestampMs,
    bool ResetOnly = false);

public sealed record LastConversionRecord(
    string OriginalToken,
    string ConvertedToken,
    LangCode SourceLang,
    LangCode TargetLang,
    long TimestampMs);

/// <summary>
/// Стандартні коди віртуальних клавіш Windows (Win32 Virtual-Key Codes).
/// </summary>
public static class VirtualKeys
{
    public const int Backspace = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int CapsLock = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;

    public const int F6 = 0x75;
    public const int F12 = 0x7B;

    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LAlt = 0xA4;
    public const int RAlt = 0xA5;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;

    public static readonly FrozenSet<int> WordBreakKeys = new[]
    {
        Space, Enter, Tab, Escape, Left, Right, Up, Down
    }.ToFrozenSet();

    public static readonly FrozenSet<int> ShiftKeys = new[]
    {
        Shift, LShift, RShift
    }.ToFrozenSet();

    public static readonly FrozenSet<int> AltKeys = new[]
    {
        Alt, LAlt, RAlt
    }.ToFrozenSet();

    public static readonly FrozenSet<int> CtrlKeys = new[]
    {
        Control, LControl, RControl
    }.ToFrozenSet();

    public static readonly FrozenSet<int> WinKeys = new[]
    {
        LWin, RWin
    }.ToFrozenSet();

    public static readonly FrozenSet<int> ModifierKeys =
        AltKeys.Union(CtrlKeys).Union(WinKeys).ToFrozenSet();
}
