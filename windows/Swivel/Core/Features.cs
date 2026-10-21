namespace Swivel;

/// The tools in the Swivel bundle. Each one can be switched on or off on its own.
enum Feature { Wheel, Clothesline, TextCapture, Shelf, ImagePresets, QuickActions }

static class Features
{
    public static readonly Feature[] All =
        [Feature.Wheel, Feature.Clothesline, Feature.TextCapture, Feature.Shelf, Feature.ImagePresets, Feature.QuickActions];

    /// The settings key part, matching the Mac app ("feature.textCapture").
    public static string Id(this Feature feature) => feature switch
    {
        Feature.Wheel => "wheel",
        Feature.Clothesline => "clothesline",
        Feature.TextCapture => "textCapture",
        Feature.Shelf => "shelf",
        Feature.ImagePresets => "imagePresets",
        _ => "quickActions",
    };

    public static string Title(this Feature feature) => feature switch
    {
        Feature.Wheel => L("Wheel"),
        Feature.Clothesline => L("Clothesline"),
        Feature.TextCapture => L("Text Capture"),
        Feature.Shelf => L("File Shelf"),
        Feature.ImagePresets => L("Image Presets"),
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
        _ => "",                    // Keyboard
    };

    public static string Blurb(this Feature feature) => feature switch
    {
        Feature.Wheel => L("Drag a file and tap a key: a menu pops up to convert it, edit it or send it somewhere."),
        Feature.Clothesline => L("Screenshots hang on a rope along the top of the screen instead of piling up in a folder."),
        Feature.TextCapture => L("Turn text in an image into editable, copyable text. Everything stays on your PC."),
        Feature.Shelf => L("Gather files from different places, then drag a selection out together."),
        Feature.ImagePresets => L("Save your favourite image sizes, formats and privacy settings for next time."),
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
                    }
                }
                break;
        }
    }
}

/// The three menus a drag can open.
enum WheelMode { Convert, Tools, Actions }

static class WheelModes
{
    public static readonly WheelMode[] All = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions];

    /// The settings key part ("convert"); stays English, like the Mac app's stored keys.
    public static string Id(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => "convert",
        WheelMode.Tools => "tools",
        _ => "actions",
    };

    public static WheelMode? FromId(string? id) => All.Cast<WheelMode?>().FirstOrDefault(m => m!.Value.Id() == id);

    public static string Title(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => L("Convert"),
        WheelMode.Tools => L("Tools"),
        _ => L("Actions"),
    };

    /// A Segoe Fluent Icons glyph for the mode.
    public static string Glyph(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => "", // Sync
        WheelMode.Tools => "",   // Equalizer
        _ => "",                 // Lightning bolt
    };

    public static string KeySymbol(this WheelMode mode) => Prefs.Key(mode).Symbol();
}
