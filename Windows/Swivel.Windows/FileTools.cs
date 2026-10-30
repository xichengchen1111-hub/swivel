using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class ArchiveWork : IDisposable
{
    private readonly List<NativeFiles.Input> inputs = new();
    private readonly ArchiveFormat format;
    private ArchiveSource[] sources = Array.Empty<ArchiveSource>();
    public long TotalBytes => sources.Sum(x => x.Stream.Length);
    public IReadOnlyList<string> Paths => inputs.Select(x => x.Path).ToArray();
    private ArchiveWork(ArchiveFormat format) { this.format = format; }
    public static ArchiveWork Open(IReadOnlyList<string> paths, ArchiveFormat format, CancellationToken token)
    {
        var work = new ArchiveWork(format);
        try
        {
            if (paths.Count is < 1 or > 100) throw new UserError("Choose 1–100 files for ZIP or TAR, or one file for GZip.");
            foreach (string path in paths) { token.ThrowIfCancellationRequested(); work.inputs.Add(NativeFiles.OpenRead(path)); }
            work.sources = work.inputs.Select(x => new ArchiveSource(Path.GetFileName(x.Path), x.Stream)).ToArray();
            Archives.Validate(work.sources, format); return work;
        }
        catch { work.Dispose(); throw; }
    }
    public string Save(CancellationToken token) => NativeFiles.WriteBeside(inputs[0], Path.GetFileNameWithoutExtension(inputs[0].Path) + " archive",
        format switch { ArchiveFormat.Zip => "zip", ArchiveFormat.Tar => "tar", _ => "gz" }, output => Archives.Create(sources, format, output, token), token);
    public void Dispose() { foreach (var input in inputs) input.Dispose(); inputs.Clear(); }
}

/// One chosen folder for Compress Folder to ZIP: the folder and its ancestors stay held from
/// reading the tree through writing, and every file is reopened through held, rechecked folders.
public sealed class FolderZipWork : IDisposable
{
    private readonly NativeFiles.MetadataDirectory root;
    public string Folder { get; }
    public FolderZipPlan Plan { get; }
    private FolderZipWork(string folder, NativeFiles.MetadataDirectory root, FolderZipPlan plan) { Folder = folder; this.root = root; Plan = plan; }
    public static FolderZipWork Open(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetDirectoryName(folder) is null) throw new UserError("Choose a folder, not a whole drive.");
        var root = NativeFiles.MetadataDirectory.Open(folder);
        try
        {
            // The ZIP's top folder and its own name come from this spelling, so it must be the stored name.
            if (!string.Equals(Path.GetFileName(NativeFiles.FinalDirectoryPath(folder)), Path.GetFileName(folder), StringComparison.Ordinal))
                throw new UserError("Use the folder's exact name as stored (same letter case, not a short 8.3 name).");
            return new(folder, root, FolderZip.Plan(root, token, folder.Length) with { Root = root.Identity });
        }
        catch { root.Dispose(); throw; }
    }
    public string Save(CancellationToken token, Action<int, int>? progress) =>
        NativeFiles.WriteInDirectory(Path.GetDirectoryName(Folder)!, Plan.Name, "zip", output => FolderZip.Write(Plan, item =>
        {
            try { return root.OpenDuplicate(new DuplicateCandidate(item.RelativePath, item.Length)); }
            catch (IOException) { throw new UserError(FolderZip.Changed); }
        }, output, token, progress), token);
    public void Dispose() => root.Dispose();
}

