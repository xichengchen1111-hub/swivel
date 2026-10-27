using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace Swivel;

// The QR Library's data: saved codes, their look, the PNG export and the store (a port of QRLibrary.swift).

enum QrSeason { Spring, Summer, Autumn }

static class QrSeasons
{
    public static readonly QrSeason[] All = [QrSeason.Spring, QrSeason.Summer, QrSeason.Autumn];

    public static string Title(this QrSeason season) => season switch
    {
        QrSeason.Spring => L("Spring"),
        QrSeason.Summer => L("Summer"),
        _ => L("Autumn"),
    };

    /// The raw value saved in the library file (the Mac app's enum raw values).
    public static string Key(this QrSeason season) => season switch
    {
        QrSeason.Spring => "spring",
        QrSeason.Summer => "summer",
        _ => "autumn",
    };

    public static QrSeason? Parse(string? key) => All.Cast<QrSeason?>().FirstOrDefault(s => s!.Value.Key() == key);
}

/// One explicitly saved code. `Id` and `Name` are what the Codes menu shows; the rest is the content and its scene.
sealed record SavedQrCode(Guid Id, string Name)
{
    public string Value { get; init; } = "";
    public string Collection { get; init; } = "";
    public QrSeason Season { get; init; } = QrSeason.Summer;
    public QrTemplate Template { get; init; } = QrTemplate.Tree;
    public DateTime Created { get; init; } = DateTime.UtcNow;

    public static SavedQrCode New(string name, string value, string collection = "", QrSeason season = QrSeason.Summer,
                                  QrTemplate template = QrTemplate.Tree) =>
        new(Guid.NewGuid(), name) { Value = value, Collection = collection, Season = season, Template = template };

    /// Swift counts characters (grapheme clusters); so does this.
    static int Characters(string text) => new StringInfo(text).LengthInTextElements;

    public bool IsValid =>
        Name.Trim().Length > 0 && Characters(Name) <= 120 &&
        Value.Length > 0 && Encoding.UTF8.GetByteCount(Value) <= QrImage.MaxBytes && Characters(Collection) <= 80;
}

/// The plain, scannable QR picture of a saved code.
static class QrImage
{
    public const int MaxBytes = 2000;

    /// The code's modules (no quiet zone), at error correction M like the Mac's CIQRCodeGenerator setting.
    public static QrMatrix Raw(string value)
    {
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > MaxBytes) throw new ToolError(L("Enter up to 2,000 bytes of QR content."));
        try { return QrCodec.Encode(value, QrErrorCorrection.M); }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("This content is too long for a QR code.")); }
    }

    /// Integer scaling and a four-module white quiet zone keep the exported QR reliably scannable.
    public static BitmapSource Bitmap(string value, int scale = 8)
    {
        var matrix = Raw(value);
        try
        {
            var image = QrCodec.Render(matrix, scale, 4);
            if (image.CanFreeze && !image.IsFrozen) image.Freeze();
            return image;
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Could not generate the QR image.")); }
    }

    public static byte[] Png(string value)
    {
        var image = Bitmap(value);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Could not generate the QR image.")); }
    }

    /// Writes a PNG without ever replacing an existing file (even when the Save dialog offered to replace it): a temp
    /// file in the same folder is moved into place only if the name is still free.
    public static void Export(string value, string path)
    {
        var data = Png(value);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) throw new ToolError(L("Couldn't save %@", Path.GetFileName(path)));
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) { throw new ToolError(L("Couldn't save %@", Path.GetFileName(path))); }
        catch (UnauthorizedAccessException) { throw new ToolError(L("Couldn't save %@", Path.GetFileName(path))); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// A file name for the export: the code's name without characters Windows doesn't allow.
    public static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return (clean.Length == 0 ? "QR" : clean) + ".png";
    }
}

/// Only explicitly saved content is retained. Originals are not needed after a QR is imported.
///
/// The library is `%APPDATA%\Swivel\QRCodes\library-v1.json` (the Mac app's Application Support folder). The folder is
/// inside the user's profile, whose permissions already keep other accounts out, so the Mac's 0600/0700 modes have no
/// separate equivalent here.
sealed class QrLibraryStore
{
    /// SWIVEL_QR_LIBRARY_DIR for isolated previews and tests. Never changes the production library.
    public static string? PreviewDirectory { get; set; }

    /// Any library changed (the Codes menu refreshes).
    public static event Action? Changed;

    /// The file that changed, so each library window refreshes only for its own library.
    public static event Action<string>? FileChanged;

    public string FilePath { get; }

    public QrLibraryStore(string? directory = null)
    {
        var root = directory ?? PreviewDirectory ??
                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swivel", "QRCodes");
        FilePath = Path.GetFullPath(Path.Combine(root, "library-v1.json"));
    }

