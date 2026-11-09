using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinQEMU.Controls;
using WinQEMU.Core.Sharing;

namespace WinQEMU
{
    /// <summary>
    /// Files dragged out of a guest's console (Windows, or Linux with a desktop) and dropped on this PC
    /// (Sharing.GuestDragOut for the guest half, Controls.GuestDragSource for the drag on this PC).
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _guestDragBusy;

        private void InitializeGuestDragOut() => ConsoleHost.GuestDragLeft += button => _ = DragOutOfGuestAsync(button);

        /// <summary>
        /// A button pressed in the console is held outside it. If the guest is dragging files, they are copied into a
        /// folder of WinQEMU's own as of now, and dragged on this PC from there; the drop waits for the copy.
        /// </summary>
        private async Task DragOutOfGuestAsync(string button)
        {
            if (_guestDragBusy || !_viewModel.CanDragFromGuest)
                return;
            _guestDragBusy = true;
            try
            {
                if (await _viewModel.WaitForGuestDragAsync(ConsoleHost.EdgeOfPointer(), ConsoleHost.PinGuestPointerToEdgeAsync) is not { } files)
                {
                    _viewModel.AppendDiagnostic("Files: a press in the console left it, but the guest dragged no files.");
                    return;
                }
                // Released meanwhile: the release reached the guest, whose drag ended on the window of its tools.
                if (GetAsyncKeyState(0x01) >= 0 && GetAsyncKeyState(0x02) >= 0)
                {
                    _viewModel.AppendDiagnostic("Files: the button was released before the guest's drag could be taken over.");
                    return;
                }

                var folder = NewDragFolder();
                var paths = files.Select(file => Path.Combine(folder, NameOf(file))).ToList();
                Task? inner = null;
                var copy = _viewModel.PullFilesAsync(files, folder, forDrag: true, copying: task => inner = task);
                _viewModel.AppendDiagnostic($"Files: {files.Count} item(s) dragged out of the guest, copied to {folder}.");

                bool dropped;
                try
                {
                    dropped = GuestDragSource.Run(WinRT.Interop.WindowNative.GetWindowHandle(this), paths,
                        inner ?? copy, _viewModel.AppendDiagnostic, () => ConsoleHost.ReleaseGuestButton(button));
                }
                finally
                {
                    // Again for a drag that ended without a release (Escape); a second release does no harm.
                    ConsoleHost.ReleaseGuestButton(button);
                }

                if (!dropped)
                    _viewModel.CancelGuestDragCopy();
                string? failure = null;
                DropResult? result = null;
                try
                {
                    result = await copy;
                }
                catch (Exception ex) when (ex is GuestFileDropException or InvalidOperationException or IOException
                                               or UnauthorizedAccessException or Core.Qemu.GuestAgentException)
                {
                    failure = ex.Message;
                }
                if (!dropped || result is null)
                    DeleteQuietly(folder);
                _viewModel.ReportGuestDrag(dropped && result is not null, result?.Names ?? Array.Empty<string>(), failure);
            }
            finally
            {
                _guestDragBusy = false;
                // A Linux guest's window along the edge: its drag has ended on it by now (the guest's button was released).
                _ = _viewModel.HideGuestDragEdgeAsync();
            }
        }

        /// <summary>
        /// A new folder for one drag, in the package's temporary folder: a real path that Explorer can read, where a
        /// path under AppData would be the package's virtualised view. Folders of earlier drags are removed after an
        /// hour: the drop target may still be copying from them when the drag ends.
        /// </summary>
        private static string NewDragFolder()
        {
            var root = Path.Combine(Windows.Storage.ApplicationData.Current.TemporaryFolder.Path, "DragOut");
            if (Directory.Exists(root))
            {
                foreach (var old in Directory.EnumerateDirectories(root))
                {
                    if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                        DeleteQuietly(old);
                }
            }
            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The name an item of the guest gets in the drag folder: its own, made a valid Windows name (a Linux name may not be).</summary>
        private static string NameOf(string guestPath)
        {
            var trimmed = guestPath.TrimEnd('\\', '/');
            var slash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return GuestFilePull.WindowsName(slash >= 0 ? trimmed[(slash + 1)..] : trimmed);
        }

        private static void DeleteQuietly(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed with the next drag's clean-up.
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
    }
}
