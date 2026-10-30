using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class RegisteredTargetNativeChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        reject(() => RegisteredTargetReader.Read(new CancellationToken(true)), "F08 precancel avoids every native source");
        reject(() => ServiceReader.Commands(new CancellationToken(true), () => false, new List<string>()), "F08 service precancel before enumeration");
        check(ServiceReader.Commands(default, () => true, new List<string>()).Count == 0, "F08 expired deadline avoids SCM calls");
        foreach (var method in typeof(ServiceReader).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null))
            check(method.GetCustomAttribute<DllImportAttribute>()!.Value == "advapi32.dll" && method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths == DllImportSearchPath.System32, "Service target queries keep System32-only imports");
        var buffer = Marshal.AllocHGlobal(ServiceReader.ConfigBytes);
        try
        {
            int size = Marshal.SizeOf<ServiceReader.NativeConfig>();
            void Put(IntPtr pointer) => Marshal.StructureToPtr(new ServiceReader.NativeConfig { Binary = pointer }, buffer, false);
            check(IntPtr.Size == 8 && size == 64 && Marshal.OffsetOf<ServiceReader.NativeConfig>("Binary").ToInt32() == 16, "QUERY_SERVICE_CONFIGW x64/ARM64 layout");
            Marshal.Copy(new byte[ServiceReader.ConfigBytes], 0, buffer, ServiceReader.ConfigBytes);
            foreach (IntPtr pointer in new[] { IntPtr.Zero, IntPtr.Subtract(buffer, 2), buffer, IntPtr.Add(buffer, size + 1), IntPtr.Add(buffer, ServiceReader.ConfigBytes) })
            { Put(pointer); check(ServiceReader.ConfigCommand(buffer) is null, "Service binary pointer must be aligned and inside returned string area"); }
            string command = "\"C:\\Apps\\Foo\\foo.exe\" --flag";
            var chars = (command + "\0").ToCharArray(); Marshal.Copy(chars, 0, IntPtr.Add(buffer, size), chars.Length); Put(IntPtr.Add(buffer, size));
            check(ServiceReader.ConfigCommand(buffer) == command, "Service bounded configuration retains complete raw command for Core parser");
            var longText = Enumerable.Repeat('x', RegisteredTargets.MaxCommand + 1).ToArray(); Marshal.Copy(longText, 0, IntPtr.Add(buffer, size), longText.Length);
            check(ServiceReader.ConfigCommand(buffer) is null, "Oversized command is unresolved, never truncated");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var app = Application.Current as App ?? new App(validationHost: true);
        var snapshot = RegisteredTargets.Resolve(new[] { new RegisteredCommand("Services", "name\r\n\u202e", @"C:\Apps\Foo\a.exe"), new RegisteredCommand("Run / RunOnce", "unparsed", "relative.exe") }, new[] { RegisteredTargets.TasksFailed }, new Dictionary<string,string>(), default);
        var evidence = RegisteredTargets.ForFolder(snapshot, @"C:\Apps\Foo", default);
        var dialog = new RegisteredTargetsDialog(app, evidence);
        var buttons = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        check(buttons.Length == 3 && buttons.Last().IsCancel && !buttons.Any(b => new[] { "Delete", "Disable", "Stop" }.Contains(b.Content)), "Registration review only offers fixed Settings, explicit copy and Close");
        check(dialog.CurrentReport.Contains("name[U+000D][U+000A][U+202E]") && dialog.CurrentReport.Contains(app.L(RegisteredTargets.Unresolved)) && dialog.CurrentReport.Contains(app.L(RegisteredTargets.TasksFailed)), "Sanitized evidence keeps unresolved and unreadable facts visible"); dialog.Close();
        var folder = new LeftoverFolder(new("Foo", Array.Empty<string>(), Array.Empty<RecordKey>()), LeftoverBase.Local, null, "Foo", @"C:\Apps\Foo");
        var item = new LeftoverItem(folder, null) { Registrations = evidence };
        var result = new AppLeftoverResult(new[] { folder.App }, Array.Empty<RemovedApp>(), Array.Empty<LeftoverBaseResult>(), new[] { item }, Array.Empty<string>(), false, false, false, false, default);
        var leftovers = new AppLeftoversDialog(app, result, () => Array.Empty<string>(), () => _ => { });
        var root = (DockPanel)leftovers.Content; var body = (StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
        body.Children.OfType<ListView>().Single().SelectAll();
        check(!root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b => Equals(b.Content, app.L("Move Selected to Recycle Bin…"))).IsEnabled, "A still-registered leftover row cannot be offered for Trash even when selected");
        var cleanItem = item with { Registrations = RegisteredTargets.ForFolder(new(Array.Empty<RegisteredTarget>(), Array.Empty<string>()), folder.Path, default) };
        var changed = new AppLeftoversDialog(app, result with { Items = new[] { cleanItem } }, () => Array.Empty<string>(), () => _ => RegisteredTargets.RequireTrash(evidence));
        reject(() => changed.Check(cleanItem), "Fresh move-time check refuses a registration added since the clean row snapshot"); changed.Close();
        reject(() => leftovers.Check(item), "Move-time check independently refuses registered folder"); leftovers.Close();
    }
}
