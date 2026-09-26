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
    DateTime StartTime
);

/// <summary>
/// Real-time Active Game Tracker.
/// Detects currently running Steam games and Universal Watcher games,
/// displays live status badges, and automatically triggers cloud save backups upon game exit.
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

                    _currentGame = new ActiveGameInfo(
                        runningAppId,
                        name,
                        headerUrl,
                        null,
                        false,
                        DateTime.Now
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
                                0,
                                profile.GameName,
                                null,
                                targetProcName,
                                true,
                                DateTime.Now
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

                // Auto-sync universal game if it just exited
                if (exitedGame.IsUniversal && _lastActiveUniversalProfile != null)
                {
                    var profileToSync = _lastActiveUniversalProfile;
                    _lastActiveUniversalProfile = null;
                    _lastMonitoredUniversalProcess = null;

                    _ = Task.Run(async () =>
                    {
                        await UniversalSaveWatcherService.SyncProfileNowAsync(profileToSync, "Auto-Backup on Game Exit");
                    });
                }
                else
                {
                    // Steam Game Exited: notify that saves are monitored
                    TrayIconService.Instance.ShowNotification(
                        "CloudRedirect",
                        $"{exitedGame.Name} closed. Cloud save redirection remains active.");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ActiveGameTracker error: {ex}");
        }
    }
}
