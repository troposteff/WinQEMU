namespace WinQEMU.Core.Services
{
    /// <summary>
    /// %ProgramData%\WinQEMU in a Windows guest: where the agent, as SYSTEM, compiles the programs it then runs (file drop
    /// and retrieval receivers, drag-out and resolution helpers).
    /// </summary>
    /// <remarks>
    /// ProgramData lets every user create files in a new folder (Users: write data, CREATOR OWNER: full control). A
    /// standard user of the guest could create WinQemuDrop-&lt;hash&gt;.dll before WinQEMU did (the name is public): the
    /// receiver script saw it there, did not compile, and SYSTEM loaded the user's code. The folder is therefore given to
    /// SYSTEM, inheritance cut, Administrators full control, Users read and execute (the helpers run in their sessions);
    /// only the drag-out list stays writable by them. The first time, before it is locked, the programs already there
    /// cannot be trusted: they are stopped and deleted, and compiled again.
    /// </remarks>
    public static class GuestWorkFolder
    {
        /// <summary>A PowerShell function, <c>Protect-WinQemuFolder $dir</c>, to define at the top of a script run as SYSTEM.</summary>
        public const string ProtectFunction = """
            function Protect-WinQemuFolder($dir) {
              New-Item $dir -ItemType Directory -Force | Out-Null
              if ((Get-Acl $dir).AreAccessRulesProtected) { return }
              Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith("$dir\", [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
              $programs = { Get-ChildItem $dir -File -Force | Where-Object { $_.Extension -in '.dll', '.exe' } }
              & $programs | Remove-Item -Force -ErrorAction SilentlyContinue
              # One still in use (a receiver stuck in an earlier drop holds its DLL): try again next time, never lock it in.
              if (& $programs) { throw "$dir holds programs that could not be replaced." }
              icacls $dir /setowner '*S-1-5-18' /T /C /Q | Out-Null
              icacls $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
              if (Get-ChildItem $dir -Force) { icacls "$dir\*" /reset /T /C /Q | Out-Null }
              $list = Join-Path $dir 'dragout.txt'
              if (Test-Path $list) { icacls $list /grant '*S-1-5-32-545:M' /Q | Out-Null }
              if (-not (Get-Acl $dir).AreAccessRulesProtected) { throw "$dir could not be protected." }
            }
            """;
    }
}
