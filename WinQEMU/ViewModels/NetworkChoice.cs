using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using WinQEMU.Core;
using WinQEMU.Core.Models;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// One entry of the "Connexion" list of a network card: NAT, or one of the virtual networks, side by
    /// side as VMware lists NAT, its bridge and its VMnets. <see cref="Key"/> is what
    /// <see cref="NetworkAdapterConfig.Connection"/> takes.
    /// </summary>
    public sealed class NetworkChoice : ObservableBase
    {
        private NetworkChoice(string key, VirtualNetwork? network)
        {
            Key = key;
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

        /// <summary>Read each time, in the language in force: the list keeps its items when the language changes.</summary>
        public string Label => Network is { } network
            ? string.Format(CultureInfo.CurrentCulture, DialogText.NetworkChoiceLabel, network.Name, network.TypeText)
            : Key == NetworkAdapterConfig.NoConnection ? DialogText.NetworkChoiceNone : DialogText.NetworkChoiceNat;

        /// <summary>The language changed: the label is read again.</summary>
        public void RefreshText() => RaisePropertyChanged(nameof(Label));

        public static NetworkChoice Nat() =>
            new(NetworkAdapterConfig.NatConnection, null);

        public static NetworkChoice None() =>
            new(NetworkAdapterConfig.NoConnection, null);

        public static NetworkChoice For(VirtualNetwork network) =>
            new(NetworkAdapterConfig.NetworkConnection(network.Id), network);

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
