using System.Runtime.InteropServices;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// What the host machine can actually back. Used to keep a VM's configuration inside the realm of
    /// the possible rather than letting the user allocate more than exists.
    /// </summary>
    public static class HostCapabilities
    {
        public static int LogicalProcessors => Environment.ProcessorCount;

        /// <summary>Installed physical memory, or 0 when the query fails.</summary>
        public static int TotalPhysicalMemoryMb
        {
            get
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status)
                    ? (int)(status.TotalPhys / 1024 / 1024)
                    : 0;
            }
        }

        /// <summary>
        /// Memory we are comfortable handing to a single guest: about two thirds of the host, leaving
        /// the host OS enough to stay responsive. QEMU commits guest RAM lazily, but a guest that
        /// touches all of it will otherwise push the host into swapping.
        /// </summary>
        public static int RecommendedMaxGuestMemoryMb
        {
            get
            {
                var total = TotalPhysicalMemoryMb;
                return total > 0 ? Math.Max(1024, total * 2 / 3) : 8192;
            }
        }

        /// <summary>Free space on the volume holding <paramref name="path"/>, or -1 when unknown.</summary>
        public static long GetFreeSpaceBytes(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                    return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
