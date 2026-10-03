namespace Screenshot.MyPowerTools;

public sealed record ScreenshotBackend(
    bool Installed,
    string BackendId,
    string DisplayName,
    string? ExecutablePath,
    string ProcessName,
    IReadOnlyList<string> CaptureArguments,
    string InstallHint,
    string HotkeyNote)
{
    public bool SupportsCaptureCommand => CaptureArguments.Count > 0;
}

public static class ScreenshotBackendResolver
{
    public const string SnowShotExecutable = "snow_shot.exe";
    public const string SnowShotLegacyExecutable = "snowshot.exe";
    public const string SnowShotBinary = "snow_shot";
    public const string SnowShotLegacyBinary = "snowshot";
    public const string FlameshotExecutable = "flameshot";

    private static readonly string[] WindowsExecutableNames = [SnowShotExecutable, SnowShotLegacyExecutable];
    private static readonly string[] MacExecutableNames = [SnowShotBinary, SnowShotLegacyBinary];

    public static ScreenshotBackend Resolve(IScreenshotEnvironment environment, string? executableOverride)
    {
        var platform = environment.Platform;
        var normalized = NormalizeOverride(environment, platform, executableOverride);
        if (normalized.Error is not null)
        {
            return Missing(platform, normalized.Error);
        }

        if (normalized.Path is not null)
        {
            return Found(environment, platform, normalized.Path);
        }

        var discovered = platform switch
        {
            ScreenshotPlatform.Windows => FindWindows(environment),
            ScreenshotPlatform.MacOs => FindMac(environment),
            _ => FindLinux(environment)
        };
        return discovered is null ? Missing(platform, InstallHintFor(platform)) : Found(environment, platform, discovered);
    }

