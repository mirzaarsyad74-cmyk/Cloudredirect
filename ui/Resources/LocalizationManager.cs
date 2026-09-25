using System.ComponentModel;

namespace CloudRedirect.Resources;

/// <summary>
/// Singleton notifier that raises PropertyChanged whenever the application culture changes,
/// allowing WPF bindings targeting indexed resource keys to refresh live in real-time.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => S.Get(key);

    public void Invalidate()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
