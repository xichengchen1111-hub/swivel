using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Swivel;

/// What an action reports back: a toast message and the files it saved (the toast can reveal them in Explorer).
sealed record ActionResult(string Message, IReadOnlyList<string>? SavedFiles = null)
{
    public IReadOnlyList<string> Files => SavedFiles ?? [];
}

/// One option on a menu. `Title` is the stable English ID saved in Customize; it is translated only when drawn.
sealed record WheelItem(string Title, Func<IReadOnlyList<string>, Task<ActionResult>> Run)
{
    public string DisplayTitle => Catalog.DisplayTitle(Title);
}

/// A titled per-file operation offered on a menu.
sealed record FileOption(string Title, Func<string, Task<List<string>>> Run);

/// A failure shown in the toast as-is.
class ToolError(string message) : Exception(message);

/// Thrown to report a result that isn't a file (shown in the toast as information, not an error).
sealed class Info(string message) : Exception(message);

/// Thrown when the user closes an editor or dialog without saving.
sealed class Cancelled() : Exception("Cancelled");

enum FileKind { Image, Video, Audio, Pdf, Document, Archive, Folder, Other }

static class FileKinds
{
    public static readonly FileKind[] All =
        [FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Archive, FileKind.Folder, FileKind.Other];

    public static string Id(this FileKind kind) => kind.ToString().ToLowerInvariant();

    public static string Title(this FileKind kind) => kind switch
    {
        FileKind.Image => L("Images"),
        FileKind.Video => L("Video"),
        FileKind.Audio => L("Audio"),
        FileKind.Pdf => L("PDF"),
        FileKind.Document => L("Documents"),
        FileKind.Archive => L("Archives"),
        FileKind.Folder => L("Folders"),
        _ => L("Other files"),
    };

    /// A Segoe Fluent Icons glyph for the kind.
    public static string Glyph(this FileKind kind) => kind switch
    {
        FileKind.Image => "\uEB9F",
        FileKind.Video => "\uE714",
        FileKind.Audio => "\uE8D6",
        FileKind.Pdf => "\uEA90",
        FileKind.Document => "\uE8A5",
        FileKind.Archive => "\uE7B8",
        FileKind.Folder => "\uE8B7",
        _ => "\uE7C3",
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "jpe", "jfif", "png", "heic", "heif", "hif", "tif", "tiff", "avif", "gif", "bmp", "dib", "webp", "ico",
          "jxr", "wdp", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf" };
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mov", "mp4", "m4v", "avi", "wmv", "mkv", "webm", "3gp", "3g2", "mpg", "mpeg", "ts", "mts", "m2ts" };
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "m4a", "wav", "aiff", "aif", "aifc", "flac", "aac", "caf", "wma", "ogg", "oga", "opus", "alac", "amr" };
    public static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "zip", "tar", "tgz", "gz", "bz2", "tbz", "tbz2", "xz", "txz", "7z", "rar", "cab" };

    /// A made-up file of this kind, for listing a menu's default contents.
    public static IReadOnlyList<string> SampleFiles(this FileKind kind)
    {
        string[] names = kind switch
        {
            FileKind.Image => ["a.jpg", "b.jpg"],
            FileKind.Video => ["a.mov", "b.mov"],
            FileKind.Audio => ["a.mp3", "b.mp3"],
            FileKind.Pdf => ["a.pdf", "b.pdf"],
            FileKind.Document => ["a.docx"],
            FileKind.Archive => ["a.zip"],
            _ => ["a.unknownext"],
        };
        return names.Select(n => $@"Z:\nonexistent\{n}").ToList();
    }

    public static string Ext(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind Of(string path)
    {
        var ext = Ext(path);
        if (ext is "md" or "markdown") return FileKind.Document;
        if (Directory.Exists(path)) return FileKind.Folder;
        if (ext == "pdf") return FileKind.Pdf;
        if (ImageExtensions.Contains(ext)) return FileKind.Image;
        if (VideoExtensions.Contains(ext)) return FileKind.Video;
        if (AudioExtensions.Contains(ext)) return FileKind.Audio;
        if (ArchiveExtensions.Contains(ext)) return FileKind.Archive;
        if (Docs.ReadableExtensions.Contains(ext)) return FileKind.Document;
        return FileKind.Other;
    }

    public static FileKind Of(IReadOnlyList<string> paths)
    {
        var kinds = paths.Select(Of).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : FileKind.Other;
    }
}

