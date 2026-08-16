using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Soundboard.Views.Controls;

/// <summary>Reusable "labeled device ComboBox with a Default badge" — replaces what used to be
/// three separately hand-rolled Label+ComboBox blocks in the Settings window (Headphones, Virtual
/// Mic Output, Microphone), all binding the same way to an AudioDeviceInfo collection.</summary>
public partial class DeviceSelector : UserControl
{
    public DeviceSelector() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(DeviceSelector));

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(DeviceSelector));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedValueProperty =
        DependencyProperty.Register(nameof(SelectedValue), typeof(string), typeof(DeviceSelector),
            new FrameworkPropertyMetadata(default(string), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string? SelectedValue
    {
        get => (string?)GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }
}
