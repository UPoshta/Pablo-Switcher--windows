using System.Collections.Frozen;
using System.Collections.Generic;

namespace Pablo_Switcher.Models;

public sealed class LanguageProfile
{
    public LangCode Code { get; }
    public string CodeString { get; } // "en", "uk"
    public string Label { get; }

    // Коди мов Windows (LANGID): 0x0409 (US), 0x0422 (UK)
    public ushort WindowsLangId { get; }
    public string LayoutChars { get; }
    public FrozenSet<char> Charset { get; }

    // Словник частотності повних слів (замість виклику zipf_frequency)
    public FrozenDictionary<string, float> WordScores { get; }

    // Словник частотності префіксів
    public FrozenDictionary<string, float> PrefixScores { get; }

    public LanguageProfile(
        LangCode code,
        string codeString,
        string label,
        ushort windowsLangId,
        string layoutChars,
        Dictionary<string, float> wordScores,
        Dictionary<string, float> prefixScores)
    {
        Code = code;
        CodeString = codeString;
        Label = label;
        WindowsLangId = windowsLangId;
        LayoutChars = layoutChars;
        Charset = layoutChars.ToFrozenSet();
        WordScores = wordScores.ToFrozenDictionary();
        PrefixScores = prefixScores.ToFrozenDictionary();
    }
}
