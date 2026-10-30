using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class KeepAwake : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer timer;
    private DateTimeOffset? until;
    public bool Active { get; private set; }
    public bool Display { get; private set; }
    public DateTimeOffset? Until => until;
    public event Action? Changed;
    public KeepAwake()
    {
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => {
            if (until.HasValue && DateTimeOffset.UtcNow >= until.Value) Stop();
        }, dispatcher);
        timer.Stop();
    }
    public void Start(TimeSpan? duration, bool display)
    {
        dispatcher.VerifyAccess();
        if (duration.HasValue && (duration.Value < TimeSpan.FromMinutes(1) || duration.Value > TimeSpan.FromHours(5)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (SetThreadExecutionState(0x80000000 | 1u | (display ? 2u : 0u)) == 0)
            throw new UserError("Windows could not start Keep Awake.");
        until = duration.HasValue ? DateTimeOffset.UtcNow + duration.Value : null;
        Active = true; Display = display;
        timer.Start();
        Changed?.Invoke();
    }
    public void Stop()
    {
        dispatcher.VerifyAccess();
        if (Active && SetThreadExecutionState(0x80000000) == 0)
            throw new UserError("Windows could not stop Keep Awake. Quit Swivel to release it.");
        timer.Stop(); until = null; Active = false; Display = false; Changed?.Invoke();
    }
    public void Dispose() { Stop(); }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}

public sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source;
    private readonly Action wheel, quick, custom;
    public Hotkeys(Action wheel, Action quick, Action custom)
    {
        this.wheel = wheel; this.quick = quick; this.custom = custom;
        source = new HwndSource(new HwndSourceParameters("Swivel hotkeys") { ParentWindow = new IntPtr(-3) });
        source.AddHook(Hook);
    }
    public bool Configure(Prefs prefs)
    {
        UnregisterHotKey(source.Handle, 1); UnregisterHotKey(source.Handle, 2); UnregisterHotKey(source.Handle, 3);
        bool wheelOK = !prefs.IsOn(Feature.Wheel) || RegisterHotKey(source.Handle, 1, 0x4000 | prefs.Shortcuts.Wheel.Modifiers, prefs.Shortcuts.Wheel.Key);
        bool quickOK = !prefs.IsOn(Feature.QuickActions) || RegisterHotKey(source.Handle, 2, 0x4000 | prefs.Shortcuts.Quick.Modifiers, prefs.Shortcuts.Quick.Key);
        bool customOK = !prefs.IsOn(Feature.MyWheel) || RegisterHotKey(source.Handle, 3, 0x4000 | prefs.Shortcuts.Custom.Modifiers, prefs.Shortcuts.Custom.Key);
        return wheelOK && quickOK && customOK;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            handled = true;
            if (wParam.ToInt32() == 1) wheel(); else if (wParam.ToInt32() == 2) quick(); else if (wParam.ToInt32() == 3) custom();
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        UnregisterHotKey(source.Handle, 1); UnregisterHotKey(source.Handle, 2); UnregisterHotKey(source.Handle, 3);
        source.RemoveHook(Hook); source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

public static class SettingsStore
{
    private const string Key = @"Software\Swivel\WindowsPreview";
    public static Prefs Load(out bool failed)
    {
        failed = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("Preferences") is string json ? Prefs.Parse(json) : new Prefs();
        }
        catch { failed = true; return new Prefs(); }
    }
    public static void Save(Prefs prefs)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("Preferences", prefs.Serialize(), RegistryValueKind.String);
    }
}

public static class Explorer
{
    public static void Reveal(string path)
    {
        NativeFiles.InspectShelfReference(path, System.Threading.CancellationToken.None, reference => {
            // Explorer can have a content preview pane; do not ask it to select
            // an online-only item. Copy/drag remains a pure reference handoff.
            if (reference.IsOnlineOnly) throw new UserError(ShelfReferences.OnlineOnlyRefusal);
            int result = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
            Marshal.ThrowExceptionForHR(result);
            try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0)); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        });
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint input, out uint output);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
}
