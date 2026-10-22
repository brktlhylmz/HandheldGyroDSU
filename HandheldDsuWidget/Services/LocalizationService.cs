using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HandheldDsuWidget.Services;

public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// Dynamic JSON/ARB-style localization provider that loads embedded Locales/*.json resources
/// (for standalone single-file .exe execution) and merges any external Locales/*.json files from disk.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _locales = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageOption> _availableLanguages = [];
    private string _currentLanguage = "en";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public IReadOnlyList<LanguageOption> AvailableLanguages => _availableLanguages;
    public string CurrentLanguage => _currentLanguage;

    private LocalizationService()
    {
        ReloadLocales();
    }

    public void ReloadLocales()
    {
        _locales.Clear();
        _availableLanguages.Clear();

        // 1. Load embedded locale JSON resources (works when running as a standalone single-file .exe)
        LoadEmbeddedLocales();

        // 2. Load/override from external Locales/*.json directory if present on disk
        string? localesDir = ResolveLocalesDirectory();
        if (localesDir is not null && Directory.Exists(localesDir))
        {
            foreach (string filePath in Directory.GetFiles(localesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                TryLoadLocaleFile(filePath);
            }
        }

        if (!_locales.ContainsKey(_currentLanguage) && _locales.Count > 0)
        {
            _currentLanguage = _locales.ContainsKey("en") ? "en" : _locales.Keys.First();
        }
    }

    private void LoadEmbeddedLocales()
    {
        try
        {
            Assembly asm = typeof(LocalizationService).Assembly;
            foreach (string resName in asm.GetManifestResourceNames()
                         .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                using Stream? stream = asm.GetManifestResourceStream(resName);
                if (stream is null)
                    continue;

                using StreamReader reader = new(stream);
                string json = reader.ReadToEnd();
                // Extract fallback code from resource name (e.g., "HandheldDsuWidget.Locales.en.json" -> "en")
                string withoutExt = resName[..^5];
                int lastDot = withoutExt.LastIndexOf('.');
                string fallbackCode = lastDot >= 0 ? withoutExt[(lastDot + 1)..] : withoutExt;
                TryLoadLocaleJson(json, fallbackCode);
            }
        }
        catch
        {
            // Ignore embedded resource enumeration failures
        }
    }

    private static string? ResolveLocalesDirectory()
    {
        string baseDirLocales = Path.Combine(AppContext.BaseDirectory, "Locales");
        if (Directory.Exists(baseDirLocales))
            return baseDirLocales;

        // Development fallback: walk up parent directories to locate Locales folder
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "Locales");
            if (Directory.Exists(candidate))
                return candidate;

            string widgetCandidate = Path.Combine(dir.FullName, "HandheldDsuWidget", "Locales");
            if (Directory.Exists(widgetCandidate))
                return widgetCandidate;

            dir = dir.Parent;
        }

        return null;
    }

    private void TryLoadLocaleFile(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath);
            string fallbackCode = Path.GetFileNameWithoutExtension(filePath);
            TryLoadLocaleJson(json, fallbackCode);
        }
        catch
        {
            // Ignore malformed locale JSON files
        }
    }

    private void TryLoadLocaleJson(string json, string fallbackCode)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }

            string code = dict.TryGetValue("@@locale", out string? metaLocale) && !string.IsNullOrWhiteSpace(metaLocale)
                ? metaLocale.Trim().ToLowerInvariant()
                : fallbackCode.ToLowerInvariant();

            string displayName = dict.TryGetValue("@@languageName", out string? metaName) && !string.IsNullOrWhiteSpace(metaName)
                ? metaName.Trim()
                : code.ToUpperInvariant();

            _locales[code] = dict;

            int existingIdx = _availableLanguages.FindIndex(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                _availableLanguages[existingIdx] = new LanguageOption(code, displayName);
            }
            else
            {
                _availableLanguages.Add(new LanguageOption(code, displayName));
            }
        }
        catch
        {
            // Ignore malformed locale JSON content
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (_locales.TryGetValue(_currentLanguage, out var activeDict) &&
            activeDict.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (_locales.TryGetValue("en", out var enDict) &&
            enDict.TryGetValue(key, out string? fallback))
        {
            return fallback;
        }

        return key;
    }

    public string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, Get(key), args);
        }
        catch
        {
            return Get(key);
        }
    }

    public bool SetLanguage(string? languageCode)
    {
        string normalized = (languageCode ?? "en").Trim().ToLowerInvariant();
        if (!_locales.ContainsKey(normalized))
        {
            // Match prefix (e.g., "en-US" -> "en")
            string? prefixMatch = _locales.Keys.FirstOrDefault(k => normalized.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            normalized = prefixMatch ?? (_locales.ContainsKey("en") ? "en" : _locales.Keys.FirstOrDefault() ?? "en");
        }

        if (string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        _currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
