using System;
using System.Globalization;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;
using WinQEMU.Text;
using WinQEMU.ViewModels;

namespace WinQEMU
{
    public sealed partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly string _dropTargetStatus;
        private bool _xamlDragSeen;
        private bool _closeConfirmed;
        private int? _attachedProcessId;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            // Window has no DataContext in WinUI 3, so the root element carries it.
            RootGrid.DataContext = _viewModel;

            ConsoleHost.Initialize(WinRT.Interop.WindowNative.GetWindowHandle(this));
            ConsoleHost.Log = _viewModel.AppendDiagnostic;
            // An encrypted machine's password, asked when something needs it (MainWindow.Encryption.cs).
            _viewModel.Manager.DiskPasswordPrompt = PromptDiskPasswordAsync;

            // Files dragged from Explorer onto the console are copied to the guest's desktop. Queued: the
            // drop arrives inside OLE's drag loop, too early for a dialog. The outcome is said each time a
            // console is shown: the journal belongs to the selected machine and starts empty.
            _dropTargetStatus = ConsoleHost.EnableFileDrop(() => _viewModel.CanSendFiles,
                files => DispatcherQueue.TryEnqueue(() => _ = SendFilesAsync(files)));
            // Keys captured by the host travel to the guest over the control channel, because an
            // embedded console window cannot receive them.
            ConsoleHost.KeyForwarder = _viewModel.ForwardKeysAsync;
            ConsoleHost.PointerForwarder = _viewModel.ForwardPointerAsync;
            ConsoleHost.PreferredSizeChanged += _viewModel.RequestGuestResolution;

            // Keep the newest QEMU output in view without stealing focus.
            _viewModel.LogUpdated += OnLogUpdated;
            _viewModel.ConsoleTargetChanged += OnConsoleTargetChanged;

            ConsoleHost.GuestSizeChanged += (_, _) => UpdateGuestSizeText();
            ConsoleHost.ConsoleClicked += (_, _) => _openMenu?.Hide();