/// Decides which menu items a drag gets, per file kind and key.
static class Catalog
{
    public static string DisplayTitle(string id)
    {
        if (id == "Float") return L("Add to Shelf");
        if (id.StartsWith("preset.") && Prefs.ImagePresets.FirstOrDefault(p => p.ActionId == id) is { } preset)
            return preset.DisplayName;
        return L(id);
    }

    public static List<WheelItem> StandaloneItems(IReadOnlyList<string> paths, WheelMode mode) =>
        DefaultItems(paths, mode)
            .Where(i => i.Title is "Extract Text" or "Float" or "Hang" || i.Title.StartsWith("preset."))
            .ToList();

    /// The menu for a drag, in the user's order with hidden items removed.
    public static List<WheelItem> Items(IReadOnlyList<string> paths, WheelMode mode) =>
        Prefs.Arrange(DefaultItems(paths, mode), mode, FileKinds.Of(paths));

    /// Everything a menu can offer for a kind of file, used by the Customize settings.
    public static List<string> AllItems(WheelMode mode, FileKind kind)
    {
        var samples = kind.SampleFiles();
        var multi = DefaultItems(samples, mode).Select(i => i.Title).ToList();
        var single = DefaultItems(samples.Take(1).ToList(), mode).Select(i => i.Title);
        return [.. multi, .. single.Where(t => !multi.Contains(t))];
    }

