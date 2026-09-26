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

    private async void OpenDriveFolder_Click(object sender, RoutedEventArgs e)
    {
        var driveLink = await UniversalCloudSyncService.GetGameDriveFolderWebLinkAsync(_gameIdentifier);
        if (!string.IsNullOrEmpty(driveLink))
        {
            try
            {
                Process.Start(new ProcessStartInfo(driveLink) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(_appId))
        {
            var acctId = !string.IsNullOrEmpty(_accountId) ? _accountId : "0";
            await CloudLocationService.OpenCloudLocationAsync(acctId, _appId, _gameIdentifier);
        }
        else
        {
            var url = $"https://drive.google.com/drive/search?q={Uri.EscapeDataString(_gameIdentifier)}";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch { }
        }
    }

    private void OpenLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_targetSaveDir) && Directory.Exists(_targetSaveDir))
        {
            Process.Start(new ProcessStartInfo { FileName = _targetSaveDir, UseShellExecute = true })?.Dispose();
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
        var snapshots = SaveHistoryManager.GetSnapshots(_gameIdentifier);
        SnapshotsListBox.ItemsSource = snapshots;

        EmptyStateBorder.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsListBox.Visibility = snapshots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SnapshotInfo snapshot)
            return;

        var targetDir = _targetSaveDir;
        if (string.IsNullOrEmpty(targetDir))
        {
            // If targetDir wasn't provided directly, look for profile in UniversalSaveWatcherService
            var profile = UniversalSaveWatcherService.GetProfiles()
                .Find(p => p.GameName.Equals(_gameIdentifier, StringComparison.OrdinalIgnoreCase));
            targetDir = profile?.ExpandedSavePath;
        }

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
