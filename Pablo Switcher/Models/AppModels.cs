using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pablo_Switcher.Models;

[JsonConverter(typeof(JsonStringEnumConverter<LangCode>))]
public enum LangCode
{
    [JsonStringEnumMemberName("en")]
    En,

    [JsonStringEnumMemberName("uk")]
    Uk
}

[JsonConverter(typeof(JsonStringEnumConverter<HotkeyPreset>))]
public enum HotkeyPreset
{
    [JsonStringEnumMemberName("double_shift")]
    DoubleShift,

    [JsonStringEnumMemberName("double_alt")]
    DoubleAlt,

    [JsonStringEnumMemberName("double_ctrl")]
    DoubleCtrl,

    [JsonStringEnumMemberName("double_win")]
    DoubleWin,

    [JsonStringEnumMemberName("f12")]
    KeyF12,

    [JsonStringEnumMemberName("f6")]
    KeyF6,

    [JsonStringEnumMemberName("caps_lock")]
    KeyCapsLock
}

public static class AppPaths
{
    public static string AppSupportDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pablo_Switcher");

    public static string ConfigPath { get; } = Path.Combine(AppSupportDir, "config.json");

    public static string CustomVocabPath { get; } = Path.Combine(AppSupportDir, "custom_vocab.json");

    public static string LocalesDir { get; } = Path.Combine(AppContext.BaseDirectory, "locales");

    public static LangCode DetectSystemLanguage()
    {
        string isoLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return isoLang.Equals("uk", StringComparison.OrdinalIgnoreCase)
            ? LangCode.Uk
            : LangCode.En;
    }
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [JsonPropertyName("ui_language")]
    public LangCode UiLanguage { get; set; } = AppPaths.DetectSystemLanguage();

    [JsonPropertyName("hotkey_preset")]
    public HotkeyPreset HotkeyPreset { get; set; } = HotkeyPreset.DoubleShift;

    [JsonPropertyName("auto_switch_enabled")]
    public bool AutoSwitchEnabled { get; set; } = true;

    public static AppSettings Load(string? path = null)
    {
        string targetPath = path ?? AppPaths.ConfigPath;

        if (!File.Exists(targetPath))
        {
            var defaultSettings = new AppSettings();
            defaultSettings.Save(targetPath);
            return defaultSettings;
        }

        try
        {
            string json = File.ReadAllText(targetPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        string targetPath = path ?? AppPaths.ConfigPath;

        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine;
            File.WriteAllText(targetPath, json);
        }
        catch (Exception)
        {
            // Ігноруємо помилки доступу до ФС, аналогічно contextlib.suppress(OSError)
        }
    }
}
