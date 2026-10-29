using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Swivel.Windows;

internal static class NativeAsync
{
    // Register Cancel, but await the uncancelled task representing native completion.
    // Passing the token to AsTask can otherwise let managed ownership end too early.
    internal static async Task<T> Join<T>(IAsyncOperation<T> operation,CancellationToken token)
    {
        using var cancel=token.Register(()=>TryCancel(operation));
        try{var result=await operation.AsTask().ConfigureAwait(false);token.ThrowIfCancellationRequested();return result;}
        finally{operation.Close();}
    }
    internal static async Task Join(IAsyncActionWithProgress<double> operation,CancellationToken token)
    {
        using var cancel=token.Register(()=>TryCancel(operation));
        try{await operation.AsTask().ConfigureAwait(false);token.ThrowIfCancellationRequested();}
        finally{operation.Close();}
    }
    internal static async Task Join(IAsyncActionWithProgress<double> operation,Action<double> progress,CancellationToken token)
    {
        using var cancel=token.Register(()=>TryCancel(operation));
        operation.Progress=(_,value)=>progress(value);
        try{await operation.AsTask().ConfigureAwait(false);token.ThrowIfCancellationRequested();}
        finally{operation.Close();}
    }
    internal static async Task Join(IAsyncAction operation,CancellationToken token)
    {
        using var cancel=token.Register(()=>TryCancel(operation));
        try{await operation.AsTask().ConfigureAwait(false);token.ThrowIfCancellationRequested();}
        finally{operation.Close();}
    }
    internal static async Task<T> Join<T>(IAsyncOperationWithProgress<T,double> operation,Action<double> progress,CancellationToken token)
    {
        using var cancel=token.Register(()=>TryCancel(operation));
        operation.Progress=(_,value)=>progress(value);
        try{var result=await operation.AsTask().ConfigureAwait(false);token.ThrowIfCancellationRequested();return result;}
        finally{operation.Close();}
    }
    private static void TryCancel(IAsyncInfo operation){try{operation.Cancel();}catch(Exception){/* A completed native operation may already reject cancellation. */}}
}
