using System.Runtime.InteropServices;

namespace WinQEMU.Core.Interop
{
    /// <summary>An audio output of this PC (speakers, headset…), by its Windows endpoint id.</summary>
    public sealed record AudioOutput(string Id, string Name);

    /// <summary>
    /// The machines' sound as Windows sees it: each QEMU process plays in an audio session of its own, the
    /// one Windows' volume mixer shows. QEMU has no volume or mute of its own, and no choice of output.
    /// </summary>
    /// <remarks>
    /// Volume and mute are each process's own: set on one QEMU, another running beside it keeps its own
    /// (measured). But Windows remembers the last one set for qemu-system-x86_64.exe and gives it to the
    /// next session that program opens: a machine started after another was muted started muted. So each
    /// machine's own setting is laid on every session its process opens.
    ///
    /// QEMU's DirectSound output opens its session when the guest first plays, not at start.
    /// </remarks>
    public static class HostAudio
    {
        private const int RenderFlow = 0;          // eRender
        private const int ActiveState = 0x1;       // DEVICE_STATE_ACTIVE
        private const int ClsctxAll = 23;

        /// <summary>The active outputs, in Windows' order.</summary>
        public static IReadOnlyList<AudioOutput> ListOutputs()
        {
            var outputs = new List<AudioOutput>();
            foreach (var device in ActiveOutputs())
            {
                if (device.GetId(out var id) != 0)
                    continue;
                outputs.Add(new AudioOutput(id, FriendlyName(device) ?? id));
            }
            return outputs;
        }

        /// <summary>
        /// Lays a volume (0 to 1) and a mute on the sessions of <paramref name="processId"/>, on every output,
        /// skipping the session instances listed in <paramref name="applied"/>, to which those set are added.
        /// Returns whether the process has a session at all.
        /// </summary>
        public static bool SetProcessVolume(int processId, float level, bool muted, ISet<string> applied)
        {
            var found = false;
            var context = Guid.Empty;
            foreach (var device in ActiveOutputs())
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var activated) != 0
                    || ((IAudioSessionManager2)activated).GetSessionEnumerator(out var sessions) != 0
                    || sessions.GetCount(out var count) != 0)
                    continue;

                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0 || session.GetProcessId(out var pid) != 0 || pid != processId)
                        continue;
                    found = true;
                    session.GetSessionInstanceIdentifier(out var instance);
                    if (instance is null || applied.Contains(instance))
                        continue;
                    var volume = (ISimpleAudioVolume)session;
                    if (volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context) == 0 && volume.SetMute(muted, ref context) == 0)
                        applied.Add(instance);
                }
            }
            return found;
        }

        /// <summary>
        /// Sends the sound of the program <paramref name="processId"/> runs to an output, or back to Windows'
        /// default one (null). False when Windows does not offer it.
        /// </summary>
        /// <remarks>
        /// Windows' "App volume and device preferences", through the interface its settings page uses, which
        /// is not documented (EarTrumpet uses it too). The choice is kept per program, not per process: sent
        /// to the headset, every QEMU already running and every one started later followed (measured). Hence
        /// one setting for all machines rather than one each.
        /// </remarks>
        public static bool RouteProgramOutput(int processId, string? outputId)
        {
            var factory = PolicyConfigFactory();
            if (factory == IntPtr.Zero)
                return false;
            try
            {
                var device = IntPtr.Zero;
                if (!string.IsNullOrEmpty(outputId))
                {
                    // Endpoint ids become device interface paths of the audio render class.
                    var path = $@"\\?\SWD#MMDEVAPI#{outputId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";
                    if (WindowsCreateString(path, path.Length, out device) != 0)
                        return false;
                }
                try
                {
                    // After IInspectable's six methods, nineteen this code does not use; then Set is the 26th slot.
                    var vtable = Marshal.ReadIntPtr(factory);
                    var set = Marshal.GetDelegateForFunctionPointer<SetPersistedEndpoint>(Marshal.ReadIntPtr(vtable, 25 * IntPtr.Size));
                    // Both roles: games and system sounds use the first, players the second.
                    return set(factory, (uint)processId, RenderFlow, 0, device) == 0
                           && set(factory, (uint)processId, RenderFlow, 1, device) == 0;
                }
                finally
                {
                    if (device != IntPtr.Zero)
                        WindowsDeleteString(device);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }

        private static IntPtr PolicyConfigFactory()
        {
            // Windows 10 21H2 and later, then Windows 10 from 1803.
            foreach (var iidText in new[] { "ab3d4648-e242-459f-b02f-541c70306324", "2a59116d-6c4f-45e0-a74f-707e3fef9258" })
            {
                const string className = "Windows.Media.Internal.AudioPolicyConfig";
                if (WindowsCreateString(className, className.Length, out var name) != 0)
                    return IntPtr.Zero;
                try
                {
                    var iid = new Guid(iidText);
                    if (RoGetActivationFactory(name, ref iid, out var factory) == 0)
                        return factory;
                }
                finally
                {
                    WindowsDeleteString(name);
                }
            }
            return IntPtr.Zero;
        }

        private static IEnumerable<IMMDevice> ActiveOutputs()
        {
            IMMDeviceEnumerator enumerator;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            }
            catch (COMException)
            {
                yield break;
            }
            if (enumerator.EnumAudioEndpoints(RenderFlow, ActiveState, out var devices) != 0 || devices.GetCount(out var count) != 0)
                yield break;
            for (var i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) == 0)
                    yield return device;
            }
        }

        private static string? FriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(0, out var store) != 0)
                return null;
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; // VT_LPWSTR
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPersistedEndpoint(IntPtr self, uint processId, int flow, int role, IntPtr device);

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Format;
            public int Id;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out int processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    }
}
