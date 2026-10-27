using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class AppRouteChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string path = Path.Combine(directory,"test application.exe"); var header = new byte[88];
        header[0] = 77; header[1] = 90; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60),64);
        header[64] = 80; header[65] = 69; BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68),0x8664); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(86),2);
        File.WriteAllBytes(path,header); string file = Path.Combine(directory,"selected & literal ; 中文.txt"); File.WriteAllText(file,"data");
        var route = new AppRoute(Guid.NewGuid().ToString("N"),"Test",path);
        // These are header-validation fixtures only. No process is ever launched by tests.
        using (var launch = AppLaunch.Open(route,new[] { file },CancellationToken.None))
        {
            reject(() => File.Move(path,path + ".moved"),"Chosen executable pinned through confirmation");
            var info = AppLaunch.StartInfo(path,new[] { file });
            check(!info.UseShellExecute && info.FileName == path && info.ArgumentList.SequenceEqual(new[] { file }) && info.Arguments == "", "Literal Unicode/metacharacter paths are individual arguments");
            check(info.WorkingDirectory == Environment.SystemDirectory,"No source-folder working directory");
            var copied = AppLaunch.StartInfo(path,new[] { file },true);
            check(!copied.UseShellExecute && copied.FileName==path && copied.ArgumentList.Count==0 && copied.Arguments=="" && copied.WorkingDirectory==Environment.SystemDirectory,"Clipboard delivery starts chosen app without file or shell arguments");
            reject(() => launch.Launch(new CancellationToken(true)),"Cancelled launch never starts a process");
        }
        foreach (ushort flags in new ushort[] { 0,0x2002 })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(86),flags); File.WriteAllBytes(path,header);
            reject(() => { using var input = NativeFiles.OpenRead(path); AppLaunch.ValidateExecutable(input); },"Refuse non-executable and DLL headers");
        }
        File.WriteAllText(path,"script or invalid PE");
        reject(() => AppLaunch.Open(route,new[] { file },CancellationToken.None).Dispose(),"Refuse non-PE executable");
        reject(() => AppLaunch.StartInfo(path,new[] { @"C:\a.txt:ads" }),"No ADS arguments");
        reject(() => AppLaunch.StartInfo(path,new[] { @"C:\a.txt:ads" },true),"Clipboard mode still validates every file path");
        check(File.ReadAllText(file) == "data","Routing validation leaves selected file untouched");
        var terminal = TerminalLaunch.StartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),Path.Combine(directory,"literal & %PATH% ; 中文"));
        check(!terminal.UseShellExecute && terminal.ArgumentList.SequenceEqual(new[] { "/D" }) && terminal.Arguments == "" && terminal.WorkingDirectory.EndsWith("literal & %PATH% ; 中文"),"Terminal uses only fixed AutoRun-disable argument; special directory characters remain data");
        reject(() => TerminalLaunch.Open(file,new CancellationToken(true)).Dispose(),"Precancelled terminal does not acquire source or launch process");
        check(Catalog.Available(new Prefs(),new[] { file }).Any(x => x.Id == "file.terminal") && !Catalog.Available(new Prefs(),new[] { file,path }).Any(x => x.Id == "file.terminal"),"Terminal targets exactly one supplied file");
    }
}
