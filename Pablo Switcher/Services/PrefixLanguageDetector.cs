using Pablo_Switcher.Models;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pablo_Switcher.Services;

public sealed record SwitchEvaluationResult(LangCode SourceLang, LangCode TargetLang, string ConvertedText);
public sealed class PrefixLanguageDetector
{
    public const string EnLayout =
        "`qwertyuiop[]asdfghjkl;'zxcvbnm,./" +
        "~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?~";

    public const string UkLayout =
        "'йцукенгшщзхїфівапролджєячсмитьбю." +
        "ʼЙЦУКЕНГШЩЗХЇФІВАПРОЛДЖЄЯЧСМИТЬБЮ,₴";

    public const float SourceKeepThreshold = 4.2f;
    public const float TargetMinThreshold = 3.8f;
    public const float MinScoreDelta = 2.2f;
    public const float DefaultCustomWordScore = 6.5f;

    private static readonly byte[] ExpectedMagic = "ASVB"u8.ToArray();

    public int MinPrefixLen { get; }

    private readonly Dictionary<LangCode, LanguageProfile> _profiles = new();
    private readonly Dictionary<(LangCode Source, LangCode Target), FrozenDictionary<char, char>> _conversionMaps = new();

    public PrefixLanguageDetector(
        string vocabBinPath,
        string? customVocabPath = null,
        int minPrefixLen = 3)
    {
        MinPrefixLen = minPrefixLen;
        BootstrapDefaultLanguages(vocabBinPath, customVocabPath);
    }

    private void BootstrapDefaultLanguages(string vocabBinPath, string? customVocabPath)
    {
        var rawVocab = LoadVocabFromBinary(vocabBinPath);

        if (!string.IsNullOrWhiteSpace(customVocabPath))
        {
            MergeCustomVocab(rawVocab, customVocabPath);
        }

        RegisterLanguage(
            code: LangCode.En,
            codeString: "en",
            label: "English",
            windowsLangId: 0x0409,
            layoutChars: EnLayout,
            wordScores: rawVocab.GetValueOrDefault("en") ?? new Dictionary<string, float>()
        );

        RegisterLanguage(
            code: LangCode.Uk,
            codeString: "uk",
            label: "Українська",
            windowsLangId: 0x0422,
            layoutChars: UkLayout,
            wordScores: rawVocab.GetValueOrDefault("uk") ?? new Dictionary<string, float>()
        );
    }
    private static void MergeCustomVocab(
        Dictionary<string, Dictionary<string, float>> targetVocab,
        string customPath)
    {
        if (!File.Exists(customPath))
        {
            CreateDefaultCustomVocabTemplate(customPath);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(customPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            int addedCount = 0;

            foreach (JsonProperty langProp in doc.RootElement.EnumerateObject())
            {
                if (!targetVocab.TryGetValue(langProp.Name, out var langDict))
                {
                    langDict = new Dictionary<string, float>(StringComparer.Ordinal);
                    targetVocab[langProp.Name] = langDict;
                }

                // Варіант 1: масив слів ["git", "dotnet", "деплой"]
                if (langProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in langProp.Value.EnumerateArray())
                    {
                        string? raw = item.GetString();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            string clean = raw.Trim().ToLowerInvariant().Replace('ʼ', '\'');
                            langDict[clean] = DefaultCustomWordScore;
                            addedCount++;
                        }
                    }
                }
                // Варіант 2: об'єкт із явними вагами {"git": 6.8}
                else if (langProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty wordProp in langProp.Value.EnumerateObject())
                    {
                        if (wordProp.Value.ValueKind == JsonValueKind.Number)
                        {
                            string clean = wordProp.Name.Trim().ToLowerInvariant().Replace('ʼ', '\'');
                            langDict[clean] = wordProp.Value.GetSingle();
                            addedCount++;
                        }
                    }
                }
            }

