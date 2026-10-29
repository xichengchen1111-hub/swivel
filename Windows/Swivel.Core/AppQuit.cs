using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

/// One recorded process that may be asked to close: identity is the identifier, start time and
/// exact reported executable path from the App Memory snapshot.
public sealed record AppQuitTarget(uint Pid,ulong Created,string Path,bool Helper);
public enum AppQuitBlock { None,NoPath,Self,Essential,SystemFolder,OtherSession,Unverified }
/// Windows lists indices into the window session (main process first, then helpers).
/// Replaced counts listed windows with a recorded identifier whose start time or path differs:
/// the process was replaced, so they are skipped.
public sealed record AppQuitPlanApp(AppMemoryGroup Group,IReadOnlyList<AppQuitTarget> Targets,IReadOnlyList<int> Windows,IReadOnlyList<AppQuitTarget> WithoutWindows,int Replaced,int Unverified);
public sealed record AppQuitPlan(IReadOnlyList<AppQuitPlanApp> Apps,IReadOnlyList<AppMemoryGroup> Refused)
{
    public IReadOnlyList<int> Windows=>Apps.SelectMany(x=>x.Windows).ToArray();
    public IReadOnlyList<AppQuitTarget> Targets=>Apps.SelectMany(x=>x.Targets).ToArray();
}
public enum AppQuitState { Exited,Running,Unknown }
public interface IAppQuitProbe
{
    /// Running only when the identifier is still the same process: same start time and path.
    AppQuitState State(AppQuitTarget target);
}

