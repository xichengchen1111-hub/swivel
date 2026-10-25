using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Opens an empty system Command Prompt. Paths are passed only as the process's
/// working directory; no path or file content is interpolated into shell syntax.
public sealed class TerminalLaunch : IDisposable
{
    private readonly NativeFiles.Input source, executable;
    public string Directory { get; }
    public string Executable => executable.Path;
    private TerminalLaunch(NativeFiles.Input source,NativeFiles.Input executable)
    { this.source = source; this.executable = executable; Directory = Path.GetDirectoryName(source.Path)!; }
    public static TerminalLaunch Open(string file,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var source = NativeFiles.OpenRead(file);
        NativeFiles.Input? executable = null;
        try
        {
            // SystemDirectory is the OS API result, not COMSPEC, PATH or a saved preference.
            executable = NativeFiles.OpenRead(Path.Combine(Environment.SystemDirectory,"cmd.exe"));
            AppLaunch.ValidateExecutable(executable); token.ThrowIfCancellationRequested(); return new(source,executable);
        }
        catch { executable?.Dispose(); source.Dispose(); throw; }
    }
    public static ProcessStartInfo StartInfo(string executable,string directory)
    {
        WindowsPathPolicy.Validate(executable); WindowsPathPolicy.Validate(Path.Combine(directory,"placeholder"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add("/D"); // Disable Command Processor AutoRun; no /C or /K command.
        return start;
    }
    public void Launch(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var process = Process.Start(StartInfo(Executable,Directory))
            ?? throw new UserError("Windows could not open Command Prompt.");
    }
    public void Dispose() { executable.Dispose(); source.Dispose(); }
}

internal sealed class TerminalDialog : Window
{
    public TerminalDialog(App app,TerminalLaunch launch)
    {
        Title = app.L("Open Command Prompt Here…"); Width = 650; Height = 380; MinWidth = 420; MinHeight = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(app.L("Open an empty Command Prompt in this file's folder. Swivel will not run a command, execute the file or request administrator access.")));
        panel.Children.Add(UI.Text(launch.Directory,18)); panel.Children.Add(UI.Text(launch.Executable,12));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Open Command Prompt"),() => DialogResult = true));
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
