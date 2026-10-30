namespace WinQEMU.Core.Models
{
    /// <summary>
    /// A <c>-cpu</c> value taken apart: the model, then its options (<c>+flag</c>, <c>-flag</c>, <c>key=value</c>), as
    /// <see cref="VmConfig.CpuModel"/> stores them in one string.
    /// </summary>
    /// <remarks>
    /// The model and the options are set apart in the interface: choosing a model from QEMU's list used to replace the
    /// whole string, and the default options (<see cref="VmConfig.DefaultCpuModel"/>, which Windows 11 needs) were lost.
    /// </remarks>
    public static class CpuModelSpec
    {
        public static string Model(string? value) =>
            string.IsNullOrWhiteSpace(value) ? Model(VmConfig.DefaultCpuModel) : value.Split(',')[0].Trim();

        public static IReadOnlyList<string> Options(string? value) =>
            (string.IsNullOrWhiteSpace(value) ? VmConfig.DefaultCpuModel : value)
                .Split(',').Skip(1).Select(o => o.Trim()).Where(o => o.Length > 0).ToList();

        /// <summary>The flags turned on (<c>+flag</c>), without their sign.</summary>
        public static IReadOnlyList<string> EnabledFlags(string? value) =>
            Options(value).Where(o => o.StartsWith('+')).Select(o => o[1..]).ToList();

        public static string Join(string model, IEnumerable<string> options) =>
            string.Join(",", new[] { model.Trim() }.Concat(options.Where(o => !string.IsNullOrWhiteSpace(o))));

        /// <summary>Another model, the options kept.</summary>
        public static string WithModel(string? value, string model) => Join(model, Options(value));

        /// <summary>
        /// <paramref name="enabled"/> as the <c>+flag</c> options; the other options (<c>-flag</c>, <c>key=value</c>) are
        /// kept as they were, those of a flag now turned on excepted.
        /// </summary>
        public static string WithFlags(string? value, IEnumerable<string> enabled)
        {
            var on = enabled.Distinct(StringComparer.Ordinal).ToList();
            var kept = Options(value).Where(o => !o.StartsWith('+') && !(o.StartsWith('-') && on.Contains(o[1..])));
            return Join(Model(value), kept.Concat(on.Select(f => "+" + f)));
        }
    }
}
