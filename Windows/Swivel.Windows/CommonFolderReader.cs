using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class CommonFolderReader
{
    internal static IReadOnlyList<CommonFolderChoice> Read(CancellationToken token)
    {
        var choices=new List<CommonFolderChoice>();
        foreach(var kind in CommonFolders.Kinds)
        {
            token.ThrowIfCancellationRequested();CommonFolderChoice choice;
            try
            {
                if(kind==CommonFolderKind.Downloads)choice=ReadDownloads(token);
                else
                {
                    var folder=kind switch{CommonFolderKind.Desktop=>Environment.SpecialFolder.DesktopDirectory,CommonFolderKind.Documents=>Environment.SpecialFolder.MyDocuments,CommonFolderKind.Pictures=>Environment.SpecialFolder.MyPictures,CommonFolderKind.Music=>Environment.SpecialFolder.MyMusic,CommonFolderKind.Videos=>Environment.SpecialFolder.MyVideos,_=>throw new ArgumentOutOfRangeException(nameof(kind))};
                    string path=Environment.GetFolderPath(folder,Environment.SpecialFolderOption.DoNotVerify);token.ThrowIfCancellationRequested();choice=CommonFolderChoice.FromReported(kind,path);
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
            {token.ThrowIfCancellationRequested();choice=CommonFolderChoice.Unavailable(kind,"Windows could not report this folder location.");}
            token.ThrowIfCancellationRequested();choices.Add(choice);
        }
        return choices.AsReadOnly();
    }
    private static CommonFolderChoice ReadDownloads(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();IntPtr pointer=IntPtr.Zero;bool initialized=false;
        try
        {
            int init=CoInitializeEx(IntPtr.Zero,2);initialized=init>=0;token.ThrowIfCancellationRequested();
            if(!initialized)return CommonFolderChoice.Unavailable(CommonFolderKind.Downloads,"COM initialization for folder lookup failed.",unchecked((uint)init));
            Guid id=CommonFolders.DownloadsId;int result=SHGetKnownFolderPath(in id,0x4000,IntPtr.Zero,out pointer);token.ThrowIfCancellationRequested();
            if(result!=0)return CommonFolderChoice.Unavailable(CommonFolderKind.Downloads,"Windows could not report this folder location.",unchecked((uint)result));
            if(pointer==IntPtr.Zero)return CommonFolderChoice.Unavailable(CommonFolderKind.Downloads,"Windows returned no path for this folder.");
            // The OS contract supplies an allocated NUL-terminated string. This
            // bounds copied text; it is not validation of arbitrary native pointers.
            var prefix=new StringBuilder(513);
            for(int i=0;i<=512;i++)
            {
                token.ThrowIfCancellationRequested();char c=(char)(ushort)Marshal.ReadInt16(pointer,i*2);
                if(c=='\0')return CommonFolderChoice.FromReported(CommonFolderKind.Downloads,prefix.ToString());prefix.Append(c);
            }
            return CommonFolderChoice.FromReported(CommonFolderKind.Downloads,prefix.ToString(),truncated:true);
        }
        finally
        {
            try{if(pointer!=IntPtr.Zero)CoTaskMemFree(pointer);}
            finally{if(initialized)CoUninitialize();}
        }
    }
    internal static FolderScanResult Measure(CommonFolderChoice choice,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string path=choice.Path??throw new UserError("Choose an available common folder first.");
        try{using var root=NativeFiles.MetadataDirectory.Open(path);var scan=FolderSizes.Scan(root,token);token.ThrowIfCancellationRequested();return scan;}
        catch{token.ThrowIfCancellationRequested();throw;}
    }
    [DllImport("ole32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern int CoInitializeEx(IntPtr reserved,uint mode);
    [DllImport("ole32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern void CoUninitialize();
    [DllImport("shell32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern int SHGetKnownFolderPath(in Guid id,uint flags,IntPtr token,out IntPtr path);
    [DllImport("ole32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern void CoTaskMemFree(IntPtr memory);
}

internal sealed class CommonFolderResult
{
    internal CommonFolderChoice Choice{get;}internal FolderScanResult? Scan{get;}internal string? Issue{get;}internal uint? Error{get;}internal DateTimeOffset Recorded{get;}
    internal CommonFolderResult(CommonFolderChoice choice,FolderScanResult? scan,string? issue=null,uint? error=null,DateTimeOffset? recorded=null)
    {
        if(!choice.CanAttempt||(scan is null)==(issue is null)||scan is not null&&error is not null)throw new ArgumentException("Invalid common folder result.");
        Choice=choice;Scan=scan;Issue=issue;Error=error;Recorded=recorded??DateTimeOffset.Now;
    }
}
