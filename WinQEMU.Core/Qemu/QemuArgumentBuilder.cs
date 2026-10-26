using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Translates a <see cref="VmConfig"/> into a QEMU command line. This is the only place in the
    /// application allowed to emit QEMU flags, so the generated command can be shown to the user
    /// verbatim and reproduced by hand.
    /// </summary>
    public static class QemuArgumentBuilder
    {
        /// <summary>
        /// Chooses the accelerator that will actually be used. A foreign guest ISA always falls back
        /// to TCG: WHPX virtualises the host CPU, it cannot emulate a different architecture.
        /// </summary>
        public static AcceleratorType ResolveAccelerator(VmConfig config, QemuInstallation installation)
        {
            if (config.Accelerator == AcceleratorType.Tcg)
                return AcceleratorType.Tcg;

            var guestMatchesHost = config.Architecture switch
            {
                GuestArchitecture.X86_64 => RuntimeInformation.OSArchitecture == Architecture.X64,
                GuestArchitecture.Aarch64 => RuntimeInformation.OSArchitecture == Architecture.Arm64,
                _ => false
            };

            if (!guestMatchesHost)
                return AcceleratorType.Tcg;

            // An explicit WHPX request is honoured even when the probe came back negative, so a
            // false negative in detection never locks the user out of acceleration.
            if (config.Accelerator == AcceleratorType.Whpx)
                return AcceleratorType.Whpx;

            return installation.SupportsWhpx ? AcceleratorType.Whpx : AcceleratorType.Tcg;
        }

        /// <summary>Per-VM writable copy of the UEFI variable store, holding boot entries and Secure Boot keys.</summary>
        public static string GetUefiVarsPath(VmConfig config) =>
            Path.Combine(config.FolderPath, "firmware-vars.fd");

        /// <summary>
        /// Problems that would make the VM fail to start, phrased for display. An empty list means
        /// the configuration is launchable.
        /// </summary>
        public static IReadOnlyList<string> Validate(VmConfig config, QemuInstallation installation)
        {
            var problems = new List<string>();

            if (!installation.SupportsArchitecture(config.Architecture))
                problems.Add($"QEMU has no emulator for {config.Architecture} " +
                             $"({QemuLocator.SystemBinaryName(config.Architecture)} is missing).");

            if (config.CpuCount < 1)
                problems.Add("The CPU count must be at least 1.");

            if (config.MemoryMb < 128)
                problems.Add("At least 128 MB of memory is required.");

            // QEMU only says "Invalid host forwarding rule ... (Bad guest port)" and exits; a port of 0
            // is what an untouched or half-typed field holds.
            foreach (var adapter in config.NetworkAdapters.Where(a => a.Mode == NetworkMode.Nat))
            {
                foreach (var forward in adapter.PortForwards)
                {
                    if (forward.HostPort is < 1 or > 65535 || forward.GuestPort is < 1 or > 65535)
                    {
                        problems.Add(
                            $"La redirection de port {forward.HostPort} → {forward.GuestPort} est invalide : " +
                            "le port hôte et le port invité doivent être compris entre 1 et 65535.");
                    }
                }
            }

            if (config.Firmware != FirmwareType.Bios
                && !installation.SupportsUefi(config.Architecture, config.Firmware == FirmwareType.UefiSecureBoot))
            {
                problems.Add($"UEFI firmware for {config.Architecture} was not found in " +
                             $"{installation.InstallDirectory}. Switch the VM to BIOS or install the edk2 firmware files.");
            }

            if (config.Firmware == FirmwareType.UefiSecureBoot
                && ResolveAccelerator(config, installation) == AcceleratorType.Whpx)
            {
                // Secure Boot needs SMM to keep the guest out of the variable store, and WHPX refuses
                // with "System Management Mode not supported by this hypervisor" — QEMU then exits
                // immediately. Caught here so the VM cannot be launched into that dead end.
                problems.Add(
                    "Le démarrage sécurisé exige le mode SMM, que l'hyperviseur WHPX ne fournit pas : QEMU " +
                    "refuserait de démarrer. Utilisez UEFI sans démarrage sécurisé, ou forcez l'émulation TCG " +
                    "(bien plus lente).");
            }

            foreach (var disk in config.Disks)
            {
                var path = config.ResolvePath(disk.FileName);
                if (string.IsNullOrWhiteSpace(path))
                    problems.Add($"Disk '{disk.Id}' has no file path.");
                else if (!File.Exists(path))
                    problems.Add($"Disk image not found: {path}");
            }

            if (!string.IsNullOrWhiteSpace(config.IsoPath) && !File.Exists(config.ResolvePath(config.IsoPath)))
                problems.Add($"ISO not found: {config.ResolvePath(config.IsoPath)}");

            if (!string.IsNullOrWhiteSpace(config.SecondaryIsoPath) && !File.Exists(config.ResolvePath(config.SecondaryIsoPath)))
                problems.Add($"Secondary ISO not found: {config.ResolvePath(config.SecondaryIsoPath)}");

            if (config.Disks.Count == 0 && string.IsNullOrWhiteSpace(config.IsoPath))
                problems.Add("The VM has neither a disk nor an ISO, so there is nothing to boot.");

            if (config.Disks.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != config.Disks.Count)
                problems.Add("Two disks share the same identifier.");

            if (ResolveAccelerator(config, installation) == AcceleratorType.Whpx
                && config.CpuModel.Split(',')[0].Trim() is "max" or "host")
            {
                // Observed with QEMU 11 on WHPX: the guest never executes a single instruction, so not
                // even the firmware appears — the console just reads "Display output is not active"
                // and nothing is logged. Refused up front rather than left as a blank screen.
                problems.Add(
                    $"Le modèle de processeur « {config.CpuModel} » ne démarre pas avec l'accélération WHPX : " +
                    "la machine reste totalement muette, sans même afficher le microprogramme. Utilisez un " +
                    "modèle nommé, par exemple qemu64 avec les extensions voulues.");
            }

            if (config.Architecture != GuestArchitecture.Aarch64)
            {
                // q35's ICH9 AHCI controller exposes six ports; SATA/IDE disks and optical drives share them.
                var ahciPorts = config.Disks.Count(d => d.Bus is DiskBus.Sata or DiskBus.Ide)
                                // The primary optical drive is always attached, empty or not, so it
                                // always occupies a port.
                                + 1
                                + (string.IsNullOrWhiteSpace(config.SecondaryIsoPath) ? 0 : 1);
                if (ahciPorts > 6)
                {
                    problems.Add($"{ahciPorts} périphériques réclament un port SATA alors que le chipset q35 " +
                                 "n'en expose que 6. Passez des disques en virtio ou NVMe.");
                }
            }

            return problems;
        }

        /// <param name="clipboardVncPort">
        /// Loopback port for the clipboard channel, or null to leave the clipboard unshared.
        /// </param>
        public static IReadOnlyList<string> Build(
            VmConfig config, QemuInstallation installation, int qmpPort, int? clipboardVncPort = null)
        {
            var args = new List<string>();

            AppendPlatform(args, config, installation);
            AppendFirmware(args, config, installation);
            AppendStorage(args, config);
            AppendNetworking(args, config);
            AppendDisplayAndInput(args, config);
            AppendClipboard(args, clipboardVncPort);
            AppendManagement(args, qmpPort);

            foreach (var token in TokenizeExtraArguments(config.ExtraArguments))
                args.Add(token);

            return args;
        }

        private static void AppendPlatform(List<string> args, VmConfig config, QemuInstallation installation)
        {
            args.Add("-name");
            // -name takes sub-options (process=, debug-threads=), so a comma in the VM name would
            // otherwise be parsed as the start of one.
            args.Add(EscapeOptionValue(config.Name));

            var machine = new StringBuilder(ResolveMachineType(config));
            if (config.Firmware == FirmwareType.UefiSecureBoot)
            {
                // Secure Boot needs System Management Mode so the firmware can protect the
                // variable store from the guest.
                machine.Append(",smm=on");
            }

            args.Add("-machine");
            args.Add(machine.ToString());

            var accelerator = ResolveAccelerator(config, installation);
            args.Add("-accel");
            args.Add(accelerator == AcceleratorType.Whpx
                // WHPX has no in-kernel interrupt controller; leaving kernel-irqchip at its default
                // makes QEMU refuse to start.
                ? "whpx,kernel-irqchip=off"
                : "tcg,thread=multi");

            if (accelerator == AcceleratorType.Whpx)
            {
                // Under WHPX a reset never comes back from UEFI firmware: the vCPU hits an unrecoverable
                // exception ("WHPX: Unexpected VP exit code 4") as soon as OVMF restarts, and QEMU stops
                // the VM — the shell then shows it as paused. This happens with one vCPU or four, with or
                // without XSAVE, even when the reset arrives while the firmware itself is still running.
                // So a reboot, from the guest or from the reset button, ends the process instead, and the
                // session starts it again from cold. See VmSession.OnQmpEvent.
                args.Add("-action");
                args.Add("reboot=shutdown");
            }

            var cpuModel = string.IsNullOrWhiteSpace(config.CpuModel) ? "max" : config.CpuModel;
            args.Add("-cpu");
            args.Add(accelerator == AcceleratorType.Whpx && config.Architecture == GuestArchitecture.X86_64
                ? WithConsistentCetState(cpuModel)
                : cpuModel);

            args.Add("-smp");
            args.Add(config.CoresPerSocket > 1
                ? string.Create(CultureInfo.InvariantCulture,
                    $"cpus={config.CpuCount},cores={config.CoresPerSocket},sockets={Math.Max(1, config.CpuCount / config.CoresPerSocket)},threads=1")
                : config.CpuCount.ToString(CultureInfo.InvariantCulture));

            args.Add("-m");
            args.Add($"{config.MemoryMb.ToString(CultureInfo.InvariantCulture)}M");

            args.Add("-rtc");
            args.Add(config.RtcLocalTime ? "base=localtime" : "base=utc");

            if (config.EnableBalloon)
            {
                args.Add("-device");
                args.Add("virtio-balloon");
            }

            // The USB controller is declared here, before any device that plugs into it: on aarch64
            // the optical drive is USB mass storage, and it needs its bus to already exist.
            if (config.EnableUsbTablet || NeedsUsbOpticalDrive(config))
            {
                if (config.Architecture == GuestArchitecture.Aarch64)
                {
                    // 'virt' has no built-in USB; -usb would silently give us nothing.
                    args.Add("-device");
                    args.Add("qemu-xhci,id=xhci");
                }
                else
                {
                    args.Add("-usb");
                }
            }
        }

        /// <summary>True when this guest's optical drives have to be USB rather than IDE.</summary>
        private static bool NeedsUsbOpticalDrive(VmConfig config) =>
            config.Architecture == GuestArchitecture.Aarch64
            && (!string.IsNullOrWhiteSpace(config.IsoPath) || !string.IsNullOrWhiteSpace(config.SecondaryIsoPath));

        /// <summary>
        /// Flags that keep the guest's view of CET (shadow stacks, IBT) consistent with the XSAVE
        /// state the guest is told it can save.
        /// </summary>
        private static readonly string[] CetStateFlags = { "xsave", "xsavec", "xsaves", "cet-ss", "cet-ibt" };

        /// <summary>
        /// Appends the CPUID flags WHPX needs for a guest to context-switch CET state, unless the model
        /// already mentions them either way.
        /// </summary>
        /// <remarks>
        /// Under WHPX, QEMU only answers some CPUID leaves itself; leaf 7 comes straight from the host,
        /// and with it the shadow-stack and IBT bits. The named model, meanwhile, declares no XSAVES and no
        /// CET component in leaf 0xD. A Linux guest then advertises user_shstk while saving only x87/SSE
        /// state, so a process's shadow-stack pointer is never saved or restored: once one process turns
        /// shadow stacks on, every process scheduled after it on that CPU inherits its pointer and dies on
        /// its first <c>ret</c> — "segfault ... error 44" in several unrelated processes at the same
        /// address, then systemd, then "Attempted to kill init! exitcode=0x0000000b".
        ///
        /// Masking leaf 7 (<c>-cet-ss</c>) is not an option: WHPX ignores it. Declaring the matching XSAVE
        /// state instead makes the guest enable the CET user/supervisor components (xstate 0x1803 instead
        /// of 0x3), which carries the pointer across context switches. On a host without CET or XSAVES,
        /// QEMU filters these flags out as it does any unsupported feature, and the guest stays consistent.
        ///
        /// A flag the model already names — with + or - — is left as the user wrote it.
        /// </remarks>
        private static string WithConsistentCetState(string cpuModel)
        {
            var parts = cpuModel.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Skip(1))
            {
                var name = part.TrimStart('+', '-');
                var equals = name.IndexOf('=');
                named.Add(equals >= 0 ? name[..equals] : name);
            }

            var missing = CetStateFlags.Where(flag => !named.Contains(flag)).Select(flag => "+" + flag);
            return string.Join(',', parts.Concat(missing));
        }

        private static string ResolveMachineType(VmConfig config)
        {
            if (config.Architecture == GuestArchitecture.Aarch64)
            {
                // There is no q35 on ARM; 'virt' is the only generally useful machine.
                return string.IsNullOrWhiteSpace(config.MachineType) || config.MachineType is "q35" or "pc"
                    ? "virt"
                    : config.MachineType;
            }

            return string.IsNullOrWhiteSpace(config.MachineType) ? "q35" : config.MachineType;
        }

        private static void AppendFirmware(List<string> args, VmConfig config, QemuInstallation installation)
        {
            if (config.Firmware == FirmwareType.Bios)
                return;

            var secureBoot = config.Firmware == FirmwareType.UefiSecureBoot;
            var code = config.Architecture switch
            {
                GuestArchitecture.X86_64 => secureBoot ? installation.UefiCodeX64SecureBoot : installation.UefiCodeX64,
                GuestArchitecture.Aarch64 => installation.UefiCodeAarch64,
                _ => null
            };

            if (code is null)
                throw new QemuNotAvailableException($"No UEFI firmware image available for {config.Architecture}.");

            // Firmware is split in two flash banks: read-only code, and a writable variable store
            // that must be private to this VM or boot entries would leak between machines.
            args.Add("-drive");
            args.Add($"if=pflash,format=raw,unit=0,readonly=on,file={EscapeOptionValue(code)}");

            args.Add("-drive");
            args.Add($"if=pflash,format=raw,unit=1,file={EscapeOptionValue(GetUefiVarsPath(config))}");

            if (secureBoot)
            {
                args.Add("-global");
                args.Add("driver=cfi.pflash01,property=secure,value=on");
                args.Add("-global");
                args.Add("ICH9-LPC.disable_s3=1");
            }
        }

        /// <summary>Primary and secondary optical drive block-node ids.</summary>
        public const string PrimaryCdId = "cd0";
        public const string SecondaryCdId = "cd1";

        /// <summary>
        /// The qdev id of the device a block node is attached to. QMP addresses a medium change by the
        /// device, not the node, and this is the single place that decides how the two are named — the
        /// live medium change used to target the legacy "ide1-cd0" and could never have worked against
        /// the explicit devices emitted here.
        /// </summary>
        public static string DeviceIdFor(string blockNodeId) => blockNodeId + "-dev";

        /// <summary>
        /// Every device that can be booted, in the order the fallback rules would try them. The shell
        /// uses this to present a boot list without duplicating the naming rules for optical drives.
        /// </summary>
        public static IReadOnlyList<string> EnumerateBootableIds(VmConfig config)
        {
            var ids = new List<string>();

            if (!string.IsNullOrWhiteSpace(config.IsoPath))
                ids.Add(PrimaryCdId);

            ids.AddRange(config.Disks.Select(d => d.Id));

            if (!string.IsNullOrWhiteSpace(config.SecondaryIsoPath))
                ids.Add(SecondaryCdId);

            return ids;
        }

        /// <summary>
        /// Assigns each bootable device its position in the boot order.
        /// </summary>
        /// <remarks>
        /// UEFI firmware ignores <c>-boot order=</c> entirely; the only ordering signal it honours is
        /// a <c>bootindex</c> on each device. Without one on the optical drive, a VM configured to
        /// boot from an ISO would try the empty disk and drop into the UEFI shell instead.
        /// </remarks>
        private static Dictionary<string, int> BuildBootOrder(VmConfig config)
        {
            var available = EnumerateBootableIds(config);

            if (config.BootOrder.Count > 0)
            {
                // Chosen order first, restricted to devices that still exist — a disk can be detached
                // after the order was set. Anything the user never ranked is appended rather than
                // dropped: a device with no bootindex at all is invisible to the UEFI boot manager,
                // which is exactly how an ISO used to end up unbootable.
                var ranked = config.BootOrder
                    .Where(id => available.Contains(id, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                ranked.AddRange(available.Where(id => !ranked.Contains(id, StringComparer.Ordinal)));
                return ToBootIndexMap(ranked);
            }

            var order = new List<string>();
            var hasIso = !string.IsNullOrWhiteSpace(config.IsoPath);

            if (config.BootFromIso && hasIso)
                order.Add(PrimaryCdId);

            foreach (var disk in config.Disks.Where(d => d.BootIndex >= 0).OrderBy(d => d.BootIndex))
                order.Add(disk.Id);

            if (!config.BootFromIso && hasIso)
                order.Add(PrimaryCdId);

            // The secondary drive carries drivers, never the installer, so it comes last.
            if (!string.IsNullOrWhiteSpace(config.SecondaryIsoPath))
                order.Add(SecondaryCdId);

            return ToBootIndexMap(order);
        }

        private static Dictionary<string, int> ToBootIndexMap(IEnumerable<string> order) =>
            order
                .Select((id, index) => (id, index))
                .ToDictionary(entry => entry.id, entry => entry.index, StringComparer.Ordinal);

        private static void AppendStorage(List<string> args, VmConfig config)
        {
            var ideUnit = 0;
            var bootOrder = BuildBootOrder(config);

            foreach (var disk in config.Disks)
            {
                var path = config.ResolvePath(disk.FileName);
                var drive = new StringBuilder($"if=none,id={disk.Id},file={EscapeOptionValue(path)}");
                drive.Append($",format={FormatName(disk.Format)}");
                drive.Append($",cache={CacheName(disk.Cache)}");
                if (disk.Discard)
                    drive.Append(",discard=unmap");
                if (disk.ReadOnly)
                    drive.Append(",readonly=on");

                args.Add("-drive");
                args.Add(drive.ToString());

                var device = new StringBuilder();
                switch (disk.Bus)
                {
                    case DiskBus.VirtioBlk:
                        device.Append($"virtio-blk-pci,drive={disk.Id}");
                        break;
                    case DiskBus.Nvme:
                        device.Append($"nvme,drive={disk.Id},serial={disk.Id}");
                        break;
                    default:
                        // q35 exposes its AHCI controller as the 'ide' bus; SATA and IDE both land here.
                        device.Append($"ide-hd,drive={disk.Id},bus=ide.{ideUnit.ToString(CultureInfo.InvariantCulture)}");
                        ideUnit++;
                        break;
                }

                device.Append($",id={DeviceIdFor(disk.Id)}");
                if (bootOrder.TryGetValue(disk.Id, out var diskBootIndex))
                    device.Append($",bootindex={diskBootIndex.ToString(CultureInfo.InvariantCulture)}");

                args.Add("-device");
                args.Add(device.ToString());
            }

            AppendOpticalDrive(args, config, config.IsoPath, PrimaryCdId, bootOrder, ref ideUnit);
            AppendOpticalDrive(args, config, config.SecondaryIsoPath, SecondaryCdId, bootOrder, ref ideUnit);

            // menu=on only. The order is expressed through bootindex above, which both SeaBIOS and
            // UEFI honour, whereas order= is silently ignored by UEFI.
            args.Add("-boot");
            args.Add("menu=on");
        }

        /// <summary>
        /// Attaches an ISO as a real optical device.
        /// </summary>
        /// <remarks>
        /// The legacy <c>-drive media=cdrom,index=N</c> form was used here before and produced nothing
        /// usable on q35: that machine has no legacy IDE controller, so the drive never appeared in the
        /// firmware's device map and no ISO could ever be booted. An explicit device also gives us
        /// somewhere to put the bootindex.
        /// </remarks>
        private static void AppendOpticalDrive(
            List<string> args,
            VmConfig config,
            string isoPath,
            string driveId,
            Dictionary<string, int> bootOrder,
            ref int ideUnit)
        {
            var hasMedium = !string.IsNullOrWhiteSpace(isoPath);

            // The primary drive is present even with nothing in it, on x86. An absent device cannot be
            // filled later: swapping the medium of a running machine addresses a drive by its qdev id,
            // so a machine started without an ISO could never be handed one — which is exactly when a
            // driver disc is needed. An empty tray costs one AHCI port and is invisible to the boot
            // manager, since only a drive holding something is given a bootindex.
            // Not on aarch64: there the drive is usb-storage, which refuses to start without media.
            var alwaysPresent = driveId == PrimaryCdId && config.Architecture != GuestArchitecture.Aarch64;

            if (!hasMedium && !alwaysPresent)
                return;

            args.Add("-drive");
            var drive = new StringBuilder($"if=none,id={driveId},media=cdrom,readonly=on");
            if (hasMedium)
                drive.Append($",file={EscapeOptionValue(config.ResolvePath(isoPath))}");
            args.Add(drive.ToString());

            var device = new StringBuilder();
            if (config.Architecture == GuestArchitecture.Aarch64)
            {
                // The 'virt' machine has no IDE/AHCI controller at all; USB mass storage is the
                // portable way to present removable media there.
                device.Append($"usb-storage,drive={driveId},id={DeviceIdFor(driveId)}");
            }
            else
            {
                device.Append($"ide-cd,drive={driveId},bus=ide.{ideUnit.ToString(CultureInfo.InvariantCulture)},id={DeviceIdFor(driveId)}");
                ideUnit++;
            }

            if (bootOrder.TryGetValue(driveId, out var bootIndex))
                device.Append($",bootindex={bootIndex.ToString(CultureInfo.InvariantCulture)}");

            args.Add("-device");
            args.Add(device.ToString());
        }

        private static void AppendNetworking(List<string> args, VmConfig config)
        {
            var active = config.NetworkAdapters.Where(a => a.Mode != NetworkMode.None).ToList();
            if (active.Count == 0)
            {
                args.Add("-nic");
                args.Add("none");
                return;
            }

            foreach (var adapter in active)
            {
                var netdev = new StringBuilder();
                switch (adapter.Mode)
                {
                    case NetworkMode.Nat:
                        netdev.Append($"user,id={adapter.Id}");
                        foreach (var forward in adapter.PortForwards)
                        {
                            var protocol = forward.Protocol == PortForwardProtocol.Udp ? "udp" : "tcp";
                            var host = string.IsNullOrWhiteSpace(forward.HostAddress) ? "127.0.0.1" : forward.HostAddress;
                            netdev.Append($",hostfwd={protocol}:{host}:{forward.HostPort.ToString(CultureInfo.InvariantCulture)}-:{forward.GuestPort.ToString(CultureInfo.InvariantCulture)}");
                        }
                        break;

                    case NetworkMode.Bridged:
                        netdev.Append($"tap,id={adapter.Id}");
                        if (!string.IsNullOrWhiteSpace(adapter.BridgeInterface))
                            netdev.Append($",ifname={EscapeOptionValue(adapter.BridgeInterface)}");
                        break;

                    case NetworkMode.Internal:
                        // A multicast socket backend gives VMs a shared segment with no host route,
                        // which is what an isolated internal network means.
                        netdev.Append($"socket,id={adapter.Id},mcast=230.0.0.{Math.Clamp(adapter.InternalSegment, 1, 254).ToString(CultureInfo.InvariantCulture)}:1234");
                        break;
                }

                args.Add("-netdev");
                args.Add(netdev.ToString());

                var device = new StringBuilder($"{NicDeviceName(adapter.Model)},netdev={adapter.Id}");
                if (!string.IsNullOrWhiteSpace(adapter.MacAddress))
                    device.Append($",mac={adapter.MacAddress}");

                args.Add("-device");
                args.Add(device.ToString());
            }
        }

        private static void AppendDisplayAndInput(List<string> args, VmConfig config)
        {
            args.Add("-display");
            // SDL is what the shell reparents into a tab; disabling host GL avoids driver-dependent
            // failures on machines without a usable OpenGL context.
            // The window stays closable on purpose. window-close=off was tried to stop Alt+F4 from
            // killing the VM behind the application's back, but whenever embedding does not happen
            // the user is left with a QEMU window that cannot be dismissed at all — a far worse
            // outcome. Closing the console powers the VM off, and the session reports that normally.
            args.Add(config.Graphics == GraphicsAdapter.None ? "none" : "sdl,gl=off");

            args.Add("-vga");
            args.Add(config.Graphics switch
            {
                GraphicsAdapter.VirtioVga => "virtio",
                GraphicsAdapter.Std => "std",
                GraphicsAdapter.Qxl => "qxl",
                GraphicsAdapter.Vmware => "vmware",
                _ => "none"
            });

            if (config.EnableUsbTablet)
            {
                // The controller itself was declared with the platform, ahead of anything using it.
                args.Add("-device");
                args.Add("usb-tablet");
            }

            if (config.EnableAudio)
            {
                args.Add("-audiodev");
                args.Add("dsound,id=snd0");
                args.Add("-device");
                args.Add("intel-hda");
                args.Add("-device");
                args.Add("hda-duplex,audiodev=snd0");
            }
        }

        /// <summary>Device id of the virtio-serial port spice-vdagent opens inside the guest.</summary>
        public const string ClipboardAgentPortId = "wq-vdagent-port";

        /// <summary>Lowest port a VNC display can use: QEMU listens on 5900 + display number.</summary>
        public const int VncBasePort = 5900;

        /// <summary>
        /// The clipboard channel: qemu-vdagent towards the guest, a VNC server towards the host.
        /// </summary>
        /// <remarks>
        /// QEMU's SDL display has no clipboard support, so the shell cannot get it from the console
        /// window. In the guest, spice-vdagent finds the agent on the virtio-serial port named
        /// com.redhat.spice.0 — the name is fixed by the agent. On the host, the VNC server is the only
        /// clipboard peer QEMU's Windows build offers; the session connects to it with
        /// <see cref="VncClipboardClient"/> and never requests a single pixel. It listens on loopback
        /// only, and with password=on nobody can connect until the session has set a random password
        /// over QMP.
        /// </remarks>
        private static void AppendClipboard(List<string> args, int? vncPort)
        {
            if (vncPort is not { } port || port < VncBasePort)
                return;

            args.Add("-device");
            args.Add("virtio-serial-pci,id=wq-agent-bus");
            args.Add("-chardev");
            args.Add("qemu-vdagent,id=wq-vdagent,name=vdagent,clipboard=on");
            args.Add("-device");
            // The id is what makes QEMU report the agent connecting (VSERPORT_CHANGE); see VmSession.
            args.Add($"virtserialport,bus=wq-agent-bus.0,chardev=wq-vdagent,name=com.redhat.spice.0,id={ClipboardAgentPortId}");

            args.Add("-vnc");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port - VncBasePort},password=on"));
        }

        private static void AppendManagement(List<string> args, int qmpPort)
        {
            // QMP on loopback TCP. The 'pipe' chardev is not reliably full-duplex on Windows,
            // which QMP requires; 'server=on,wait=off' lets QEMU boot without waiting for us.
            args.Add("-qmp");
            args.Add($"tcp:127.0.0.1:{qmpPort.ToString(CultureInfo.InvariantCulture)},server=on,wait=off");
        }

        private static string FormatName(DiskFormat format) => format switch
        {
            DiskFormat.Qcow2 => "qcow2",
            DiskFormat.Raw => "raw",
            DiskFormat.Vhdx => "vhdx",
            DiskFormat.Vmdk => "vmdk",
            DiskFormat.Vdi => "vdi",
            _ => "qcow2"
        };

        private static string CacheName(DiskCacheMode cache) => cache switch
        {
            DiskCacheMode.Writeback => "writeback",
            DiskCacheMode.WriteThrough => "writethrough",
            DiskCacheMode.None => "none",
            DiskCacheMode.Unsafe => "unsafe",
            _ => "writeback"
        };

        private static string NicDeviceName(NicModel model) => model switch
        {
            NicModel.VirtioNet => "virtio-net-pci",
            NicModel.E1000 => "e1000",
            NicModel.E1000e => "e1000e",
            NicModel.Rtl8139 => "rtl8139",
            _ => "virtio-net-pci"
        };

        /// <summary>
        /// QEMU's option parser treats a comma as a field separator, so a literal comma in a value
        /// (common in Windows paths under "Program Files, Inc"-style folders) must be doubled.
        /// </summary>
        public static string EscapeOptionValue(string value) => value.Replace(",", ",,");

        /// <summary>Splits user-supplied extra arguments on whitespace, honouring double quotes.</summary>
        public static IReadOnlyList<string> TokenizeExtraArguments(string raw)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return tokens;

            var current = new StringBuilder();
            var inQuotes = false;

            foreach (var c in raw)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens;
        }

        /// <summary>Renders a copy-pasteable command line for the log and the settings UI.</summary>
        public static string ToDisplayString(string executable, IEnumerable<string> arguments)
        {
            var builder = new StringBuilder(Quote(executable));
            foreach (var argument in arguments)
            {
                builder.Append(' ');
                builder.Append(Quote(argument));
            }
            return builder.ToString();

            static string Quote(string value) =>
                value.Contains(' ') || value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
        }
    }
}
