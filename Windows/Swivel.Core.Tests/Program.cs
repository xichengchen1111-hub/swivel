using System;
using System.IO;
using System.Linq;
using Swivel.Core;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
void Reject(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch { rejected = true; }
    Check(rejected, name);
}

var prefs = new Prefs();
Check(DoctorFacts.HasValidCapacity(0, 1) && DoctorFacts.HasValidCapacity(1, 1), "Valid drive capacity boundaries");
Check(!DoctorFacts.HasValidCapacity(0, 0) && !DoctorFacts.HasValidCapacity(2, 1), "Invalid drive capacity is unavailable");
Check(!DoctorFacts.LowSpace(2UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024), "Invalid drive capacity cannot report low space");
Check(prefs.IsOn(Feature.Wheel), "Wheel is initially enabled");
foreach (var feature in new[] { Feature.Shelf, Feature.QuickActions, Feature.KeepAwake, Feature.ImagePresets, Feature.FileMoves }) Check(!prefs.IsOn(feature), "Opt-in: " + feature);
int changes = 0; prefs.Changed += () => changes++;
prefs.Set(Feature.Wheel, false); prefs.Set(Feature.Shelf, true); prefs.SetLanguage("zh-Hans");
var restored = Prefs.Parse(prefs.Serialize());
Check(!restored.IsOn(Feature.Wheel) && restored.IsOn(Feature.Shelf) && !restored.IsOn(Feature.KeepAwake) && restored.Language == "zh-Hans", "Preference round trip");
Check(changes == 3, "Change notifications");
foreach (var json in new[] { "null", "{}", "[]", "{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"99\"]}", "{\"Version\":2,\"Language\":\"en\",\"Enabled\":[]}" })
    Reject(() => Prefs.Parse(json), "Reject invalid settings: " + json);
Reject(() => Prefs.Parse(new string('x', 131073)), "Bound preference data");
Reject(() => prefs.SetLanguage("xx"), "Reject unknown language");

string[] invalid = {
    @"relative.png", @"C:relative.png", @"\\server\share\a.png", @"\\?\C:\a.png", @"\\.\pipe\abc", @"C:\a.png:secret",
    @"C:\CON.jpg", @"C:\NUL", @"C:\COM1.jpg", @"C:\LPT¹.png", @"C:\CON .png", @"C:\CONIN$", @"C:\x.\a.png", @"C:\x \a.png",
    @"C:\..\a.png", @"C:\.\a.png", @"C:\\a.png", @"C:/a.png", "C:\\x\u202e.png", "C:\\a\0.png", @"C:\a|b.png", @"C:\", @"C:\a.png\"
};
foreach (string path in invalid) Reject(() => WindowsPathPolicy.Validate(path), "Reject path: " + path);
foreach (string path in new[] { @"C:\Photos\test.png", @"D:\照片\合照 🦜.jpg", @"E:\safe\COM10.png", @"C:\a-b\x.y.z" })
    Check(WindowsPathPolicy.Validate(path) == path, "Plain path unchanged");
