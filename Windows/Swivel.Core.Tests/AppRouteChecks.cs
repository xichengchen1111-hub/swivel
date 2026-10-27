using System;
using System.Linq;
using Swivel.Core;

internal static class AppRouteChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs = new Prefs(); string id = Guid.NewGuid().ToString("N");
        var route = new AppRoute(id,"Local Editor",@"C:\Program Files\Editor\Editor.exe","PNG, .jpg,png"); prefs.SaveApp(route);
        check(prefs.Apps.Single().Extensions == "png,jpg", "App extension normalization");
        check(!prefs.IsOn(Feature.AppRouting) && !Catalog.Available(prefs,new[] { "a.png" }).Any(x => x.Id.StartsWith("app.")), "App routing independently opt-in");
        prefs.Set(Feature.AppRouting,true);
        check(Catalog.Available(prefs,new[] { "a.PNG", "b.jpg" }).Any(x => x.Id == "app."+id), "Explicit extension routing");
        check(!Catalog.Available(prefs,new[] { "a.png", "b.txt" }).Any(x => x.Id == "app."+id), "Entire selection must match route");
        prefs.SetCustomActions(new[] { "app."+id }); prefs.SaveApp(route with { Name = "Renamed" }); prefs.Set(Feature.MyWheel,true);
        check(Catalog.ForCustomWheel(prefs,new[] { "a.png" }).Single().Title == "Renamed", "App custom action identity retained");
        var loaded = Prefs.Parse(prefs.Serialize()); check(loaded.Apps.Single() == prefs.Apps.Single() && loaded.CustomActionIds.Single() == "app."+id, "Apps load before custom-action references");
        check(!loaded.Apps.Single().CopyToClipboard,"Existing app delivery defaults to direct opening");
        prefs.SaveApp(prefs.Apps.Single() with { CopyToClipboard = true }); check(Prefs.Parse(prefs.Serialize()).Apps.Single().CopyToClipboard,"Clipboard app delivery persists without changing stable identity");
        string legacy=prefs.Serialize().Replace(",\"CopyToClipboard\":true","");check(!Prefs.Parse(legacy).Apps.Single().CopyToClipboard,"Old saved routes without delivery field preserve direct behavior");
        foreach (var bad in new[] { route with { Executable = @"C:\run.cmd" }, route with { Executable = @"\\server\tool.exe" }, route with { Executable = @"C:\tool.exe:stream" },route with { Extensions = "../png" },route with { Extensions = "*" },route with { Name = "bad\u202e" } }) reject(bad.Validate,"Unsafe app configuration rejected");
        reject(() => prefs.SaveApp(route with { Id = Guid.NewGuid().ToString("N"), Name = "Renamed" }), "Duplicate route names refused");
        prefs.RemoveApp(id); check(prefs.CustomActionIds.Count == 0, "App removal cleans custom reference");
        route = route with { Extensions = "" }; prefs.SaveApp(route);
        check(Catalog.Available(prefs,new[] { "a.noext", "b.pdf" }).Any(x => x.Id == "app."+id), "Explicit any-type route");

        var page = new AppRoute(Guid.NewGuid().ToString("N"), "Web Editor", "", "png,jpg", WebAddress: "HTTPS://EXAMPLE.COM:443/editor");
        prefs.SaveApp(page);page=prefs.Apps.Single(x=>x.Id==page.Id);
        check(page.IsWebPage && page.Executable=="" && page.WebAddress=="https://example.com/editor", "Saved web action canonicalizes its explicit destination");
        var noFiles=Catalog.Available(prefs,Array.Empty<string>()).Single(x=>x.Id=="app."+page.Id);
        check(noFiles.WebPage && !noFiles.RequiresFiles && noFiles.Extensions is null && Catalog.DisplayTitle(noFiles,"en")=="Open web page: Web Editor", "Web opening has a distinct title and requires no file input");
        check(Catalog.Available(prefs,new[]{"wrong.txt"}).Any(x=>x.Id=="app."+page.Id), "No-copy web opening ignores file type filters");
        prefs.SetCustomActions(new[]{"app."+page.Id});prefs.Set(Feature.MyWheel,true);
        prefs.SaveApp(page with{Name="Browser Editor",CopyToClipboard=true});
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="app."+page.Id) && !Catalog.Available(prefs,new[]{"wrong.txt"}).Any(x=>x.Id=="app."+page.Id), "Clipboard web action requires matching explicit files");
        check(Catalog.Available(prefs,new[]{"image.PNG","another.jpg"}).Any(x=>x.Id=="app."+page.Id) && !Catalog.Available(prefs,new[]{"image.PNG","wrong.txt"}).Any(x=>x.Id=="app."+page.Id), "Web clipboard filter covers the entire selection");
        check(Catalog.ForCustomWheel(prefs,new[]{"image.png"}).Single().Id=="app."+page.Id, "Editing web name/delivery retains custom wheel ID");
        string pages=prefs.Serialize();var restored=Prefs.Parse(pages);
        check(restored.Serialize()==pages && restored.Apps.Single(x=>x.Id==page.Id).CopyToClipboard, "Mixed local and web routes retain IDs, destinations and delivery through settings roundtrip");
        prefs.Set(Feature.AppRouting,false);
        check(!Catalog.Available(prefs,new[]{"image.png"}).Any(x=>x.Id.StartsWith("app.",StringComparison.Ordinal)) && !Catalog.ForCustomWheel(prefs,new[]{"image.png"}).Any(), "Disabling routing hides web actions and custom references");
        prefs.RemoveApp(page.Id);check(prefs.CustomActionIds.Count==0,"Removing a web action cleans its stable custom-wheel reference");
        var onlyPages=new Prefs();for(int i=0;i<16;i++)onlyPages.SaveApp(page with{Id=Guid.NewGuid().ToString("N"),Name="Page "+i});
        reject(()=>onlyPages.SaveApp(route with{Id=Guid.NewGuid().ToString("N"),Name="Seventeenth"}),"Local applications and pages share sixteen slots");
        reject(()=>onlyPages.SaveApp(page with{Id=Guid.NewGuid().ToString("N"),Name="Page 0"}),"Web page names cannot conflict");
        foreach(var invalid in new[]{page with{Executable=@"C:\app.exe"},page with{WebAddress=""},page with{WebAddress="javascript:alert(1)"},page with{Extensions="*"},page with{Extensions=null!}})
            reject(invalid.Validate,"Malformed web route configuration refused");
        var chosen=page with { BrowserExecutable=@"C:\Program Files\Trusted Browser\browser.exe" };
        var browserPrefs=new Prefs();browserPrefs.SaveApp(chosen);browserPrefs.SetCustomActions(new[]{"app."+chosen.Id});
        var browserRestored=Prefs.Parse(browserPrefs.Serialize());
        check(browserRestored.Apps.Single()==chosen && browserRestored.CustomActionIds.Single()=="app."+chosen.Id && !browserRestored.IsOn(Feature.AppRouting),"Chosen browser roundtrips without changing action identity or feature switches");
        browserPrefs.SaveApp(chosen with { BrowserExecutable=null });
        string noBrowser=browserPrefs.Serialize();
        check(Prefs.Parse(noBrowser.Replace(",\"BrowserExecutable\":null","")).Apps.Single().BrowserExecutable is null,"Missing optional browser preserves default browser behavior");
        foreach(string invalid in new[]{"",@"\\server\browser.exe",@"C:\browser.cmd",@"C:\browser.lnk",@"C:\browser.exe:stream",@"C:\x\..\browser.exe",new string('x',241)})
            reject(()=>(page with { BrowserExecutable=invalid }).Validate(),"Unsafe browser selection refused");
        reject(()=>(route with { BrowserExecutable=@"C:\browser.exe" }).Validate(),"Local file routes cannot secretly carry browser selection");
        string oldSaved=pages.Replace(",\"WebAddress\":null", "");
        check(!Prefs.Parse(oldSaved).Apps.Single(x=>x.Id==route.Id).IsWebPage,"Old executable routes without optional web field preserve direct behavior");
        foreach(var (input, expected) in new[]{
            ("https://example.com", "https://example.com/"),
            ("HTTPS://EXAMPLE.COM:443/a/../editor?x=1&y=2#section", "https://example.com/editor?x=1&y=2#section"),
            ("https://example.com:8443/a%20b", "https://example.com:8443/a%20b"),
            ("http://LOCALHOST:3000/tool", "http://localhost:3000/tool"),
            ("http://127.0.0.1:8181/", "http://127.0.0.1:8181/"),
            ("http://[::1]:8181/", "http://[::1]:8181/"),
            ("https://192.0.2.1/", "https://192.0.2.1/"),
            ("https://[2001:db8::1]/", "https://[2001:db8::1]/"),
            ("https://xn--bcher-kva.example/", "https://xn--bcher-kva.example/"),
            ("https://example.com/中文?q=猫#片", "https://example.com/%E4%B8%AD%E6%96%87?q=%E7%8C%AB#%E7%89%87"),
            ("https://example.com/%F0%9F%A6%9C", "https://example.com/%F0%9F%A6%9C"),
            ("https://example.com/a%2Fb?redirect=https%3A%2F%2Felsewhere.example", "https://example.com/a%2Fb?redirect=https%3A%2F%2Felsewhere.example")})
        {
            string normalized=WebAddress.Normalize(input);
            check(normalized==expected,"Web address canonicalizes only its explicitly saved destination: "+input);
            check(WebAddress.Normalize(normalized)==normalized,"Web address normalization is idempotent");
        }
        foreach(string invalid in new[]{
            "", "example.com", "//example.com", "https:example.com", "https:/example.com", "https:///example.com", "ftp://example.com", "file:///C:/test", "javascript:alert(1)", "ms-settings:display",
            "http://example.com", "http://localhost.example", "http://localhost.", "http://127.0.0.2", "http://[0:0:0:0:0:0:0:1]", "http://[::ffff:127.0.0.1]",
            "https://user@example.com", "https://user:pass@example.com", "https://@example.com", "https://example.com@localhost", "https://example.com%2f@evil.example",
            "https://%65xample.com", "https://example%2ecom", "https://127.1", "https://2130706433", "https://0x7f000001", "https://0177.0.0.1", "https://127.0.0.01", "https://127.0.0.1.", "https://example.123", "https://example.0xff", "https://0x", "https://example.0X",
            "https://[::1%25eth0]", "https://[2001:0db8::1]", "https://[::1]junk", "https://::1", "https://example.com:", "https://example.com:0", "https://example.com:-1", "https://example.com:65536", "https://example.com:1:2",
            "https://.example.com", "https://example..com", "https://-example.com", "https://example-.com", "https://example_com", "https://例子.com", "https://ｅxample.com",
            " https://example.com", "https://example.com ", "https://example.com/a b", "https://example.com/\\evil", "https://example.com\\@evil.example", "https://example.com/\t", "https://example.com/\n", "https://example.com/\u202e", "https://example.com/\u200b", "https://example.com/\u034f", "https://example.com/\ud800",
            "https://example.com/%", "https://example.com/%2", "https://example.com/%xx", "https://example.com/%00", "https://example.com/%0A", "https://example.com/%7f", "https://example.com/%5c", "https://example.com/%E2%80%AE", "https://example.com/%C2%A0", "https://example.com/%ff", "https://example.com/%C0%AF", "https://example.com/%ED%A0%80", "https://example.com/%E4x%B8%AD"
        }) reject(()=>WebAddress.Normalize(invalid),"Unsafe or unsupported web address refused: "+invalid);
        string maximum="https://example.com/"+new string('&',WebAddress.MaxLength-"https://example.com/".Length);
        check(WebAddress.Normalize(maximum)==maximum,"Exactly 2048 canonical address characters accepted");
        reject(()=>WebAddress.Normalize(maximum+"x"),"Raw address length is bounded");
        reject(()=>WebAddress.Normalize("https://example.com/"+new string('界',300)),"Escaping expansion is bounded in the saved canonical address");
        reject(()=>WebAddress.Normalize("https://"+new string('x',64)+".com/"),"DNS labels are bounded");

    }
}
