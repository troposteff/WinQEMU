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
            ScaleModeBox.ItemsSource = new[] { "Ajuster", "Étirer", "Taille réelle" };
            ScaleModeBox.SelectedIndex = 0;
            UpdateGuestSizeText();

            RootGrid.SizeChanged += (_, _) => ApplyAdaptiveLayout();

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
            if (ConsoleHost is null)
                return;

            ConsoleHost.ScaleMode = ScaleModeBox.SelectedIndex switch
            {
                1 => Controls.ConsoleScaleMode.Stretch,
                2 => Controls.ConsoleScaleMode.Actual,
                _ => Controls.ConsoleScaleMode.Fit
            };
        }

        /// <summary>Reports the guest's own resolution, which is what the letterboxing is based on.</summary>
        private void UpdateGuestSizeText()
        {
            var (width, height) = ConsoleHost.GuestSize;
            GuestSizeText.Text = width > 0 && height > 0 ? $"{width} × {height}" : string.Empty;
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

        private void SetConsoleFullScreen(bool fullScreen)
        {
            _consoleFullScreen = fullScreen;

            // The console has to be showing for this to mean anything.
            if (fullScreen)
                ShowView(console: true);

            NotificationsPanel.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
            SidebarColumn.Width = fullScreen ? new GridLength(0) : new GridLength(320);
            HeaderRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
            SubHeaderRow.Height = fullScreen ? new GridLength(0) : GridLength.Auto;
            // The hint and the scale controls go, but the button stays: it is the visible way back, and
            // relying on F11 alone would strand anyone whose focus is not in the console.
            ConsoleHintText.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
            ConsoleScaleControls.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;

            AppWindow.SetPresenter(fullScreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);

            FullScreenButton.Content = fullScreen ? "Quitter le plein écran" : "Plein écran";

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

        private async void OnSnapshotsExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
            await _viewModel.Snapshots.EnsureLoadedAsync();

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

        private async void OnDiscardOverlayClick(object sender, RoutedEventArgs e)
        {
            if (DiskOf(sender) is not { } disk)
                return;

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = $"Revenir à l'image de base de « {disk.Id} » ?",
                Content = DialogBody(
                    $"La superposition {disk.FileName} sera supprimée et le disque reviendra à " +
                    $"{disk.BackingFileName}." +
                    // Stacked live snapshots: the image underneath is itself an overlay, undone the same way.
                    (disk.OverlayDepth > 1
                        ? $" Cette image est elle-même une superposition ({disk.OverlayDepth - 1} niveau(x) restant(s)), " +
                          "que vous pourrez défaire à son tour."
                        : string.Empty) +
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
            if (DiskOf(sender) is not { } disk)
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

            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = $"Restaurer « {snapshot.Name} » ?",
                Content = DialogBody(
                    $"Pris le {snapshot.CreatedText}.\n\nLes disques reviendront à cet état. " +
                    $"Tout ce qui a été écrit depuis sera perdu.{partial}"),
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

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedVm;
            if (selected is null)
                return;

            // Two distinct destructive outcomes, so they get two distinct buttons rather than a
            // single confirmation that guesses which one the user meant.
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = $"Supprimer « {selected.Name} » ?",
                // Three buttons share the width, so the labels have to be short enough to survive it.
                // The title already names the machine and the body gives the folder.
                Content = DialogBody($"Les disques virtuels se trouvent dans :\n{selected.FolderPath}", 540),
                PrimaryButtonText = "Supprimer les fichiers",
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
