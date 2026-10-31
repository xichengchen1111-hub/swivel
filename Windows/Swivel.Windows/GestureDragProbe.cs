using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

[StructLayout(LayoutKind.Sequential)]
public struct GestureOlePoint { internal int X, Y; }

[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGestureOleDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, GestureOlePoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, GestureOlePoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, GestureOlePoint point, ref uint effect);
}

// NOREDIRECTIONBITMAP at creation leaves no visible surface, while the entire HWND
// remains hit-testable. Unlike a zero-alpha layered window it still receives OLE.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class GestureDragProbe : IGestureOleDropTarget
{
    private readonly HwndSource source;
    private readonly Func<bool> available;
    private readonly Action<string[]> confirm;
    private readonly Action cancel;
    private readonly Action<Exception> report;
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool registered, initialized, closing, queued, confirmed, cleaned;
    private int depth;
    internal IntPtr Handle => source.Handle;
    internal Task Completion => done.Task;
    internal GestureDragProbe(WheelGestureRequest request, Func<bool> available, Action<string[]> confirm, Action cancel, Action<Exception> report)
    {
        this.available = available; this.confirm = confirm; this.cancel = cancel; this.report = report;
        int result = OleInitialize(IntPtr.Zero);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        initialized = true;
        try
        {
            if (!available()) throw new OperationCanceledException();
            source = new HwndSource(new HwndSourceParameters("Swivel file drag probe") {
                WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x08000000 | 0x00000080 | 0x00200000 | 0x00000008,
                PositionX = request.PhysicalX - 12, PositionY = request.PhysicalY - 12, Width = 24, Height = 24
            });
            source.AddHook(Hook);
            result = RegisterDragDrop(source.Handle, this);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            registered = true;
            if (!available()) throw new OperationCanceledException();
            if (!SetWindowPos(source.Handle, new IntPtr(-1), request.PhysicalX - 12, request.PhysicalY - 12, 24, 24, 0x0010 | 0x0040))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            if (registered && source is not null) RevokeDragDrop(source.Handle);
            source?.Dispose(); if (initialized) { initialized = false; OleUninitialize(); }
            throw;
        }
    }
    private bool Current
    {
        get
        {
            if (closing || confirmed) return false;
            if (available()) return true;
            cancel(); Close(); return false;
        }
    }
    public int DragEnter(IDataObject data, uint keys, GestureOlePoint point, ref uint effect)
    {
        depth++;
        uint allowed = effect; effect = 0;
        try
        {
            if (!Current || (allowed & 1) == 0) return 0;
            var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            if (data.QueryGetData(ref format) != 0 || !Current) { Close(); return 0; }
            data.GetData(ref format, out var medium);
            string[]? snapshot = null;
            try
            {
                if (!Current || medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero) return 0;
                uint count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
                if (count is < 1 or > GestureDropRules.MaxFiles) throw new UserError("Drop between 1 and 100 local files.");
                var paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFile(medium.unionmember, i, null, 0);
                    if (length is < 1 or > GestureDropRules.MaxPathLength) throw new UserError("A dropped path is empty, too long, or contains a null character.");
                    var path = new StringBuilder((int)length + 1);
                    if (DragQueryFile(medium.unionmember, i, path, length + 1) != length) throw new UserError("A dropped path is empty, too long, or contains a null character.");
                    paths[i] = path.ToString();
                }
                snapshot = GestureDropRules.Snapshot(paths);
            }
            finally { ReleaseStgMedium(ref medium); }
            if (snapshot is not null && Current)
            {
                confirmed = true;
                // The owner posts the hand-off; it must not destroy this HWND in COM.
                confirm(snapshot);
            }
        }
        catch (Exception error)
        {
            Close();
            try { source.Dispatcher.BeginInvoke(new Action(() => report(error))); } catch { /* Cleanup still completes below. */ }
        }
        finally { Leave(); }
        // The probe itself never accepts a drop or promises a file operation.
        return 0;
    }
    public int DragOver(uint keys, GestureOlePoint point, ref uint effect) { effect = 0; return 0; }
    public int DragLeave()
    {
        depth++;
        try { if (!closing) { cancel(); Close(); } }
        finally { Leave(); }
        return 0;
    }
    public int Drop(IDataObject data, uint keys, GestureOlePoint point, ref uint effect) { effect = 0; Close(); return 0; }
    internal void Close()
    {
        if (!closing) { closing = true; ShowWindow(source.Handle, 0); }
        if (depth == 0) QueueClose();
    }
    private void Leave() { depth--; if (closing && depth == 0) QueueClose(); }
    private void QueueClose()
    {
        if (queued || done.Task.IsCompleted) return;
        queued = true;
        try
        {
            source.Dispatcher.BeginInvoke(new Action(() => {
                queued = false;
                if (depth == 0) Cleanup(null);
            }));
        }
        catch (Exception error) { queued = false; Cleanup(error); }
    }
    private void Cleanup(Exception? failure)
    {
        if (cleaned) return;
        cleaned = true;
        try { if (registered) { registered = false; Marshal.ThrowExceptionForHR(RevokeDragDrop(source.Handle)); } }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        try { source.RemoveHook(Hook); source.Dispose(); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        finally { if (initialized) { initialized = false; OleUninitialize(); } }
        if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IGestureOleDropTarget target);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint length);
    [DllImport("user32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
