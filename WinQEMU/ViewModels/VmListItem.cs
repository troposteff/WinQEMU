using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// One row of the virtual machine inventory: the configuration plus the live state of its session.
    /// </summary>
    /// <remarks>
    /// The list used to bind straight to <see cref="VmConfig"/>, which carries no runtime state — so
    /// nothing in the inventory showed which machines were running. State lives on the session, hence
    /// this wrapper.
    /// </remarks>
    public sealed class VmListItem : ObservableObject, IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly VmSession? _session;
        private bool _disposed;

        public VmListItem(VmConfig config, VmSession? session, DispatcherQueue dispatcher)
        {
            Config = config;
            _session = session;
            _dispatcher = dispatcher;

            Config.PropertyChanged += OnConfigPropertyChanged;
            if (_session is not null)
                _session.StateChanged += OnSessionStateChanged;
        }

        public VmConfig Config { get; }

        public string Name => Config.Name;

        public string Description => Config.Description;

        public VmState State => _session?.State ?? VmState.Stopped;

        public string StateText => State switch
        {
            VmState.Stopped => DialogText.StateStopped,
            VmState.Starting => DialogText.StateStarting,
            VmState.Running => DialogText.StateRunning,
            VmState.Paused => DialogText.StatePaused,
            VmState.Stopping => DialogText.StateStopping,
            VmState.Error => CommonText.Error,
            _ => State.ToString()
        };

        /// <summary>Colour of the status dot. Follows the Windows convention: green runs, amber waits, red failed.</summary>
        public SolidColorBrush StateBrush => new(State switch
        {
            VmState.Running => Colors.LimeGreen,
            VmState.Paused => Colors.Goldenrod,
            VmState.Starting or VmState.Stopping => Colors.DodgerBlue,
            VmState.Error => Colors.OrangeRed,
            _ => Colors.Gray
        });

        /// <summary>
        /// What a screen reader announces. Without it the row reads out as the class name of the bound
        /// object, which is what the inventory did before.
        /// </summary>
        public string AutomationName =>
            string.IsNullOrWhiteSpace(Description)
                ? $"{Name}, {StateText}"
                : $"{Name}, {StateText}, {Description}";

        /// <summary>True when the machine is doing something the user should not interrupt.</summary>
        public bool IsBusy => State is VmState.Starting or VmState.Stopping;

        public bool Matches(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || Config.GuestOs.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }

        // Session events arrive on background threads; the collection is bound, so hop to the UI thread.
        private void OnSessionStateChanged(object? sender, VmState state) =>
            _dispatcher.TryEnqueue(RaiseStateProperties);

        private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_dispatcher.HasThreadAccess)
                RaiseConfigProperties();
            else
                _dispatcher.TryEnqueue(RaiseConfigProperties);
        }

        private void RaiseStateProperties()
        {
            RaisePropertyChanged(nameof(State));
            RaisePropertyChanged(nameof(StateText));
            RaisePropertyChanged(nameof(StateBrush));
            RaisePropertyChanged(nameof(IsBusy));
            RaisePropertyChanged(nameof(AutomationName));
        }

        private void RaiseConfigProperties()
        {
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(AutomationName));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Config.PropertyChanged -= OnConfigPropertyChanged;
            if (_session is not null)
                _session.StateChanged -= OnSessionStateChanged;
        }
    }
}
