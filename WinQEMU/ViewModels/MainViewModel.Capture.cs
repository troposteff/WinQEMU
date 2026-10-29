using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Console capture (VmSession.CaptureScreenAsync): the guest's screen as a PNG in Images\WinQEMU, as VMware's « Capturer
    /// l'écran ». The window copies it to the clipboard too.
    /// </summary>
    public partial class MainViewModel
    {
        private bool _capturing;

        /// <summary>Running or paused: a paused guest's screen is still there to capture.</summary>
        public bool CanCapture => SelectedSession is { State: VmState.Running or VmState.Paused } && !_capturing;

        /// <summary>Where captures go: a folder of their own under Images, created when first needed.</summary>
        public static string CaptureFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinQEMU");

        /// <summary>
        /// Captures the selection's screen to « <machine> 2026-10-29 14.05.33.png » in <see cref="CaptureFolder"/>.
        /// Returns the file, or throws with the reason.
        /// </summary>
        public async Task<string> CaptureSelectedAsync()
        {
            if (SelectedSession is not { State: VmState.Running or VmState.Paused } session || _capturing)
                throw new InvalidOperationException(ShellText.MachineMustRun);

            _capturing = true;
            RaisePropertyChanged(nameof(CanCapture));
            try
            {
                Directory.CreateDirectory(CaptureFolder);
                var path = UniqueCapturePath(session.Config.Name, DateTime.Now);
                await session.CaptureScreenAsync(path);
                AppendDiagnostic($"Screen captured to {path}.");
                return path;
            }
            catch (Exception ex) when (ex is QmpException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ShellText.CaptureFailed, ex.Message), ex);
            }
            finally
            {
                _capturing = false;
                RaisePropertyChanged(nameof(CanCapture));
            }
        }

        /// <summary>The machine's name with what Windows refuses in a file name replaced, then the time; numbered if taken.</summary>
        private static string UniqueCapturePath(string machine, DateTime when)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(machine.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            var stem = $"{(name.Length == 0 ? "WinQEMU" : name)} {when.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)}";
            var path = Path.Combine(CaptureFolder, stem + ".png");
            for (var index = 2; File.Exists(path); index++)
                path = Path.Combine(CaptureFolder, $"{stem} ({index.ToString(CultureInfo.InvariantCulture)}).png");
            return path;
        }
    }
}
