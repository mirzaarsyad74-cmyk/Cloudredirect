using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CloudRedirect.Services;

public record ActiveGameInfo(
    uint AppId,
    string Name,
    string? HeaderUrl,
    string? ProcessName,
    bool IsUniversal,
    DateTime StartTime,
    bool IsLuaGame = false,
    bool HasSteamCloud = true,
    bool IsGenuineOwned = false,
    bool HasAntiCheat = false,
    UniversalGameProfile? UniversalProfile = null
);

/// <summary>
/// Real-time Active Game Tracker.
/// Distinguishes between:
/// 1. Lua Games (redirected by CloudRedirect DLL).
/// 2. Genuine Steam Games with Steam Cloud (uses original native Steam Cloud untouched).
/// 3. Genuine Steam Games without Steam Cloud (auto-protected by CloudRedirect Universal Saves).
/// 4. Non-Steam / Anti-Cheat / Bypass Games (monitored by Universal Safe Mode).
/// </summary>
public static class ActiveGameTrackerService
{
    private static Timer? _pollTimer;
    private static ActiveGameInfo? _currentGame;
    private static string? _lastMonitoredUniversalProcess;
    private static UniversalGameProfile? _lastActiveUniversalProfile;

    public static ActiveGameInfo? CurrentGame => _currentGame;
    public static event Action<ActiveGameInfo?>? OnActiveGameChanged;

