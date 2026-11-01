using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

// Bounds refer to the source raster and are estimates for sampled reads.
// QR metadata remains the exact original record for a strict single-code image.
public sealed record ImageCodeResult(string Type,string Text,int X,int Y,int Width,int Height,QrReadResult? Qr=null,int ClockwiseOrientationDegrees=0);

public static class ImageCodeRead
{
    public const int MaxCodes=16;
    public const string Scope="Reads up to 16 codes per image (16 megapixels maximum), ordered by their top edge then left edge. Separate symbols with identical text remain separate results. Exact single QR images keep the strict flat result; other QR codes use the photo reader and error correction. Linear reading supports dark-on-light EAN-13, UPC-A, EAN-8, Code 128, Code 39 and ITF at 0°, 90°, 180° or 270°, with complete quiet zones, check digits and agreement across multiple scan lines. UPC-A is reported for EAN-13 with a leading zero. Code 39 requires a Mod-43 check character and at least six payload characters; the check character is not returned. ITF requires at least eight digits including its Mod-10 check digit, which is returned. Linear payloads are limited to 128 characters. Code 128 FNC1 becomes a group separator except at the start, where it is omitted; GS1 fields are not interpreted. FNC2/3/4 and extended Code 39 are not decoded. At most 512 scan lines per axis are sampled; very short bars may be missed and extremely close identical symbols may merge. Small, cropped, oblique, curved or heavily blurred symbols may be missed. Finder, scan-line, candidate and time budgets may omit additional codes; an empty result does not prove none exist. Data Matrix, PDF417 and Aztec are not supported. Nothing is opened automatically, and decoded text does not authenticate its source.";
    public const string NoCode="No supported code was found. Try a clearer image with complete QR corner squares or complete barcode bars and quiet zones. Code 39 and ITF also need their check digits and minimum lengths.";

    public static IReadOnlyList<ImageCodeResult> Read(PixelImage image,CancellationToken token)
    {
        var qr=QrPhotoRead.ReadAll(image,token);
        if(qr.Count==1&&qr[0].Qr is{Photo:false})return qr;
        token.ThrowIfCancellationRequested();
        var linear=LinearBarcodeRead.Read(image,token);
        token.ThrowIfCancellationRequested();
        return qr.Concat(linear.Select(c=>new ImageCodeResult(c.Type,c.Text,c.X,c.Y,c.Width,c.Height,null,c.ClockwiseOrientationDegrees)))
            .OrderBy(c=>c.Y).ThenBy(c=>c.X).ThenBy(c=>c.Type,StringComparer.Ordinal).Take(MaxCodes).ToArray();
    }
}
