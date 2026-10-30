using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Where Windows says screenshots go. Never creates the folder (no KF_FLAG_CREATE) and
/// asks Windows not to verify it, so a missing folder is reported rather than made.
internal static class ScreenshotFolder
{
    internal const uint DontVerify = 0x4000;
    internal readonly record struct Location(string? Path,string? Issue);

    internal static Location Resolve(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? reported = Reported(ScreenshotCards.FolderId,token);
        token.ThrowIfCancellationRequested();
        if (reported is null) return new(null,ScreenshotCards.Unreported);
        var checkedPath = ReportedFolderPath.FromReported(reported);
        if (checkedPath.Path is not { } path) return new(null,checkedPath.Issue);
        if (!Directory.Exists(path)) return new(null,ScreenshotCards.NoFolder);
        return new(path,null);
    }

    /// The reported path for a known folder, or null when Windows reports none.
    internal static string? Reported(Guid id,CancellationToken token)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int result = SHGetKnownFolderPath(in id,DontVerify,IntPtr.Zero,out pointer);
            token.ThrowIfCancellationRequested();
            if (result != 0 || pointer == IntPtr.Zero) return null;
            // The OS contract supplies an allocated NUL-terminated string; copy at most 512 units.
            var text = new StringBuilder(513);
            for (int i = 0; i <= 512; i++)
            {
                char c = (char)(ushort)Marshal.ReadInt16(pointer,i * 2);
                if (c == '\0') return text.ToString();
                text.Append(c);
            }
            return null;
        }
        finally { if (pointer != IntPtr.Zero) CoTaskMemFree(pointer); }
    }

    [DllImport("shell32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(in Guid id,uint flags,IntPtr token,out IntPtr path);
    [DllImport("ole32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CoTaskMemFree(IntPtr memory);
}

/// Runs one arrival session against the Screenshots folder while the setting is on.
/// All members are called on the UI thread. Turning the setting on, off and on again
/// is serialized: a new session starts only after the previous one finished cleanup.
/// A session that reached its budgets or lost its notifications starts afresh at most once a
/// minute; a missing folder is waited for (checked every few seconds, never created); a session
/// whose folder was renamed or moved, or whose known-folder location changed, is replaced.
/// Other failures stop it until the setting is turned off and on again.
internal sealed class ScreenshotCardController
{
    private readonly Func<bool> desired;
    private readonly Func<CancellationToken,Task<IImageArrivalSession>> create;
    private readonly Action<ArrivalCard<BitmapSource>> show;
    private readonly Action hide;
    private readonly Action<string> report;
    private IImageArrivalSession? session;
    private CancellationTokenSource? starting;
    private Task loop = Task.CompletedTask;
    private bool dirty, looping, halted, renew, waitingForFolder;
    private readonly Func<bool>? folderReady;
    private readonly Func<IImageArrivalSession,bool>? stillValid;
    private DispatcherTimer? check;
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private Guid shown;
    private DateTime? lastRenewal;

    internal ScreenshotCardController(Func<bool> desired,Func<CancellationToken,Task<IImageArrivalSession>> create,
        Action<ArrivalCard<BitmapSource>> show,Action hide,Action<string> report,
        Func<bool>? folderReady = null,Func<IImageArrivalSession,bool>? stillValid = null)
    {
        this.desired = desired; this.create = create; this.show = show; this.hide = hide; this.report = report;
        this.folderReady = folderReady; this.stillValid = stillValid;
    }
    internal bool WaitingForFolder => waitingForFolder;

    internal IImageArrivalSession? Session => session;
    internal bool Running => session?.Snapshot.Status == ArrivalStatus.Running;
    internal bool Halted => halted;
    /// Completes when every start or stop requested so far has finished, including cleanup.
    internal Task Idle => loop;

    internal Task Update()
    {
        if (!desired()) { halted = false; waitingForFolder = false; renew = false; lastRenewal = null; check?.Stop(); try { starting?.Cancel(); } catch (ObjectDisposedException) { } }
        else if (folderReady is not null || stillValid is not null)
        {
            check ??= new DispatcherTimer(CheckInterval,DispatcherPriority.Background,(_,_) => Recheck(),System.Windows.Threading.Dispatcher.CurrentDispatcher);
            check.Start();
        }
        dirty = true;
        if (!looping) { looping = true; loop = Run(); }
        return loop;
    }

    /// Every few seconds while wanted: start once a missing folder exists; replace a session whose folder moved.
    internal void Recheck()
    {
        if (looping || !desired() || halted) return;
        try
        {
            if (session is null && waitingForFolder && folderReady?.Invoke() == true) { waitingForFolder = false; _ = Update(); }
            else if (session is { } active && active.Snapshot.Status == ArrivalStatus.Running && stillValid is not null && !stillValid(active))
            {
                // A folder that never matches its reported path (a substituted drive, say) must not restart every few seconds.
                var now = DateTime.UtcNow;
                if (lastRenewal is { } previous && now - previous < TimeSpan.FromMinutes(1))
                { halted = true; check?.Stop(); renew = true; _ = Update(); report("Screenshot cards are not running. Turn the setting off and on to try again."); return; }
                lastRenewal = now; renew = true; _ = Update();
            }
        }
        catch { } // A failed check tries again next time.
    }

    /// The card is gone (hung, handed out, dismissed): free its slot in the session's card limit.
    internal void Done(Guid id) { if (id == shown) shown = Guid.Empty; session?.Dismiss(id); }

    private async Task Run()
    {
        try
        {
            while (dirty)
            {
                dirty = false;
                bool want = desired();
                if (renew && session is { } moved) { renew = false; await StopAsync(moved,ArrivalStopReason.Cancelled); dirty = true; }
                else if (want && session is null && !halted && !waitingForFolder) await StartAsync();
                else if (!want && session is { } active) await StopAsync(active,ArrivalStopReason.FeatureDisabled);
            }
        }
        finally { looping = false; }
    }

    private async Task StartAsync()
    {
        using var source = new CancellationTokenSource();
        starting = source;
        IImageArrivalSession? created = null;
        try
        {
            created = await create(source.Token);
            if (source.IsCancellationRequested || !desired()) { await Join(created,ArrivalStopReason.Cancelled); return; }
            created.Start();
            session = created; created.Changed += Changed;
            _ = Observe(created);
        }
        catch (Exception error)
        {
            if (created is not null && !ReferenceEquals(created,session)) await Join(created,ArrivalStopReason.Cancelled);
            if (!source.IsCancellationRequested && desired())
            {
                // No folder yet: wait for Windows to create it (with the next Windows+Print Screen), say so once.
                if (error is UserError { Message: ScreenshotCards.NoFolder } && folderReady is not null) { if (!waitingForFolder) report(ScreenshotCards.NoFolder); waitingForFolder = true; }
                else { halted = true; report(error is UserError user ? user.Message : error.Message); }
            }
        }
        finally { starting = null; }
    }

    private async Task StopAsync(IImageArrivalSession active,ArrivalStopReason reason)
    {
        // Forget it first: its own completion must not be mistaken for a stop by itself.
        active.Changed -= Changed;
        if (ReferenceEquals(session,active)) { session = null; shown = Guid.Empty; }
        hide();
        await Join(active,reason);
    }

    private static async Task Join(IImageArrivalSession source,ArrivalStopReason reason)
    {
        try { await source.StopAsync(reason); } catch { }
        // A stop error must not abandon the reader or the watcher's cleanup.
        try { await source.Completion; } catch { }
    }

    private async Task Observe(IImageArrivalSession active)
    {
        try { await active.Completion; } catch { }
        if (!ReferenceEquals(session,active)) return; // Stopped on purpose; StopAsync tidies up.
        active.Changed -= Changed;
        var snapshot = active.Snapshot;
        session = null; shown = Guid.Empty;
        // A long-running watcher reaches the session budgets in ordinary use: start afresh (there is
        // no initial scan, so nothing is replayed), but at most once a minute so a flood stays stopped.
        var now = DateTime.UtcNow;
        if (ScreenshotCards.Renewable(snapshot.StopReason) && desired() && (lastRenewal is null || now - lastRenewal.Value >= TimeSpan.FromMinutes(1)))
        { lastRenewal = now; _ = Update(); return; }
        halted = true;
        report(ScreenshotCards.Reportable(snapshot.StopReason) ? Reason(snapshot.StopReason) : "Screenshot cards are not running. Turn the setting off and on to try again.");
    }

    private void Changed()
    {
        // Raised on the reader's thread; cards appear on the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        try { dispatcher.BeginInvoke(new Action(Refresh)); } catch (InvalidOperationException) { }
    }

    internal void Refresh()
    {
        if (session is not { } active || active.Snapshot is not { Status: ArrivalStatus.Running } snapshot || snapshot.Cards.Count == 0) return;
        var newest = snapshot.Cards[^1];
        if (newest.Id == shown) return;
        // One card at a time: a newer screenshot replaces the one waiting.
        foreach (var older in snapshot.Cards.Take(snapshot.Cards.Count - 1)) active.Dismiss(older.Id);
        shown = newest.Id;
        show(newest);
    }

    internal static string Reason(ArrivalStopReason reason) => reason switch
    {
        ArrivalStopReason.CardLimit or ArrivalStopReason.QueueLimit or ArrivalStopReason.NotificationLimit or ArrivalStopReason.ByteLimit =>
            "Screenshot cards stopped at a session limit. Turn the setting off and on to watch again.",
        ArrivalStopReason.WatcherError => "Screenshot cards stopped: folder notifications failed. Turn the setting off and on to watch again.",
        ArrivalStopReason.CleanupError => ImageArrivalRules.CleanupFailed,
        _ => "Screenshot cards stopped: an image could not be read. Turn the setting off and on to watch again."
    };

    /// The folder Windows reports now exists (never created here).
    internal static bool NativeFolderReady() => ScreenshotFolder.Resolve(CancellationToken.None).Path is not null;
    /// The session still watches the folder Windows reports now: not renamed, moved or relocated.
    internal static bool NativeStillValid(IImageArrivalSession active) =>
        active is ImageArrivalSession real && ScreenshotFolder.Resolve(CancellationToken.None).Path is { } now && real.StillAt(now);

    /// The real session: resolve the folder, then the existing arrival watcher on it (no initial scan).
    internal static Task<IImageArrivalSession> Native(CancellationToken token) => Task.Run<IImageArrivalSession>(() =>
    {
        var location = ScreenshotFolder.Resolve(token);
        if (location.Path is not { } path) throw new UserError(location.Issue ?? ScreenshotCards.Unreported);
        // Always on once enabled: never stop the person renaming or moving Screenshots or Pictures.
        try { return ImageArrivalSession.Create(path,token,shareDelete: true); }
        catch (UserError error) when (error.Message == NativeFiles.LocalOnly) { throw new UserError(NotLocal); }
    },token);
    internal const string NotLocal = "The Screenshots folder is not a plain folder on this PC (for example, it is kept in OneDrive), so screenshot cards cannot watch it.";
}
