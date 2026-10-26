using System;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
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

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Save();
            Hide();
        }

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Save();
                Hide();
            }
        }
    }
}
