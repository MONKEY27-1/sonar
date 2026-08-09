using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soundboard.Core.Models;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class MainWindow : Window
{
    private const string SoundDragFormat = "Soundboard.SoundButtonViewModel";

    private readonly MainViewModel _viewModel;
    private Point _soundDragStartPoint;
    private Border? _dragHighlightedBorder;
    private AdornerLayer? _dragAdornerLayer;
    private DragGhostAdorner? _dragGhostAdorner;
    private bool _isDraggingSound;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync().ConfigureAwait(true);
        _viewModel.RestoreLayout(this);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await _viewModel.SaveLayoutAsync(this).ConfigureAwait(true);
    }

    /// <summary>ContextMenu only opens on right-click by default — this opens the same menu on a
    /// normal left click, so the button reads as a dropdown rather than a right-click-only menu.</summary>
    private void SoundsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        await _viewModel.ImportDroppedFilesCommand.ExecuteAsync(files).ConfigureAwait(true);
    }

    private void SoundButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _soundDragStartPoint = e.GetPosition(null);

    private void SoundButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // DoDragDrop pumps its own message loop while it blocks, which can let a queued
        // PreviewMouseMove for the same gesture re-enter this handler before the first call
        // returns — without this guard that starts a second, nested drag for the same tile, and
        // both eventually fire their own Drop. The two reorders land in quick succession, and the
        // second one (computed against the already-reordered list) swaps the tiles right back,
        // which is what "does it, then resets" actually was.
        if (_isDraggingSound) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Button { DataContext: SoundButtonViewModel button } sourceButton) return;

        var diff = _soundDragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDraggingSound = true;
        try
        {
            // Dimmed rather than left at full opacity so it's visually obvious which tile is
            // actually being moved — SetCurrentValue (not a plain assignment) so this doesn't tear
            // out the existing IsPlaying-driven Opacity binding; ClearValue below hands control back
            // to that binding instead of leaving a stale local value behind.
            sourceButton.SetCurrentValue(OpacityProperty, 0.35);

            OpenDragGhost(sourceButton);
            sourceButton.GiveFeedback += DragGhost_GiveFeedback;
            DragDrop.DoDragDrop(sourceButton, new DataObject(SoundDragFormat, button), DragDropEffects.Move);
            sourceButton.GiveFeedback -= DragGhost_GiveFeedback;
            CloseDragGhost();

            sourceButton.ClearValue(OpacityProperty);
            ClearDragHighlight();
        }
        finally
        {
            _isDraggingSound = false;
        }
    }

    /// <summary>WPF's DragDrop gives you a cursor icon and nothing else by default — no visual
    /// of what's actually being dragged. This renders the tile being dragged into a bitmap once,
    /// up front, and draws it via an Adorner that GiveFeedback (below) keeps pinned to the
    /// cursor for the duration of the drag — a real ghost of the tile itself, not a generic
    /// placeholder.
    ///
    /// Deliberately an Adorner (rendered inside this window's own visual tree), not a Popup —
    /// a Popup is a separate OS window, and opening one WHILE DragDrop.DoDragDrop's own OLE-based
    /// drag loop is running (it does its own low-level mouse tracking, entirely separate from
    /// normal WPF input) stalled that loop and froze the drag in place. An Adorner never creates
    /// a new window, so it doesn't compete with OLE for mouse input.</summary>
    private void OpenDragGhost(Button sourceButton)
    {
        var size = new Size(sourceButton.ActualWidth, sourceButton.ActualHeight);
        if (size.Width <= 0 || size.Height <= 0) return;

        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sourceButton);

        _dragAdornerLayer = AdornerLayer.GetAdornerLayer(sourceButton);
        if (_dragAdornerLayer is null) return;

        _dragGhostAdorner = new DragGhostAdorner(sourceButton, bitmap, size);
        _dragAdornerLayer.Add(_dragGhostAdorner);
        PositionDragGhost(sourceButton);
    }

    private void DragGhost_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (sender is Button sourceButton)
        {
            PositionDragGhost(sourceButton);
        }

        // Default cursor (with its little "move" glyph) still shown alongside the ghost image —
        // the two together read better than either alone.
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void PositionDragGhost(Button sourceButton)
    {
        if (_dragGhostAdorner is null) return;

        // GetCursorPos (raw Win32) rather than WPF's own Mouse.GetPosition — WPF's normal
        // mouse-tracking doesn't update through DoDragDrop's OLE loop, so it would return stale
        // positions during an active drag. PointFromScreen converts the result into this
        // button's own coordinate space, handling DPI scaling correctly.
        var cursorScreenDips = GetCursorPosInDips();
        var localPos = sourceButton.PointFromScreen(cursorScreenDips);

        // Centered under the cursor rather than pinned at its corner — reads more like "you're
        // holding the tile" than like a tooltip trailing behind the pointer.
        _dragGhostAdorner.Offset = new Point(
            localPos.X - _dragGhostAdorner.GhostSize.Width / 2,
            localPos.Y - _dragGhostAdorner.GhostSize.Height / 2);
        _dragGhostAdorner.InvalidateVisual();
    }

    private void CloseDragGhost()
    {
        if (_dragGhostAdorner is not null && _dragAdornerLayer is not null)
        {
            _dragAdornerLayer.Remove(_dragGhostAdorner);
        }

        _dragGhostAdorner = null;
        _dragAdornerLayer = null;
    }

    /// <summary>Just draws a bitmap at a caller-controlled offset — all the actual cursor
    /// tracking/positioning logic lives in PositionDragGhost above, this only needs to know
    /// where to paint.</summary>
    private sealed class DragGhostAdorner : Adorner
    {
        private readonly ImageSource _image;

        public Size GhostSize { get; }
        public Point Offset { get; set; }

        public DragGhostAdorner(UIElement adornedElement, ImageSource image, Size size) : base(adornedElement)
        {
            _image = image;
            GhostSize = size;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.PushOpacity(0.85);
            drawingContext.DrawImage(_image, new Rect(Offset, GhostSize));
            drawingContext.Pop();
        }
    }

    /// <summary>GetCursorPos returns physical device pixels, but Popup/Window positioning APIs
    /// (HorizontalOffset, Left/Top with PlacementMode.Absolute) expect device-independent units
    /// — the two only match at exactly 100% display scaling. Everything else (125%, 150%, etc.,
    /// the actual default on most Windows displays) would silently drift the ghost away from the
    /// real cursor position without this conversion.</summary>
    private Point GetCursorPosInDips()
    {
        GetCursorPos(out var point);
        var physicalPoint = new Point(point.X, point.Y);

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform is { } m ? m.Transform(physicalPoint) : physicalPoint;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private void SoundButton_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SoundDragFormat)) return;
        if (sender is not Button { Template: { } template } button) return;
        if (template.FindName("Root", button) is not Border root) return;

        ClearDragHighlight();
        root.BorderBrush = (Brush)FindResource("AccentBrush");
        root.BorderThickness = new Thickness(3);
        _dragHighlightedBorder = root;
    }

    private void SoundButton_DragLeave(object sender, DragEventArgs e) => ClearDragHighlight();

    private void ClearDragHighlight()
    {
        if (_dragHighlightedBorder is null) return;

        _dragHighlightedBorder.ClearValue(Border.BorderBrushProperty);
        _dragHighlightedBorder.ClearValue(Border.BorderThicknessProperty);
        _dragHighlightedBorder = null;
    }

    private void SoundGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SoundDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SoundGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDragHighlight();
        if (e.Data.GetData(SoundDragFormat) is not SoundButtonViewModel dragged) return;

        var targetButton = FindAncestor<Button>(e.OriginalSource as DependencyObject);
        var target = targetButton?.DataContext as SoundButtonViewModel;
        _viewModel.ReorderSound(dragged, target);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private void FolderButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu, DataContext: SoundFolder folder }) return;

        var index = menu.Items.IndexOf(menu.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == "DeleteFolderMenuItem"));
        if (index < 0) return;

        // Replaced with a brand-new MenuItem each open (rather than rewiring the existing one)
        // so there's never more than one Click handler subscribed — a reused element with a
        // closure-based handler re-added on every open would stack up duplicate handlers.
        var freshItem = new MenuItem { Header = "Delete Folder", Name = "DeleteFolderMenuItem" };
        freshItem.Click += async (_, _) => await _viewModel.DeleteFolderAsync(folder).ConfigureAwait(true);
        menu.Items[index] = freshItem;
    }

    private void SoundButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // ContextMenus render in their own popup, outside the normal visual tree, so
        // RelativeSource AncestorType=Window inside a MenuItem's Command binding can't find
        // this window at all — it just silently resolves to nothing. Bridging the window's
        // DataContext through here lets the XAML reach it via AncestorType=ContextMenu instead,
        // which DOES work since MenuItems are visual descendants of their own ContextMenu.
        if (sender is not Button { ContextMenu: { } menu, DataContext: SoundButtonViewModel button }) return;

        menu.Tag = DataContext;
        PopulateMoveToFolderMenu(menu, button);
        PopulateSetHotkeyMenuItem(menu, button);
        PopulateOutputRouteMenu(menu, button);
    }

    private void VoiceChangerPresetButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Same Tag-bridging reason as SoundButton_ContextMenuOpening above.
        if (sender is not Button { ContextMenu: { } menu }) return;

        menu.Tag = DataContext;
    }

    private void PopulateOutputRouteMenu(ContextMenu menu, SoundButtonViewModel button)
    {
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == "OutputRouteMenuItem") is not { } routeItem)
        {
            return;
        }

        routeItem.Items.Clear();

        var current = button.Sound.OutputRouteOverride;
        AddOutputRouteOption(routeItem, button, "Use Default", null, current);
        routeItem.Items.Add(new Separator());
        AddOutputRouteOption(routeItem, button, "Headphones Only", OutputRoute.Headphones, current);
        AddOutputRouteOption(routeItem, button, "Microphone Only", OutputRoute.Microphone, current);
        AddOutputRouteOption(routeItem, button, "Both", OutputRoute.Both, current);
    }

    private void AddOutputRouteOption(MenuItem parent, SoundButtonViewModel button, string header, OutputRoute? value, OutputRoute? current)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value == current };
        item.Click += async (_, _) => await _viewModel.SetSoundOutputRouteOverrideAsync(button, value).ConfigureAwait(true);
        parent.Items.Add(item);
    }

    private void PopulateSetHotkeyMenuItem(ContextMenu menu, SoundButtonViewModel button)
    {
        var index = menu.Items.IndexOf(menu.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == "SetHotkeyMenuItem"));
        if (index < 0) return;

        // Same reasoning as the folder delete item: a fresh MenuItem each open avoids ever
        // stacking up duplicate Click handlers on a reused element.
        var freshItem = new MenuItem { Header = "Set Hotkey...", Name = "SetHotkeyMenuItem" };
        freshItem.Click += async (_, _) =>
        {
            var dialog = new HotkeyCaptureDialog(button.DisplayName, button.Sound.Hotkey) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await _viewModel.SetSoundHotkeyAsync(button, dialog.Result).ConfigureAwait(true);
            }
        };
        menu.Items[index] = freshItem;
    }

    private void PopulateMoveToFolderMenu(ContextMenu menu, SoundButtonViewModel button)
    {
        // The available folders are only known at menu-open time and can change between opens,
        // so this submenu is built fresh here rather than via XAML binding (which would need to
        // reach both "which sound" and "which folder" from inside a nested ItemsSource — doable,
        // but far more fragile than just building it directly where we already have both).
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == "MoveToFolderMenuItem") is not { } moveToFolderItem)
        {
            return;
        }

        moveToFolderItem.Items.Clear();

        var unfiledItem = new MenuItem { Header = "Unfiled" };
        unfiledItem.Click += async (_, _) => await _viewModel.MoveSoundToFolderAsync(button, null).ConfigureAwait(true);
        moveToFolderItem.Items.Add(unfiledItem);

        if (_viewModel.Folders.Count > 0)
        {
            moveToFolderItem.Items.Add(new Separator());
        }

        foreach (var folder in _viewModel.Folders)
        {
            var folderItem = new MenuItem { Header = folder.Name };
            folderItem.Click += async (_, _) => await _viewModel.MoveSoundToFolderAsync(button, folder.Id).ConfigureAwait(true);
            moveToFolderItem.Items.Add(folderItem);
        }
    }
}
