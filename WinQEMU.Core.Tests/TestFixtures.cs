using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Builders for the two inputs every command line needs. Kept deliberately explicit: a test that
/// asserts on a flag should be readable as "given this configuration, expect this flag", without
/// having to know what a shared default happened to be.
/// </summary>
internal static class Fixtures
{
    /// <summary>An installation that has everything, so a test only has to say what it lacks.</summary>
    public static QemuInstallation Installation(
        bool supportsWhpx = true,
        bool hasUefiX64 = true,
        bool hasSecureBoot = true,
        bool hasAarch64 = true) => new()
    {
        InstallDirectory = @"C:\qemu",
        SystemBinaries = hasAarch64
            ? new Dictionary<GuestArchitecture, string>
            {
                [GuestArchitecture.X86_64] = @"C:\qemu\qemu-system-x86_64.exe",
                [GuestArchitecture.Aarch64] = @"C:\qemu\qemu-system-aarch64.exe"
            }
            : new Dictionary<GuestArchitecture, string>
            {
                [GuestArchitecture.X86_64] = @"C:\qemu\qemu-system-x86_64.exe"
            },
        QemuImgPath = @"C:\qemu\qemu-img.exe",
        Version = "11.1.0",
        SupportsWhpx = supportsWhpx,
        UefiCodeX64 = hasUefiX64 ? @"C:\qemu\share\edk2-x86_64-code.fd" : null,
        UefiCodeX64SecureBoot = hasSecureBoot ? @"C:\qemu\share\edk2-x86_64-secure-code.fd" : null,
        UefiVarsTemplateX64 = hasUefiX64 ? @"C:\qemu\share\edk2-i386-vars.fd" : null,
        UefiCodeAarch64 = @"C:\qemu\share\edk2-aarch64-code.fd",
        UefiVarsTemplateAarch64 = @"C:\qemu\share\edk2-arm-vars.fd"
    };

    /// <summary>A minimal machine: one virtio disk, no ISO, no network.</summary>
    public static VmConfig Vm(string name = "Test") => new()
    {
        Name = name,
        FolderPath = @"C:\qemu\Test",
        Architecture = GuestArchitecture.X86_64,
        Firmware = FirmwareType.Uefi,
        Accelerator = AcceleratorType.Auto,
        CpuCount = 2,
        MemoryMb = 4096,
        // BootIndex 0 mirrors what the creation wizard writes for a system disk. A disk left at -1 is
        // the deliberate "not ranked" case and has its own test.
        Disks = { new DiskConfig { Id = "disk0", FileName = "disk0.qcow2", Bus = DiskBus.VirtioBlk, BootIndex = 0 } }
    };

    public static IReadOnlyList<string> Build(VmConfig config, QemuInstallation? installation = null, int qmpPort = 4444) =>
        QemuArgumentBuilder.Build(config, installation ?? Installation(), qmpPort);

    /// <summary>The value following a flag, e.g. Value(args, "-cpu"). Null when the flag is absent.</summary>
    public static string? Value(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    /// <summary>Every value following each occurrence of a repeated flag, e.g. all -device values.</summary>
    public static List<string> Values(IReadOnlyList<string> args, string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag)
                values.Add(args[i + 1]);
        }
        return values;
    }

    /// <summary>Index of the first argument equal to <paramref name="token"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<string> args, string token) => args.ToList().IndexOf(token);

    /// <summary>Index of the first argument containing <paramref name="fragment"/>, or -1.</summary>
    public static int IndexOfContaining(IReadOnlyList<string> args, string fragment)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Contains(fragment, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>The -device value for one device kind, e.g. Device(args, "ide-cd").</summary>
    public static string? Device(IReadOnlyList<string> args, string kind) =>
        Values(args, "-device").FirstOrDefault(v => v.StartsWith(kind + ",", StringComparison.Ordinal) || v == kind);

    /// <summary>The -drive value carrying a given id.</summary>
    public static string? Drive(IReadOnlyList<string> args, string id) =>
        Values(args, "-drive").FirstOrDefault(v => v.Contains($"id={id},", StringComparison.Ordinal));
}
