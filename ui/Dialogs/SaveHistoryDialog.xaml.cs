using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CloudRedirect.Resources;
using CloudRedirect.Services;

namespace CloudRedirect.Dialogs;

public partial class SaveHistoryDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _gameIdentifier;
    private readonly string? _targetSaveDir;
    private readonly string? _appId;
    private readonly string? _accountId;

    public SaveHistoryDialog(string gameIdentifier, string? targetSaveDir = null, string? appId = null, string? accountId = null)
    {
        InitializeComponent();
        _gameIdentifier = gameIdentifier;
        _targetSaveDir = targetSaveDir;
        _appId = appId;
        _accountId = accountId;

        // If appId was not provided, see if gameIdentifier is numeric or in universal profiles
        if (string.IsNullOrEmpty(_appId))
        {
            if (uint.TryParse(_gameIdentifier, out _))
            {
                _appId = _gameIdentifier;
            }
            else
            {
                var profile = UniversalSaveWatcherService.GetProfiles()
                    .Find(p => p.GameName.Equals(_gameIdentifier, StringComparison.OrdinalIgnoreCase));
                if (profile != null && profile.SteamAppId > 0)
                {
                    _appId = profile.SteamAppId.ToString();
                }
            }
        }

        var historyLabel = S.Get("SaveHistory_Title");
        if (string.IsNullOrWhiteSpace(historyLabel) || historyLabel == "SaveHistory_Title") historyLabel = "Save History";
        GameTitleText.Text = $"{gameIdentifier} — {historyLabel}";
        LoadSnapshots();
    }

    private string? ResolveTargetSaveDir()
    {
        if (!string.IsNullOrEmpty(_targetSaveDir) && Directory.Exists(_targetSaveDir))
            return _targetSaveDir;

        var steamPath = SteamDetector.FindSteamPath();
        if (!string.IsNullOrEmpty(_appId) && uint.TryParse(_appId, out var aid))
        {
            var found = SaveHistoryManager.FindAppStorageDir(steamPath, aid, _accountId);
            if (found != null) return found;
        }

        var profile = UniversalSaveWatcherService.GetProfiles()
            .Find(p => p.GameName.Equals(_gameIdentifier, StringComparison.OrdinalIgnoreCase));
        if (profile != null && Directory.Exists(profile.ExpandedSavePath))
        {
            return profile.ExpandedSavePath;
        }

        return _targetSaveDir;
    }

    private void OpenDriveFolder_Click(object sender, RoutedEventArgs e)
    {
        uint appId = 0;
        if (!string.IsNullOrEmpty(_appId)) uint.TryParse(_appId, out appId);

        var targetDir = ResolveTargetSaveDir();
        var dlg = new CloudFolderBrowserDialog(_gameIdentifier, targetDir, appId)
        {
            Owner = this
        };
        dlg.ShowDialog();
    }

    private void OpenLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        var targetDir = ResolveTargetSaveDir();
        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            Process.Start(new ProcessStartInfo { FileName = targetDir, UseShellExecute = true })?.Dispose();
            return;
        }

        if (!string.IsNullOrEmpty(_appId))
        {
            var acctId = !string.IsNullOrEmpty(_accountId) ? _accountId : "0";
            CloudLocationService.OpenLocalStorageFolder(acctId, _appId);
        }
    }

    private void LoadSnapshots()
    {
        var targetDir = ResolveTargetSaveDir();
        var snapshots = SaveHistoryManager.GetSnapshots(_gameIdentifier, _appId);

        // If no snapshots exist yet, but we found save files on disk, automatically create a baseline snapshot!
        if (snapshots.Count == 0 && !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                var files = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    SaveHistoryManager.CreateSnapshot(_gameIdentifier, targetDir, "Current Save State", _appId);
                    snapshots = SaveHistoryManager.GetSnapshots(_gameIdentifier, _appId);
                }
            }
            catch
            {
                // Ignore initial snapshot creation failure
            }
        }

        SnapshotsListBox.ItemsSource = snapshots;

        EmptyStateBorder.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsListBox.Visibility = snapshots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CreateSnapshotNow_Click(object sender, RoutedEventArgs e)
    {
        var targetDir = ResolveTargetSaveDir();
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            var msg = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Create Snapshot",
                Content = "No local save folder found for this game yet. Run the game first or ensure save files exist.",
                CloseButtonText = "OK"
            };
            await msg.ShowDialogAsync();
            return;
        }

        var files = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            var msg = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Create Snapshot",
                Content = "The save directory is currently empty. No save files to back up.",
                CloseButtonText = "OK"
            };
            await msg.ShowDialogAsync();
            return;
        }

        var snapshot = SaveHistoryManager.CreateSnapshot(_gameIdentifier, targetDir, "Manual Snapshot", _appId);
        if (snapshot != null)
        {
            LoadSnapshots();
        }
        else
        {
            var msg = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Snapshot Up-To-Date",
                Content = "The current save files are identical to the most recent snapshot.",
                CloseButtonText = "OK"
            };
            await msg.ShowDialogAsync();
        }
    }

    private void BrowseSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SnapshotInfo snapshot && Directory.Exists(snapshot.DirectoryPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = snapshot.DirectoryPath,
                UseShellExecute = true
            });
        }
    }

    private async void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SnapshotInfo snapshot)
            return;

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete Snapshot",
            Content = $"Are you sure you want to permanently delete the snapshot from {snapshot.FormattedTime}?",
            PrimaryButtonText = "Delete",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel"
        };

        var result = await confirm.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            SaveHistoryManager.DeleteSnapshot(snapshot);
            LoadSnapshots();
        }
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SnapshotInfo snapshot)
            return;

        var targetDir = ResolveTargetSaveDir();

        if (string.IsNullOrEmpty(targetDir))
        {
            var msg = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Restore Snapshot",
                Content = "Save directory not resolved. You can still manually view the snapshot files.",
                CloseButtonText = "OK"
            };
            await msg.ShowDialogAsync();
            return;
        }

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Confirm Save Rollback",
            Content = $"Are you sure you want to rollback to the version from {snapshot.FormattedTime}?\n\nA safety backup of your current save files will be created before restoring.",
            PrimaryButtonText = "Restore Version",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "Cancel"
        };

        var result = await confirm.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            bool success = SaveHistoryManager.RestoreSnapshot(_gameIdentifier, targetDir, snapshot);
            if (success)
            {
                var okBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Rollback Successful",
                    Content = $"Save files from {snapshot.FormattedTime} have been successfully restored.\nYour previous save files were backed up as a safeguard.",
                    CloseButtonText = "OK"
                };
                await okBox.ShowDialogAsync();
                LoadSnapshots();
            }
            else
            {
                var failBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Rollback Failed",
                    Content = "Could not restore the snapshot files. Please ensure the game is not currently running.",
                    CloseButtonText = "OK"
                };
                await failBox.ShowDialogAsync();
            }
        }
    }

    private void OpenBaseFolder_Click(object sender, RoutedEventArgs e)
    {
        var safeName = SaveHistoryManager.SanitizeFolderName(_gameIdentifier);
        var path = Path.Combine(SaveHistoryManager.GetSnapshotsBaseDir(), safeName);
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
