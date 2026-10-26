using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU
{
    /// <summary>
    /// Keeps the Windows clipboard and every running guest's clipboard in step.
    /// </summary>
    /// <remarks>
    /// Text only. Copying on the host reaches every running VM; copying inside a VM reaches the host
    /// and, through it, every other VM. The text last exchanged is remembered so that writing a guest's
    /// text to the Windows clipboard — which raises ContentChanged like any other copy — is not sent
    /// straight back to the guests as if the user had copied it again.
    /// </remarks>
    public sealed class ClipboardBridge
    {
        private readonly VmManager _manager;
        private readonly DispatcherQueue _dispatcher;
        private string? _lastExchanged;

        public ClipboardBridge(VmManager manager, DispatcherQueue dispatcher)
        {
            _manager = manager;
            _dispatcher = dispatcher;

            _manager.SessionCreated += (_, session) => Follow(session);
            Clipboard.ContentChanged += (_, _) => _ = OnHostClipboardChangedAsync();
        }

        /// <summary>Diagnostics sink, shown in the VM log.</summary>
        public Action<string>? Log { get; set; }

        private void Follow(VmSession session)
        {
            session.GuestClipboardChanged += (_, text) => _dispatcher.TryEnqueue(() => OnGuestCopied(session, text));

            // A VM started after something was copied should still be able to paste it.
            session.ClipboardReady += (_, _) => _dispatcher.TryEnqueue(async () =>
            {
                if (await ReadHostTextAsync() is { } text)
                    await session.SetHostClipboardAsync(text);
            });
        }

        private void OnGuestCopied(VmSession source, string text)
        {
            if (text == _lastExchanged)
                return;

            _lastExchanged = text;
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch (Exception ex)
            {
                // The clipboard can be held open by another application for a moment.
                Log?.Invoke($"Clipboard: could not write the guest's text to Windows ({ex.Message}).");
            }

            foreach (var session in _manager.RunningSessions.Where(s => !ReferenceEquals(s, source)))
                _ = session.SetHostClipboardAsync(text);
        }

        private async Task OnHostClipboardChangedAsync()
        {
            var text = await ReadHostTextAsync();
            if (text is null || text == _lastExchanged)
                return;

            _lastExchanged = text;
            foreach (var session in _manager.RunningSessions)
                await session.SetHostClipboardAsync(text);
        }

        private async Task<string?> ReadHostTextAsync()
        {
            try
            {
                var content = Clipboard.GetContent();
                return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Clipboard: could not read the Windows clipboard ({ex.Message}).");
                return null;
            }
        }
    }
}
