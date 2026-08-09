using System.Windows;

namespace Soundboard.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush")!;

        var promptBlock = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Margin = new Thickness(16, 16, 16, 8)
        };

        var inputBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(16, 0, 16, 16),
            Padding = new Thickness(8)
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Style = (Style)Application.Current.FindResource("AccentButton")!,
            Width = 80,
            Margin = new Thickness(0, 0, 8, 16),
            IsDefault = true
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("ToolbarButton")!,
            Width = 80,
            Margin = new Thickness(0, 0, 16, 16),
            IsCancel = true
        };

        okButton.Click += (_, _) =>
        {
            InputText = inputBox.Text;
            DialogResult = true;
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var root = new System.Windows.Controls.StackPanel();
        root.Children.Add(promptBlock);
        root.Children.Add(inputBox);
        root.Children.Add(buttons);

        Content = root;
    }

    public string InputText { get; private set; } = string.Empty;
}
