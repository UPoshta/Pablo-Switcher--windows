using Pablo_Switcher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace Pablo_Switcher.Services;

public sealed class XmlI18nManager
{
    private readonly string _localesDir;
    private readonly Dictionary<LangCode, Dictionary<string, string>> _translations = new();
    private LangCode _currentLang = LangCode.En;

    public event Action<LangCode>? LanguageChanged;

    public XmlI18nManager(string? localesDir = null)
    {
        _localesDir = localesDir ?? AppPaths.LocalesDir;
        LoadXmlFiles();
    }

    public LangCode Language
    {
        get => _currentLang;
        set
        {
            if (_translations.ContainsKey(value) && _currentLang != value)
            {
                _currentLang = value;
                LanguageChanged?.Invoke(value);
            }
        }
    }

    private void LoadXmlFiles()
    {
        _translations[LangCode.En] = ParseXmlFile(Path.Combine(_localesDir, "en.xml"));
        _translations[LangCode.Uk] = ParseXmlFile(Path.Combine(_localesDir, "uk.xml"));
    }

    private static Dictionary<string, string> ParseXmlFile(string xmlPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(xmlPath))
            return result;

        try
        {
            XDocument doc = XDocument.Load(xmlPath);
            if (doc.Root is null)
                return result;

            foreach (XElement node in doc.Root.Elements("string"))
            {
                string? key = node.Attribute("key")?.Value;
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = node.Value;
                }
            }
        }
        catch (Exception)
        {
            // Повертаємо те, що встигли прочитати, або порожній словник
        }

        return result;
    }

    /// <summary>
    /// Повертає переклад за ключем з підстановкою параметрів формату {name}.
    /// Приклад: i18n.T("greeting", ("name", "Сергій"))
    /// </summary>
    public string T(string key, params (string Key, object? Value)[] args)
    {
        string template = GetRawTemplate(key);

        if (args.Length == 0)
            return template;

        string formatted = template;
        foreach (var (argKey, argValue) in args)
        {
            formatted = formatted.Replace($"{{{argKey}}}", argValue?.ToString() ?? string.Empty);
        }

        return formatted;
    }

    public string HotkeyLabel(HotkeyPreset preset)
    {
        string suffix = preset switch
        {
            HotkeyPreset.DoubleShift => "double_shift",
            HotkeyPreset.DoubleAlt => "double_alt",
            HotkeyPreset.DoubleCtrl => "double_ctrl",
            HotkeyPreset.DoubleWin => "double_win",
            HotkeyPreset.KeyF12 => "f12",
            HotkeyPreset.KeyF6 => "f6",
            HotkeyPreset.KeyCapsLock => "caps_lock",
            _ => "double_shift"
        };

        return T($"hotkey_{suffix}");
    }

    private string GetRawTemplate(string key)
    {
        if (_translations.TryGetValue(_currentLang, out var langTable) &&
            langTable.TryGetValue(key, out string? value) &&
            !string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (_translations.TryGetValue(LangCode.En, out var fallbackTable) &&
            fallbackTable.TryGetValue(key, out string? fallbackValue) &&
            !string.IsNullOrEmpty(fallbackValue))
        {
            return fallbackValue;
        }

        return key;
    }
}
