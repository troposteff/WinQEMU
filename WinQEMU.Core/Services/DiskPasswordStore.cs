using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Remembered passwords of encrypted machines (« Mémoriser »), in the Windows Credential Manager: a generic
    /// credential per machine, "WinQEMU/&lt;machine id&gt;", which Windows protects with the user's session (DPAPI) and
    /// lists under « Informations d'identification Windows ». Nothing in WinQEMU's own files.
    /// </summary>
    /// <remarks>
    /// Keyed by the machine's id, not its name: renaming keeps the password, and a clone (new id) does not inherit one
    /// unless WinQEMU copies it on purpose. Persisted for this computer (CRED_PERSIST_LOCAL_MACHINE), not roamed.
    /// </remarks>
    public static class DiskPasswordStore
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        public static string TargetFor(Guid machineId) => $"WinQEMU/{machineId:D}";

        public static void Save(Guid machineId, string password, string machineName = "")
        {
            var blob = Encoding.Unicode.GetBytes(password);
            var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
            try
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = TargetFor(machineId),
                    Comment = machineName,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = handle.AddrOfPinnedObject(),
                    Persist = CredPersistLocalMachine,
                    UserName = "WinQEMU"
                };
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Array.Clear(blob);
                handle.Free();
            }
        }

        /// <summary>The remembered password, or null when there is none.</summary>
        public static string? Read(Guid machineId)
        {
            if (!CredRead(TargetFor(machineId), CredTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorNotFound ? null : throw new Win32Exception(error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                return credential.CredentialBlobSize == 0
                    ? null
                    : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static bool Exists(Guid machineId)
        {
            try
            {
                return Read(machineId) is not null;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        /// <summary>Forgets the password; nothing happens when none was remembered.</summary>
        public static void Delete(Guid machineId)
        {
            if (!CredDelete(TargetFor(machineId), CredTypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                    throw new Win32Exception(error);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
