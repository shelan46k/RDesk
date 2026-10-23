namespace RDesk.Helpers;

/// <summary>
/// User data lives in Roaming AppData so it does not collide with the Velopack
/// install folder (%LocalAppData%\RDesk). That install folder is deleted on uninstall.
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RDesk");

    public static string LegacyLocalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RDesk");

    public static string ConfigFilePath => Path.Combine(DataDirectory, "config.json");
    public static string StartupLogPath => Path.Combine(DataDirectory, "startup.log");
    public static string OrganizeHistoryPath => Path.Combine(DataDirectory, "organize-history.json");
    public static string SnapshotFolder => Path.Combine(DataDirectory, "Backups");

    public static void EnsureDataDirectory()
    {
        MigrateFromLegacyInstallFolder();
        Directory.CreateDirectory(DataDirectory);
    }

    private static void MigrateFromLegacyInstallFolder()
    {
        try
        {
            if (!Directory.Exists(LegacyLocalDirectory))
            {
                return;
            }

            // A real Velopack install has Update.exe; do not touch that tree.
            if (File.Exists(Path.Combine(LegacyLocalDirectory, "Update.exe")))
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);
            foreach (var name in new[] { "config.json", "organize-history.json", "startup.log", "selftest.log" })
            {
                var src = Path.Combine(LegacyLocalDirectory, name);
                var dst = Path.Combine(DataDirectory, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst);
                }
            }

            var oldBackups = Path.Combine(LegacyLocalDirectory, "Backups");
            if (Directory.Exists(oldBackups))
            {
                Directory.CreateDirectory(SnapshotFolder);
                foreach (var file in Directory.GetFiles(oldBackups))
                {
                    var dst = Path.Combine(SnapshotFolder, Path.GetFileName(file));
                    if (!File.Exists(dst))
                    {
                        File.Copy(file, dst);
                    }
                }
            }

            try
            {
                Directory.Delete(LegacyLocalDirectory, recursive: true);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }
}
