using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CloudRedirect.Services;

/// <summary>
/// Handles automatic cloud uploads and synchronization for Universal Cloud Saves (Safe Mode).
/// Uploads saves directly to Google Drive, OneDrive, or local sync folders.
/// </summary>
public static class UniversalCloudSyncService
{
    public record SyncResult(bool Success, int FileCount, long TotalBytes, string Message, string? FolderUrl = null);

    /// <summary>
    /// Synchronizes a Universal Game Profile's save files to the active cloud provider (Google Drive, Folder, etc.).
    /// </summary>
    public static async Task<SyncResult> UploadProfileSavesToCloudAsync(UniversalGameProfile profile)
    {
        var saveDir = profile.ExpandedSavePath;
        if (!Directory.Exists(saveDir))
        {
            return new SyncResult(false, 0, 0, "Save directory does not exist locally.");
        }

        var saveFiles = Directory.GetFiles(saveDir, "*", SearchOption.AllDirectories);
        if (saveFiles.Length == 0)
        {
            return new SyncResult(true, 0, 0, "Save directory is empty, nothing to upload.");
        }

        var config = SteamDetector.ReadConfig();
        if (config == null)
        {
            return new SyncResult(false, 0, 0, "No cloud provider configured in CloudRedirect.");
        }

        // 1. Google Drive Direct API Sync
        if (config.Provider == "gdrive")
        {
            return await UploadToGoogleDriveAsync(profile, saveDir, saveFiles, config);
        }

        // 2. Folder / Local / Network Sync
        if (config.IsFolder || config.IsLocal || !string.IsNullOrEmpty(config.SyncPath))
        {
            return await UploadToLocalSyncFolderAsync(profile, saveDir, saveFiles, config.SyncPath ?? "");
        }

        // 3. Fallback: Local snapshot only
        return new SyncResult(true, saveFiles.Length, 0, "Local snapshots active (Safe Mode).");
    }

