using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class StartupApprovalChecks
{
    private sealed class Source(Func<StartupApprovalLocation,StartupApprovalOpen> open):IStartupApprovalSource{public StartupApprovalOpen Open(StartupApprovalLocation l)=>open(l);}
    private sealed class Root(Func<uint,StartupApprovalRead> at):IStartupApprovalRoot
    {public bool Disposed;public int Calls;public StartupApprovalRead At(uint i){Calls++;return at(i);}public void Dispose()=>Disposed=true;}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var locations=StartupApprovalSnapshotReader.Locations();check(locations.Count==12&&locations.Distinct().Count()==12,"StartupApproved uses twelve fixed hive/view and category locations");
        check(locations.All(x=>Enum.IsDefined(x.Source)&&Enum.IsDefined(x.Area)),"StartupApproved location set is closed");
        var complete=StartupApprovalSnapshotReader.Scan(new Source(l=>new(new Root(i=>i==0?new(StartupApprovalReadKind.Item,"value",3,new byte[]{0,1,2,255}):new(StartupApprovalReadKind.End)),RegistrationOpenState.Open)),default);
        check(complete.Records.Count==12&&complete.Roots.Count==12&&!complete.Partial,"StartupApproved complete bounded snapshot");
        check(complete.Records.All(x=>x.HexData=="000102FF"&&x.Type==3),"StartupApproved retains exact raw bytes and registry type without decoding");
        string l(string k)=>Strings.L(k,"en");check(StartupApprovalSnapshotReader.Header(complete,l).Contains("not a count of enabled apps"),"StartupApproved report cautions no enabled-state claim");
        var root=new Root(i=>i switch{0=>new(StartupApprovalReadKind.Item,"Same",1,new byte[]{1}),1=>new(StartupApprovalReadKind.Item,"same",1,new byte[]{2}),2=>new(StartupApprovalReadKind.Item,"large",3,new byte[65]),3=>new(StartupApprovalReadKind.TooLarge),_=>new(StartupApprovalReadKind.End)});
        var special=StartupApprovalSnapshotReader.Scan(new Source(l=>l==locations[0]?new(root,RegistrationOpenState.Open):new(null,RegistrationOpenState.Missing)),default);
        check(special.Partial&&special.Records.Count==4&&special.Roots[0].Issues.HasFlag(StartupApprovalRootIssue.Duplicates)&&root.Disposed,"StartupApproved duplicate/oversize rows remain visible and native root is disposed");
        check(special.Records[0].HexData=="01"&&special.Records[1].Issues.HasFlag(StartupApprovalIssue.Duplicate)&&special.Records[2].HexData is null&&special.Records[3].HexData is null,"StartupApproved oversized data never exposes a partial prefix");
        var missing=StartupApprovalSnapshotReader.Scan(new Source(_=>new(null,RegistrationOpenState.Missing)),default);check(missing.Records.Count==0&&!missing.Partial&&missing.Roots.Count==12&&missing.Roots.All(x=>x.Issues==StartupApprovalRootIssue.Missing),"StartupApproved missing locations remain distinct from query errors");
        TimeSpan time=TimeSpan.Zero;int reads=0;var timed=StartupApprovalSnapshotReader.ScanWithClock(new Source(_=>{reads++;time=TimeSpan.FromSeconds(11);return new(null,RegistrationOpenState.Missing);}),()=>time,default);
        check(reads==1&&timed.Partial&&timed.Roots.Count==12&&timed.Roots.All(x=>x.Issues.HasFlag(StartupApprovalRootIssue.TimeLimit)),"StartupApproved deadline retains late result and marks unattempted locations");
        using(var cancel=new CancellationTokenSource()){var canceledRoot=new Root(_=>{cancel.Cancel();return new(StartupApprovalReadKind.End);});reject(()=>StartupApprovalSnapshotReader.Scan(new Source(_=>new(canceledRoot,RegistrationOpenState.Open)),cancel.Token),"StartupApproved cancellation propagates");check(canceledRoot.Disposed,"StartupApproved cancellation releases registry root");}
    }
}
