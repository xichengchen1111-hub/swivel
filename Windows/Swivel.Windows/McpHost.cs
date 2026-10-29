using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

/// `Swivel.exe --mcp`: the agent server on stdin/stdout only (no port, no network, no window).
/// It runs on the STA main thread without starting the app, so it never shows settings,
/// tray or hotkeys and does not take the single-instance lock.
public sealed class McpHost : IMcpHost
{
    private const string Key = @"Software\Swivel\WindowsPreview";
    private readonly Func<bool>? enabled;
    private readonly string historyValue;
    /// Tests pass their own switch and history value, never the user's.
    internal McpHost(Func<bool>? enabled = null, string historyValue = "AgentHistory") { this.enabled = enabled; this.historyValue = historyValue; }

    public static int Run()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        if (input == Stream.Null || output == Stream.Null) return 2; // started without pipes, e.g. from a shortcut
        string version = typeof(McpHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        var session = new McpSession(new McpHost(), version);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 65536);
        using var writer = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        while (true)
        {
            string? line;
            try { line = ReadLine(reader); }
            catch (DecoderFallbackException) { writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Invalid UTF-8.\"}}"); return 1; }
            if (line is null) return 0; // end of input: the host closed the server
            if (line.Length == 0) continue;
            string? reply = session.Handle(line);
            if (reply is not null) writer.WriteLine(reply);
        }
    }

    /// A line with an upper bound, so one endless line cannot exhaust memory.
    private static string? ReadLine(StreamReader reader)
    {
        var line = new StringBuilder();
        while (true)
        {
            int c = reader.Read();
            if (c < 0) return line.Length == 0 ? null : line.ToString();
            if (c == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length > McpSession.MaxLineChars)
            {
                while ((c = reader.Read()) >= 0 && c != '\n') { } // discard the rest; Handle reports it too large
                return line.ToString();
            }
            line.Append((char)c);
        }
    }

    public bool Enabled => enabled?.Invoke() ?? (SettingsStore.Load(out bool failed) is var prefs && !failed && prefs.IsOn(Feature.AgentTools));
    public DateTime UtcNow => DateTime.UtcNow;
    public FileStamp Inspect(string path) => NativeFiles.InspectStamp(path);

    // The file is held, and checked against the plan before it is read, through publishing.
    public string Run(string action, string path, FileStamp expected, CancellationToken token)
    {
        var allowed = McpSession.Actions.FirstOrDefault(a => a.Id == action) ?? throw new McpToolError("Unknown action.");
        if (allowed.Font)
            return SubtitleConvert.Run(path, FontFileFormats.ForId(action) ?? throw new McpToolError("Unknown action."), token, expected);
        if (allowed.Data)
            return SubtitleConvert.Run(path, DataFileFormats.ForId(action) ?? throw new McpToolError("Unknown action."), token, expected);
        if (allowed.Subtitle)
            return SubtitleConvert.Run(path, SubtitleFormats.ForId(action) ?? throw new McpToolError("Unknown action."), token, expected);
        if (allowed.Document)
        {
            using var file = DocumentTextFile.Open(path, token, expected);
            return file.Save(allowed.Extension switch { "txt" => TextExport.Utf8, "rtf" => TextExport.Rtf, "docx" => TextExport.Docx, "html" => TextExport.Html, "odt" => TextExport.Odt, _ => throw new McpToolError("Unknown action.") }, token);
        }
        using var document = ImageTools.Open(path, token, expected);
        return ImageTools.Export(document, action, 0, token);
    }

    // On this STA thread, like the image actions; OCR itself awaits on the thread pool.
    public QrReadResult ReadCode(string path, CancellationToken token)
    {
        using var document = QrReadDocument.Open(path, token);
        return document.Result;
    }

    public IReadOnlyList<string> TextLanguages
    {
        get
        {
            if (!NativeIdentity.HasPackage) return Array.Empty<string>();
            try { return TextCaptureDocument.InstalledLanguages(); }
            catch (Exception error) when (error is COMException or InvalidCastException or TypeInitializationException) { return Array.Empty<string>(); }
        }
    }

    public string RecognizeText(string path, string? language, CancellationToken token)
    {
        using var document = TextCaptureDocument.Open(path, token);
        if (language is not null && !document.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            throw new McpToolError("The OCR language " + language + " is not installed in Windows. Installed: " + string.Join(", ", document.Languages) + ".");
        return Task.Run(() => document.RecognizeAsync(language, token), token).GetAwaiter().GetResult();
    }

    // Windows' media editing API awaits on the thread pool, as OCR does. Its file and clip
    // objects can keep the video open until they are collected, which would stop the user
    // renaming or moving it, so they are collected before returning.
    public VideoProbe ProbeVideo(string path, CancellationToken token)
    {
        try { return Task.Run(() => VideoCompress.ProbeAsync(path, token), token).GetAwaiter().GetResult(); }
        finally { Release(); }
    }

    // Images on this STA thread, like the image actions; videos on the thread pool.
    public string Compress(string path, long maxBytes, FileStamp expected, CancellationToken token)
    {
        if (Catalog.IsImage(path)) return ImageTools.CompressTo(path, maxBytes, expected, token);
        if (!VideoBudget.IsVideo(path)) throw new McpToolError("Only images and videos can be compressed to a size.");
        try { return Task.Run(() => VideoCompress.CompressAsync(path, maxBytes, expected, token), token).GetAwaiter().GetResult(); }
        finally { Release(); }
    }

    private static void Release() { GC.Collect(); GC.WaitForPendingFinalizers(); }

    public string ReadDocumentText(string path, CancellationToken token)
    {
        using var document = DocumentTextFile.Open(path, token);
        return document.Document.Text;
    }

    public bool NameTaken(string path) => NativeFiles.NameTaken(path);
    public string StoredName(string path) => NativeFiles.StoredName(path);

    // The same lease the rename takes, so its refusals (file system, a file another app holds open) show at planning.
    public void CheckRename(string path) { using var lease = NativeFiles.OpenRename(path); }

    // Leases pin each file and its folder from the stamp check through the last rename; while
    // held, nobody else can rename the file, so its stored name is checked once here.
    public RenameOutcome Rename(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, CancellationToken token)
    {
        using var session = RenameSession.Open(plan.Select(p => p.Source), token);
        if (session.Stamps.Where((stamp, i) => !stamp.SameObjectAndContent(expected[i])).Any()) throw new McpToolError("A file changed just now.");
        if (session.StoredNames.Where((name, i) => name != Path.GetFileName(plan[i].Source)).Any()) throw new McpToolError("A file's name changed just now.");
        return session.Execute(plan, token);
    }

    // The Move Files leases: the folder chain, and each file with its folder, held as the move holds them.
    public MoveIdentity CheckFolder(string folder)
    {
        try { using var lease = NativeFiles.MoveDirectoryLease.Open(folder, CancellationToken.None); return lease.Snapshot.LeafIdentity; }
        catch (UserError error) when (error.Message == NativeFiles.LocalOnly)
        { throw new McpToolError("Choose a plain folder on a local drive; files, links, junctions, network drives and cloud placeholders are not supported."); }
        catch (UserError error) { throw ForAgent(error); }
    }

    public bool CheckMove(string path, string folder)
    {
        try { using var session = MoveSession.Open(new[] { path }, folder, CancellationToken.None); return session.Plan[0].Changed; }
        catch (UserError error) { throw ForAgent(error); }
    }

    // Move Files speaks to someone looking at its preview window; an agent gets the facts.
    private static McpToolError ForAgent(Exception error) => new(error.Message switch
    {
        "Move Files does not accept read-only files or hard links." => "Swivel does not move read-only or hard-linked files.",
        "Move Files currently requires source and destination on the same NTFS volume. Nothing was copied or deleted." => "Swivel moves files only within one NTFS drive.",
        "Move Files requires complete Windows file identities." => "Swivel needs complete Windows file identities to move a file.",
        string message => message.Replace(" Create a new move preview.", "").Replace("; create a new preview.", "."),
    });

    // While the leases are held nobody else can rename, move or delete these files, so their
    // stamps and stored names are checked once here, then the Move Files transaction runs.
    public RenameOutcome Move(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, MoveIdentity? folder, CancellationToken token)
    {
        string destination = MoveRules.Parent(plan[0].Destination);
        if (plan.Any(p => MoveRules.Parent(p.Destination) != destination || MoveRules.FileName(p.Destination) != MoveRules.FileName(p.Source)))
            throw new ArgumentException("A move goes into one folder and keeps names.");
        MoveSession session;
        try { session = MoveSession.Open(plan.Select(p => p.Source).ToArray(), destination, token); }
        catch (UserError error) { throw ForAgent(error); }
        using (session)
        {
            if (folder is not null && session.DestinationIdentity != folder) throw new McpToolError("The destination folder was replaced since planning.");
            if (session.Plan.Where((pair, i) => pair.Destination != plan[i].Destination).Any()) throw new McpToolError("A file is already in that folder.");
            var stamps = session.Stamps;
            if (stamps.Where((stamp, i) => !stamp.SameObjectAndContent(expected[i])).Any()) throw new McpToolError("A file changed just now.");
            if (session.StoredNames.Where((name, i) => name != MoveRules.FileName(plan[i].Source)).Any()) throw new McpToolError("A file's name changed just now.");
            var outcome = session.Execute(token);
            return new RenameOutcome(outcome.Locations.Select((l, i) => new RenameLocation(l.Original, l.Current, stamps[i])).ToArray(),
                outcome.Error is UserError failed ? ForAgent(failed) : outcome.Error, outcome.Cancelled);
        }
    }

    public FolderZipPlan PlanFolderZip(string folder, CancellationToken token)
    {
        using var work = FolderZipWork.Open(folder, token);
        return work.Plan;
    }

    // Re-read through held folders and compared with the plan the agent saw; the same session
    // then writes the ZIP, so what was compared is what is compressed.
    public string ZipFolder(string folder, string fingerprint, CancellationToken token)
    {
        using var work = FolderZipWork.Open(folder, token);
        if (FolderZip.Fingerprint(work.Plan) != fingerprint) throw new McpToolError(McpSession.FolderChanged);
        return work.Save(token, null);
    }

    public FileStamp? Stamp(string path)
    {
        try { return NativeFiles.InspectStamp(path); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        // A folder there now: no file to compare (it still takes the name, which NameTaken reports).
        catch (Win32Exception error) when (error.NativeErrorCode == 5 && Directory.Exists(path)) { return null; }
    }

    public void Recycle(string path, FileStamp expected)
    {
        // The folder stays held until the file is gone, so it cannot be swapped for a junction
        // between the identity check and the Recycle Bin move.
        WindowsPathPolicy.Validate(path);
        using var parent = NativeFiles.DirectoryLease.Open(Path.GetDirectoryName(path)!);
        if (!NativeFiles.InspectStamp(path).SameObjectAndContent(expected)) throw new McpToolError("The file changed; it was left alone.");
        // Only fixed drives with a Recycle Bin; elsewhere Windows would delete permanently.
        if (!RecycleBin.Available(path)) throw new McpToolError("This drive has no Recycle Bin, so the file was left alone.");
        if (!RecycleBin.Move(path)) throw new McpToolError("Windows did not move the file to the Recycle Bin; it was left alone.");
        if (Stamp(path) is not null) throw new McpToolError("Windows did not move the file to the Recycle Bin; it was left alone.");
    }

    public IDisposable LockHistory()
    {
        var mutex = new Mutex(false, @"Local\Swivel.WindowsPreview.AgentHistory." + historyValue);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new McpToolError("Another Swivel agent server is busy with the history. Try again."); }
        catch (AbandonedMutexException) { } // the other server ended without releasing it; the history itself is still checked on load
        catch { mutex.Dispose(); throw; }
        return new Held(mutex);
    }
    private sealed class Held(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public string? LoadHistory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(historyValue) switch { null => null, string json => json, _ => "" };
    }

    public void SaveHistory(string json)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(historyValue, json, RegistryValueKind.String);
    }
}
