using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class ExecutableStructureChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"exe-inspection");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"Header.EXE");byte[] data=new byte[1024];
        void U16(int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at),value);
        void U32(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at),value);
        data[0]=77;data[1]=90;U32(60,128);"PE\0\0"u8.CopyTo(data.AsSpan(128));U16(132,0xa641);U16(134,1);U16(148,240);U16(150,2);U16(152,0x20b);U16(152+68,2);U32(152+60,512);U32(152+108,16);U32(392+16,512);U32(392+20,512);File.WriteAllBytes(path,data);
        using(var source=ExecutableStructureFile.Open(path,default))
        {
            check(source.Info.Machine==0xa641&&source.Info.Pe32Plus&&source.Info.Certificates.State==PeCertificateState.NotDeclared,"Held EXE source returns header declarations without executing it");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"EXE source stays write-protected throughout report");reject(()=>Directory.Move(folder,folder+" moved"),"EXE ancestors stay pinned throughout report");
            var app=Application.Current as App??new App();var dialog=new ExecutableStructureDialog(app,source);
            var root=(DockPanel)dialog.Content;var footer=root.Children.OfType<WrapPanel>().Single();
            check(footer.Children.OfType<Button>().Count()==2&&dialog.CurrentReport.Contains("ARM64EC")&&dialog.CurrentReport.Contains("0xA641")&&dialog.CurrentReport.Contains(path),"Native EXE report has only explicit Copy/Close and retains path/numeric header code");
            check(dialog.CurrentReport.Contains(app.L(ExecutableStructureNotices.Certificates))&&dialog.CurrentReport.Contains(app.L(PeInspection.Limits)),"Certificate non-verification and source/read budgets stay in copied report");
            dialog.Close();
        }
        check(File.ReadAllBytes(path).SequenceEqual(data),"Header inspection never changes input bytes");
        reject(()=>ExecutableStructureFile.Open(path,new CancellationToken(true)).Dispose(),"Precancelled EXE inspection opens no source");
        string bad=Path.Combine(folder,"Bad.exe");File.WriteAllBytes(bad,new byte[100]);reject(()=>ExecutableStructureFile.Open(bad,default).Dispose(),"Malformed EXE refused before report");File.Move(bad,bad+" released");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"EXE success/error source and ancestor leases release");
    }
}
