using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Soundboard.Views.Controls;

/// <summary>Reusable "labeled checkbox list of devices" — the multi-select counterpart to
/// DeviceSelector (which is inherently single-select, wrapping a ComboBox's SelectedValue). Each
/// row's checked state lives on the bound DeviceCheckItem itself; this control has no notion of
/// a single selected value.</summary>
public partial class MultiDeviceSelector : UserControl
{
    public MultiDeviceSelector() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(MultiDeviceSelector));

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(MultiDeviceSelector));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
}
