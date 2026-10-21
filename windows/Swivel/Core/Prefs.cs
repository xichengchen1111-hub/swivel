using System.Text.Json;
using System.Text.Json.Nodes;

namespace Swivel;

/// A modifier key that can open a menu while dragging. (Windows has no Command or fn key that survives a drag:
/// the Windows key opens Start, so the three menus use Shift, Ctrl and Alt.)
enum TriggerKey { Shift, Control, Alt }

static class TriggerKeys
{
    public static readonly TriggerKey[] All = [TriggerKey.Shift, TriggerKey.Control, TriggerKey.Alt];

    public static string Id(this TriggerKey key) => key switch
    {
        TriggerKey.Shift => "shift",
        TriggerKey.Control => "control",
        _ => "alt",
    };

    public static TriggerKey? FromId(string? id) => All.Cast<TriggerKey?>().FirstOrDefault(k => k!.Value.Id() == id);

    /// Short name for hints, e.g. "Shift".
    public static string Symbol(this TriggerKey key) => key switch
    {
        TriggerKey.Shift => L("Shift"),
        TriggerKey.Control => L("Ctrl"),
        _ => L("Alt"),
    };

    public static string Title(this TriggerKey key) => key switch
    {
        TriggerKey.Shift => L("⇧ Shift"),
        TriggerKey.Control => L("Ctrl"),
        _ => L("Alt"),
    };

    /// The Win32 virtual-key code (VK_SHIFT, VK_CONTROL, VK_MENU).
    public static int VirtualKey(this TriggerKey key) => key switch
    {
        TriggerKey.Shift => 0x10,
        TriggerKey.Control => 0x11,
        _ => 0x12,
    };
}

/// How the menu that pops up under the cursor is laid out.
enum MenuStyle { Wheel, List, Shelf, Notes, Clothesline }

static class MenuStyles
{
    public static readonly MenuStyle[] All = [MenuStyle.Wheel, MenuStyle.List, MenuStyle.Shelf, MenuStyle.Notes, MenuStyle.Clothesline];

    public static string Id(this MenuStyle style) => style.ToString().ToLowerInvariant();

    public static MenuStyle? FromId(string? id) => All.Cast<MenuStyle?>().FirstOrDefault(s => s!.Value.Id() == id);

    public static string Title(this MenuStyle style) => style switch
    {
        MenuStyle.Wheel => L("Wheel"),
        MenuStyle.List => L("List"),
        MenuStyle.Shelf => L("Bookshelf"),
        MenuStyle.Notes => L("Sticky Notes"),
        _ => L("Clothesline"),
    };

    public static string Blurb(this MenuStyle style) => style switch
    {
        MenuStyle.Wheel => L("A ring of keycaps around the cursor."),
        MenuStyle.List => L("A tidy column, like a context menu."),
        MenuStyle.Shelf => L("Book spines standing on a wooden shelf."),
        MenuStyle.Notes => L("Colourful sticky notes pinned in a grid."),
        _ => L("Tags pegged on a rope, swaying gently."),
    };
}

/// The global shortcut that opens Quick Actions.
enum QuickShortcut { Space, K, P }

static class QuickShortcuts
{
    public static readonly QuickShortcut[] All = [QuickShortcut.Space, QuickShortcut.K, QuickShortcut.P];

    public static string Id(this QuickShortcut s) => s.ToString().ToLowerInvariant();

    public static QuickShortcut? FromId(string? id) => All.Cast<QuickShortcut?>().FirstOrDefault(s => s!.Value.Id() == id);

    public static string Title(this QuickShortcut s) => s switch
    {
        QuickShortcut.Space => "Ctrl + Shift + Space",
        QuickShortcut.K => "Ctrl + Alt + K",
        _ => "Ctrl + Shift + P",
    };

    /// RegisterHotKey modifiers (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4) and virtual key.
    public static (uint Modifiers, uint Key) Win32(this QuickShortcut s) => s switch
    {
        QuickShortcut.Space => (2 | 4, 0x20),
        QuickShortcut.K => (2 | 1, 0x4B),
        _ => (2 | 4, 0x50),
    };
}

/// User settings, stored as JSON in %APPDATA%\Swivel\settings.json (or the file named by SWIVEL_SETTINGS).
/// Raises `Changed` whenever anything is saved. Keys match the Mac app's UserDefaults keys.
static class Prefs
{
    public static event Action? Changed;

    static readonly object gate = new();
    static JsonObject? store;

    public static string FilePath { get; set; } = Environment.GetEnvironmentVariable("SWIVEL_SETTINGS") is { Length: > 0 } custom
        ? custom
        : System.IO.Path.Combine(AppFolder, "settings.json");

    /// %APPDATA%\Swivel, created on demand. App data that is not a setting (shelf state, hung prints) lives here too.
    public static string AppFolder
    {
        get
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swivel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static JsonObject Store
    {
        get
        {
            if (store != null) return store;
            try
            {
                if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject loaded) return store = loaded;
            }
            catch (Exception) { /* A damaged file starts over with defaults. */ }
            return store = new JsonObject();
        }
    }

    /// Forgets the loaded values (tests point FilePath at a fresh file, then reload).
    public static void Reload()
    {
        lock (gate) store = null;
    }

