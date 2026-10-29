using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class StorePackagesChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var a=new Fake{Name="zeta app",Publisher="Pub",Family="private-family_abc",Version=new(1,2,3,4),Arch=9,Installed=new DateTimeOffset(2026,1,2,3,4,0,TimeSpan.FromHours(8))};
        var b=new Fake{Name="Alpha‮App",Publisher=null,Family="other-family_x",Arch=12,Framework=true,Development=true};
        var c=new Fake{Name="  ",Publisher="P",Family="f",Arch=777,Resource=null};
        var list=new FakeList(new[]{a,b,c});var snap=StorePackages.Scan(new FakeSource(list),default);
        check(snap.Records.Count==3&&list.Disposed&&snap.Issues==PackageListIssue.None,"All packages recorded, enumerator disposed, list reached its end");
        check(snap.Records[0].Name.Value=="Alpha‮App"&&snap.Records[1].Name.Value=="zeta app"&&snap.Records[2].Name.State==PackageValueState.Empty,"Present names sort case-insensitively before empty names");
        check(snap.Records.Select(x=>x.Ordinal).OrderBy(x=>x).SequenceEqual(new[]{0,1,2}),"Enumeration positions retained");
        check(snap.Records[1].Version?.ToString()=="1.2.3.4"&&snap.Records[0].Version is null&&StorePackages.Version(null,x=>x)=="Not available from the package manager","Four-part version and unavailable version explicit");
        check(snap.Partial,"Unavailable publisher, version or flag makes the snapshot partial");
        string hidden=StorePackages.Report(snap,false,x=>x,default),shown=StorePackages.Report(snap,true,x=>x,default);
        check(!hidden.Contains("private-family_abc")&&!hidden.Contains("other-family_x")&&shown.Contains("private-family_abc"),"Package family names hidden by default, explicit opt-in only");
        check(hidden.Contains("Alpha[U+202E]App")&&!hidden.Contains('‮'),"Invisible reported text escaped");
        check(hidden.Contains(StorePackages.Scope)&&hidden.Contains(StorePackages.Limits)&&hidden.Contains(StorePackages.Privacy)&&hidden.Contains("includes frameworks and resource packages"),"Copies carry scope, limits and privacy");
        check(StorePackages.Architecture(9,x=>x)=="x64"&&StorePackages.Architecture(12,x=>x)=="ARM64"&&StorePackages.Architecture(0,x=>x)=="x86"&&StorePackages.Architecture(5,x=>x)=="ARM"&&StorePackages.Architecture(11,x=>x)=="Neutral"&&StorePackages.Architecture(14,x=>x)=="x86 on ARM64"&&StorePackages.Architecture(65535,x=>x)=="Unknown","Documented ProcessorArchitecture values named");
        check(StorePackages.Architecture(777,x=>x)=="Unrecognized architecture value 777"&&StorePackages.Architecture(null,x=>x)=="Not available from the package manager","Unknown raw architecture shown, not guessed");
        check(StorePackages.Kind(snap.Records[0],x=>x)=="Framework, Development (sideloaded)"&&StorePackages.Kind(snap.Records[1],x=>x)=="App package"&&StorePackages.Kind(snap.Records[2],x=>x).Contains("Resource: unknown"),"Kind flags, plain app and unknown flag explicit");
        check(StorePackages.Installed(a.Installed,x=>x)=="2026-01-01 19:04 UTC"&&StorePackages.Installed(null,x=>x)=="Not available from the package manager"&&StorePackages.Installed(new DateTimeOffset(1601,1,1,0,0,0,TimeSpan.Zero),x=>x)=="Not reported","Install date in UTC; missing or FILETIME-zero date not reported");
        check(StorePackages.Validate(new string('界',1024)).State==PackageValueState.Present&&StorePackages.Validate(new string('界',1025)).State==PackageValueState.TooLarge,"Text bound exact, never truncated");
        foreach(string bad in new[]{"a\0b","\ud800","x\udc00"})check(StorePackages.Validate(bad).State==PackageValueState.Invalid,"Malformed Unicode and NUL rejected whole");
        check(StorePackages.Validate(null).State==PackageValueState.Unavailable&&StorePackages.Validate("\t ").State==PackageValueState.Empty,"Unavailable and blank states distinct");
        foreach(var state in Enum.GetValues<PackageValueState>())check(!string.IsNullOrEmpty(StorePackages.Value(state==PackageValueState.Present?new("v",state):PackageText.Unknown(state),x=>x)),"Every value state labelled");
        var none=StorePackages.Scan(new FakeSource(null),default);
        check(none.Records.Count==0&&none.Issues==PackageListIssue.Unavailable&&none.Partial&&StorePackages.Status(none.Issues,x=>x).Contains("unavailable"),"Unavailable package manager is a status, not a crash");
        var broken=new FakeList(new[]{a,b}){FailAt=1};var stopped=StorePackages.Scan(new FakeSource(broken),default);
        check(stopped.Records.Count==1&&stopped.Issues==PackageListIssue.EnumerationFailed&&broken.Disposed,"Mid-list enumeration failure keeps earlier packages and marks partial");
        var many=new FakeList(Enumerable.Range(0,5000).Select(i=>new Fake{Name="p"+i,Publisher="x",Family="f",Version=new(1,0,0,0),Arch=9,Framework=false,Resource=false,Bundle=false,Development=false}).ToArray());
        var capped=StorePackages.Scan(new FakeSource(many),default);
        check(capped.Records.Count==StorePackages.MaxPackages&&capped.Issues==PackageListIssue.Limit&&many.Calls==StorePackages.MaxPackages,"4096 package cap stops enumeration before the next call");
        var exact=new FakeList(Enumerable.Range(0,4096).Select(_=>new Fake{Name="n"}).ToArray());check(StorePackages.Scan(new FakeSource(exact),default).Issues.HasFlag(PackageListIssue.Limit),"Exactly 4096 still reports the limit because the end was not observed");
        var time=TimeSpan.Zero;var slow=new FakeList(new[]{a,b,c}){OnNext=()=>time+=TimeSpan.FromSeconds(4)};
        var timed=StorePackages.ScanWithClock(new FakeSource(slow),()=>time,default);
        check(timed.Issues.HasFlag(PackageListIssue.TimeLimit)&&timed.Records.Count==3&&timed.Records.Count(x=>x.TimeLimited)==1&&slow.Calls==3&&timed.Partial,"Soft time limit stops further enumeration and keeps recorded packages");
        var fieldTime=TimeSpan.Zero;var midField=new Fake{Name="n",OnRead=()=>fieldTime+=TimeSpan.FromSeconds(5)};
        var fieldTimed=StorePackages.ScanWithClock(new FakeSource(new FakeList(new[]{midField,a})),()=>fieldTime,default);
        check(fieldTimed.Records.Count==1&&fieldTimed.Records[0].TimeLimited&&fieldTimed.Records[0].Family.State==PackageValueState.TimeLimit&&fieldTimed.Issues.HasFlag(PackageListIssue.TimeLimit),"Time limit inside one package marks remaining fields, never reads the next package");
        reject(()=>StorePackages.Scan(new FakeSource(new FakeList(new[]{a})),new CancellationToken(true)),"Precancelled scan refused before opening");
        using(var cts=new CancellationTokenSource())
        {
            var cancelling=new FakeList(new[]{a,b,c}){OnNext=cts.Cancel};bool threw=false;
            try{StorePackages.Scan(new FakeSource(cancelling),cts.Token);}catch(OperationCanceledException){threw=true;}
            check(threw&&cancelling.Disposed,"Cancellation during enumeration throws and disposes the enumerator");
        }
        var big=new FakeList(Enumerable.Range(0,4000).Select(i=>new Fake{Name=new string('x',1000)+i,Publisher=new string('y',1000)}).ToArray());
        var bigSnap=StorePackages.Scan(new FakeSource(big),default);reject(()=>StorePackages.Report(bigSnap,false,x=>x,default),"Report over 4 MiB refused whole");
        check(Encoding.UTF8.GetByteCount(StorePackages.Report(snap,true,x=>x,default))<StorePackages.MaxReportBytes,"Ordinary report within the bound");
        foreach(string text in new[]{StorePackages.Scope,StorePackages.Limits,StorePackages.Privacy,StorePackages.ReportLimit,InstalledApps.Packages,"Store and MSIX Packages (Current User)","Package snapshot is partial.","The current user's package list was read within the limits.",
            "Recorded packages: {0} (includes frameworks and resource packages)","Show Package Family Names","Package family names are included.","Package family names are hidden.","Reported Package Name","Package Version","Package Kind","Package Family Name","Installed","Architecture",
            "Neutral","x86 on ARM64","Unrecognized architecture value {0}","Framework","Resource","Bundle","Development (sideloaded)","{0}: unknown","App package","Not available from the package manager","Package text exceeds the length limit","Invalid package text",
            "The package manager is unavailable in this process; no packages were read","Package enumeration stopped with an error; the list may be incomplete","Package limit reached; the list may be incomplete","Package list not completed because of the time limit",
            "Package enumeration reached the end of the current user's list","Package not completed because of the time limit","No recorded package-read issue; inspect each reported field","Packages","Selected Package","Read Store and MSIX Packages…","Reading the current user's Store and MSIX packages…",
            "List text may be shortened. Select a package for complete reported text. Package family names are never used as a fallback name.","No packages were returned. This does not prove that no Store or MSIX apps are installed.","Select a package to inspect its reported fields.",
            "Reported text is empty","The package list could not be read. The installation records read earlier are kept.","Not queried because of the time limit","Reported Publisher","Hidden","Not reported","Unknown","Enumeration position: {0}"})
            check(Strings.Table.TryGetValue(text,out var row)&&row.Length==3&&row.All(t=>!text.Contains("{0}")||t.Contains("{0}")),"Package text translated: "+text[..Math.Min(40,text.Length)]);
    }
    private sealed class FakeSource(FakeList? list):IStorePackageSource{public IStorePackageList? Open()=>list;}
    private sealed class FakeList(Fake[] items):IStorePackageList
    {
        public int FailAt=-1,Calls;public bool Disposed;public Action? OnNext;
        public PackageNext Next(){OnNext?.Invoke();int at=Calls++;if(at==FailAt)return new(PackageNextKind.Unavailable);return at<items.Length?new(PackageNextKind.Item,items[at]):new(PackageNextKind.End);}
        public void Dispose()=>Disposed=true;
    }
    private sealed class Fake:IStorePackageEntry
    {
        public string? Name,Publisher,Family;public PackageVersion? Version;public uint? Arch;public bool? Framework=false,Resource=false,Bundle=false,Development=false;public DateTimeOffset? Installed;public Action? OnRead;
        public string? Text(PackageTextField field){OnRead?.Invoke();return field switch{PackageTextField.DisplayName=>Name,PackageTextField.PublisherDisplayName=>Publisher,_=>Family};}
        PackageVersion? IStorePackageEntry.Version()=>Version;
        public uint? Architecture()=>Arch;
        public bool? Flag(PackageFlag flag)=>flag switch{PackageFlag.Framework=>Framework,PackageFlag.Resource=>Resource,PackageFlag.Bundle=>Bundle,_=>Development};
        DateTimeOffset? IStorePackageEntry.Installed()=>Installed;
    }
}