            TraceLogger.Log("INIT", $"Завантажено {addedCount} слів із custom_vocab.json");
        }
        catch (Exception ex)
        {
            TraceLogger.Log("ERROR", $"Помилка читання custom_vocab.json: {ex.Message}");
        }
    }
    private static void CreateDefaultCustomVocabTemplate(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var template = new Dictionary<string, string[]>
            {
                ["en"] = ["git", "dotnet", "docker", "async"],
                ["uk"] = ["деплой", "фіча", "пушити", "коміт"]
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            File.WriteAllText(path, JsonSerializer.Serialize(template, options) + Environment.NewLine, Encoding.UTF8);
            TraceLogger.Log("INIT", $"Створено шаблон користувацького словника: {path}");
        }
        catch
        {
            // Ігноруємо, якщо немає прав запису
        }
    }

    private static Dictionary<string, Dictionary<string, float>> LoadVocabFromBinary(string path)
    {
        var result = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            TraceLogger.Log("ERROR", $"Бінарний словник не знайдено: {path}");
            return result;
        }

        var sw = Stopwatch.StartNew();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        using var reader = new BinaryReader(fs, Encoding.UTF8);

        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(ExpectedMagic))
        {
            TraceLogger.Log("ERROR", "Невірний заголовок файлу vocab_data.bin");
            return result;
        }

        ushort version = reader.ReadUInt16();
        if (version != 1)
        {
            TraceLogger.Log("ERROR", $"Непідтримувана версія бінарного словника: {version}");
            return result;
        }

        byte langCount = reader.ReadByte();
        for (int i = 0; i < langCount; i++)
        {
            string langCode = reader.ReadString();
            int wordCount = reader.ReadInt32();

            // Виділяємо точний розмір одразу, без рехешування
            var wordScores = new Dictionary<string, float>(wordCount, StringComparer.Ordinal);
            for (int w = 0; w < wordCount; w++)
            {
                string word = reader.ReadString();
                float score = reader.ReadSingle();
                wordScores[word] = score;
            }

            result[langCode] = wordScores;
        }

        sw.Stop();
        TraceLogger.Log(
            "INIT",
            $"Бінарний словник завантажено за {sw.ElapsedMilliseconds} мс (EN={result.GetValueOrDefault("en")?.Count ?? 0}, UK={result.GetValueOrDefault("uk")?.Count ?? 0})");

        return result;
    }

    public void RegisterLanguage(
        LangCode code,
        string codeString,
        string label,
        ushort windowsLangId,
        string layoutChars,
        Dictionary<string, float> wordScores)
    {
        var prefixScores = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var (word, score) in wordScores)
        {
            if (word.Length >= MinPrefixLen)
            {
                string prefix = word[..MinPrefixLen];
                // Беремо максимальний бал, щоб користувацькі слова перекривали рідкісні префікси з бінарника
                if (!prefixScores.TryGetValue(prefix, out float existing) || score > existing)
                {
                    prefixScores[prefix] = score;
                }
            }
        }

        var profile = new LanguageProfile(
            code, codeString, label, windowsLangId, layoutChars, wordScores, prefixScores);

        _profiles[code] = profile;

        foreach (var (otherCode, otherProfile) in _profiles)
        {
            if (otherCode == code)
                continue;

            _conversionMaps[(code, otherCode)] = BuildTranslationMap(profile.LayoutChars, otherProfile.LayoutChars);
            _conversionMaps[(otherCode, code)] = BuildTranslationMap(otherProfile.LayoutChars, profile.LayoutChars);
        }
    }

    private static FrozenDictionary<char, char> BuildTranslationMap(string fromChars, string toChars)
    {
        var map = new Dictionary<char, char>(fromChars.Length);
        int len = Math.Min(fromChars.Length, toChars.Length);

        for (int i = 0; i < len; i++)
        {
            // TryAdd гарантує, що перший мапінг (наприклад '~' -> 'ʼ') має пріоритет
            map.TryAdd(fromChars[i], toChars[i]);
        }

        return map.ToFrozenDictionary();
    }

    public LangCode? DetectSourceLanguage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var (code, profile) in _profiles)
        {
            if (text.All(ch => profile.Charset.Contains(ch)))
                return code;
        }

        var counts = _profiles.Keys.ToDictionary(k => k, _ => 0);

        foreach (char ch in text)
        {
            if (ch is '.' or ',' or '\'' or '"' or ' ')
                continue;

            foreach (var (code, profile) in _profiles)
            {
                if (profile.Charset.Contains(ch))
                    counts[code]++;
            }
        }

        var best = counts.MaxBy(kv => kv.Value);
        return best.Value > 0 ? best.Key : null;
    }

    public SwitchEvaluationResult? ForceTransliterate(string text, LangCode? preferredSource = null)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        LangCode? sourceLang = preferredSource ?? DetectSourceLanguage(text);
        if (sourceLang is null)
            return null;

        foreach (var targetLang in _profiles.Keys)
        {
            if (targetLang == sourceLang.Value)
                continue;

            if (_conversionMaps.TryGetValue((sourceLang.Value, targetLang), out var transMap))
            {
                return new SwitchEvaluationResult(
                    sourceLang.Value,
                    targetLang,
                    TranslateText(text, transMap));
            }
        }

        return null;
    }

    private float ScoreToken(string token, LanguageProfile profile)
    {
        // Для звичайних слів виділяємо буфер на стеку без алокацій у Heap
        Span<char> lowerBuffer = token.Length <= 64
            ? stackalloc char[token.Length]
            : new char[token.Length];

        token.AsSpan().ToLowerInvariant(lowerBuffer);

        var wordLookup = profile.WordScores.GetAlternateLookup<ReadOnlySpan<char>>();
        var prefixLookup = profile.PrefixScores.GetAlternateLookup<ReadOnlySpan<char>>();

        float wordScore = wordLookup.TryGetValue(lowerBuffer, out float ws) ? ws : 0.0f;
        float prefixScore = prefixLookup.TryGetValue(lowerBuffer[..MinPrefixLen], out float ps) ? ps : 0.0f;

        return Math.Max(prefixScore, wordScore);
    }

    public SwitchEvaluationResult? EvaluateSwitch(string token)
    {
        if (token.Length < MinPrefixLen)
        {
            TraceLogger.Log("EVAL", $"'{token}' -> занадто коротке ({token.Length} < {MinPrefixLen})");
            return null;
        }

        LangCode? sourceLang = DetectSourceLanguage(token);
        if (sourceLang is null)
        {
            TraceLogger.Log("EVAL", $"'{token}' -> мову розкладки не визначено");
            return null;
        }

        var sourceProfile = _profiles[sourceLang.Value];
        float sourceScore = ScoreToken(token, sourceProfile);

        if (sourceScore >= SourceKeepThreshold)
        {
            TraceLogger.Log("EVAL", $"'{token}' [{sourceLang}] -> залишено (score={sourceScore:F2} >= {SourceKeepThreshold:F2})");
            return null;
        }

        LangCode? bestTarget = null;
        string bestConverted = string.Empty;
        float bestScore = 0.0f;

        foreach (var (targetLang, targetProfile) in _profiles)
        {
            if (targetLang == sourceLang.Value)
                continue;

            if (!_conversionMaps.TryGetValue((sourceLang.Value, targetLang), out var transMap))
                continue;

            string convertedToken = TranslateText(token, transMap);
            float targetScore = ScoreToken(convertedToken, targetProfile);

            if (targetScore > bestScore)
            {
                bestScore = targetScore;
                bestTarget = targetLang;
                bestConverted = convertedToken;
            }
        }

        float delta = bestScore - sourceScore;
        if (bestTarget is not null &&
            bestScore >= TargetMinThreshold &&
            delta >= MinScoreDelta)
        {
            TraceLogger.Log("DECIDE", $"'{token}' -> '{bestConverted}' ({sourceLang}->{bestTarget}, src={sourceScore:F2}, dst={bestScore:F2}, Δ={delta:F2})");
            return new SwitchEvaluationResult(sourceLang.Value, bestTarget.Value, bestConverted);
        }

        TraceLogger.Log("EVAL", $"'{token}' -> не пройшло поріг (best='{bestConverted}', src={sourceScore:F2}, dst={bestScore:F2}, Δ={delta:F2})");
        return null;
    }

    private static string TranslateText(string input, FrozenDictionary<char, char> map)
    {
        return string.Create(input.Length, (input, map), static (span, state) =>
        {
            for (int i = 0; i < state.input.Length; i++)
            {
                char ch = state.input[i];
                span[i] = state.map.TryGetValue(ch, out char mapped) ? mapped : ch;
            }
        });
    }
}
