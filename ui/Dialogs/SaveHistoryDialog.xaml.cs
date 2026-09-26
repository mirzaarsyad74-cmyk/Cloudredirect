using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CloudRedirect.Services;

namespace CloudRedirect.Dialogs;

public partial class SaveHistoryDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _gameIdentifier;
    private readonly string? _targetSaveDir;

    public SaveHistoryDialog(string gameIdentifier, string? targetSaveDir = null)
    {
        InitializeComponent();
        _gameIdentifier = gameIdentifier;
        _targetSaveDir = targetSaveDir;

        GameTitleText.Text = $"{gameIdentifier} — Save History";
        LoadSnapshots();
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
