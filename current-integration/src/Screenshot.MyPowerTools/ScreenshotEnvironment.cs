namespace Screenshot.MyPowerTools;

public interface IScreenshotEnvironment
{
    ScreenshotPlatform Platform { get; }
    string? ProgramFiles { get; }
    string? ProgramFilesX86 { get; }
    string? LocalAppData { get; }
    string? UserProfile { get; }
    string? PathEnvironment { get; }
    bool FileExists(string path);
    string? ReadAllText(string path);
    IEnumerable<string> EnumerateDirectories(string directory);
    IReadOnlyList<string> WindowsInstallLocations();
    string? ReadLaunchHelp(string executablePath);
    string? ReadConfiguredScreenshotHotkey(string executablePath);
    bool TrySendShortcut(string gesture, out string error);
    void WaitAfterResidentStart();
    bool IsProcessRunning(string processName);
    bool IsProcessAlive(int processId);
    bool TryGetLiveProcessIdentity(int processId, out string processName, out string? imagePath);
    int StartProcess(string executablePath, IReadOnlyList<string> arguments);
    void KillProcess(int processId);
}

public enum ScreenshotPlatform
{
    Windows,
    MacOs,
    Linux
}

public sealed class SystemScreenshotEnvironment : IScreenshotEnvironment
{
    public static ScreenshotPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ScreenshotPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return ScreenshotPlatform.MacOs;
        }

        return ScreenshotPlatform.Linux;
    }

    public ScreenshotPlatform Platform { get; } = DetectPlatform();
    public string? ProgramFiles { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string? ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public string? LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string? UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? PathEnvironment { get; } = Environment.GetEnvironmentVariable("PATH");

    public bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public string? ReadAllText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> WindowsInstallLocations()
    {
        return WindowsSnowShotInstallLocations.Read();
    }

    public string? ReadLaunchHelp(string executablePath)
    {
        // Current Snow Shot (snow_shot.exe) and the older snowshot.exe both
        // start the GUI when given --help, and neither prints a capture flag.
        // The live probe stays empty. Flameshot's `gui` command is fixed in
        // the resolver and does not depend on this text.
        return null;
    }

    public string? ReadConfiguredScreenshotHotkey(string executablePath)
    {
        foreach (var path in ScreenshotSnowShotHotkey.CandidateConfigPaths(executablePath))
        {
            var gesture = ScreenshotSnowShotHotkey.ReadFromConfigJson(ReadAllText(path));
            if (!string.IsNullOrWhiteSpace(gesture))
            {
                return gesture;
            }
        }

        return null;
    }

    public bool TrySendShortcut(string gesture, out string error)
    {
        if (Platform != ScreenshotPlatform.Windows)
        {
            error = "当前平台无法从 MyPowerTools 注入截图快捷键。";
            return false;
        }

        return ScreenshotWindowsShortcut.TrySend(gesture, out error);
    }

    public void WaitAfterResidentStart()
    {
        // Newly started Snow Shot registers global shortcuts just after ready.
        Thread.Sleep(700);
    }

    public bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        try
        {
            return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryGetLiveProcessIdentity(int processId, out string processName, out string? imagePath)
    {
        processName = "";
        imagePath = null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            processName = process.ProcessName;
            try
            {
                imagePath = process.MainModule?.FileName;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                imagePath = null;
            }

            return !string.IsNullOrWhiteSpace(processName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public int StartProcess(string executablePath, IReadOnlyList<string> arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ""
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        return process.Id;
    }

    public void KillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                // Only the owned screenshot process — not an entire tree.
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
