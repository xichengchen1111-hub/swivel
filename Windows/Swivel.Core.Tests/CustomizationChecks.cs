using System;
using System.Linq;
using Swivel.Core;

internal static class CustomizationChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.MyWheel), "My Wheel independently off");
        prefs.SetCustomActions(new[] { "file.paths", "convert.png", "shelf.add" });
        check(!Catalog.ForCustomWheel(prefs, new[] { "photo.png" }).Any(), "Custom wheel gated");
        prefs.Set(Feature.MyWheel,true); prefs.Set(Feature.Wheel,false);
        check(Catalog.ForCustomWheel(prefs,new[] { "photo.png" }).Select(x => x.Id).SequenceEqual(new[] { "file.paths", "convert.png" }), "Order retained and disabled tool omitted");
        check(Catalog.ForCustomWheel(prefs,new[] { "a.txt" }).Select(x => x.Id).SequenceEqual(new[] { "file.paths" }), "My Wheel filters file types");
        prefs.Set(Feature.Shelf,true); check(Catalog.ForCustomWheel(prefs,Array.Empty<string>()).Count() == 3, "Enabled tool returns without reconfiguration");
        string id = Guid.NewGuid().ToString("N"); prefs.SavePreset(new(id,"First",new("png"))); prefs.SetCustomActions(new[] { "preset."+id,"file.paths" });
        prefs.SavePreset(new(id,"Renamed",new("jpg"))); prefs.Set(Feature.ImagePresets,true);
        check(Catalog.ForCustomWheel(prefs,new[] { "a.png" }).First().Title == "Renamed", "Preset custom action survives rename");
        var shortcuts = new ShortcutSet(new(3,0x41),new(6,0x42),new(5,0x70)); prefs.SetShortcuts(shortcuts);
        var loaded = Prefs.Parse(prefs.Serialize()); check(loaded.CustomActionIds.SequenceEqual(prefs.CustomActionIds) && loaded.Shortcuts == shortcuts, "Customization settings round trip");
        prefs.RemovePreset(id); check(prefs.CustomActionIds.SequenceEqual(new[] { "file.paths" }), "Deleted preset removes only stale custom reference");
        check(new Shortcut(3,0x57).Label == "Ctrl+Alt+W" && new Shortcut(6,0x20).Label == "Ctrl+Shift+Space" && new Shortcut(5,0x87).Label == "Alt+Shift+F24", "Shortcut labels");
        foreach (var key in new[] { new Shortcut(2,0x41),new Shortcut(11,0x41),new Shortcut(3,0x1b),new Shortcut(0,0x70),new Shortcut(7,0xff) }) reject(key.Validate, "Unsupported global shortcut rejected");
        reject(() => new ShortcutSet(new(3,65),new(3,65),new(3,66)).Validate(), "No duplicate hotkeys");
        reject(() => prefs.SetCustomActions(new[] { "file.paths","file.paths" }), "No duplicate custom actions");
        reject(() => prefs.SetCustomActions(new[] { "not-an-action" }), "No arbitrary custom action IDs");
        var legacy = Prefs.Parse("{\"Version\":2,\"Language\":\"en\",\"Enabled\":[\"Wheel\"],\"Presets\":[]}");
        check(legacy.Shortcuts == ShortcutSet.Default && legacy.CustomActionIds.Count == 0, "Existing v2 preferences migrate without reset");
    }
}
