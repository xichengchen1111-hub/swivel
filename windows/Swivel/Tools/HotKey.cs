using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Swivel;

/// A system-wide keyboard shortcut (RegisterHotKey) delivered to its own message-only window, so several shortcuts
/// never replace each other. A shortcut another app already holds fails to register: `IsRegistered` is false and
/// `Status` holds the Win32 error (1409, ERROR_HOTKEY_ALREADY_REGISTERED). The Mac app's `HotKey` (Carbon) twin.
///
/// Create it on a thread with a running dispatcher (the UI thread); WM_HOTKEY arrives through that thread's
/// message loop and `action` runs there.
sealed partial class HotKey : IHotKeyRegistration
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x0312;
    static readonly IntPtr HWND_MESSAGE = new(-3);
    static int nextId;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly HwndSource source;
    readonly int id;
    readonly Action action;

    public uint Modifiers { get; }
    public uint Key { get; }
    public bool IsRegistered { get; private set; }
    /// 0 when registered, otherwise the Win32 error code.
    public int Status { get; }

    /// `modifiers` are MOD_* flags; MOD_NOREPEAT is added so holding the keys fires once.
    public HotKey(uint modifiers, uint key, Action action)
    {
        Modifiers = modifiers & ~MOD_NOREPEAT;
        Key = key;
        this.action = action;
        // IDs 0x0000–0xBFFF are for applications.
        id = 1 + Interlocked.Increment(ref nextId) % 0xBFFE;
        source = new HwndSource(new HwndSourceParameters("SwivelHotKey")
        {
            ParentWindow = HWND_MESSAGE, WindowStyle = 0, Width = 0, Height = 0,
        });
        source.AddHook(Hook);
        IsRegistered = RegisterHotKey(source.Handle, id, Modifiers | MOD_NOREPEAT, key);
        Status = IsRegistered ? 0 : Math.Max(Marshal.GetLastPInvokeError(), 1);
    }

    /// For a `QuickShortcuts.Win32()` pair.
    public HotKey((uint Modifiers, uint Key) shortcut, Action action) : this(shortcut.Modifiers, shortcut.Key, action) { }

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == id)
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Unregister()
    {
        if (source.IsDisposed) return;
        if (IsRegistered) UnregisterHotKey(source.Handle, id);
        IsRegistered = false;
        source.RemoveHook(Hook);
        source.Dispose();
    }

    public void Dispose() => Unregister();
}
