using System.ComponentModel;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>What the password prompt answered: the password, and whether to keep it in the Credential Manager.</summary>
    public sealed record DiskPasswordAnswer(string Password, bool Remember);

    /// <summary>
    /// Encrypted machines (Qemu.DiskKey): their passwords, unlocked once per run of WinQEMU and kept in memory only, or
    /// remembered in the Windows Credential Manager when the user asked (DiskPasswordStore).
    /// </summary>
    /// <remarks>
    /// The entry points for anything that needs a password, the future command line included:
    /// <list type="bullet">
    /// <item><see cref="IsEncrypted"/>, <see cref="IsDiskPasswordRemembered"/>, <see cref="IsUnlocked"/>.</item>
    /// <item><see cref="UnlockAsync"/> (asks through <see cref="DiskPasswordPrompt"/> when allowed) or
    /// <see cref="UnlockWithPasswordAsync"/> (a password supplied by a program; checked, never stored unless asked).</item>
    /// <item><see cref="ImagesFor"/>: qemu-img with the machine's password, for everything that opens its disks.</item>
    /// </list>
    /// Called on the UI thread, as the rest of this class.
    /// </remarks>
    public sealed partial class VmManager
    {
        private readonly Dictionary<Guid, DiskKey> _diskKeys = new();

        /// <summary>
        /// Asks the user for a machine's password: the machine, and why it is asked again (a wrong password) or null.
        /// Returns null when the user cancels. Set by the shell; without it, nothing is asked.
        /// </summary>
        public Func<VmConfig, string?, Task<DiskPasswordAnswer?>>? DiskPasswordPrompt { get; set; }

        public bool IsEncrypted(VmConfig config) => config.DisksEncrypted;

        public bool IsDiskPasswordRemembered(VmConfig config) => DiskPasswordStore.Exists(config.Id);

        /// <summary>The password was given (or read from the Credential Manager) during this run of WinQEMU.</summary>
        public bool IsUnlocked(VmConfig config) => _diskKeys.ContainsKey(config.Id);

        /// <summary>
        /// The machine's password, from memory, then from the Credential Manager, then from the user when
        /// <paramref name="interactive"/>. Checked against the disks before it is kept. Null for a machine that is not
        /// encrypted, or when nobody can be asked (an automatic start); <see cref="DiskUnlockCanceledException"/> when
        /// the user cancels.
        /// </summary>
        public async Task<DiskKey?> UnlockAsync(VmConfig config, bool interactive = true, CancellationToken cancellationToken = default)
        {
            if (!config.DisksEncrypted)
                return null;
            if (_diskKeys.TryGetValue(config.Id, out var known))
                return known;

            string? problem = null;
            string? remembered = null;
            try
            {
                remembered = DiskPasswordStore.Read(config.Id);
            }
            catch (Win32Exception)
            {
                // An unreadable credential is as good as none: the user is asked.
            }

            if (remembered is not null)
            {
                var key = new DiskKey(remembered);
                if (await IsRightAsync(config, key, cancellationToken))
                {
                    _diskKeys[config.Id] = key;
                    return key;
                }
                problem = EncryptionText.RememberedPasswordWrong;
            }

            if (!interactive || DiskPasswordPrompt is null)
                return null;

            while (true)
            {
                var answer = await DiskPasswordPrompt(config, problem) ?? throw new DiskUnlockCanceledException();
                if (answer.Password.Length == 0)
                {
                    problem = EncryptionText.PasswordRequired;
                    continue;
                }

                var key = new DiskKey(answer.Password);
                if (!await IsRightAsync(config, key, cancellationToken))
                {
                    problem = EncryptionText.WrongPasswordRetry;
                    continue;
                }

                KeepDiskKey(config, key, answer.Remember);
                return key;
            }
        }

        /// <summary>
        /// Unlocks with a password given by a program (the command line), without asking anyone. Throws
        /// <see cref="DiskPasswordException"/> when it does not open the disks. Remembered only when
        /// <paramref name="remember"/> says so.
        /// </summary>
        public async Task<DiskKey> UnlockWithPasswordAsync(VmConfig config, string password, bool remember = false,
            CancellationToken cancellationToken = default)
        {
            var key = new DiskKey(password);
            if (!await IsRightAsync(config, key, cancellationToken))
                throw new DiskPasswordException(EncryptionText.WrongPassword, isWrongPassword: true);
            KeepDiskKey(config, key, remember);
            return key;
        }

        /// <summary>Keeps the password for this run, and in the Credential Manager when asked.</summary>
        public void KeepDiskKey(VmConfig config, DiskKey key, bool remember)
        {
            _diskKeys[config.Id] = key;
            if (remember)
                DiskPasswordStore.Save(config.Id, key.Password, config.Name);
        }

        /// <summary>Forgets the remembered password and the one held in memory: the next use asks again.</summary>
        public void ForgetDiskPassword(VmConfig config)
        {
            DiskPasswordStore.Delete(config.Id);
            _diskKeys.Remove(config.Id);
        }

        private void ForgetDiskPasswordQuietly(VmConfig config)
        {
            try
            {
                ForgetDiskPassword(config);
            }
            catch (Win32Exception)
            {
                // Left in the Credential Manager, where the user can still remove it.
            }
        }

        /// <summary>
        /// qemu-img for this machine's disks: with its password when encrypted. Throws
        /// <see cref="DiskPasswordException"/> for an encrypted machine not unlocked yet (see <see cref="ImagesForAsync"/>).
        /// </summary>
        public QemuImgService ImagesFor(VmConfig config)
        {
            var images = DiskService ?? throw new QemuNotAvailableException("QEMU was not found.");
            if (!config.DisksEncrypted)
                return images;
            return _diskKeys.TryGetValue(config.Id, out var key)
                ? images.WithKey(key)
                : throw new DiskPasswordException(EncryptionText.ImageLocked, isWrongPassword: false);
        }

        /// <summary><see cref="ImagesFor"/>, asking for the password first when it is needed.</summary>
        public async Task<QemuImgService> ImagesForAsync(VmConfig config, CancellationToken cancellationToken = default)
        {
            await UnlockAsync(config, interactive: true, cancellationToken);
            return ImagesFor(config);
        }

        /// <summary>
        /// Gives the session the password its disks need, before it starts. False when the machine is encrypted and no
        /// password could be had without asking (<paramref name="interactive"/> false): it cannot start unattended.
        /// </summary>
        public async Task<bool> PrepareDiskKeyAsync(VmSession session, bool interactive, CancellationToken cancellationToken = default)
        {
            if (!session.Config.DisksEncrypted)
            {
                session.DiskKey = null;
                return true;
            }
            var key = await UnlockAsync(session.Config, interactive, cancellationToken);
            session.DiskKey = key;
            return key is not null;
        }

        /// <summary>A clone made with its source's password opens with the same one, remembered if the source's was.</summary>
        private void ShareDiskKey(VmConfig source, VmConfig clone)
        {
            if (!source.DisksEncrypted || !_diskKeys.TryGetValue(source.Id, out var key))
                return;
            bool remembered;
            try
            {
                remembered = DiskPasswordStore.Read(source.Id) == key.Password;
            }
            catch (Win32Exception)
            {
                remembered = false;
            }
            KeepDiskKey(clone, key, remembered);
        }

        private async Task<bool> IsRightAsync(VmConfig config, DiskKey key, CancellationToken cancellationToken)
        {
            var images = DiskService ?? throw new QemuNotAvailableException("QEMU was not found.");
            var disk = config.Disks.FirstOrDefault(d => d.Format == DiskFormat.Qcow2 && File.Exists(config.ResolvePath(d.FileName)));
            if (disk is null)
                return true;
            try
            {
                await images.WithKey(key).VerifyKeyAsync(config.ResolvePath(disk.FileName), cancellationToken);
                return true;
            }
            catch (DiskPasswordException)
            {
                return false;
            }
        }

        private bool IsStopped(VmConfig config) => FindSession(config) is not { } session || session.State is VmState.Stopped or VmState.Error;

        /// <summary>Why the machine's disks cannot be encrypted now, or null.</summary>
        public string? WhyCannotEncrypt(VmConfig config) =>
            !IsStopped(config) ? EncryptionText.MustBeStopped : DiskEncryption.WhyCannotEncrypt(config, ProtectedImages);

        /// <summary>Why the machine's disks cannot be decrypted now, or null.</summary>
        public string? WhyCannotDecrypt(VmConfig config) =>
            !IsStopped(config) ? EncryptionText.MustBeStopped : DiskEncryption.WhyCannotDecrypt(config, ProtectedImages);

        /// <summary>Why the password cannot be changed now, or null.</summary>
        public string? WhyCannotChangeDiskPassword(VmConfig config) =>
            !IsStopped(config) ? EncryptionText.MustBeStopped : DiskEncryption.WhyCannotChangePassword(config, ProtectedImages);

        /// <summary>Encrypts a stopped machine's disks with a new password (see DiskEncryption.RewriteAsync).</summary>
        public async Task<DiskEncryptionResult> EncryptVirtualMachineAsync(VmConfig config, DiskKey key, bool remember,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (WhyCannotEncrypt(config) is { } refusal)
                throw new InvalidOperationException(refusal);
            var images = DiskService ?? throw new QemuNotAvailableException("QEMU was not found.");

            var result = await DiskEncryption.RewriteAsync(config, images.WithKey(key), encrypt: true, () => Save(config), progress, cancellationToken);
            KeepDiskKey(config, key, remember);
            return result;
        }

        /// <summary>Decrypts a stopped machine's disks, which must be unlocked; forgets its password.</summary>
        public async Task<DiskEncryptionResult> DecryptVirtualMachineAsync(VmConfig config, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (WhyCannotDecrypt(config) is { } refusal)
                throw new InvalidOperationException(refusal);

            var result = await DiskEncryption.RewriteAsync(config, ImagesFor(config), encrypt: false, () => Save(config), progress, cancellationToken);
            ForgetDiskPassword(config);
            return result;
        }

        /// <summary>Gives an unlocked, stopped machine's disks a new password; remembered in its place if asked.</summary>
        public async Task ChangeDiskPasswordAsync(VmConfig config, DiskKey newKey, bool remember, CancellationToken cancellationToken = default)
        {
            if (WhyCannotChangeDiskPassword(config) is { } refusal)
                throw new InvalidOperationException(refusal);

            await DiskEncryption.ChangePasswordAsync(config, ImagesFor(config), newKey, cancellationToken);
            // The old one, remembered, would no longer open anything.
            DiskPasswordStore.Delete(config.Id);
            KeepDiskKey(config, newKey, remember);
        }
    }
}