    static ToolError Unreadable() => new(L("The QR library could not be read. Its saved data was kept."));

    public IReadOnlyList<SavedQrCode> Load()
    {
        if (!File.Exists(FilePath)) return [];
        string text;
        try { text = File.ReadAllText(FilePath, Encoding.UTF8); }
        catch (IOException) { throw Unreadable(); }
        catch (UnauthorizedAccessException) { throw Unreadable(); }
        List<SavedQrCode> values;
        try { values = Decode(text); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException) { throw Unreadable(); }
        if (!values.All(v => v.IsValid) || values.Select(v => v.Id).Distinct().Count() != values.Count) throw Unreadable();
        return values;
    }

    public void Save(IReadOnlyList<SavedQrCode> values)
    {
        if (!values.All(v => v.IsValid) || values.Select(v => v.Id).Distinct().Count() != values.Count)
            throw new ToolError(L("Check the code name and content."));
        var data = Encoding.UTF8.GetBytes(Encode(values));
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        // Atomic: a temp file in the same folder replaces the library in one move, so a failure never leaves half a file.
        var temp = Path.Combine(folder, "library-v1." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        FileChanged?.Invoke(FilePath);
        Changed?.Invoke();
    }

    /// Adds a code unless the same content is already saved in the same collection (then that one is returned).
    public SavedQrCode Add(SavedQrCode value)
    {
        var values = Load().ToList();
        if (values.FirstOrDefault(v => v.Value == value.Value && v.Collection == value.Collection) is { } existing) return existing;
        values.Insert(0, value);
        Save(values);
        return value;
    }

    /// Undo only this window's changes. Codes saved or edited by another library window remain intact.
    public void Revert(IReadOnlyList<SavedQrCode> before, IReadOnlyList<SavedQrCode> after)
    {
        var old = before.ToDictionary(v => v.Id);
        var @new = after.ToDictionary(v => v.Id);
        var affected = old.Keys.Union(@new.Keys)
            .Where(id => !Equals(old.GetValueOrDefault(id), @new.GetValueOrDefault(id)))
            .ToHashSet();
        var current = Load().ToList();
        foreach (var id in affected)
        {
            if (!Equals(current.FirstOrDefault(v => v.Id == id), @new.GetValueOrDefault(id)))
                throw new ToolError(L("This code changed in another window. Refresh before undoing."));
        }
        current.RemoveAll(v => affected.Contains(v.Id));
        current.AddRange(before.Where(v => affected.Contains(v.Id)));
        Save(current.OrderByDescending(v => v.Created).ToList());
    }

    // MARK: JSON (the Mac app's keys; dates as ISO 8601)

    static readonly DateTime MacReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static string Encode(IReadOnlyList<SavedQrCode> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(new JsonObject
            {
                ["id"] = v.Id.ToString().ToUpperInvariant(),
                ["name"] = v.Name,
                ["value"] = v.Value,
                ["collection"] = v.Collection,
                ["season"] = v.Season.Key(),
                ["template"] = v.Template.Key(),
                ["created"] = v.Created.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            });
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// Libraries saved before templates existed (or by a later version with new seasons or templates) still load:
    /// a missing or unknown season or template falls back to Summer / Tree, a missing collection to none.
    internal static List<SavedQrCode> Decode(string text)
    {
        var root = JsonNode.Parse(text) as JsonArray ?? throw new FormatException("not an array");
        var values = new List<SavedQrCode>();
        foreach (var node in root)
        {
            var item = node as JsonObject ?? throw new FormatException("not an object");
            var id = Guid.Parse(Required(item, "id"));
            var name = Required(item, "name");
            var value = Required(item, "value");
            var collection = Optional(item, "collection") ?? "";
            var season = QrSeasons.Parse(Optional(item, "season")) ?? QrSeason.Summer;
            var template = QrTemplates.Parse(Optional(item, "template")) ?? QrTemplate.Tree;
            values.Add(new SavedQrCode(id, name) { Value = value, Collection = collection, Season = season, Template = template, Created = Date(item["created"]) });
        }
        return values;
    }

    static string Required(JsonObject item, string key) =>
        item[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new FormatException(key);

    static string? Optional(JsonObject item, string key)
    {
        if (item[key] is null) return null;
        return item[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// ISO 8601, or seconds since 2001 (Foundation's default date encoding) for a file written by the Mac app.
    static DateTime Date(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                return date.ToUniversalTime();
            if (value.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && Math.Abs(seconds) < 1e10)
                return MacReferenceDate.AddSeconds(seconds);
        }
        if (node is null) return DateTime.UtcNow;
        throw new FormatException("created");
    }
}