public static class AppQuit
{
    public const int MaxApps=32;
    public const string Title="Quit Selected Apps…";
    public const string Scope="Tick apps in the group list to ask them to quit. Swivel sends the same normal Close request as Request Window Close… to every listed desktop window of the app's process and, with helper grouping on, of its attached helpers. It never ends a process by force: an app can ask to save, refuse, or keep running in the background or notification area. Windows, the shell, system processes, services, other sessions and Swivel itself are never offered; windows of apps running as administrator are not asked unless Swivel itself runs as administrator. Window titles in the review and results may contain private document names; nothing is copied or saved automatically.";
    public const string Confirmation="Ask these apps to quit? Swivel sends the normal Close request to each listed window below, one after another. Save your work first: an app may close without asking. Each window's process is checked again right before sending; a process that was replaced or has changed is skipped. Processes without a listed window are not asked and may keep running. Closing windows one by one is not always the same as quitting: an app with several windows may restore only the last one next time. After a short wait Swivel reports which processes are still running and does not force anything.";
    public const string Nothing="None of the selected apps has a listed desktop window that can be asked to close. No close request was sent.";
    public static readonly TimeSpan Wait=TimeSpan.FromSeconds(5);
    // Compared case-insensitively by file name, in addition to every executable in the Windows folder.
    private static readonly HashSet<string> Essential=new(StringComparer.OrdinalIgnoreCase)
    {
        "Swivel.exe","explorer.exe","dwm.exe","taskmgr.exe","csrss.exe","wininit.exe","winlogon.exe","services.exe","lsass.exe","lsaiso.exe","smss.exe","svchost.exe",
        "fontdrvhost.exe","sihost.exe","ShellExperienceHost.exe","StartMenuExperienceHost.exe","SearchHost.exe","SearchApp.exe","RuntimeBroker.exe",
        "ApplicationFrameHost.exe","conhost.exe","OpenConsole.exe","ctfmon.exe","dllhost.exe","taskhostw.exe","TextInputHost.exe","LockApp.exe","userinit.exe","audiodg.exe"
    };
    public static bool EssentialName(string path)=>Essential.Contains(path[(path.LastIndexOf('\\')+1)..]);
    private static bool InFolder(string path,string folder)=>folder.Length>0&&path.Length>folder.Length+1&&path[folder.Length]=='\\'&&path.StartsWith(folder,StringComparison.OrdinalIgnoreCase);
    private static AppQuitBlock Block(AppMemoryProcess process,uint self,uint session,string windows)
    {
        if(process.Pid is 0 or 4||process.Pid==self)return AppQuitBlock.Self;
        if(process.Path is null)return AppQuitBlock.NoPath;
        if(EssentialName(process.Path))return AppQuitBlock.Essential;
        if(InFolder(process.Path,windows))return AppQuitBlock.SystemFolder;
        if(process.Session is not uint known||process.Created is not ulong)return AppQuitBlock.Unverified;
        return known==0||known!=session?AppQuitBlock.OtherSession:AppQuitBlock.None;
    }
    /// Whether a group may be ticked. The app is its first process; a group that contains
    /// Swivel is never offered, even through a helper.
    public static AppQuitBlock Eligibility(AppMemoryGroup group,uint self,uint session,string windows)
    {
        ArgumentNullException.ThrowIfNull(group);ArgumentNullException.ThrowIfNull(windows);
        if(group.Processes.Count==0)return AppQuitBlock.NoPath;
        if(group.Processes.Any(x=>x.Pid==self))return AppQuitBlock.Self;
        return Block(group.Processes[0],self,session,windows.TrimEnd('\\'));
    }
    /// The processes of an offered group that may be asked; members that are themselves
    /// blocked are left out.
    public static IReadOnlyList<AppQuitTarget> Targets(AppMemoryGroup group,uint self,uint session,string windows)
    {
        if(Eligibility(group,self,session,windows)!=AppQuitBlock.None)return Array.Empty<AppQuitTarget>();
        string folder=windows.TrimEnd('\\');var helpers=new HashSet<uint>((group.Helpers??Array.Empty<AppMemoryHelper>()).Select(x=>x.Pid));
        return group.Processes.Where(x=>Block(x,self,session,folder)==AppQuitBlock.None)
            .Select(x=>new AppQuitTarget(x.Pid,x.Created!.Value,x.Path!,helpers.Contains(x.Pid))).OrderBy(x=>x.Helper?1:0).ToArray();
    }
    /// Matches the recorded window list against the ticked groups. A window is used only when its
    /// identifier, start time and exact path equal one target's; nothing is matched by name.
    public static AppQuitPlan Plan(IReadOnlyList<AppMemoryGroup> groups,IReadOnlyList<CloseWindowInfo> windows,uint self,uint session,string windowsFolder,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(groups);ArgumentNullException.ThrowIfNull(windows);ArgumentNullException.ThrowIfNull(windowsFolder);
        if(groups.Count>MaxApps)throw new UserError("Select at most 32 apps to quit at once.");
        var apps=new List<AppQuitPlanApp>();var refused=new List<AppMemoryGroup>();var claimed=new HashSet<uint>();
        foreach(var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var targets=Targets(group,self,session,windowsFolder).Where(x=>claimed.Add(x.Pid)).ToArray();
            if(targets.Length==0){refused.Add(group);continue;}
            var indices=new List<int>();var without=new List<AppQuitTarget>();int replaced=0;
            foreach(var target in targets)
            {
                bool any=false;
                for(int i=0;i<windows.Count;i++)
                {
                    var window=windows[i];if(window.ProcessId!=target.Pid)continue;
                    if(window.Created==target.Created&&string.Equals(window.ExecutablePath,target.Path,StringComparison.Ordinal)){indices.Add(i);any=true;}
                    else replaced++;
                }
                if(!any)without.Add(target);
            }
            apps.Add(new(group,targets,indices.AsReadOnly(),without.AsReadOnly(),replaced,group.Count-targets.Length));
        }
        return new(apps.AsReadOnly(),refused.AsReadOnly());
    }
    public static string Details(AppQuitPlan plan,IReadOnlyList<CloseWindowInfo> windows,Func<string,string> localize)
    {
        ArgumentNullException.ThrowIfNull(plan);ArgumentNullException.ThrowIfNull(windows);
        var lines=new List<string>();
        foreach(var app in plan.Apps)
        {
            lines.Add(AppMemory.Name(app.Group,localize));
            foreach(int index in app.Windows)
            {
                var window=windows[index];var target=app.Targets.First(x=>x.Pid==window.ProcessId);
                lines.Add("  "+localize(target.Helper?"Helper window":"Window")+": "+AppMemory.Visible(window.Title)+" · "+AppMemory.Visible(window.ExecutablePath[(window.ExecutablePath.LastIndexOf('\\')+1)..])+" · "+window.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            foreach(var target in app.WithoutWindows)
                lines.Add("  "+string.Format(localize("No listed window, not asked: {0} · {1}"),AppMemory.Visible(target.Path[(target.Path.LastIndexOf('\\')+1)..]),target.Pid.ToString(CultureInfo.InvariantCulture)));
            if(app.Replaced>0)lines.Add("  "+string.Format(localize("Windows skipped because their process was replaced: {0}"),app.Replaced));
            if(app.Unverified>0)lines.Add("  "+string.Format(localize("Processes left out because they are not offered: {0}"),app.Unverified));
        }
        foreach(var group in plan.Refused)lines.Add(string.Format(localize("Not offered, nothing sent: {0}"),AppMemory.Name(group,localize)));
        return string.Join(Environment.NewLine,lines);
    }
    public static IReadOnlyList<AppQuitState> Check(IReadOnlyList<AppQuitTarget> targets,IAppQuitProbe probe,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(targets);ArgumentNullException.ThrowIfNull(probe);
        var states=new AppQuitState[targets.Count];
        for(int i=0;i<targets.Count;i++)
        {
            token.ThrowIfCancellationRequested();
            try{states[i]=probe.State(targets[i]);}
            catch(Exception error)when(error is not OperationCanceledException){states[i]=AppQuitState.Unknown;}
        }
        return Array.AsReadOnly(states);
    }
    /// The final report: how many processes exited, and every one still running or unknown by name.
    /// `asked` holds the identifiers that were sent a request; others still running are listed as not asked.
    public static string Outcome(IReadOnlyList<AppQuitTarget> targets,IReadOnlyList<AppQuitState> states,Func<string,string> localize,IReadOnlySet<uint>? asked=null)
    {
        ArgumentNullException.ThrowIfNull(targets);ArgumentNullException.ThrowIfNull(states);
        if(targets.Count!=states.Count)throw new ArgumentException("Each process needs one state.",nameof(states));
        var lines=new List<string>{string.Format(localize("Processes that exited: {0}. Still running: {1}. Unknown: {2}. Nothing was forced to end."),
            states.Count(x=>x==AppQuitState.Exited),states.Count(x=>x==AppQuitState.Running),states.Count(x=>x==AppQuitState.Unknown))};
        for(int i=0;i<targets.Count;i++)
        {
            if(states[i]==AppQuitState.Exited)continue;
            var target=targets[i];
            lines.Add(localize(states[i]==AppQuitState.Unknown?"Unknown":asked is null||asked.Contains(target.Pid)?"Still running":"Not asked, still running")+" · "+AppMemory.Visible(target.Path[(target.Path.LastIndexOf('\\')+1)..])+" · "+target.Pid.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(Environment.NewLine,lines);
    }
}

/// Per-app CPU from the change in kernel plus user process time between two snapshots.
public static class AppCpu
{
    public const string Notice="CPU is the change in each process's kernel and user time between two snapshots, as a percentage of one logical processor; it can exceed 100% on several processors. A process needs two readings with the same start time; new, replaced or unreadable processes are not counted, and a group without any counted process shows Unknown.";
    /// Percent of one logical processor, or null when a reading is missing, the counter went
    /// backwards (reset or replaced process) or no time passed.
    public static double? Percent(ulong? before,ulong? after,TimeSpan elapsed)
    {
        if(before is not ulong a||after is not ulong b||b<a||elapsed.Ticks<=0)return null;
        return (double)((decimal)(b-a)*100m/elapsed.Ticks);
    }
    /// Sum over the group's processes found in the previous snapshot with the same identifier and
    /// start time. Unmeasured counts the rest.
    public static (double? Percent,int Unmeasured) Group(AppMemoryGroup group,AppMemorySnapshot? previous,TimeSpan elapsed)=>Group(group,previous is null?null:Index(previous),elapsed);
    /// Previous processes by identifier, built once per refresh for Group.
    public static IReadOnlyDictionary<uint,AppMemoryProcess> Index(AppMemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var before=new Dictionary<uint,AppMemoryProcess>();foreach(var process in snapshot.Processes)before.TryAdd(process.Pid,process);return before;
    }
    public static (double? Percent,int Unmeasured) Group(AppMemoryGroup group,IReadOnlyDictionary<uint,AppMemoryProcess>? before,TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(group);
        if(before is null)return (null,group.Count);
        double sum=0;int measured=0;
        foreach(var process in group.Processes)
        {
            if(process.Created is ulong created&&before.TryGetValue(process.Pid,out var old)&&old.Created==created&&Percent(old.Cpu,process.Cpu,elapsed) is double value){sum+=value;measured++;}
        }
        return (measured==0?null:sum,group.Count-measured);
    }
}
