using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class ShelfReferenceChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        const string path = @"C:\Shelf\chosen";
        check(ShelfReferences.FromAttributes(@"C:\", 0x10, 0).IsFolder, "Root folder reference accepted");
        var folder = ShelfReferences.FromAttributes(path, 0x10, 0);
        check(folder.IsFolder && !folder.IsOnlineOnly, "Shelf accepts a folder without inspecting its children");
        foreach (uint flag in new uint[] { 0x1000, 0x40000, 0x400000 })
        foreach (uint directory in new uint[] { 0, 0x10 })
        {
            var reference = ShelfReferences.FromAttributes(path, flag | directory, 0);
            check(reference.IsOnlineOnly && reference.IsFolder == (directory != 0), "Each recall/offline flag makes a reference online only");
            try { ShelfReferences.RequireContent(reference); check(false, "Cloud content refused"); }
            catch (UserError error) { check(error.Message == ShelfReferences.OnlineOnlyRefusal, "Cloud content gets an explicit no-download refusal"); }
        }
        for (uint variant = 0; variant < 16; variant++)
            check(ShelfReferences.FromAttributes(path, 0x400, 0x9000001a | variant << 12).IsOnlineOnly, "All cloud reparse variants stay conservative references");
        foreach (uint tag in new uint[] { 0, 0xa0000003, 0xa000000c, 0x8000001b, 0xffffffff, 0x9001001a })
        foreach (uint flags in new uint[] { 0x400, 0x410, 0x1410 })
            reject(() => ShelfReferences.FromAttributes(path, flags, tag), "Non-cloud reparse files and folders refused even with offline bit");
        reject(() => ShelfReferences.FromAttributes(path, uint.MaxValue, 0), "Failed attribute sentinel never becomes a reference");
        reject(() => ShelfReferences.RequireContent(folder), "Folder cannot reach a file content reader");
        ShelfReferences.RequireContent(ShelfReferences.FromAttributes(path, 0x80, 0));
        int calls = 0;
        ShelfReference Inspect(string p, CancellationToken token) { token.ThrowIfCancellationRequested(); calls++; return new(p, false, false); }
        check(ShelfReferences.Inspect(new[] { path, path.ToUpperInvariant() }, Inspect, default).Length == 1 && calls == 1, "Reference inspection folds duplicate paths");
        calls = 0;
        reject(() => ShelfReferences.Inspect(new[] { path }, Inspect, new CancellationToken(true)), "Cancelled add refuses before attributes");
        check(calls == 0, "Pre-cancel performs no native inspection");
        foreach (string hostile in new[] { @"\\server\share\x", @"C:\Shelf\..\x", @"C:\Shelf\x:stream", "C:\\Shelf\\bad\0" })
            reject(() => ShelfReferences.Inspect(new[] { hostile }, Inspect, default), "Hostile reference refused before native access");
        check(calls == 0, "Hostile paths never reach the attribute API");
        check(ShelfReferences.Inspect(Enumerable.Range(0, 100).Select(i => path + i), Inspect, default).Length == 100, "100 session references supported");
        reject(() => ShelfReferences.Inspect(Enumerable.Range(0, 101).Select(i => path + i), Inspect, default), "Reference bound rejects overflow");
        using var cancellation = new CancellationTokenSource();
        reject(() => ShelfReferences.Inspect(new[] { path, path + "2" }, (p, _) => { cancellation.Cancel(); return new(p, false, false); }, cancellation.Token), "Mid-inspection cancellation refuses whole add");
    }
}
