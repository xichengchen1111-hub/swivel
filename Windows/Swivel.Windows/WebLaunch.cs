using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// A reviewed, immutable page address and optional local file references.
/// No network request, clipboard access or process creation happens in Prepare.
public sealed class WebLaunch : IDisposable
{
    private readonly NativeFiles.Input? browser;
    private bool disposed;
    public AppRoute Route { get; }
    public string Address { get; }
    public IReadOnlyList<string> Files { get; }
    private WebLaunch(AppRoute route, string address, string[] files, NativeFiles.Input? browser)
    { Route = route; Address = address; Files = Array.AsReadOnly(files); this.browser = browser; }

    public static WebLaunch Prepare(AppRoute route, IReadOnlyList<string> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); route.Validate();
        if (!route.IsWebPage) throw new UserError("Choose a saved web page action.");
        string address = WebAddress.Normalize(route.WebAddress!);
        // Page-only actions must not examine or pass unrelated selected files.
        var selected = route.CopyToClipboard ? files.ToArray() : Array.Empty<string>();
        if (route.CopyToClipboard && selected.Length is < 1 or > 100) throw new UserError("Choose up to 100 files at a time.");
        if (route.CopyToClipboard && route.Types.Length > 0 && selected.Any(path => !route.Types.Contains(System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant())))
            throw new UserError("This action is not available for these files.");
        NativeFiles.Input? browser = null;
        try
        {
            if (route.BrowserExecutable is not null)
            {
                token.ThrowIfCancellationRequested();
                browser = NativeFiles.OpenRead(route.BrowserExecutable);
                token.ThrowIfCancellationRequested(); AppLaunch.ValidateExecutable(browser);
            }
            var result = new WebLaunch(route, address, selected, browser);
            result.ValidateFiles(token); return result;
        }
        catch { browser?.Dispose(); throw; }
    }

    public static ProcessStartInfo StartInfo(string address) => new(WebAddress.Normalize(address)) {
        // ShellExecute delegates a strictly validated web URL to the registered
        // browser. This is not cmd.exe or a concatenated command line.
        UseShellExecute = true, Verb = "open", WorkingDirectory = Environment.SystemDirectory
    };

    public static ProcessStartInfo StartInfo(string address, string? browserExecutable)
    {
        if (browserExecutable is null) return StartInfo(address);
        var info = new ProcessStartInfo(AppRoute.ValidateBrowser(browserExecutable)) {
            UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory
        };
        // Exactly one URL argument; no browser switches, shell text or file paths.
        info.ArgumentList.Add(WebAddress.Normalize(address)); return info;
    }

    public void ValidateFiles(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this); token.ThrowIfCancellationRequested();
        foreach (string file in Files) { token.ThrowIfCancellationRequested(); NativeFiles.ValidateInput(file); }
    }

    public void Launch(CancellationToken token)
    {
        ValidateFiles(token); var info = StartInfo(Address,Route.BrowserExecutable); token.ThrowIfCancellationRequested();
        // A running default browser may accept the URL without returning a new
        // process handle. A null result therefore is not evidence of failure.
        using var process = Process.Start(info);
        if (Route.BrowserExecutable is not null && process is null) throw new UserError("Windows could not start this application.");
    }
    public void Dispose() { if (disposed) return; disposed = true; browser?.Dispose(); }
}