    public static void Start()
    {
        if (_pollTimer != null) return;
        _pollTimer = new Timer(async _ => await CheckActiveGamesAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
    }

    public static void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private static async Task CheckActiveGamesAsync()
    {
        try
        {
            // 1. Check Steam's native RunningAppId in registry
            uint runningAppId = 0;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
                var val = key?.GetValue("RunningAppId");
                if (val is int intVal && intVal > 0)
                {
                    runningAppId = (uint)intVal;
                }
                else if (val is long longVal && longVal > 0)
                {
                    runningAppId = (uint)longVal;
                }
            }
            catch { }

            if (runningAppId > 0)
            {
                if (_currentGame == null || _currentGame.AppId != runningAppId)
                {
                    string name = $"Steam App {runningAppId}";
                    string? headerUrl = null;

                    try
                    {
                        var appMap = await SteamStoreClient.Shared.GetAppInfoAsync(new[] { runningAppId });
                        if (appMap.TryGetValue(runningAppId, out var storeInfo))
                        {
                            if (!string.IsNullOrEmpty(storeInfo.Name))
                                name = storeInfo.Name;
                            headerUrl = storeInfo.HeaderUrl;
                        }
                    }
                    catch { }

                    // Classify the game:
                    bool isLuaGame = SteamDetector.IsLuaGame(runningAppId);
                    bool hasCloud = AppInfoParser.HasCloudSave(runningAppId);
                    bool isGenuine = !isLuaGame;

                    string? procName = null;
                    string? installDir = null;
                    try
                    {
                        var steamPath = SteamDetector.FindSteamPath();
                        if (steamPath != null)
                        {
                            installDir = AppCloudConfig.FindGameInstallDir(steamPath, runningAppId);
                        }

                        var procs = Process.GetProcesses();
                        foreach (var p in procs)
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(installDir) && p.MainModule?.FileName.StartsWith(installDir, StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    procName = p.ProcessName;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    bool hasAntiCheat = GameSaveAutoDetector.HasAntiCheatOrHypervisor(installDir, procName);

                    UniversalGameProfile? universalProfile = null;

                    // Evaluate:
                    // Genuine game without cloud saves -> auto-protect via Universal Cloud Saves
                    if (isGenuine && !hasCloud && AppSettings.AutoProtectNonCloudGames)
                    {
                        universalProfile = UniversalSaveWatcherService.FindProfile(runningAppId, procName, name);
                        if (universalProfile == null)
                        {
                            var saveFolder = GameSaveAutoDetector.DetectSaveFolder(name, procName, runningAppId);
                            if (saveFolder != null)
                            {
                                universalProfile = UniversalSaveWatcherService.AutoEnrollIfNeeded(
                                    name, procName, runningAppId, saveFolder, hasAntiCheat, isGenuine: true);

                                if (universalProfile != null && AppSettings.ShowSyncNotifications)
                                {
                                    TrayIconService.Instance.ShowNotification(
                                        "CloudRedirect Auto-Protection",
                                        $"{name} does not have Steam Cloud. Save folder is now automatically protected!");
                                }
                            }
                        }
                    }
                    // Lua game without cloud saves or with anti-cheat -> also link to Universal profile if available
                    else if (isLuaGame && (!hasCloud || hasAntiCheat) && AppSettings.AutoProtectNonCloudGames)
                    {
                        universalProfile = UniversalSaveWatcherService.FindProfile(runningAppId, procName, name);
                        if (universalProfile == null)
                        {
                            var saveFolder = GameSaveAutoDetector.DetectSaveFolder(name, procName, runningAppId);
                            if (saveFolder != null)
                            {
                                universalProfile = UniversalSaveWatcherService.AutoEnrollIfNeeded(
                                    name, procName, runningAppId, saveFolder, hasAntiCheat, isGenuine: false);
                            }
                        }
                    }

                    if (universalProfile != null)
                    {
                        _lastActiveUniversalProfile = universalProfile;
                        _lastMonitoredUniversalProcess = procName ?? universalProfile.ProcessName;
                        UniversalSaveWatcherService.UpdateProfileStatus(universalProfile, "Game Running 🎮");
                    }

                    _currentGame = new ActiveGameInfo(
                        runningAppId,
                        name,
                        headerUrl,
                        procName,
                        universalProfile != null,
                        DateTime.Now,
                        IsLuaGame: isLuaGame,
                        HasSteamCloud: hasCloud,
                        IsGenuineOwned: isGenuine,
                        HasAntiCheat: hasAntiCheat,
                        UniversalProfile: universalProfile
                    );

                    OnActiveGameChanged?.Invoke(_currentGame);
                }
                return;
            }

            // 2. If no Steam game is flagged in registry, check user-configured Universal Save Watcher games
            var universalProfiles = UniversalSaveWatcherService.GetProfiles().Where(p => p.Enabled).ToList();
            if (universalProfiles.Count > 0)
            {
                var processes = Process.GetProcesses();
                foreach (var profile in universalProfiles)
                {
                    if (string.IsNullOrWhiteSpace(profile.ProcessName)) continue;
                    if (GameSaveAutoDetector.IsSystemProcess(profile.ProcessName)) continue;

                    var targetProcName = profile.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(profile.ProcessName)
                        : profile.ProcessName;

                    var isRunning = processes.Any(p =>
                    {
                        try { return p.ProcessName.Equals(targetProcName, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    });

                    if (isRunning)
                    {
                        if (_currentGame == null || _currentGame.ProcessName != targetProcName)
                        {
                            _currentGame = new ActiveGameInfo(
                                profile.SteamAppId,
                                profile.GameName,
                                null,
                                targetProcName,
                                true,
                                DateTime.Now,
                                IsLuaGame: false,
                                HasSteamCloud: false,
                                IsGenuineOwned: profile.IsGenuineSteamGame,
                                HasAntiCheat: profile.HasAntiCheat,
                                UniversalProfile: profile
                            );
                            _lastMonitoredUniversalProcess = targetProcName;
                            _lastActiveUniversalProfile = profile;

                            UniversalSaveWatcherService.UpdateProfileStatus(profile, "Game Running 🎮");
                            OnActiveGameChanged?.Invoke(_currentGame);
                        }
                        return;
                    }
                }
            }

            // 3. If a game was active and now stopped:
            if (_currentGame != null)
            {
                var exitedGame = _currentGame;
                _currentGame = null;
                OnActiveGameChanged?.Invoke(null);

                var gameName = exitedGame.Name;
                var appId = exitedGame.AppId;
                var procName = exitedGame.ProcessName;
                var universalProfile = exitedGame.UniversalProfile;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (universalProfile != null)
                        {
                            _lastActiveUniversalProfile = null;
                            _lastMonitoredUniversalProcess = null;
                            await UniversalSaveWatcherService.SyncProfileNowAsync(universalProfile, "Auto-Backup on Game Exit");
                        }
                        else
                        {
                            // Wait briefly for game process / Steam cloud to finish flushing saves
                            await Task.Delay(2000);

                            var steamPath = SteamDetector.FindSteamPath();
                            string? saveDir = null;
                            if (appId > 0)
                            {
                                saveDir = SaveHistoryManager.FindAppStorageDir(steamPath, appId);
                            }

                            if (saveDir == null)
                            {
                                saveDir = GameSaveAutoDetector.DetectSaveFolder(gameName, procName, appId);
                            }

                            if (saveDir != null && Directory.Exists(saveDir))
                            {
                                var snapshot = SaveHistoryManager.CreateSnapshot(
                                    gameName,
                                    saveDir,
                                    "Auto-Backup on Game Exit",
                                    appId > 0 ? appId.ToString() : null);

                                if (snapshot != null && AppSettings.ShowSyncNotifications)
                                {
                                    TrayIconService.Instance.ShowNotification(
                                        "Save Protection",
                                        $"{gameName}: Save snapshot created upon game exit ({snapshot.FileCount} file(s), {snapshot.FormattedSize}).");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to auto-snapshot on exit for {gameName}: {ex}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ActiveGameTracker error: {ex}");
        }
    }
}
