using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using Grpc.Core;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.HostControl;

namespace Screenshot.Surface.ViewModels;

public sealed class ScreenshotViewModel : ToolSurfacePageViewModel
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ScreenshotUiCommand _captureCommand;
    private readonly ScreenshotUiCommand _openCommand;
    private readonly ScreenshotUiCommand _refreshCommand;
    private readonly ScreenshotUiCommand _saveCommand;
    private string _backend = "正在查找截图程序…";
    private string _executable = "";
    private string _running = "检查中";
    private string _hotkeyNote = "截图快捷键由外部截图程序自己注册。";
    private string _installHint = "";
    private string _message = "MyPowerTools 负责发现并拉起截图程序，框选和标注仍在该程序里完成。";
    private string _executablePath = "";
    private bool _launchWithMyPowerTools = true;
    private bool _isBusy;
    private bool _installed;
    private bool _showInstallHint;

    public ScreenshotViewModel(MptAvaloniaSurfaceContext context)
        : base("截图", "Windows 和 macOS 使用 Snow Shot，Linux 使用 Flameshot", ToolSurfaceState.Loading)
    {
        _context = context;
        _captureCommand = new ScreenshotUiCommand(CaptureAsync, () => CanCapture);
        _openCommand = new ScreenshotUiCommand(OpenAsync, () => CanCapture);
        _refreshCommand = new ScreenshotUiCommand(InitializeAsync, () => !IsBusy);
        _saveCommand = new ScreenshotUiCommand(SaveSettingsAsync, () => CanSave);
        CaptureCommand = _captureCommand;
        OpenCommand = _openCommand;
        RefreshCommand = _refreshCommand;
        SaveCommand = _saveCommand;
    }

    public string Backend { get => _backend; private set => SetProperty(ref _backend, value); }
    public string Executable { get => _executable; private set => SetProperty(ref _executable, value); }
    public string Running { get => _running; private set => SetProperty(ref _running, value); }
    public string HotkeyNote { get => _hotkeyNote; private set => SetProperty(ref _hotkeyNote, value); }
    public string InstallHint { get => _installHint; private set => SetProperty(ref _installHint, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool ShowInstallHint { get => _showInstallHint; private set => SetProperty(ref _showInstallHint, value); }
    public ICommand CaptureCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public bool CanCapture => !IsBusy && _installed;
    public bool CanSave => !IsBusy;

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public bool LaunchWithMyPowerTools
    {
        get => _launchWithMyPowerTools;
        set => SetProperty(ref _launchWithMyPowerTools, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            NotifyCommands();
        }
    }

    public async Task InitializeAsync()
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            var response = await ExecuteAsync("screenshot.status", TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "读取截图状态失败。");
            }

            using var document = JsonDocument.Parse(response.Output);
            var root = document.RootElement;
            var installed = root.GetProperty("installed").GetBoolean();
            var running = root.GetProperty("running").GetBoolean();
            var settings = await ReadSettingsAsync(_lifetime.Token).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                _installed = installed;
                Backend = root.GetProperty("displayName").GetString() ?? "截图程序";
                Executable = root.GetProperty("executablePath").GetString() ?? "";
                Running = running ? "进程正在运行" : "进程未运行";
                HotkeyNote = root.GetProperty("hotkeyNote").GetString() ?? "";
                InstallHint = root.GetProperty("installHint").GetString() ?? "";
                ShowInstallHint = !installed;
                Message = root.GetProperty("summary").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(Executable))
                {
                    Executable = installed ? "" : "尚未找到可执行文件";
                }

                ExecutablePath = settings.Path;
                LaunchWithMyPowerTools = settings.LaunchWithMyPowerTools;
                SetProductState(installed ? ToolSurfaceState.Ready : ToolSurfaceState.Empty);
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                _installed = false;
                ShowInstallHint = false;
                Message = exception.Message;
                SetProductState(ToolSurfaceState.Failed, exception.Message);
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private async Task CaptureAsync()
    {
        await RunActionAsync("screenshot.capture").ConfigureAwait(false);
    }

    private async Task OpenAsync()
    {
        await RunActionAsync("screenshot.open").ConfigureAwait(false);
    }

    private async Task SaveSettingsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            var path = "";
            var launch = true;
            await RunOnUiAsync(() =>
            {
                path = ExecutablePath.Trim();
                launch = LaunchWithMyPowerTools;
            }).ConfigureAwait(false);

            using var client = HostControlClient.ForDefaultEndpoint();
            var current = await client.GetSettingsAsync("screenshot", _lifetime.Token).ConfigureAwait(false);
            var patch = new JsonObject
            {
                ["executablePath"] = path,
                ["launchWithMyPowerTools"] = launch
            };
            var updated = await client.UpdateSettingsAsync(
                "screenshot",
                current.Revision,
                JsonStructMapper.ToStruct(patch),
                _lifetime.Token).ConfigureAwait(false);
            var saved = updated.ApplyState switch
            {
                "applied" => "设置已保存。截图程序会按新的路径和启动选项运行。",
                "stored" => "设置已保存。",
                _ => string.IsNullOrWhiteSpace(updated.ApplyMessage) ? "设置没有保存。" : updated.ApplyMessage
            };
            await RunOnUiAsync(() => Message = saved).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            await RunOnUiAsync(() => Message = exception.Status.Detail).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => Message = exception.Message).ConfigureAwait(false);
            return;
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }

        await InitializeAsync().ConfigureAwait(false);
    }

    private async Task RunActionAsync(string commandId)
    {
        if (IsBusy || !_installed)
        {
            return;
        }

        await SetBusyAsync(true).ConfigureAwait(false);
        try
        {
            var response = await ExecuteAsync(commandId, TimeSpan.FromSeconds(10), _lifetime.Token).ConfigureAwait(false);
            var text = response.Success
                ? ReadActionMessage(response.Output)
                : response.Error?.Message ?? response.Output;
            // Do not re-enter InitializeAsync here: it races CanExecuteChanged / bindings and
            // overwrites the action result. Refresh stays a separate user action.
            await RunOnUiAsync(() => Message = text).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => Message = exception.Message).ConfigureAwait(false);
        }
        finally
        {
            await SetBusyAsync(false).ConfigureAwait(false);
        }
    }

    private static async Task<(string Path, bool LaunchWithMyPowerTools)> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var current = await client.GetSettingsAsync("screenshot", cancellationToken).ConfigureAwait(false);
        var values = JsonStructMapper.ToJsonObject(current.Values);
        return (
            SettingsJson.ReadString(values, "executablePath") ?? "",
            SettingsJson.ReadBool(values, "launchWithMyPowerTools") ?? true);
    }

    private async Task<CommandExecutionResult> ExecuteAsync(string commandId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await _context.ExecuteCommandAsync(commandId, new JsonObject(), deadline.Token).ConfigureAwait(false);
    }

    private static string ReadActionMessage(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? output;
            }
        }
        catch (JsonException)
        {
        }

        return output;
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanSave));
        _captureCommand.NotifyCanExecuteChanged();
        _openCommand.NotifyCanExecuteChanged();
        _refreshCommand.NotifyCanExecuteChanged();
        _saveCommand.NotifyCanExecuteChanged();
    }

    private async Task SetBusyAsync(bool value)
    {
        await RunOnUiAsync(() => IsBusy = value).ConfigureAwait(false);
    }

    private static Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        // Post (not InvokeAsync) so we never sync-block a hook/gRPC thread waiting on the UI pump.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }
}
