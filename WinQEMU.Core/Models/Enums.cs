namespace WinQEMU.Core.Models
{
    public enum VmState
    {
        Stopped,
        Starting,
        Running,
        Paused,
        Stopping,
        Error
    }

    public enum GuestArchitecture
    {
        X86_64,
        Aarch64
    }

    public enum FirmwareType
    {
        Bios,
        Uefi,
        UefiSecureBoot
    }

    public enum AcceleratorType
    {
        /// <summary>Use WHPX when available, fall back to TCG.</summary>
        Auto,
        /// <summary>Windows Hypervisor Platform (hardware acceleration).</summary>
        Whpx,
        /// <summary>Pure emulation. Required when the guest architecture differs from the host.</summary>
        Tcg
    }

    public enum DiskFormat
    {
        Qcow2,
        Raw,
        Vhdx,
        Vmdk,
        Vdi
    }

    public enum DiskBus
    {
        VirtioBlk,
        Sata,
        Nvme,
        Ide
    }

    public enum DiskCacheMode
    {
        Writeback,
        WriteThrough,
        None,
        Unsafe
    }

    public enum NetworkMode
    {
        None,
        /// <summary>QEMU user-mode networking (SLIRP). No driver required, but no inbound access without port forwards.</summary>
        Nat,
        /// <summary>TAP device attached to a host bridge. Requires an external TAP driver on Windows.</summary>
        Bridged,
        /// <summary>Isolated network shared only between VMs on the same segment.</summary>
        Internal
    }

    public enum NicModel
    {
        VirtioNet,
        E1000,
        E1000e,
        Rtl8139
    }

    public enum GraphicsAdapter
    {
        VirtioVga,
        Std,
        Qxl,
        Vmware,
        None
    }

    public enum PortForwardProtocol
    {
        Tcp,
        Udp
    }
}
