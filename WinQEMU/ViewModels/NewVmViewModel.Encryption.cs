using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>« Chiffrer les disques » of the wizard's disk step (Core.Qemu.DiskKey).</summary>
    public partial class NewVmViewModel
    {
        private bool _encryptDisks;
        public bool EncryptDisks
        {
            get => _encryptDisks;
            set { if (SetProperty(ref _encryptDisks, value)) RevalidateCurrentStep(); }
        }

        private bool _rememberDiskPassword;
        /// <summary>Unchecked by default: kept in the Credential Manager only when asked.</summary>
        public bool RememberDiskPassword
        {
            get => _rememberDiskPassword;
            set { if (SetProperty(ref _rememberDiskPassword, value)) RevalidateCurrentStep(); }
        }

        /// <summary>Memory only: PasswordBox has no bindable password.</summary>
        private string DiskPassword { get; set; } = string.Empty;
        private string DiskPasswordConfirmation { get; set; } = string.Empty;

        public void SetDiskPassword(string password, string confirmation)
        {
            DiskPassword = password;
            DiskPasswordConfirmation = confirmation;
            RevalidateCurrentStep();
        }

        /// <summary>Only a disk created here is encrypted; an existing image is attached as it is.</summary>
        private bool UseEncryption => EncryptDisks && CreateNewDisk;

        private string? ValidateEncryption()
        {
            if (!UseEncryption)
                return null;
            if (DiskFormat != DiskFormat.Qcow2)
                return EncryptionText.WizardNeedsQcow2;
            return DiskKey.WhyPasswordIsRefused(DiskPassword, DiskPasswordConfirmation);
        }
    }
}
