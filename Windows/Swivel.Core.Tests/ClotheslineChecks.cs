using System;
using System.Linq;
using Swivel.Core;

internal static class ClotheslineChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.Clothesline),"Clothesline defaults off");
        check(!Catalog.Available(prefs,new[] { "photo.png" }).Any(x => x.Id == "clothesline.add"),"Disabled rope action absent");
        prefs.Set(Feature.Wheel,false); prefs.Set(Feature.Clothesline,true);
        check(Catalog.Available(prefs,new[] { "photo.png","photo.jpg" }).Any(x => x.Id == "clothesline.add"),"Clothesline independent of Wheel");
        check(!Catalog.Available(prefs,new[] { "photo.png","audio.wav" }).Any(x => x.Id == "clothesline.add"),"Rope only accepts all-image inputs");
        var existing = Enumerable.Range(0,11).Select(i => $"{i}.png").ToArray();
        check(ClotheslineRules.Additions(existing,new[] { "0.PNG","next.png","NEXT.PNG" }).SequenceEqual(new[] { "next.png" }),"Duplicate print paths are not counted twice");
        reject(() => ClotheslineRules.Additions(existing,new[] { "a.png","b.png" }),"Rope batch capacity checked before decoding");
        reject(() => ClotheslineRules.Additions(Array.Empty<string>(),new[] { "x.mp4" }),"Invalid print type rejected");
        check(ClotheslineRules.Additions(existing,Array.Empty<string>()).Length == 0,"Empty addition preserves prints");
        var layout = ClotheslineRules.Layout(12); check(layout.Length == 12,"All twelve prints visible");
        foreach (var tile in layout) check(tile.X > 0 && tile.Y > 0 && tile.X + tile.Width <= 1000 && tile.Y + tile.Height <= 400,"Print inside scalable canvas");
        for (int i = 0; i < layout.Length; i++) for (int j = i+1; j < layout.Length; j++) { var a = layout[i]; var b = layout[j]; check(a.X+a.Width <= b.X || b.X+b.Width <= a.X || a.Y+a.Height <= b.Y || b.Y+b.Height <= a.Y,"Print targets do not overlap"); }
        reject(() => ClotheslineRules.Layout(13),"Extra print refused");
        check(Prefs.Parse(prefs.Serialize()).IsOn(Feature.Clothesline),"Rope feature choice survives restart without storing image paths");
    }
}
