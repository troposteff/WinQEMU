using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using WinQEMU.Core.Cli;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;

namespace WinQEMU.Cli
{
    /// <summary>
    /// winqemu, the command line of WinQEMU, like VMware's vmrun: "winqemu start Debian". It hands each command to
    /// the running WinQEMU, which it starts in the notification area when needed, and prints the answer.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            UiLanguage.Apply(AppSettings.Load().Language);

            if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
            {
                Console.WriteLine(CliText.Usage);
                return args.Length == 0 ? CliProtocol.Misuse : CliProtocol.Success;
            }

            var arguments = args.Skip(1).Where(a => a != "--password-stdin").ToArray();
            var password = args.Contains("--password-stdin") ? Console.In.ReadLine() ?? string.Empty : null;

            using var stop = new CancellationTokenSource();
            // Ctrl+C leaves the machine as it is: only this command gives up waiting.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                var response = await SendAsync(new CliRequest(args[0], arguments, password), stop.Token);
                if (response.ExitCode == CliProtocol.PasswordRequired && password is null && !Console.IsInputRedirected)
                {
                    Console.Error.Write(response.Error + " ");
                    password = ReadPasswordFromConsole();
                    response = await SendAsync(new CliRequest(args[0], arguments, password), stop.Token);
                }
                if (response.Output.Length > 0)
                    Console.WriteLine(response.Output);
                if (response.ExitCode == CliProtocol.PasswordRequired)
                    Console.Error.WriteLine(CliText.PasswordNeeded);
                else if (!string.IsNullOrEmpty(response.Error))
                    Console.Error.WriteLine(response.Error);
                return response.ExitCode == CliProtocol.PasswordRequired ? CliProtocol.Failure : response.ExitCode;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(CliText.Cancelled);
                return CliProtocol.Failure;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or COMException or InvalidOperationException
                                           or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                return CliProtocol.Failure;
            }
        }

        private static async Task<CliResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            await using var pipe = await ConnectAsync(cancellationToken);
            await CliProtocol.WriteAsync(pipe, request, cancellationToken);
            return await CliProtocol.ReadAsync<CliResponse>(pipe, cancellationToken)
                   ?? throw new IOException(CliText.ConnectionLost);
        }

        /// <summary>To the running WinQEMU, started first in the notification area if it is not running.</summary>
        private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(".", CliProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, cancellationToken);
                return pipe;
            }
            catch (TimeoutException)
            {
            }

            StartWinQemuInBackground();
            var waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    // It reads its library before it answers: leave the time a slow disk needs.
                    await pipe.ConnectAsync(1000, cancellationToken);
                    return pipe;
                }
                catch (TimeoutException) when (waited.Elapsed < TimeSpan.FromSeconds(60))
                {
                }
                catch (TimeoutException)
                {
                    await pipe.DisposeAsync();
                    throw new TimeoutException(CliText.NoAnswer);
                }
            }
        }

        /// <summary>
        /// Through Windows' activation manager, the way to hand a packaged application an argument: WinQEMU then
        /// stays in the notification area ("--background", see App.OnLaunched) rather than opening its window in
        /// front of the terminal.
        /// </summary>
        private static void StartWinQemuInBackground()
        {
            var length = 0u;
            GetCurrentPackageFamilyName(ref length, null);
            if (length == 0)
                throw new InvalidOperationException(CliText.NotPackaged);
            var name = new StringBuilder((int)length);
            if (GetCurrentPackageFamilyName(ref length, name) != 0)
                throw new InvalidOperationException(CliText.NotPackaged);
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(name + "!App", "--background", 0, out _));
        }

        /// <summary>Typed without being shown, as a password prompt does.</summary>
        private static string ReadPasswordFromConsole()
        {
            var password = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                        password.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                    password.Append(key.KeyChar);
            }
            Console.Error.WriteLine();
            return password.ToString();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? name);

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManager
        {
        }

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig]
            int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        }
    }
}
