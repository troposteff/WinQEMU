using System.Globalization;
using System.Text.Json;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Encrypted images (see <see cref="DiskKey"/>). A service made by <see cref="WithKey"/> opens every encrypted
    /// image with the machine's password, and creates and converts qcow2 images encrypted: an encrypted machine never
    /// gets a clear image from it, except where a method says so (export to VMDK, decryption).
    /// </summary>
    /// <remarks>
    /// An encrypted image is named with <c>--image-opts</c> (<c>driver=qcow2,file.filename=…,encrypt.key-secret=…</c>)
    /// rather than by its path, with a key for every encrypted level of its backing chain: qemu-img gives a backing
    /// image none of its overlay's options ("Could not open backing file: Parameter 'encrypt.key-secret' is required
    /// for cipher"). A clear image is still named by its path, so a keyed service handles both. Relative backing names
    /// resolve as usual under --image-opts (verified with commit, convert and qemu-io on a two-level chain).
    /// </remarks>
    public sealed partial class QemuImgService
    {
        /// <summary>The secret object holding the machine's password, in every keyed qemu-img run.</summary>
        internal const string SecretId = "wq-key";
        private const string NewSecretId = "wq-new-key";
        private const string OldSecretId = "wq-old-key";

        private QemuImgService(string qemuImgPath, DiskKey? key)
        {
            _qemuImgPath = qemuImgPath;
            Key = key;
        }

        /// <summary>The password encrypted images are opened and created with; null for a service that has none.</summary>
        public DiskKey? Key { get; }

        /// <summary>The same qemu-img, with this password (or none).</summary>
        public QemuImgService WithKey(DiskKey? key) => new(_qemuImgPath, key);

        /// <summary>
        /// The arguments of the last qemu-img run, as given to the process: for tests, which check that no password
        /// appears in them, and for diagnostics.
        /// </summary>
        public IReadOnlyList<string>? LastArguments { get; private set; }

        private IReadOnlyList<(string Id, DiskKey Key)> KeySecrets() => new[] { (SecretId, Key!) };

        private static string SecretObject(string id, string file) => $"secret,id={id},file={file}";

        /// <summary>
        /// qcow2 is the only format here with encryption. A keyed service asked for another one refuses rather than
        /// writing an encrypted machine's data in clear.
        /// </summary>
        private static bool RequireEncryptable(DiskFormat format) =>
            format == DiskFormat.Qcow2 ? true : throw new InvalidOperationException(EncryptionText.NeedsQcow2);

        /// <summary>Whether a conversion's output is encrypted: by default when the service has a key.</summary>
        private bool DestinationEncrypted(DiskFormat format, bool? requested)
        {
            if (requested == false)
                return false;
            if (Key is null)
                return requested == true ? throw new InvalidOperationException(EncryptionText.PasswordRequired) : false;
            return RequireEncryptable(format);
        }

        /// <summary>How an image is named on a qemu-img command line, and the secrets that takes.</summary>
        private sealed record ImageArgument(string Spec, bool ImageOpts, IReadOnlyList<(string Id, DiskKey Key)>? Secrets);

        /// <param name="topOnly">The key of the image itself only, for commands that leave its backing closed (rebase -u).</param>
        private async Task<ImageArgument> OpenAsync(string path, bool topOnly, CancellationToken cancellationToken)
        {
            if (Key is null)
                return new ImageArgument(path, false, null);

            var (levels, format) = await ReadChainAsync(path, cancellationToken).ConfigureAwait(false);
            if (!levels.Contains(true))
                return new ImageArgument(path, false, null);
            if (!string.Equals(format, "qcow2", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(EncryptionText.NeedsQcow2);

            var keyed = topOnly ? levels.Take(1).ToList() : levels;
            var spec = $"driver=qcow2,file.filename={QemuArgumentBuilder.EscapeOptionValue(path)}" + DiskKeyArguments.KeyOptions(keyed, SecretId);
            return new ImageArgument(spec, true, KeySecrets());
        }

        /// <summary>Runs <c>qemu-img &lt;command…&gt; &lt;image&gt; &lt;after…&gt;</c>, the image opened with the key when it needs one.</summary>
        private async Task<ProcessResult> RunOnImageAsync(string path, bool topOnly, CancellationToken cancellationToken,
            string[] command, params string[] after)
        {
            var image = await OpenAsync(path, topOnly, cancellationToken).ConfigureAwait(false);
            var arguments = new List<string> { command[0] };
            if (image.ImageOpts)
                arguments.Add("--image-opts");
            arguments.AddRange(command.Skip(1));
            arguments.Add(image.Spec);
            arguments.AddRange(after);
            return await RunAsync(arguments, cancellationToken, secrets: image.Secrets).ConfigureAwait(false);
        }

        /// <summary>
        /// Which levels of an image's backing chain are encrypted, the image itself first. Needs no password: qemu-img
        /// info reads headers only, and -U lets it read images a running QEMU holds.
        /// </summary>
        public async Task<IReadOnlyList<bool>> GetEncryptedLevelsAsync(string path, CancellationToken cancellationToken = default) =>
            (await ReadChainAsync(path, cancellationToken).ConfigureAwait(false)).Levels;

        /// <summary>Whether the image itself is encrypted (its header says so; no password needed).</summary>
        public async Task<bool> IsEncryptedAsync(string path, CancellationToken cancellationToken = default) =>
            (await GetEncryptedLevelsAsync(path, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        private async Task<(IReadOnlyList<bool> Levels, string Format)> ReadChainAsync(string path, CancellationToken cancellationToken)
        {
            var result = await RunAsync(new[] { "info", "--output=json", "--force-share", "--backing-chain", path }, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var levels = new List<bool>();
            var format = "unknown";
            foreach (var image in document.RootElement.EnumerateArray())
            {
                if (levels.Count == 0 && image.TryGetProperty("format", out var top))
                    format = top.GetString() ?? format;
                levels.Add(image.TryGetProperty("encrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True);
            }
            return (levels, format);
        }

        /// <summary>
        /// Checks the password against an image and its chain, cheaply: qemu-img map of the first sector, which must
        /// unlock every level (2.3 s each with QEMU's default keyslot cost). Throws <see cref="DiskPasswordException"/>
        /// when it does not open. -U: also on the images of a running machine. Does nothing for a clear image.
        /// </summary>
        public async Task VerifyKeyAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Key is null)
                throw new InvalidOperationException(EncryptionText.PasswordRequired);
            await RunOnImageAsync(path, false, cancellationToken, new[] { "map", "-U", "--output=json", "--max-length=512" })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Adds <paramref name="newKey"/> to the image's LUKS keyslots (qemu-img amend), the image opened with this
        /// service's key. Both passwords then open it, until <see cref="RemovePasswordAsync"/>.
        /// </summary>
        public async Task AddPasswordAsync(string path, DiskKey newKey, CancellationToken cancellationToken = default)
        {
            var image = await RequireEncryptedAsync(path, cancellationToken).ConfigureAwait(false);
            var options = $"encrypt.state=active,encrypt.new-secret={NewSecretId}";
            if (newKey.IterationTimeMs is { } milliseconds)
                options += $",encrypt.iter-time={milliseconds.ToString(CultureInfo.InvariantCulture)}";
            await RunAsync(new[] { "amend", "--image-opts", "-o", options, image.Spec }, cancellationToken,
                secrets: new[] { (SecretId, Key!), (NewSecretId, newKey) }).ConfigureAwait(false);
        }

        /// <summary>
        /// Erases the keyslots <paramref name="oldKey"/> opens, the image opened with this service's key. QEMU refuses
        /// to erase the last one ("erasing them will erase all the data in the image irreversibly").
        /// </summary>
        public async Task RemovePasswordAsync(string path, DiskKey oldKey, CancellationToken cancellationToken = default)
        {
            var image = await RequireEncryptedAsync(path, cancellationToken).ConfigureAwait(false);
            await RunAsync(new[] { "amend", "--image-opts", "-o", $"encrypt.state=inactive,encrypt.old-secret={OldSecretId}", image.Spec },
                cancellationToken, secrets: new[] { (SecretId, Key!), (OldSecretId, oldKey) }).ConfigureAwait(false);
        }

        private async Task<ImageArgument> RequireEncryptedAsync(string path, CancellationToken cancellationToken)
        {
            if (Key is null)
                throw new InvalidOperationException(EncryptionText.PasswordRequired);
            // amend opens the backing too: every level's key.
            var image = await OpenAsync(path, topOnly: false, cancellationToken).ConfigureAwait(false);
            if (!image.ImageOpts || !await IsEncryptedAsync(path, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, EncryptionText.ImageNotEncrypted, path));
            return image;
        }

        /// <summary>
        /// Creates an overlay without opening its base (<c>create -u</c>), for a live snapshot: the base is held by the
        /// running QEMU, which refuses to share it for writing. Encrypted when the service has a key.
        /// </summary>
        /// <param name="backingFile">Written into the overlay as it is: absolute, as QEMU writes its own overlays' bases.</param>
        public async Task CreateOverlayAsync(string path, long capacityBytes, string backingFile, CancellationToken cancellationToken = default)
        {
            if (File.Exists(path))
                throw new IOException(string.Format(CultureInfo.CurrentCulture, PlatformText.ImageFileExists, path));

            var arguments = new List<string> { "create", "-f", "qcow2" };
            if (Key is not null)
            {
                arguments.Add("-o");
                arguments.Add(Key.CreateOptions(SecretId));
            }
            arguments.AddRange(new[] { "-b", backingFile, "-F", "qcow2", "-u", path, capacityBytes.ToString(CultureInfo.InvariantCulture) });
            await RunAsync(arguments, cancellationToken, secrets: Key is null ? null : KeySecrets()).ConfigureAwait(false);
        }
    }
}
