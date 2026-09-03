using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soundboard.Core.Models;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class MainWindow : Window
{
    private const string SoundDragFormat = "Soundboard.SoundButtonViewModel";

    private readonly MainViewModel _viewModel;

    // TaskbarIcon (Hardcodet.NotifyIcon.Wpf) declared in Window.Resources doesn't get a
    // generated x:Name field the way a normal visual-tree element would — pulled out of the
    // resource dictionary by key instead, once, in the constructor.
    private readonly Hardcodet.Wpf.TaskbarNotification.TaskbarIcon _trayIcon;

    private Point _soundDragStartPoint;
    private Border? _dragHighlightedBorder;
    private SoundButtonViewModel? _dragHighlightedFor;
    private AdornerLayer? _dragAdornerLayer;
    private DragGhostAdorner? _dragGhostAdorner;
    private bool _isDraggingSound;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.BulkOperationCompleted += (_, _) => ResetGridVirtualization();
        _trayIcon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)Resources["TrayIcon"];
    }

    /// <summary>Grid view's third-party VirtualizingWrapPanel doesn't reliably recover its
    /// internal container/recycling state after a bulk multi-select operation touches several
    /// sounds in VisibleSounds at once — even a full Clear()+rebuild of the bound collection isn't
    /// enough (symptom: tiles stop rendering, only fixed today by disabling virtualization
    /// entirely). Detaching and reattaching ItemsSource forces WPF to tear down and regenerate the
    /// whole panel from scratch, which single-sound mutations never needed and never triggered.
    /// Reassigning the same collection reference afterward doesn't lose live updates — the
    /// ItemsControl keeps listening to that ObservableCollection's own CollectionChanged directly,
    /// independent of whether the reference arrived via the original {Binding VisibleSounds} or
    /// this direct reassignment.</summary>
    private void ResetGridVirtualization()
    {
        var itemsSource = SoundGridItemsControl.ItemsSource;
        SoundGridItemsControl.ItemsSource = null;
        SoundGridItemsControl.ItemsSource = itemsSource;
    }

    /// <summary>Keeps the native min/max/close title bar (per the redesign brief — no custom-
    /// drawn chrome, that's a much bigger and riskier undertaking than this shell pass calls
    /// for) but asks Windows to paint it dark so it doesn't look like a stray light-mode strip
    /// glued onto an otherwise dark app. Purely cosmetic and best-effort — never worth crashing
    /// startup over on an older Windows build that doesn't support the attribute.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var useDarkMode = 1;

            // Attribute 20 is correct on Windows 10 20H1+ and Windows 11; older 10 builds used 19.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeCurrent, ref useDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref useDarkMode, sizeof(int));
            }
        }
        catch
        {
            // Best-effort cosmetic tweak — the title bar just stays the OS default light strip.
        }
    }

    private const int DwmwaUseImmersiveDarkModeCurrent = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync().ConfigureAwait(true);
        _viewModel.RestoreLayout(this);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await _viewModel.SaveLayoutAsync(this).ConfigureAwait(true);

        // TaskbarIcon isn't a WPF visual, so WPF's own teardown doesn't dispose it — leaving it
        // undisposed can leave a "ghost" icon in the tray until the user hovers over it.
        _trayIcon.Dispose();
    }

    /// <summary>Minimizing (native title bar button or Windows key shortcut both route through
    /// WindowState the same way) hides the window instead when "Minimize to tray" is on, leaving
    /// the app running with only the tray icon visible. This is a separate path from closing —
    /// Window_Closing above is untouched, so the X button still quits normally either way.</summary>
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
        {
            Hide();
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayShowMenuItem_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    // Close() (not a raw Application.Shutdown()) so this still goes through Window_Closing above
    // — same layout-save-on-exit behavior as quitting via the title bar, even though the window
    // may currently be Hidden rather than visible.
    private void TrayExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Ctrl+K jumps focus to the search box (and selects any existing text, so typing
    /// immediately replaces it) — the shortcut the top bar's search field advertises inline.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
        }
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

    private async void NowPlayingProgressBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = (ProgressBar)sender;
        if (bar.ActualWidth <= 0 || bar.Maximum <= 0) return;

        var fraction = Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1);
        var positionSeconds = fraction * bar.Maximum;
        await _viewModel.NowPlayingSeekToCommand.ExecuteAsync(positionSeconds).ConfigureAwait(true);
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

            // Under grid virtualization, a scroll DURING the drag (DoDragDrop pumps its own
            // message loop while blocked above, so one can sneak in) can recycle this exact
            // container to a different sound before the call returns — clearing Opacity on it at
            // that point would silently mutate some other, unrelated tile instead. Only touch it
            // if it's still actually showing the sound that was being dragged.
            if (sourceButton.DataContext == button)
            {
                sourceButton.ClearValue(OpacityProperty);
            }

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
        if (sender is not Button { Template: { } template, DataContext: SoundButtonViewModel button } source) return;
        if (template.FindName("Root", source) is not Border root) return;

        ClearDragHighlight();
        root.BorderBrush = (Brush)FindResource("AccentBrush");
        root.BorderThickness = new Thickness(3);
        _dragHighlightedBorder = root;
        _dragHighlightedFor = button;
    }

    private void SoundButton_DragLeave(object sender, DragEventArgs e) => ClearDragHighlight();

    private void ClearDragHighlight()
    {
        if (_dragHighlightedBorder is null) return;

        // Same container-recycling guard as the drag-source cleanup above — DataContext is an
        // inherited property, so this Border reflects whichever sound its container currently
        // holds, which may no longer be the one that was actually highlighted if a scroll
        // recycled it mid-drag.
        if (_dragHighlightedBorder.DataContext == _dragHighlightedFor)
        {
            _dragHighlightedBorder.ClearValue(Border.BorderBrushProperty);
            _dragHighlightedBorder.ClearValue(Border.BorderThicknessProperty);
        }

        _dragHighlightedBorder = null;
        _dragHighlightedFor = null;
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

    private void VoiceTileButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
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

    private async void DetailsFolder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: SoundButtonViewModel button, SelectedItem: FolderOption option }) return;
        await _viewModel.MoveSoundToFolderAsync(button, option.Id).ConfigureAwait(true);
    }

    private async void DetailsTags_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SoundButtonViewModel button } textBox) return;
        await _viewModel.SetSoundTagsAsync(button, textBox.Text).ConfigureAwait(true);
        button.NotifyTagsChanged();
    }

    private async void DetailsSetHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoundButtonViewModel button }) return;

        var dialog = new HotkeyCaptureDialog(button.DisplayName, button.Sound.Hotkey) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.SetSoundHotkeyAsync(button, dialog.Result).ConfigureAwait(true);
        }
    }

    private async void DetailsVolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { DataContext: SoundButtonViewModel button } slider) return;
        await _viewModel.SetSoundVolumeAsync(button, (float)slider.Value).ConfigureAwait(true);
    }

    private async void DetailsPlaybackMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: SoundButtonViewModel button, SelectedItem: PlaybackMode mode }) return;
        await _viewModel.SetSoundPlaybackModeAsync(button, mode).ConfigureAwait(true);
    }

    private async void DetailsRoute_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: SoundButtonViewModel button, SelectedItem: RouteOption option }) return;
        await _viewModel.SetSoundOutputRouteOverrideAsync(button, option.Route).ConfigureAwait(true);
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

    /// <summary>Bulk counterpart of PopulateMoveToFolderMenu — same Unfiled/separator/folder-list
    /// shape, but this button's ContextMenu IS the folder list directly (no nested "Move to
    /// Folder" submenu item to fill, since there's only one action this button offers), and it
    /// targets the whole current selection via BulkMoveSelectedToFolderAsync rather than one
    /// sound. Opened the same way SoundsMenuButton_Click already opens its own menu, since this
    /// is a plain toolbar-style button click, not a right-click ContextMenuOpening.</summary>
    private void BulkMoveToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu) return;

        menu.Items.Clear();

        var unfiledItem = new MenuItem { Header = "Unfiled" };
        unfiledItem.Click += async (_, _) => await _viewModel.BulkMoveSelectedToFolderAsync(null).ConfigureAwait(true);
        menu.Items.Add(unfiledItem);

        if (_viewModel.Folders.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        foreach (var folder in _viewModel.Folders)
        {
            var folderItem = new MenuItem { Header = folder.Name };
            folderItem.Click += async (_, _) => await _viewModel.BulkMoveSelectedToFolderAsync(folder.Id).ConfigureAwait(true);
            menu.Items.Add(folderItem);
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
