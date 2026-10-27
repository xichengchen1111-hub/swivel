namespace Swivel;

/// The tools in the Swivel bundle. Each one can be switched on or off on its own.
enum Feature { Wheel, Clothesline, TextCapture, Shelf, ImagePresets, QuickActions, BatchRename, PdfPages, QrCodes, AppRouting }

static class Features
{
    public static readonly Feature[] All =
        [Feature.Wheel, Feature.Clothesline, Feature.TextCapture, Feature.Shelf, Feature.ImagePresets, Feature.QuickActions,
         Feature.BatchRename, Feature.PdfPages, Feature.QrCodes, Feature.AppRouting];

    /// The settings key part, matching the Mac app ("feature.textCapture").
    public static string Id(this Feature feature) => feature switch
    {
        Feature.Wheel => "wheel",
        Feature.Clothesline => "clothesline",
        Feature.TextCapture => "textCapture",
        Feature.Shelf => "shelf",
        Feature.ImagePresets => "imagePresets",
        Feature.BatchRename => "batchRename",
        Feature.PdfPages => "pdfPages",
        Feature.QrCodes => "qrCodes",
        Feature.AppRouting => "appRouting",
        _ => "quickActions",
    };

    public static string Title(this Feature feature) => feature switch
    {
        Feature.Wheel => L("Wheel"),
        Feature.Clothesline => L("Clothesline"),
        Feature.TextCapture => L("Text Capture"),
        Feature.Shelf => L("File Shelf"),
        Feature.ImagePresets => L("Image Presets"),
        Feature.BatchRename => L("Batch Rename"),
        Feature.PdfPages => L("PDF Pages"),
        Feature.QrCodes => L("QR & Barcodes"),
        Feature.AppRouting => L("App Actions"),
        _ => L("Quick Actions"),
    };

    /// A Segoe Fluent Icons glyph for the feature.
    public static string Glyph(this Feature feature) => feature switch
    {
        Feature.Wheel => "",        // Target
        Feature.Clothesline => "",  // Photo
        Feature.TextCapture => "",  // Font / text
        Feature.Shelf => "",        // Package / tray
        Feature.ImagePresets => "", // Equalizer
        Feature.BatchRename => "",  // Rename
        Feature.PdfPages => "",     // Document
        Feature.QrCodes => "",      // QR code
        Feature.AppRouting => "",   // Open in new window
        _ => "",                    // Keyboard
    };

    public static string Blurb(this Feature feature) => feature switch
    {
        Feature.Wheel => L("Drag a file and tap a key: a menu pops up to convert it, edit it or send it somewhere."),
        Feature.Clothesline => L("Screenshots hang on a rope along the top of the screen instead of piling up in a folder."),
        Feature.TextCapture => L("Turn text in an image into editable, copyable text. Everything stays on your PC."),
        Feature.Shelf => L("Gather files from different places, then drag a selection out together."),
        Feature.ImagePresets => L("Save your favourite image sizes, formats and privacy settings for next time."),
        Feature.BatchRename => L("Preview and rename a batch of files, with conflict checks and undo."),
        Feature.PdfPages => L("Pick, reorder, rotate and export PDF pages without changing the originals."),
        Feature.QrCodes => L("Read QR codes and barcodes from selected images, entirely on your PC."),
        Feature.AppRouting => L("Open files in your chosen apps, or prepare a Codex or Claude Code draft."),
        _ => L("Find an action from the keyboard, even when the taskbar is crowded."),
    };

    public static bool IsOn(this Feature feature) => feature switch
    {
        Feature.Wheel => Prefs.IsEnabled,
        Feature.Clothesline => Prefs.ClotheslineEnabled,
        _ => Prefs.FeatureEnabled(feature),
    };

    public static void SetOn(this Feature feature, bool on)
    {
        switch (feature)
        {
            case Feature.Wheel:
                Prefs.IsEnabled = on;
                break;
            case Feature.Clothesline:
                Clothesline.Shared.SetEnabled(on);
                break;
            default:
                Prefs.SetFeatureEnabled(feature, on);
                if (!on)
                {
                    switch (feature)
                    {
                        case Feature.Shelf: Shelf.Shared.Hide(); break;
                        case Feature.TextCapture: TextCaptureWindow.CloseAll(); break;
                        case Feature.QuickActions: QuickActions.Shared.Hide(); break;
                        case Feature.BatchRename: BatchRenameWindow.CloseAll(); break;
                        case Feature.PdfPages: PdfPagesWindow.CloseAll(); break;
                        case Feature.QrCodes: QrCodesWindow.CloseAll(); QrLibraryWindow.CloseAll(); break;
                    }
                }
                break;
        }
    }
}

static class FileTools
{
    /// Asks for files, then opens the feature's window with them (Quick Actions, the tray menu and settings pages).
    public static void ChooseAndOpen(Feature feature)
    {
        if (!feature.IsOn()) return;
        var files = feature == Feature.PdfPages
            ? ToolUI.ChoosePdfs(multiple: true)
            : ToolUI.ChooseFiles(imagesOnly: feature == Feature.QrCodes, multiple: true);
        if (files.Count == 0) return;
        switch (feature)
        {
            case Feature.BatchRename: BatchRenameWindow.Present(files); break;
            case Feature.PdfPages: PdfPagesWindow.Present(files); break;
            case Feature.QrCodes: QrCodesWindow.Present(files); break;
        }
    }
}

/// The five menus a drag can open. Apps and Codes (1.4) are only offered while their feature is on.
enum WheelMode { Convert, Tools, Actions, Apps, Codes }

static class WheelModes
{
    public static readonly WheelMode[] All = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions, WheelMode.Apps, WheelMode.Codes];

    /// The settings key part ("convert"); stays English, like the Mac app's stored keys.
    public static string Id(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => "convert",
        WheelMode.Tools => "tools",
        WheelMode.Apps => "apps",
        WheelMode.Codes => "codes",
        _ => "actions",
    };

    public static WheelMode? FromId(string? id) => All.Cast<WheelMode?>().FirstOrDefault(m => m!.Value.Id() == id);

    public static string Title(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => L("Convert"),
        WheelMode.Tools => L("Tools"),
        WheelMode.Apps => L("App Actions"),
        WheelMode.Codes => L("QR Library"),
        _ => L("Actions"),
    };

    /// A Segoe Fluent Icons glyph for the mode.
    public static string Glyph(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => "", // Sync
        WheelMode.Tools => "",   // Equalizer
        WheelMode.Apps => "",    // Open in new window
        WheelMode.Codes => "",   // QR code
        _ => "",                 // Lightning bolt
    };

    /// Whether this menu can open now: Apps and Codes follow their features' switches.
    public static bool IsAvailable(this WheelMode mode) => mode switch
    {
        WheelMode.Apps => Feature.AppRouting.IsOn(),
        WheelMode.Codes => Feature.QrCodes.IsOn(),
        _ => true,
    };

    public static string KeySymbol(this WheelMode mode) => Prefs.Key(mode).Symbol();
}
