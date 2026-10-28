namespace WinQEMU.Core.Models
{
    /// <summary>Where an unattended installation stands.</summary>
    public enum UnattendedSetupState
    {
        /// <summary>None, or finished.</summary>
        None,
        /// <summary>Answer file written, machine never started: the first start presses "any key" for the CD.</summary>
        Pending,
        /// <summary>Started: Setup runs, reboots and logs on; done once the guest agent answers.</summary>
        Installing
    }
}
