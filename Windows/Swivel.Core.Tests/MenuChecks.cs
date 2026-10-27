using System;
using Swivel.Core;

internal static class MenuChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        foreach (var template in Enum.GetValues<MenuTemplate>())
            for (int count = 0; count <= 8; count++)
            {
                var tiles = MenuGeometry.Layout(template,count); check(tiles.Length == count,"Menu action count preserved");
                foreach (var t in tiles) check(t.X >= 0 && t.Y >= 0 && t.X + t.Width <= 620 && t.Y + t.Height <= 450,"Menu tile within canvas");
                for (int i = 0; i < tiles.Length; i++) for (int j = i + 1; j < tiles.Length; j++)
                { var a = tiles[i]; var b = tiles[j]; check(a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y,"Distinct menu hit targets"); }
            }
        reject(() => MenuGeometry.Layout((MenuTemplate)99,1),"Unknown template refused");
        reject(() => MenuGeometry.Layout(MenuTemplate.List,9),"Page limited to eight actions");
        var prefs = new Prefs(); prefs.SetMenuPresentation(MenuTemplate.Clothesline,false);
        var restored = Prefs.Parse(prefs.Serialize()); check(restored.MenuTemplate == MenuTemplate.Clothesline && !restored.AnimateMenus,"Style and reduced motion survive restart");
        var legacy = Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Wheel\"]}");
        check(legacy.MenuTemplate == MenuTemplate.Wheel && legacy.AnimateMenus,"Old settings retain default wheel");
        reject(() => Prefs.Parse(prefs.Serialize().Replace("\"Template\":4","\"Template\":99")),"Malformed saved template refused");
        foreach (int fps in new[] { 30,60,120,240 })
        {
            var spring = new MotionSpring(1); spring.Target = 1.025; double max = 1;
            for (int i = 0; i < fps * 1.5; i++) { spring.Advance(1d/fps); max = Math.Max(max,spring.Value); }
            check(spring.Settled && spring.Value == 1.025,"Menu spring settles at every display rate");
            check(max < 1.026,"Small menu overshoot");
        }
        var whole = new MotionSpring(0); var parts = new MotionSpring(0); whole.Target = parts.Target = 1;
        whole.Advance(.1); for (int i = 0; i < 10; i++) parts.Advance(.01);
        check(Math.Abs(whole.Value-parts.Value) < 1e-12 && Math.Abs(whole.Velocity-parts.Velocity) < 1e-12,"Frame independent analytic solution");
        double position = whole.Value, velocity = whole.Velocity; whole.Target = -.2;
        check(whole.Value == position && whole.Velocity == velocity,"Retargeting preserves position and velocity");
        whole.Advance(.01); check(double.IsFinite(whole.Value),"Retarget remains finite"); whole.Snap(); check(whole.Value == -.2 && whole.Velocity == 0,"Reduced motion exact target");
        var critical = new MotionSpring(0,damping:1); critical.Target = 1; double previous = 0;
        for (int i = 0; i < 90; i++) { critical.Advance(1d/60); check(critical.Value >= previous && critical.Value <= 1,"Critical spring has no overshoot"); previous = critical.Value; }
        reject(() => new MotionSpring(double.NaN),"Invalid position refused"); reject(() => new MotionSpring(0,damping:.5),"Underdamped motion refused"); reject(() => whole.Target = double.NaN,"Invalid target refused"); reject(() => whole.Advance(-1),"Negative frame time refused");
    }
}