    static void Save(bool notify = true)
    {
        lock (gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, Store.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (notify) Changed?.Invoke();
    }

    public static T? Get<T>(string key)
    {
        lock (gate)
        {
            if (!Store.TryGetPropertyValue(key, out var node) || node is null) return default;
            try { return node.Deserialize<T>(); } catch (Exception) { return default; }
        }
    }

    public static bool Has(string key)
    {
        lock (gate) return Store.ContainsKey(key);
    }

    public static void Set<T>(string key, T value, bool notify = true)
    {
        lock (gate) Store[key] = JsonSerializer.SerializeToNode(value);
        Save(notify);
    }

    public static void Remove(string key, bool notify = true)
    {
        lock (gate) Store.Remove(key);
        Save(notify);
    }

    static bool Bool(string key, bool fallback) => Has(key) ? Get<bool>(key) : fallback;

    // MARK: Features

    /// Additional tools are opt-in. Disabling them retains their settings.
    public static bool FeatureEnabled(Feature feature) => Bool($"feature.{feature.Id()}", false);

    public static void SetFeatureEnabled(Feature feature, bool enabled) => Set($"feature.{feature.Id()}", enabled);

    public static bool IsEnabled
    {
        get => Bool("enabled", true);
        set => Set("enabled", value);
    }

    public static string? LanguageId
    {
        get => Get<string>("language");
        set { if (value is null or "system") Remove("language"); else Set("language", value); }
    }

    public static List<ImagePreset> ImagePresets
    {
        get
        {
            var presets = Has("imagePresets.v1") ? Get<List<ImagePreset>>("imagePresets.v1") : null;
            return presets is null ? [.. ImagePreset.Starters] : presets.Where(p => p.IsValid).ToList();
        }
        set => Set("imagePresets.v1", value.Where(p => p.IsValid).ToList());
    }

    public static QuickShortcut QuickShortcut
    {
        get => QuickShortcuts.FromId(Get<string>("quickActions.shortcut")) ?? Swivel.QuickShortcut.Space;
        set => Set("quickActions.shortcut", value.Id());
    }

    /// Windows has no public trackpad haptics API; kept so the settings file matches the Mac app's keys.
    public static bool Haptics
    {
        get => Bool("feedback.haptics", true);
        set => Set("feedback.haptics", value);
    }

    /// Short interface sounds for the moments that get feedback.
    public static bool SoundEffects
    {
        get => Bool("feedback.sounds", true);
        set => Set("feedback.sounds", value);
    }

    // MARK: Clothesline

    public static bool ClotheslineEnabled
    {
        get => Bool("clothesline.enabled", true);
        set => Set("clothesline.enabled", value);
    }

    /// Whether new screenshots (from the Screenshots folder) are offered to the clothesline (asked once on first launch).
    public static bool ClotheslineTakeover
    {
        get => Bool("clothesline.takeover", false);
        set => Set("clothesline.takeover", value);
    }

    public static bool ClotheslineAsked
    {
        get => Bool("clothesline.asked", false);
        set => Set("clothesline.asked", value, notify: false);
    }

    public static bool ClotheslineHangCopied
    {
        get => Bool("clothesline.hangCopied", false);
        set => Set("clothesline.hangCopied", value);
    }

    // MARK: Wheel

    public static MenuStyle Style
    {
        get => MenuStyles.FromId(Get<string>("style")) ?? MenuStyle.Wheel;
        set => Set("style", value.Id());
    }

    static TriggerKey DefaultKey(WheelMode mode) => mode switch
    {
        WheelMode.Convert => TriggerKey.Shift,
        WheelMode.Tools => TriggerKey.Control,
        _ => TriggerKey.Alt,
    };

    public static TriggerKey Key(WheelMode mode) => TriggerKeys.FromId(Get<string>($"key.{mode.Id()}")) ?? DefaultKey(mode);

    /// Assigns a key; if another menu already used it, that menu takes this one's old key.
    public static void SetKey(TriggerKey key, WheelMode mode)
    {
        var old = Key(mode);
        foreach (var other in WheelModes.All)
            if (other != mode && Key(other) == key) Set($"key.{other.Id()}", old.Id(), notify: false);
        Set($"key.{mode.Id()}", key.Id());
    }

    public static void ResetKeys()
    {
        foreach (var mode in WheelModes.All) Remove($"key.{mode.Id()}", notify: false);
        Changed?.Invoke();
    }

    // MARK: Menu contents

    static string Slot(WheelMode mode, FileKind kind) => $"{mode.Id()}.{kind.Id()}";

    public static HashSet<string> Hidden(WheelMode mode, FileKind kind) =>
        [.. Get<List<string>>($"hidden.{Slot(mode, kind)}") ?? []];

    public static void SetHidden(IEnumerable<string> titles, WheelMode mode, FileKind kind) =>
        Set($"hidden.{Slot(mode, kind)}", titles.ToList());

    public static List<string> Order(WheelMode mode, FileKind kind) => Get<List<string>>($"order.{Slot(mode, kind)}") ?? [];

    public static void SetOrder(IEnumerable<string> titles, WheelMode mode, FileKind kind) =>
        Set($"order.{Slot(mode, kind)}", titles.ToList());

    /// Saves order and hidden items together, raising one change.
    public static void SetArrangement(IEnumerable<string> order, IEnumerable<string> hidden, WheelMode mode, FileKind kind)
    {
        Set($"order.{Slot(mode, kind)}", order.ToList(), notify: false);
        Set($"hidden.{Slot(mode, kind)}", hidden.ToList());
    }

    public static void ResetContents(WheelMode mode, FileKind kind)
    {
        Remove($"hidden.{Slot(mode, kind)}", notify: false);
        Remove($"order.{Slot(mode, kind)}");
    }

    /// Applies the user's order and hidden set to a menu's default items.
    public static List<WheelItem> Arrange(IReadOnlyList<WheelItem> items, WheelMode mode, FileKind kind)
    {
        var hidden = Hidden(mode, kind);
        var order = Order(mode, kind);
        return items.Where(i => !hidden.Contains(i.Title))
            .Select((item, offset) => (item, offset))
            .OrderBy(x => order.IndexOf(x.item.Title) is var i and >= 0 ? i : order.Count + x.offset)
            .Select(x => x.item)
            .ToList();
    }
}
