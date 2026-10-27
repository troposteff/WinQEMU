using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using WinQEMU.Core;
using WinQEMU.Core.Models;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// One entry of the "Connexion" list of a network card: NAT, or one of the virtual networks, side by
    /// side as VMware lists NAT, its bridge and its VMnets. <see cref="Key"/> is what
    /// <see cref="NetworkAdapterConfig.Connection"/> takes.
    /// </summary>
    public sealed class NetworkChoice : ObservableBase
    {
        private readonly string _label;

        private NetworkChoice(string key, string label, VirtualNetwork? network)
        {
            Key = key;
            _label = label;
            Network = network;
            // A network renamed in the editor is renamed in the list at once.
            if (network is not null)
                network.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(VirtualNetwork.Name))
                        RaisePropertyChanged(nameof(Label));
                };
        }

        public string Key { get; }

        public VirtualNetwork? Network { get; }

        public string Label => Network is { } network ? $"{network.Name} — {network.TypeText}" : _label;

        public static NetworkChoice Nat() =>
            new(NetworkAdapterConfig.NatConnection, "NAT (partage la connexion de ce PC)", null);

        public static NetworkChoice None() =>
            new(NetworkAdapterConfig.NoConnection, "Aucune carte réseau", null);

        public static NetworkChoice For(VirtualNetwork network) =>
            new(NetworkAdapterConfig.NetworkConnection(network.Id), string.Empty, network);

        /// <summary>
        /// NAT then every network, kept in step with the networks: items are added and removed in place,
        /// never rebuilt, so the cards' selections stay put.
        /// </summary>
        public static ObservableCollection<NetworkChoice> Follow(ObservableCollection<VirtualNetwork> networks)
        {
            var choices = new ObservableCollection<NetworkChoice> { Nat() };
            foreach (var network in networks)
                choices.Add(For(network));

            networks.CollectionChanged += (_, e) =>
            {
                foreach (var removed in e.OldItems?.OfType<VirtualNetwork>() ?? Enumerable.Empty<VirtualNetwork>())
                    if (choices.FirstOrDefault(c => c.Network == removed) is { } choice)
                        choices.Remove(choice);
                foreach (var added in e.NewItems?.OfType<VirtualNetwork>() ?? Enumerable.Empty<VirtualNetwork>())
                    choices.Add(For(added));
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    while (choices.Count > 1)
                        choices.RemoveAt(1);
                    foreach (var network in networks)
                        choices.Add(For(network));
                }
            };
            return choices;
        }
    }
}
