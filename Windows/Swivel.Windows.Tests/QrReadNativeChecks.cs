using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class QrReadNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"qr-read");Directory.CreateDirectory(folder);
        string text="https://example.com/?x=1&y=2 中文 😀",path=QrTools.Save(text,QrCorrection.High,folder,default);byte[] before=File.ReadAllBytes(path);
        using(var document=QrReadDocument.Open(path,default))
        {
            check(document.Result.Text==text&&document.Result.Correction==QrCorrection.High,"Native generated PNG can be explicitly read without opening decoded URL");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"QR read source stays pinned through result dialog");reject(()=>Directory.Move(folder,folder+" moved"),"QR source ancestors stay pinned through result dialog");
            var app=Application.Current as App??new App();var dialog=new QrReadDialog(app,document);var root=(DockPanel)dialog.Content;
            check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"QR dialog has only explicit Copy Text/Close without Open/Run/Save");dialog.Close();
        }
        check(File.ReadAllBytes(path).SequenceEqual(before)&&Directory.GetFiles(folder).Length==1,"QR reading creates no file and leaves original unchanged");
        reject(()=>QrReadDocument.Open(path,new CancellationToken(true)).Dispose(),"Precancelled QR read opens no image");
        string bad=Path.Combine(folder,"bad.png");File.WriteAllBytes(bad,new byte[16]);reject(()=>QrReadDocument.Open(bad,default).Dispose(),"Invalid QR image refuses and releases source");File.Move(bad,bad+" released");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"QR reader releases successful and failed source leases");
    }
}
