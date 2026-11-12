using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Networking;
using WinQEMU.Core.Services;
using WinQEMU.Text;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// The virtual networks, as VMware's network editor and Hyper-V's switch manager present them.
    /// Changes are saved as they are made.
    /// </summary>
    public sealed partial class VirtualNetworksDialog : ContentDialog
    {
        private readonly VmManager _manager;

        public VirtualNetworksDialog(VmManager manager)
        {
            InitializeComponent();
            DialogWidth.Fit(this, 640);
            _manager = manager;
            // The Microsoft Store build carries no driver installer: saying "comes with WinQEMU" there contradicted the
            // download link shown a line below, on the first internal network.
            TypesHint.Text = TapDriver.IsBundled ? DialogText.NetworksTypesHint : DialogText.NetworksTypesHintNoDriver;
            NetworkList.ItemsSource = manager.Settings.VirtualNetworks;
            manager.Settings.VirtualNetworks.CollectionChanged += OnNetworksChanged;
            UpdateEmptyText();

            // A driver is installed outside WinQEMU, while this dialog is open: looked for again every two seconds, so
            // its button comes back by itself.
            // Read off the interface's thread: the registry and every network card of this PC, every two seconds.
            var driverWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            var reading = false;
            driverWatch.Tick += async (_, _) =>
            {
                if (reading)
                    return;
                reading = true;
                try
                {
                    var (tap, npcap) = await System.Threading.Tasks.Task.Run(() => (CanAddInternal(), Npcap.IsInstalled));
                    ShowInternalAvailability(tap);
                    ShowExternalAvailability(npcap);
                }
                finally
                {
                    reading = false;
                }
            };
            TapDownloadLink.NavigateUri = new Uri(TapDriver.DownloadPage);
            UpdateInternalAvailability();
            UpdateExternalAvailability();
            driverWatch.Start();
            Closed += (_, _) =>
            {
                driverWatch.Stop();
                manager.Settings.VirtualNetworks.CollectionChanged -= OnNetworksChanged;
            };
        }

        /// <summary>
        /// Without the TAP driver, and in a build that cannot install it (the Microsoft Store's), an internal network
        /// cannot be added: its button is greyed out (the user's request: a button that only answered with a guide looked
        /// as if it worked), and the guide with the download link stays below it, since a disabled button explains nothing.
        /// </summary>
        /// <returns>Whether an internal network can be added.</returns>
        private bool UpdateInternalAvailability()
        {
            var available = CanAddInternal();
            ShowInternalAvailability(available);
            return available;
        }

        private void ShowInternalAvailability(bool available)
        {
            AddInternalButton.IsEnabled = available;
            TapPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        }

        private readonly object _driverGate = new();
        private DateTime? _driverWithoutAdapterSince;
        private bool _driverLookedFor;

        /// <summary>
        /// Whether the TAP driver is there to add an internal network with; read from any thread.
        /// </summary>
        /// <remarks>
        /// The driver's installer writes its folder (tapinstall.exe) before it creates the first adapter. Taken as
        /// installed in between, the button came back mid-installation, and a click then asked for a second elevation to
        /// add an adapter beside the one being created. A driver with no adapter at all counts once it has been so for
        /// fifteen seconds, or was already so when the dialog opened (adapters removed by hand: adding one is right).
        /// </remarks>
        private bool CanAddInternal()
        {
            if (TapDriver.IsBundled || TapAdapter.List().Count > 0)
                return true;
            lock (_driverGate)
            {
                var first = !_driverLookedFor;
                _driverLookedFor = true;
                if (TapDriver.InstallDirectory is null)
                {
                    _driverWithoutAdapterSince = null;
                    return false;
                }
                _driverWithoutAdapterSince ??= first ? DateTime.MinValue : DateTime.UtcNow;
                return DateTime.UtcNow - _driverWithoutAdapterSince > TimeSpan.FromSeconds(15);
            }
        }

        /// <summary>The same for an external network and Npcap, which no build carries.</summary>
        /// <returns>Whether an external network can be added.</returns>
        private bool UpdateExternalAvailability()
        {
            var available = Npcap.IsInstalled;
            ShowExternalAvailability(available);
            return available;
        }

        private void ShowExternalAvailability(bool available)
        {
            AddExternalButton.IsEnabled = available;
            NpcapPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyText();

        private void UpdateEmptyText() =>
            EmptyText.Visibility = _manager.Settings.VirtualNetworks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnAddPrivateClick(object sender, RoutedEventArgs e)
        {
            var networks = _manager.Settings.VirtualNetworks;
            var name = DialogText.NetworksDefaultPrivate;
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = string.Format(CultureInfo.CurrentCulture, DialogText.NetworksDefaultNumbered, DialogText.NetworksDefaultPrivate, index);

            networks.Add(new VirtualNetwork
            {
                Name = name,
                Type = VirtualNetworkType.Private,
                Subnet = VirtualNetwork.FreeSubnet(networks)
            });
            Save();
        }

        /// <summary>
        /// An internal network takes a TAP-Windows adapter of its own: two networks on one adapter would
        /// be bridged into one through this PC. When none is free, WinQEMU installs the bundled driver, or
        /// adds an adapter to it — one UAC prompt either way.
        /// </summary>
        private async void OnAddInternalClick(object sender, RoutedEventArgs e)
        {
            // The driver may have gone since the last look (uninstalled with the dialog open).
            if (!UpdateInternalAvailability())
                return;
            var networks = _manager.Settings.VirtualNetworks;
            bool Free(Core.Networking.TapAdapter adapter) =>
                networks.All(n => !n.TapAdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));

            var adapters = Core.Networking.TapAdapter.List();
            var free = adapters.FirstOrDefault(Free);
            if (free is null)
            {
                var installing = !Core.Networking.TapDriver.IsInstalled;
                ShowProgress(installing
                    ? DialogText.NetworksInstallingTap
                    : DialogText.NetworksAddingTap);
                try
                {
                    if (installing)
                        await Core.Networking.TapDriver.InstallAsync();
                    else
                        await Core.Networking.TapDriver.AddAdapterAsync();

                    // Windows takes a moment to set the new adapter up after the installer returns.
                    free = await Core.Networking.TapDriver.WaitForNewAdapterAsync(adapters.Select(a => a.Id).ToList(), TimeSpan.FromSeconds(30));
                    if (free is null)
                    {
                        ShowError(DialogText.NetworksNoNewTap);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    ShowError(DialogText.NetworksTapRefused);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    ShowError(ex.Message);
                    return;
                }
                finally
                {
                    ShowProgress(null);
                }
            }

            ShowError(null);
            var name = DialogText.NetworksDefaultInternal;
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = string.Format(CultureInfo.CurrentCulture, DialogText.NetworksDefaultNumbered, DialogText.NetworksDefaultInternal, index);

            networks.Add(new VirtualNetwork
            {
                Name = name,
                Type = VirtualNetworkType.Internal,
                Subnet = VirtualNetwork.FreeSubnet(networks),
                TapAdapterId = free.Id,
                TapAdapterName = free.Name
            });
            Save();
        }

        /// <summary>
        /// An external network puts the machines on a physical card through Npcap, which the user installs
        /// (its licence forbids bundling it). The card is opened once here, so an Npcap restricted to
        /// administrators is caught now rather than when a machine starts.
        /// </summary>
        private void OnAddExternalClick(object sender, RoutedEventArgs e)
        {
            // Npcap may have gone since the last look (uninstalled with the dialog open).
            if (!UpdateExternalAvailability())
                return;

            var adapter = PhysicalAdapter.List().FirstOrDefault();
            if (adapter is null)
            {
                ShowError(DialogText.NetworksNoPhysicalAdapter);
                return;
            }
            if (WhyUnusable(adapter) is { } reason)
            {
                ShowError(reason);
                return;
            }

            ShowError(null);
            var networks = _manager.Settings.VirtualNetworks;
            var name = DialogText.NetworksDefaultExternal;
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = string.Format(CultureInfo.CurrentCulture, DialogText.NetworksDefaultNumbered, DialogText.NetworksDefaultExternal, index);

            networks.Add(new VirtualNetwork
            {
                Name = name,
                Type = VirtualNetworkType.External,
                PhysicalAdapterId = adapter.Id,
                PhysicalAdapterName = adapter.Name
            });
            Save();
        }

        /// <summary>Why Npcap cannot open the card, or null when it can.</summary>
        private static string? WhyUnusable(PhysicalAdapter adapter)
        {
            try
            {
                Npcap.Open(adapter).Dispose();
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or BadImageFormatException)
            {
                return ex.Message;
            }
        }

        /// <summary>The cards an external network can use, as of when its box is shown; its own first if it has gone.</summary>
        private void OnAdapterBoxLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox box || box.DataContext is not VirtualNetwork network || box.Items.Count > 0)
                return;
            var adapters = PhysicalAdapter.List();
            foreach (var adapter in adapters)
                box.Items.Add(new ComboBoxItem { Content = $"{adapter.Name} ({adapter.Description})", Tag = adapter });
            var index = adapters.ToList().FindIndex(a => a.Id.Equals(network.PhysicalAdapterId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                box.Items.Insert(0, new ComboBoxItem { Content = string.Format(CultureInfo.CurrentCulture, DialogText.NetworksAdapterDisconnected, network.PhysicalAdapterName), IsEnabled = false });
                index = 0;
            }
            box.SelectedIndex = index;
        }

        /// <summary>Takes effect when the network's switch next starts, i.e. once no machine uses it any more.</summary>
        private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: PhysicalAdapter adapter } } box
                || box.DataContext is not VirtualNetwork network
                || adapter.Id.Equals(network.PhysicalAdapterId, StringComparison.OrdinalIgnoreCase))
                return;
            if (WhyUnusable(adapter) is { } reason)
            {
                ShowError(reason);
                return;
            }
            ShowError(null);
            network.PhysicalAdapterId = adapter.Id;
            network.PhysicalAdapterName = adapter.Name;
            Save();
        }

        /// <summary>Shows what is under way and holds the buttons back meanwhile.</summary>
        private void ShowProgress(string? message)
        {
            ProgressText.Text = message ?? string.Empty;
            ProgressText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
            ProgressRing.IsActive = message is not null;
            ProgressRing.Visibility = ProgressText.Visibility;
            AddButtons.IsHitTestVisible = message is null;
            AddButtons.Opacity = message is null ? 1 : 0.5;
        }

        /// <summary>Refused while a machine's card is plugged into it: the machine would no longer start.</summary>
        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not VirtualNetwork network)
                return;

            var users = _manager.VirtualMachines
                .Where(vm => vm.NetworkAdapters.Any(a => a.Mode == NetworkMode.Network && a.NetworkId == network.Id))
                .Select(vm => vm.Name)
                .ToList();
            if (users.Count > 0)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, DialogText.NetworksInUse, network.Name, string.Join(", ", users)));
                return;
            }

            ShowError(null);
            _manager.Settings.VirtualNetworks.Remove(network);
            Save();
            await _manager.Networks.StopAsync(network.Id);
        }

        private void OnNameLostFocus(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is VirtualNetwork network && string.IsNullOrWhiteSpace(network.Name))
                network.Name = DialogText.NetworksDefaultUnnamed;
            Save();
        }

        /// <summary>The masks offered, /8 to /29, shown as Windows shows them: 255.255.255.0 (/24).</summary>
        private static readonly IReadOnlyList<(int Length, string Text)> Masks =
            Enumerable.Range(8, 22).Reverse().Select(length => (length, $"{DhcpScope.MaskText(length)} (/{length})")).ToList();

        private void OnMaskBoxLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox box || box.DataContext is not VirtualNetwork network || box.Items.Count > 0)
                return;
            foreach (var (_, text) in Masks)
                box.Items.Add(text);
            box.SelectedIndex = Masks.ToList().FindIndex(m => m.Length == network.PrefixLength);
        }

        private void OnMaskChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox { SelectedIndex: >= 0 } box || box.DataContext is not VirtualNetwork network)
                return;
            network.PrefixLength = Masks[box.SelectedIndex].Length;
            Check(network);
        }

        /// <summary>
        /// Saved even when wrong, so nothing typed is lost; the network's line says what is wrong, and its
        /// switch refuses to start until it is fixed. Takes effect when the switch next starts.
        /// </summary>
        private void OnAddressLostFocus(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is VirtualNetwork network)
                Check(network);
        }

        private void Check(VirtualNetwork network)
        {
            var problem = network.Problem ?? network.ConflictAmong(_manager.Settings.VirtualNetworks);
            ShowError(problem is null ? null : string.Format(CultureInfo.CurrentCulture, DialogText.NetworksProblem, network.Name, problem));
            Save();
        }

        /// <summary>Takes effect when the network's switch next starts, i.e. once no machine uses it any more.</summary>
        private void OnDhcpToggled(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            try
            {
                _manager.Settings.Save();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, DialogText.NetworksSaveFailed, ex.Message));
            }
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnBackgroundPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
            NetworkScroller.Focus(FocusState.Pointer);

        private void OnCloseClick(object sender, RoutedEventArgs e) => TryClose();

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                TryClose();
            }
        }

        /// <summary>
        /// Closes only once every network's addresses are usable: a network left wrong would refuse to start
        /// with the next machine plugged into it, far from here, where the fields to fix it are.
        /// </summary>
        private void TryClose()
        {
            Save();
            if (FirstProblem() is { } problem)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, DialogText.NetworksFixBeforeClosing, problem));
                return;
            }
            Hide();
        }

        private string? FirstProblem()
        {
            var networks = _manager.Settings.VirtualNetworks;
            foreach (var network in networks)
            {
                if ((network.Problem ?? network.ConflictAmong(networks)) is { } problem)
                    return string.Format(CultureInfo.CurrentCulture, DialogText.NetworksProblem, network.Name, problem);
            }
            return null;
        }

        /// <summary>The same rule for any other way the dialog could close.</summary>
        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (FirstProblem() is { } problem)
            {
                args.Cancel = true;
                ShowError(string.Format(CultureInfo.CurrentCulture, DialogText.NetworksFixBeforeClosing, problem));
            }
        }
    }
}
