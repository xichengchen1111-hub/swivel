using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Swivel.Windows;

public sealed class TextCaptureDocument:IDisposable
{
    public const string Scope="Recognize text locally with an installed Windows OCR language, then review and edit it before explicitly copying. No clipboard reading, microphone, network, automatic recognition or resource download. Text accuracy depends on the image and language. Transparent pixels are composited over white; the original stays unchanged.";
    public const string Unsupported="No suitable installed Windows OCR language is available, or this image exceeds the system OCR dimension limit. Resize or crop the image first; no automatic downsampling or language download is performed.";
    public const string TooLarge="This image is wider or taller than Windows OCR accepts. Resize or crop it first; no automatic downsampling is performed.";
    private readonly ImageTools.Document input;private readonly PixelImage pixels;
    public string Path=>input.Input.Path;
    public string[] Languages{get;}
    private TextCaptureDocument(ImageTools.Document input,PixelImage pixels,string[] languages){this.input=input;this.pixels=pixels;Languages=languages;}
    public static TextCaptureDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();NativeIdentity.Require();var input=ImageTools.OpenForReading(path,token);
        try
        {
            var pixels=ImageTools.ReadPixels(input.Bitmap,0,token);if(pixels.Width>OcrEngine.MaxImageDimension||pixels.Height>OcrEngine.MaxImageDimension)throw new UserError(TooLarge);
            var languages=InstalledLanguages();if(languages.Length==0)throw new UserError(Unsupported);
            token.ThrowIfCancellationRequested();return new(input,OcrText.White(pixels),languages);
        }
        catch{input.Dispose();throw;}
    }
    /// The installed OCR language tags (at most 128), or none when there are more.
    internal static string[] InstalledLanguages(){var languages=OcrEngine.AvailableRecognizerLanguages.Select(x=>x.LanguageTag).Take(129).ToArray();return languages.Length>128?Array.Empty<string>():languages;}
    /// `language` is one of Languages, or null for the user's profile languages (agent tools).
    public async Task<string> RecognizeAsync(string? language,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(language is not null&&!Languages.Contains(language,StringComparer.OrdinalIgnoreCase))throw new UserError(Unsupported);
        var engine=language is null?OcrEngine.TryCreateFromUserProfileLanguages():OcrEngine.TryCreateFromLanguage(new Language(language));
        if(engine is null)throw new UserError(language is null?"None of the languages in the Windows language settings has OCR installed. Pass one of the installed OCR languages: "+string.Join(", ",Languages)+".":Unsupported);
        using var bitmap=new SoftwareBitmap(BitmapPixelFormat.Bgra8,pixels.Width,pixels.Height,BitmapAlphaMode.Ignore);bitmap.CopyFromBuffer(pixels.Bgra.AsBuffer());token.ThrowIfCancellationRequested();
        var result=await NativeAsync.Join(engine.RecognizeAsync(bitmap),token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return OcrText.Validate(result.Text);
    }
    public void Dispose()=>input.Dispose();
}
