using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class ScriptPluginAuthoringWindow : Window
{
    public ScriptPluginAuthoringWindow(ScriptPluginAuthoringViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
