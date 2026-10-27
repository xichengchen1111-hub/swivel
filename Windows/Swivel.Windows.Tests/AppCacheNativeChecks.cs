using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AppCacheNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string local = Path.Combine(directory, "app-caches", "Local"), roaming = Path.Combine(directory, "app-caches", "Roaming");
        string chrome = Path.Combine(local, @"Google\Chrome\User Data");
        void Put(string relative, int bytes) { string path = Path.Combine(chrome, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllBytes(path, new byte[bytes]); }
        Put(@"Default\Preferences", 2); Put(@"Default\Cache\Cache_Data\f_000001", 300); Put(@"Default\Cache\Cache_Data\index", 20);
        Put(@"Default\Network\Cookies", 50); Put(@"Crashpad\Cache\x", 5); Put(@"GrShaderCache\data_0", 10);
        string hidden = Path.Combine(chrome, @"Default\GPUCache"); Directory.CreateDirectory(hidden);
        new DirectoryInfo(hidden).Attributes |= FileAttributes.System;
        string firefox = Path.Combine(local, @"Mozilla\Firefox\Profiles\abc.default-release\cache2"); Directory.CreateDirectory(firefox);
        System.IO.File.WriteAllBytes(Path.Combine(firefox, "entries"), new byte[7]);
        Directory.CreateDirectory(Path.Combine(roaming, @"Code\Cache"));
        string outside = Path.Combine(directory, "app-caches", "outside"); Directory.CreateDirectory(outside);
        string link = Path.Combine(chrome, @"Default\Code Cache"); bool linked = false;
        try { Directory.CreateSymbolicLink(link, outside); linked = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        { Console.WriteLine("SKIP: app-cache directory symlink fixture requires Windows policy permission."); }

        AppCacheResult Read() => AppCaches.Read(root => root == AppCacheBase.Local ? local : roaming, path => NativeFiles.MetadataDirectory.Open(path), CancellationToken.None);
        AppCacheResult result;
        try
        {
            result = Read();
            var paths = result.Items.Select(i => i.Folder.Path).ToArray();
            check(paths.SequenceEqual(new[] { Path.Combine(chrome, "GrShaderCache"), Path.Combine(chrome, @"Default\Cache"), firefox, Path.Combine(roaming, @"Code\Cache") }),
                "Native search finds only the plain catalogue folders: no Crashpad, system-marked or linked cache folder: " + string.Join(" | ", paths));
            var cache = result.Items.Single(i => i.Folder.Path == Path.Combine(chrome, @"Default\Cache")).Size!;
            check(cache.Bytes == 320 && cache.Files == 2 && !cache.Partial, "Native directory records measure the cache folder four levels deep");
            check(result.Apps.Single(a => a.App.Id == "chrome").Profiles == 1 && result.Apps.Single(a => a.App.Id == "edge").State == AppCacheAppState.NotFound, "Native search counts the one Chromium profile and reports a missing app as not found");
            check(!linked || Directory.Exists(outside) && Directory.Exists(link), "The linked folder and its target are left alone");
        }
        finally { new DirectoryInfo(hidden).Attributes &= ~FileAttributes.System; if (linked) Directory.Delete(link); }
        // A held handle without delete sharing would make the rename fail.
        bool released;
        try { Directory.Move(chrome, chrome + " moved"); Directory.Move(chrome + " moved", chrome); released = true; }
        catch (IOException) { released = false; }
        catch (UnauthorizedAccessException) { released = false; }
        check(released, "Every search and measurement handle is released afterwards");

        var names = AppCacheReader.RunningNames();
        string self; using (var current = Process.GetCurrentProcess()) self = current.ProcessName;
        check(names is not null && names.Contains(self) && names.Contains(self.ToUpperInvariant()), "Running process names are listed, ignoring case");
        var fake = AppCaches.Apps[0] with { Processes = new[] { self } };
        check(AppCacheReader.IsRunning(fake) && !AppCacheReader.IsRunning(AppCaches.Apps[0] with { Processes = new[] { "swivel-no-such-process" } }), "An app counts as running only while a process with its name runs");

        string target = Path.Combine(chrome, @"Default\Cache");
        try { NativeFiles.RecycleAppCache(target, () => true); check(false, "A cache folder is never moved while its app runs"); }
        catch (UserError error) { check(error.Message == NativeFiles.CacheAppRunning, "A cache folder is never moved while its app runs, and the refusal says why"); }
        check(Directory.Exists(target) && System.IO.File.Exists(Path.Combine(target, @"Cache_Data\f_000001")), "The refused folder stays in place");
        string swapped = Path.Combine(chrome, "GrShaderCache");
        Directory.Delete(swapped, true); System.IO.File.WriteAllBytes(swapped, new byte[1]);
        try { NativeFiles.RecycleAppCache(swapped, () => false); check(false, "A cache folder replaced by a file is refused"); }
        catch (UserError error) { check(error.Message == NativeFiles.RecycleChanged, "A cache folder replaced by a file is refused as changed"); }
        System.IO.File.Delete(swapped);

        var app = Application.Current as App ?? new App();
        var dialog = new AppCacheDialog(app, result, () => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var footer = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        var move = footer.Single(b => Equals(b.Content, app.L("Move Selected to Recycle Bin…")));
        var list = ((StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<ListView>().Single();
        check(!move.IsEnabled && list.Items.Count == 4 && list.SelectedItems.Count == 0 && footer.Last().IsCancel, "Browser and App Caches lists the found folders with nothing selected and Move disabled");
        list.SelectAll(); check(move.IsEnabled, "Selecting listed folders enables the move");
        string notFound = app.L("Not found at the default place: {0}").Replace("{0}", "");
        check(dialog.CurrentReport.Contains(target) && dialog.CurrentReport.Split('\n').Any(line => line.StartsWith(notFound, StringComparison.Ordinal) && line.Contains("Microsoft Edge"))
            && dialog.CurrentReport.Contains("Google Chrome (chrome.exe)") && dialog.CurrentReport.Contains(app.L(AppCaches.Notice)), "The report lists the folders, the apps looked for with their programs, the apps not found and the limits");
        dialog.Close();
        var unknown = new AppCacheDialog(app, result, () => null);
        check(unknown.CurrentReport.Length > 0 && ((StackPanel)((ScrollViewer)((DockPanel)unknown.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<TextBlock>()
            .Any(t => t.Text == app.L("Swivel could not list the running programs, so no folder can be moved or put back now.")), "When programs cannot be listed the window says nothing can be moved or put back");
        unknown.Close();

        if (!RecycleBin.Available(target)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleAppCache(target, () => false);
        check(!Directory.Exists(target) && stored.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(chrome, @"Default\Network")),
            "The cache folder moves to the Recycle Bin and the profile's other data stays");
        try { NativeFiles.RestoreAppCache(target, stored, () => true); check(false, "Undo waits while the app runs"); }
        catch (UserError error) { check(error.Message == NativeFiles.CacheRestoreRunning && !Directory.Exists(target), "Undo waits while the app runs, leaving the folder in the Recycle Bin"); }
        NativeFiles.RestoreAppCache(target, stored, () => false);
        check(new FileInfo(Path.Combine(target, @"Cache_Data\f_000001")).Length == 300, "Undo restores the cache folder with its files");
        string again = NativeFiles.RecycleAppCache(target, () => false);
        Directory.CreateDirectory(target);
        reject(() => NativeFiles.RestoreAppCache(target, again, () => false), "Undo refuses when the app made a new cache folder in its place");
        // Leaves nothing behind in the Recycle Bin.
        Directory.Delete(target);
        NativeFiles.RestoreAppCache(target, again, () => false);
        check(System.IO.File.Exists(Path.Combine(target, @"Cache_Data\f_000001")), "Undo works again once the new folder is gone");
    }
}
