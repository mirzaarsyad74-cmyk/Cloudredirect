using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CloudRedirect.Dialogs;
using CloudRedirect.Services;

namespace CloudRedirect.Pages;

public partial class UniversalSavesPage : Page
{
    public UniversalSavesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshList();
            UniversalSaveWatcherService.OnProfilesChanged += OnProfilesChangedHandler;
            UniversalSaveWatcherService.OnProfileStatusChanged += OnProfileStatusChangedHandler;
        };
        Unloaded += (_, _) =>
        {
            UniversalSaveWatcherService.OnProfilesChanged -= OnProfilesChangedHandler;
            UniversalSaveWatcherService.OnProfileStatusChanged -= OnProfileStatusChangedHandler;
        };
    }

    private void OnProfilesChangedHandler()
    {
        Dispatcher.Invoke(RefreshList);
    }

    private void OnProfileStatusChangedHandler(UniversalGameProfile profile)
    {
        Dispatcher.Invoke(RefreshList);
    }

    private void RefreshList()
    {
        var list = UniversalSaveWatcherService.GetProfiles().ToList();
        GamesItemsControl.ItemsSource = list;
        EmptyStateBorder.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesItemsControl.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var existing = UniversalSaveWatcherService.GetProfiles();
        var availablePresets = UniversalSaveWatcherService.PopularPresets
            .Where(p => !existing.Any(ex => ex.GameName.Equals(p.GameName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (availablePresets.Count == 0)
        {
            var msg = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Popular Presets",
                Content = "All built-in presets have already been added to your library!",
                CloseButtonText = "OK"
            };
            await msg.ShowDialogAsync();
            return;
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Select a game preset to add to Universal Cloud Saves:",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var combo = new ComboBox
        {
            ItemsSource = availablePresets.Select(p => p.GameName).ToList(),
            SelectedIndex = 0,
            Height = 32,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(combo);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Add Game Preset",
            Content = stack,
            PrimaryButtonText = "Add Game",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "Cancel"
        };

        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary && combo.SelectedIndex >= 0)
        {
            var selectedPreset = availablePresets[combo.SelectedIndex];
            UniversalSaveWatcherService.AddProfile(new UniversalGameProfile
            {
                GameName = selectedPreset.GameName,
                ProcessName = selectedPreset.ProcessName,
                SaveFolderPath = selectedPreset.SaveFolderPath
            });
            RefreshList();
        }
    }

    private async void AddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock { Text = "Game Name:", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new Wpf.Ui.Controls.TextBox { PlaceholderText = "e.g. My Custom Game", Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(nameBox);

        stack.Children.Add(new TextBlock { Text = "Process Executable Name:", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var procBox = new Wpf.Ui.Controls.TextBox { PlaceholderText = "e.g. game.exe (without path)", Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(procBox);

        stack.Children.Add(new TextBlock { Text = "Save Folder Path (supports %APPDATA%, %USERPROFILE%):", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var pathBox = new Wpf.Ui.Controls.TextBox { PlaceholderText = @"e.g. %APPDATA%\MyGame\Saves", Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(pathBox);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Add Custom Game",
            Content = stack,
            PrimaryButtonText = "Add Game",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = "Cancel"
        };

        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            var name = nameBox.Text.Trim();
            var proc = procBox.Text.Trim();
            var path = pathBox.Text.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                var errBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Invalid Input",
                    Content = "Please provide at least a Game Name and Save Folder Path.",
                    CloseButtonText = "OK"
                };
                await errBox.ShowDialogAsync();
                return;
            }

            UniversalSaveWatcherService.AddProfile(new UniversalGameProfile
            {
                GameName = name,
                ProcessName = proc,
                SaveFolderPath = path
            });
            RefreshList();
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is UniversalGameProfile profile)
        {
            await UniversalSaveWatcherService.SyncProfileNowAsync(profile, "Manual Sync");
            RefreshList();
        }
    }

    private void SaveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is UniversalGameProfile profile)
        {
            var dialog = new SaveHistoryDialog(profile.GameName, profile.ExpandedSavePath)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is UniversalGameProfile profile)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Remove Game",
                Content = $"Stop monitoring '{profile.GameName}'? Existing backups will not be deleted.",
                PrimaryButtonText = "Remove",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                CloseButtonText = "Cancel"
            };

            var res = await box.ShowDialogAsync();
            if (res == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                UniversalSaveWatcherService.RemoveProfile(profile.Id);
                RefreshList();
            }
        }
    }
}
