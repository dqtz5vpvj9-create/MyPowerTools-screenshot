namespace Screenshot.MyPowerTools;

public sealed record ScreenshotInspection(
    bool Installed,
    string BackendId,
    string DisplayName,
    string? ExecutablePath,
    bool Running,
    bool OwnedByModule,
    bool SupportsCaptureCommand,
    string HotkeyNote,
    string InstallHint,
    string Summary);

public sealed record ScreenshotActionResult(bool Success, string Message, ScreenshotInspection Inspection);

public sealed class ScreenshotSession
{
    private readonly IScreenshotEnvironment _environment;
    private readonly object _gate = new();
    private int? _ownedProcessId;
    private string? _ownedProcessName;
    private string? _ownedImagePath;

    public ScreenshotSession(IScreenshotEnvironment environment)
    {
        _environment = environment;
    }

    public static ScreenshotSession CreateReal() => new(new SystemScreenshotEnvironment());

    public ScreenshotInspection Inspect(string? executableOverride)
    {
        lock (_gate)
        {
            ForgetStaleOwnedProcess();
            return CreateInspection(ScreenshotBackendResolver.Resolve(_environment, executableOverride));
        }
    }

    public ScreenshotActionResult EnsureResident(string? executableOverride)
    {
        lock (_gate)
        {
            return EnsureResidentLocked(executableOverride);
        }
    }

    public ScreenshotActionResult Capture(string? executableOverride)
    {
        ScreenshotBackend backend;
        bool wasAlreadyRunning;
        lock (_gate)
        {
            backend = ScreenshotBackendResolver.Resolve(_environment, executableOverride);
            if (!backend.Installed || backend.ExecutablePath is null)
            {
                return new ScreenshotActionResult(false, backend.InstallHint, CreateInspection(backend));
            }

            if (backend.SupportsCaptureCommand)
            {
                try
                {
                    _environment.StartProcess(backend.ExecutablePath, backend.CaptureArguments);
                }
                catch (Exception exception)
                {
                    return new ScreenshotActionResult(false, exception.Message, CreateInspection(backend));
                }

                return new ScreenshotActionResult(true, $"已打开 {backend.DisplayName} 的截图选区。", CreateInspection(backend));
            }

            wasAlreadyRunning = _environment.IsProcessRunning(backend.ProcessName);
            var resident = EnsureResidentLocked(executableOverride);
            if (!resident.Success)
            {
                return resident;
            }
        }

        return TriggerSnowShotCapture(backend, wasAlreadyRunning);
    }

    private ScreenshotActionResult TriggerSnowShotCapture(ScreenshotBackend backend, bool wasAlreadyRunning)
    {
        if (backend.ExecutablePath is null)
        {
            lock (_gate)
            {
                return new ScreenshotActionResult(false, backend.InstallHint, CreateInspection(backend));
            }
        }

        var hotkey = _environment.ReadConfiguredScreenshotHotkey(backend.ExecutablePath);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            lock (_gate)
            {
                return new ScreenshotActionResult(
                    false,
                    $"{backend.DisplayName} 已就绪，但没有配置截图快捷键，无法从 MyPowerTools 触发截图。请在 Snow Shot 设置里为「截图」指定快捷键，或点托盘图标截图。",
                    CreateInspection(backend));
            }
        }

        if (!wasAlreadyRunning)
        {
            _environment.WaitAfterResidentStart();
        }

        // SendInput must not run on a thread that owns a WH_KEYBOARD_LL / UI pump in this
        // process (Input Monitor shares the Runner). Always inject from a worker thread.
        Task<(bool Sent, string Error)> send;
        try
        {
            send = Task.Run(() =>
            {
                var ok = _environment.TrySendShortcut(hotkey, out var sendError);
                return (ok, sendError ?? "");
            });
            if (!send.Wait(TimeSpan.FromSeconds(3)))
            {
                lock (_gate)
                {
                    return new ScreenshotActionResult(
                        false,
                        $"发送 Snow Shot 截图快捷键 {hotkey} 超时。",
                        CreateInspection(backend));
                }
            }
        }
        catch (AggregateException aggregate) when (aggregate.InnerException is not null)
        {
            lock (_gate)
            {
                return new ScreenshotActionResult(
                    false,
                    $"无法发送 Snow Shot 截图快捷键 {hotkey}：{aggregate.InnerException.Message}",
                    CreateInspection(backend));
            }
        }

