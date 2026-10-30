using System;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class FilePreviewChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs=new Prefs();var action=Catalog.Actions.Single(x=>x.Id=="file.preview");
        check(action.Feature is null&&action.SingleFile&&action.RequiresFiles&&!action.ImageOnly,"Explicit preview is independent, single-file and non-image");
        check(action.Extensions!.Order().SequenceEqual(FilePreview.Extensions.Order()),"Preview accepts the closed set of existing readers");
        foreach(string extension in FilePreview.Extensions)foreach(string suffix in new[]{extension,extension.ToUpperInvariant()})
        {
            check(Catalog.Available(prefs,new[]{"chosen."+suffix}).Any(x=>x.Id==action.Id),"Supported explicit preview extension is offered");
            check(!Catalog.Available(prefs,new[]{"a."+suffix,"b."+suffix}).Any(x=>x.Id==action.Id),"Multiple references never trigger bulk preview reads");
        }
        foreach(string extension in new[]{"ico","pdf","exe","ppt","mp4","ogg","yaml","zip","lnk",""})
            check(!Catalog.Available(prefs,new[]{"chosen."+extension}).Any(x=>x.Id==action.Id),"Unsupported/non-document types have no generic non-image preview");
        foreach (string extension in FilePreview.Extensions)
        {
            check(FilePreview.Kind("chosen."+extension.ToUpperInvariant()) != FilePreviewKind.Unsupported, "Every offered extension has a preview route");
            check(FilePreview.Kind(@"C:\folder\chosen."+extension) == FilePreview.Kind("chosen."+extension), "Preview routing uses final extension on Mac and Windows");
        }
        foreach (string ext in FilePreview.ImageExtensions)
            check(FilePreview.Kind("a."+ext)==FilePreviewKind.Image && Catalog.IsImage("a."+ext), "Image preview routes only catalogued built-in readers");
        foreach (string ext in FilePreview.LocalAudioExtensions) check(FilePreview.Kind("a."+ext)==FilePreviewKind.LocalAudio,"Local audio preparation routing");
        foreach (string ext in FilePreview.DecodedAudioExtensions) check(FilePreview.Kind("a."+ext)==FilePreviewKind.DecodedAudio,"Windows audio decode routing");
        foreach (string ext in SubtitleFormats.InputExtensions) check(FilePreview.Kind("a."+ext)==FilePreviewKind.Subtitle,"Strict subtitle reader takes priority over text");
        foreach (string ext in DataFileFormats.InputExtensions) check(FilePreview.Kind("a."+ext)==FilePreviewKind.Data,"Strict data reader takes priority over text");
        check(FilePreview.Kind("a.DOCX")==FilePreviewKind.Document&&FilePreview.Kind("a.ico")==FilePreviewKind.Unsupported,"Documents retain their reader; no ICO decoder invented");
        string srt="1\r\n00:00:00,000 --> 00:00:01,000\r\n中文 😀\r\n";
        check(FilePreview.ReadText(Encoding.UTF8.GetBytes(srt),"a.SRT",default)==srt.Replace("\r\n","\n"),"Subtitle source shown through existing strict decoding");
        foreach (string ext in new[]{"srt","csv","json"}) reject(()=>FilePreview.ReadText(new byte[]{0xff},"a."+ext,default),"Invalid UTF8 refused by existing readers");
        string csv="name,value\r\n中文,😀\r\n";
        check(FilePreview.ReadText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(csv)).ToArray(),"a.csv",default)==csv,"BOM UTF16 data preserves source text");
        check(FilePreview.ReadText(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(srt)).ToArray(),"a.srt",default)==srt.Replace("\r\n","\n"),"BOM UTF16BE subtitle preview");
        foreach (var sample in new[]{("json","{bad}"),("jsonl","{}\ninvalid"),("srt","no timed cues"),("csv","\"unclosed"),("plist","<plist>bad</plist>")})
            reject(()=>FilePreview.ReadText(Encoding.UTF8.GetBytes(sample.Item2),"a."+sample.Item1,default),"Preview enforces existing format validation");
        foreach (var sample in new[]{("json","{\"x\":1}"),("jsonl","{}\n[]"),("ndjson","{}\n[]"),("tsv","a\tb\r\nx\ty"),("plist","<plist version='1.0'><string>中文</string></plist>")})
            check(FilePreview.ReadText(Encoding.UTF8.GetBytes(sample.Item2),"a."+sample.Item1,default)==sample.Item2,"Text-like preview preserves validated source markup");
        var binary = Encoding.ASCII.GetBytes("bplist00").Concat(new byte[]{0x09,8}).Concat(new byte[32]).ToArray();
        int trailer=binary.Length-32;binary[trailer+6]=1;binary[trailer+7]=1;binary[trailer+15]=1;binary[trailer+31]=9;
        check(FilePreview.ReadText(binary,"a.plist",default)==Encoding.UTF8.GetString(DataFileFormats.Write(DataFileFormats.Read(binary,"plist"),"yaml")),"Binary plist uses existing in-memory YAML conversion");
        reject(()=>FilePreview.ReadText(new byte[SubtitleFormats.MaxBytes+1],"a.srt",default),"Subtitle source budget retained");
        reject(()=>FilePreview.ReadText(new byte[DataFileFormats.MaxBytes+1],"a.json",default),"Data source budget retained");
        reject(()=>FilePreview.ReadText(Array.Empty<byte>(),"a.json",new CancellationToken(true)),"Preview reader observes pre-cancellation");
        reject(()=>FilePreview.ReadText(Array.Empty<byte>(),"a.exe",default),"Text preview cannot interpret unrelated formats");
        check(Strings.Table.ContainsKey(FilePreview.TextNotice),"Preview format and budget notice translated");
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id),"No-file preview cannot be offered");
        prefs.SetCustomActions(new[]{action.Id});prefs.Set(Feature.MyWheel,true);var restored=Prefs.Parse(prefs.Serialize());
        check(restored.CustomActionIds.SequenceEqual(new[]{action.Id})&&Catalog.ForCustomWheel(restored,new[]{"explicit.txt"}).Single().Id==action.Id,"Preview has a stable optional My Wheel action identity");
        reject(()=>prefs.SetCustomActions(new[]{"file.preview.unknown"}),"Preview uses the existing closed action schema");
    }
}
