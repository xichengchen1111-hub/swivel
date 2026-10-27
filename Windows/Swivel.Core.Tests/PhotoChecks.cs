using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class PhotoChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] pixels={20,60,120,255, 80,80,80,255, 11,22,33,0, 180,120,40,128};
        var source=new PixelImage(2,2,pixels); var original=(byte[])pixels.Clone();
        var neutral=PhotoAdjust.Apply(source,new(),CancellationToken.None);
        check(neutral.Bgra.SequenceEqual(pixels) && !ReferenceEquals(neutral.Bgra,pixels),"Neutral photo edit preserves exact RGBA without aliasing");
        var bright=PhotoAdjust.Apply(source,new(Exposure:1),CancellationToken.None);
        check(bright.Bgra[0]>pixels[0] && bright.Bgra[2]>pixels[2],"Positive exposure lifts linear-light channels");
        var gray=PhotoAdjust.Apply(source,new(Saturation:-1),CancellationToken.None);
        check(gray.Bgra[0]==gray.Bgra[1] && gray.Bgra[1]==gray.Bgra[2],"Grayscale equalizes visible channels");
        var warm=PhotoAdjust.Apply(source,new(Warmth:1),CancellationToken.None);
        check(warm.Bgra[6]>pixels[6] && warm.Bgra[4]<pixels[4],"Warmth increases red and decreases blue on gray");
        foreach (var settings in new[] { new PhotoSettings(Shadows:1),new PhotoSettings(Highlights:-1),new PhotoSettings(Contrast:1,Whites:1,Blacks:-1,Vibrance:1,Tint:1,Clarity:1,Sharpen:1,Noise:1,Vignette:1) })
        {
            var result=PhotoAdjust.Apply(source,settings,CancellationToken.None);
            check(Enumerable.Range(0,4).All(i=>result.Bgra[i*4+3]==pixels[i*4+3]) && result.Bgra[8..12].SequenceEqual(pixels[8..12]),"Every alpha and fully transparent RGB remains untouched");
        }
        foreach(var look in Enum.GetValues<PhotoLook>())
        {
            var result=PhotoAdjust.Apply(source,new(Look:look,LookStrength:0),CancellationToken.None);
            check(result.Bgra.SequenceEqual(pixels),"Zero look strength is neutral: "+look);
        }
        check(PhotoAdjust.Apply(source,new(Look:PhotoLook.Mono),CancellationToken.None).Bgra[0]==gray.Bgra[0],"Mono look uses the shared color pipeline");
        check(pixels.SequenceEqual(original),"Photo edits never mutate source buffer");
        foreach(var invalid in new[] {new PhotoSettings(Exposure:double.NaN),new PhotoSettings(Exposure:4),new PhotoSettings(Sharpen:-1),new PhotoSettings(LookStrength:2),new PhotoSettings(Look:(PhotoLook)100)})
            reject(()=>PhotoAdjust.Apply(source,invalid,CancellationToken.None),"Invalid adjustment refused");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        reject(()=>PhotoAdjust.Apply(source,new(Noise:1),cancelled.Token),"Photo render cancellation before allocation");
        var transparent=new PixelImage(1,1,new byte[4]);
        check(PhotoAdjust.Auto(transparent)==new PhotoSettings(),"Auto ignores fully transparent inputs");
        PhotoAdjust.Auto(source).Validate();
        var thin=new PixelImage(1,4,pixels);check(PhotoAdjust.Apply(thin,new(Sharpen:1,Noise:.3),CancellationToken.None).Bgra.Length==16,"One-pixel-wide effects remain bounded");
    }
}