Reject(() => WindowsPathPolicy.Validate(@"C:\" + new string('x', 241)), "Long path bound");
Check(WindowsPathPolicy.OutputName("photo converted", "png", 2) == "photo converted 2.png", "Exclusive collision naming");
Reject(() => WindowsPathPolicy.OutputName("bad/name", "png", 1), "Output path injection");
Reject(() => WindowsPathPolicy.OutputName("photo", "../x", 1), "Output extension injection");

Check(ImageRules.Fit(4000, 2000, 1920) == (1920, 960), "Landscape aspect");
Check(ImageRules.Fit(1000, 2000, 1920) == (960, 1920), "Portrait aspect");
Check(ImageRules.Fit(100, 50, 1920) == (100, 50), "No enlargement");
Check(ImageRules.Fit(1, 16384, 16) == (1, 16), "Thin image never zero");
Reject(() => ImageRules.Validate(100000, 100000), "Pixel overflow rejected");
Reject(() => ImageRules.Validate(4001, 4000), "16 MP limit");
Reject(() => ImageRules.Fit(10, 10, -1), "Negative resize rejected");

var off = new Prefs(); off.Set(Feature.Wheel, false);
Check(Catalog.Available(off, new[] { "photo.png" }).Any(x => x.Id == "convert.jpg"), "Image tools independent of Wheel");
Check(!Catalog.Available(off, new[] { "photo.png" }).Any(x => x.Id == "shelf.add"), "Shelf gate");
off.Set(Feature.Shelf, true);
Check(Catalog.Available(off, new[] { "photo.png" }).Any(x => x.Id == "shelf.add"), "Enabled shelf offered");
Check(!Catalog.Available(off, new[] { "photo.png", "clip.mp4" }).Any(x => x.ImageOnly), "Mixed batch not image-only");
Check(!Catalog.Available(off, Array.Empty<string>()).Any(), "No action without explicit files");
Check(Catalog.Actions.Select(x => x.Id).Distinct().Count() == Catalog.Actions.Length, "Unique stable IDs");
Check(!Catalog.IsImage("image.heic") && !Catalog.IsImage("x.svg") && Catalog.IsImage("IMAGE.JPEG"), "Closed codec extensions");

ImageRecipeChecks.Run(Check, Reject);
RenameChecks.Run(Check, Reject);
RenameDependencyChecks.Run(Check, Reject);
QrRotationChecks.Run(Check, Reject);
QrDataRecoveryChecks.Run(Check, Reject);
GifDitherChecks.Run(Check, Reject);
QrMacExportChecks.Run(Check, Reject);
ClotheslineGestureChecks.Run(Check, Reject);
CommonSimilarityChecks.Run(Check, Reject);
CloseWindowsChecks.Run(Check, Reject);
MoveIntegrationChecks.Run(Check, Reject);
ImageArrivalIntegrationChecks.Run(Check);
WheelGestureIntegrationChecks.Run(Check);
WheelGestureChecks.Run(Check, Reject);
ImageArrivalChecks.Run(Check, Reject);
MoveFilesChecks.Run(Check, Reject);
PhotoChecks.Run(Check, Reject);
MarkupChecks.Run(Check, Reject);
FileToolChecks.Run(Check, Reject);
ZipExtractChecks.Run(Check, Reject);
TarExtractChecks.Run(Check, Reject);
SevenZipExtractChecks.Run(Check, Reject);
FolderStyleChecks.Run(Check, Reject);
        McpChecks.Run(Check, Reject);
        CutoutChecks.Run(Check, Reject);
        IdPhotoChecks.Run(Check, Reject);
        UpscaleChecks.Run(Check, Reject);
        TranscribeChecks.Run(Check, Reject);
        SubtitleBurnChecks.Run(Check, Reject);
        VideoTrimChecks.Run(Check, Reject);
        VideoEditChecks.Run(Check, Reject);
        SubtitleFormatsChecks.Run(Check, Reject);
        DataFileFormatsChecks.Run(Check, Reject);
        FontFileFormatsChecks.Run(Check, Reject);
        IcoFileChecks.Run(Check, Reject);
        TgaPsdChecks.Run(Check, Reject);
        WebPChecks.Run(Check, Reject);
        FolderZipChecks.Run(Check, Reject);
        FolderCopyChecks.Run(Check, Reject);
        FolderMoveChecks.Run(Check, Reject);
        ImageStitchChecks.Run(Check, Reject);
        CleanupFilesChecks.Run(Check, Reject);
CopyFilesChecks.Run(Check, Reject);
ImageExtraChecks.Run(Check, Reject);
CustomizationChecks.Run(Check, Reject);
DoctorChecks.Run(Check);
WindowsDoctorChecks.Run(Check,Reject);
MountedDriveChecks.Run(Check,Reject);
PowerPlanChecks.Run(Check,Reject);
SleepCapabilityChecks.Run(Check,Reject);
ServiceChecks.Run(Check,Reject);
ScheduledTasksChecks.Run(Check,Reject);
ScheduledTaskIntegrationChecks.Run(Check);
CommonFolderChecks.Run(Check,Reject);
CommonDuplicateChecks.Run(Check,Reject);
StartupFolderChecks.Run(Check,Reject);
StartupApprovalChecks.Run(Check,Reject);
StartupAppsChecks.Run(Check,Reject);
CacheLocationChecks.Run(Check,Reject);
AppCacheChecks.Run(Check,Reject);
AppLeftoverChecks.Run(Check,Reject);
DoctorNetworkChecks.Run(Check);
FolderSizeChecks.Run(Check, Reject);
DuplicateFileChecks.Run(Check, Reject);
TextSimilarityChecks.Run(Check, Reject);
TempCleanupChecks.Run(Check, Reject);
PhotoSimilarityChecks.Run(Check, Reject);
ProjectFolderChecks.Run(Check, Reject);
LargeFileChecks.Run(Check, Reject);
UsageChecks.Run(Check, Reject);
AppMemoryChecks.Run(Check, Reject);
AppMemoryHelperChecks.Run(Check, Reject);
InstalledAppsChecks.Run(Check, Reject);
StorePackagesChecks.Run(Check, Reject);
StartupChecks.Run(Check, Reject);
DocumentTextChecks.Run(Check, Reject);
DocumentHtmlChecks.Run(Check, Reject);
DocumentOdtChecks.Run(Check, Reject);
DocumentEpubChecks.Run(Check, Reject);
DocumentDocChecks.Run(Check, Reject);
DocumentRtfChecks.Run(Check, Reject);
DocumentOdtExportChecks.Run(Check, Reject);
AppRouteChecks.Run(Check, Reject);
AssistantDraftChecks.Run(Check, Reject);
PngMetadataChecks.Run(Check, Reject);
JpegMetadataChecks.Run(Check, Reject);
TiffMetadataChecks.Run(Check, Reject);
PdfStructureChecks.Run(Check, Reject);
ExifOrientationChecks.Run(Check, Reject);
MenuChecks.Run(Check, Reject);
ClotheslineChecks.Run(Check, Reject);
PdfChecks.Run(Check, Reject);
RetouchChecks.Run(Check, Reject);
QrChecks.Run(Check, Reject);
QrAllLevelsChecks.Run(Check, Reject);
QrReadChecks.Run(Check,Reject);
QrPhotoReadChecks.Run(Check,Reject);
QrRecoveryIntegrationChecks.Run(Check);
PaletteAutoChecks.Run(Check, Reject);
MarkEditChecks.Run(Check, Reject);
WaveChecks.Run(Check, Reject);
WaveMusicChecks.Run(Check, Reject);
WaveMusicRateChecks.Run(Check, Reject);
WaveDuckingChecks.Run(Check, Reject);
WaveLoopChecks.Run(Check, Reject);
WaveFormatChecks.Run(Check, Reject);
WaveSpeedChecks.Run(Check, Reject);
WaveStretchChecks.Run(Check, Reject);
WinRtBoundaryChecks.Run(Check, Reject);
M4aChecks.Run(Check, Reject);
G711Checks.Run(Check, Reject);
FloatWaveChecks.Run(Check, Reject);
AiffChecks.Run(Check, Reject);
AiffExportChecks.Run(Check, Reject);
WaveJoinChecks.Run(Check, Reject);
GifImageChecks.Run(Check, Reject);
GifExportChecks.Run(Check, Reject);
GifPaletteChecks.Run(Check, Reject);
GifPresetChecks.Run(Check, Reject);
WaveNoiseChecks.Run(Check, Reject);
WaveNoiseInputChecks.Run(Check, Reject);
WaveQuietSampleChecks.Run(Check, Reject);
QrLibraryChecks.Run(Check,Reject);
QrGardenChecks.Run(Check,Reject);
AssistantChecks.Run(Check,Reject);
DoctorAssistantChecks.Run(Check,Reject);
DoctorSummaryChecks.Run(Check,Reject);
DoctorLabelChecks.Run(Check,Reject);
PeInspectionChecks.Run(Check,Reject);
FilePreviewChecks.Run(Check,Reject);
ImageBatchChecks.Run(Check);
PresetLibraryChecks.Run(Check, Reject);
MacQrLibraryChecks.Run(Check, Reject);

foreach (var pair in Strings.Table)
{
    Check(pair.Value.Length == 3 && pair.Value.All(x => !string.IsNullOrWhiteSpace(x)), "Three translations: " + pair.Key);
    if (pair.Key.Contains("{0}")) Check(pair.Value.All(x => x.Contains("{0}")), "Format placeholders retained");
}
foreach (var action in Catalog.Actions) Check(Strings.Table.ContainsKey(action.Title), "Action translated: " + action.Id);
foreach (var item in Compatibility.Items)
    Check(Strings.Table.ContainsKey(item.Title) && Strings.Table.ContainsKey(item.Status) && Strings.Table.ContainsKey(item.Detail), "Compatibility translated: " + item.Title);
Check(Strings.L("Files", "zh-Hans") == "文件" && Strings.L("Files", "zh-TW") == "檔案" && Strings.L("Files", "ja-JP") == "ファイル" && Strings.L("Files", "en") == "Files", "Language selection");

// Independently inventory UI call sites, including the dynamically translated error messages.
var directory = new DirectoryInfo(AppContext.BaseDirectory);
while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Swivel.Windows"))) directory = directory.Parent;
if (directory is null) throw new Exception("Run from this repository's Windows test output.");
// Doctor uses closed keys with arguments so system values are never translation IDs.
var doctorSource = string.Join("\n", new[] { "Swivel.Windows/PCDoctor.cs", "Swivel.Windows/DoctorNetworkReader.cs", "Swivel.Core/DoctorNetwork.cs", "Swivel.Windows/ExecutableStructureDialog.cs" }.Select(path => File.ReadAllText(Path.Combine(directory.FullName, path))));
foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(doctorSource, "(?:Add|Value|Check)\\(\"([^\"]+)\"(?:, \"([^\"]+)\")?"))
    foreach (int group in new[] { 1, 2 }) if (match.Groups[group].Success) Check(Strings.Table.ContainsKey(match.Groups[group].Value), "Doctor text translated: " + match.Groups[group].Value);
var strings = System.Text.RegularExpressions.Regex.Matches(string.Join("\n", Directory.EnumerateFiles(directory.FullName, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(".Tests") && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).Select(File.ReadAllText)), "(?:L|UserError)\\(\"([^\"\\n]*)\"");
foreach (System.Text.RegularExpressions.Match match in strings)
    Check(Strings.Table.ContainsKey(match.Groups[1].Value), "UI string translated: " + match.Groups[1].Value);

Console.WriteLine($"PASS: {checks} core checks (Windows path contract, defaults, catalog, image sizing, localization). Native Windows execution is a separate suite.");
