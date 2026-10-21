// Settings (Prefs) are process-wide, so tests run one at a time.
[assembly: DoNotParallelize]

namespace Swivel.Tests;

/// Gives every test run its own settings file and a scratch folder for output files.
[TestClass]
public static class TestSetup
{
    public static string Scratch { get; private set; } = "";

    /// windows\Swivel, the app's source folder.
    public static string SourceRoot => Path.GetFullPath(typeof(TestSetup).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "SwivelSource").Value!);

    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Scratch = Path.Combine(Path.GetTempPath(), "SwivelTests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Scratch);
        Swivel.Prefs.FilePath = Path.Combine(Scratch, "settings.json");
        Swivel.Prefs.Reload();
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// A fresh empty folder inside the scratch folder.
    public static string NewFolder(string name)
    {
        var dir = Path.Combine(Scratch, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Starts each test from default settings.
    public static void ResetPrefs()
    {
        File.Delete(Swivel.Prefs.FilePath);
        Swivel.Prefs.Reload();
    }
}
