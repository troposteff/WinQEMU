using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinQEMU.Core.Models;
using WinQEMU.ViewModels;

namespace WinQEMU
{
    public sealed partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
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
            // Keys captured by the host travel to the guest over the control channel, because an
            // embedded console window cannot receive them.
            ConsoleHost.KeyForwarder = _viewModel.ForwardKeysAsync;
            ConsoleHost.PointerForwarder = _viewModel.ForwardPointerAsync;

            // Keep the newest QEMU output in view without stealing focus.
            _viewModel.LogUpdated += OnLogUpdated;
            _viewModel.ConsoleTargetChanged += OnConsoleTargetChanged;

            ConsoleHost.GuestSizeChanged += (_, _) => UpdateGuestSizeText();
            // Only the session can answer this, and only over QMP.
            ConsoleHost.GuestSizeProvider = async () =>
            {
                var session = _viewModel.SelectedSession;
                return session is { IsRunning: true } ? await session.QueryGuestScreenSizeAsync() : (0, 0);
            };
            // Raised for F11 only, which the host keeps for itself rather than forwarding: with the guest
            // holding the keyboard there is otherwise no key left to leave full screen with.
            ConsoleHost.FullScreenRequested += (_, _) => SetConsoleFullScreen(!_consoleFullScreen);
            ScaleModeBox.ItemsSource = new[] { "Ajuster", "Taille réelle" };
            ScaleModeBox.SelectedIndex = 0;
            UpdateGuestSizeText();

            RootGrid.SizeChanged += (_, _) => ApplyAdaptiveLayout();

            _fullScreenBarTimer = DispatcherQueue.CreateTimer();
            _fullScreenBarTimer.Interval = TimeSpan.FromMilliseconds(100);
            _fullScreenBarTimer.Tick += (_, _) => UpdateFullScreenBar();

            RootGrid.Loaded += async (_, _) =>
            {
                ApplyAdaptiveLayout();
                await _viewModel.InitializeAsync();
            };

            // The window carried no icon at all — WM_GETICON returned nothing — so the title bar fell
            // back to whatever the shell had cached for the package. Setting it explicitly makes the
            // title bar, Alt+Tab and the taskbar show the application's own icon.
            AppWindow.SetIcon("Assets/WinQEMU.ico");

            ApplyTitleBarTheme();

