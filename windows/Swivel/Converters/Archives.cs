using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Swivel;

// Port of the Archives part of Sources/Swivel/Documents.swift. ditto / zip / tar become System.IO.Compression and
// System.Formats.Tar; formats .NET can't read (7z, rar, bz2, xz, cab…) go to Windows' own tar.exe (libarchive).
static partial class Archives
{
    public static List<WheelItem> CompressItems =>
    [
        new("ZIP", async files => Catalog.Saved([await Zip(files)])),
        new("TAR.GZ", async files => Catalog.Saved([await TarGz(files)])),
    ];

    /// One item → its name; several → "Archive"; next to the first item, never over an existing file.
    static string ArchivePath(IReadOnlyList<string> paths, string ext)
    {
        var first = Path.GetFullPath(paths[0]).TrimEnd('\\', '/');
        var dir = Path.GetDirectoryName(first) ?? ".";
        var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) : "Archive";
        return Paths.Unique(Path.Combine(dir, $"{name}.{ext}"));
    }

    /// Top-level entry names: each item's own name (folders keep their name as the root, like ditto --keepParent).
    static List<(string Path, string Name)> TopLevel(IReadOnlyList<string> paths)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw).TrimEnd('\\', '/');
            var name = Path.GetFileName(path);
            // Items with the same name from different folders get "name 2", like Paths.Unique.
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int n = 2; !used.Add(name); n++) name = $"{stem} {n}{ext}";
            list.Add((path, name));
        }
        return list;
    }

    public static Task<string> Zip(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        var output = ArchivePath(paths, "zip");
        WriteAtomically(output, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
            foreach (var (path, name) in TopLevel(paths)) AddToZip(zip, path, name);
        });
        return output;
    });

    static void AddToZip(ZipArchive zip, string path, string name)
    {
        if (Directory.Exists(path))
        {
            var entry = zip.CreateEntry(name + "/");
            try { entry.LastWriteTime = Directory.GetLastWriteTime(path); } catch (ArgumentOutOfRangeException) { }
            // Junctions and directory links aren't followed (they can loop or point anywhere).
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                AddToZip(zip, child, name + "/" + Path.GetFileName(child));
        }
        else
        {
            try { zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
        }
    }

    public static Task<string> TarGz(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        var output = ArchivePath(paths, "tar.gz");
        WriteAtomically(output, stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var (path, name) in TopLevel(paths)) AddToTar(tar, path, name);
        });
        return output;
    });

    static void AddToTar(TarWriter tar, string path, string name)
    {
        try { tar.WriteEntry(path, name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
        if (!Directory.Exists(path) || new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            AddToTar(tar, child, name + "/" + Path.GetFileName(child));
    }

    static void WriteAtomically(string output, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(output) ?? ".";
        var temp = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                try { File.SetAttributes(temp, FileAttributes.Hidden); } catch (IOException) { }
                write(stream);
            }
            File.Move(temp, output, overwrite: false);
            File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.Hidden);
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (e is ToolError) throw;
            if (e is IOException or UnauthorizedAccessException) throw new ToolError(L("Couldn't create the archive"));
            throw;
        }
    }

    // MARK: Extract

    /// Unpacks into a hidden ".swivel-<guid>" folder beside the archive, then moves each top-level item next to the
    /// archive under a free name (without an extra wrapping folder), skipping __MACOSX.
    public static Task<List<string>> Extract(string path) => Task.Run(async () =>
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full) ?? ".";
        var staging = Path.Combine(parent, ".swivel-" + Guid.NewGuid().ToString().ToUpperInvariant());
        try
        {
            Directory.CreateDirectory(staging);
            try { File.SetAttributes(staging, FileAttributes.Directory | FileAttributes.Hidden); } catch (IOException) { }
            await Unpack(full, staging);
            var results = new List<string>();
            foreach (var item in Directory.EnumerateFileSystemEntries(staging).Order(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (Path.GetFileName(item) == "__MACOSX") continue;
                var target = Paths.Unique(Path.Combine(parent, Path.GetFileName(item)));
                if (Directory.Exists(item)) Directory.Move(item, target);
                else File.Move(item, target);
                results.Add(target);
            }
            return results;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            throw new ToolError(L("Couldn't extract %@", name));
        }
        finally { DeleteTree(staging); }
    });

    static async Task Unpack(string path, string staging)
    {
        var lower = Path.GetFileName(path).ToLowerInvariant();
        var name = Path.GetFileName(path);
        try
        {
            if (lower.EndsWith(".zip")) { ExtractZip(path, staging); return; }
            if (lower.EndsWith(".tar")) { ExtractTar(File.OpenRead(path), staging, name); return; }
            if (lower.EndsWith(".tgz") || lower.EndsWith(".tar.gz")) { ExtractTar(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), staging, name); return; }
            if (lower.EndsWith(".gz")) { ExtractGz(path, staging, name); return; }
        }
        catch (InvalidDataException)
        {
            // Encrypted, Deflate64-damaged or mislabelled: give libarchive a try with a clean staging folder.
            foreach (var item in Directory.EnumerateFileSystemEntries(staging)) DeleteTree(item);
        }
        catch (NotSupportedException)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(staging)) DeleteTree(item);
        }
        await SystemTar(path, staging);
    }

    /// Windows 10 (1803+) ships bsdtar as tar.exe; its libarchive reads 7z, rar, bz2, xz, cab, iso and more. It
    /// refuses absolute paths and ".." on its own.
    static async Task SystemTar(string path, string staging)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var ext = FileKinds.Ext(path).ToUpperInvariant();
        if (!File.Exists(tar)) throw new ToolError(L("This PC can't open %@ archives", ext));
        try { await Shell.Run(tar, ["-xf", path, "-C", staging]); }
        catch (ToolError) { throw new ToolError(L("Couldn't extract %@", Path.GetFileName(path))); }
    }

    static void ExtractZip(string path, string staging)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Read, ZipNameEncoding(path));
        foreach (var entry in zip.Entries)
        {
            var target = SafeTarget(staging, entry.FullName, Path.GetFileName(path));
            if (target is null) continue;
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    static void ExtractTar(Stream stream, string staging, string name)
    {
        using var input = stream;
        using var reader = new TarReader(input);
        while (reader.GetNextEntry() is { } entry)
        {
            var target = SafeTarget(staging, entry.Name, name);
            if (target is null) continue;
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    break;
                // Symbolic and hard links are skipped: creating links needs Developer Mode or admin rights on
                // Windows, and a link can point outside the folder.
                default:
                    break;
            }
        }
    }

    /// A .gz is either a gzipped tar or one gzipped file (bsdtar on the Mac writes the latter as the bare name).
    static void ExtractGz(string path, string staging, string name)
    {
        var header = new byte[512];
        int read = 0;
        using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            while (read < header.Length && gz.Read(header, read, header.Length - read) is > 0 and var n) read += n;
        if (read == 512 && Encoding.ASCII.GetString(header, 257, 5) == "ustar")
        {
            ExtractTar(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), staging, name);
            return;
        }
        var target = SafeTarget(staging, Path.GetFileNameWithoutExtension(path), name) ?? Path.Combine(staging, "file");
        using var source = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var file = File.Create(target);
        source.CopyTo(file);
    }

    /// The staging path for an archive entry, with names Windows can't hold made safe. Throws on "..", absolute
    /// paths and anything else that would land outside the staging folder (zip-slip); null for empty names.
    internal static string? SafeTarget(string staging, string entryName, string archiveName)
    {
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        bool rooted = entryName.StartsWith('/') || entryName.StartsWith('\\') || (entryName.Length >= 2 && entryName[1] == ':');
        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == ".." || rooted) throw new ToolError(L("%@ contains files that would be saved outside its folder", archiveName));
            safe.Add(SafeSegment(segment));
        }
        if (safe.Count == 0) return null;
        var root = Path.GetFullPath(staging).TrimEnd('\\') + "\\";
        var target = Path.GetFullPath(Path.Combine([root, .. safe]));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ToolError(L("%@ contains files that would be saved outside its folder", archiveName));
        return target;
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// Characters Windows forbids in names (":" "*" "?" from Mac archives) become "_"; trailing dots and spaces go.
    static string SafeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0) name = "_";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();

    /// Entries without the UTF-8 flag: UTF-8 when their bytes are valid UTF-8 (macOS Archive Utility), else the PC's
    /// OEM code page (File Explorer's "Send to → Compressed folder" on Chinese / Japanese Windows writes GBK / Shift-JIS).
    static Encoding ZipNameEncoding(string path)
    {
        try
        {
            using var probe = ZipFile.Open(path, ZipArchiveMode.Read, Encoding.Latin1);
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            bool allUtf8 = true;
            foreach (var entry in probe.Entries)
            {
                var n = entry.FullName;
                if (n.Any(c => c > 0xFF)) continue;   // flagged UTF-8, already decoded
                if (n.All(c => c < 0x80)) continue;
                try { strict.GetString(Encoding.Latin1.GetBytes(n)); }
                catch (DecoderFallbackException) { allUtf8 = false; break; }
            }
            if (allUtf8) return Encoding.UTF8;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    /// Deletes a file or folder tree, clearing read-only flags first; never throws.
    static void DeleteTree(string path)
    {
        try
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
            if (!Directory.Exists(path)) return;
            var info = new DirectoryInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                foreach (var child in info.EnumerateFileSystemInfos()) DeleteTree(child.FullName);
            info.Attributes = FileAttributes.Directory;
            info.Delete(recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