        var (sent, error) = send.Result;
        if (!sent)
        {
            lock (_gate)
            {
                return new ScreenshotActionResult(
                    false,
                    string.IsNullOrWhiteSpace(error)
                        ? $"无法发送 Snow Shot 截图快捷键 {hotkey}。"
                        : $"无法发送 Snow Shot 截图快捷键 {hotkey}：{error}",
                    CreateInspection(backend));
            }
        }

        lock (_gate)
        {
            return new ScreenshotActionResult(
                true,
                $"已通过 Snow Shot 快捷键 {hotkey} 开始截图。",
                CreateInspection(backend));
        }
    }

    public ScreenshotActionResult Open(string? executableOverride)
    {
        return EnsureResident(executableOverride);
    }

    public void StopOwned()
    {
        lock (_gate)
        {
            if (_ownedProcessId is int processId && OwnsMatchingLiveProcess(processId))
            {
                _environment.KillProcess(processId);
            }

            ClearOwnership();
        }
    }

    private ScreenshotActionResult EnsureResidentLocked(string? executableOverride)
    {
        ForgetStaleOwnedProcess();
        var backend = ScreenshotBackendResolver.Resolve(_environment, executableOverride);
        if (!backend.Installed || backend.ExecutablePath is null)
        {
            return new ScreenshotActionResult(false, backend.InstallHint, CreateInspection(backend));
        }

        if (_ownedProcessId is int owned && OwnsMatchingLiveProcess(owned))
        {
            return new ScreenshotActionResult(true, $"{backend.DisplayName} 已由 MyPowerTools 拉起。", CreateInspection(backend));
        }

        if (_environment.IsProcessRunning(backend.ProcessName))
        {
            ClearOwnership();
            return new ScreenshotActionResult(true, $"{backend.DisplayName} 已在运行，MyPowerTools 不会关掉这个进程。", CreateInspection(backend));
        }

        try
        {
            var processId = _environment.StartProcess(backend.ExecutablePath, []);
            _ownedProcessId = processId;
            _ownedProcessName = backend.ProcessName;
            _ownedImagePath = backend.ExecutablePath;
        }
        catch (Exception exception)
        {
            ClearOwnership();
            return new ScreenshotActionResult(false, exception.Message, CreateInspection(backend));
        }

        return new ScreenshotActionResult(true, $"已启动 {backend.DisplayName}。", CreateInspection(backend));
    }

    private void ForgetStaleOwnedProcess()
    {
        if (_ownedProcessId is int processId && !OwnsMatchingLiveProcess(processId))
        {
            ClearOwnership();
        }
    }

    private bool OwnsMatchingLiveProcess(int processId)
    {
        if (string.IsNullOrWhiteSpace(_ownedProcessName))
        {
            return false;
        }

        if (!_environment.TryGetLiveProcessIdentity(processId, out var processName, out var imagePath))
        {
            return false;
        }

        if (!string.Equals(processName, _ownedProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_ownedImagePath) &&
            !string.IsNullOrWhiteSpace(imagePath) &&
            !ImagePathsEqual(_ownedImagePath, imagePath))
        {
            return false;
        }

        return true;
    }

    private void ClearOwnership()
    {
        _ownedProcessId = null;
        _ownedProcessName = null;
        _ownedImagePath = null;
    }

    private static bool ImagePathsEqual(string expected, string actual)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(expected),
                Path.GetFullPath(actual),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
    }

    private ScreenshotInspection CreateInspection(ScreenshotBackend backend)
    {
        var owned = _ownedProcessId is int processId && OwnsMatchingLiveProcess(processId);
        var running = owned || (backend.Installed && _environment.IsProcessRunning(backend.ProcessName));
        var summary = !backend.Installed
            ? backend.InstallHint
            : running
                ? $"{backend.DisplayName} 正在运行。{backend.HotkeyNote}"
                : $"已找到 {backend.DisplayName}，进程尚未运行。";
        return new ScreenshotInspection(
            backend.Installed,
            backend.BackendId,
            backend.DisplayName,
            backend.ExecutablePath,
            running,
            owned,
            backend.SupportsCaptureCommand,
            backend.HotkeyNote,
            backend.InstallHint,
            summary);
    }
}