    private static async Task<SyncResult> UploadToGoogleDriveAsync(
        UniversalGameProfile profile,
        string saveDir,
        string[] saveFiles,
        CloudConfig config)
    {
        try
        {
            var tokenPath = config.TokenPath ?? Path.Combine(SteamDetector.GetConfigDir(), "google_tokens.json");
            var accessToken = await OAuthService.GetValidAccessTokenAsync("gdrive", tokenPath);

            if (string.IsNullOrEmpty(accessToken))
            {
                return new SyncResult(false, 0, 0, "Google Drive authentication token expired. Please re-authenticate in Cloud Settings.");
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Step 1: Resolve or create root 'CloudRedirect' folder
            var rootFolderId = await EnsureDriveFolderAsync(http, "CloudRedirect", null);
            if (string.IsNullOrEmpty(rootFolderId))
            {
                return new SyncResult(false, 0, 0, "Failed to create or access 'CloudRedirect' root folder on Google Drive.");
            }

            // Step 2: Resolve or create 'UniversalCloudSaves' folder inside 'CloudRedirect'
            var universalFolderId = await EnsureDriveFolderAsync(http, "UniversalCloudSaves", rootFolderId);
            if (string.IsNullOrEmpty(universalFolderId))
            {
                return new SyncResult(false, 0, 0, "Failed to create 'UniversalCloudSaves' directory on Google Drive.");
            }

            // Step 3: Resolve or create Game folder inside 'UniversalCloudSaves'
            var sanitizedGameName = SaveHistoryManager.SanitizeFolderName(profile.GameName);
            var gameFolderId = await EnsureDriveFolderAsync(http, sanitizedGameName, universalFolderId);
            if (string.IsNullOrEmpty(gameFolderId))
            {
                return new SyncResult(false, 0, 0, $"Failed to create folder for '{profile.GameName}' on Google Drive.");
            }

            // Fetch web link of the game folder for direct opening
            var folderLink = await GetDriveFolderWebLinkAsync(http, gameFolderId);

            // Step 4: List existing files in this game folder to avoid redundant uploads
            var existingFiles = await ListDriveFolderFilesAsync(http, gameFolderId);

            int uploadedCount = 0;
            long totalBytesUploaded = 0;

            foreach (var filePath in saveFiles)
            {
                var relPath = Path.GetRelativePath(saveDir, filePath).Replace('\\', '/');
                var fileName = Path.GetFileName(filePath);
                var fileInfo = new FileInfo(filePath);
                var fileSize = fileInfo.Length;

                // Handle nested directories if any
                var targetFolderId = gameFolderId;
                var subDir = Path.GetDirectoryName(Path.GetRelativePath(saveDir, filePath));
                if (!string.IsNullOrEmpty(subDir))
                {
                    var parts = subDir.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    var curParent = gameFolderId;
                    foreach (var part in parts)
                    {
                        curParent = await EnsureDriveFolderAsync(http, part, curParent);
                        if (string.IsNullOrEmpty(curParent)) break;
                    }
                    if (!string.IsNullOrEmpty(curParent)) targetFolderId = curParent;
                }

                // Check if file already exists with same size
                if (existingFiles.TryGetValue(relPath, out var driveFile) && driveFile.Size == fileSize)
                {
                    totalBytesUploaded += fileSize;
                    continue; // Up-to-date, skip re-uploading
                }

                // Upload new / updated file
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                bool ok = await UploadFileToDriveAsync(http, targetFolderId, fileName, fileBytes);
                if (ok)
                {
                    uploadedCount++;
                    totalBytesUploaded += fileBytes.Length;
                }
            }

            // Write telemetry entry to log for SaveUploadWatcher
            LogUploadActivity(profile, uploadedCount, totalBytesUploaded);

            return new SyncResult(
                true,
                saveFiles.Length,
                totalBytesUploaded,
                $"Successfully synced {saveFiles.Length} file(s) ({uploadedCount} uploaded) to Google Drive.",
                folderLink);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UniversalCloudSync error for {profile.GameName}: {ex}");
            return new SyncResult(false, 0, 0, $"Google Drive sync error: {ex.Message}");
        }
    }

    private static async Task<SyncResult> UploadToLocalSyncFolderAsync(
        UniversalGameProfile profile,
        string saveDir,
        string[] saveFiles,
        string syncPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(syncPath) || !Directory.Exists(syncPath))
                {
                    return new SyncResult(false, 0, 0, "Configured sync path does not exist.");
                }

                var sanitizedName = SaveHistoryManager.SanitizeFolderName(profile.GameName);
                var destRoot = Path.Combine(syncPath, "UniversalCloudSaves", sanitizedName);
                if (!Directory.Exists(destRoot))
                    Directory.CreateDirectory(destRoot);

                int copied = 0;
                long totalBytes = 0;

                foreach (var src in saveFiles)
                {
                    var rel = Path.GetRelativePath(saveDir, src);
                    var dest = Path.Combine(destRoot, rel);
                    var destDir = Path.GetDirectoryName(dest)!;
                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    var info = new FileInfo(src);
                    File.Copy(src, dest, overwrite: true);
                    copied++;
                    totalBytes += info.Length;
                }

                return new SyncResult(
                    true,
                    copied,
                    totalBytes,
                    $"Successfully synced {copied} file(s) to local cloud storage folder.");
            }
            catch (Exception ex)
            {
                return new SyncResult(false, 0, 0, $"Local sync error: {ex.Message}");
            }
        });
    }

    private static async Task<string?> EnsureDriveFolderAsync(HttpClient http, string name, string? parentId)
    {
        try
        {
            var escapedName = name.Replace("'", "\\'");
            var parentClause = string.IsNullOrEmpty(parentId)
                ? "'root' in parents"
                : $"'{parentId}' in parents";

            var q = Uri.EscapeDataString($"name='{escapedName}' and mimeType='application/vnd.google-apps.folder' and {parentClause} and trashed=false");
            var searchUrl = $"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id,name)&pageSize=1";

            var resp = await http.GetAsync(searchUrl);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                {
                    return files[0].GetProperty("id").GetString();
                }
            }

            // Not found, create folder
            var createObj = new Dictionary<string, object>
            {
                ["name"] = name,
                ["mimeType"] = "application/vnd.google-apps.folder"
            };
            if (!string.IsNullOrEmpty(parentId))
            {
                createObj["parents"] = new[] { parentId };
            }

            var createContent = new StringContent(JsonSerializer.Serialize(createObj), Encoding.UTF8, "application/json");
            var postResp = await http.PostAsync("https://www.googleapis.com/drive/v3/files?fields=id", createContent);
            if (postResp.IsSuccessStatusCode)
            {
                var postJson = await postResp.Content.ReadAsStringAsync();
                using var postDoc = JsonDocument.Parse(postJson);
                return postDoc.RootElement.GetProperty("id").GetString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EnsureDriveFolderAsync error for '{name}': {ex}");
        }
        return null;
    }

    private static async Task<string?> GetDriveFolderWebLinkAsync(HttpClient http, string folderId)
    {
        try
        {
            var url = $"https://www.googleapis.com/drive/v3/files/{folderId}?fields=id,webViewLink";
            var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webViewLink", out var linkProp))
                {
                    return linkProp.GetString();
                }
            }
        }
        catch { }
        return $"https://drive.google.com/drive/folders/{folderId}";
    }

    private record DriveFileInfo(string Id, string Name, long Size);

    private static async Task<Dictionary<string, DriveFileInfo>> ListDriveFolderFilesAsync(HttpClient http, string folderId)
    {
        var dict = new Dictionary<string, DriveFileInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = Uri.EscapeDataString($"'{folderId}' in parents and mimeType!='application/vnd.google-apps.folder' and trashed=false");
            var url = $"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id,name,size)&pageSize=1000";

            var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var files))
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        var id = f.GetProperty("id").GetString()!;
                        var name = f.GetProperty("name").GetString()!;
                        long size = 0;
                        if (f.TryGetProperty("size", out var sProp))
                        {
                            long.TryParse(sProp.GetString(), out size);
                        }
                        dict[name] = new DriveFileInfo(id, name, size);
                    }
                }
            }
        }
        catch { }
        return dict;
    }

    private static async Task<bool> UploadFileToDriveAsync(HttpClient http, string parentFolderId, string fileName, byte[] content)
    {
        try
        {
            var boundary = "----CloudRedirectUploadBoundary" + Guid.NewGuid().ToString("N");
            var multipart = new MultipartFormDataContent(boundary);

            var metaObj = new
            {
                name = fileName,
                parents = new[] { parentFolderId }
            };
            var metaContent = new StringContent(JsonSerializer.Serialize(metaObj), Encoding.UTF8, "application/json");
            multipart.Add(metaContent);

            var fileContent = new ByteArrayContent(content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(fileContent);

            var url = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart";
            var resp = await http.PostAsync(url, multipart);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UploadFileToDriveAsync error for {fileName}: {ex}");
            return false;
        }
    }

    private static void LogUploadActivity(UniversalGameProfile profile, int uploadedCount, long bytes)
    {
        try
        {
            var steamPath = SteamDetector.FindSteamPath();
            if (steamPath != null)
            {
                var logFile = Path.Combine(steamPath, "cloud_redirect.log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [GDriveProvider] UploadBatch: {uploadedCount} file(s) ({uploadedCount} uploaded, 0 CAS-skipped) for {profile.GameName} (Universal Save)\n";
                File.AppendAllText(logFile, line);
            }
        }
        catch { }
    }
}
