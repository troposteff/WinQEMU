using System.Globalization;
using System.Text;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// The password of an encrypted machine's disks: qcow2 images with LUKS encryption (AES-256 in XTS mode,
    /// QEMU's default), every image of the machine under the same password. Held in memory only; QEMU and
    /// qemu-img receive it through <see cref="SecretPipe"/>, never on a command line.
    /// </summary>
    /// <remarks>
    /// Verified with this build (QEMU 11.1, Windows): create, info, check, snapshot -c/-a/-d, convert both ways,
    /// commit, rebase -u, resize, amend (keyslots), map, measure and qemu-system all open LUKS qcow2 with a
    /// secret object. Without the secret, qemu-img info still reads the header (snapshots included); anything
    /// that reads data says "Parameter 'encrypt.key-secret' is required for cipher".
    /// </remarks>
    public sealed class DiskKey
    {
        public DiskKey(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException(EncryptionText.PasswordRequired, nameof(password));
            Password = password;
        }

        public string Password { get; }

        /// <summary>
        /// How long LUKS spends deriving a new keyslot (encrypt.iter-time); null for QEMU's 2 s, which is also what
        /// opening an image costs (measured: 2.3 s per image, right or wrong password). Tests use a few milliseconds.
        /// </summary>
        public int? IterationTimeMs { get; init; }

        /// <summary>
        /// UTF-8, without a terminator: QEMU's secret object takes the file's bytes as they are (format=raw), so the
        /// same bytes must be sent every time, whatever the code page.
        /// </summary>
        internal byte[] ToBytes() => Encoding.UTF8.GetBytes(Password);

        /// <summary>Why a new password cannot be used, or null: empty, or not typed the same twice.</summary>
        public static string? WhyPasswordIsRefused(string password, string confirmation)
        {
            if (string.IsNullOrEmpty(password))
                return EncryptionText.PasswordRequired;
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                return EncryptionText.PasswordMismatch;
            return null;
        }

        /// <summary>The LUKS creation options for a new encrypted image, naming the secret object.</summary>
        internal string CreateOptions(string secretId)
        {
            var options = $"encrypt.format=luks,encrypt.key-secret={secretId}";
            if (IterationTimeMs is { } milliseconds)
                options += $",encrypt.iter-time={milliseconds.ToString(CultureInfo.InvariantCulture)}";
            return options;
        }
    }

    /// <summary>
    /// The disks of an encrypted machine cannot be opened: no password given (<see cref="IsWrongPassword"/> false),
    /// or not the right one. Not a <see cref="QemuImgException"/> on purpose: callers that ignore qemu-img failures
    /// (a snapshot absent from one disk) must not swallow this.
    /// </summary>
    public sealed class DiskPasswordException : InvalidOperationException
    {
        public DiskPasswordException(string message, bool isWrongPassword) : base(message) => IsWrongPassword = isWrongPassword;

        public bool IsWrongPassword { get; }

        /// <summary>qemu-img and qemu-system both say this when no keyslot opens with the password given.</summary>
        internal static bool IsWrongPasswordMessage(string? text) =>
            text?.Contains("Invalid password, cannot unlock any keyslot", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>What QEMU says when an encrypted image is opened without any secret.</summary>
        internal static bool IsMissingKeyMessage(string? text) =>
            text?.Contains("encrypt.key-secret' is required", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>The user gave up typing the password: the operation that needed it does not happen.</summary>
    public sealed class DiskUnlockCanceledException : OperationCanceledException
    {
        public DiskUnlockCanceledException() : base(EncryptionText.UnlockCanceled) { }
    }

    /// <summary>
    /// What the command line needs to open an encrypted machine's disks: where QEMU reads the password (a pipe served
    /// once, see <see cref="SecretPipe"/>), and for each disk, which levels of its backing chain are encrypted, top
    /// first.
    /// </summary>
    /// <remarks>
    /// The levels are needed because QEMU gives a backing image none of its overlay's options: each level wants its own
    /// <c>backing.….encrypt.key-secret</c>, and naming a level the chain does not have fails ("Could not open backing
    /// file: Must specify either driver or file"). The descriptor does not know a linked clone's chain below its base,
    /// so the caller reads it with <see cref="QemuImgService.GetEncryptedLevelsAsync"/>, which needs no password.
    /// </remarks>
    public sealed record DiskKeyArguments(string SecretFile, IReadOnlyDictionary<string, IReadOnlyList<bool>> EncryptedLevels)
    {
        /// <summary>The <c>key-secret</c> option for every encrypted level, to append to a -drive or --image-opts.</summary>
        public static string KeyOptions(IReadOnlyList<bool> levels, string secretId)
        {
            var options = new StringBuilder();
            for (var level = 0; level < levels.Count; level++)
            {
                if (!levels[level])
                    continue;
                options.Append(',');
                for (var depth = 0; depth < level; depth++)
                    options.Append("backing.");
                options.Append("encrypt.key-secret=").Append(secretId);
            }
            return options.ToString();
        }
    }
}
