using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using Screenshot.MyPowerTools;

var cases = new (string Name, Func<Task> Run)[]
{
    ("active install starts resident", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.BeginAsync();
        await fixture.CompleteAsync();
        Equal(1, fixture.Environment.Starts);
    }),
    ("stop during install suppresses completion launch", () => SuppressLaunchAsync("stop")),
    ("disable during install suppresses completion launch", () => SuppressLaunchAsync("disable")),
    ("dispose during install suppresses completion launch", () => SuppressLaunchAsync("dispose")),
    ("changing launch setting to false suppresses completion launch", () => SuppressLaunchAsync("setting")),
    ("changing backend during Snow Shot install suppresses its launch", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.BeginAsync();
        await fixture.Module.ApplySettingsAsync(new SettingsSnapshotDocument("screenshot", 1,
            new JsonObject { ["backend"] = "kuaika" }, DateTimeOffset.UtcNow), default);
        await fixture.CompleteAsync();
        Equal(0, fixture.Environment.Starts);
        var install = await fixture.Module.ExecuteCommandAsync(new CommandRequest("install-kuaika", "screenshot.install"), default);
        Equal(false, install.Success);
    }),
    ("fresh explicit install after stop can still launch", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.Module.StopAsync(null!, default);
        await fixture.BeginAsync();
        await fixture.CompleteAsync();
        Equal(1, fixture.Environment.Starts);
    }),
    ("reenabling does not revive an older install callback", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.BeginAsync();
        await fixture.Module.StopAsync(null!, default);
        await fixture.Module.StartAsync(null!, default);
        await fixture.Module.StartupTask;
        await fixture.CompleteAsync();
        Equal(0, fixture.Environment.Starts);
    }),
    ("explicit install still launches when startup setting was already false", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.DisableStartupSettingAsync();
        await fixture.BeginAsync();
        await fixture.CompleteAsync();
        Equal(1, fixture.Environment.Starts);
    }),
    ("stop after completion stops the owned resident", async () =>
    {
        await using var fixture = new InstallFixture();
        await fixture.BeginAsync();
        await fixture.CompleteAsync();
        await fixture.Module.StopAsync(null!, default);
        Equal(1, fixture.Environment.Starts);
        Equal(1, fixture.Environment.Kills);
        Equal(false, fixture.Environment.Running);
    }),
    ("stop preserves a preexisting user process", async () =>
    {
        await using var fixture = new InstallFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Environment.Executable)!);
        File.WriteAllText(fixture.Environment.Executable, "inert test fixture");
        fixture.Environment.Running = true;
        await fixture.Module.ExecuteCommandAsync(new CommandRequest("open", "screenshot.open"), default);
        await fixture.Module.StopAsync(null!, default);
        Equal(0, fixture.Environment.Starts);
        Equal(0, fixture.Environment.Kills);
        Equal(true, fixture.Environment.Running);
    })
}.Concat(InstallerRegression.Cases).Concat(BackendRegression.Cases).ToArray();
if (args is ["--list"])
{
    Console.WriteLine("MPT_TEST_CASES_V1");
    foreach (var test in cases) Console.WriteLine(test.Name);
    return 0;
}
var failed = 0;
var skipped = 0;
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (RegressionSkippedException exception) { skipped++; Console.WriteLine($"SKIP {test.Name}: {exception.Message}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{cases.Length - failed - skipped}/{cases.Length} passed" + (skipped > 0 ? $", {skipped} skipped" : ""));
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static async Task SuppressLaunchAsync(string action)
{
    await using var fixture = new InstallFixture();
    await fixture.BeginAsync();
    switch (action)
    {
        case "stop": await fixture.Module.StopAsync(null!, default); break;
        case "disable": await fixture.Module.DisableAsync(null!, default); break;
        case "dispose": await fixture.Module.DisposeAsync(default); break;
        case "setting": await fixture.DisableStartupSettingAsync(); break;
    }
    await fixture.CompleteAsync();
    Equal(0, fixture.Environment.Starts);
    Equal(false, fixture.Environment.Running);
}

sealed class RegressionSkippedException(string reason) : Exception(reason);

sealed class InstallFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenshot-lifecycle-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDownloadHandler _handler = new();
    private readonly ScreenshotSnowShotInstaller _installer;
    public FakeEnvironment Environment { get; }
    public ScreenshotModule Module { get; }

    public InstallFixture()
    {
        Environment = new FakeEnvironment(_root);
        _installer = new ScreenshotSnowShotInstaller(ScreenshotPlatform.Windows, _root, _root, () => new HttpClient(_handler));
        Module = new ScreenshotModule(new ScreenshotSession(Environment), (_, _) => Task.CompletedTask, _installer);
    }

    public async Task BeginAsync()
    {
        var result = await Module.ExecuteCommandAsync(new CommandRequest("install", "screenshot.install"), default);
        if (!result.Success) throw new InvalidOperationException(result.Output);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task CompleteAsync()
    {
        _handler.Release();
        await Module.InstallationCompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        if (_installer.Status.State != "installed") throw new InvalidOperationException(_installer.Status.Message);
    }

    public ValueTask<SettingsSnapshotDocument> DisableStartupSettingAsync() => Module.ApplySettingsAsync(
        new SettingsSnapshotDocument("screenshot", 1, new JsonObject { ["launchWithMyPowerTools"] = false }, DateTimeOffset.UtcNow), default);

    public async ValueTask DisposeAsync()
    {
        _handler.Release();
        await Module.InstallationCompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Module.DisposeAsync(default);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

sealed class FakeDownloadHandler : HttpMessageHandler
{
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _zip;
    private readonly string _checksum;

    public FakeDownloadHandler()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("snow_shot.exe").Open()))
            writer.Write("inert synthetic executable fixture; never run");
        _zip = buffer.ToArray();
        _checksum = Convert.ToHexString(SHA256.HashData(_zip));
    }

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) };
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(_checksum + "  " + ScreenshotSnowShotInstaller.WindowsPortableArchive) };
    }
}

