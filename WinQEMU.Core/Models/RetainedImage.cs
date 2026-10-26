namespace WinQEMU.Core.Models
{
    /// <summary>
    /// An overlay kept outside its machine's chain for the linked clones that read it
    /// (<see cref="DiskConfig.RetainedImages"/>). Both names are relative to the machine's folder, like the
    /// chain's.
    /// </summary>
    public sealed class RetainedImage
    {
        public string FileName { get; set; } = string.Empty;

        /// <summary>The image this one is layered on, as it was in the chain when it left it.</summary>
        public string BackingFileName { get; set; } = string.Empty;
    }
}
