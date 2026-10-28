using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Encrypted disks (Core.Qemu.DiskKey): the summary's « Chiffrement des disques » section, and the password a start
    /// needs. The operations themselves are VmManager's; the dialogs are the window's.
    /// </summary>
    public partial class MainViewModel
    {
        private void InitializeEncryption()
        {
            PropertyChanged += OnEncryptionInputsChanged;
        }

        private void OnEncryptionInputsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SelectedVm) or nameof(SelectedState) or nameof(IsSelectedAlive))
                RefreshEncryption();
        }

        /// <summary>
        /// Starts a machine with a password given by a program — the command line — rather than typed in a dialog. The
        /// password is checked first (DiskPasswordException when it does not open the disks) and kept for this run of
        /// WinQEMU only, unless <paramref name="rememberPassword"/>. Without a password, an encrypted machine uses its
        /// remembered one, or asks as the Démarrer button does.
        /// </summary>
        public async Task StartMachineAsync(VmConfig config, string? password = null, bool rememberPassword = false)
        {
            if (password is not null && config.DisksEncrypted)
                await Manager.UnlockWithPasswordAsync(config, password, rememberPassword);
            await StartMachineAsync(Manager.GetSession(config));
            RefreshEncryption();
        }

        /// <summary>The password the session's disks need, asked if it was neither given nor remembered.</summary>
        private async Task PrepareDiskKeyAsync(VmSession session)
        {
            await Manager.PrepareDiskKeyAsync(session, interactive: true);
            RefreshEncryption();
        }

        /// <summary>
        /// For a start nobody watches (with WinQEMU, at logon): an encrypted machine can only use a remembered password.
        /// Without one it stays stopped, said in the log and the error bar, and the next machines start as planned.
        /// </summary>
        private async Task<bool> CanStartUnattendedAsync(VmSession session)
        {
            try
            {
                if (await Manager.PrepareDiskKeyAsync(session, interactive: false))
                    return true;
            }
            catch (Exception ex) when (ex is QemuImgException or DiskPasswordException or System.IO.IOException)
            {
                AppendDiagnostic($"{session.Config.Name}: {ex.Message}");
            }

            var text = string.Format(CultureInfo.CurrentCulture, EncryptionText.AutoStartNeedsPassword, session.Config.Name);
            AppendDiagnostic(text);
            ReportError(new InvalidOperationException(text));
            return false;
        }

        public bool IsSelectedEncrypted => SelectedVm?.DisksEncrypted == true;

        /// <summary>A machine whose disks are in clear: the section offers to encrypt them, and nothing else.</summary>
        public bool IsSelectedClear => SelectedVm is not null && !IsSelectedEncrypted;

        public bool IsSelectedPasswordRemembered => SelectedVm is { DisksEncrypted: true } vm && Manager.IsDiskPasswordRemembered(vm);

        public string EncryptionStatusText => SelectedVm is not { } vm
            ? string.Empty
            : !vm.DisksEncrypted
                ? EncryptionText.StatusClear
                : EncryptionText.StatusEncrypted + " " + (Manager.IsDiskPasswordRemembered(vm) ? EncryptionText.StatusRemembered : EncryptionText.StatusAsked);

        /// <summary>Offered on a stopped machine; why it is refused, if it is, is said on click.</summary>
        public bool CanEncryptSelected => SelectedVm is not null && !IsSelectedEncrypted && !IsSelectedAlive && Manager.IsQemuAvailable;

        public bool CanDecryptSelected => IsSelectedEncrypted && !IsSelectedAlive && Manager.IsQemuAvailable;

        public bool CanChangeSelectedPassword => CanDecryptSelected;

        public bool CanForgetSelectedPassword => IsSelectedPasswordRemembered;

        /// <summary>Raises everything the encryption section shows.</summary>
        public void RefreshEncryption()
        {
            RaisePropertyChanged(nameof(IsSelectedEncrypted));
            RaisePropertyChanged(nameof(IsSelectedClear));
            RaisePropertyChanged(nameof(IsSelectedPasswordRemembered));
            RaisePropertyChanged(nameof(EncryptionStatusText));
            RaisePropertyChanged(nameof(CanEncryptSelected));
            RaisePropertyChanged(nameof(CanDecryptSelected));
            RaisePropertyChanged(nameof(CanChangeSelectedPassword));
            RaisePropertyChanged(nameof(CanForgetSelectedPassword));
        }

        /// <summary>Forgets the selection's remembered password (and the one in memory): asked at the next start.</summary>
        public void ForgetSelectedPassword()
        {
            if (SelectedVm is { } vm)
            {
                Manager.ForgetDiskPassword(vm);
                AppendDiagnostic($"{vm.Name}: disk password forgotten.");
            }
            RefreshEncryption();
        }
    }
}
