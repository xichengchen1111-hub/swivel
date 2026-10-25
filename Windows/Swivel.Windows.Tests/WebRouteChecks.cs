using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class WebRouteChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        // Inspect preparation/start info only. Tests never open a browser, send
        // a request, read or write the clipboard, or save actual preferences.
        var route = new AppRoute(Guid.NewGuid().ToString("N"),"Page","",WebAddress:"https://example.com/中文?q=a+b&next=https%3A%2F%2Fexample.net%2F#test");
        using var page = WebLaunch.Prepare(route,new[] { @"\\server\unrelated-missing-file" },default);
        check(page.Files.Count==0 && page.Address==WebAddress.Normalize(route.WebAddress!),"Page-only route ignores unrelated selections without validating or reading them");
        var info = WebLaunch.StartInfo(page.Address);
        check(info.UseShellExecute && info.Verb=="open" && info.FileName==page.Address && info.Arguments=="" && info.ArgumentList.Count==0 && info.WorkingDirectory==Environment.SystemDirectory,"Default-browser request contains exactly the validated URL and no command/file arguments");
        reject(()=>page.Launch(new CancellationToken(true)),"Cancelled page-only launch cannot reach the operating-system opener");
        reject(()=>WebLaunch.StartInfo("file:///C:/Windows/notepad.exe"),"The page launcher refuses non-web schemes");
        reject(()=>WebLaunch.Prepare(route,Array.Empty<string>(),new CancellationToken(true)),"Cancelled page preparation refuses before any input validation");
        reject(()=>AppLaunch.Open(route,Array.Empty<string>(),default).Dispose(),"Web routes cannot use the executable file launcher");
        reject(()=>WebLaunch.Prepare(new(Guid.NewGuid().ToString("N"),"Local",@"C:\test.exe"),Array.Empty<string>(),default),"Local executable routes cannot use the web launcher");
        string selected=Path.Combine(directory,"web selected & literal 中文.txt"); File.WriteAllText(selected,"local fixture");
        var copyRoute=route with { CopyToClipboard=true,Extensions="txt" };var selectedList=new[]{selected};
        using var copy=WebLaunch.Prepare(copyRoute,selectedList,default); selectedList[0]=@"C:\other.txt";
        check(copy.Files.Single()==selected && copy.Address==page.Address && !copy.Address.Contains("selected"),"Reviewed file references are a snapshot and never added to the web address");
        check(File.ReadAllText(selected)=="local fixture","Preparing a web handoff never changes the selected file");
        reject(()=>WebLaunch.Prepare(copyRoute,Array.Empty<string>(),default),"Copy web route requires explicit local files");
        reject(()=>WebLaunch.Prepare(copyRoute,new[]{@"\\server\a.txt"},default),"Copy web route rejects network file references");
        reject(()=>WebLaunch.Prepare(copyRoute with { Extensions="png" },new[]{selected},default),"Copy web route enforces file extension filter");
        reject(()=>copy.Launch(new CancellationToken(true)),"Cancelled copy route cannot open the browser");
        File.Delete(selected);
        reject(()=>copy.Launch(default),"A vanished file reference is rechecked after preparation and prevents opening");

        string browserPath=Path.Combine(directory,"chosen browser & literal 中文.exe");var header=new byte[88];
        header[0]=77;header[1]=90;BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60),64);
        header[64]=80;header[65]=69;BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68),0x8664);BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(86),2);
        File.WriteAllBytes(browserPath,header);var browserRoute=route with { BrowserExecutable=browserPath };
        using(var chosen=WebLaunch.Prepare(browserRoute,Array.Empty<string>(),default))
        {
            reject(()=>File.Move(browserPath,browserPath+".moved"),"Selected browser executable remains pinned across review");
            var selectedInfo=WebLaunch.StartInfo(chosen.Address,browserPath);
            check(!selectedInfo.UseShellExecute && selectedInfo.FileName==browserPath && selectedInfo.WorkingDirectory==Environment.SystemDirectory && selectedInfo.Arguments=="" && selectedInfo.ArgumentList.SequenceEqual(new[]{page.Address}),"Selected browser receives exactly one normalized URL argument with no shell or file arguments");
            reject(()=>chosen.Launch(new CancellationToken(true)),"Cancelled selected-browser launch never creates a process");
        }
        File.Move(browserPath,browserPath+".moved");File.Move(browserPath+".moved",browserPath);
        using(var disposed=WebLaunch.Prepare(browserRoute,Array.Empty<string>(),default))
        { disposed.Dispose();reject(()=>disposed.Launch(default),"Disposed reviewed browser cannot launch without its executable lease"); }
        reject(()=>WebLaunch.Prepare(browserRoute,Array.Empty<string>(),new CancellationToken(true)).Dispose(),"Precancelled chosen browser never acquires executable");
        File.WriteAllText(browserPath,"not PE");
        reject(()=>WebLaunch.Prepare(browserRoute,Array.Empty<string>(),default).Dispose(),"Selected browser must pass executable validation");
        File.Delete(browserPath);check(!File.Exists(browserPath),"Failed selected-browser preparation releases all leases");
        reject(()=>WebLaunch.StartInfo(page.Address,@"C:\browser.cmd"),"Browser start-info rejects scripts");
        check(WebLaunch.StartInfo(page.Address,null).UseShellExecute,"Explicit null browser preserves system default opener");

        var app=Application.Current as App ?? new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var form=new WebRouteDialog(app,route);var confirm=new WebLaunchDialog(app,page);
        try
        {
            var nodes=Descendants(form).ToArray();
            var address=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Web Address"));
            var preview=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Address to Open"));
            var save=nodes.OfType<Button>().Single(x=>Equals(x.Content,app.L("Save Web Page")));
            check(address.MaxLength==0 && address.AcceptsReturn && preview.IsReadOnly && preview.Text==page.Address && save.IsEnabled,"Native form preserves pasted content and shows the canonical reviewed address without opening it");
            address.Text="https://example.com/"+new string('x',2048);
            check(address.Text.Length>2048 && preview.Text=="" && !save.IsEnabled,"Long URL is preserved and cannot silently save a truncated address");
            address.Text="https://example.com/\r\nnext";
            check(address.Text.Contains('\n') && !save.IsEnabled,"Pasted line breaks are retained and rejected instead of changing the destination");
            address.Text="http://localhost:8080/test";
            check(save.IsEnabled && preview.Text==address.Text,"Valid loopback address restores the native preview");
            var browserChoice=nodes.OfType<CheckBox>().Single(x=>Equals(x.Content,app.L("Use a selected browser application")));
            var browserField=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Browser Application"));
            check(browserField.IsReadOnly && !browserField.IsEnabled,"Default route has a disabled read-only browser path");
            browserChoice.IsChecked=true;check(browserField.IsEnabled,"Explicit browser choice enables its path field");
            browserChoice.IsChecked=false;
            var types=nodes.OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)==app.L("File extensions (optional)"));
            var delivery=nodes.OfType<CheckBox>().Single(x=>Equals(x.Content,app.L("Copy file references before opening")));check(!types.IsEnabled,"Page-only form has no active file filter");
            delivery.IsChecked=true;check(types.IsEnabled,"Explicit copying enables the file filter");
            var targets=Descendants(confirm).OfType<TextBox>().ToArray();
            check(targets.Length==2 && targets.All(x=>x.IsReadOnly) && targets.Any(x=>x.Text==page.Address) && targets.Any(x=>x.Text==app.L("Default browser")),"Confirmation exposes the complete exact URL in a selectable native control");
            check(Descendants(confirm).OfType<Button>().Any(x=>Equals(x.Content,app.L("Open Web Page"))),"Page-only confirmation offers an explicit open action");
        }
        finally { form.Close();confirm.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach(var item in Descendants(child))yield return item; }
    }
}
