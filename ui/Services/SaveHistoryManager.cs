using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloudRedirect.Services;

public record SnapshotInfo(
    string Id,
    DateTime Timestamp,
    string Trigger,
    int FileCount,
    long TotalBytes,
    string DirectoryPath
)
{
    public string FormattedTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string FormattedSize
    {
        get
        {
            if (TotalBytes < 1024) return $"{TotalBytes} B";
            if (TotalBytes < 1024 * 1024) return $"{TotalBytes / 1024.0:F1} KB";
            return $"{TotalBytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}

/// <summary>
/// Advanced Save Protection: creates immutable, timestamped save snapshots,
/// allows 1-click rollback, and safely protects against save corruption or accidental overwrites.
/// </summary>
public static class SaveHistoryManager
{
    private const int MaxSnapshotsPerGame = 15;

    public static string GetSnapshotsBaseDir()
    {
        var dir = Path.Combine(SteamDetector.GetConfigDir(), "save_snapshots");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SanitizeFolderName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c => invalids.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(sanitized) ? "unknown_game" : sanitized;
    }

    public static SnapshotInfo? CreateSnapshot(string gameIdentifier, string sourceDirectory, string triggerDescription)
    {
        try
        {
            if (!Directory.Exists(sourceDirectory)) return null;

            var safeName = SanitizeFolderName(gameIdentifier);
            var gameSnapshotDir = Path.Combine(GetSnapshotsBaseDir(), safeName);
            if (!Directory.Exists(gameSnapshotDir))
                Directory.CreateDirectory(gameSnapshotDir);

            var now = DateTime.Now;
            var timestampStr = now.ToString("yyyyMMdd_HHmmss");
            var targetDir = Path.Combine(gameSnapshotDir, timestampStr);

            var sourceFiles = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            if (sourceFiles.Length == 0) return null;

            Directory.CreateDirectory(targetDir);
            long totalBytes = 0;

            foreach (var file in sourceFiles)
            {
                var relPath = Path.GetRelativePath(sourceDirectory, file);
                var destPath = Path.Combine(targetDir, relPath);
                var destDir = Path.GetDirectoryName(destPath)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                File.Copy(file, destPath, overwrite: true);
                totalBytes += new FileInfo(file).Length;
            }

            var meta = new
            {
                timestamp = now.ToString("o"),
                trigger = triggerDescription,
                fileCount = sourceFiles.Length,
                totalBytes
            };

            var metaPath = Path.Combine(targetDir, "snapshot.json");
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

            // Cleanup oldest if exceeds maximum
            PurgeOldSnapshots(gameSnapshotDir);

            return new SnapshotInfo(timestampStr, now, triggerDescription, sourceFiles.Length, totalBytes, targetDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create snapshot for {gameIdentifier}: {ex}");
            return null;
        }
    }

    public static List<SnapshotInfo> GetSnapshots(string gameIdentifier)
    {
        var result = new List<SnapshotInfo>();
        try
        {
            var safeName = SanitizeFolderName(gameIdentifier);
            var gameSnapshotDir = Path.Combine(GetSnapshotsBaseDir(), safeName);
            if (!Directory.Exists(gameSnapshotDir))
                return result;

            var dirs = Directory.GetDirectories(gameSnapshotDir);
            foreach (var dir in dirs)
            {
                var folderName = Path.GetFileName(dir);
                var metaPath = Path.Combine(dir, "snapshot.json");

                DateTime timestamp = Directory.GetCreationTime(dir);
                string trigger = "Save Backup";
                int fileCount = 0;
                long totalBytes = 0;

                if (File.Exists(metaPath))
                {
                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsProp) &&
                            DateTime.TryParse(tsProp.GetString(), out var parsedTs))
                        {
                            timestamp = parsedTs;
                        }
                        if (doc.RootElement.TryGetProperty("trigger", out var trigProp))
                            trigger = trigProp.GetString() ?? trigger;
                        if (doc.RootElement.TryGetProperty("fileCount", out var fcProp))
                            fileCount = fcProp.GetInt32();
                        if (doc.RootElement.TryGetProperty("totalBytes", out var tbProp))
                            totalBytes = tbProp.GetInt64();
                    }
                    catch { }
                }

                if (fileCount == 0)
                {
                    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).Equals("snapshot.json", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    fileCount = files.Length;
                    totalBytes = files.Sum(f => new FileInfo(f).Length);
                }

                result.Add(new SnapshotInfo(folderName, timestamp, trigger, fileCount, totalBytes, dir));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get snapshots: {ex}");
        }

        return result.OrderByDescending(s => s.Timestamp).ToList();
    }

    public static bool RestoreSnapshot(string gameIdentifier, string targetDirectory, SnapshotInfo snapshot)
    {
        try
        {
            if (!Directory.Exists(snapshot.DirectoryPath)) return false;

            // 1. Create a safety backup of targetDirectory first before restoring
            if (Directory.Exists(targetDirectory))
            {
                CreateSnapshot(gameIdentifier, targetDirectory, "Safety Backup before Rollback");
            }
            else
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 2. Copy snapshot files back into targetDirectory
            var snapshotFiles = Directory.GetFiles(snapshot.DirectoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in snapshotFiles)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("snapshot.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relPath = Path.GetRelativePath(snapshot.DirectoryPath, file);
                var destPath = Path.Combine(targetDirectory, relPath);
                var destDir = Path.GetDirectoryName(destPath)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                File.Copy(file, destPath, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restore snapshot: {ex}");
            return false;
        }
    }

    private static void PurgeOldSnapshots(string gameSnapshotDir)
    {
        try
        {
            var dirs = Directory.GetDirectories(gameSnapshotDir)
                .OrderBy(Directory.GetCreationTime)
                .ToList();

            while (dirs.Count > MaxSnapshotsPerGame)
            {
                var toDelete = dirs[0];
                dirs.RemoveAt(0);
                Directory.Delete(toDelete, true);
            }
        }
        catch { }
    }
}
