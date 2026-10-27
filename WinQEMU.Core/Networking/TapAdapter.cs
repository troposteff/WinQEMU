using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinQEMU.Core.Networking
{
    /// <summary>A TAP-Windows adapter of this PC, which can be this PC's card on an internal network.</summary>
    /// <param name="Id">NetCfgInstanceId, e.g. {02BACC2A-907D-4F9D-B706-E7E4649D9AFB}.</param>
    /// <param name="Name">The connection's name in Windows, e.g. "Ethernet 3".</param>
    public sealed record TapAdapter(string Id, string Name, string Description, PhysicalAddress? Mac)
    {
        private const string NetworkClass = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";
        private const string NetworkConnections = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

        /// <summary>
        /// The adapters of the TAP-Windows driver itself (component id tap0901), read from the registry,
        /// which any user may. OpenVPN Connect's own adapter (tap_ovpnconnect) is left out: QEMU refuses it,
        /// and it belongs to that VPN.
        /// </summary>
        public static IReadOnlyList<TapAdapter> List()
        {
            var adapters = new List<TapAdapter>();
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClass);
            if (classKey is null)
                return adapters;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                using var key = TryOpen(classKey, subKeyName);
                if (key?.GetValue("ComponentId") is not string componentId
                    || !(componentId.Equals("tap0901", StringComparison.OrdinalIgnoreCase)
                         || componentId.Equals(@"root\tap0901", StringComparison.OrdinalIgnoreCase))
                    || key.GetValue("NetCfgInstanceId") is not string id)
                    continue;

                using var connection = Registry.LocalMachine.OpenSubKey($@"{NetworkConnections}\{id}\Connection");
                var name = connection?.GetValue("Name") as string ?? id;
                var nic = interfaces.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                adapters.Add(new TapAdapter(id, name, key.GetValue("DriverDesc") as string ?? "TAP-Windows", nic?.GetPhysicalAddress()));
            }
            return adapters;
        }

        private static RegistryKey? TryOpen(RegistryKey parent, string name)
        {
            try
            {
                return parent.OpenSubKey(name);
            }
            catch (System.Security.SecurityException)
            {
                return null;   // "Properties" and the like are not readable, and not adapters
            }
        }
    }

    /// <summary>
    /// An open TAP-Windows device: Ethernet frames this PC sends on the adapter come out of
    /// <see cref="ReadAsync"/>, and frames written are received by this PC as if from the wire.
    /// </summary>
    /// <remarks>
    /// The device stays "cable unplugged" until told otherwise, so Windows would not even ask for an
    /// address on it: the media status is set connected once opened, and drops back when it is closed.
    /// </remarks>
    public sealed class TapDevice : IAsyncDisposable
    {
        // TAP_WIN_CONTROL_CODE(6, METHOD_BUFFERED): CTL_CODE(FILE_DEVICE_UNKNOWN, 6, METHOD_BUFFERED, FILE_ANY_ACCESS).
        private const uint SetMediaStatus = 0x00220018;

        private readonly FileStream _stream;

        private TapDevice(FileStream stream)
        {
            _stream = stream;
        }

        /// <summary>Opens the adapter's device and plugs its virtual cable in.</summary>
        public static TapDevice Open(TapAdapter adapter)
        {
            var handle = CreateFileW($@"\\.\Global\{adapter.Id}.tap", GenericRead | GenericWrite, 0, IntPtr.Zero,
                OpenExisting, FileAttributeSystem | FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new InvalidOperationException(error switch
                {
                    5 => $"Windows refuse à WinQEMU l'accès à la carte « {adapter.Name} » : dans ses propriétés " +
                         "(Gestionnaire de périphériques, onglet Avancé), réglez « Non-Admin Access » sur « Allowed ».",
                    32 => $"La carte « {adapter.Name} » est déjà utilisée par un autre programme (OpenVPN, QEMU…).",
                    _ => $"La carte « {adapter.Name} » n'a pas pu être ouverte : {new Win32Exception(error).Message}"
                });
            }

            var connected = BitConverter.GetBytes(1);
            if (!DeviceIoControl(handle, SetMediaStatus, connected, connected.Length, connected, connected.Length, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new InvalidOperationException($"La carte « {adapter.Name} » n'a pas pu être connectée : {new Win32Exception(error).Message}");
            }

            // One frame per read and per write: that is how the TAP driver hands packets over.
            return new TapDevice(new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true));
        }

        /// <summary>The next frame this PC sent on the adapter.</summary>
        public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[65536];
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer[..read];
        }

        public Task WriteAsync(byte[] frame, CancellationToken cancellationToken) =>
            _stream.WriteAsync(frame, cancellationToken).AsTask();

        public ValueTask DisposeAsync() => _stream.DisposeAsync();

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3;
        private const uint FileAttributeSystem = 0x4, FileFlagOverlapped = 0x40000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
            byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }
}
