using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Networking;
using WinQEMU.Core.Services;

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
            _manager = manager;
            NetworkList.ItemsSource = manager.Settings.VirtualNetworks;
            manager.Settings.VirtualNetworks.CollectionChanged += OnNetworksChanged;
            Closed += (_, _) => manager.Settings.VirtualNetworks.CollectionChanged -= OnNetworksChanged;
            UpdateEmptyText();
        }

        private void OnNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyText();

        private void UpdateEmptyText() =>
            EmptyText.Visibility = _manager.Settings.VirtualNetworks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnAddPrivateClick(object sender, RoutedEventArgs e)
        {
            var networks = _manager.Settings.VirtualNetworks;
            var name = "Réseau privé";
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = $"Réseau privé {index}";

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
            var networks = _manager.Settings.VirtualNetworks;
            bool Free(Core.Networking.TapAdapter adapter) =>
                networks.All(n => !n.TapAdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));

            var adapters = Core.Networking.TapAdapter.List();
            var free = adapters.FirstOrDefault(Free);
            if (free is null)
            {
                var installing = !Core.Networking.TapDriver.IsInstalled;
                ShowProgress(installing
                    ? "Installation du pilote TAP-Windows d'OpenVPN… Windows demande l'autorisation administrateur, et peut demander s'il faut faire confiance à OpenVPN."
                    : "Ajout d'une carte TAP-Windows… Windows demande l'autorisation administrateur.");
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
                        ShowError("Le pilote a répondu, mais aucune nouvelle carte TAP-Windows n'est apparue. Réessayez dans un instant.");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    ShowError("Rien n'a été installé : l'autorisation administrateur a été refusée.");
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
            var name = "Réseau interne";
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = $"Réseau interne {index}";

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
            if (!Npcap.IsInstalled)
            {
                ShowError(null);
                NpcapPanel.Visibility = Visibility.Visible;
                return;
            }
            NpcapPanel.Visibility = Visibility.Collapsed;

            var adapter = PhysicalAdapter.List().FirstOrDefault();
            if (adapter is null)
            {
                ShowError("Aucune carte Wi-Fi ou Ethernet de ce PC n'est connectée : connectez-en une, puis réessayez.");
                return;
            }
            if (WhyUnusable(adapter) is { } reason)
            {
                ShowError(reason);
                return;
            }

            ShowError(null);
            var networks = _manager.Settings.VirtualNetworks;
            var name = "Réseau externe";
            for (var index = 2; networks.Any(n => n.Name == name); index++)
                name = $"Réseau externe {index}";

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
                box.Items.Insert(0, new ComboBoxItem { Content = $"{network.PhysicalAdapterName} (non connectée)", IsEnabled = false });
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
                ShowError($"{network.Name} est utilisé par {string.Join(", ", users)} : changez d'abord leur carte réseau dans Matériel.");
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
                network.Name = "Réseau sans nom";
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
            ShowError(problem is null ? null : $"{network.Name} : {problem}");
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
                ShowError($"Les réseaux n'ont pas pu être enregistrés : {ex.Message}");
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
                ShowError($"{problem} Corrigez-le avant de fermer.");
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
                    return $"{network.Name} : {problem}";
            }
            return null;
        }

        /// <summary>The same rule for any other way the dialog could close.</summary>
        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (FirstProblem() is { } problem)
            {
                args.Cancel = true;
                ShowError($"{problem} Corrigez-le avant de fermer.");
            }
        }
    }
}
