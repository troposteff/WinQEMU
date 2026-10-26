using System.Text;

namespace WinQEMU.Core.Interop
{
    /// <summary>
    /// Adjustments the WinUI shell window needs before a foreign HWND can be embedded in it.
    /// </summary>
    public static class WinUiHostWindow
    {
        /// <summary>Window class WinUI 3 uses to host the XAML content inside the top-level window.</summary>
        private const string ContentBridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

        /// <summary>
        /// Adds <c>WS_CLIPSIBLINGS</c> to WinUI's content bridge.
        /// </summary>
        /// <remarks>
        /// WinUI creates the bridge without that style. A window lacking WS_CLIPSIBLINGS paints over
        /// the area covered by its siblings no matter what the Z-order says, so an embedded console
        /// ends up correctly parented, sized, topmost and actively rendering — yet completely
        /// invisible, because the XAML surface is painted straight over it. Setting the style makes
        /// the bridge respect sibling geometry and leave the console's rectangle alone.
        /// </remarks>
        /// <returns>True when a content bridge was found and now clips its siblings.</returns>
        public static bool EnableSiblingClipping(IntPtr shellWindow, Action<string>? trace = null)
        {
            var bridges = new List<IntPtr>();

            NativeMethods.EnumChildWindows(shellWindow, (handle, _) =>
            {
                if (ReadClassName(handle).StartsWith(ContentBridgeClass, StringComparison.Ordinal))
                    bridges.Add(handle);
                return true;
            }, IntPtr.Zero);

            if (bridges.Count == 0)
            {
                trace?.Invoke("Console: no WinUI content bridge found; cannot enable sibling clipping.");
                return false;
            }

            foreach (var bridge in bridges)
            {
                var style = NativeMethods.GetWindowStyle(bridge, NativeMethods.GwlStyle);
                if ((style & NativeMethods.WsClipSiblings) != 0)
                    continue;

                NativeMethods.SetWindowStyle(bridge, NativeMethods.GwlStyle, style | NativeMethods.WsClipSiblings);
                NativeMethods.SetWindowPos(bridge, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder
                    | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
            }

            return true;
        }

        private static string ReadClassName(IntPtr handle)
        {
            var buffer = new StringBuilder(256);
            var length = NativeMethods.GetClassName(handle, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }
    }
}
