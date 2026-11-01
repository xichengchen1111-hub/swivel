using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class MultiCodeReadNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T value)yield return value;
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach(var item in Find<T>(child))yield return item;
    }

    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        int count=0;void Verify(bool value,string message){check(value,message);count++;}
        var app=Application.Current as App??throw new InvalidOperationException("A validation App is required.");
        if(!app.IsValidationHost)throw new InvalidOperationException("Use an isolated validation host.");
        var qr1=QrFixtureEncoder.Render("FIRST 123");var qr2=QrFixtureEncoder.Render("SECOND 456");
        var barcode=LinearBarcodeReadChecks.Render("EAN-13","5901234123457");
        var pixels=LinearBarcodeReadChecks.Compose(650,340,(qr1,20,20),(qr2,300,20),(barcode,20,220));
        string folder=Path.Combine(directory,"multi-code-read");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"mixed.png");
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4)));encoder.Save(file);
        }
        var before=File.ReadAllBytes(path);
        using(var document=QrReadDocument.Open(path,default))
        {
            Verify(document.Results.Count==3&&document.Results.Select(r=>r.Type).SequenceEqual(new[]{"QR","QR","EAN-13"}),"Native WIC reader returns every independently encoded QR and barcode in source order");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"Multi-code source lease stays pinned");count++;
            reject(()=>Directory.Move(folder,folder+" moved"),"Multi-code ancestor lease stays pinned");count++;
            string language=app.Prefs.Language;
            try
            {
                foreach(string locale in new[]{"en","zh-Hans","zh-Hant","ja"})
                {
                    app.Prefs.SetLanguage(locale);var copied=new List<string>();
                    var values=document.Results.Concat(new[]{new ImageCodeResult("Code 128","a\0b\u202e",20,300,100,40)}).ToArray();
                    var dialog=new QrReadDialog(app,path,values,null,copied.Add);
                    try
                    {
                        var buttons=Find<Button>(dialog).ToArray();var copies=buttons.Where(b=>Equals(b.Content,app.L("Copy Decoded Text"))).ToArray();
                        Verify(dialog.Title==app.L("Read Codes from Image")&&buttons.Length==5&&copies.Length==4&&buttons.All(b=>!b.IsDefault)&&copied.Count==0,"Multi-code dialog has only independent explicit copies and Close, with no implicit action");
                        for(int i=0;i<3;i++)copies[i].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Verify(copied.SequenceEqual(values.Take(3).Select(r=>r.Text)),"Each native result button copies its own original payload");
                        copies[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Verify(!copies[3].IsEnabled&&copied.Count==3&&Find<TextBox>(dialog).Any(box=>box.Text=="a[U+0000]b[U+202E]"),"One NUL result disables only its own copy and displays controls visibly");
                        Verify(copies.Select(b=>AutomationProperties.GetName(b)).SequenceEqual(Enumerable.Range(1,4).Select(n=>string.Format(app.L("Copy Code {0}"),n))),"Every independent copy has a numbered translated accessibility name");
                        Verify(Find<TextBlock>(dialog).Any(t=>t.Text==app.L(ImageCodeRead.Scope)),"All-code dialog shows finite scope and unsupported formats in each language");
                    }
                    finally{dialog.Close();}
                    var single=new QrReadDialog(app,path,new[]{values[2]},null,copied.Add);
                    try{Verify(Find<Button>(single).Count()==2&&Find<TextBox>(single).Any(b=>b.Text==values[2].Text),"A single barcode has its own copy and complete read-only preview");}
                    finally{single.Close();}
                }
            }
            finally{app.Prefs.SetLanguage(language);}
        }
        var session=new McpSession(new McpHost(()=>true,"MultiCodeReadTest-"+Guid.NewGuid().ToString("N")),"test");
        var call=new JsonObject{["jsonrpc"]="2.0",["id"]=1,["method"]="tools/call",["params"]=new JsonObject{["name"]="read_codes",["arguments"]=new JsonObject{["paths"]=new JsonArray(path)}}};
        string response=session.Handle(call.ToJsonString())??throw new InvalidOperationException("Expected an MCP tool reply.");
        var reply=JsonNode.Parse(response)!["result"]!["structuredContent"]!;
        Verify(((JsonArray)reply["codes"]!).Count==3&&((JsonArray)reply["not_read"]!).Count==0&&(string?)reply["codes"]![2]!["type"]=="EAN-13", "Native MCP read_codes exposes every mixed-image result with its type");
        Verify(File.ReadAllBytes(path).SequenceEqual(before)&&Directory.GetFiles(folder).Length==1,"Mixed-image dialog and MCP reading never alter or create image files");
        reject(()=>QrReadDocument.Open(path,new CancellationToken(true)).Dispose(),"Precancelled native multi-code read stops before source acquisition");count++;
        Directory.Move(folder,folder+" released");Verify(Directory.Exists(folder+" released"),"All mixed-image source and ancestor leases are released after reading");
        Console.WriteLine($"Multi-code Windows-only checks: {count} (requires Windows execution)");
    }
}
