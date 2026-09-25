using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace CloudRedirect.Resources;

/// <summary>
/// XAML markup extension for localized strings.
/// Connects dynamic bindings to <see cref="LocalizationManager"/> so UI elements
/// automatically update their text whenever the application language changes without restart.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension() => Key = "";
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return "";

        if (serviceProvider?.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target)
        {
            if (target.TargetObject is DependencyObject && target.TargetProperty is DependencyProperty)
            {
                var binding = new Binding($"[{Key}]")
                {
                    Source = LocalizationManager.Instance,
                    Mode = BindingMode.OneWay
                };
                return binding.ProvideValue(serviceProvider);
            }

            if (target.TargetObject is Setter)
            {
                var binding = new Binding($"[{Key}]")
                {
                    Source = LocalizationManager.Instance,
                    Mode = BindingMode.OneWay
                };
                return binding;
            }
        }

        return S.Get(Key);
    }
}
