using System.Windows.Input;
using Avalonia.Threading;

namespace Screenshot.Surface.ViewModels;

/// <summary>
/// Async command that always raises <see cref="CanExecuteChanged"/> on the Avalonia UI thread.
/// The shared <c>MptAsyncRelayCommand</c> from Shell's AvaloniaSdk may still notify off-thread
/// after an await, which Avalonia turns into a fatal TextBlock affinity crash.
/// </summary>
internal sealed class ScreenshotUiCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private int _isRunning;

    public ScreenshotUiCommand(Func<Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public void NotifyCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isRunning) == 0 && _canExecute();

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"Screenshot UI command failed: {exception}");
        }
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null) || Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        NotifyCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            NotifyCanExecuteChanged();
        }
    }
}
