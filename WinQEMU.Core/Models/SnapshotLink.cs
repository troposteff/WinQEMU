namespace WinQEMU.Core.Models
{
    /// <summary>A snapshot and the one it was taken from ("" for the machine's original state).</summary>
    public sealed class SnapshotLink
    {
        public string Name { get; set; } = string.Empty;

        public string Parent { get; set; } = string.Empty;
    }
}
