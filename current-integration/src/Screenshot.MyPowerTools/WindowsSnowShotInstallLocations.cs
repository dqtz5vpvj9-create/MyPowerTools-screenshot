using System.Security;
using Microsoft.Win32;

namespace Screenshot.MyPowerTools;

internal static class WindowsSnowShotInstallLocations
{
    public static IReadOnlyList<string> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var locations = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(subKeyName);
                        var displayName = app?.GetValue("DisplayName") as string;
                        if (displayName is null ||
                            !displayName.Contains("Snow Shot", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var location = app?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            locations.Add(location.Trim().Trim('"'));
                        }
                    }
                }
                catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        return locations;
    }
}
