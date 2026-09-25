using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

/// <summary>
/// Centralized service for reading, saving, and dynamically applying UI languages
/// in real time without requiring application restart.
/// </summary>
public static class LanguageService
{
    public record LanguageItem(string Code, string DisplayName, string NativeName, string ResourceKey);

    public static readonly LanguageItem[] SupportedLanguages =
    [
        new("system", "System Default", "System Default", "Settings_SystemDefault"),
        new("en", "English", "English", "Settings_LanguageEnglish"),
        new("es", "Español (Spanish)", "Español", "Settings_LanguageSpanish"),
        new("pt-BR", "Português (Portuguese)", "Português", "Settings_LanguagePortuguese"),
        new("zh-CN", "简体中文 (Chinese)", "简体中文", "Settings_LanguageSimplifiedChinese"),
        new("ms", "Bahasa Melayu (Malay)", "Bahasa Melayu", "Settings_LanguageMalay"),
    ];

    public static event Action? OnLanguageChanged;

    public static string GetSettingsPath()
    {
        return Path.Combine(SteamDetector.GetConfigDir(), "settings.json");
    }

    public static string ReadLanguagePreference()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return "system";

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("language", out var prop))
                return prop.GetString() ?? "system";
        }
        catch { }
        return "system";
    }

    public static void SaveLanguagePreference(string code)
    {
        var path = GetSettingsPath();
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        JsonElement existing = default;
        if (File.Exists(path))
        {
            try
            {
                var oldJson = File.ReadAllText(path);
                using var oldDoc = JsonDocument.Parse(oldJson);
                existing = oldDoc.RootElement.Clone();
            }
            catch { }
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("language", code);

            if (existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (prop.NameEquals("language")) continue;
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>
    /// Applies the specified language culture immediately to the UI thread, default thread cultures,
    /// invalidates dynamic XAML localization bindings, and triggers application-wide refresh.
    /// </summary>
    public static void ApplyLanguage(string code, bool save = true)
    {
        try
        {
            CultureInfo culture;
            if (string.IsNullOrEmpty(code) || code.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                culture = CultureInfo.InstalledUICulture ?? CultureInfo.InvariantCulture;
            }
            else
            {
                culture = new CultureInfo(code);
            }

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            if (save)
            {
                SaveLanguagePreference(code);
            }

            // Invalidate dynamic XAML bindings
            LocalizationManager.Instance.Invalidate();

            // Trigger application-wide refresh
            OnLanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to apply language {code}: {ex}");
        }
    }
}
