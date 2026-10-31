using System;
using System.Linq;

namespace Swivel.Core;

public enum MenuTemplate { Wheel, List, Bookshelf, Notes, Clothesline }
public sealed record MenuTile(double X,double Y,double Width,double Height,double Angle = 0);
public static class MenuGeometry
{
    public static int HitTest(MenuTemplate template, int count, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return -1;
        var tiles = Layout(template, count);
        for (int i = tiles.Length - 1; i >= 0; i--)
        {
            var tile = tiles[i]; double dx = x - tile.X - tile.Width / 2, dy = y - tile.Y - tile.Height / 2;
            double angle = -tile.Angle * Math.PI / 180;
            double localX = dx * Math.Cos(angle) - dy * Math.Sin(angle), localY = dx * Math.Sin(angle) + dy * Math.Cos(angle);
            if (Math.Abs(localX) <= tile.Width / 2 && Math.Abs(localY) <= tile.Height / 2) return i;
        }
        return -1;
    }
    public static MenuTile[] Layout(MenuTemplate template,int count)
    {
        if (!Enum.IsDefined(template) || count is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(count));
        return Enumerable.Range(0,count).Select(i => template switch {
            MenuTemplate.Wheel => new MenuTile(310 + 215 * Math.Cos(-Math.PI / 2 + i * Math.PI / 4) - 68,225 + 180 * Math.Sin(-Math.PI / 2 + i * Math.PI / 4) - 32,136,65),
            MenuTemplate.List => new MenuTile(110,15 + i * 49,400,42),
            MenuTemplate.Bookshelf => new MenuTile(16 + i % 4 * 150,40 + i / 4 * 200,132,158),
            MenuTemplate.Notes => new MenuTile(16 + i % 4 * 150,30 + i / 4 * 200,132,150,i % 2 == 0 ? -2 : 2),
            _ => new MenuTile(18 + i % 4 * 150,68 + i / 4 * 200,128,132,i % 2 == 0 ? -2 : 2)
        }).ToArray();
    }
}

/// Analytic damped spring. Retargeting preserves current position and velocity.
public sealed class MotionSpring
{
    public double Value { get; private set; }
    public double Velocity { get; private set; }
    private double target;
    public double Target { get => target; set { if (!double.IsFinite(value)) throw new ArgumentException("Finite target required."); target = value; } }
    private readonly double omega, damping;
    public MotionSpring(double value,double response = .24,double damping = .8)
    {
        if (!double.IsFinite(value) || !double.IsFinite(response) || response is < .05 or > 2 || !double.IsFinite(damping) || damping is < .6 or > 1) throw new ArgumentOutOfRangeException(nameof(response));
        Value = Target = value; omega = 2 * Math.PI / response; this.damping = damping;
    }
    public bool Settled => Math.Abs(Value - Target) < .0001 && Math.Abs(Velocity) < .0001;
    public void Snap() { Value = Target; Velocity = 0; }
    /// Adds velocity (units per second) without moving the value: a push, not a jump.
    public void Nudge(double velocity) { if (!double.IsFinite(velocity)) throw new ArgumentException("Finite velocity required."); Velocity += velocity; }
    /// Snaps once the motion is below what anyone can see, so a frame loop can stop.
    public bool SettleWithin(double position,double velocity)
    {
        if (Math.Abs(Value - Target) < position && Math.Abs(Velocity) < velocity) Snap();
        return Value == Target && Velocity == 0;
    }
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds == 0) return;
        double y = Value - Target, v = Velocity, decay = Math.Exp(-damping * omega * seconds);
        if (damping == 1)
        {
            double j = v + omega * y;
            Value = Target + (y + j * seconds) * decay; Velocity = (v - omega * j * seconds) * decay;
        }
        else
        {
            double frequency = omega * Math.Sqrt(1 - damping * damping), angle = frequency * seconds;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            Value = Target + decay * (y * cosine + (v + damping * omega * y) / frequency * sine);
            Velocity = decay * (v * cosine - (damping * omega * v + omega * omega * y) / frequency * sine);
        }
        if (Settled) Snap();
    }
}
