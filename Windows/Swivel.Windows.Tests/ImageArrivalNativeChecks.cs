using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ImageArrivalNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
        => RunAsync(directory, check, reject).GetAwaiter().GetResult();

    private static async Task RunAsync(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Actual Windows FSW/WIC/lease exercise. Compiling on Mac does not execute this fixture.
        string root = Path.Combine(directory, "image-arrivals"); Directory.CreateDirectory(root);
        byte[] png = Png();
        string existing = Path.Combine(root, "existing.png"); File.WriteAllBytes(existing, png);
        reject(() => ImageArrivalSession.Create(root, new CancellationToken(true)), "Pre-cancelled arrival creation does not open a watcher");
        var session = ImageArrivalSession.Create(root, default);
        try
        {
            check(session.Snapshot.Status == ArrivalStatus.Stopped && session.Snapshot.Cards.Count == 0, "Native arrival window is initially stopped");
            File.WriteAllBytes(Path.Combine(root, "before-start.png"), png);
            reject(() => Directory.Move(root, root + " moved"), "Chosen arrival root remains leased before and through Start");
            session.Start();
            await Task.Delay(450);
            check(session.Snapshot.Cards.Count == 0 && session.Snapshot.EligibleNotifications == 0, "Start does not enumerate or automatically read existing images");
            File.WriteAllBytes(existing, png);
            await Task.Delay(250);
            check(session.Snapshot.EligibleNotifications == 0, "Changed-only existing image writes do not trigger reading");
            string added = Path.Combine(root, "new.png"); File.WriteAllBytes(added, png);
            await Until(() => session.Snapshot.Cards.Any(c => c.Path == added));
            var card = session.Snapshot.Cards.Single(c => c.Path == added);
            check(card.Thumbnail.IsFrozen && card.Thumbnail.PixelWidth <= 248 && card.Thumbnail.PixelHeight <= 206 && card.Kind == ArrivalKind.Created, "Native new path produces a detached frozen bounded image snapshot");
            check(session.Snapshot.ReservedBytes >= png.Length, "Source length reserved before native decoding");
            string renamed = Path.Combine(root, "renamed-old.png"); File.Move(existing, renamed);
            await Until(() => session.Snapshot.Cards.Any(c => c.Path == renamed));
            check(session.Snapshot.Cards.Single(c => c.Path == renamed).Kind == ArrivalKind.Renamed, "Old file renamed after Start is an explicitly authorized current-path arrival");
            string temporary = Path.Combine(root, "temporary.tmp"), finalized = Path.Combine(root, "finalized.png");
            File.WriteAllBytes(temporary, png); File.Move(temporary, finalized);
            await Until(() => session.Snapshot.Cards.Any(c => c.Path == finalized));
            check(session.Snapshot.Cards.All(c => c.Path != temporary), "Temporary suffix is ignored; only new renamed image path is decoded");
            string child = Path.Combine(root, "child"); Directory.CreateDirectory(child); File.WriteAllBytes(Path.Combine(child, "nested.png"), png);
            await Task.Delay(250);
            check(session.Snapshot.Cards.All(c => !c.Path.Contains("nested.png", StringComparison.Ordinal)), "Arrival watcher never recurses into new subfolders");
            // The held source lasts only through decode. A later ordinary writer can change the path;
            // the card remains a historical snapshot and Hang must invoke the existing fresh read.
            File.WriteAllBytes(added, png);
            check(session.Snapshot.Cards.Single(c => c.Path == added).Id == card.Id, "Changed notification does not replace the historical card");
            int beforeStop = session.Snapshot.Cards.Count;
            await session.StopAsync();
            check(session.Completion.IsCompletedSuccessfully && session.Snapshot.Status == ArrivalStatus.Stopped && session.Snapshot.Cards.Count == beforeStop, "Native Stop joins resources while preserving already decoded cards");
            File.WriteAllBytes(Path.Combine(root, "after-stop.png"), png); await Task.Delay(200);
            check(session.Snapshot.Cards.Count == beforeStop, "Stopped native session never delivers a late new card");
        }
        finally { await session.StopAsync(ArrivalStopReason.Closed); }

        // Reservation aborts before content decoding and still releases both stream and ancestors.
        string source = Path.Combine(root, "new.png"); bool reserved = false;
        reject(() => ImageArrivalSession.ReadThumbnail(source, length => { reserved = length == png.Length; throw new OperationCanceledException(); }, default), "Reservation cancellation prevents native image decoding");
        check(reserved, "Native reservation uses current held source stream length");
        using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None)) check(writer.CanWrite, "Reservation failure releases the source file lease");
        reject(() => ImageArrivalSession.ReadThumbnail(source, _ => throw new Exception("must not reserve"), new CancellationToken(true)), "Pre-cancelled thumbnail does not acquire input");

        var partial = ImageArrivalSession.Create(root, default);
        try
        {
            partial.Start(); string writing = Path.Combine(root, "still-writing.png");
            using (var writer = new FileStream(writing, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                writer.Write(png); writer.Flush();
                await Until(() => partial.Snapshot.Attempts >= 1);
                check(partial.Snapshot.Cards.Count == 0 && partial.Snapshot.ReservedBytes == 0, "Active writer is refused before source read or byte reservation");
            }
            await Until(() => partial.Snapshot.Cards.Any(c => c.Path == writing));
            // Real Windows may hold a new file briefly (antivirus) after the writer closes, so a later
            // notification can read it again; the per-file cap of four is checked in Core.
            var after = partial.Snapshot;
            check(after.Attempts >= 2 && after.Attempts <= 4 * (1 + after.FailedFiles), "Spaced retry reads after writer release within four attempts per notification: attempts " + after.Attempts + ", failed " + after.FailedFiles);
        }
        finally { await partial.StopAsync(ArrivalStopReason.Closed); }
        Directory.Move(root, root + " released");
        check(Directory.Exists(root + " released"), "All watcher/root/input leases are released after joined sessions");
    }
    private static byte[] Png()
    {
        var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 32 * 16 * 4).ToArray(), 32 * 4);
        bitmap.Freeze(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
