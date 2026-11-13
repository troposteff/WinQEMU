namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// A live snapshot that failed after some disks were switched to their overlays. Those disks write there now, so
    /// <see cref="Switched"/> must be recorded in the descriptor all the same: dropped with the exception, the next start
    /// reopened their old images and lost everything written since.
    /// </summary>
    public sealed class LiveSnapshotIncompleteException : Exception
    {
        public LiveSnapshotIncompleteException(IReadOnlyDictionary<string, string> switched, Exception inner)
            : base(inner.Message, inner)
        {
            Switched = switched;
        }

        /// <summary>The overlay file each switched disk writes to, by disk id.</summary>
        public IReadOnlyDictionary<string, string> Switched { get; }
    }
}
