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

/// One held .zip: its handle (and folder ancestors) stay open from planning through
/// extraction, so the members reviewed are the members written.
public sealed class ZipExtractWork : IDisposable
{
    private readonly NativeFiles.Input input;
    public ZipPlan Plan { get; }
    public int Zone { get; }
    public string Path => input.Path;
    private ZipExtractWork(NativeFiles.Input input, ZipPlan plan, int zone) { this.input = input; Plan = plan; Zone = zone; }
    public static ZipExtractWork Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)) throw new UserError("Choose a .zip file to extract.");
        var input = NativeFiles.OpenRead(path);
        try
        {
            var plan = ZipExtract.Read(input.Stream, token);
            NativeFiles.CheckExtractionTarget(input, plan);
            int zone = ZipExtract.TargetZone(NativeFiles.ReadZoneIdentifier(input));
            token.ThrowIfCancellationRequested();
            return new(input, plan, zone);
        }
        catch { input.Dispose(); throw; }
    }
    public ZipOutcome Extract(CancellationToken token, Action<int, int>? progress) => NativeFiles.ExtractZip(input, Plan, Zone, token, progress);
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
