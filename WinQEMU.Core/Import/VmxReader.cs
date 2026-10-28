using System.Globalization;
using System.Text.RegularExpressions;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Import
{
    /// <summary>
    /// Reads a VMware machine from its .vmx: a flat list of <c>key = "value"</c> lines, keys
    /// case-insensitive, special characters escaped as <c>|XX</c> in hexadecimal.
    /// </summary>
    public static class VmxReader
    {
        public static ImportPlan Read(string vmxPath)
        {
            var full = Path.GetFullPath(vmxPath);
            var plan = Parse(File.ReadAllText(full), full);
            var folder = Path.GetDirectoryName(full)!;

            // VMware keeps a .lck folder next to every file it has open. Converting a disk a running machine
            // writes to would copy it caught mid-write.
            if (Directory.EnumerateFileSystemEntries(folder, "*.lck").Any())
            {
                plan.Blocker ??= PlatformText.ImportVmwareLocked;
            }

            if (Directory.EnumerateFiles(folder, "*.vmss").Any())
            {
                plan.Notes.Add(PlatformText.ImportVmwareSuspended);
            }

            foreach (var disk in plan.Disks.Where(d => !File.Exists(d.SourcePath)))
                plan.Blocker ??= string.Format(CultureInfo.CurrentCulture, PlatformText.ImportDiskNotFound, disk.SourcePath);

            return plan;
        }

        /// <param name="vmxPath">Absolute path of the .vmx, against which relative file names resolve.</param>
        public static ImportPlan Parse(string text, string vmxPath)
        {
            var values = ParseValues(text);
            var folder = Path.GetDirectoryName(vmxPath)!;
            string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
            bool IsTrue(string key) => string.Equals(Get(key), "TRUE", StringComparison.OrdinalIgnoreCase);
            bool IsPresent(string device) => !string.Equals(Get(device + ".present"), "FALSE", StringComparison.OrdinalIgnoreCase);
            string Resolve(string file) => Path.GetFullPath(Path.Combine(folder, file));

            var plan = new ImportPlan
            {
                Source = ImportSource.VMware,
                SourcePath = vmxPath,
                Name = Get("displayName") ?? Path.GetFileNameWithoutExtension(vmxPath),
                SourceGuestOs = Get("guestOS") ?? string.Empty
            };
            plan.GuestOs = GuestOsMapping.FromVmware(plan.SourceGuestOs);

            var profile = plan.Profile;
            plan.Firmware = string.Equals(Get("firmware"), "efi", StringComparison.OrdinalIgnoreCase) ? FirmwareType.Uefi : FirmwareType.Bios;
            plan.MemoryMb = ParseInt(Get("memsize")) ?? profile.MemoryMb;
            // VMware writes nothing for its default of one processor.
            plan.CpuCount = ParseInt(Get("numvcpus")) ?? 1;
            var cores = ParseInt(Get("cpuid.coresPerSocket")) ?? 1;
            plan.CoresPerSocket = cores > 1 && plan.CpuCount % cores == 0 ? cores : 1;

            if (plan.Firmware == FirmwareType.Uefi && IsTrue("uefi.secureBoot.enabled"))
            {
                plan.Notes.Add(PlatformText.ImportVmwareSecureBootDropped);
            }

            if (Get("encryption.keySafe") is not null || Get("encryption.data") is not null)
                plan.Blocker = PlatformText.ImportVmwareEncrypted;

            ReadDisks(plan, values, IsPresent, Resolve);
            ReadAdapters(plan, values, IsPresent);
            return plan;
        }

        private static readonly Regex DeviceKey = new(
            @"^(?<kind>scsi|sata|nvme|ide)(?<ctrl>\d+):(?<unit>\d+)\.filename$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void ReadDisks(
            ImportPlan plan, IReadOnlyDictionary<string, string> values, Func<string, bool> isPresent, Func<string, string> resolve)
        {
            var found = new List<(int Rank, int Controller, int Unit, ImportedDisk Disk)>();
            foreach (var (key, file) in values)
            {
                var match = DeviceKey.Match(key);
                if (!match.Success)
                    continue;

                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                var controller = int.Parse(match.Groups["ctrl"].Value, CultureInfo.InvariantCulture);
                var unit = int.Parse(match.Groups["unit"].Value, CultureInfo.InvariantCulture);
                var device = $"{kind}{controller}:{unit}";
                if (!isPresent(device) || !isPresent($"{kind}{controller}") || string.IsNullOrWhiteSpace(file))
                    continue;

                var deviceType = values.GetValueOrDefault(device + ".deviceType") ?? string.Empty;
                if (deviceType.Contains("cdrom", StringComparison.OrdinalIgnoreCase))
                {
                    // Only an image can come along; a physical drive of the old host means nothing here.
                    if (string.IsNullOrEmpty(plan.IsoPath) && file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(resolve(file)))
                        plan.IsoPath = resolve(file);
                    continue;
                }

                var controllerModel = values.GetValueOrDefault($"{kind}{controller}.virtualDev") ?? string.Empty;
                var bus = BusFor(kind, controllerModel);
                if (kind == "scsi" && bus == DiskBus.Sata)
                {
                    AddOnce(plan.Notes,
                        string.Format(CultureInfo.CurrentCulture, PlatformText.ImportScsiControllerToSata,
                            controllerModel.Length == 0 ? "lsilogic" : controllerModel) +
                        (plan.IsWindows
                            ? " " + PlatformText.ImportAhciHintVmware
                            : string.Empty));
                }

                found.Add((Rank(kind), controller, unit, new ImportedDisk
                {
                    SourcePath = resolve(file),
                    Bus = bus,
                    SourceSlot = controllerModel.Length == 0 ? device : $"{device} ({controllerModel})"
                }));
            }

            // The firmware boots the first disk; VMware's own default order is the same as this one.
            foreach (var entry in found.OrderBy(f => f.Rank).ThenBy(f => f.Controller).ThenBy(f => f.Unit))
                plan.Disks.Add(entry.Disk);
        }

        private static int Rank(string kind) => kind switch
        {
            "nvme" => 0,
            "scsi" => 1,
            "sata" => 2,
            _ => 3
        };

        /// <summary>
        /// The same controller where QEMU has it: a Windows guest only boots from a controller whose driver
        /// it starts at boot, which is the one it was installed on.
        /// </summary>
        private static DiskBus BusFor(string kind, string controllerModel) => kind switch
        {
            "nvme" => DiskBus.Nvme,
            "ide" => DiskBus.Ide,
            "sata" => DiskBus.Sata,
            _ => controllerModel.ToLowerInvariant() switch
            {
                "lsisas1068" => DiskBus.LsiSas,
                "pvscsi" => DiskBus.Pvscsi,
                // lsilogic (parallel LSI 53c1030) and buslogic: QEMU emulates neither.
                _ => DiskBus.Sata
            }
        };

        private static readonly Regex AdapterKey = new(@"^ethernet(?<n>\d+)\.present$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void ReadAdapters(ImportPlan plan, IReadOnlyDictionary<string, string> values, Func<string, bool> isPresent)
        {
            var adapters = values.Keys
                .Select(k => AdapterKey.Match(k))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture))
                .OrderBy(n => n);

            foreach (var index in adapters)
            {
                var prefix = $"ethernet{index.ToString(CultureInfo.InvariantCulture)}";
                if (!isPresent(prefix))
                    continue;

                var model = (values.GetValueOrDefault(prefix + ".virtualDev") ?? "e1000").ToLowerInvariant() switch
                {
                    "vmxnet3" => NicModel.Vmxnet3,
                    "e1000e" => NicModel.E1000e,
                    _ => NicModel.E1000
                };

                var mac = string.Equals(values.GetValueOrDefault(prefix + ".addressType"), "static", StringComparison.OrdinalIgnoreCase)
                    ? values.GetValueOrDefault(prefix + ".address")
                    : values.GetValueOrDefault(prefix + ".generatedAddress");

                var connection = (values.GetValueOrDefault(prefix + ".connectionType") ?? "bridged").ToLowerInvariant();
                if (connection != "nat")
                {
                    AddOnce(plan.Notes,
                        string.Format(CultureInfo.CurrentCulture, PlatformText.ImportVmwareAdapterToNat, connection));
                }

                plan.Adapters.Add(new ImportedAdapter
                {
                    Model = model,
                    MacAddress = NormalizeMac(mac),
                    Mode = NetworkMode.Nat
                });
            }
        }

        /// <summary>Every line's key (lower-cased) and value, escapes decoded. Later lines win, as in VMware.</summary>
        public static Dictionary<string, string> ParseValues(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];

                values[key] = Escape.Replace(value, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
            }

            return values;
        }

        private static readonly Regex Escape = new(@"\|([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant);

        internal static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                return string.Empty;

            var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
            return hex.Length == 12
                ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
                : string.Empty;
        }

        private static int? ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

        internal static void AddOnce(List<string> notes, string note)
        {
            if (!notes.Contains(note))
                notes.Add(note);
        }
    }
}
