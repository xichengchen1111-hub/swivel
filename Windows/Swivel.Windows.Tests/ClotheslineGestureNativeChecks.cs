using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Swivel.Windows;

internal static class ClotheslineGestureNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        // Real WPF controls and event/automation paths with inert callbacks. This
        // creates only a hidden validation HWND, never App.Run/OnStartup or a tray,
        // clipboard write, file preview, shell launch or simulated physical input.
        using var source=new HwndSource(new HwndSourceParameters("Swivel gesture checks")
        {
            Width=10,Height=10,WindowStyle=unchecked((int)0x80000000)
        });
        int copied=0,previewed=0,dragged=0;bool available=true;
        using var button=new ClotheslinePrintButton(()=>available,()=>copied++,()=>previewed++,_=>dragged++);
        source.RootVisual=button;
        var provider=(IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        void Pump()
        {
            var frame=new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        void Enter()
        {
            var preview=new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            button.RaiseEvent(preview);
            if(!preview.Handled)button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
        }
        Enter();
        check(copied==1&&previewed==0&&dragged==0,"Native Enter copies immediately without scheduling long press or mouse click delay");
        provider.Invoke();Pump();
        check(copied==2&&previewed==0&&dragged==0,"Native UI Automation invokes one immediate copy through Button OnClick");
        available=false;provider.Invoke();Pump();
        check(copied==2,"Queued native Invoke honours unavailable feature/window/busy gate");available=true;
        provider.Invoke();button.IsEnabled=false;Pump();
        check(copied==2,"Disabling before queued native Invoke dispatch suppresses the action");
        reject(()=>provider.Invoke(),"Native disabled button refuses Invoke");
        button.IsEnabled=true;Enter();
        check(copied==3,"Reenabled native keyboard activation remains usable");
        provider.Invoke();button.Dispose();Pump();
        check(copied==3&&previewed==0&&dragged==0,"Disposed print suppresses already queued automation without preview or drag");
        Enter();provider.Invoke();Pump();
        check(copied==3&&previewed==0&&dragged==0,"Disposed print cannot be reactivated by stale keyboard or automation events");
        source.RootVisual=null;
    }
}
