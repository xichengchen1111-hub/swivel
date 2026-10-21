namespace Swivel;

/// The moments the app gives feedback for, in one place (Feel.swift's `Feedback`). Every call fires from the same
/// event that changes the picture.
///
/// The Mac app pairs each moment with a trackpad haptic. Windows has no public API for trackpad or mouse haptics, so
/// the moments that are haptic-only on the Mac (a hover onto the cancel hub, a refusal, an editor snap) stay silent
/// here, and the others play their sound.
static class Feedback
{
    /// The pointer moved onto a different menu item (or back onto the hub, where releasing cancels: haptic only).
    public static void Hover(bool sound = true)
    {
        if (sound) Sounds.Play(SoundEffect.Hover);
    }

    /// The menu turned to another page.
    public static void Page() => Sounds.Play(SoundEffect.Page);

    /// A file was dropped on an item.
    public static void Press() => Sounds.Play(SoundEffect.Press);

    /// A dropped action finished successfully.
    public static void Done() => Sounds.Play(SoundEffect.Done);

    /// Nothing to do, or something failed. Haptic-only on the Mac: silent on Windows.
    public static void Refuse() { }

    /// A feature switch was flipped: the click of a light switch, a little brighter for on than for off.
    public static void Toggle(bool on) => Sounds.Play(on ? SoundEffect.SwitchOn : SoundEffect.SwitchOff);

    /// A snap or detent in an editor (crop edges, slider neutral value). Haptic-only on the Mac: silent on Windows.
    public static void Snap() { }

    /// A drag started or a menu is opening: get the sounds ready so the first one is on time.
    public static void Prepare() => Sounds.Prepare();

    /// `--feedback check|play`: check reports whether the sounds are ready; play runs through every sound once, a
    /// second apart.
    public static async Task<int> CommandLine(string mode)
    {
        Prepare();
        if (mode == "play")
        {
            (string Name, Action Run)[] steps =
            [
                ("hover", () => Hover()), ("page", Page), ("press", Press), ("done", Done),
                ("switch on", () => Toggle(true)), ("switch off", () => Toggle(false)),
            ];
            await Task.Delay(600);
            foreach (var (name, run) in steps)
            {
                Console.WriteLine(name);
                run();
                await Task.Delay(1000);
            }
        }
        else
        {
            await Task.Delay(600);
        }
        Console.WriteLine($"haptics: unavailable on Windows sounds: {Sounds.RegisteredCount} device: {(Sounds.DeviceReady ? "ready" : "unavailable")} enabled: {(Prefs.SoundEffects ? "yes" : "no")}");
        return Sounds.RegisteredCount > 0 || !Prefs.SoundEffects ? 0 : 1;
    }
}