            // The snapshot tree is drawn in code (boxes and lines), redrawn when the tree or the selection changes.
            _viewModel.Snapshots.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(VmSnapshotsViewModel.Tree) or nameof(VmSnapshotsViewModel.Selected))
                    DrawSnapshotTree();
            };
            SnapshotTreeDrawing.NodeClicked += OnSnapshotNodeClicked;

            CpuChart.FixedMax = 100;
            CpuChart.Format = v => string.Format(CultureInfo.CurrentCulture, ShellText.PercentFormat, v.ToString("0", CultureInfo.CurrentCulture));
            MemoryChart.Format = Sizes.Format;
            DiskChart.Format = v => Sizes.Format(v) + "/s";
            NetworkChart.Format = v => Sizes.Format(v) + "/s";
            _viewModel.MetricsSampled += (_, _) => DrawMetrics();
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedHistory))
                    DrawMetrics();
            };
            // Only the session can answer this, and only over QMP.
            ConsoleHost.GuestSizeProvider = async () =>
            {
                var session = _viewModel.SelectedSession;
                return session is { IsRunning: true } ? await session.QueryGuestScreenSizeAsync() : (0, 0);
            };
            // Raised for F11 only, which the host keeps for itself rather than forwarding: with the guest
            // holding the keyboard there is otherwise no key left to leave full screen with.
            ConsoleHost.FullScreenRequested += (_, _) => SetConsoleFullScreen(!_consoleFullScreen);
            ScaleModeBox.ItemsSource = new[] { ShellText.ScaleFit, ShellText.ScaleActual };
            ScaleModeBox.SelectedIndex = 0;
            UpdateGuestSizeText();

            RootGrid.SizeChanged += (_, _) => ApplyAdaptiveLayout();

            _fullScreenBarTimer = DispatcherQueue.CreateTimer();
            _fullScreenBarTimer.Interval = TimeSpan.FromMilliseconds(100);
            _fullScreenBarTimer.Tick += (_, _) => UpdateFullScreenBar();

            RootGrid.Loaded += async (_, _) =>
            {
                ApplyMinimumSize();
                NameInTooltips(MachineToolbar);
                if (FullScreenBar.Child is Panel fullScreenCommands)
                    NameInTooltips(fullScreenCommands);
                ApplyAdaptiveLayout();
                await InitializeOnceAsync();
            };
            _viewModel.BackgroundConsoleStarted += processId => _ = ConsoleHost.AdoptAsync(processId);

            _sessionEnd = new SessionEndWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this),
                () => _viewModel.Manager.LiveSessions.Any(),
                () => DispatcherQueue.TryEnqueue(() => _ = StopMachinesForWindowsAsync()),
                ending => DispatcherQueue.TryEnqueue(() => OnWindowsSessionEnded(ending)));
            if (!_sessionEnd.IsInstalled)
                _viewModel.AppendDiagnostic("Windows: cannot hold its shutdown; running machines would lose their power with it.");

            // The window carried no icon at all — WM_GETICON returned nothing — so the title bar fell
            // back to whatever the shell had cached for the package. Setting it explicitly makes the
            // title bar, Alt+Tab and the taskbar show the application's own icon.
            AppWindow.SetIcon("Assets/WinQEMU.ico");

            ApplyTheme();
            _viewModel.ThemeChanged += ApplyTheme;
            _viewModel.LanguageChanged += ApplyLanguage;

            AppWindow.Closing += OnAppWindowClosing;
        }

        /// <summary>
        /// The narrowest window the layout was checked at, 800 x 600 (at 150 % on the user's 1920 x 1200 screen): below
        /// it the toolbar, the summary and the wizard had nothing left to give. In physical pixels, hence the scale.
        /// </summary>
        private void ApplyMinimumSize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                return;
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            presenter.PreferredMinimumWidth = (int)(800 * scale);
            presenter.PreferredMinimumHeight = (int)(600 * scale);
        }

        // ---- Title bar theme ----

        // The title bar is drawn by DWM, not by XAML, and a standard one stays light until it is
        // told otherwise: the window came up with a light title bar above dark content while
        // Windows itself was in dark mode. It follows the theme chosen in the settings, Windows' by default.
        private void ApplyTitleBarTheme()
        {
            AppWindow.TitleBar.PreferredTheme = _viewModel.SelectedTheme switch
            {
                "light" => TitleBarTheme.Light,
                "dark" => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
        }

        /// <summary>The theme chosen in the settings (Paramètres), at once: the content, the title bar and the dialogs shown from now on.</summary>
        private void ApplyTheme()
        {
            RootGrid.RequestedTheme = _viewModel.SelectedTheme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            ApplyTitleBarTheme();
            // What is drawn from code takes its colours once (ThemeBrushes): drawn again in the new theme.
            DispatcherQueue.TryEnqueue(() =>
            {
                DrawSnapshotTree();
                DrawMetrics();
            });
        }

        // ---- Console embedding ----

        private async void OnConsoleTargetChanged(object? sender, EventArgs e)
        {
            var processId = _viewModel.ConsoleProcessId;

            if (processId == _attachedProcessId)
                return;

            _attachedProcessId = processId;

            if (processId is null)
            {
                ConsoleHost.Detach();
                return;
            }

            await ConsoleHost.AttachAsync(processId.Value);
            _viewModel.AppendDiagnostic(_dropTargetStatus);
        }

        private void OnSummaryViewClick(object sender, RoutedEventArgs e) => ShowView(console: false);

        private void OnConsoleViewClick(object sender, RoutedEventArgs e) => ShowView(console: true);

        /// <summary>
        /// Switches between the summary and the console.
        /// </summary>
        /// <remarks>
        /// Toggling visibility keeps both views loaded, unlike the TabView this replaced, which
        /// unloaded the inactive tab. The console host therefore no longer receives Unloaded when it
        /// goes off screen and has to be suspended and resumed explicitly.
        /// </remarks>
        private void ShowView(bool console)
        {
            SummaryViewButton.IsChecked = !console;
            ConsoleViewButton.IsChecked = console;

            SummaryView.Visibility = console ? Visibility.Collapsed : Visibility.Visible;
            ConsoleView.Visibility = console ? Visibility.Visible : Visibility.Collapsed;

            if (!console)
            {
                ConsoleHost.Suspend();
                ApplyAdaptiveLayout();
                return;
            }

            // Queued so the console host has been measured before it positions the native window;
            // straight after the visibility change its ActualWidth is still zero.
            DispatcherQueue.TryEnqueue(() =>
            {
                ConsoleHost.Resume();
                ConsoleHost.SynchronizeBounds();
                ConsoleHost.FocusGuest();
            });
        }

        /// <summary>
        /// Dialog body that gives the button row room to breathe.
        /// </summary>
        /// <remarks>
        /// ContentDialog splits its width into equal columns, one per button, and sizes itself to its
        /// content. A short message therefore produced a narrow dialog whose buttons trimmed their own
        /// labels to "Supprimer la VM et se…". Stating a minimum width on the body widens the dialog,
        /// and with it every column.
        /// </remarks>
        private static TextBlock DialogBody(string text, double minWidth = 460) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = minWidth
        };

        /// <summary>The selected machine's last two minutes; empty charts when it is not running.</summary>
        private void DrawMetrics()
        {
            var history = _viewModel.SelectedHistory;
            var none = Array.Empty<double>();
            CpuChart.Show(new[] { (ShellText.ChartCpu, history?.Cpu ?? none) });
            MemoryChart.FixedMax = _viewModel.SelectedVm is { } vm ? vm.MemoryMb * 1024d * 1024d : null;
            MemoryChart.Show(new[] { (ShellText.ChartMemory, history?.MemoryBytes ?? none) });
            DiskChart.Show(new[] { (ShellText.ChartRead, history?.DiskRead ?? none), (ShellText.ChartWrite, history?.DiskWrite ?? none) });
            NetworkChart.Show(new[] { (ShellText.ChartReceived, history?.NetworkReceived ?? none), (ShellText.ChartSent, history?.NetworkSent ?? none) },
                history is { HasNetwork: false } && history.Count > 5
                    ? ShellText.ChartNetworkNoAgent
                    : null);
        }

        private void DrawSnapshotTree() =>
            SnapshotTreeDrawing.Show(_viewModel.Snapshots.Tree, _viewModel.Snapshots.Snapshots, _viewModel.Snapshots.Selected?.Name);

        /// <summary>A snapshot becomes the one the buttons act on; a linked clone is opened.</summary>
        private void OnSnapshotNodeClicked(Core.Qemu.SnapshotTreeNode node)
        {
            // Selected through the list, the one place selection originates (a clone hidden by the search
            // stays where it is).
            if (node.Clone is { } clone)
            {
                if (_viewModel.Items.FirstOrDefault(i => ReferenceEquals(i.Config, clone)) is { } listed)
                    _viewModel.SelectedItem = listed;
            }
            else if (_viewModel.Snapshots.Snapshots.FirstOrDefault(s => s.Name == node.Title) is { } item)
                _viewModel.Snapshots.Selected = item;
        }

        private void OnScaleModeChanged(object sender, SelectionChangedEventArgs e)
        {
            // The list refilled in the new language passes through no selection: nothing to apply, nor focus to move.
            if (_applyingLanguage)
                return;
            ApplyScaleMode();
            // The box would otherwise keep the keyboard, and the keys meant for the guest change its mode.
            ConsoleHost?.FocusGuest();
        }

        /// <summary>The mode chosen in the box; full screen sets its own and this puts the user's back.</summary>
        private void ApplyScaleMode()
        {
            if (ConsoleHost is null)
                return;

            ConsoleHost.ScaleMode = ScaleModeBox.SelectedIndex switch
            {
                1 => Controls.ConsoleScaleMode.Actual,
                _ => Controls.ConsoleScaleMode.Fit
            };
        }

        /// <summary>Reports the guest's own resolution, which "Taille réelle" sizes the console to.</summary>
        private void UpdateGuestSizeText()
        {
            var (width, height) = ConsoleHost.GuestSize;
            GuestSizeText.Text = width <= 0 || height <= 0 ? string.Empty
                : ConsoleHost.IsPointerImprecise
                    ? string.Format(CultureInfo.CurrentCulture, ShellText.GuestSizeScaled, width, height)
                    : $"{width} × {height}";
        }

        private bool _consoleFullScreen;

        /// <summary>
        /// Gives the console the whole window.
        /// </summary>
        /// <remarks>
        /// Full screen alone would only enlarge the window: the console shares it with the inventory and
        /// the header bands, so those are collapsed too, otherwise the guest gains a few hundred pixels
        /// and keeps the chrome around it.
        /// </remarks>
        private void OnToggleFullScreenClick(object sender, RoutedEventArgs e) => SetConsoleFullScreen(!_consoleFullScreen);

        private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SetConsoleFullScreen(!_consoleFullScreen);
        }

        /// <summary>
        /// Watches the pointer while in full screen. Polled rather than evented: over the guest the
        /// pointer belongs to QEMU's window, in another process, so XAML never hears of it.
        /// </summary>
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _fullScreenBarTimer;
        private bool _fullScreenBarShown;

        private void UpdateFullScreenBar()
        {
            if (!_consoleFullScreen || !Core.Interop.NativeMethods.GetCursorPos(out var pointer))
                return;

            var top = AppWindow.Position.Y;
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            if (!_fullScreenBarShown)
            {
                // The very top row of pixels, the same gesture as VMware's and as Windows' own full screen.
                if (pointer.Y <= top + 1)
                    ShowFullScreenBar(true);
                return;
            }

            // Kept while the pointer is on the bar or just below it, and while one of its menus is open.
            var barBottom = top + (int)Math.Round((FullScreenBar.ActualHeight + 24) * scale);
            if (pointer.Y > barBottom && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count == 0)
                ShowFullScreenBar(false);
        }

        private void ShowFullScreenBar(bool show)
        {
            if (_fullScreenBarShown == show && (FullScreenBar.Visibility == Visibility.Visible) == show)
                return;

            _fullScreenBarShown = show;
            FullScreenBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ConsoleHost.OverlayCornerRadius = FullScreenBar.CornerRadius.TopLeft;
            ConsoleHost.Overlay = show ? FullScreenBarBounds() : null;

            // Back to the guest once the bar is gone, so typing resumes without a click.
            if (!show && _consoleFullScreen)
                ConsoleHost.FocusGuest();
        }

        /// <summary>The bar's rectangle over the console, measured now that it is shown.</summary>
        private Windows.Foundation.Rect FullScreenBarBounds()
        {
            FullScreenBar.UpdateLayout();
            return FullScreenBar.TransformToVisual(ConsoleHost)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, FullScreenBar.ActualWidth, FullScreenBar.ActualHeight));
        }

        private void SetConsoleFullScreen(bool fullScreen)
        {
            _consoleFullScreen = fullScreen;

            // The console has to be showing for this to mean anything.
            if (fullScreen)
                ShowView(console: true);

            // Everything but the guest goes, down to the paddings and rounded corners: the user asked for
            // the guest alone on the monitor, as VMware does. The way back is F11, or the bar that
            // appears when the pointer touches the top of the screen.
            var collapsed = fullScreen ? Visibility.Collapsed : Visibility.Visible;
            NotificationsPanel.Visibility = collapsed;
            ViewSwitcher.Visibility = collapsed;
            StatePanel.Visibility = collapsed;
            ConsoleBar.Visibility = collapsed;
            SidebarColumn.Width = fullScreen ? new GridLength(0) : new GridLength(320);
            HeaderRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
            HeaderRow.MinHeight = fullScreen ? 0 : 64;
            SubHeaderRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
            // The footer belongs to the sidebar column; a zero-width column still lends it its height.
            FooterRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
            ShellGrid.Padding = fullScreen ? new Thickness(0) : new Thickness(12, 8, 12, 12);
            ShellGrid.ColumnSpacing = fullScreen ? 0 : 12;
            ContentGrid.RowSpacing = fullScreen ? 0 : 12;
            ConsoleView.RowSpacing = fullScreen ? 0 : 8;
            ConsoleFrame.CornerRadius = fullScreen ? new CornerRadius(0) : new CornerRadius(8);
            // Letterbox bars black, as on any full-screen video, rather than the theme's background.
            RootGrid.Background = fullScreen ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) : null;

            AppWindow.SetPresenter(fullScreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            // The default presenter comes back new, without the minimum size.
            if (!fullScreen)
                ApplyMinimumSize();

            FullScreenTitle.Text = _viewModel.SelectedVm?.Name ?? string.Empty;
            ShowFullScreenBar(false);
            if (fullScreen)
                _fullScreenBarTimer.Start();
            else
                _fullScreenBarTimer.Stop();

            // Fit hands the whole monitor to QEMU, which passes its size on to the guest: a virtio-gpu
            // guest adopts the monitor's resolution, any other is letterboxed by QEMU itself.
            if (fullScreen)
                ConsoleHost.ScaleMode = Controls.ConsoleScaleMode.Fit;
            else
                ApplyScaleMode();

            if (!fullScreen)
                ApplyAdaptiveLayout();

            // Queued so the new layout has been measured before the native window is repositioned.
            DispatcherQueue.TryEnqueue(() =>
            {
                ConsoleHost.SynchronizeBounds();
                ConsoleHost.FocusGuest();
            });
        }

        /// <summary>
        /// Shows a dialog with the console hidden. A child HWND draws above all XAML content, so an
        /// embedded console would otherwise cover the dialog completely.
        /// </summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            ConsoleHost.Suspend();
            // A dialog is shown in a popup, outside the window's tree: it takes the window's theme only if told.
            dialog.RequestedTheme = RootGrid.RequestedTheme;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                ConsoleHost.Resume();
            }
        }

        // ---- Handlers ----

        private void OnLogUpdated(object? sender, EventArgs e)
        {
            // Queued so the TextBlock has been re-measured and ScrollableHeight reflects the new text.
            DispatcherQueue.TryEnqueue(() =>
                LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, disableAnimation: true));
        }

        /// <summary>
        /// Reflows the summary tab for the available width.
        /// </summary>
        /// <remarks>
        /// Widths here are logical pixels, so a 1280-pixel window on a 150% display is really 853 wide.
        /// A fixed sidebar plus a fixed resources panel consumed all of that and the detail fields
        /// collapsed to zero width. Below the threshold the sidebar narrows and the resources panel
        /// moves under the details rather than beside them.
        /// </remarks>
        private void ApplyAdaptiveLayout()
        {
            // Full screen owns the layout; a resize there (entering it is one) must not bring the sidebar back.
            if (_consoleFullScreen)
                return;

            // Two steps, measured at 150 % on the user's 1920 x 1200 screen: at 1024 px the summary, squeezed between
            // the list and the resources, clipped a disk's file name, its lists and its buttons, so the resources go
            // under the summary below 1200 px; below 1000 the list narrows too, to the width its title still fits in
            // (at 240 it read "Machines virtuel…").
            const double narrowThreshold = 1000;
            const double stackedThreshold = 1200;
            var narrow = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < narrowThreshold;
            var stacked = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < stackedThreshold;

            SidebarColumn.Width = new GridLength(narrow ? 280 : 320);
            FitMachineToolbar();

            // The summary tab's content is only realised while that tab is selected, so these may not
            // exist yet when a resize happens with the console showing.
            if (ResourcesColumn is null || ResourcesPanel is null)
                return;

            ResourcesColumn.Width = new GridLength(stacked ? 0 : 320);
            Grid.SetColumn(ResourcesPanel, stacked ? 0 : 1);
            Grid.SetRow(ResourcesPanel, stacked ? 1 : 0);
            Grid.SetRowSpan(ResourcesPanel, stacked ? 1 : 3);
            ResourcesPanel.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        }

        /// <summary>How many groups of the toolbar are folded into "Plus": 0, 1 (the library's) or 2 (and Son, Envoyer).</summary>
        private int _foldedGroups;

        /// <summary>
        /// The toolbars show icons alone (ToolbarIconButton in App.xaml): each tooltip then says which command it is,
        /// its name in bold above what it does. Built as the tooltip opens, so Pause/Reprendre and the sound's
        /// name are the current ones.
        /// </summary>
        private static void NameInTooltips(Panel bar)
        {
            foreach (var button in bar.Children.OfType<Control>())
            {
                var description = ToolTipService.GetToolTip(button) as string;
                var tip = new ToolTip();
                tip.Opened += (_, _) =>
                {
                    var name = button is AppBarButton appBarButton
                        ? appBarButton.Label
                        : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button);
                    tip.Content = string.IsNullOrEmpty(description) || description == name
                        ? name
                        : new StackPanel
                        {
                            Spacing = 2,
                            MaxWidth = 320,
                            Children =
                            {
                                new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }
                            }
                        };
                };
                ToolTipService.SetToolTip(button, tip);
            }
        }

        /// <summary>
        /// Folds commands into a "Plus" menu while the toolbar would run under the Réseaux virtuels and
        /// Paramètres buttons, as it did at 1440 px once Son and Envoyer were added. The library's actions go
        /// first (Cloner, Exporter, Supprimer), then Envoyer and Son; the machine's power and USB stay in view.
        /// </summary>
        private void FitMachineToolbar()
        {
            if (ContentGrid.ActualWidth <= 0)
                return;

            // The two round buttons at the right of the band, and their margins.
            const double rightButtons = 92;
            for (var folded = 0; folded <= 2; folded++)
            {
                FoldToolbar(folded);
                MachineToolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                if (MachineToolbar.DesiredSize.Width <= ContentGrid.ActualWidth - rightButtons)
                    break;
            }
        }

        private void FoldToolbar(int groups)
        {
            _foldedGroups = groups;
            var library = groups >= 1 ? Visibility.Collapsed : Visibility.Visible;
            LibrarySeparator.Visibility = library;
            CloneButton.Visibility = library;
            ExportButton.Visibility = library;
            DeleteButton.Visibility = library;
            var guest = groups >= 2 ? Visibility.Collapsed : Visibility.Visible;
            SoundButton.Visibility = guest;
            SendFilesButton.Visibility = guest;
            PullFilesButton.Visibility = guest;
            MoreButton.Visibility = groups > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            if (OnlyClosedItsMenu(sender))
                return;
            var menu = new MenuFlyout();
            void Add(string text, string glyph, bool enabled, RoutedEventHandler click)
            {
                var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
                item.Click += click;
                menu.Items.Add(item);
            }
            if (_foldedGroups >= 2)
            {
                // Opened under "Plus": the menu item is gone by the time the flyout shows.
                Add(_viewModel.SoundLabel + "…", _viewModel.SoundGlyph, _viewModel.CanAdjustSound, (_, _) => ShowSoundFlyout(MoreButton));
                Add(ShellText.MoreSendFiles, "\uE898", _viewModel.CanSendFiles, OnSendFilesClick);
                Add(ShellText.MorePullFiles, "\uE896", _viewModel.CanSendFiles, OnPullFilesClick);
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            Add(ShellText.MoreClone, "\uE8C8", _viewModel.CanClone, OnCloneClick);
            Add(ShellText.MoreExport, "\uEDE1", _viewModel.CanClone, OnExportClick);
            Add(ShellText.MoreDelete, "\uE74D", _viewModel.SelectedVm is not null, OnDeleteClick);
            ShowAboveConsole(menu, MoreButton);
        }

        private void OnDismissErrorClick(InfoBar sender, object args) => _viewModel.ClearError();

        private async void OnNewVmClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.IsQemuAvailable)
                return;

            var dialog = new Dialogs.NewVmDialog(
                _viewModel.Manager,
                WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                XamlRoot = Content.XamlRoot
            };

            // The wizard uses its own button row, so it reports the outcome itself rather than
            // through ContentDialogResult.
            await ShowDialogAsync(dialog);
            if (!dialog.Confirmed)
                return;

            try
            {
                await _viewModel.CreateFromRequestAsync(dialog.BuildRequest());
            }
            catch (Exception ex)
            {
                // Creation touches the filesystem and qemu-img, so it can fail for mundane reasons
                // (permissions, disk full) that the wizard's validation cannot anticipate.
                await ShowDialogAsync(new ContentDialog
                {
                    Title = ShellText.CreateFailedTitle,
                    Content = ex.Message,
                    CloseButtonText = CommonText.Close,
                    XamlRoot = Content.XamlRoot
                });
            }
        }

        /// <summary>
        /// Opens the settings dialog. Also serves the "QEMU introuvable" banner, which used to run its
        /// own folder picker: the dialog reports the outcome of a chosen folder, which a bare picker
        /// could not.
        /// </summary>
        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.SettingsDialog(
                _viewModel,
                WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                XamlRoot = Content.XamlRoot
            };

            await ShowDialogAsync(dialog);
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } target)
                return;

            var box = new TextBox { Header = ShellText.RenameBoxHeader, Text = target.Name };
            var error = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            var body = new StackPanel { Spacing = 8, MinWidth = 420 };
            body.Children.Add(box);
            body.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title = ShellText.RenameTitle,
                Content = body,
                PrimaryButtonText = ShellText.RenameButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            // Checked as it is typed, so Renommer is only offered for a name that will be accepted.
            void Validate()
            {
                var problem = string.Equals(box.Text.Trim(), target.Name, StringComparison.Ordinal) ? null : _viewModel.WhyNameIsRefused(box.Text);
                error.Text = problem ?? string.Empty;
                error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = problem is null && box.Text.Trim().Length > 0;
            }
            box.TextChanged += (_, _) => Validate();
            box.Loaded += (_, _) => box.SelectAll();
            Validate();

            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && box.Text.Trim() != target.Name)
                _viewModel.RenameSelected(box.Text);
        }

        private async void OnVirtualNetworksClick(object sender, RoutedEventArgs e) =>
            await ShowDialogAsync(new Dialogs.VirtualNetworksDialog(_viewModel.Manager) { XamlRoot = Content.XamlRoot });

        // ---- Disks ----

        private async void OnAttachDiskClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var extension in new[] { ".qcow2", ".img", ".raw", ".vhdx", ".vmdk", ".vdi" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await _viewModel.Hardware.AttachExistingDiskAsync(file.Path);
        }

        private async void OnCreateDiskClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is null)
                return;

            var dialog = new Dialogs.NewDiskDialog(_viewModel.Hardware.SuggestedBus)
            {
                XamlRoot = Content.XamlRoot
            };

            await ShowDialogAsync(dialog);
            if (!dialog.Confirmed)
                return;

            await _viewModel.Hardware.CreateDiskAsync(dialog.CapacityBytes, dialog.Format, dialog.Bus);
        }

        /// <summary>The disk a per-row button belongs to, taken from the row it was drawn in.</summary>
        private static DiskConfig? DiskOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as DiskConfig;

        private async void OnGrowDiskClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk)
                return;

            var dialog = new Dialogs.GrowDiskDialog(disk) { XamlRoot = Content.XamlRoot };
            await ShowDialogAsync(dialog);
            if (!dialog.Confirmed)
                return;

            await _viewModel.Hardware.GrowDiskAsync(disk, dialog.CapacityBytes);
        }

        private async void OnConvertDiskClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk)
                return;

            var dialog = new Dialogs.ConvertDiskDialog(disk) { XamlRoot = Content.XamlRoot };
            await ShowDialogAsync(dialog);
            if (!dialog.Confirmed)
                return;

            await _viewModel.Hardware.ConvertDiskAsync(disk, dialog.Format, dialog.Compress);
        }

        /// <summary>
        /// Compacts a disk (Services.DiskCompaction): says why not, or what it does, then rewrites it with a
        /// progress bar and gives the size it went from and to.
        /// </summary>
        private async void OnCompactDiskClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk)
                return;
            if (await _viewModel.Hardware.WhyDiskCannotBeCompactedAsync(disk) is { } reason)
            {
                await ShowProblemAsync(ShellText.CompactNotPossibleTitle, reason);
                return;
            }

            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.CompactConfirmTitle, disk.Id),
                Content = DialogBody(ShellText.CompactConfirmBody),
                PrimaryButtonText = ShellText.CompactButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (choice != ContentDialogResult.Primary)
                return;
            // Asked now: no second dialog can open over the progress one.
            if (!await UnlockSelectedAsync())
                return;

            var bar = new ProgressBar { Maximum = 1, MinWidth = 360 };
            var progressDialog = new ContentDialog { Title = string.Format(CultureInfo.CurrentCulture, ShellText.CompactProgressTitle, disk.Id), Content = bar, XamlRoot = Content.XamlRoot };
            // Shown first, hidden once done: hidden before it opened, it would stay open for good.
            var showing = ShowDialogAsync(progressDialog);
            var result = await _viewModel.Hardware.CompactDiskAsync(disk, new Progress<double>(p => bar.Value = p));
            progressDialog.Hide();
            await showing;
            if (result is null)
                return;
            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.CompactDoneTitle,
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.CompactDoneBody,
                    disk.Id, Sizes.Format(result.BytesBefore), Sizes.Format(result.BytesAfter))),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        private async void OnTrimGuestDisksClick(object sender, RoutedEventArgs e)
        {
            var text = await _viewModel.TrimGuestDisksAsync();
            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.TrimTitle,
                Content = DialogBody(text),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        // ---- Optical drive ----

        private async void OnBrowseSecondaryIsoClick(object sender, RoutedEventArgs e)
        {
            var path = await PickIsoAsync();
            if (path is null || _viewModel.SelectedVm is null)
                return;

            // Not a live change: the second drive is only built at launch, unlike the primary one.
            _viewModel.SelectedVm.SecondaryIsoPath = path;
        }

        private async Task<string?> PickIsoAsync()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".iso");
            picker.FileTypeFilter.Add(".img");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private async void OnBrowseIsoClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".iso");
            picker.FileTypeFilter.Add(".img");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            // Applied straight away: on a running machine that swaps the medium, otherwise it is just
            // recorded for the next start. Either way the user chose a file and expects it to take.
            await _viewModel.ChangeIsoAsync(file.Path);
        }

        private async void OnInsertIsoClick(object sender, RoutedEventArgs e) =>
            await _viewModel.ChangeIsoAsync(_viewModel.SelectedVm?.IsoPath);

        // ---- Snapshots ----

        private async void OnRefreshSnapshotsClick(object sender, RoutedEventArgs e) =>
            await _viewModel.Snapshots.RefreshAsync();

        private async void OnCreateSnapshotClick(object sender, RoutedEventArgs e) =>
            await _viewModel.Snapshots.CreateAsync();

        private async void OnLiveSnapshotClick(object sender, RoutedEventArgs e)
        {
            // The overlay changes which file the machine writes to, so the descriptor is rewritten. Said
            // plainly beforehand, because the disk layout on disk is not what it was afterwards.
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.LiveSnapshotTitle,
                Content = DialogBody(ShellText.LiveSnapshotBody),
                PrimaryButtonText = ShellText.LiveSnapshotButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Snapshots.CreateLiveSnapshotAsync();
        }

        private async void OnMemorySnapshotClick(object sender, RoutedEventArgs e) =>
            await _viewModel.Snapshots.CreateMemorySnapshotAsync();

        private async void OnResumeSnapshotClick(object sender, RoutedEventArgs e)
        {
            var snapshot = _viewModel.Snapshots.Selected;
            if (snapshot is null)
                return;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.ResumeSnapshotTitle, snapshot.Name),
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.ResumeSnapshotBody, snapshot.CreatedText)),
                PrimaryButtonText = CommonText.Resume,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Snapshots.ResumeAsync(snapshot);
        }

        // ---- Live-snapshot overlays ----

        /// <summary>
        /// Explains a refusal before any confirmation is asked: confirming something that is then refused
        /// would make the user decide for nothing.
        /// </summary>
        private async Task<bool> RefuseIfOverlayLockedAsync(DiskConfig disk)
        {
            if (_viewModel.SelectedVm is not { } vm || _viewModel.Manager.WhyOverlayCannotBeMerged(vm, disk) is not { } reason)
                return false;

            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.MergeLockedTitle,
                Content = DialogBody(reason),
                CloseButtonText = CommonText.Close,
                XamlRoot = Content.XamlRoot
            });
            return true;
        }

        private async void OnDiscardOverlayClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk || _viewModel.SelectedVm is not { } vm)
                return;

            // Allowed either way; what happens to the files differs, so the dialog says which case this is.
            var keptForClone = _viewModel.Manager.ProtectedImages(vm.ResolvePath(disk.BackingFileName));

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.DiscardOverlayTitle, disk.Id),
                Content = DialogBody(
                    (keptForClone
                        ? string.Format(CultureInfo.CurrentCulture, ShellText.DiscardOverlayKeptBody, disk.BackingFileName)
                        : string.Format(CultureInfo.CurrentCulture, ShellText.DiscardOverlayBody, disk.FileName, disk.BackingFileName) +
                          // Stacked live snapshots: the image underneath is itself an overlay, undone the same way.
                          (disk.OverlayDepth > 1
                              ? " " + string.Format(CultureInfo.CurrentCulture, ShellText.DiscardOverlayDeeper, disk.OverlayDepth - 1)
                              : string.Empty)) +
                    "\n\n" + ShellText.DiscardOverlayLoss),
                PrimaryButtonText = ShellText.DiscardOverlayButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Hardware.DiscardOverlayAsync(disk);
        }

        private async void OnCommitOverlayClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk || await RefuseIfOverlayLockedAsync(disk))
                return;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.CommitOverlayTitle, disk.Id),
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.CommitOverlayBody, disk.FileName, disk.BackingFileName)),
                PrimaryButtonText = ShellText.CommitOverlayButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Hardware.CommitOverlayAsync(disk);
        }

        private async void OnRestoreSnapshotClick(object sender, RoutedEventArgs e)
        {
            var snapshot = _viewModel.Snapshots.Selected;
            if (snapshot is null)
                return;

            // Restoring throws away everything written since, which no undo can recover.
            var partial = snapshot.IsPartial
                ? "\n\n" + ShellText.RestorePartial
                : string.Empty;

            // The list and the history it shows change differently in that case, so it is said up front.
            var keptForClone = snapshot.IsLocked
                ? "\n\n" + ShellText.RestoreKeptForClone
                : string.Empty;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.RestoreSnapshotTitle, snapshot.Name),
                Content = DialogBody(
                    string.Format(CultureInfo.CurrentCulture, ShellText.RestoreSnapshotBody, snapshot.CreatedText) + partial + keptForClone),
                PrimaryButtonText = ShellText.RestoreButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Snapshots.RestoreAsync(snapshot);
        }

        private async void OnDeleteSnapshotClick(object sender, RoutedEventArgs e)
        {
            var snapshot = _viewModel.Snapshots.Selected;
            if (snapshot is null)
                return;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.DeleteNamedTitle, snapshot.Name),
                Content = DialogBody(ShellText.DeleteSnapshotBody),
                PrimaryButtonText = CommonText.Delete,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.Primary)
                await _viewModel.Snapshots.DeleteAsync(snapshot);
        }

        private async void OnImportVmClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Manager.DiskService is not { } images)
                return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var extension in Core.Import.VmImporter.Extensions)
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            Core.Import.ImportPlan plan;
            try
            {
                // A few qemu-img runs: quick, but long enough to want the busy cursor of a disabled window.
                plan = await Core.Import.VmImporter.ReadAsync(file.Path, images);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException)
            {
                await ShowDialogAsync(new ContentDialog
                {
                    Title = ShellText.ImportUnreadableTitle,
                    Content = DialogBody($"{file.Path}\n\n{ex.Message}"),
                    CloseButtonText = CommonText.Close,
                    XamlRoot = Content.XamlRoot
                });
                return;
            }

            // The source's own name unless a machine here already has it.
            var name = plan.Name.Trim();
            if (_viewModel.VMs.Any(vm => string.Equals(vm.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
                name += " " + ShellText.ImportedSuffix;

            // The dialog runs the import itself and reports failures in place; on success the view model
            // has already selected the new machine.
            await ShowDialogAsync(new Dialogs.ImportVmDialog(plan, name, _viewModel.ImportAsync)
            {
                XamlRoot = Content.XamlRoot
            });
        }

        /// <summary>
        /// The USB menu: the host's devices, checked when the running machine holds them, then those to
        /// connect at every start. Built on each click from Windows and from QEMU, since a device can be
        /// plugged, unplugged or rebound to WinUSB at any time.
        /// </summary>
        /// <summary>
        /// The machine's sound on this PC: cut, and its volume, as in Windows' volume mixer. Built on each
        /// click, like the USB menu, for the machine selected now.
        /// </summary>
        private void OnSoundClick(object sender, RoutedEventArgs e)
        {
            if (!OnlyClosedItsMenu(sender))
                ShowSoundFlyout(sender as FrameworkElement ?? SoundButton);
        }

        private void ShowSoundFlyout(FrameworkElement target)
        {
            if (_viewModel.SelectedVm is not { } vm)
                return;

            var on = new ToggleSwitch
            {
                Header = ShellText.SoundSwitchHeader,
                OnContent = ShellText.SoundOn,
                OffContent = ShellText.SoundOff,
                IsOn = !vm.AudioMuted
            };
            on.Toggled += (_, _) => vm.AudioMuted = !on.IsOn;
            var volume = new Slider
            {
                Header = ShellText.SoundVolume,
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 1,
                Value = vm.AudioVolume,
                Width = 260
            };
            volume.ValueChanged += (_, args) => vm.AudioVolume = (int)Math.Round(args.NewValue);
            var output = _viewModel.AudioOutputChoices.FirstOrDefault(c => c.Id == _viewModel.SelectedAudioOutputId)?.Name
                         ?? ShellText.AudioDefaultOutput;
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(on);
            panel.Children.Add(volume);
            panel.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, ShellText.SoundOutputNote, output),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                FontSize = 12,
                Opacity = 0.7
            });

            ShowAboveConsole(new Flyout { Content = panel }, target);
        }

        private Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? _openMenu;
        private FrameworkElement? _closedMenuTarget;
        private DateTime _closedMenuAt;

        /// <summary>
        /// Opens a menu or flyout of the toolbar or of the full-screen bar, which hang over the console, in a
        /// window of its own: Windows draws that above QEMU's, so the guest stays in view behind it.
        /// </summary>
        /// <remarks>
        /// Drawn by XAML in the shell's window, a popup is covered by the console, a native window, and the
        /// console used to be hidden while one was open: the guest vanished for as long as the volume was
        /// being set.
        ///
        /// A click elsewhere in the window closes it and still reaches what was clicked
        /// (OverlayInputPassThroughElement): Son open, a click on USB opens USB, where it used to only close
        /// Son. A click on the guest closes it too (ConsoleClicked).
        /// </remarks>
        private void ShowAboveConsole(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout, FrameworkElement target)
        {
            flyout.ShouldConstrainToRootBounds = false;
            flyout.OverlayInputPassThroughElement = RootGrid;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, flyout))
                    _openMenu = null;
                _closedMenuTarget = target;
                _closedMenuAt = DateTime.UtcNow;
            };
            _openMenu = flyout;
            flyout.ShowAt(target);
        }

        /// <summary>
        /// Whether a click on a menu's own button only closed that menu. The click passes through to the
        /// button once the menu is closed, which would open it again at once instead of toggling it.
        /// </summary>
        private bool OnlyClosedItsMenu(object sender) =>
            ReferenceEquals(sender, _closedMenuTarget) && DateTime.UtcNow - _closedMenuAt < TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Installs the selection's guest tools, after saying what will happen in the guest: without an agent,
        /// WinQEMU types at a Linux guest's keyboard, which the user must leave alone, and a Windows guest's user
        /// starts the installers from the drive WinQEMU fills.
        /// </summary>
        private async void OnInstallGuestToolsClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.CanInstallGuestTools)
                return;
            var windows = _viewModel.IsSelectedWindowsGuest;
            var agentMissing = _viewModel.GuestToolsState == Core.Services.GuestToolsState.AgentMissing;

            string? iso = null;
            if (windows)
            {
                iso = _viewModel.FindVirtioWinIso() ?? await PickVirtioWinIsoAsync();
                if (iso is null)
                    return;
            }

            var current = _viewModel.SelectedVm?.IsoPath;
            var swap = windows && !string.IsNullOrWhiteSpace(current) && !string.Equals(current, iso, StringComparison.OrdinalIgnoreCase)
                ? " " + string.Format(CultureInfo.CurrentCulture, ShellText.InstallToolsSwap, System.IO.Path.GetFileName(current))
                : string.Empty;
            var text = (windows, agentMissing) switch
            {
                (true, true) =>
                    string.Format(CultureInfo.CurrentCulture, ShellText.InstallToolsWindowsNoAgent, System.IO.Path.GetFileName(iso), swap),
                (true, false) =>
                    string.Format(CultureInfo.CurrentCulture, ShellText.InstallToolsWindowsAgent, System.IO.Path.GetFileName(iso), swap),
                (false, true) =>
                    string.Format(CultureInfo.CurrentCulture, ShellText.InstallToolsLinuxNoAgent, Core.Services.GuestTools.LinuxInstallCommand),
                (false, false) =>
                    ShellText.InstallToolsLinuxAgent
            };

            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.InstallToolsTitle,
                Content = DialogBody(text),
                PrimaryButtonText = !windows && agentMissing ? ShellText.InstallToolsTypeButton : ShellText.InstallToolsButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (choice != ContentDialogResult.Primary)
                return;

            // The console is back: keys typed now reach the guest window that has its focus.
            await Task.Delay(800);
            var problem = await _viewModel.InstallGuestToolsAsync(iso);
            if (problem is not null)
                await ShowProblemAsync(ShellText.InstallToolsFailedTitle, problem);
        }

        /// <summary>
        /// virtio-win's ISO, chosen by the user when none was found. Nothing is downloaded: WinQEMU says where
        /// Red Hat publishes it.
        /// </summary>
        private async Task<string?> PickVirtioWinIsoAsync()
        {
            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.VirtioIsoMissingTitle,
                Content = DialogBody(ShellText.VirtioIsoMissingBody),
                PrimaryButtonText = ShellText.VirtioIsoChooseButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (choice != ContentDialogResult.Primary)
                return null;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".iso");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null || string.IsNullOrEmpty(file.Path))
                return null;
            _viewModel.RememberVirtioWinIso(file.Path);
            return file.Path;
        }

        /// <summary>
        /// Types the text of this PC's clipboard at the guest's keyboard. A long text is confirmed first: it goes
        /// at about thirty characters a second, and every key reaches whatever has the guest's focus.
        /// </summary>
        private async void OnTypeClipboardClick(object sender, RoutedEventArgs e)
        {
            string? text = null;
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    text = await content.GetTextAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // Another program holds the clipboard: said below as empty.
            }
            if (string.IsNullOrEmpty(text))
            {
                await ShowProblemAsync(ShellText.TypeNothingTitle, ShellText.TypeNothingBody);
                return;
            }

            if (text.Length > 300)
            {
                var seconds = Math.Max(1, text.Length / 30);
                var choice = await ShowDialogAsync(new ContentDialog
                {
                    Title = ShellText.TypeClipboardTitle,
                    Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.TypeClipboardBody, text.Length, seconds)),
                    PrimaryButtonText = ShellText.TypeButton,
                    CloseButtonText = CommonText.Cancel,
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                });
                if (choice != ContentDialogResult.Primary)
                    return;
            }

            // The keys go to the guest's focused window: the console's, as it was before the click.
            ConsoleHost.FocusGuest();
            var problem = await _viewModel.TypeIntoGuestAsync(text);
            if (problem is not null)
                await ShowProblemAsync(ShellText.TypeFailedTitle, problem);
        }

        /// <summary>
        /// Copies files of the guest to this PC: chosen in a browser of the guest's folders, then a folder here.
        /// Opened in the Explorer once there.
        /// </summary>
        private async void OnPullFilesClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.CanSendFiles)
                return;
            var browser = new Dialogs.GuestFilesDialog(_viewModel) { XamlRoot = Content.XamlRoot };
            await ShowDialogAsync(browser);
            if (browser.Chosen.Count == 0)
                return;

            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || string.IsNullOrEmpty(folder.Path))
                return;

            string? problem = null;
            try
            {
                if (await _viewModel.PullFilesAsync(browser.Chosen, folder.Path) is { Names.Count: > 0 } result)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{System.IO.Path.Combine(folder.Path, result.Names[0])}\"");
            }
            catch (Exception ex) when (ex is Core.Sharing.GuestFileDropException or InvalidOperationException
                                           or System.IO.IOException or UnauthorizedAccessException or Core.Qemu.GuestAgentException)
            {
                problem = ex.Message;
            }
            if (problem is not null)
                await ShowProblemAsync(ShellText.PullFailedTitle, problem);
        }

        private async void OnSendFilesClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            var paths = files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths is { Count: > 0 })
                await SendFilesAsync(paths);
        }

        private void OnConsoleDragEnter(object sender, DragEventArgs e) => _xamlDragSeen = false;

        private void OnConsoleDragOver(object sender, DragEventArgs e)
        {
            if (!_xamlDragSeen)
            {
                _xamlDragSeen = true;
                _viewModel.AppendDiagnostic($"Console: a drag reached the console through XAML (taken: {_viewModel.CanSendFiles}).");
            }
            if (!_viewModel.CanSendFiles || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ShellText.DropCaption;
        }

        private async void OnConsoleDrop(object sender, DragEventArgs e)
        {
            if (!_viewModel.CanSendFiles || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
                await SendFilesAsync(paths);
        }

        private async Task SendFilesAsync(System.Collections.Generic.IReadOnlyList<string> paths)
        {
            string message;
            try
            {
                await _viewModel.SendFilesAsync(paths);
                return;
            }
            catch (Exception ex) when (ex is Core.Sharing.GuestFileDropException or InvalidOperationException
                                           or System.IO.IOException or UnauthorizedAccessException)
            {
                message = ex.Message;
            }
            catch (Core.Qemu.GuestAgentException ex)
            {
                message = string.Format(CultureInfo.CurrentCulture, ShellText.SendAgentError, ex.Message);
            }

            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.SendFailedTitle,
                Content = DialogBody(message),
                CloseButtonText = CommonText.Close,
                XamlRoot = Content.XamlRoot
            });
        }

        private async void OnUsbClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is null || OnlyClosedItsMenu(sender))
                return;

            var entries = await _viewModel.GetUsbEntriesAsync();
            var running = _viewModel.IsSelectedRunning;
            var menu = new MenuFlyout();

            if (!running)
            {
                menu.Items.Add(new MenuFlyoutItem
                {
                    Text = ShellText.UsbStoppedHint,
                    IsEnabled = false
                });
            }

            foreach (var entry in entries.Where(candidate => candidate.Host is not null))
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = $"{entry.Name} ({entry.IdText}){UsbStatus(entry)}",
                    IsChecked = entry.Connected,
                    // Offered even when not on WinUSB: the attempt explains what is missing.
                    IsEnabled = running
                };
                item.Click += async (_, _) => await ToggleUsbAsync(entry);
                menu.Items.Add(item);
            }

            if (entries.All(candidate => candidate.Host is null))
                menu.Items.Add(new MenuFlyoutItem { Text = ShellText.UsbNoDevices, IsEnabled = false });

            menu.Items.Add(new MenuFlyoutSeparator());
            var atStart = new MenuFlyoutSubItem { Text = ShellText.UsbAtStart };
            foreach (var entry in entries)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = entry.Host is null ? $"{entry.Name} ({entry.IdText}) — {ShellText.UsbUnplugged}" : $"{entry.Name} ({entry.IdText})",
                    IsChecked = entry.AtStart
                };
                item.Click += (_, _) => _viewModel.SetUsbAtStart(entry, item.IsChecked);
                atStart.Items.Add(item);
            }
            if (atStart.Items.Count == 0)
                atStart.IsEnabled = false;
            menu.Items.Add(atStart);

            var help = new MenuFlyoutItem { Text = ShellText.UsbHelpItem };
            help.Click += async (_, _) => await ShowWinUsbHelpAsync();
            menu.Items.Add(help);

            // Under whichever button opened it: the toolbar's, or the full-screen bar's.
            ShowAboveConsole(menu, sender as FrameworkElement ?? UsbButton);
        }

        private static string UsbStatus(MainViewModel.UsbMenuEntry entry) =>
            entry.Connected ? string.Empty
            : entry.Waiting ? " — " + ShellText.UsbWaiting
            : entry.Host is { IsReachable: false } ? " — " + ShellText.UsbDriverRequired
            : string.Empty;

        private async Task ToggleUsbAsync(MainViewModel.UsbMenuEntry entry)
        {
            var problem = await _viewModel.ToggleUsbAsync(entry);
            if (problem is null)
                return;

            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.UsbNotConnectedTitle,
                Content = DialogBody(problem),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        private async Task ShowWinUsbHelpAsync()
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.UsbHelpTitle,
                // Default width: with a 560 px minimum this text was clipped on the right instead of wrapped.
                Content = DialogBody(ShellText.UsbHelpBody),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        /// <summary>
        /// A card's cable, plugged in or out by the user. Toggled also fires when the template binds the
        /// switch to its card: only a switch that has focus — it was clicked or keyed — is the user's.
        /// </summary>
        private async void OnAdapterConnectedToggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch { FocusState: not FocusState.Unfocused } toggle
                || toggle.DataContext is not NetworkAdapterConfig adapter)
                return;
            adapter.Connected = toggle.IsOn;
            await _viewModel.SetAdapterConnectedAsync(adapter);
        }

        private void OnLanAccessExpanding(Microsoft.UI.Xaml.Controls.Expander sender, ExpanderExpandingEventArgs args) =>
            _viewModel.RefreshLanServices();

        /// <summary>The common services, then any port: what is opened is reachable from the network at once.</summary>
        private void OnOpenServiceClick(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            foreach (var preset in Core.Services.LanAccess.Presets)
            {
                var item = new MenuFlyoutItem { Text = string.Format(CultureInfo.CurrentCulture, ShellText.ServicePresetItem, preset.Label, preset.GuestPort) };
                item.Click += async (_, _) => await ShowProblemAsync(ShellText.ServiceNotOpenedTitle,
                    await _viewModel.OpenServiceAsync(preset.Protocol, preset.GuestPort));
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var other = new MenuFlyoutItem { Text = ShellText.ServiceOtherPort };
            other.Click += async (_, _) => await OpenOtherServiceAsync();
            menu.Items.Add(other);

            // The console is a native window drawn above all XAML, the menu included.
            ConsoleHost.Suspend();
            menu.Closed += (_, _) => ConsoleHost.Resume();
            menu.ShowAt(OpenServiceButton);
        }

        private async Task OpenOtherServiceAsync()
        {
            var port = new NumberBox { Header = ShellText.ServicePortHeader, Minimum = 1, Maximum = 65535, Value = 8080, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var udp = new CheckBox { Content = ShellText.ServiceUdp };
            var body = new StackPanel { Spacing = 12, MinWidth = 360 };
            body.Children.Add(port);
            body.Children.Add(udp);

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.ServiceOpenTitle,
                Content = body,
                PrimaryButtonText = ShellText.ServiceOpenButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (result != ContentDialogResult.Primary || double.IsNaN(port.Value))
                return;

            await ShowProblemAsync(ShellText.ServiceNotOpenedTitle, await _viewModel.OpenServiceAsync(
                udp.IsChecked == true ? PortForwardProtocol.Udp : PortForwardProtocol.Tcp, (int)port.Value));
        }

        private async void OnCloseServiceClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is LanServiceItem item)
                await _viewModel.CloseServiceAsync(item);
        }

        private async void OnAllowFirewallClick(object sender, RoutedEventArgs e) =>
            await ShowProblemAsync(ShellText.FirewallUnchangedTitle, await _viewModel.AllowQemuThroughFirewallAsync());

        private async void OnShareFolderClick(object sender, RoutedEventArgs e) => await ShareFolderAsync(readOnly: false);

        private async void OnShareFolderReadOnlyClick(object sender, RoutedEventArgs e) => await ShareFolderAsync(readOnly: true);

        private async Task ShareFolderAsync(bool readOnly)
        {
            if (_viewModel.SelectedVm is null)
                return;

            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            // Pickers need an owner window; without it they throw in a packaged desktop app.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
                return;

            await ShowProblemAsync(ShellText.ShareFailedTitle, _viewModel.ShareFolder(folder.Path, readOnly));
        }

        private void OnUnshareFolderClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SharedFolderConfig folder)
                _viewModel.UnshareFolder(folder);
        }

        private async Task ShowProblemAsync(string title, string? problem)
        {
            if (problem is null)
                return;

            await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = DialogBody(problem),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        private async void OnCloneClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null || !_viewModel.CanClone)
                return;
            // The copy is encrypted with the source's password, asked before the dialog that runs it opens.
            if (!await UnlockSelectedAsync())
                return;

            // The dialog runs the clone itself and reports failures in place, so there is nothing to do
            // here afterwards: on success the view model has already selected the clone.
            var dialog = new Dialogs.CloneVmDialog(
                selected,
                _viewModel.Manager.SuggestCloneName(selected),
                _viewModel.CloneSelectedAsync)
            {
                XamlRoot = Content.XamlRoot
            };

            await ShowDialogAsync(dialog);
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null || !_viewModel.CanClone)
                return;
            if (!await UnlockSelectedAsync())
                return;

            var dialog = new Dialogs.ExportVmDialog(selected, PickOvaAsync, _viewModel.ExportSelectedAsync)
            {
                XamlRoot = Content.XamlRoot
            };
            await ShowDialogAsync(dialog);
            if (dialog.ExportedPath is not { } path)
                return;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.ExportedTitle,
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.ExportedBody, path)),
                PrimaryButtonText = ShellText.ShowInExplorer,
                CloseButtonText = CommonText.Close,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (result == ContentDialogResult.Primary)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }

        /// <summary>The save picker for an export, starting from the dialog's file name; null when dismissed.</summary>
        private async Task<string?> PickOvaAsync(string current)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(current)
            };
            picker.FileTypeChoices.Add(ShellText.OvfPackage, new System.Collections.Generic.List<string> { ".ova" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null)
                return;

            var body = string.Format(CultureInfo.CurrentCulture, ShellText.DeleteVmBody, selected.FolderPath);

            // The files of a machine linked clones read from cannot go; the button is then not offered at
            // all, and the body says why.
            var filesLocked = _viewModel.Manager.WhyFilesCannotBeDeleted(selected);
            if (filesLocked is not null)
                body += "\n\n" + filesLocked;

            // A clone the library no longer lists protects nothing: its original could then be changed
            // under it. Said here because "Retirer de la liste" sounds harmless.
            if (selected.Disks.Any(d => d.IsLinkedClone))
                body += "\n\n" + ShellText.DeleteVmLinkedClone;

            // Two distinct destructive outcomes, so they get two distinct buttons rather than a
            // single confirmation that guesses which one the user meant.
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.DeleteNamedTitle, selected.Name),
                // Three buttons share the width, so the labels have to be short enough to survive it.
                // The title already names the machine and the body gives the folder.
                Content = DialogBody(body, 540),
                PrimaryButtonText = filesLocked is null ? ShellText.DeleteVmFilesButton : string.Empty,
                SecondaryButtonText = ShellText.DeleteVmRemoveButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (result == ContentDialogResult.None)
                return;

            _viewModel.DeleteCommand.Execute(result == ContentDialogResult.Primary);
        }

        /// <summary>
        /// Closing the shell would leave orphaned QEMU processes, so a running VM has to be dealt
        /// with explicitly rather than killed silently.
        /// </summary>
        private async void OnAppWindowClosing(
            Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_closeConfirmed)
                return;

            var running = _viewModel.Manager.LiveSessions.ToList();
            if (running.Count == 0)
            {
                ConsoleHost.Detach();
                _tray?.Dispose();
                await _viewModel.Manager.DisposeAsync();
                return;
            }

            args.Cancel = true;

            var names = string.Join(", ", running.Select(s => s.Config.Name));
            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = ShellText.CloseRunningTitle,
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, ShellText.CloseRunningBody, names)),
                PrimaryButtonText = ShellText.CloseRunInBackground,
                SecondaryButtonText = ShellText.CloseShutDownAndQuit,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });

            if (choice == ContentDialogResult.Primary)
                HideToBackground();
            else if (choice == ContentDialogResult.Secondary)
                await StopMachinesAndQuitAsync();
        }

        // ---- Windows ending the session ----

        private readonly SessionEndWatcher _sessionEnd;
        private bool _windowsEnding;

        /// <summary>
        /// Windows is shutting down, restarting or signing out, and waits: every guest is asked to shut down
        /// (ACPI, as the Arrêter button), Windows' shutdown screen names those still running, and WinQEMU quits
        /// once none is left, which lets Windows carry on.
        /// </summary>
        private async Task StopMachinesForWindowsAsync()
        {
            // Windows asks again each time the user tries once more: one stop is enough.
            if (_windowsEnding)
                return;
            _windowsEnding = true;
            _viewModel.AppendDiagnostic("Windows is ending the session: the machines are shut down first.");
            await _viewModel.RequestShutdownOfAllAsync();

            while (_windowsEnding)
            {
                var left = _viewModel.Manager.LiveSessions.Select(s => s.Config.Name).ToList();
                if (left.Count == 0)
                    break;
                _sessionEnd.SetReason(string.Format(CultureInfo.CurrentCulture, ShellText.ShutdownReason, string.Join(", ", left)));
                await Task.Delay(500);
            }
            if (!_windowsEnding)
                return;

            ConsoleHost.Detach();
            _tray?.Dispose();
            _tray = null;
            await _viewModel.Manager.DisposeAsync();
            _sessionEnd.ClearReason();
            _closeConfirmed = true;
            Close();
        }

        /// <param name="ending">
        /// True: the session ends now (the user chose to shut down anyway, or WinQEMU was done). False: the
        /// user cancelled; the guests asked to shut down keep doing so, but WinQEMU stays.
        /// </param>
        private void OnWindowsSessionEnded(bool ending)
        {
            if (ending)
                return;
            _windowsEnding = false;
            _sessionEnd.ClearReason();
        }

        // ---- Background ----

        private TrayIcon? _tray;
        private bool _backgroundExplained;
        private Task? _initialization;

        /// <summary>Detects QEMU and loads the library once, whether the window is shown or not.</summary>
        private Task InitializeOnceAsync() => _initialization ??= InitializeAndServeAsync();

        private Cli.CommandServer? _commandServer;

        /// <summary>
        /// The winqemu command is answered once the library is read: one typed earlier waits on its pipe (the command
        /// gives WinQEMU a minute) rather than being told its machine does not exist.
        /// </summary>
        private async Task InitializeAndServeAsync()
        {
            await _viewModel.InitializeAsync();
            _viewModel.ShowRequested += ShowFromBackground;
            _commandServer = new Cli.CommandServer(_viewModel.RunCommandAsync, DispatcherQueue);
            _commandServer.Start();
        }

        /// <summary>Started at the Windows logon: the window stays hidden, WinQEMU waits in the notification area.</summary>
        public void StartInBackground()
        {
            _ = InitializeOnceAsync();
            ShowTray(ShellText.TrayStartedWithWindows);
        }

        /// <summary>
        /// Hides the window and leaves the machines running. The consoles stay children of the hidden window,
        /// so nothing has to be found again when it comes back.
        /// </summary>
        private void HideToBackground()
        {
            AppWindow.Hide();
            ShowTray(_backgroundExplained ? null
                : ShellText.TrayBackgroundExplained);
            _backgroundExplained = true;
        }

        private void ShowTray(string? balloon)
        {
            if (_tray is null)
            {
                _tray = new TrayIcon();
                _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowFromBackground);
                _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(async () =>
                {
                    ShowFromBackground();
                    await StopMachinesAndQuitAsync();
                });
            }
            var count = _viewModel.Manager.LiveSessions.Count();
            var tip = count switch
            {
                0 => "WinQEMU",
                1 => ShellText.TrayOneRunning,
                _ => string.Format(CultureInfo.CurrentCulture, ShellText.TrayManyRunning, count)
            };
            _tray.Show(tip, balloon is null ? null : ShellText.TrayBalloonTitle, balloon);
        }

        /// <summary>Brings the window back from the notification area, or forward when started again.</summary>
        public void ShowFromBackground()
        {
            AppWindow.Show();
            Activate();
            _tray?.Hide();
        }

        /// <summary>
        /// Asks every guest to shut down (ACPI, as the Arrêter button), shows them going, and quits once all
        /// are stopped. A guest that does not answer the power button can be switched off from there.
        /// </summary>
        private async Task StopMachinesAndQuitAsync()
        {
            await _viewModel.RequestShutdownOfAllAsync();

            var status = DialogBody(string.Empty);
            var dialog = new ContentDialog
            {
                Title = ShellText.ShuttingDownTitle,
                Content = status,
                PrimaryButtonText = ShellText.PowerOffNow,
                CloseButtonText = CommonText.Cancel,
                XamlRoot = Content.XamlRoot
            };
            var allStopped = false;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            void Refresh()
            {
                var left = _viewModel.Manager.LiveSessions.Select(s => s.Config.Name).ToList();
                if (left.Count == 0)
                {
                    allStopped = true;
                    timer.Stop();
                    dialog.Hide();
                    return;
                }
                status.Text = string.Format(CultureInfo.CurrentCulture, ShellText.ShuttingDownBody, string.Join(", ", left));
            }
            timer.Tick += (_, _) => Refresh();
            Refresh();
            if (!allStopped)
            {
                timer.Start();
                var choice = await ShowDialogAsync(dialog);
                timer.Stop();
                if (!allStopped && choice != ContentDialogResult.Primary)
                    return;
            }

            // Release the console before the process dies, so no stale child HWND outlives it.
            ConsoleHost.Detach();
            _tray?.Dispose();
            _tray = null;
            await _viewModel.Manager.DisposeAsync();
            _closeConfirmed = true;
            Close();
        }
    }
}