            AppWindow.Closing += OnAppWindowClosing;
        }

        // ---- Title bar theme ----

        // The title bar is drawn by DWM, not by XAML, and a standard one stays light until it is
        // told otherwise: the window came up with a light title bar above dark content while
        // Windows itself was in dark mode. Following the app mode the user picked fixes it.
        private void ApplyTitleBarTheme()
        {
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
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

        private void OnScaleModeChanged(object sender, SelectionChangedEventArgs e)
        {
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
                    ? $"{width} × {height} réduite : pointeur décalé (F11 ou Taille réelle)"
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
            ConsoleHost.TopOverlayHeight = show ? FullScreenBar.Height : 0;

            // Back to the guest once the bar is gone, so typing resumes without a click.
            if (!show && _consoleFullScreen)
                ConsoleHost.FocusGuest();
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
            ConsoleBar.Visibility = collapsed;
            SidebarColumn.Width = fullScreen ? new GridLength(0) : new GridLength(320);
            HeaderRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
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

            const double narrowThreshold = 1000;
            var narrow = RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < narrowThreshold;

            SidebarColumn.Width = new GridLength(narrow ? 240 : 320);

            // The summary tab's content is only realised while that tab is selected, so these may not
            // exist yet when a resize happens with the console showing.
            if (ResourcesColumn is null || ResourcesPanel is null)
                return;

            ResourcesColumn.Width = new GridLength(narrow ? 0 : 320);
            Grid.SetColumn(ResourcesPanel, narrow ? 0 : 1);
            Grid.SetRow(ResourcesPanel, narrow ? 1 : 0);
            Grid.SetRowSpan(ResourcesPanel, narrow ? 1 : 3);
            ResourcesPanel.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
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
                    Title = "Création impossible",
                    Content = ex.Message,
                    CloseButtonText = "Fermer",
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

            var box = new TextBox { Header = "Nouveau nom", Text = target.Name };
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
                Title = "Renommer la machine virtuelle",
                Content = body,
                PrimaryButtonText = "Renommer",
                CloseButtonText = "Annuler",
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
                Title = "Instantané à chaud des disques ?",
                Content = DialogBody(
                    "Chaque disque reçoit une superposition et la machine écrit désormais dedans ; " +
                    "l'image actuelle devient son image de base.\n\nLa mémoire n'est pas capturée : " +
                    "revenir en arrière ressemblera pour l'invité à une coupure de courant. La " +
                    "superposition se défait depuis la section Disques."),
                PrimaryButtonText = "Prendre l'instantané",
                CloseButtonText = "Annuler",
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
                Title = $"Reprendre « {snapshot.Name} » ?",
                Content = DialogBody(
                    $"Pris le {snapshot.CreatedText}.\n\nLa machine repartira exactement à cet instant, " +
                    "mémoire comprise. Son état actuel, disques et mémoire, est perdu."),
                PrimaryButtonText = "Reprendre",
                CloseButtonText = "Annuler",
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
                Title = "Fusion impossible : un clone lié en dépend",
                Content = DialogBody(reason),
                CloseButtonText = "Fermer",
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
                Title = $"Revenir à l'image de base de « {disk.Id} » ?",
                Content = DialogBody(
                    (keptForClone
                        ? $"{disk.BackingFileName} sert de base à un clone lié : il n'est pas modifié. Le disque " +
                          "repart de son état sur une nouvelle superposition, et la superposition actuelle " +
                          "est supprimée."
                        : $"La superposition {disk.FileName} sera supprimée et le disque reviendra à " +
                          $"{disk.BackingFileName}." +
                          // Stacked live snapshots: the image underneath is itself an overlay, undone the same way.
                          (disk.OverlayDepth > 1
                              ? $" Cette image est elle-même une superposition ({disk.OverlayDepth - 1} niveau(x) restant(s)), " +
                                "que vous pourrez défaire à son tour."
                              : string.Empty)) +
                    "\n\nTout ce que l'invité a écrit depuis l'instantané est perdu."),
                PrimaryButtonText = "Revenir à la base",
                CloseButtonText = "Annuler",
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
                Title = $"Fusionner « {disk.Id} » dans son image de base ?",
                Content = DialogBody(
                    $"Le contenu de {disk.FileName} est écrit dans {disk.BackingFileName}, puis la " +
                    "superposition est supprimée.\n\nRien n'est perdu de ce que l'invité a écrit, mais " +
                    "l'instantané cesse d'exister : il ne sera plus possible d'y revenir."),
                PrimaryButtonText = "Fusionner",
                CloseButtonText = "Annuler",
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
                ? "\n\nCet instantané ne couvre pas tous les disques de la machine : ceux qui ne l'ont pas " +
                  "resteront dans leur état actuel, et l'ensemble sera incohérent."
                : string.Empty;

            // The list and the history it shows change differently in that case, so it is said up front.
            var keptForClone = snapshot.IsLocked
                ? "\n\nUn clone lié repose sur cet instantané : les images qu'il lit sont conservées, et la " +
                  "machine repart de cet état sur une nouvelle couche, qui apparaîtra dans la liste sous le " +
                  "même nom ou suivi de « (2) »."
                : string.Empty;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = $"Restaurer « {snapshot.Name} » ?",
                Content = DialogBody(
                    $"Pris le {snapshot.CreatedText}.\n\nLes disques reviendront à cet état. " +
                    $"Tout ce qui a été écrit depuis sera perdu.{partial}{keptForClone}"),
                PrimaryButtonText = "Restaurer",
                CloseButtonText = "Annuler",
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
                Title = $"Supprimer « {snapshot.Name} » ?",
                Content = DialogBody(
                    "L'instantané est effacé des disques. Leur état actuel n'est pas touché, mais il ne " +
                    "sera plus possible d'y revenir."),
                PrimaryButtonText = "Supprimer",
                CloseButtonText = "Annuler",
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
                    Title = "Fichier illisible",
                    Content = DialogBody($"{file.Path}\n\n{ex.Message}"),
                    CloseButtonText = "Fermer",
                    XamlRoot = Content.XamlRoot
                });
                return;
            }

            // The source's own name unless a machine here already has it.
            var name = plan.Name.Trim();
            if (_viewModel.VMs.Any(vm => string.Equals(vm.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
                name += " (importée)";

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
        private async void OnUsbClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is null)
                return;

            var entries = await _viewModel.GetUsbEntriesAsync();
            var running = _viewModel.IsSelectedRunning;
            var menu = new MenuFlyout();

            if (!running)
            {
                menu.Items.Add(new MenuFlyoutItem
                {
                    Text = "Machine arrêtée : choisissez ce qui sera connecté au démarrage",
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
                menu.Items.Add(new MenuFlyoutItem { Text = "Aucun périphérique USB branché sur ce PC", IsEnabled = false });

            menu.Items.Add(new MenuFlyoutSeparator());
            var atStart = new MenuFlyoutSubItem { Text = "Connecter à chaque démarrage" };
            foreach (var entry in entries)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = entry.Host is null ? $"{entry.Name} ({entry.IdText}) — débranché" : $"{entry.Name} ({entry.IdText})",
                    IsChecked = entry.AtStart
                };
                item.Click += (_, _) => _viewModel.SetUsbAtStart(entry, item.IsChecked);
                atStart.Items.Add(item);
            }
            if (atStart.Items.Count == 0)
                atStart.IsEnabled = false;
            menu.Items.Add(atStart);

            var help = new MenuFlyoutItem { Text = "Rendre un périphérique accessible (pilote WinUSB)…" };
            help.Click += async (_, _) => await ShowWinUsbHelpAsync();
            menu.Items.Add(help);

            // The console is a native window drawn above all XAML, the menu included.
            ConsoleHost.Suspend();
            menu.Closed += (_, _) => ConsoleHost.Resume();
            // Under whichever button opened it: the toolbar's, or the full-screen bar's.
            menu.ShowAt(sender as FrameworkElement ?? UsbButton);
        }

        private static string UsbStatus(MainViewModel.UsbMenuEntry entry) =>
            entry.Connected ? string.Empty
            : entry.Waiting ? " — en attente"
            : entry.Host is { IsReachable: false } ? " — pilote WinUSB requis"
            : string.Empty;

        private async Task ToggleUsbAsync(MainViewModel.UsbMenuEntry entry)
        {
            var problem = await _viewModel.ToggleUsbAsync(entry);
            if (problem is null)
                return;

            await ShowDialogAsync(new ContentDialog
            {
                Title = "Périphérique USB non connecté",
                Content = DialogBody(problem),
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            });
        }

        private async Task ShowWinUsbHelpAsync()
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = "Rendre un périphérique USB accessible",
                Content = DialogBody(
                    "QEMU n'accède qu'aux périphériques USB qui utilisent le pilote générique WinUSB de Microsoft. " +
                    "Contrairement à VMware, il n'a pas de pilote à lui qui saurait prendre un périphérique à Windows.\n\n" +
                    "Pour équiper un périphérique de WinUSB :\n" +
                    "1. Téléchargez Zadig (zadig.akeo.ie), l'outil de référence pour cela.\n" +
                    "2. Lancez-le. Dans le menu Options, cochez « List All Devices » : sans cela, Zadig ne montre " +
                    "pas les périphériques qui ont déjà un pilote.\n" +
                    "3. Dans la liste déroulante du haut, choisissez le périphérique. Vérifiez que « USB ID » " +
                    "affiche les mêmes numéros que ce menu (par exemple 0781 559C).\n" +
                    "4. Au centre, la case de gauche montre le pilote actuel (par exemple USBSTOR) ; celle de " +
                    "droite, le pilote à installer : réglez-la sur WinUSB avec ses petites flèches.\n" +
                    "5. Cliquez sur le grand bouton « Replace Driver » et attendez la fin (une minute environ).\n" +
                    "6. Rouvrez ce menu : le périphérique se connecte.\n\n" +
                    "Tant qu'il est sur WinUSB, Windows ne s'en sert plus lui-même : une clé n'apparaît plus dans " +
                    "l'Explorateur. Pour le lui rendre : Gestionnaire de périphériques, clic droit sur le " +
                    "périphérique, « Désinstaller l'appareil » en cochant « Tenter de supprimer le pilote », puis " +
                    "débranchez-le et rebranchez-le.\n\n" +
                    // Default width: with a 560 px minimum this text was clipped on the right instead of wrapped.
                    "Ne faites jamais cela pour le clavier, la souris ou le Bluetooth dont ce PC a besoin."),
                CloseButtonText = "OK",
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
                var item = new MenuFlyoutItem { Text = $"{preset.Label} (port {preset.GuestPort})" };
                item.Click += async (_, _) => await ShowProblemAsync("Service non ouvert",
                    await _viewModel.OpenServiceAsync(preset.Protocol, preset.GuestPort));
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var other = new MenuFlyoutItem { Text = "Autre port de l'invité…" };
            other.Click += async (_, _) => await OpenOtherServiceAsync();
            menu.Items.Add(other);

            // The console is a native window drawn above all XAML, the menu included.
            ConsoleHost.Suspend();
            menu.Closed += (_, _) => ConsoleHost.Resume();
            menu.ShowAt(OpenServiceButton);
        }

        private async Task OpenOtherServiceAsync()
        {
            var port = new NumberBox { Header = "Port de l'invité", Minimum = 1, Maximum = 65535, Value = 8080, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var udp = new CheckBox { Content = "UDP (TCP sinon)" };
            var body = new StackPanel { Spacing = 12, MinWidth = 360 };
            body.Children.Add(port);
            body.Children.Add(udp);

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Ouvrir un port de l'invité",
                Content = body,
                PrimaryButtonText = "Ouvrir",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (result != ContentDialogResult.Primary || double.IsNaN(port.Value))
                return;

            await ShowProblemAsync("Service non ouvert", await _viewModel.OpenServiceAsync(
                udp.IsChecked == true ? PortForwardProtocol.Udp : PortForwardProtocol.Tcp, (int)port.Value));
        }

        private async void OnCloseServiceClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MainViewModel.LanServiceItem item)
                await _viewModel.CloseServiceAsync(item);
        }

        private async void OnAllowFirewallClick(object sender, RoutedEventArgs e) =>
            await ShowProblemAsync("Pare-feu inchangé", await _viewModel.AllowQemuThroughFirewallAsync());

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

            await ShowProblemAsync("Dossier non partagé", _viewModel.ShareFolder(folder.Path, readOnly));
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
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            });
        }

        private async void OnCloneClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null || !_viewModel.CanClone)
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

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null)
                return;

            var body = $"Les disques virtuels se trouvent dans :\n{selected.FolderPath}";

            // The files of a machine linked clones read from cannot go; the button is then not offered at
            // all, and the body says why.
            var filesLocked = _viewModel.Manager.WhyFilesCannotBeDeleted(selected);
            if (filesLocked is not null)
                body += "\n\n" + filesLocked;

            // A clone the library no longer lists protects nothing: its original could then be changed
            // under it. Said here because "Retirer de la liste" sounds harmless.
            if (selected.Disks.Any(d => d.IsLinkedClone))
                body += "\n\nC'est un clone lié : retiré de la liste, il ne protège plus les disques de son " +
                        "original, et ne fonctionnera plus si celui-ci les modifie.";

            // Two distinct destructive outcomes, so they get two distinct buttons rather than a
            // single confirmation that guesses which one the user meant.
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = $"Supprimer « {selected.Name} » ?",
                // Three buttons share the width, so the labels have to be short enough to survive it.
                // The title already names the machine and the body gives the folder.
                Content = DialogBody(body, 540),
                PrimaryButtonText = filesLocked is null ? "Supprimer les fichiers" : string.Empty,
                SecondaryButtonText = "Retirer de la liste",
                CloseButtonText = "Annuler",
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

            var running = _viewModel.Manager.RunningSessions.ToList();
            if (running.Count == 0)
            {
                ConsoleHost.Detach();
                await _viewModel.Manager.DisposeAsync();
                return;
            }

            args.Cancel = true;

            var names = string.Join(", ", running.Select(s => s.Config.Name));
            var confirmed = await ShowDialogAsync(new ContentDialog
            {
                Title = "Des machines virtuelles sont en cours d'exécution",
                // Same wording as the Éteindre button, since it is the same brutal action: the label
                // alone sounds like an ordinary shutdown, so the consequence has to be spelled out.
                Content = DialogBody(
                    $"{names}\n\nQuitter maintenant les éteint immédiatement, sans prévenir l'invité, " +
                    "comme si on débranchait l'alimentation : les données non écrites sont perdues."),
                PrimaryButtonText = "Éteindre et quitter",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });

            if (confirmed != ContentDialogResult.Primary)
                return;

            // Release the console before the process dies, so no stale child HWND outlives it.
            ConsoleHost.Detach();
            await _viewModel.Manager.DisposeAsync();
            _closeConfirmed = true;
            Close();
        }
    }
}
