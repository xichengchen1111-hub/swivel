using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swivel.Core;

public enum Feature { Wheel, Shelf, QuickActions, KeepAwake, ImagePresets, BatchRename, Archives, SubtitleFiles, MyWheel, Doctor, AppRouting, Clothesline, PdfPages, QrCodes, TextCapture, Assistant, FileMoves, FolderStyle, AgentTools }

/// Windows preferences are separate from macOS. Only Wheel starts enabled.
public sealed class Prefs
{
    // Bound incoming JSON while allowing all supported QR, app and photo
    // entries together, including JSON-escaped text and longest finite numbers.
    public const int MaxJsonLength = 1048576;
    private readonly HashSet<Feature> enabled = new() { Feature.Wheel };
    private readonly List<ImagePreset> presets = new();
    public IReadOnlyList<ImagePreset> Presets => presets.AsReadOnly();
    private readonly List<QrEntry> qrCodes = new();
    public IReadOnlyList<QrEntry> QrCodes => qrCodes.AsReadOnly();
    public void SaveQr(QrEntry entry)
    {
        entry.Validate(); int index = qrCodes.FindIndex(x => x.Id == entry.Id);
        if (index < 0 && qrCodes.Count >= 32) throw new UserError("Save up to 32 QR codes.");
        if (qrCodes.Any(x => x.Id != entry.Id && x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase))) throw new UserError("Choose a different QR name.");
        if (index >= 0) qrCodes[index] = entry; else qrCodes.Add(entry); Changed?.Invoke();
    }
    public void RemoveQr(string id) { if (qrCodes.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke(); }
    public void ImportQr(IReadOnlyList<QrEntry> incoming)
    {
        var merged=QrLibrary.Merge(qrCodes,incoming);if(merged.Length==qrCodes.Count)return;
        qrCodes.Clear();qrCodes.AddRange(merged);Changed?.Invoke();
    }
    private readonly List<AppRoute> apps = new();
    public IReadOnlyList<AppRoute> Apps => apps.AsReadOnly();
    public void SaveApp(AppRoute route)
    {
        route.Validate(); int index = apps.FindIndex(x => x.Id == route.Id);
        if (index < 0 && apps.Count >= 16) throw new UserError("Save up to 16 application or web page actions.");
        if (apps.Any(x => x.Id != route.Id && x.Name.Equals(route.Name, StringComparison.OrdinalIgnoreCase))) throw new UserError("Choose a different action name.");
        route = route with { Extensions = string.Join(",", route.Types), WebAddress = route.IsWebPage ? WebAddress.Normalize(route.WebAddress!) : null };
        if (index >= 0) apps[index] = route; else apps.Add(route); Changed?.Invoke();
    }
    public void RemoveApp(string id)
    {
        if (apps.RemoveAll(x => x.Id == id) > 0) { customActions = customActions.Where(x => x != "app." + id).ToArray(); Changed?.Invoke(); }
    }
    private string[] customActions = Array.Empty<string>();
    public IReadOnlyList<string> CustomActionIds => Array.AsReadOnly(customActions);
    public ShortcutSet Shortcuts { get; private set; } = ShortcutSet.Default;
    public MenuTemplate MenuTemplate { get; private set; } = Core.MenuTemplate.Wheel;
    public bool AnimateMenus { get; private set; } = true;
    private readonly MenuPalette?[] palettes = new MenuPalette?[5];
    public MenuPalette? Palette(MenuTemplate template) => Enum.IsDefined(template) ? palettes[(int)template] : throw new ArgumentOutOfRangeException(nameof(template));
    public void SetPalette(MenuTemplate template,MenuPalette? palette)
    {
        if (!Enum.IsDefined(template)) throw new ArgumentOutOfRangeException(nameof(template));
        palette?.Validate(); palettes[(int)template] = palette; Changed?.Invoke();
    }
    public void SetMenuPresentation(MenuTemplate template, bool animate)
    {
        if (!Enum.IsDefined(template)) throw new ArgumentOutOfRangeException(nameof(template));
        MenuTemplate = template; AnimateMenus = animate; Changed?.Invoke();
    }
    public void SetCustomActions(IEnumerable<string> ids) { customActions = CustomActions.Validate(ids, Catalog.All(this)); Changed?.Invoke(); }
    public void SetShortcuts(ShortcutSet shortcuts) { shortcuts.Validate(); Shortcuts = shortcuts; Changed?.Invoke(); }
    public string Language { get; private set; } = "system";
    public AssistantSettings? Assistant { get; private set; }
    public void SaveAssistant(AssistantSettings settings){Assistant=settings.Normalize();Changed?.Invoke();}
    public event Action? Changed;
    public bool IsOn(Feature feature) => enabled.Contains(feature);
    public void Set(Feature feature, bool value)
    {
        if (value) enabled.Add(feature); else enabled.Remove(feature);
        Changed?.Invoke();
    }
    public void SetLanguage(string language)
    {
        if (!Languages.Contains(language)) throw new ArgumentException(nameof(language));
        Language = language;
        Changed?.Invoke();
    }
    public static readonly string[] Languages = { "system", "en", "zh-Hans", "zh-Hant", "ja" };
    public void SavePreset(ImagePreset preset)
    {
        preset.Validate();
        int index = presets.FindIndex(x => x.Id == preset.Id);
        if (index < 0 && presets.Count >= 24) throw new UserError("Save up to 24 image presets.");
        if (presets.Any(x => x.Id != preset.Id && x.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
            throw new UserError("Choose a different preset name.");
        if (index >= 0) presets[index] = preset; else presets.Add(preset);
        Changed?.Invoke();
    }
    public void ImportPresets(IReadOnlyList<ImagePreset> incoming)
    {
        var merged = PresetLibrary.Merge(presets, incoming);
        if (merged.Length == presets.Count) return;
        presets.Clear(); presets.AddRange(merged); Changed?.Invoke();
    }
    public void RemovePreset(string id)
    {
        if (presets.RemoveAll(x => x.Id == id) > 0) { customActions = customActions.Where(x => x != "preset." + id).ToArray(); Changed?.Invoke(); }
    }
    public string Serialize() => JsonSerializer.Serialize(new Saved(2, Language, enabled.Select(x => x.ToString()).Order().ToArray(), presets.ToArray(), customActions, Shortcuts, apps.ToArray(), MenuTemplate, AnimateMenus, qrCodes.ToArray(),palettes.ToArray(),Assistant));
    public static Prefs Parse(string json)
    {
        if (json.Length > MaxJsonLength) throw new FormatException("Invalid preferences.");
        var saved = JsonSerializer.Deserialize<Saved>(json) ?? throw new FormatException("Invalid preferences.");
        if (saved.Version is not 1 and not 2 || saved.Enabled is null || saved.Enabled.Length > Enum.GetValues<Feature>().Length || !Languages.Contains(saved.Language) ||
            (saved.Version == 2 && saved.Presets is null) || saved.Presets?.Length > 24)
            throw new FormatException("Invalid preferences.");
        var prefs = new Prefs();
        prefs.enabled.Clear();
        foreach (var name in saved.Enabled)
        {
            if (!Enum.TryParse<Feature>(name, out var feature) || !Enum.IsDefined(feature) || feature.ToString() != name)
                throw new FormatException("Invalid preferences.");
            prefs.enabled.Add(feature);
        }
        prefs.Language = saved.Language;
        if(saved.Assistant is not null)prefs.SaveAssistant(saved.Assistant);
        if (saved.Version == 2)
            foreach (var preset in saved.Presets!)
            {
                if (preset is null || prefs.presets.Any(x => x.Id == preset.Id)) throw new FormatException("Invalid preferences.");
                prefs.SavePreset(preset);
            }
        if (saved.Apps is not null)
        {
            if (saved.Apps.Length > 16) throw new FormatException("Invalid applications.");
            foreach (var route in saved.Apps)
            {
                if (route is null || prefs.apps.Any(x => x.Id == route.Id)) throw new FormatException("Invalid applications.");
                prefs.SaveApp(route);
            }
        }
        if (saved.QrCodes is not null)
        {
            if (saved.QrCodes.Length > 32) throw new FormatException("Invalid QR library.");
            foreach (var entry in saved.QrCodes)
            {
                if (entry is null || prefs.qrCodes.Any(x => x.Id == entry.Id)) throw new FormatException("Invalid QR library.");
                prefs.SaveQr(entry);
            }
        }
        if (saved.CustomActions is not null) prefs.SetCustomActions(saved.CustomActions);
        if (saved.Shortcuts is not null) prefs.SetShortcuts(saved.Shortcuts);
        prefs.SetMenuPresentation(saved.Template ?? Core.MenuTemplate.Wheel, saved.AnimateMenus ?? true);
        if (saved.Palettes is not null)
        {
            if (saved.Palettes.Length != 5) throw new FormatException("Invalid menu palettes.");
            for (int i = 0; i < 5; i++) prefs.SetPalette((MenuTemplate)i,saved.Palettes[i]);
        }
        return prefs;
    }
    public sealed record Saved(int Version, string Language, string[] Enabled, ImagePreset[]? Presets = null, string[]? CustomActions = null, ShortcutSet? Shortcuts = null, AppRoute[]? Apps = null, MenuTemplate? Template = null, bool? AnimateMenus = null, QrEntry[]? QrCodes = null, MenuPalette?[]? Palettes = null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AssistantSettings? Assistant=null);
}

public sealed record ToolAction(string Id, string Title, Feature? Feature = null, bool ImageOnly = false, bool SingleFile = false, bool SrtOnly = false, string[]? Extensions = null, bool RequiresFiles = true, bool WebPage = false);

public static class Catalog
{
    // IDs are stable English values, translated only for display.
    public static readonly ToolAction[] Actions = {
        new("convert.gif", "Convert to Static GIF", ImageOnly: true),
        new("convert.png", "Convert to PNG", ImageOnly: true),
        new("convert.jpg", "Convert to JPEG", ImageOnly: true),
        new("convert.bmp", "Convert to BMP", ImageOnly: true),
        new("convert.tiff", "Convert to TIFF", ImageOnly: true),
        new("convert.ico", "Convert to ICO", ImageOnly: true),
        new("convert.tga", "Convert to TGA", ImageOnly: true),
        new("convert.psd", "Convert to PSD", ImageOnly: true),
        new("image.gifpalette", "Reduce Colors to GIF…", ImageOnly: true, SingleFile: true),
        new("image.preview", "Preview Image", ImageOnly: true, SingleFile: true),
        new("image.resize", "Resize…", ImageOnly: true),
        new("image.rotate", "Rotate Right", ImageOnly: true),
        new("image.crop", "Crop…", ImageOnly: true, SingleFile: true),
        new("image.adjust", "Edit Photo…", ImageOnly: true, SingleFile: true),
        new("image.flip", "Flip Horizontal", ImageOnly: true),
        new("image.gray", "Grayscale", ImageOnly: true),
        new("image.removebg", "Remove Background", ImageOnly: true),
        new("image.blurbg", "Blur Background", ImageOnly: true),
        new("image.idphoto", "ID Photo…", ImageOnly: true, SingleFile: true),
        new("image.upscale", "Upscale…", ImageOnly: true),
        new("image.retouch", "Retouch", ImageOnly: true, SingleFile: true),
        new("image.redact", "Redact…", ImageOnly: true, SingleFile: true),
        new("image.annotate", "Annotate…", ImageOnly: true, SingleFile: true),
        new("image.collage", "Collage…", ImageOnly: true),
        new("image.background", "Add Background…", ImageOnly: true, SingleFile: true),
        new("image.metadata", "Inspect Metadata…", ImageOnly: true, SingleFile: true),
        new("image.pngmetadata", "Remove PNG Text & EXIF…", SingleFile: true, Extensions: new[] { "png" }),
        new("image.jpegmetadata", "Remove JPEG EXIF & Comments…", SingleFile: true, Extensions: new[] { "jpg", "jpeg" }),
        new("image.compress", "Compress JPEG…", ImageOnly: true),
        new("audio.join", "Join WAV Files…", Extensions: new[] { "wav" }),
        new("audio.music", "Add WAV Background Music…", SingleFile: true, Extensions: new[] { "wav" }),
        new("audio.convert", "Convert WAV Format…", SingleFile: true, Extensions: new[] { "wav" }),
        new("audio.speed", "Change WAV Speed…", SingleFile: true, Extensions: new[] { "wav" }),
        new("audio.exportaiff", "Export WAV as AIFF…", SingleFile: true, Extensions: new[] { "wav" }),
        new("audio.aiff", "Convert AIFF to WAV…", SingleFile: true, Extensions: new[] { "aif", "aiff", "aifc" }),
        new("text.ocr", "Recognize Text…", Feature.TextCapture, ImageOnly:true, SingleFile:true),
        new("pdf.raster", "Render PDF Pages…", Feature.PdfPages, Extensions:new[]{"pdf"}),
        new("audio.systemdecode", "Decode Audio with Windows…", SingleFile:true, Extensions:new[]{"wav","mp3","aac","flac"}),
        new("audio.transcribe", "Transcribe Speech…", SingleFile: true, Extensions: new[] { "wav", "mp3", "aac", "flac" }),
        new("video.trim", "Trim Video…", SingleFile: true, Extensions: new[] { "mp4", "mov", "m4v", "wmv" }),
        new("video.subtitles", "Burn In Subtitles…", SingleFile: true, Extensions: new[] { "mp4", "mov", "m4v", "wmv" }),
        new("audio.g711", "Decode G.711 WAV…", Extensions: new[]{"wav"}, SingleFile: true),
        new("audio.floatpcm", "Convert Float WAV to PCM…", SingleFile: true, Extensions: new[] { "wav" }),
        new("audio.noise", "Reduce Audio Noise…", SingleFile: true, Extensions: new[] { "wav", "mp3", "aac", "flac" }),
        new("audio.wav", "Edit WAV Audio…", SingleFile: true, Extensions: new[] { "wav" }),
        new("file.assistantdraft", "Prepare Assistant Draft…", Core.Feature.AppRouting),
        new("file.copy", "Copy Files"),
        new("file.move", "Move to Folder…", Core.Feature.FileMoves),
        new("file.copyto", "Copy to Folder…", Core.Feature.FileMoves),
        new("file.copyfolder", "Copy Folder to…", Core.Feature.FileMoves, RequiresFiles: false),
        new("file.paths", "Copy Path"),
        new("file.reveal", "Show in Explorer"),
        new("file.terminal", "Open Command Prompt Here…", SingleFile: true),
        new("folder.style", "Change Folder Style…", Core.Feature.FolderStyle, RequiresFiles: false),
        new("archive.zip", "Create ZIP…", Core.Feature.Archives),
        new("archive.zipfolder", "Compress Folder to ZIP…", Core.Feature.Archives, RequiresFiles: false),
        new("archive.tar", "Create TAR…", Core.Feature.Archives),
        new("archive.gzip", "Create GZip…", Core.Feature.Archives, SingleFile: true),
        new("archive.extract", "Extract Archive…", Core.Feature.Archives, SingleFile: true, Extensions: new[] { "zip", "7z", "tar", "tgz", "gz" }),
        new("subtitle.edit", "Edit SRT…", Core.Feature.SubtitleFiles, SingleFile: true, SrtOnly: true),
        new("subtitle.srt", "Convert Subtitles to SRT", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("subtitle.vtt", "Convert Subtitles to VTT", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("subtitle.ass", "Convert Subtitles to ASS", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("subtitle.lrc", "Convert Subtitles to LRC", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("subtitle.sbv", "Convert Subtitles to SBV", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("subtitle.txt", "Convert Subtitles to TXT", Core.Feature.SubtitleFiles, Extensions: SubtitleFormats.InputExtensions),
        new("data.json", "Convert Data to JSON", Extensions: DataFileFormats.InputExtensions),
        new("data.jsonl", "Convert Data to JSONL", Extensions: DataFileFormats.InputExtensions),
        new("data.plist", "Convert Data to PLIST", Extensions: DataFileFormats.InputExtensions),
        new("data.csv", "Convert Data to CSV", Extensions: DataFileFormats.InputExtensions),
        new("data.tsv", "Convert Data to TSV", Extensions: DataFileFormats.InputExtensions),
        new("data.yaml", "Convert Data to YAML", Extensions: DataFileFormats.InputExtensions),
        new("font.desktop", "Unpack Web Font to TTF/OTF", Extensions: new[] { "woff", "woff2" }),
        new("font.woff", "Convert Font to WOFF", Extensions: FontFileFormats.InputExtensions),
        new("font.woff2", "Convert Font to WOFF2", Extensions: FontFileFormats.InputExtensions),
        new("document.text", "Inspect Document Text…", SingleFile: true, Extensions: DocumentText.Extensions),
        new("file.preview", "Preview File", SingleFile: true, Extensions: DocumentText.Extensions.Append("wav").ToArray()),
        new("rename.files", "Batch Rename…", Core.Feature.BatchRename),
        new("shelf.add", "Add to File Shelf", Core.Feature.Shelf),
        new("clothesline.add", "Hang on Clothesline", Core.Feature.Clothesline, ImageOnly: true)
        ,new("wheel.gesture", "Wheel Drag Gesture…", Core.Feature.Wheel, RequiresFiles: false),
        new("clothesline.watch", "Image Arrival Cards…", Core.Feature.Clothesline, RequiresFiles: false)
        ,new("pdf.images", "Images to PDF…", Core.Feature.PdfPages, ImageOnly: true)
        ,new("presets.import", "Import Image Presets…", Core.Feature.ImagePresets, RequiresFiles: false)
        ,new("presets.export", "Export Image Presets…", Core.Feature.ImagePresets, RequiresFiles: false)
        ,new("qr.create", "Create QR Code", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.readflat", "Read Flat QR Image…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.recoverflat", "Recover Flat QR Text…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.import", "Import QR Library…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.importMac", "Import Mac QR Library…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.export", "Export QR Library…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("qr.exportMacImages", "Export QR Images for Mac…", Core.Feature.QrCodes, RequiresFiles: false)
        ,new("doctor.startupfolders", "Inspect Startup Folder Entries…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.commonduplicates", "Compare Common Folders…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.closewindow", "Request Window Close…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.commontextsimilar", "Compare Text in Common Folders…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.commonphotosimilar", "Compare Images in Common Folders…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.commonfolders", "Measure Common Folders…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.scheduledtasks", "Inspect Scheduled Task Metadata…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.services", "Inspect Windows Services…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.startupapproved", "Inspect StartupApproved Records…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.sleepstates", "Inspect Sleep Capabilities…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.caches", "Inspect User Cache Locations…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.appcaches", "Review Browser and App Caches…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.power", "Inspect Power & Sleep Settings…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.drives", "Inspect Mounted Drives…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.folder", "Measure Folder Sizes…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.photosimilar", "Find Similar Images…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.textsimilar", "Find Similar Text Files…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.duplicates", "Find Duplicate Files…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.projects", "Find Project Build Folders…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.largefiles", "Find Large Files…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.appmemory", "Inspect App Memory…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.startup", "Inspect Logon Startup Records…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.executable", "Inspect EXE Structure…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.installedapps", "Inspect Desktop Installation Records…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.monitor", "Monitor CPU and Memory…", Core.Feature.Doctor, RequiresFiles: false)
        ,new("doctor.cleanup", "Clean Up Files…", Core.Feature.Doctor, RequiresFiles: false)
    };
    public static bool IsImage(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".tga" or ".psd";
    public static IEnumerable<ToolAction> All(Prefs prefs) => Actions.Concat(prefs.Presets.Select(x => new ToolAction("preset." + x.Id, x.Name, Feature.ImagePresets, true))).Concat(prefs.Apps.Select(x => new ToolAction("app." + x.Id, x.Name, Feature.AppRouting, Extensions: x.IsWebPage && !x.CopyToClipboard ? null : x.Types, RequiresFiles: !x.IsWebPage || x.CopyToClipboard, WebPage: x.IsWebPage)));
    public static IEnumerable<ToolAction> Offered(Prefs prefs) => All(prefs).Where(x => x.Feature is null || prefs.IsOn(x.Feature.Value));
    public static IEnumerable<ToolAction> Available(Prefs prefs, IReadOnlyCollection<string> files) =>
        Offered(prefs).Where(x => (x.Id != "audio.join" || files.Count is >= 2 and <= WaveJoin.MaxInputs) && (SubtitleFormats.ForId(x.Id) is not { } subtitle || !files.All(f => System.IO.Path.GetExtension(f).TrimStart('.').Equals(subtitle.Extension, StringComparison.OrdinalIgnoreCase))) && (DataFileFormats.ForId(x.Id) is not { } data || !files.All(f => DataFileFormats.IsSame(f, data))) && (FontFileFormats.ForId(x.Id) is not { } font || font == FontFileFormats.Desktop || !files.All(f => FontFileFormats.ExtensionOf(f) == font.Id["font.".Length..])) && (!x.RequiresFiles || files.Count > 0) && (!x.SingleFile || files.Count == 1) && (!x.ImageOnly || files.All(IsImage)) && (!x.SrtOnly || files.All(f => System.IO.Path.GetExtension(f).Equals(".srt", StringComparison.OrdinalIgnoreCase))) && (x.Extensions is null || x.Extensions.Length == 0 || files.All(f => x.Extensions.Contains(System.IO.Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))));
    public static IEnumerable<ToolAction> ForCustomWheel(Prefs prefs, IReadOnlyCollection<string> files)
    {
        if (!prefs.IsOn(Feature.MyWheel)) return Array.Empty<ToolAction>();
        var offered = (files.Count == 0 ? Offered(prefs) : Available(prefs, files)).ToDictionary(x => x.Id);
        return prefs.CustomActionIds.Where(offered.ContainsKey).Select(x => offered[x]).ToArray();
    }
    public static string DisplayTitle(ToolAction action, string language) => action.Id.StartsWith("preset.", StringComparison.Ordinal)
        ? string.Format(Strings.L("Preset: {0}", language), action.Title) : action.Id.StartsWith("app.", StringComparison.Ordinal) ? string.Format(Strings.L(action.WebPage ? "Open web page: {0}" : "Open with {0}", language),action.Title) : Strings.L(action.Title, language);
}

public static class ImageRules
{
    public const long MaxPixels = 16_000_000;
    public const long MaxInputBytes = 128 * 1024 * 1024;
    public static void Validate(int width, int height)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > MaxPixels)
            throw new UserError("Images are limited to 16 megapixels and 16384 pixels per side.");
    }
    public static (int Width, int Height) Fit(int width, int height, int edge)
    {
        Validate(width, height);
        if (edge is < 16 or > 8192) throw new UserError("Choose a long edge from 16 to 8192 pixels.");
        double scale = Math.Min(1, (double)edge / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

public sealed class UserError : Exception
{
    public UserError(string message) : base(message) { Template = message; }
    /// A message with {0}-style values: Template is what is translated, Message the English text.
    public UserError(string template, params object[] values) : base(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, values)) { Template = template; Values = values; }
    public string Template { get; }
    public object[] Values { get; } = Array.Empty<object>();
    /// The message in the user's language, given the app's translation function.
    public string Localized(Func<string, string> translate) =>
        Values.Length == 0 ? translate(Message) : string.Format(System.Globalization.CultureInfo.CurrentCulture, translate(Template), Values);
}

public sealed record CompatibilityItem(string Title, string Status, string Detail);
public static class Compatibility
{
    public static readonly CompatibilityItem[] Items = {
        new("Document Text", "Partial", "Inspect strict UTF-8/BOM UTF-16 text, bounded RTF or UTF-8 static HTML source body text, DOCX/ODT body XML and EPUB spine XHTML. Count whitespace segments and Unicode text elements; save plain TXT, RTF, DOCX, ODT or HTML beside the source. HTML requires explicit balanced markup and refuses scripts; browser rendering, formatting fidelity and full Office/PDF conversion remain pending."),
        new("Wheel", "Partial", "Five menu styles, paging, saved colors and gentle spring feedback. An explicitly started modifier-and-mouse gesture opens a nonactivating drop target, then Wheel after an accepted file drop. It does not detect file drags globally; focus, reliability and real Windows interaction remain unverified. Shortcuts and tray entry remain available."),
        new("Image Tools", "Partial", "SDR image editing and metadata-free PNG/JPEG/BMP/TIFF output. Static GIF export preserves normalized RGB only within 256 palette entries, including transparency; partial alpha or excess colors are refused without quantization. A separate Reduce Colors to GIF tool offers bounded lossy palette reduction with optional dithering, full-size preparation and a shared result for preview and saving; partial alpha remains refused. Full-canvas static GIF input supports palettes, binary transparency and interlacing; animation, special layouts and color-profile extensions are refused. JPEG supports quality or a measured size ceiling. Extended formats, HDR and Windows visual validation remain pending."),
        new("File Shelf", "Preview", "A floating shelf and main-window shelf share session references and copy drag. Explicit single-file preview supports existing SDR images, extracted document text and integer PCM WAV with manual playback. Shelf refresh never reads previews. Folder/virtual/cloud items, other formats and non-activating focus remain planned; real Windows acceptance is pending."),
        new("Quick Actions", "Preview", "Search supplied-file actions. The opt-in assistant shows complete requests and only selects a row for manual Return. Custom action labels need per-request permission. Doctor explains reviewed redacted basic/static snapshots or one frozen CPU/memory sample; selected reported labels need separate explicit consent and may contain private text. No automatic fixes, requests or copying. Monitoring stays paused until Resume. Real Windows/service acceptance remains pending."),
        new("Keep Awake", "Preview", "Timed system or display wake requests. Turning the feature off or quitting releases the request."),
        new("Clothesline", "Partial", "Manually hang up to 12 images, or explicitly start a folder arrival session. New or renamed image paths are read as they exist when processed; thumbnails are snapshots and notifications may be missed. No initial folder scan, screenshot takeover or clipboard reading. Screen-edge activation and full rope physics remain planned."),
        new("Text Capture", "Partial", "Windows OCR requires an installed signed MSIX identity and installed OCR languages. The portable ZIP is explicitly unavailable for this system feature. A default-off local image workflow recognizes only on request, then offers editable text and explicit copy; packaged runtime accuracy and UI acceptance remain pending."),
        new("JPEG Metadata", "Partial", "Remove JPEG APP1, APP13 and comments without recompressing supported baseline/progressive scans. Other data including ICC profiles and thumbnails remain and may contain private information. Orientation, color or display can change. APP11/non-ICC APP2 and unsupported coding are refused; this is not complete privacy or HDR/credential detection."),
        new("PNG Metadata", "Partial", "Remove PNG text, EXIF and time chunks without recompressing image data. Other chunks, color profiles and unknown ancillary data are retained and may contain private information. EXIF orientation removal may change display. Static SDR PNG only, up to 32 MiB; full privacy sanitization, selective tags and TIFF metadata editing remain pending. JPEG has a separate limited tool."),
        new("Image Presets", "Preview", "Back up and preview full Windows recipes before importing. Import compatible Mac preset JSON; HEIC, metadata retention and unsupported sizes are marked. Existing action IDs and feature switches are preserved."),
        new("Move Files", "Partial", "Explicitly move ordinary files within one NTFS volume after a full path preview. No overwriting; the same file objects retain attached data and original permissions. Failed moves attempt rollback, with exact recovery paths and up to 20 session Undo records checked against recorded identities and metadata. Content changes are not fully detected. Copy to Folder copies 1–100 files to a local NTFS folder with every data stream, the download warning, attributes and the modified time, never overwriting; each copy's stream names and sizes are checked against the original. Read-only files stay read-only and moving a hard link moves only that name. Moving across volumes, and moving folders or cloud links, remain unavailable; real Windows acceptance is pending."),
        new("Batch Rename", "Preview", "Preview names, rename without overwriting and undo after identity checks. NTFS regular files only. Case-only changes, swaps and chains use temporary names in the same folder; a crash may leave them behind. Read-only files stay read-only; renaming a hard link changes only that name. Other file systems remain unsupported."),
        new("Archives", "Partial", "Create ZIP, TAR or single-file GZip from selected regular files, up to 2 GB. Extract ordinary ZIP and ZIP64 (Store/Deflate), TAR (POSIX ustar/pax or GNU), .tar.gz/.tgz and single-file .gz archives, and 7z archives (LZMA, LZMA2, BCJ x86 or Copy; no encryption, split archives or other methods), up to 2 GB and 10,000 entries, into a new folder on NTFS. Links, devices and sparse files are refused; macOS ._ attribute files are left out. Compress Folder to ZIP archives one folder tree (up to 10,000 items, 32 levels, 2 GB), skipping links, cloud-only and system files. RAR extraction, other 7z methods and encryption are still planned."),
        new("Subtitles", "Preview", "Edit UTF-8 SRT (shift timing, change timing speed, export plain text), and convert between SRT, WebVTT, ASS/SSA, LRC and SBV or to plain text from UTF-8 or UTF-16 files. Each cue's text and timing are kept; styling, colours and positions are not. Transcription and burning subtitles into video are separate audio and video tools."),
        new("My Wheel", "Preview", "Choose and order stable action IDs, including presets and applications. Three configurable global shortcuts; disabled tools remain hidden."),
        new("Retouch", "Preview", "Local brush repair with per-stroke undo, cancellable preview and exclusive PNG output. Original unpainted SDR pixels are preserved. Real-photo quality and Windows performance still need validation."),
        new("PDF Pages", "Partial", "Image-only PDF creation remains available in the portable app. Existing PDF rendering requires an installed signed MSIX identity: bounded page selection, order, rotation and raster PDF/PNG copies are implemented behind that gate. Searchable text, vectors, forms, links, metadata and signatures are not preserved; packaged runtime acceptance remains pending."),
        new("QR & Barcodes", "Partial", "Create L/M/Q/H QR codes and keep 32 local entries with optional garden reliefs. Read strict flat QR images or explicitly try bounded data-codeword recovery; excess damage can yield different valid text. Function modules and borders must remain exact. Export compatible M-level PNGs for Mac image import; names, groups and scenes do not transfer. Other barcodes, perspective and broader image repair remain pending. 3D scanning and Windows visual acceptance are unverified."),
        new("App Actions", "Partial", "Choose trusted local EXE applications or saved HTTPS web pages, with explicit confirmation and optional file-reference copying for manual pasting. Only literal loopback HTTP is supported. Open an empty system Command Prompt in a file folder. Preview complete file-reference drafts for Codex or Claude Code, then explicitly copy or open them. File contents, workspace permissions and automatic submission are not supplied; protocol-handler acceptance still needs Windows validation."),
        new("Video & Audio", "Partial", "Existing integer PCM, float/G.711 and AIFF tools remain separate. An additional bounded Windows codec decoder prepares PCM16 from local WAV, FLAC, MP3 Layer III or ADTS AAC, with complete prepared-audio preview and exclusive save. Change WAV Speed can keep pitch (25–400%, original time-stretch; clicks and chords can show artefacts). Codec availability and runtime quality are unverified; other containers, video and speech classification remain pending."),
        new("AI Image Tools", "Planned", "Remove BG, subject blur, ID Photo masks and AI upscale need separately validated local Windows models or system APIs. Apple frameworks and the Core ML package cannot run directly."),
        new("Windows Doctor", "Partial", "Button-only bounded system, network, memory, installation, startup, folder, duplicate and similarity snapshots show unknowns and partial results. Monitoring pauses when hidden or minimized. Sensitive details stay hidden where stated. A separate confirmed request can ask one eligible window to close normally; saving, app exit and memory release are not guaranteed. Reliable app ownership, cleanup, undo, uninstalling and deeper repair remain planned."),
        new("Agent Tools (MCP)", "Partial", "With Agent Tools on, Swivel.exe --mcp serves local agents over stdin/stdout only (no port or network). Agents can inspect files, read document text, image text (Windows OCR, MSIX package only) and clean flat QR images, convert, rotate, flip or grayscale images or remove or blur their background, save a document's text as TXT, RTF, DOCX, HTML or ODT, compress images to JPEG or videos to MP4 under a size, rename files in their folders and move files into another folder on the same drive: every write is planned, applied once within an hour if no input changed, never overwrites anything, and is logged so undo can move unchanged outputs to the Recycle Bin, rename files back or move them back. The Mac's other agent tools are not offered yet."),
        new("AirDrop & Force Touch", "Platform Difference", "Apple AirDrop and Mac trackpad haptics have no direct Windows equivalent in this app."),
        new("Folder Style", "Partial", "Give 1–20 local folders a coloured Explorer icon with an optional two-character badge, through a hidden desktop.ini and icon file inside each folder; the folder becomes Read-only if it was not. Restore Default removes exactly those files. Folders with their own desktop.ini are left unchanged. Mac textures, pictures and emoji colours are not available; Explorer refresh timing is unverified."),
        new("Windows Validation", "Pending", "This preview must pass real Windows drag, focus, display scaling and file-system checks before release.")
    };
}
