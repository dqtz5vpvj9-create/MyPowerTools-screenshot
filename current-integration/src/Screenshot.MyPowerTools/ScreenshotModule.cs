using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;

namespace Screenshot.MyPowerTools;

public sealed class ScreenshotModule : IMptModule, IMptModuleLifecycle
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ScreenshotSession _session;
    private readonly ScreenshotSnowShotInstaller? _installer;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _startupGate = new();
    private JsonObject _settings = DefaultSettings();
    private CancellationTokenSource? _startupCancellation;
    private int _startupGeneration;
    private int _installLaunchGeneration;
    private long _eventSequence;

    public ScreenshotModule()
        : this(ScreenshotSession.CreateReal(), null, ScreenshotSnowShotInstaller.CreateReal())
    {
    }

    public ScreenshotModule(
        ScreenshotSession session,
        Func<TimeSpan, CancellationToken, Task>? delay,
        ScreenshotSnowShotInstaller? installer = null)
    {
        _session = session;
        _installer = installer;
        _delay = delay ?? ((duration, cancellationToken) => Task.Delay(duration, cancellationToken));
    }

    public string Id => "screenshot";
    public string PackageId => "screenshot";
    public Version Version => new(0, 1, 0);
    public Task StartupTask { get; private set; } = Task.CompletedTask;
    internal Task InstallationCompletionTask { get; private set; } = Task.CompletedTask;

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        // "立即截图" on macOS sends Snow Shot's hotkey through the platform's CGEvent service.
        context.TryGetCapability<IKeyboardShortcutService>("keyboard.shortcut", out var keyboardShortcuts);
        _session.UseKeyboardShortcuts(keyboardShortcuts);
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        // Runner startup loads an already-enabled module here and does not call
        // StartAsync. The delayed launch lets a following settings apply cancel it.
        ScheduleResidentStartup();
        return ValueTask.FromResult(new InitializeResult(
            true,
            context.ProtocolVersion,
            ["status", "commands", "settings", "logs", "lifecycle"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var inspection = Inspect();
        var checks = new List<HealthCheckSnapshot>
        {
            new HealthCheckSnapshot(
                "backend.installed",
                inspection.DisplayName,
                inspection.Installed,
                inspection.Installed ? inspection.ExecutablePath ?? "Installed." : inspection.InstallHint),
            new HealthCheckSnapshot(
                "backend.process",
                "Screenshot process",
                inspection.Running,
                inspection.Running ? "The screenshot process is running." : "The screenshot process is not running.")
        };
        if (inspection.InstallState is not "idle")
        {
            checks.Add(new HealthCheckSnapshot(
                "backend.install",
                "Snow Shot 安装",
                inspection.InstallState is "installing" or "installed",
                inspection.InstallMessage));
        }

        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            inspection.Installed ? "running" : "degraded",
            inspection.Summary,
            DateTimeOffset.UtcNow,
            checks,
            (ulong)Math.Max(0, Interlocked.Read(ref _eventSequence))));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("screenshot.status", "查看截图状态", "查看当前截图程序、路径，以及进程是否在运行"),
            Command("screenshot.capture", "立即截图", "Linux 打开 Flameshot 选区；Windows 和 macOS 通过 Snow Shot 已配置的截图快捷键开始框选"),
            Command("screenshot.open", "打开截图工具", "如果截图程序还没在运行，就由 MyPowerTools 拉起它"),
            Command("screenshot.install", "下载并安装 Snow Shot", "从 Snow Shot 官网下载官方版本，校验 SHA-256 后为当前用户安装")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = request.CommandId switch
            {
                "screenshot.status" => Succeeded(request, Serialize(Inspect())),
                "screenshot.install" => FromAction(request, Install()),
                "screenshot.capture" => FromAction(request, _session.Capture(ExecutableOverride())),
                "screenshot.open" => FromAction(request, _session.Open(ExecutableOverride())),
                _ => Failed(request, MptErrorCodes.NotFound, $"Unknown command '{request.CommandId}'.")
            };
            if (result.Success && request.CommandId is "screenshot.capture" or "screenshot.open" or "screenshot.install")
            {
                Publish(request.CommandId, result.Output);
            }

            return ValueTask.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Failed(request, MptErrorCodes.RuntimeUnavailable, exception.Message));
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var moduleEvent in _events.Reader.ReadAllAsync(cancellationToken))
        {
            if (moduleEvent.Seq > cursor.LastEventSeq)
            {
                yield return moduleEvent;
            }
        }
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "required": ["launchWithMyPowerTools"],
          "properties": {
            "executablePath": {
              "type": "string",
              "title": "截图程序路径",
              "description": "留空则自动查找。Windows 和 macOS 指向 snow_shot 或旧版 snowshot，Linux 指向 flameshot。4K 或多屏如果 Snow Shot 界面发虚，改用它的 fixedRuntime 构建后再填这里。",
              "default": ""
            },
            "launchWithMyPowerTools": {
              "type": "boolean",
              "title": "随 MyPowerTools 启动",
              "description": "启用后，模块启动时拉起截图程序。关闭模块只停止这次由 MyPowerTools 拉起的进程。",
              "default": true
            }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, (JsonObject)_settings.DeepClone(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var values = SettingsJson.Merge(_settings, patch.Patch);
        var messages = ValidateSettings(values);
        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var merged = SettingsJson.Merge(DefaultSettings(), snapshot.Values);
        var messages = ValidateSettings(merged);
        if (messages.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", messages));
        }

        _settings = merged;
        lock (_startupGate)
        {
            CancelPendingStartup();
            if (LaunchWithMyPowerTools())
            {
                var started = _session.EnsureResident(ExecutableOverride());
                Publish("screenshot.launch", started.Message);
            }
            else
            {
                _installLaunchGeneration++;
                _session.StopOwned();
                Publish("screenshot.stop", "已关闭随 MyPowerTools 启动，并停止本次拉起的截图进程。");
            }
        }

        return ValueTask.FromResult(snapshot with { Values = (JsonObject)_settings.DeepClone() });
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("screenshot.dashboard", "dashboard-card", "Screenshot", new JsonObject { ["state"] = "ready" }),
            new("screenshot.detail", "detail-page", "Screenshot", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask EnableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return StopAsync(context, cancellationToken);
    }

    public ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        ScheduleResidentStartup();
        return ValueTask.CompletedTask;
    }

    private void ScheduleResidentStartup()
    {
        CancellationTokenSource startup;
        int generation;
        lock (_startupGate)
        {
            CancelPendingStartup();
            generation = ++_startupGeneration;
            startup = new CancellationTokenSource();
            _startupCancellation = startup;
        }

        StartupTask = CompleteStartupAsync(generation, startup.Token);
    }

    public ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        lock (_startupGate)
        {
            CancelPendingStartup();
            _installLaunchGeneration++;
            _session.StopOwned();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        lock (_startupGate)
        {
            CancelPendingStartup();
            _installLaunchGeneration++;
            _session.StopOwned();
            _events.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }

    private async Task CompleteStartupAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        lock (_startupGate)
        {
            if (generation != _startupGeneration || cancellationToken.IsCancellationRequested || !LaunchWithMyPowerTools())
            {
                return;
            }

            var started = _session.EnsureResident(ExecutableOverride());
            Publish("screenshot.launch", started.Message);
        }
    }

    private void CancelPendingStartup()
    {
        lock (_startupGate)
        {
            _startupGeneration++;
            _startupCancellation?.Cancel();
            _startupCancellation = null;
        }
    }

    private ScreenshotInspection Inspect()
    {
        var inspection = _session.Inspect(ExecutableOverride());
        if (_installer is null)
        {
            return inspection;
        }

        var status = _installer.Status;
        return inspection with
        {
            InstallState = status.State,
            InstallMessage = status.Message,
            CanInstall = !inspection.Installed && _installer.IsSupported(out _)
        };
    }

    // Only runs when the user explicitly asks (the 下载并安装 button or command); nothing
    // downloads Snow Shot on its own.
    private ScreenshotActionResult Install()
    {
        if (_installer is null)
        {
            return new ScreenshotActionResult(false, "当前环境不支持自动安装。", Inspect());
        }

        if (!_installer.IsSupported(out var unsupported))
        {
            return new ScreenshotActionResult(false, unsupported, Inspect());
        }

        var current = _session.Inspect(ExecutableOverride());
        if (current.Installed)
        {
            return new ScreenshotActionResult(true, $"{current.DisplayName} 已安装：{current.ExecutablePath}", Inspect());
        }

        lock (_startupGate)
        {
            if (_installer.TryStartInstall(out var install))
            {
                // The download may finish after Stop/Disable/Dispose. Only the explicit
                // install request's still-current launch permission can start a resident.
                var generation = _installLaunchGeneration;
                InstallationCompletionTask = install.ContinueWith(
                    completed => OnInstallCompleted(completed.Result, generation),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
            }
        }

        var status = _installer.Status;
        return new ScreenshotActionResult(status.State is "installing" or "installed", status.Message, Inspect());
    }

    private void OnInstallCompleted(ScreenshotInstallStatus status, int generation)
    {
        Publish("screenshot.install", status.Message);
        if (status.State != "installed")
        {
            return;
        }

        lock (_startupGate)
        {
            if (generation != _installLaunchGeneration)
            {
                return;
            }

            var started = _session.EnsureResident(ExecutableOverride());
            Publish("screenshot.launch", started.Message);
        }
    }

    private CommandExecutionResult FromAction(CommandRequest request, ScreenshotActionResult action)
    {
        return action.Success
            ? Succeeded(request, Serialize(action))
            : Failed(request, MptErrorCodes.NotFound, action.Message);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private string? ExecutableOverride() => SettingsJson.ReadString(_settings, "executablePath");

    private bool LaunchWithMyPowerTools() => SettingsJson.ReadBool(_settings, "launchWithMyPowerTools") ?? true;

    private static List<string> ValidateSettings(JsonObject values)
    {
        var messages = new List<string>();
        var path = SettingsJson.ReadString(values, "executablePath");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var platform = SystemScreenshotEnvironment.DetectPlatform();
            if (!Path.IsPathRooted(path))
            {
                messages.Add("截图程序路径必须是绝对路径。");
            }
            else if (!File.Exists(path) &&
                     !(path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
                       (File.Exists(Path.Combine(path, "Contents", "MacOS", ScreenshotBackendResolver.SnowShotBinary)) ||
                        File.Exists(Path.Combine(path, "Contents", "MacOS", ScreenshotBackendResolver.SnowShotLegacyBinary)))))
            {
                messages.Add("设置中的截图程序路径不存在。");
            }
            else if (!ScreenshotBackendResolver.IsAcceptedExecutable(platform, path) &&
                     !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(platform == ScreenshotPlatform.Linux
                    ? "Linux 上的路径必须指向 flameshot。"
                    : "路径必须指向 snow_shot 或 snowshot。");
            }
        }

        if (SettingsJson.ReadBool(values, "launchWithMyPowerTools") is null &&
            values.ContainsKey("launchWithMyPowerTools"))
        {
            messages.Add("随 MyPowerTools 启动必须是布尔值。");
        }

        return messages;
    }

    private static JsonObject DefaultSettings() => new()
    {
        ["executablePath"] = "",
        ["launchWithMyPowerTools"] = true
    };

    private static MptCommandDescriptor Command(string id, string title, string subtitle)
    {
        return new MptCommandDescriptor(
            id,
            "screenshot",
            title,
            subtitle,
            "action",
            Category: "Capture",
            TimeoutMs: 10000,
            Constraints: [MptOperationConstraints.RunsExternalProcesses],
            SupportsCancellation: true);
    }

    private void Publish(string type, string message)
    {
        var sequence = (ulong)Interlocked.Increment(ref _eventSequence);
        _events.Writer.TryWrite(new MptModuleEvent(
            Id,
            sequence,
            type,
            DateTimeOffset.UtcNow,
            new JsonObject { ["message"] = message }));
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message)
    {
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            message,
            new MptRuntimeError(code, message));
    }
}