    public static List<WheelItem> DefaultItems(IReadOnlyList<string> paths, WheelMode mode)
    {
        if (paths.Count == 0) return [];
        var kind = FileKinds.Of(paths);
        var sourceExt = paths.Select(FileKinds.Ext).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool multi = paths.Count > 1;

        switch (mode)
        {
            case WheelMode.Actions:
            {
                var list = Actions(multi);
                if (kind == FileKind.Image && Feature.Clothesline.IsOn())
                {
                    list.Insert(1, new WheelItem("Hang", files =>
                    {
                        if (!Feature.Clothesline.IsOn()) throw new ToolError(L("This feature is turned off."));
                        Clothesline.Shared.Hang(files);
                        return Task.FromResult(new ActionResult(files.Count == 1
                            ? L("Hung on the clothesline") : L("Hung %d images on the clothesline", files.Count)));
                    }));
                }
                return Feature.Shelf.IsOn() ? list : list.Where(i => i.Title != "Float").ToList();
            }

            case WheelMode.Convert:
                switch (kind)
                {
                    case FileKind.Image:
                    {
                        var list = Images.OutputFormats
                            .Where(f => multi || !f.Extensions.Any(sourceExt.Contains))
                            .Select(f => Each(f.Name, paths, p => Images.Convert(p, f)))
                            .ToList();
                        list.Add(new WheelItem("PDF", async files => Saved(await Images.MakePdf(files))));
                        return list;
                    }
                    case FileKind.Video:
                        return Video.ConvertItems(sourceExt).Select(o => Each(o.Title, paths, o.Run)).ToList();
                    case FileKind.Audio:
                        return Audio.Formats
                            .Where(f => !sourceExt.Contains(f.Ext) || f.Name == "ALAC")
                            .Select(f => Each(f.Name, paths, p => Audio.Convert(p, f)))
                            .ToList();
                    case FileKind.Pdf:
                        return Pdfs.ConvertItems.Select(o => Each(o.Title, paths, o.Run)).ToList();
                    case FileKind.Document:
                        return Docs.Formats
                            .Where(f => !sourceExt.Contains(f.Ext))
                            .Select(f => Each(f.Name, paths, p => Docs.Convert(p, f)))
                            .ToList();
                    case FileKind.Archive:
                        return [Each("Extract", paths, Archives.Extract)];
                    default:
                        return Archives.CompressItems;
                }

            default: // Tools
                switch (kind)
                {
                    case FileKind.Image:
                    {
                        List<WheelItem> list =
                        [
                            Each("Crop", paths, p => new CropEditor(p).PresentAsync()),
                            Each("Compress", paths, p => new CompressEditor(p).PresentAsync()),
                            Each("Edit", paths, p => new AdjustEditor(p).PresentAsync()),
                            Each("Add BG", paths, p => new BackgroundEditor(p).PresentAsync()),
                            Each("Redact", paths, p => new RedactEditor(p).PresentAsync()),
                            Each("Metadata", paths, p => new MetadataEditor(p).PresentAsync()),
                            Each("Annotate", paths, p => new AnnotateEditor(p).PresentAsync()),
                            .. Images.ToolItems.Select(o => Each(o.Title, paths, o.Run)),
                            new WheelItem("Make PDF", async files => Saved(await Images.MakePdf(files))),
                        ];
                        if (Feature.TextCapture.IsOn() && !multi)
                        {
                            list.Insert(0, new WheelItem("Extract Text", files =>
                            {
                                if (!Feature.TextCapture.IsOn() || files.Count == 0) throw new ToolError(L("This feature is turned off."));
                                TextCaptureWindow.Present(files[0]);
                                // The window opening is the feedback; no toast.
                                return Task.FromResult(new ActionResult(""));
                            }));
                        }
                        if (Feature.ImagePresets.IsOn())
                            list.AddRange(Prefs.ImagePresets.Select(preset =>
                                new WheelItem(preset.ActionId, files => PresetActions.Run(preset, files))));
                        if (multi)
                            list.Insert(0, new WheelItem("Collage", async files => Saved(await new CollageEditor(files).PresentAsync())));
                        return list;
                    }
                    case FileKind.Video:
                    {
                        List<WheelItem> list =
                        [
                            Each("Trim", paths, p => new TrimEditor(p).PresentAsync()),
                            .. Video.ToolItems.Select(o => Each(o.Title, paths, o.Run)),
                        ];
                        if (multi) list.Insert(0, new WheelItem("Merge", async files => Saved([await Video.Merge(files, audioOnly: false)])));
                        return list;
                    }
                    case FileKind.Audio:
                    {
                        List<WheelItem> list =
                        [
                            Each("Trim", paths, p => new TrimEditor(p).PresentAsync()),
                            .. Audio.ToolItems.Select(o => Each(o.Title, paths, o.Run)),
                        ];
                        if (multi) list.Insert(0, new WheelItem("Merge", async files => Saved([await Video.Merge(files, audioOnly: true)])));
                        return list;
                    }
                    case FileKind.Pdf:
                    {
                        List<WheelItem> list =
                        [
                            Each("Crop", paths, p => new CropEditor(p).PresentAsync()),
                            .. Pdfs.ToolItems.Select(o => Each(o.Title, paths, o.Run)),
                        ];
                        if (multi) list.Insert(0, new WheelItem("Merge", async files => Saved([await Pdfs.Merge(files)])));
                        return list;
                    }
                    case FileKind.Document:
                        return [Each("Word Count", paths, Docs.WordCount)];
                    default:
                        return Archives.CompressItems;
                }
        }
    }

    /// An item that runs `work` on each file in turn and collects the outputs.
    public static WheelItem Each(string title, IReadOnlyList<string> _, Func<string, Task<List<string>>> work) =>
        new(title, async files =>
        {
            var outputs = new List<string>();
            foreach (var file in files) outputs.AddRange(await work(file));
            return Saved(outputs);
        });

    public static ActionResult Saved(IReadOnlyList<string> files) => files.Count switch
    {
        0 => new ActionResult("Done"),
        1 => new ActionResult(L("Saved %@", System.IO.Path.GetFileName(files[0])), files),
        _ => new ActionResult(L("Saved %d files", files.Count), files),
    };

