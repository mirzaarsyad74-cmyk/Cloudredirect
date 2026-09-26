using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CloudRedirect.Services;

/// <summary>
/// Manages application-level preferences such as Windows startup,
/// minimize-to-tray behavior, and sync notifications.
/// </summary>
public static class AppSettings
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CloudRedirect";

    public static string GetSettingsPath()
    {
        return Path.Combine(SteamDetector.GetConfigDir(), "settings.json");
    }

    public static bool StartWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key == null) return;

                if (value)
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\" -minimized");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update Run registry key: {ex}");
            }
        }
    }

    public static bool MinimizeToTrayOnClose
    {
        get => ReadBool("minimize_to_tray_on_close", true);
        set => WriteBool("minimize_to_tray_on_close", value);
    }

    public static bool ShowSyncNotifications
    {
        get => ReadBool("show_sync_notifications", true);
        set => WriteBool("show_sync_notifications", value);
    }

    public static bool AutoFitZoom
    {
        get => ReadBool("auto_fit_zoom", true);
        set => WriteBool("auto_fit_zoom", value);
    }

    public static double ZoomScale
    {
        get => ReadDouble("zoom_scale", 1.0);
        set => WriteDouble("zoom_scale", value);
    }

    private static bool ReadBool(string keyName, bool defaultValue)
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return defaultValue;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(keyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                    return prop.GetBoolean();
            }
        }
        catch { }
        return defaultValue;
    }

    private static void WriteBool(string keyName, bool value)
    {
        try
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
                    using var doc = JsonDocument.Parse(oldJson);
                    existing = doc.RootElement.Clone();
                }
                catch { }
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean(keyName, value);

                if (existing.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in existing.EnumerateObject())
                    {
                        if (prop.NameEquals(keyName)) continue;
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            File.WriteAllBytes(path, ms.ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write setting {keyName}: {ex}");
        }
    }

    private static double ReadDouble(string keyName, double defaultValue)
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return defaultValue;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(keyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
        }
        catch { }
        return defaultValue;
    }

    public static void WriteDouble(string keyName, double value)
    {
        try
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
                    using var doc = JsonDocument.Parse(oldJson);
                    existing = doc.RootElement.Clone();
                }
                catch { }
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber(keyName, value);

                if (existing.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in existing.EnumerateObject())
                    {
                        if (prop.NameEquals(keyName)) continue;
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            File.WriteAllBytes(path, ms.ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write setting {keyName}: {ex}");
        }
    }
}
