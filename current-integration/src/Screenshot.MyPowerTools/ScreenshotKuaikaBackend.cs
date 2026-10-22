namespace Screenshot.MyPowerTools;

internal static class ScreenshotKuaikaBackend
{
    public const string Website = "https://www.kuaika.app/";
    private static readonly string[] WindowsNames = ["kuaika.exe", "快咔.exe"];
    private static readonly string[] UnixNames = ["kuaika", "快咔"];

    public static bool Accepts(ScreenshotPlatform platform, string path) =>
        (platform == ScreenshotPlatform.Windows ? WindowsNames : UnixNames)
            .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
        (platform == ScreenshotPlatform.Linux && Path.GetFileName(path).StartsWith("kuaika-", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

    public static ScreenshotBackend Resolve(IScreenshotEnvironment environment, string? executableOverride)
    {
        var names = environment.Platform == ScreenshotPlatform.Windows ? WindowsNames : UnixNames;
        string? path = null;
        string? error = null;
        if (!string.IsNullOrWhiteSpace(executableOverride))
        {
            path = executableOverride.Trim().Trim('"');
            if (environment.Platform == ScreenshotPlatform.MacOs && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                path = Find(environment, [Path.Combine(path, "Contents", "MacOS")], names);
            if (path is null || !Path.IsPathRooted(path) || !environment.FileExists(path) || !Accepts(environment.Platform, path))
            {
                path = null;
                error = "请填写已安装的快咔可执行文件的绝对路径（macOS 可选择 .app）。";
            }
        }
        else
        {
            var directories = new List<string>();
            if (environment.Platform == ScreenshotPlatform.Windows)
            {
                directories.AddRange(environment.WindowsInstallLocations());
                foreach (var root in new[] { environment.ProgramFiles, environment.ProgramFilesX86,
                    Path.Combine(environment.LocalAppData ?? "", "Programs"), environment.LocalAppData })
                    if (!string.IsNullOrWhiteSpace(root))
                        foreach (var folder in new[] { "kuaika", "快咔" }) directories.Add(Path.Combine(root, folder));
            }
            else if (environment.Platform == ScreenshotPlatform.MacOs)
            {
                foreach (var root in new[] { "/Applications", Path.Combine(environment.UserProfile ?? "", "Applications") })
                    foreach (var bundle in new[] { "快咔.app", "kuaika.app", "Kuaika.app" })
                        directories.Add(Path.Combine(root, bundle, "Contents", "MacOS"));
            }
            else
                directories.AddRange((environment.PathEnvironment ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            path = Find(environment, directories, names);
        }
        return new ScreenshotBackend(path is not null, "kuaika", "快咔", path,
            path is null ? "kuaika" : Path.GetFileNameWithoutExtension(path), ["gui"],
            error ?? $"请从 {Website} 下载并安装快咔，然后刷新；便携版可填写程序路径。",
            "通过快咔的 gui 命令打开截图选区；全局快捷键由快咔管理。");
    }

    private static string? Find(IScreenshotEnvironment environment, IEnumerable<string> directories, IEnumerable<string> names) =>
        directories.SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(environment.FileExists);
}
