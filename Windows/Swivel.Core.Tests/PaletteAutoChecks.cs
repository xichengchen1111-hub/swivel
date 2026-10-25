using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class PaletteAutoChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs = new Prefs(); check(Enum.GetValues<MenuTemplate>().All(x => prefs.Palette(x) is null),"Menu colors default to system for every template");
        prefs.SetPalette(MenuTemplate.Wheel,MenuPalette.Ocean); prefs.SetPalette(MenuTemplate.Clothesline,MenuPalette.Night);
        var loaded = Prefs.Parse(prefs.Serialize()); check(loaded.Palette(MenuTemplate.Wheel) == MenuPalette.Ocean && loaded.Palette(MenuTemplate.Clothesline) == MenuPalette.Night && loaded.Palette(MenuTemplate.Notes) is null,"Menu palettes persist independently");
        loaded.SetPalette(MenuTemplate.Wheel,null); check(loaded.Palette(MenuTemplate.Wheel) is null && loaded.Palette(MenuTemplate.Clothesline) == MenuPalette.Night,"Reset affects one palette only");
        reject(() => prefs.SetPalette(MenuTemplate.List,new(0x1000000,0,0,0)),"Nonopaque RGB range refused"); reject(() => prefs.SetPalette((MenuTemplate)9,MenuPalette.Warm),"Invalid palette template refused");
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(prefs.Serialize())!; malformed["Palettes"]!.AsArray().RemoveAt(4);
        reject(() => Prefs.Parse(malformed.ToJsonString()),"Wrong saved palette count refused");
        check(Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Wheel\"]}").Palette(MenuTemplate.Wheel) is null,"Old settings retain system palette");
        double Luminance(uint color)
        {
            double Value(uint c) => c <= 10 ? c / 255d / 12.92 : Math.Pow((c / 255d + .055)/1.055,2.4);
            return .2126*Value(color>>16)+.7152*Value((color>>8)&255)+.0722*Value(color&255);
        }
        var random = new Random(1025); bool contrast = true;
        for (int i = 0; i < 2000; i++)
        {
            uint surface = (uint)random.Next(0x1000000); uint text = new MenuPalette(surface,0,0,0).TextColor;
            double a = Luminance(surface),b = Luminance(text); contrast &= (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05) >= 4.5;
        }
        check(contrast,"Automatic text meets 4.5 contrast across 2000 deterministic colors");
        check(new MenuPalette(0,0,0,0).TextColor == 0xffffff && new MenuPalette(0xffffff,0,0,0).TextColor == 0,"Black/white surfaces choose opposite text");
        var dark = new PixelImage(2,1,new byte[] { 25,25,25,255,25,25,25,255 }); var bright = new PixelImage(2,1,new byte[] { 240,240,240,255,240,240,240,255 });
        var settings = new PhotoSettings(Exposure:2,Contrast:.2,Highlights:.8,Shadows:-.4,Vibrance:-.5,Warmth:.3,Look:PhotoLook.Film,LookStrength:.4);
        var recipe = new ImageRecipe("png",Adjustments:settings,AutoPerImage:true);
        var aDark = recipe.ResolvePhoto(dark); var aBright = recipe.ResolvePhoto(bright);
        check(aDark.Exposure > 0 && aBright.Exposure < 0 && aDark.Shadows > aBright.Shadows,"Per-image Auto responds independently to different originals");
        check(aDark.Contrast == .2 && aDark.Warmth == .3 && aDark.Look == PhotoLook.Film && aDark.LookStrength == .4,"Auto retains other fixed values and look");
        check((recipe with { AutoPerImage = false }).ResolvePhoto(dark) == settings,"Switching Auto off preserves exact fixed values");
        byte darkResult = PhotoAdjust.Apply(dark,aDark,CancellationToken.None).Bgra[0]; byte brightResult = PhotoAdjust.Apply(bright,aBright,CancellationToken.None).Bgra[0];
        check(darkResult > 25 && brightResult < 240 && dark.Bgra[0] == 25 && bright.Bgra[0] == 240,"Auto lightens dark image and reduces bright image without modifying originals");
        string id = Guid.NewGuid().ToString("N"); prefs.SavePreset(new(id,"Auto",recipe)); var restored = Prefs.Parse(prefs.Serialize()).Presets.Single();
        check(restored.Id == id && restored.Recipe == recipe,"Per-image Auto survives settings round trip");
        check(!new ImageRecipe("png").AutoPerImage,"Legacy recipes never silently opt into Auto");
    }
}
