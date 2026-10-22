using MyPowerTools.Platform.Abstractions;
using Screenshot.MyPowerTools;

internal static class BackendRegression
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("kuaika: discovery is explicit and capture uses gui", () =>
        {
            var environment = new EnvironmentStub();
            var session = new ScreenshotSession(environment);
            Check(session.Inspect(null).BackendId == "snow-shot", "existing default changed");
            var state = session.Inspect(null, "kuaika");
            Check(state.Installed && state.BackendId == "kuaika", "kuaika discovery");
            Check(session.Capture(null, "kuaika").Success, "capture failed");
            Check(environment.LastArguments.SequenceEqual(new[] { "gui" }), "capture CLI arguments");
            Check(environment.Kills == 0, "capture killed a process");
            return Task.CompletedTask;
        }),
        ("kuaika: switch replaces only the module-owned backend", () =>
        {
            var environment = new EnvironmentStub();
            var session = new ScreenshotSession(environment);
            Check(session.EnsureResident(null).Success, "start snow shot");
            Check(!session.Inspect(null, "kuaika").OwnedByModule, "wrong backend ownership");
            Check(session.EnsureResident(null, "kuaika").Success, "switch failed");
            Check(environment.Kills == 1, "old owned process not stopped");
            Check(session.Inspect(null, "kuaika").OwnedByModule, "new ownership missing");
            session.StopOwned();
            Check(environment.Kills == 2, "new owned process not stopped");
            return Task.CompletedTask;
        }),
        ("kuaika: external resident survives switching and stop", () =>
        {
            var environment = new EnvironmentStub();
            environment.StartProcess(environment.Kuaika, []);
            var session = new ScreenshotSession(environment);
            Check(session.EnsureResident(null, "kuaika").Success, "existing resident");
            session.StopOwned();
            Check(environment.Kills == 0, "external resident killed");
            return Task.CompletedTask;
        }),
        ("kuaika: missing and mismatched override never starts Snow Shot", () =>
        {
            var environment = new EnvironmentStub();
            var session = new ScreenshotSession(environment);
            Check(!session.Open(environment.SnowShot, "kuaika").Success, "wrong executable accepted");
            environment.HasKuaika = false;
            Check(!session.Open(null, "kuaika").Success, "missing backend fell back");
            Check(environment.Starts == 0, "started unexpected backend");
            return Task.CompletedTask;
        }),
        ("kuaika: mac bundle resolves its executable and Linux accepts AppImage", () =>
        {
            var environment = new EnvironmentStub { Platform = ScreenshotPlatform.MacOs };
            Check(ScreenshotKuaikaBackend.Resolve(environment, "/Applications/快咔.app").Installed, "mac bundle");
            Check(ScreenshotKuaikaBackend.Accepts(ScreenshotPlatform.Linux, "/opt/kuaika-0.0.117-x86_64.AppImage"), "AppImage");
            Check(!ScreenshotKuaikaBackend.Accepts(ScreenshotPlatform.Windows, "other.exe"), "unrelated executable");
            return Task.CompletedTask;
        })
    ];

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class EnvironmentStub : IScreenshotEnvironment
    {
        private readonly Dictionary<int, string> _processes = new();
        private int _next = 1;
        public ScreenshotPlatform Platform { get; init; } = ScreenshotPlatform.Windows;
        public string SnowShot => Path.Combine(LocalAppData, "Programs", "Snow Shot", "snow_shot.exe");
        public string Kuaika => Platform == ScreenshotPlatform.MacOs ? Path.Combine("/Applications", "快咔.app", "Contents", "MacOS", "快咔") : Path.Combine(LocalAppData, "Programs", "kuaika", "kuaika.exe");
        public string LocalAppData => Path.Combine(Path.GetTempPath(), "mpt-kuaika-synthetic");
        public string? ProgramFiles => null;
        public string? ProgramFilesX86 => null;
        public string? UserProfile => null;
        public string? PathEnvironment => null;
        public bool HasKuaika { get; set; } = true;
        public int Kills { get; private set; }
        public int Starts { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public bool FileExists(string path) => path.Replace('\\', '/') == SnowShot.Replace('\\', '/') ||
            (HasKuaika && path.Replace('\\', '/') == Kuaika.Replace('\\', '/'));
        public string? ReadAllText(string path) => null;
        public IEnumerable<string> EnumerateDirectories(string directory) => [];
        public IReadOnlyList<string> WindowsInstallLocations() => [];
        public string? ReadLaunchHelp(string path) => null;
        public string? ReadConfiguredScreenshotHotkey(string path) => throw new InvalidOperationException("kuaika must use CLI");
        public bool TrySendShortcut(string gesture, out string error) => throw new InvalidOperationException("No input injection");
        public void UseKeyboardShortcuts(IKeyboardShortcutService? service) { }
        public void WaitAfterResidentStart() { }
        public bool IsProcessRunning(string name) => _processes.Values.Any(path => Path.GetFileNameWithoutExtension(path) == name);
        public bool IsProcessAlive(int pid) => _processes.ContainsKey(pid);
        public bool TryGetLiveProcessIdentity(int pid, out string name, out string? path)
        {
            var alive = _processes.TryGetValue(pid, out path);
            name = path is null ? "" : Path.GetFileNameWithoutExtension(path);
            return alive;
        }
        public int StartProcess(string path, IReadOnlyList<string> arguments)
        {
            Starts++; LastArguments = arguments; _processes[_next] = path; return _next++;
        }
        public void KillProcess(int pid) { Kills++; _processes.Remove(pid); }
    }
}
