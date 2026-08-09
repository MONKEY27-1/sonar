using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class PluginAuthoringWindow : Window
{
    public PluginAuthoringWindow(PluginAuthoringViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
