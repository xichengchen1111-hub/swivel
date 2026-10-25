using System;
using System.Buffers;
using System.Text;

namespace Swivel.Core;

public static class OcrText
{
    public const int MaxUnits=262144;
    public const string Limit="OCR text is limited to 262,144 UTF-16 units, without null characters or invalid Unicode. Oversized results are refused without truncation.";
    public static string Validate(string text)
    {
        if(text.Length>MaxUnits||text.Contains('\0'))throw new UserError(Limit);
        for(int at=0;at<text.Length;){if(Rune.DecodeFromUtf16(text.AsSpan(at),out _,out int used)!=OperationStatus.Done)throw new UserError(Limit);at+=used;}return text;
    }
    public static PixelImage White(PixelImage image)
    {
        image.Validate();var bytes=new byte[image.Bgra.Length];
        for(int p=0;p<bytes.Length;p+=4){int alpha=image.Bgra[p+3];for(int c=0;c<3;c++)bytes[p+c]=(byte)((image.Bgra[p+c]*alpha+255*(255-alpha)+127)/255);bytes[p+3]=255;}return new(image.Width,image.Height,bytes);
    }
}
