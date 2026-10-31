using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ShelfReferenceNativeChecks
{
    internal static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string folder = Path.Combine(directory, "shelf-folder"); Directory.CreateDirectory(folder);
        string child = Path.Combine(folder, "locked.dat"); File.WriteAllBytes(child, new byte[] { 1, 2, 3 });
        using (var held = new FileStream(child, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var reference = NativeFiles.InspectShelfReference(folder, default);
            check(reference.Path == folder && reference.IsFolder && !reference.IsOnlineOnly, "Native folder reference needs no child data handles");
            check(ShelfReferences.Inspect(new[] { folder, folder.ToUpperInvariant() }, NativeFiles.InspectShelfReference, default).Length == 1, "Native reference batch retains one real folder path");
        }
        check(File.ReadAllBytes(child).SequenceEqual(new byte[] { 1, 2, 3 }), "Reference leaves folder contents unchanged");
        check(!NativeFiles.InspectShelfReference(child, default).IsFolder, "Native file reference retains its type");
        string root = Path.GetPathRoot(directory)!;
        check(NativeFiles.InspectShelfReference(root, default).IsFolder, "Drive root can be a folder reference");
        reject(() => NativeFiles.InspectShelfReference(child + ".missing", default), "Stale reference refused");
        reject(() => NativeFiles.InspectShelfReference(folder, new CancellationToken(true)), "Native cancelled reference refuses before filesystem access");
        foreach (string path in new[] { @"\\server\share\x", @"C:\safe\..\x", @"C:\safe\x:secret" })
            reject(() => NativeFiles.InspectShelfReference(path, default), "Native hostile reference refused");
        // Symlink creation is privilege dependent; the existing suite reports
        // that limitation too. Test both leaf and ancestor refusal when present.
        string link = Path.Combine(directory, "shelf-link"); bool created = false;
        try { Directory.CreateSymbolicLink(link, folder); created = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { Console.WriteLine("SKIP: Shelf directory symlink checks require Developer Mode or symlink privilege."); }
        if (created)
        {
            try
            {
                reject(() => NativeFiles.InspectShelfReference(link, default), "Non-cloud reparse folder refused");
                reject(() => NativeFiles.InspectShelfReference(Path.Combine(link, "locked.dat"), default), "Reparse ancestor refused before child lookup");
            }
            finally { Directory.Delete(link); }
        }
        // OFFLINE can be set without a cloud service; never manufacture a cloud reparse tag.
        FileAttributes original = File.GetAttributes(child);
        try
        {
            File.SetAttributes(child, original | FileAttributes.Offline);
            check(NativeFiles.InspectShelfReference(child, default).IsOnlineOnly, "Native OFFLINE file accepted as an online-only reference");
            try { using var input = NativeFiles.OpenRead(child); check(false, "Offline file content refused"); }
            catch (UserError error) { check(error.Message == ShelfReferences.OnlineOnlyRefusal, "Native content reader refuses OFFLINE with no-download message"); }
            reject(() => Explorer.Reveal(child), "Online-only reveal refuses before asking Explorer to preview it");
            reject(() => NativeFiles.ValidateInput(child), "Content validation does not accept OFFLINE references");
        }
        finally { File.SetAttributes(child, original); }

        void Cancelled(Action action, string message)
        {
            try { action(); check(false, message); }
            catch (OperationCanceledException) { check(true, message); }
        }
        int inspections = 0, cacheCalls = 0;
        ShelfReference Inspect(string path, CancellationToken token) { inspections++; return new(path, false, true); }
        BitmapSource? Cache(string path, uint flags, int edge, CancellationToken token)
        {
            cacheCalls++;
            check(path == child && flags == 0x18 && edge == 256, "Placeholder uses THUMBNAILONLY plus INCACHEONLY, bounded to 256 pixels");
            return null;
        }
        Cancelled(() => ShelfSystemThumbnail.Load(child, new CancellationToken(true), Inspect, Cache), "Pre-cancel prevents all thumbnail work");
        check(inspections == 0 && cacheCalls == 0, "Cancelled thumbnail does not enter Shell or inspect metadata");
        check(ShelfSystemThumbnail.Load(child, default, Inspect, Cache) is null && cacheCalls == 1, "Cache miss leaves plain reference row intact");
        using var cancellation = new CancellationTokenSource();
        Cancelled(() => ShelfSystemThumbnail.Load(child, cancellation.Token, (path, _) => { cancellation.Cancel(); return new(path, true, true); }, Cache), "Cancel after attributes prevents Shell access");
        check(cacheCalls == 1, "No Shell call after cancellation during inspection");
        using var failedInspection = new CancellationTokenSource();
        Cancelled(() => ShelfSystemThumbnail.Load(child, failedInspection.Token, (_, _) => { failedInspection.Cancel(); throw new IOException("Injected metadata failure"); }, Cache), "Cancellation suppresses a late metadata failure before Shell access");
        reject(() => ShelfSystemThumbnail.Load(child, default, (_, _) => throw new UserError(ShelfReferences.LocalOnly), Cache), "Rejected reference never reaches Shell");
        check(cacheCalls == 1, "Unsafe metadata failure cannot request a thumbnail");
        using var late = new CancellationTokenSource();
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); bitmap.Freeze();
        BitmapSource? PinnedCache(string path, uint flags, int edge, CancellationToken token)
        {
            reject(() => File.Move(child, child + ".swapped"), "Shell lookup pins the checked leaf against path replacement");
            reject(() => Directory.Move(folder, folder + ".swapped"), "Shell lookup pins checked ancestors against path replacement");
            return bitmap;
        }
        check(ReferenceEquals(ShelfSystemThumbnail.Load(child, default, PinnedCache), bitmap), "Pinned Shell lookup returns its result");
        void CanRenameAfterShell(string message)
        {
            File.Move(child, child + ".after"); File.Move(child + ".after", child);
            Directory.Move(folder, folder + ".after"); Directory.Move(folder + ".after", folder);
            check(true, message);
        }
        CanRenameAfterShell("Successful Shell lookup releases leaf and ancestor handles");
        using var pinnedCancel = new CancellationTokenSource();
        Cancelled(() => ShelfSystemThumbnail.Load(child, pinnedCancel.Token, (p, f, e, t) => { PinnedCache(p, f, e, t); pinnedCancel.Cancel(); return bitmap; }), "Cancelled pinned lookup discards its result");
        CanRenameAfterShell("Cancelled Shell lookup releases leaf and ancestor handles");
        bool injectedFailure = false;
        try { ShelfSystemThumbnail.Load(child, default, (p, f, e, t) => { PinnedCache(p, f, e, t); throw new IOException("Injected pinned Shell failure"); }); }
        catch (IOException) { injectedFailure = true; }
        check(injectedFailure, "Pinned Shell failure reaches the caller");
        CanRenameAfterShell("Failed Shell lookup releases leaf and ancestor handles");
        Cancelled(() => ShelfSystemThumbnail.Load(child, late.Token, Inspect, (_, _, _, _) => { late.Cancel(); return bitmap; }), "Late cancellation discards a completed bitmap");
        using var failed = new CancellationTokenSource();
        Cancelled(() => ShelfSystemThumbnail.Load(child, failed.Token, Inspect, (_, _, _, _) => { failed.Cancel(); throw new IOException("Injected Shell failure"); }), "Cancelled failed Shell call cannot publish a late result");
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not use real product settings in this fixture.");
        bool shelfWasOn = app.Prefs.IsOn(Feature.Shelf);
        string[] savedShelf = app.Shelf.ToArray();
        try
        {
            app.Shelf.Clear();
            app.Prefs.Set(Feature.Shelf, false);
            reject(() => app.AddShelf(new[] { folder }), "Shelf is still opt-in for folder references");
            app.Prefs.Set(Feature.Shelf, true);
            reject(() => app.AddShelf(new[] { folder, child + ".missing" }), "Mixed stale batch refuses atomically");
            check(app.Shelf.Count == 0, "Failed add never partially mutates session references");
            app.AddShelf(new[] { folder, child }); app.AddShelf(new[] { folder.ToUpperInvariant() });
            check(app.Shelf.SequenceEqual(new[] { folder, child }), "Shelf stores exact real paths for mixed folder/file drag-out, without duplicates");
            check(app.ShelfLabel(folder).Contains(app.L("Folder reference"), StringComparison.Ordinal), "Folder reference row has a distinct label");
            var list = UI.ShelfList(app); list.SelectedItems.Add(folder); list.SelectedItems.Add(child);
            check(list.SelectedItems.Cast<string>().SequenceEqual(new[] { folder, child }), "Shelf presentation retains real string paths in multiselection");
            check(app.ValidateShelf(list.SelectedItems.Cast<string>()).SequenceEqual(new[] { folder, child }), "Drag validation accepts the exact mixed multiselection");
            File.SetAttributes(child, original | FileAttributes.Offline);
            app.Shelf.Remove(child); app.AddShelf(new[] { child });
            check(app.ShelfLabel(child).Contains(app.L("Online only"), StringComparison.Ordinal), "Online-only row label is explicit");
            check(app.ValidateShelf(new[] { child }).Single() == child, "Online-only item can leave Shelf as the real file reference");
            var floating = new FloatingShelf(app);
            check(floating.Content is not null, "Floating Shelf builds with folder and cloud reference rows"); floating.Close();
            var preview = new ShelfSystemPreviewDialog(app, child, bitmap);
            var image = ((DockPanel)preview.Content).Children.OfType<Image>().Single();
            check(ReferenceEquals(image.Source, bitmap), "System preview holds only the frozen image copy");
            preview.Close(); check(image.Source is null, "Closing preview releases managed image reference");
            preview.ReleaseImage(); check(image.Source is null, "Preview cleanup is idempotent");
            app.Shelf.Remove(child); check(app.ShelfLabel(child) == child, "Removing a row drops its stored metadata label");
            app.Shelf.Clear(); check(app.ShelfLabel(folder) == folder, "Clearing Shelf drops all metadata labels");
        }
        finally
        {
            File.SetAttributes(child, original); app.Shelf.Clear();
            app.Prefs.Set(Feature.Shelf, true); if (savedShelf.Length != 0) app.AddShelf(savedShelf);
            app.Prefs.Set(Feature.Shelf, shelfWasOn);
        }
        // A real shell cache hit is machine dependent; a miss is valid. This is
        // run on an STA background thread, never on the WPF dispatcher.
        Exception? failure = null;
        var worker = new Thread(() => { try { var result = ShelfSystemThumbnail.Load(child, default); check(result is null || result.IsFrozen && result.PixelWidth <= 256 && result.PixelHeight <= 256, "Native Shell result is frozen and bounded, or absent"); } catch (Exception error) { failure = error; } });
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); worker.Join();
        check(failure is null, "Native cached Shell lookup releases resources without failure");
    }
}