    static List<WheelItem> Actions(bool multi) =>
    [
        // Windows' share sheet (Nearby Sharing, Mail, Phone Link…) stands in for AirDrop.
        new("Share", files =>
        {
            FileActions.Share(files);
            return Task.FromResult(new ActionResult(L("Opened Share")));
        }),
        new("Float", files =>
        {
            if (!Feature.Shelf.IsOn()) throw new ToolError(L("This feature is turned off."));
            Shelf.Shared.Add(files);
            return Task.FromResult(new ActionResult(L("Added to Shelf")));
        }),
        new("Copy", files =>
        {
            FileActions.CopyFiles(files);
            return Task.FromResult(new ActionResult(multi
                ? L("Copied %d files", files.Count) : L("Copied %@", System.IO.Path.GetFileName(files[0]))));
        }),
        new("Move", files =>
        {
            var moved = FileActions.Move(files);
            if (moved.Count == 0) return Task.FromResult(new ActionResult(L("Move cancelled")));
            return Task.FromResult(new ActionResult(moved.Count == 1
                ? L("Moved %@", System.IO.Path.GetFileName(moved[0])) : L("Moved %d files", moved.Count), moved));
        }),
        new("Copy Path", files =>
        {
            FileActions.SetClipboardText(string.Join(Environment.NewLine, files));
            return Task.FromResult(new ActionResult(multi ? L("Copied %d paths", files.Count) : L("Copied path")));
        }),
        new("Terminal", files =>
        {
            FileActions.OpenTerminal(files[0]);
            return Task.FromResult(new ActionResult(L("Opened in Terminal")));
        }),
    ];
}

static class FileActions
{
    public static void CopyFiles(IReadOnlyList<string> files)
    {
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(files.ToArray());
        RetryClipboard(() => System.Windows.Clipboard.SetFileDropList(list));
    }

    public static void SetClipboardText(string text) => RetryClipboard(() => System.Windows.Clipboard.SetText(text));

    /// The clipboard is shared; another app holding it open makes a call fail briefly.
    public static void RetryClipboard(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (COMException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }

    /// Asks for a folder, then moves the files there under unique names.
    public static List<string> Move(IReadOnlyList<string> files)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = files.Count == 1
                ? L("Choose where to move %@", System.IO.Path.GetFileName(files[0]))
                : L("Choose where to move %d files", files.Count),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.FolderName)) return [];
        return files.Select(file =>
        {
            var destination = Paths.Unique(System.IO.Path.Combine(dialog.FolderName, System.IO.Path.GetFileName(file)));
            if (Directory.Exists(file)) Directory.Move(file, destination);
            else File.Move(file, destination);
            return destination;
        }).ToList();
    }

    /// Opens Windows Terminal (or PowerShell when it isn't installed) in the file's folder.
    public static void OpenTerminal(string path)
    {
        var folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }

    /// Shows the files in Explorer, selected.
    public static void Reveal(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (files.Count == 1 || files.Select(System.IO.Path.GetDirectoryName).Distinct().Count() > 1)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", files[0] }, UseShellExecute = false });
            return;
        }
        Native.RevealMany(files);
    }

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// Windows' share sheet for files (DataTransferManager), anchored to a hidden owner window.
    public static void Share(IReadOnlyList<string> files) => ShareSheet.Show(files);
}

static class Paths
{
    /// `folder\name.ext`, or `name 2.ext`, `name 3.ext`… if taken.
    public static string Output(string source, string ext, string suffix = "")
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')) + suffix;
        var dir = System.IO.Path.GetDirectoryName(source.TrimEnd('\\', '/')) ?? "";
        return Unique(System.IO.Path.Combine(dir, ext.Length == 0 ? name : $"{name}.{ext}"));
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var fileName = System.IO.Path.GetFileName(path);
        // "Archive.tar.gz" numbers as "Archive 2.tar.gz".
        string stem = fileName, ext = "";
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) { stem = fileName[..^7]; ext = fileName[^7..]; }
        else if (System.IO.Path.HasExtension(fileName)) { stem = System.IO.Path.GetFileNameWithoutExtension(fileName); ext = System.IO.Path.GetExtension(fileName); }
        for (int n = 2; ; n++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} {n}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}

static class Shell
{
    /// Runs a command-line tool and throws its stderr on failure.
    public static async Task Run(string tool, IEnumerable<string> args, string? directory = null)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (directory != null) info.WorkingDirectory = directory;
        using var process = Process.Start(info) ?? throw new ToolError($"{System.IO.Path.GetFileName(tool)} failed");
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var text = (await errors).Trim();
            var name = System.IO.Path.GetFileName(tool);
            throw new ToolError(text.Length == 0 ? $"{name} failed" : $"{name}: {text}");
        }
    }
}