    public static bool IsAcceptedExecutable(ScreenshotPlatform platform, string path)
    {
        var name = Path.GetFileName(path);
        if (platform == ScreenshotPlatform.Linux)
        {
            return name.Equals(FlameshotExecutable, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(FlameshotExecutable + ".exe", StringComparison.OrdinalIgnoreCase);
        }

        return name.Equals(SnowShotBinary, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(SnowShotLegacyBinary, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(SnowShotExecutable, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(SnowShotLegacyExecutable, StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Path, string? Error) NormalizeOverride(
        IScreenshotEnvironment environment,
        ScreenshotPlatform platform,
        string? executableOverride)
    {
        if (string.IsNullOrWhiteSpace(executableOverride))
        {
            return (null, null);
        }

        var path = executableOverride.Trim().Trim('"');
        if (!Path.IsPathRooted(path))
        {
            return (null, "截图程序路径必须是绝对路径。");
        }

        if (platform == ScreenshotPlatform.MacOs &&
            path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(path, "Contents", "MacOS", SnowShotBinary);
            if (!environment.FileExists(path))
            {
                path = Path.Combine(Path.GetDirectoryName(path) ?? "", SnowShotLegacyBinary);
            }
        }

        if (!environment.FileExists(path))
        {
            return (null, "设置中的截图程序路径不存在。");
        }

        if (!IsAcceptedExecutable(platform, path))
        {
            return (null, platform == ScreenshotPlatform.Linux
                ? "Linux 上的路径必须指向 flameshot。"
                : "路径必须指向 snow_shot 或 snowshot。");
        }

        return (path, null);
    }

    private static string? FindWindows(IScreenshotEnvironment environment)
    {
        foreach (var location in environment.WindowsInstallLocations())
        {
            var installed = FirstExisting(environment, location, WindowsExecutableNames);
            if (installed is not null)
            {
                return installed;
            }
        }

        foreach (var root in new[] { environment.ProgramFiles, environment.ProgramFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var installed = FirstExisting(environment, Path.Combine(root, "Snow Shot"), WindowsExecutableNames);
            if (installed is not null)
            {
                return installed;
            }
        }

        if (!string.IsNullOrWhiteSpace(environment.LocalAppData))
        {
            var programs = Path.Combine(environment.LocalAppData, "Programs");
            var userInstall = FirstExisting(environment, Path.Combine(programs, "Snow Shot"), WindowsExecutableNames);
            if (userInstall is not null)
            {
                return userInstall;
            }

            foreach (var directory in environment.EnumerateDirectories(programs))
            {
                if (!Path.GetFileName(directory).Contains("snow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = FirstExisting(environment, directory, WindowsExecutableNames);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            var wingetPackages = Path.Combine(environment.LocalAppData, "Microsoft", "WinGet", "Packages");
            foreach (var package in environment.EnumerateDirectories(wingetPackages))
            {
                if (!Path.GetFileName(package).StartsWith("mg-chao.snow-shot", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = FirstExisting(environment, package, WindowsExecutableNames);
                if (candidate is not null)
                {
                    return candidate;
                }

                foreach (var child in environment.EnumerateDirectories(package))
                {
                    var nested = FirstExisting(environment, child, WindowsExecutableNames);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    private static string? FirstExisting(IScreenshotEnvironment environment, string directory, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var direct = Path.Combine(directory, name);
            if (environment.FileExists(direct))
            {
                return direct;
            }

            var nested = Path.Combine(directory, "bin", name);
            if (environment.FileExists(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static string? FindMac(IScreenshotEnvironment environment)
    {
        var roots = new List<string> { "/Applications" };
        if (!string.IsNullOrWhiteSpace(environment.UserProfile))
        {
            roots.Add(Path.Combine(environment.UserProfile, "Applications"));
        }

        foreach (var root in roots)
        {
            // The release DMG ships "Snow Shot.app"; a CMake install tree keeps "snow_shot.app".
            foreach (var bundleName in new[] { "Snow Shot.app", "snow_shot.app" })
            {
                var bundle = Path.Combine(root, bundleName, "Contents", "MacOS");
                var candidate = FirstExisting(environment, bundle, MacExecutableNames);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindLinux(IScreenshotEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.PathEnvironment))
        {
            foreach (var entry in environment.PathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(entry.Trim(), FlameshotExecutable);
                if (environment.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var fallback in new[] { "/usr/bin/flameshot", "/usr/local/bin/flameshot", "/snap/bin/flameshot" })
        {
            if (environment.FileExists(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static ScreenshotBackend Found(IScreenshotEnvironment environment, ScreenshotPlatform platform, string path)
    {
        var captureArguments = ScreenshotCaptureHelp.Parse(environment.ReadLaunchHelp(path));
        if (platform == ScreenshotPlatform.Linux && captureArguments.Count == 0)
        {
            captureArguments = ["gui"];
        }

        var snowShot = platform != ScreenshotPlatform.Linux;
        return new ScreenshotBackend(
            true,
            snowShot ? "snow-shot" : "flameshot",
            snowShot ? "Snow Shot" : "Flameshot",
            path,
            Path.GetFileNameWithoutExtension(path),
            captureArguments,
            InstallHintFor(platform),
            HotkeyNoteFor(platform));
    }

    private static ScreenshotBackend Missing(ScreenshotPlatform platform, string hint)
    {
        var snowShot = platform != ScreenshotPlatform.Linux;
        return new ScreenshotBackend(
            false,
            snowShot ? "snow-shot" : "flameshot",
            snowShot ? "Snow Shot" : "Flameshot",
            null,
            snowShot ? SnowShotBinary : FlameshotExecutable,
            platform == ScreenshotPlatform.Linux ? ["gui"] : [],
            hint,
            HotkeyNoteFor(platform));
    }

    private static string InstallHintFor(ScreenshotPlatform platform)
    {
        return platform switch
        {
            ScreenshotPlatform.Windows =>
                "未找到 Snow Shot。可以用 winget install --id mg-chao.snow-shot -e 安装，或在设置里填写 snow_shot.exe 的绝对路径。4K 或多屏如果界面发虚，改用 Snow Shot 的 fixedRuntime 构建。",
            ScreenshotPlatform.MacOs =>
                "未找到 Snow Shot。请从 https://snowshot.top/download/ 安装，或在设置里填写 Snow Shot.app 内 snow_shot 的绝对路径。",
            _ =>
                "未找到 Flameshot。请用发行版的包管理器安装 flameshot，或在设置里填写可执行文件的绝对路径。"
        };
    }

    private static string HotkeyNoteFor(ScreenshotPlatform platform)
    {
        return platform switch
        {
            ScreenshotPlatform.Linux =>
                "截图快捷键由 Flameshot 或桌面环境注册。MyPowerTools 的 Linux 全局热键尚未实现，不会再注册一组。",
            ScreenshotPlatform.MacOs =>
                "截图快捷键由 Snow Shot 自己注册。MyPowerTools 不会再占用一组全局热键；「立即截图」需要在「系统设置 › 隐私与安全性 › 辅助功能」中授权 MyPowerTools。",
            _ => "截图快捷键由 Snow Shot 自己注册。MyPowerTools 不会再占用一组全局热键。"
        };
    }
}
