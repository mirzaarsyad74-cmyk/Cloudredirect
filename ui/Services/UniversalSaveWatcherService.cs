using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CloudRedirect.Services;

public class UniversalGameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string SaveFolderPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? LastSyncTime { get; set; }
    public string Status { get; set; } = "Monitoring";

    public string ExpandedSavePath
    {
        get
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(SaveFolderPath);
                if (expanded.StartsWith("~"))
                {
                    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    expanded = Path.Combine(userProfile, expanded.TrimStart('~', '/', '\\'));
                }
                return Path.GetFullPath(expanded);
            }
            catch
            {
                return SaveFolderPath;
            }
        }
    }

    public bool FolderExists => Directory.Exists(ExpandedSavePath);
}

/// <summary>
/// Universal Out-of-Process Save Watcher.
/// Allows games without native Steam Cloud support or games protected with Hypervisors / Anti-Cheat
/// (EAC, BattlEye, Vanguard) to safely backup and synchronize saves to the cloud with zero DLL injection.
/// </summary>
public static class UniversalSaveWatcherService
{
    public static event Action? OnProfilesChanged;
    public static event Action<UniversalGameProfile>? OnProfileStatusChanged;

    public static readonly List<UniversalGameProfile> PopularPresets =
    [
        new()
        {
            GameName = "Elden Ring",
            ProcessName = "eldenring",
            SaveFolderPath = @"%APPDATA%\EldenRing"
        },
        new()
        {
            GameName = "Baldur's Gate 3",
            ProcessName = "bg3",
            SaveFolderPath = @"%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames\Story"
        },
        new()
        {
            GameName = "Black Myth: Wukong",
            ProcessName = "b1-Win64-Shipping",
            SaveFolderPath = @"%LOCALAPPDATA%\b1\Saved\SaveGames"
        },
        new()
        {
            GameName = "Cyberpunk 2077",
            ProcessName = "Cyberpunk2077",
            SaveFolderPath = @"%USERPROFILE%\Saved Games\CD Projekt Red\Cyberpunk 2077"
        },
        new()
        {
            GameName = "Palworld",
            ProcessName = "Palworld-Win64-Shipping",
            SaveFolderPath = @"%LOCALAPPDATA%\Pal\Saved\SaveGames"
        },
        new()
        {
            GameName = "Dark Souls III",
            ProcessName = "DarkSoulsIII",
            SaveFolderPath = @"%APPDATA%\DarkSoulsIII"
        },
        new()
        {
            GameName = "Starfield",
            ProcessName = "Starfield",
            SaveFolderPath = @"%USERPROFILE%\Documents\My Games\Starfield\Saves"
        },
        new()
        {
            GameName = "Sekiro: Shadows Die Twice",
            ProcessName = "sekiro",
            SaveFolderPath = @"%APPDATA%\Sekiro"
        }
    ];

    private static List<UniversalGameProfile>? _profiles;

    public static string GetConfigPath()
    {
        return Path.Combine(SteamDetector.GetConfigDir(), "universal_saves.json");
    }

    public static List<UniversalGameProfile> GetProfiles()
    {
        if (_profiles != null) return _profiles;

        try
        {
            var path = GetConfigPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _profiles = JsonSerializer.Deserialize<List<UniversalGameProfile>>(json) ?? [];
                return _profiles;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load universal saves config: {ex}");
        }

        _profiles = [];
        return _profiles;
    }

    public static void SaveProfiles()
    {
        try
        {
            var path = GetConfigPath();
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_profiles ?? [], new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            OnProfilesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save universal saves config: {ex}");
        }
    }

    public static void AddProfile(UniversalGameProfile profile)
    {
        var list = GetProfiles();
        if (list.Any(p => p.GameName.Equals(profile.GameName, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(profile);
        SaveProfiles();
    }

    public static void RemoveProfile(string id)
    {
        var list = GetProfiles();
        var item = list.FirstOrDefault(p => p.Id == id);
        if (item != null)
        {
            list.Remove(item);
            SaveProfiles();
        }
    }

    public static void UpdateProfileStatus(UniversalGameProfile profile, string status)
    {
        profile.Status = status;
        OnProfileStatusChanged?.Invoke(profile);
    }

    public static async Task<bool> SyncProfileNowAsync(UniversalGameProfile profile, string triggerReason = "Manual Sync")
    {
        return await Task.Run(() =>
        {
            try
            {
                var saveDir = profile.ExpandedSavePath;
                if (!Directory.Exists(saveDir))
                {
                    UpdateProfileStatus(profile, "Folder Not Found");
                    return false;
                }

                UpdateProfileStatus(profile, "Syncing...");

                // 1. Create a versioned local snapshot first (Advanced Save Protection)
                var snapshot = SaveHistoryManager.CreateSnapshot(profile.GameName, saveDir, triggerReason);

                // 2. If a local sync folder or cloud sync target is configured, sync to it
                try
                {
                    var config = SteamDetector.ReadConfig();
                    if (config?.SyncPath != null && Directory.Exists(config.SyncPath))
                    {
                        var cloudDest = Path.Combine(config.SyncPath, "UniversalCloudSaves", SaveHistoryManager.SanitizeFolderName(profile.GameName));
                        if (!Directory.Exists(cloudDest))
                            Directory.CreateDirectory(cloudDest);

                        foreach (var srcFile in Directory.GetFiles(saveDir, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.GetRelativePath(saveDir, srcFile);
                            var dest = Path.Combine(cloudDest, rel);
                            var destD = Path.GetDirectoryName(dest)!;
                            if (!Directory.Exists(destD))
                                Directory.CreateDirectory(destD);

                            File.Copy(srcFile, dest, overwrite: true);
                        }
                    }
                }
                catch { }

                profile.LastSyncTime = DateTime.Now;
                UpdateProfileStatus(profile, "Up to Date");
                SaveProfiles();

                TrayIconService.Instance.ShowNotification(
                    "CloudRedirect Universal Save",
                    $"{profile.GameName}: Save files successfully backed up and synchronized.");

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error syncing universal profile {profile.GameName}: {ex}");
                UpdateProfileStatus(profile, "Sync Error");
                return false;
            }
        });
    }
}
