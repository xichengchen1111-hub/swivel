using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class TempCleanupChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var now=new DateTime(2026,10,27,12,0,0,DateTimeKind.Utc);long Ago(double days)=>now.AddDays(-days).ToFileTimeUtc();
        var tree=new Dir("root",new[]{
            new FolderEntry("old.tmp",false,100,0x20,Ago(30),Ago(30)),
            new FolderEntry("new.tmp",false,50,0x20,Ago(1),Ago(1)),
            new FolderEntry("undated.tmp",false,10,0x20,0,0),
            new FolderEntry("link",false,10,0x400,Ago(30),Ago(30)),
            new FolderEntry("oldDir",true,0,0x10,Ago(20),Ago(20)),
            new FolderEntry("touchedDir",true,0,0x10,Ago(20),Ago(20)),
            new FolderEntry("big.tmp",false,900,0x20,Ago(8),Ago(8)),
        },new Dictionary<string,Dir>{
            ["oldDir"]=new("oldDir",new[]{new FolderEntry("a",false,5,0x20,Ago(40),Ago(40)),new FolderEntry("b",false,6,0x20,Ago(10),Ago(10))}),
            ["touchedDir"]=new("touchedDir",new[]{new FolderEntry("fresh",false,7,0x20,Ago(2),Ago(2))}),
        });
        var scan=FolderSizes.Scan(tree,CancellationToken.None);
        check(scan.Root.Children.Single(c=>c.Name=="oldDir").Newest==Ago(10)&&scan.Root.Children.Single(c=>c.Name=="touchedDir").Newest==Ago(2)&&scan.Root.Children.Single(c=>c.Name=="old.tmp").LastWrite==Ago(30),"Folder scan records each item's last write and the newest write below a folder");
        var plan=TempCleanup.Plan(scan,now);
        check(plan.Old.Select(o=>o.Name).SequenceEqual(new[]{"big.tmp","old.tmp","oldDir"})&&plan.OldBytes==1011,"Only items whose own and inner writes are older than 7 days are listed, largest first");
        check(plan.Recent==2&&plan.NotListed==2,"Recently changed items (including a folder changed only inside) and skipped or undated items are not listed");
        check(plan.Cutoff==Ago(7)&&TempCleanup.IsOld(Ago(7.01),plan.Cutoff)&&!TempCleanup.IsOld(Ago(6.99),plan.Cutoff)&&!TempCleanup.IsOld(0,plan.Cutoff),"Seven-day cutoff is strict and an unknown time is never old");
        var partial=FolderSizes.Scan(new Dir("root",new[]{new FolderEntry("deep",true,0,0x10,Ago(30),Ago(30))},new Dictionary<string,Dir>{["deep"]=new("deep",Array.Empty<FolderEntry>(),fail:true)}),CancellationToken.None);
        check(TempCleanup.Plan(partial,now).Old.Count==0&&TempCleanup.Plan(partial,now).NotListed==1,"A folder that could not be measured completely is never listed");
        var buffer=new byte[80];BitConverter.GetBytes(Ago(20)).CopyTo(buffer,8);BitConverter.GetBytes(Ago(9)).CopyTo(buffer,24);BitConverter.GetBytes(Ago(3)).CopyTo(buffer,32);BitConverter.GetBytes(0x20u).CopyTo(buffer,56);BitConverter.GetBytes(2u).CopyTo(buffer,60);buffer[68]=(byte)'x';
        check(FolderDirectoryBuffer.Parse(buffer).Single().LastWrite==Ago(9)&&FolderDirectoryBuffer.Parse(buffer).Single().Touched==Ago(3),"FILE_FULL_DIR_INFO last write (offset 24) is read, and Touched is the latest of creation, last write and change");
        var copied=FolderSizes.Scan(new Dir("root",new[]{new FolderEntry("setup.exe",false,10,0x20,Ago(400),Ago(0.1)),new FolderEntry("unpacked",true,0,0x10,Ago(400),Ago(400))},new Dictionary<string,Dir>{["unpacked"]=new("unpacked",new[]{new FolderEntry("a.dll",false,1,0x20,Ago(400),Ago(0.1)),new FolderEntry("undated",false,1,0x20,0,0)})}),CancellationToken.None);
        var copiedPlan=TempCleanup.Plan(copied,now);check(copiedPlan.Old.Count==0&&copiedPlan.Recent==1&&copiedPlan.NotListed==1,"A file copied in today with an old modified date counts as recent; a folder with anything undated inside is never listed");
        reject(()=>TempCleanup.Plan(null!,now),"Missing scan refused");
    }
    private sealed class Dir(string name,FolderEntry[] entries,Dictionary<string,Dir>? children=null,bool fail=false):IFolderMetadata
    {
        public string Name=>name;
        public IEnumerable<FolderEntry> Entries(CancellationToken token){if(fail)throw new System.IO.IOException("refused");foreach(var e in entries)yield return e;}
        public IFolderMetadata OpenChild(string child)=>children?[child]??throw new System.IO.IOException();
        public void Dispose(){}
    }
}
