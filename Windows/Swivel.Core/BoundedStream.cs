using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// A seekable budgeted view. The caller retains and disposes the underlying lease.
// WinRT adapters may dispose this view without disposing the held native input.
public sealed class BoundedStream:Stream
{
    private readonly Stream inner;private readonly long limit;private readonly bool writable;private readonly CancellationToken token;private readonly object gate=new();private bool disposed;
    public BoundedStream(Stream inner,long limit,bool writable,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(inner);if(limit<0||!inner.CanSeek||(writable?!inner.CanWrite:!inner.CanRead))throw new ArgumentException("Readable seekable stream required.");
        if(inner.Length>limit)throw new UserError(Budget);this.inner=inner;this.limit=limit;this.writable=writable;this.token=token;
    }
    public const string Budget="The system codec exceeded the stream size or seek budget. No output was published.";
    private void Check(){ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();}
    private void PositionAllowed(long position){if(position<0||position>(writable?limit:inner.Length))throw new UserError(Budget);}
    public override bool CanRead=>!disposed&&inner.CanRead;public override bool CanSeek=>!disposed;public override bool CanWrite=>!disposed&&writable;
    public override long Length{get{lock(gate){Check();return inner.Length;}}}
    public override long Position{get{lock(gate){Check();return inner.Position;}}set{lock(gate){Check();PositionAllowed(value);inner.Position=value;}}}
    public override int Read(Span<byte> buffer){lock(gate){Check();int n=inner.Read(buffer);Check();return n;}}
    public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
    public override void Write(ReadOnlySpan<byte> buffer){lock(gate){Check();if(!writable)throw new NotSupportedException();if(inner.Position<0||inner.Position>limit-buffer.Length)throw new UserError(Budget);inner.Write(buffer);Check();}}
    public override void Write(byte[] buffer,int offset,int count)=>Write(buffer.AsSpan(offset,count));
    public override long Seek(long offset,SeekOrigin origin)
    {
        lock(gate){Check();long position;try{position=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>inner.Position,SeekOrigin.End=>inner.Length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);}catch(OverflowException){throw new UserError(Budget);}PositionAllowed(position);return inner.Seek(position,SeekOrigin.Begin);}
    }
    public override void SetLength(long value){lock(gate){Check();if(!writable)throw new NotSupportedException();if(value<0||value>limit)throw new UserError(Budget);inner.SetLength(value);}}
    public override void Flush(){lock(gate){Check();if(writable)inner.Flush();}}
    protected override void Dispose(bool disposing){lock(gate){disposed=true;}base.Dispose(disposing);}
}
