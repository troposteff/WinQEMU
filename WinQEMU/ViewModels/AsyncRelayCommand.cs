using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Command for awaitable work. Blocks re-entry while running, which matters here because VM
    /// transitions take seconds and a double-click would otherwise start QEMU twice.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private readonly Action<Exception>? _onError;
        private bool _isRunning;

        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Func<object?, bool>? canExecute = null,
            Action<Exception>? onError = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError;
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                    return;
                _isRunning = value;
                RaiseCanExecuteChanged();
            }
        }

        public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;

            IsRunning = true;
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // An unhandled exception in an async void handler would tear down the process,
                // so failures are surfaced to the view model instead.
                _onError?.Invoke(ex);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
