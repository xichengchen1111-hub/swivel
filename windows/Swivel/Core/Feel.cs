using System.Windows.Media;

namespace Swivel;

/// A damped spring that can be retargeted at any moment without a jump (position and velocity carry over).
///
/// Parameterised like SwiftUI: `Response` is the period of the undamped oscillation in seconds, `Damping` is the
/// damping fraction (1 = no overshoot, lower = bouncier). It is solved analytically, so any frame length is stable.
/// A port of the Mac app's `Spring` (Feel.swift); the numbers tuned there carry over unchanged.
struct Spring
{
    public double Value;
    public double Target;
    public double Velocity;
    public double Response;
    public double Damping;

    public Spring(double value = 0, double response = 0.3, double damping = 0.8)
    {
        Value = value;
        Target = value;
        Velocity = 0;
        Response = response;
        Damping = damping;
    }

    public readonly bool IsSettled => Math.Abs(Value - Target) < 0.0005 && Math.Abs(Velocity) < 0.005;

    /// Jumps to a value with no motion.
    public void Snap(double v)
    {
        Value = v;
        Target = v;
        Velocity = 0;
    }

    /// Advances by dt. `tuning` is the user's Feel & Text speed and bounce for this area (1.3); the default keeps
    /// the tuned 1.2 motion.
    public void Step(double dt, MotionTuning? tuning = null)
    {
        tuning ??= MotionTuning.Default;
        if (dt <= 0 || IsSettled)
        {
            if (IsSettled) { Value = Target; Velocity = 0; }
            return;
        }
        double w0 = 2 * Math.PI / Math.Max(Response / tuning.SafeSpeed, 0.001);
        double zeta = Math.Min(Math.Max(tuning.Damping(Damping), 0.05), 1);
        double x0 = Value - Target, v0 = Velocity;
        double x, v;
        if (zeta < 0.999)
        {
            double wd = w0 * Math.Sqrt(1 - zeta * zeta);
            double decay = Math.Exp(-zeta * w0 * dt);
            double c = Math.Cos(wd * dt), s = Math.Sin(wd * dt);
            double b = (v0 + zeta * w0 * x0) / wd;
            x = decay * (x0 * c + b * s);
            v = decay * ((b * wd - zeta * w0 * x0) * c - (x0 * wd + zeta * w0 * b) * s);
        }
        else
        {
            double decay = Math.Exp(-w0 * dt);
            double b = v0 + w0 * x0;
            x = decay * (x0 + b * dt);
            v = decay * (b - w0 * (x0 + b * dt));
        }
        Value = Target + x;
        Velocity = v;
    }
}

/// Calls `OnFrame` with the frame time on every display refresh (WPF's CompositionTarget.Rendering, which follows the
/// monitor's refresh rate) until it returns false. Never animate with timers or fixed tweens; drive springs from this.
sealed class FrameDriver
{
    /// Return true to keep animating.
    public Func<double, bool>? OnFrame { get; set; }

    TimeSpan last = TimeSpan.Zero;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        last = TimeSpan.Zero;
        CompositionTarget.Rendering += Tick;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        CompositionTarget.Rendering -= Tick;
    }

    void Tick(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; only a new frame time advances.
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        if (now == last) return;
        // The first frame and long stalls advance by one nominal frame, so nothing jumps.
        double dt = last == TimeSpan.Zero ? 1.0 / 60 : Math.Min(Math.Max((now - last).TotalSeconds, 1.0 / 240), 1.0 / 30);
        last = now;
        if (OnFrame?.Invoke(dt) != true) Stop();
    }
}

/// Shared rules for motion: honour "Animation effects" off (Settings › Accessibility › Visual effects) everywhere.
static class Motion
{
    public static bool Reduced => Native.AnimationsOff;
}

static class Mix
{
    /// Linear interpolation from a to b by t.
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// Blends two colours in sRGB.
    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte ch(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(ch(a.A, b.A), ch(a.R, b.R), ch(a.G, b.G), ch(a.B, b.B));
    }
}
