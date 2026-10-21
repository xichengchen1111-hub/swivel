using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Swivel;

/// The tap logic of the drag watcher as a pure state machine, fed one sample at a time (so tests can drive it).
///
/// A drag is the primary button held and the pointer moved past the system drag threshold. A trigger key that goes
/// down during the drag is a tap. A key tapped after the button went down but before the drag was recognised (a
/// quick hand) counts once the drag is recognised, if it is still held; a key already held when the button went
/// down never counts. Releasing the button ends the drag.
sealed class DragTapDetector
{
    bool wasDown;
    bool active;
    int lastKeys;
    /// Keys pressed after the button went down but before the drag was recognised.
    int earlyTaps;
    int downX, downY;

    /// The trigger key bit of each menu (bit n = TriggerKey n).
    public Func<WheelMode, int> KeyBit { get; set; } = mode => 1 << (int)Prefs.Key(mode);
    /// Pixels the pointer must move with the button held before it counts as a drag.
    public int ThresholdX { get; set; } = 4;
    public int ThresholdY { get; set; } = 4;

    public bool IsDragging => active;

    public readonly record struct Result(WheelMode? Tap, bool Started, bool Ended);

    /// One poll: button state, pointer (pixels) and held trigger keys as bits.
    public Result Feed(bool down, int x, int y, int keys, bool enabled = true)
    {
        try
        {
            if (!down || !enabled)
            {
                earlyTaps = 0;
                if (active)
                {
                    active = false;
                    return new Result(null, false, true);
                }
                return default;
            }

            if (!wasDown)
            {
                downX = x;
                downY = y;
                earlyTaps = 0;
                return default;
            }

            if (!active)
            {
                earlyTaps |= keys & ~lastKeys;
                if (Math.Abs(x - downX) < ThresholdX && Math.Abs(y - downY) < ThresholdY) return default;
                active = true;
                // Keys already held when the button went down don't count; one tapped since then (and still held) does.
                int early = earlyTaps & keys;
                earlyTaps = 0;
                return new Result(FirstMode(early), true, false);
            }

            return new Result(FirstMode(keys & ~lastKeys), false, false);
        }
        finally
        {
            wasDown = down && enabled;
            lastKeys = keys;
        }
    }

    WheelMode? FirstMode(int pressed)
    {
        if (pressed == 0) return null;
        foreach (var mode in WheelModes.All)
            if ((pressed & KeyBit(mode)) != 0) return mode;
        return null;
    }
}

/// Notices a drag in progress anywhere on screen (primary button held and moved) and reports trigger-key taps
/// during it.
///
/// Polls instead of hooking: reading the button, pointer and keys needs no special rights and no low-level hooks.
/// Polling is slow (20 Hz) while the button is up and switches to the fastest timer rate while it is held. During a
/// drag the process opts out of power throttling so Windows doesn't slow the timer down (the Mac's App Nap opt-out).
sealed partial class DragWatcher
{
    public Action<WheelMode>? OnKeyTap { get; set; }
    public Action? OnDragEnd { get; set; }
    /// When false, drags are ignored and no menu opens.
    public bool IsEnabled { get; set; } = true;

    readonly DragTapDetector detector = new();
    DispatcherTimer? timer;
    bool fast;
    bool throttlingOff;

    static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1.0 / 20);
    static readonly TimeSpan DragInterval = TimeSpan.FromMilliseconds(1);

    public void Start()
    {
        if (timer != null) return;
        detector.ThresholdX = Math.Max(Native.GetSystemMetrics(68 /* SM_CXDRAG */), 1);
        detector.ThresholdY = Math.Max(Native.GetSystemMetrics(69 /* SM_CYDRAG */), 1);
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = IdleInterval };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Stop()
    {
        timer?.Stop();
        timer = null;
        SetThrottling(on: true);
    }

    void Tick()
    {
        bool down = Native.PrimaryButtonDown();
        var cursor = Native.CursorPixels();
        int keys = 0;
        foreach (var key in TriggerKeys.All)
            if (Native.IsKeyDown(key.VirtualKey())) keys |= 1 << (int)key;

        var result = detector.Feed(down, cursor.X, cursor.Y, keys, IsEnabled);
        Schedule(down && IsEnabled);
        if (result.Started)
        {
            SetThrottling(on: false);
            Feedback.Prepare();
        }
        if (result.Tap is { } mode) OnKeyTap?.Invoke(mode);
        if (result.Ended)
        {
            SetThrottling(on: true);
            OnDragEnd?.Invoke();
        }
    }

    void Schedule(bool held)
    {
        if (timer is null || held == fast) return;
        fast = held;
        timer.Interval = held ? DragInterval : IdleInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// Off while a drag is in progress (full speed, precise timers); afterwards Windows decides again.
    void SetThrottling(bool on)
    {
        if (throttlingOff == !on) return;
        throttlingOff = !on;
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, with the state bits clear: never throttle.
            ControlMask = on ? 0u : 0x1u | 0x4u,
            StateMask = 0,
        };
        try { SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()); }
        catch (EntryPointNotFoundException) { }
    }
}
