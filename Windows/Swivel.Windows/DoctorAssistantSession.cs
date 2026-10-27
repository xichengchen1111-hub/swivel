using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

// Owned by the App operation. Holds immutable projections and optional labels;
// labels require separate explicit selection and never join the default JSON.
// No underlying report, live list, filter or rescan callback is held.
internal sealed class DoctorAssistantSession
{
    internal const string Scope="Only aggregate counts, sizes and status from the complete recorded result are included. Current filters, selected rows, names, paths, hashes, commands and content are omitted. No new scan is run. Missing or partial results are not zero or proof of safety.";
    private readonly App app;private readonly Window parent;private readonly DoctorReportSnapshot snapshot;private readonly CancellationToken token;private readonly DoctorLabelSet? labels;
    private Task pending=Task.CompletedTask;private AssistantDialog? child;private bool closed;
    internal Button Button{get;}
    internal DoctorAssistantSession(App app,Window parent,Panel footer,DoctorReportSnapshot snapshot,CancellationToken token,bool attachButton=true,DoctorLabelSet? labels=null)
    {
        this.app=app;this.parent=parent;this.snapshot=snapshot;this.token=token;this.labels=labels;
        Button=UI.Button(app.L("Explain Redacted Snapshot…"),()=>{});Button.ToolTip=app.L(Scope);Button.Click+=Explain;if(attachButton)footer.Children.Insert(0,Button);parent.Closed+=ParentClosed;
    }
    private async void Explain(object sender,RoutedEventArgs args)=>await OpenAsync();
    internal Task OpenAsync()
    {
        if(closed||!pending.IsCompleted)return pending;var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);pending=completion.Task;
        _=RunAsync(completion);return pending;
    }
    private async Task RunAsync(TaskCompletionSource completion)
    {
        try
        {
            Button.IsEnabled=false;
            try
            {
                token.ThrowIfCancellationRequested();if(!app.Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");if(!app.Prefs.IsOn(Feature.Assistant))throw new UserError("Enable Assistant in Settings first.");var settings=app.Prefs.Assistant??throw new UserError("Configure the assistant service in Assistant Settings first.");
                child=new AssistantDialog(app,settings,Array.Empty<AssistantChoice>(),Array.Empty<string>(),app.L("Explain this redacted snapshot and its limits. Do not apply fixes."),token,doctorSnapshot:snapshot,doctorLabels:labels){Owner=parent};
                try{child.ShowDialog();}finally{await child.FinishAsync();}
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!closed)app.ShowError(error);}
            finally{child=null;if(!closed)Button.IsEnabled=true;}
        }
        catch(Exception error){completion.TrySetException(error);}
        finally{completion.TrySetResult();}
    }
    private void ParentClosed(object? sender,EventArgs args){closed=true;child?.RequestClose();}
    internal async Task FinishAsync()
    {
        closed=true;Button.IsEnabled=false;parent.Closed-=ParentClosed;Button.Click-=Explain;child?.RequestClose();await pending;
    }
}
