using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;
using IDataObject = System.Windows.IDataObject;

internal static class GestureActionDropNativeChecks
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    internal static void Until(Func<bool> done)
    {
        var time = Stopwatch.StartNew();
        while (!done()) { if (time.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Gesture fixture did not settle."); Pump(); Thread.Sleep(1); }
        Pump();
    }
    internal static void Close(GestureDropWindow target)
    {
        ((IGestureDropTarget)target).Close(); Until(() => target.Completion.IsCompleted); target.Completion.GetAwaiter().GetResult();
    }
    private static DragEventArgs Send(GestureDropWindow target, IDataObject data, string method, Point point)
    {
        var args = (DragEventArgs)typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point) }, null)!
            .Invoke(new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, target.Content, point });
        args.RoutedEvent = method switch { "OnDragEnter" => DragDrop.DragEnterEvent, "OnDragOver" => DragDrop.DragOverEvent, "OnDragLeave" => DragDrop.DragLeaveEvent, _ => DragDrop.DropEvent };
        typeof(GestureDropWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, new object[] { target, args });
        return args;
    }
    internal static void Confirm(GestureDropWindow target, DataObject data)
    {
        uint effect = 3;
        var probe = target.Probe ?? throw new InvalidOperationException("Expected the real native probe.");
        IntPtr exposed = Marshal.GetComInterfaceForObject(probe, typeof(IGestureOleDropTarget));
        try
        {
            Guid iid = typeof(IGestureOleDropTarget).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(exposed, in iid, out IntPtr queried));
            Marshal.Release(queried);
        }
        finally { Marshal.Release(exposed); }
        probe.DragEnter((System.Runtime.InteropServices.ComTypes.IDataObject)data, 1, default, ref effect);
        Until(() => target.IsVisible || target.Completion.IsCompleted);
    }
    internal static void Run(Action<bool, string> check)
    {
        var app = (App)Application.Current;
        if (!app.IsValidationHost) throw new InvalidOperationException("Only run in the validation host.");
        var prefs = app.Prefs;
        var oldTemplate = prefs.MenuTemplate; bool oldAnimation = prefs.AnimateMenus;
        bool oldWheel = prefs.IsOn(Feature.Wheel);
        try
        {
            prefs.Set(Feature.Wheel, true);
            // Real WPF DataObject, including its native COM CF_HDROP/HGLOBAL contract. No clipboard access or file reads.
            string[] paths = { @"C:\GestureFixture\photo.png" };
            var data = new DataObject(DataFormats.FileDrop, paths);
            var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            check(((System.Runtime.InteropServices.ComTypes.IDataObject)data).QueryGetData(ref format) == 0, "Real data object supplies native CF_HDROP without conversion");
            foreach (bool loseCapture in new[] { false, true })
            {
                bool down = true, evidence = true;
                var target = new GestureDropWindow(app, () => true, (_, _) => { }, error => throw error, () => down, () => 0, () => evidence);
                try
                {
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    var probe = target.Probe!;
                    if (loseCapture) evidence = false; else down = false;
                    typeof(GestureDropWindow).GetMethod("PollDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
                    check(!IsWindowVisible(probe.Handle), loseCapture ? "Capture loss immediately hides the hit-testable probe" : "Button up immediately hides the hit-testable probe");
                    Until(() => target.Completion.IsCompleted);
                    check(target.Probe is null && !target.IsVisible && target.Completion.IsCompletedSuccessfully, "Invalidated probe destruction is joined before session completion");
                }
                finally { Close(target); }
            }
            foreach (var template in Enum.GetValues<MenuTemplate>())
            {
                prefs.SetMenuPresentation(template, false);
                ToolAction? chosen = null; string[]? received = null; Exception? failure = null; int calls = 0;
                var target = new GestureDropWindow(app, () => true, (action, files) => { calls++; chosen = action; received = files; }, error => failure = error, () => true, () => 0, () => true);
                try
                {
                    IntPtr foreground = GetForegroundWindow();
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    check(((Grid)target.Content).Children.Count == 0 && !target.ShowActivated, "Unconfirmed target renders no menu or controls");
                    Confirm(target, data);
                    target.UpdateLayout();
                    long style = GetWindowLongPtr(new WindowInteropHelper(target).Handle, -20).ToInt64();
                    check((style & 0x08000000L) != 0 && GetForegroundWindow() == foreground && !target.ShowActivated, "Confirmed Wheel is NOACTIVATE and preserves the drag source foreground");
                    var canvas = Find<Canvas>((DependencyObject)target.Content).Single();
                    var buttons = canvas.Children.OfType<Button>().Where(button => button.Tag is ToolAction).ToArray();
                    check(buttons.Length > 0 && chosen is null, "File DragEnter builds a kind-filtered catalog but never executes");
                    var tile = MenuGeometry.Layout(template, buttons.Length)[0];
                    Point point = canvas.TranslatePoint(new Point(tile.X + tile.Width / 2, tile.Y + tile.Height / 2), (UIElement)target.Content);
                    check(target.HitItem(point) == 0 && Send(target, data, "OnDragOver", point).Effects == DragDropEffects.Copy, "Real handler hit-tests each menu template using the shared layout");
                    check(Send(target, data, "OnDrop", point).Effects == DragDropEffects.Copy && calls == 1 && chosen?.Id == ((ToolAction)buttons[0].Tag).Id && received!.SequenceEqual(paths), "Drop delivers exactly the hit action and the independent file snapshot");
                    check(Send(target, data, "OnDrop", point).Effects == DragDropEffects.None && calls == 1, "Repeated Drop cannot run twice");
                }
                finally { Close(target); }
                check(target.Completion.IsCompletedSuccessfully && failure is null, "The real OLE window closes after its callback and releases motion");
            }
            foreach (bool visible in new[] { false, true })
            {
                var target = new GestureDropWindow(app, () => true, (_, _) => { }, _ => { }, () => true, () => 0, () => true);
                try
                {
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    if (visible) { Confirm(target, data); Send(target, data, "OnDragLeave", new Point(100, 100)); }
                    else target.Probe!.DragLeave();
                    Until(() => target.Completion.IsCompleted);
                    check(!target.IsVisible, "OLE cancellation closes both probe and Wheel even while the physical button remains down");
                }
                finally { Close(target); }
            }
            foreach (bool duringQuery in new[] { true, false })
            {
                int calls = 0; bool current = true; GestureDropWindow? target = null;
                target = new GestureDropWindow(app, () => current, (_, _) => calls++, _ => { }, () => true, () => 0, () => true);
                try
                {
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    var probe = target.Probe!;
                    void Reenter()
                    {
                        current = false; ((IGestureDropTarget)target).Close(); Pump();
                        check(!target.Completion.IsCompleted && !probe.Completion.IsCompleted, "Nested provider pump cannot dispose or join a probe inside its COM callback");
                    }
                    var provider = new Provider((System.Runtime.InteropServices.ComTypes.IDataObject)data,
                        duringQuery ? Reenter : () => { }, duringQuery ? () => { } : Reenter);
                    uint effect = 3; probe.DragEnter(provider, 1, default, ref effect);
                    Until(() => target.Completion.IsCompleted);
                    check(effect == 0 && !target.IsVisible && calls == 0 && target.Probe is null, "Probe query/read reentry cancels and joins without displaying or executing");
                }
                finally { Close(target); }
            }
            {
                bool down = true; int calls = 0;
                var target = new GestureDropWindow(app, () => true, (_, _) => calls++, _ => { }, () => down, () => 0, () => true);
                try
                {
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    var provider = new Provider((System.Runtime.InteropServices.ComTypes.IDataObject)data, () => { }, () => down = false);
                    uint effect = 3; target.Probe!.DragEnter(provider, 1, default, ref effect);
                    Until(() => target.Completion.IsCompleted);
                    check(!target.IsVisible && calls == 0 && effect == 0, "Releasing the primary button during GetData never shows Wheel");
                }
                finally { Close(target); }
            }
            foreach (bool file in new[] { false, true })
            {
                int calls = 0; var target = new GestureDropWindow(app, () => true, (_, _) => calls++, _ => { }, () => true, () => 0, () => true);
                try
                {
                    target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false));
                    var input = file ? data : new DataObject(DataFormats.UnicodeText, "text selection");
                    if (file) Confirm(target, data);
                    else
                    {
                        uint effect = 3;
                        target.Probe!.DragEnter((System.Runtime.InteropServices.ComTypes.IDataObject)input, 1, default, ref effect);
                        check(effect == 0, "Native non-file probe returns DROPEFFECT_NONE");
                    }
                    check(Send(target, input, "OnDrop", new Point(-50, -50)).Effects == DragDropEffects.None && calls == 0,
                        file ? "Dropping outside never executes" : "A real non-file data object is refused with DROPEFFECT_NONE");
                    if (!file) check(((Grid)target.Content).Children.Count == 0, "Text drag never displays Wheel");
                }
                finally { Close(target); }
            }
        }
        finally { prefs.SetMenuPresentation(oldTemplate, oldAnimation); prefs.Set(Feature.Wheel, oldWheel); }
        // Capture evidence from real DoDragDrop is checked separately by GestureOleEvidenceNativeChecks.
    }
    private sealed class Provider(System.Runtime.InteropServices.ComTypes.IDataObject inner, Action beforeQuery, Action beforeRead) : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        public int QueryGetData(ref FORMATETC format) { beforeQuery(); return inner.QueryGetData(ref format); }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) { beforeRead(); inner.GetData(ref format, out medium); }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) => throw new NotSupportedException();
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) => throw new NotSupportedException();
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? enumerator) => throw new NotSupportedException();
    }
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    private static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Find<T>(child)) yield return found;
    }
}
