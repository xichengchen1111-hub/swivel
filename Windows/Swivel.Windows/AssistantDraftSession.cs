using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class AssistantDraftSession : IDisposable
{
    private readonly List<NativeFiles.Input> held;
    private readonly string[] paths;
    private bool disposed;
    private AssistantDraftSession(string[] paths,List<NativeFiles.Input> held){this.paths=paths;this.held=held;}
    internal static AssistantDraftSession Open(IReadOnlyList<string> files,string language,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var plan=AssistantDraft.Build(DraftTarget.Codex,files,language,token);
        var paths=plan.Paths.ToArray();var held=new List<NativeFiles.Input>();
        try
        {
            foreach(string path in paths){token.ThrowIfCancellationRequested();held.Add(NativeFiles.OpenRead(path));}
            token.ThrowIfCancellationRequested();return new(paths,held);
        }
        catch { Release(held);throw; }
    }
    internal AssistantDraft.Plan Review(DraftTarget target,string language,CancellationToken token)
    {Check(token);return AssistantDraft.Build(target,paths,language,token);}
    internal void Validate(AssistantDraft.Plan plan,CancellationToken token)
    {
        Check(token);
        if(!plan.Paths.SequenceEqual(paths,StringComparer.Ordinal))throw new UserError("The reviewed draft does not match the held files.");
    }
    private void Check(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(held.Any(input=>input.Stream.SafeFileHandle.IsClosed))throw new InvalidOperationException("Draft input lease was closed.");
    }
    internal static ProcessStartInfo StartInfo(AssistantDraft.Plan plan)
    {
        if(plan.Address is null)throw new UserError(AssistantDraft.LongAddress);
        // Plan is immutable and constructed only by the closed Core builder. This
        // is a single protocol address, never a command/argument concatenation.
        return new(plan.Address){UseShellExecute=true,Verb="open",WorkingDirectory=Environment.SystemDirectory};
    }
    internal void Launch(AssistantDraft.Plan plan,CancellationToken token)
    {
        Validate(plan,token);var info=StartInfo(plan);token.ThrowIfCancellationRequested();
        // Existing registered applications may accept a URI without a new handle.
        using var process=Process.Start(info);
    }
    public void Dispose(){if(disposed)return;disposed=true;Release(held);}
    private static void Release(List<NativeFiles.Input> held)
    {
        Exception? error=null;
        for(int i=held.Count-1;i>=0;i--)try{held[i].Dispose();}catch(Exception ex){error??=ex;}
        held.Clear();if(error is not null)throw error;
    }
}
