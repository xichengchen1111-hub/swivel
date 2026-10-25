using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderSizeNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        check(Marshal.OffsetOf<DirectoryLayout>(nameof(DirectoryLayout.FileName)).ToInt32()==68,"FILE_FULL_DIR_INFO filename is at documented byte offset on this architecture");
        string folder=Path.Combine(directory,"folder-map");Directory.CreateDirectory(folder);
        string child=Path.Combine(folder,"child");Directory.CreateDirectory(child);
        string first=Path.Combine(folder,"first.bin"),second=Path.Combine(child,"second.bin");
        File.WriteAllBytes(first,new byte[120]);File.WriteAllBytes(second,new byte[330]);
        using(var blocked=new FileStream(first,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        using(var metadata=NativeFiles.MetadataDirectory.Open(folder))
        {
            var result=FolderSizes.Scan(metadata,default);
            check(result.Root.Bytes==450 && result.Root.Files==2 && !result.Root.Incomplete,"Native metadata scan sums exact logical lengths even when a file denies all opens");
            reject(()=>Directory.Move(folder,folder+" moved"),"Selected directory and ancestors remain pinned during scan lifetime");
            var app=Application.Current as App ?? new App();var window=new FolderMapDialog(app,folder,result);
            check(window.MinWidth==540 && window.MinHeight==480 && window.Content is System.Windows.Controls.DockPanel,"Folder map has native scrolling content with fixed footer");
            check(window.CurrentReport.Contains("first.bin") && window.CurrentReport.Contains("child") && window.CurrentReport.Contains(folder) && !window.CurrentReport.Contains("second.bin"),"Copy source includes only current view, explicit folder path and displayed names");
            window.Close();
        }
        check(new FileInfo(first).Length==120 && new FileInfo(second).Length==330,"Read-only folder measurement preserves both files");
        string system=Path.Combine(folder,"system.bin");File.WriteAllBytes(system,new byte[700]);File.SetAttributes(system,FileAttributes.System);
        try
        {
            using var metadata=NativeFiles.MetadataDirectory.Open(folder);var result=FolderSizes.Scan(metadata,default);
            check(result.Root.Bytes==450 && result.Root.Incomplete && result.Root.Skipped==1 && result.Root.Children.Single(x=>x.Name=="system.bin").Issue is not null,"Native system entry is skipped and retains a visible partial reason");
        }
        finally{File.SetAttributes(system,FileAttributes.Normal);}
        using(var metadata=NativeFiles.MetadataDirectory.Open(folder))
        {
            reject(()=>FolderSizes.Scan(metadata,new CancellationToken(true)),"Native folder pre-cancellation");
            var limited=FolderSizes.Scan(metadata,default,new(MaxEntries:1));check(limited.EntryLimit && limited.Root.Incomplete,"Native entry limit returns explicit partial results");
        }
        string outside=Path.Combine(directory,"folder-map-outside");Directory.CreateDirectory(outside);File.WriteAllBytes(Path.Combine(outside,"not-counted"),new byte[900]);
        string link=Path.Combine(folder,"link");bool linked=false;
        try{Directory.CreateSymbolicLink(link,outside);linked=true;}catch(Exception ex)when(ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception){Console.WriteLine("SKIP: folder-map symlink fixture needs Windows policy permission.");}
        if(linked)
        {
            using(var metadata=NativeFiles.MetadataDirectory.Open(folder))
            {
                var result=FolderSizes.Scan(metadata,default);check(result.Root.Bytes==1150 && result.Root.Skipped==1 && result.Root.Incomplete,"Directory symlink target bytes are never followed or counted");
            }
            reject(()=>NativeFiles.MetadataDirectory.Open(link).Dispose(),"Explicit symlink root is refused");Directory.Delete(link);
        }
        reject(()=>NativeFiles.MetadataDirectory.Open(@"\\server\share\folder").Dispose(),"Network roots rejected before any native lookup");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All metadata handles released after scan/window lifetime");
    }
    [StructLayout(LayoutKind.Sequential)]private struct DirectoryLayout
    {
        public uint NextEntryOffset,FileIndex;public long Creation,Access,Write,Change,EndOfFile,Allocation;public uint Attributes,NameBytes,EaSize;public ushort FileName;
    }
}
