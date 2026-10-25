using System;

namespace Swivel.Core;

/// Four opaque sRGB colors per template. Text chooses whichever of black/white
/// gives higher contrast; Windows high-contrast mode overrides the palette in UI.
public sealed record MenuPalette(uint Surface,uint Outline,uint Rope,uint Peg)
{
    public void Validate()
    {
        if (Surface > 0xffffff || Outline > 0xffffff || Rope > 0xffffff || Peg > 0xffffff)
            throw new UserError("Choose opaque RGB menu colors.");
    }
    public uint TextColor
    {
        get
        {
            Validate();
            static double Linear(uint channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055,2.4); }
            double light = .2126*Linear(Surface>>16)+.7152*Linear((Surface>>8)&255)+.0722*Linear(Surface&255);
            return (light + .05) / .05 >= 1.05 / (light + .05) ? 0u : 0xffffffu;
        }
    }
    public static MenuPalette Warm => new(0xfff1d6,0xb79e76,0x8e7650,0xb5784e);
    public static MenuPalette Ocean => new(0xe3f1fa,0x7495ad,0x547c9c,0x3a759b);
    public static MenuPalette Night => new(0x28323e,0x8597aa,0x96abc0,0xd2a86f);
}