sealed class FakeEnvironment(string root) : IScreenshotEnvironment
{
    public int Starts { get; private set; }
    public int Kills { get; private set; }
    public bool Running { get; set; }
    public string Executable => Path.Combine(root, "Programs", "Snow Shot", "snow_shot.exe");
    public ScreenshotPlatform Platform => ScreenshotPlatform.Windows;
    public string? ProgramFiles => null;
    public string? ProgramFilesX86 => null;
    public string? LocalAppData => root;
    public string? UserProfile => root;
    public string? PathEnvironment => null;
    public bool FileExists(string path) => path == Executable && File.Exists(path);
    public string? ReadAllText(string path) => null;
    public IEnumerable<string> EnumerateDirectories(string directory) => [];
    public IReadOnlyList<string> WindowsInstallLocations() => [];
    public string? ReadLaunchHelp(string executablePath) => null;
    public string? ReadConfiguredScreenshotHotkey(string executablePath) => "Alt+A";
    public bool TrySendShortcut(string gesture, out string error) => throw new InvalidOperationException("Input injection is forbidden in this test.");
    public void UseKeyboardShortcuts(IKeyboardShortcutService? service) { }
    public void WaitAfterResidentStart() { }
    public bool IsProcessRunning(string processName) => Running;
    public bool IsProcessAlive(int processId) => Running;
    public bool TryGetLiveProcessIdentity(int processId, out string processName, out string? imagePath)
    {
        processName = "snow_shot"; imagePath = Executable; return Running;
    }
    public int StartProcess(string executablePath, IReadOnlyList<string> arguments) { Starts++; Running = true; return 12345; }
    public void KillProcess(int processId) { Kills++; Running = false; }
}