/// One held .zip: its handle (and folder ancestors) stay open from planning through
/// extraction, so the members reviewed are the members written.
public sealed class ZipExtractWork : IDisposable
{
    private readonly NativeFiles.Input input;
    public ZipPlan Plan { get; }
    public int Zone { get; }
    /// Null for ZIP; otherwise the TAR or gzip kind.
    public TarKind? Tar { get; }
    public string Path => input.Path;
    private readonly string stem;
    /// Non-null for 7z: where each reviewed member's data lives.
    private readonly SevenZipArchive? sevenZip;
    private ZipExtractWork(NativeFiles.Input input, ZipPlan plan, int zone, TarKind? tar, string stem, SevenZipArchive? sevenZip)
    { this.input = input; Plan = plan; Zone = zone; Tar = tar; this.stem = stem; this.sevenZip = sevenZip; }
    public static ZipExtractWork Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = System.IO.Path.GetFileName(path);
        string extension = System.IO.Path.GetExtension(path);
        bool zip = string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase), seven = string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase);
        TarKind? tar = zip || seven ? null : TarExtract.KindFor(name);
        if (!zip && !seven && tar is null) throw new UserError("Choose a .zip, .7z, .tar, .tar.gz, .tgz or .gz file to extract.");
        var input = NativeFiles.OpenRead(path);
        try
        {
            SevenZipArchive? sevenZip = seven ? SevenZipExtract.Read(input.Stream, token) : null;
            var plan = sevenZip?.Plan ?? (tar is TarKind kind ? TarExtract.Read(input.Stream, kind, name, token) : ZipExtract.Read(input.Stream, token));
            string stem = tar is TarKind folderKind ? TarExtract.FolderStem(name, folderKind) : ZipExtract.FolderStem(name);
            NativeFiles.CheckExtractionTarget(input, plan, stem);
            int zone = ZipExtract.TargetZone(NativeFiles.ReadZoneIdentifier(input));
            token.ThrowIfCancellationRequested();
            return new(input, plan, zone, tar, stem, sevenZip);
        }
        catch { input.Dispose(); throw; }
    }
    /// ZIP members that need a password (none for TAR and 7z).
    public IReadOnlyList<ZipMember> Encrypted => Plan.Files.Where(file => file.Encryption != ZipEncryption.None).ToArray();

    /// The quick check against the first encrypted member only (one key derivation), so the
    /// extract dialog can ask again. The held archive is used by one caller at a time.
    public bool PasswordMatchesFirst(ZipPassword password, CancellationToken token) =>
        Plan.Files.FirstOrDefault(file => file.Encryption != ZipEncryption.None) is not { } first || ZipExtract.PasswordMatches(input.Stream, first, password, token);

    /// Encrypted ZIP: every encrypted member's password check runs before the new folder
    /// exists. A mismatch on the first one refuses the whole extraction; later mismatches
    /// (archives using several passwords) are left out and listed in ZipOutcome.Refused.
    public ZipOutcome Extract(CancellationToken token, Action<int, int>? progress, ZipPassword password)
    {
        if (Tar is not null || sevenZip is not null || Encrypted.Count == 0) return Extract(token, progress);
        var refused = new HashSet<ZipMember>(ReferenceEqualityComparer.Instance);
        bool first = true;
        foreach (var member in Plan.Files)
        {
            token.ThrowIfCancellationRequested();
            if (member.Encryption == ZipEncryption.None) continue;
            bool matches = ZipExtract.PasswordMatches(input.Stream, member, password, token);
            if (!matches && first) throw new UserError("The password is not correct. Nothing was extracted.");
            if (!matches) refused.Add(member);
            first = false;
        }
        var plan = refused.Count == 0 ? Plan : new ZipPlan
        {
            Files = Plan.Files.Where(file => !refused.Contains(file)).ToArray(), Directories = Plan.Directories,
            TotalBytes = Plan.TotalBytes - refused.Sum(file => file.Size), LongestPath = Plan.LongestPath,
        };
        var outcome = NativeFiles.ExtractArchive(input, plan, stem, Zone, (member, output) => ZipExtract.Extract(input.Stream, member, output, token, password), token, progress);
        outcome.FileCount = Plan.Files.Count;
        outcome.Refused = Plan.Files.Where(refused.Contains).Select(file => file.Path).ToArray();
        return outcome;
    }

    public ZipOutcome Extract(CancellationToken token, Action<int, int>? progress)
    {
        if (sevenZip is null && Tar is null && Encrypted.Count > 0) throw new UserError("This ZIP entry is encrypted and needs a password.");
        if (sevenZip is not null)
        {
            using var reader = SevenZipExtract.OpenSource(input.Stream, sevenZip);
            return NativeFiles.ExtractArchive(input, Plan, stem, Zone, (member, output) => reader.Extract(member, output, token), token, progress);
        }
        if (Tar is not TarKind kind) return NativeFiles.ExtractZip(input, Plan, Zone, token, progress);
        using var source = TarExtract.OpenSource(input.Stream, kind, token);
        return NativeFiles.ExtractArchive(input, Plan, stem, Zone, (member, output) => source.Extract(member, output, token), token, progress);
    }
    public void Dispose() => input.Dispose();
}

public sealed class SubtitleFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public string Text { get; }
    private SubtitleFile(NativeFiles.Input input, string text) { this.input = input; Text = text; }
    public static SubtitleFile Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = NativeFiles.OpenRead(path);
        try
        {
            if (input.Stream.Length > SubRip.MaxBytes) throw new UserError("Subtitle files are limited to 2 MB.");
            byte[] bytes = new byte[checked((int)input.Stream.Length)]; input.Stream.ReadExactly(bytes);
            token.ThrowIfCancellationRequested(); string text = SubRip.Decode(bytes); SubRip.Parse(text);
            return new(input, text);
        }
        catch { input.Dispose(); throw; }
    }
    public string Save(string text, bool transcript, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var cues = SubRip.Parse(text);
        string output = transcript ? SubRip.Transcript(cues) : SubRip.Write(cues);
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        if (bytes.Length > SubRip.MaxBytes) throw new UserError("Subtitle files are limited to 2 MB.");
        return NativeFiles.WriteBeside(input, Path.GetFileNameWithoutExtension(input.Path) + (transcript ? " transcript" : " edited"), transcript ? "txt" : "srt", stream => stream.Write(bytes), token);
    }
    public void Dispose() => input.Dispose();
}
