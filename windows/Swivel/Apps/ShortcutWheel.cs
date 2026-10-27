namespace Swivel;

/// A registered global shortcut, or the attempt at one (HotKey; tests inject their own).
interface IHotKeyRegistration : IDisposable
{
    bool IsRegistered { get; }
}

/// The clickable tool wheel opened from the keyboard (ShortcutWheel.swift): Ctrl + Alt + J (or Space / W) shows the
/// enabled file tools at the pointer in the chosen menu template, without dragging anything.
///
/// Windows has no list of reserved system shortcuts like macOS's symbolic hot keys; a shortcut another app (or
/// Windows) already holds fails RegisterHotKey, and Settings reports that as a conflict instead of "ready".
sealed class ShortcutWheel
{
    public static ShortcutWheel Shared { get; } = new();

    /// Raised after the shortcut is registered again, released or refused (on the UI thread), so Settings can refresh.
    public event Action? Changed;

    readonly Func<uint, uint, Action, IHotKeyRegistration> register;
    WheelController? wheel;
    IHotKeyRegistration? hotKey;
    WheelShortcut? registered;

    internal ShortcutWheel(Func<uint, uint, Action, IHotKeyRegistration>? register = null, WheelController? wheel = null)
    {
        this.register = register ?? ((modifiers, key, action) => new HotKey(modifiers, key, action));
        this.wheel = wheel;
    }

    /// The shortcut is registered and will open the wheel.
    public bool Available { get; private set; }

    /// The shortcut registered now (or tried last); null while the tool wheel is off.
    public WheelShortcut? RegisteredShortcut => registered;

    internal WheelController Wheel => wheel ??= new WheelController();

    /// Registers or releases the shortcut to match the settings. A refused shortcut is tried again on the next sync
    /// (the app holding it may have quit).
    public void Sync()
    {
        if (!Feature.Wheel.IsOn() || !Prefs.ShortcutWheelEnabled)
        {
            bool had = hotKey != null || registered != null;
            hotKey?.Dispose();
            hotKey = null;
            registered = null;
            Available = false;
            wheel?.Dismiss();
            if (had) Changed?.Invoke();
            return;
        }
        var key = Prefs.WheelShortcut;
        if (registered == key && Available) return;
        hotKey?.Dispose();
        hotKey = register(HotKey.MOD_CONTROL | HotKey.MOD_ALT, (uint)key.VirtualKey(), Show);
        registered = key;
        Available = hotKey.IsRegistered;
        if (!Available)
        {
            hotKey.Dispose();
            hotKey = null;
        }
        Changed?.Invoke();
    }

    /// Opens the wheel at the pointer, or closes it if it is open (pressing the shortcut again).
    public void Show()
    {
        if (!Feature.Wheel.IsOn() || !Prefs.ShortcutWheelEnabled) return;
        if (Wheel.IsVisible)
        {
            Wheel.Dismiss();
            return;
        }
        Wheel.ShowClickable(Items());
    }

    /// The root items: each enabled file tool, the QR Library, App Actions (a second wheel of apps) and Swivel itself.
    public List<WheelItem> Items()
    {
        var items = new List<WheelItem>();
        foreach (var (feature, title) in new[] { (Feature.BatchRename, "Batch Rename"), (Feature.PdfPages, "Arrange PDF Pages"), (Feature.QrCodes, "Read QR Codes") })
        {
            if (!feature.IsOn()) continue;
            items.Add(new WheelItem(title, _ =>
            {
                if (!feature.IsOn()) throw new ToolError(L("This feature is turned off."));
                FileTools.ChooseAndOpen(feature);
                return Task.FromResult(new ActionResult(""));
            }));
        }
        if (Feature.QrCodes.IsOn())
        {
            items.Add(new WheelItem("QR Library", _ =>
            {
                if (!Feature.QrCodes.IsOn()) throw new ToolError(L("This feature is turned off."));
                QrLibraryWindow.Show();
                return Task.FromResult(new ActionResult(""));
            }));
        }
        if (Feature.AppRouting.IsOn())
        {
            items.Add(new WheelItem("App Actions", _ =>
            {
                if (!Feature.AppRouting.IsOn()) throw new ToolError(L("This feature is turned off."));
                var apps = Catalog.Items([], WheelMode.Apps);
                if (apps.Count == 0) AppWindow.Shared.Show(AppTab.AppRouting);
                else Wheel.ShowClickable(apps, WheelMode.Apps);
                return Task.FromResult(new ActionResult(""));
            }));
        }
        items.Add(new WheelItem("Open Swivel…", _ =>
        {
            AppWindow.Shared.Show();
            return Task.FromResult(new ActionResult(""));
        }));
        return items;
    }
}
