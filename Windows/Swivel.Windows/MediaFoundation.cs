using System;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Media Foundation's Source Reader and Sink Writer (System32 mfplat/mfreadwrite), used where
/// every frame of a video must be read and written: Upscale Video 2×. The reader decodes the
/// first video stream to 32-bit RGB with Windows' own decoders and colour converter; the writer
/// encodes 32-bit RGB frames to H.264 in an MP4 with the encoder Windows picks. Nothing is
/// downloaded and no codec is installed.
internal static class MediaFoundation
{
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    internal static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    internal static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    internal static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    internal static readonly Guid ContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    internal static readonly Guid ContainerMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    internal const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;
    internal const uint ReaderError = 0x1, EndOfStream = 0x2, CurrentTypeChanged = 0x20;
    private const uint Version = 0x00020070; // MF_VERSION (MF_SDK_VERSION 2, MF_API_VERSION 0x70)

    internal static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;
    internal static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)value);

    /// Starts Media Foundation for one piece of work; disposing shuts it down (counted by Windows).
    internal static IDisposable Start()
    {
        Marshal.ThrowExceptionForHR(MFStartup(Version, 0));
        return new Started();
    }
    private sealed class Started : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) MFShutdown(); }
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    internal static IMFMediaType VideoType(Guid subtype, int width, int height, uint rateNumerator, uint rateDenominator)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out var type));
        type.SetGUID(MajorType, Video);
        type.SetGUID(Subtype, subtype);
        type.SetUINT64(FrameSize, Pack((uint)width, (uint)height));
        type.SetUINT64(FrameRate, Pack(rateNumerator, rateDenominator));
        type.SetUINT64(PixelAspect, Pack(1, 1));
        type.SetUINT32(InterlaceMode, 2); // MFVideoInterlace_Progressive
        return type;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint size);
    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateMemoryBuffer(uint length, out IMFMediaBuffer buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);

    // Vtable order follows mfobjects.h / mfreadwrite.h. Methods Swivel never calls are placeholders.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        void GetUINT32(in Guid key, out uint value);
        void GetUINT64(in Guid key, out ulong value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(in Guid key, uint value);
        void SetUINT64(in Guid key, ulong value);
        void SetDouble();
        void SetGUID(in Guid key, in Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        new void GetItem(); new void GetItemType(); new void CompareItem(); new void Compare();
        new void GetUINT32(in Guid key, out uint value);
        new void GetUINT64(in Guid key, out ulong value);
        new void GetDouble(); new void GetGUID(); new void GetStringLength(); new void GetString(); new void GetAllocatedString(); new void GetBlobSize(); new void GetBlob(); new void GetAllocatedBlob(); new void GetUnknown(); new void SetItem(); new void DeleteItem(); new void DeleteAllItems();
        new void SetUINT32(in Guid key, uint value);
        new void SetUINT64(in Guid key, ulong value);
        new void SetDouble();
        new void SetGUID(in Guid key, in Guid value);
        new void SetString(); new void SetBlob(); new void SetUnknown(); new void LockStore(); new void UnlockStore(); new void GetCount(); new void GetItemByIndex(); new void CopyAllItems();
        void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        new void GetItem(); new void GetItemType(); new void CompareItem(); new void Compare();
        new void GetUINT32(in Guid key, out uint value);
        new void GetUINT64(in Guid key, out ulong value);
        new void GetDouble(); new void GetGUID(); new void GetStringLength(); new void GetString(); new void GetAllocatedString(); new void GetBlobSize(); new void GetBlob(); new void GetAllocatedBlob(); new void GetUnknown(); new void SetItem(); new void DeleteItem(); new void DeleteAllItems();
        new void SetUINT32(in Guid key, uint value);
        new void SetUINT64(in Guid key, ulong value);
        new void SetDouble();
        new void SetGUID(in Guid key, in Guid value);
        new void SetString(); new void SetBlob(); new void SetUnknown(); new void LockStore(); new void UnlockStore(); new void GetCount(); new void GetItemByIndex(); new void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags();
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void GetSampleDuration(out long duration);
        void SetSampleDuration(long duration);
        void GetBufferCount(out uint count);
        void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(IMFMediaBuffer buffer);
        void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        void Lock(out IntPtr data, out uint maxLength, out uint currentLength);
        void Unlock();
        void GetCurrentLength(out uint length);
        void SetCurrentLength(uint length);
        void GetMaxLength(out uint length);
    }

    [ComImport, Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMF2DBuffer
    {
        void Lock2D(out IntPtr scanline0, out int pitch);
        void Unlock2D();
        void GetScanline0AndPitch(); void IsContiguousFormat(); void GetContiguousLength(); void ContiguousCopyTo(); void ContiguousCopyFrom();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        void GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        void GetCurrentMediaType(uint stream, out IMFMediaType type);
        void SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition();
        void ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        void Flush(); void GetServiceForStream(); void GetPresentationAttribute();
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        void AddStream(IMFMediaType type, out uint stream);
        void SetInputMediaType(uint stream, IMFMediaType type, IMFAttributes? encodingParameters);
        void BeginWriting();
        void WriteSample(uint stream, IMFSample sample);
        void SendStreamTick(); void PlaceMarker(); void NotifyEndOfSegment(); void Flush();
        void FinishWriting(); // IMFSinkWriter::Finalize
        void GetServiceForStream(); void GetStatistics();
    }
}

/// One video's frames, decoded in order to 32-bit BGRA (alpha set opaque), top row first.
internal sealed class VideoFrameReader : IDisposable
{
    private MediaFoundation.IMFSourceReader? reader;
    private readonly IDisposable started;
    private int stride, rows;
    public int Width { get; }
    public int Height { get; }
    public uint RateNumerator { get; private set; }
    public uint RateDenominator { get; private set; }

    /// `width` × `height` is the picture size Windows' media API reported; a decoder whose frames
    /// are larger (coded size padded to whole macroblocks) is read from the top-left corner.
    public VideoFrameReader(string path, int width, int height)
    {
        Width = width; Height = height;
        started = MediaFoundation.Start();
        MediaFoundation.IMFAttributes? attributes = null; MediaFoundation.IMFMediaType? type = null;
        // Locals are released before a failure shuts Media Foundation down again.
        try
        {
            try
            {
                Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateAttributes(out attributes, 1));
                attributes.SetUINT32(MediaFoundation.EnableVideoProcessing, 1);
                Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateSourceReaderFromURL(path, attributes, out var opened));
                reader = opened;
                reader.SetStreamSelection(MediaFoundation.AllStreams, false);
                reader.SetStreamSelection(MediaFoundation.FirstVideoStream, true);
                reader.GetNativeMediaType(MediaFoundation.FirstVideoStream, 0, out var native);
                try
                {
                    native.GetUINT64(MediaFoundation.FrameRate, out ulong rate);
                    (RateNumerator, RateDenominator) = MediaFoundation.Unpack(rate);
                }
                catch (COMException) { RateNumerator = 0; RateDenominator = 0; }
                try
                {
                    native.GetUINT64(MediaFoundation.PixelAspect, out ulong aspect);
                    var (across, down) = MediaFoundation.Unpack(aspect);
                    SquarePixels = across == down || across == 0 || down == 0;
                }
                catch (COMException) { }
                finally { MediaFoundation.Release(native); }
                Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateMediaType(out type));
                type.SetGUID(MediaFoundation.MajorType, MediaFoundation.Video);
                type.SetGUID(MediaFoundation.Subtype, MediaFoundation.Rgb32);
                reader.SetCurrentMediaType(MediaFoundation.FirstVideoStream, IntPtr.Zero, type);
                ReadType();
            }
            finally { MediaFoundation.Release(type); MediaFoundation.Release(attributes); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException or UserError)
        {
            Dispose();
            throw new UserError(VideoUpscale.Unreadable);
        }
        catch { Dispose(); throw; }
    }

    /// False when the stream states non-square pixels (anamorphic video), which the 1:1 output would squash.
    public bool SquarePixels { get; private set; } = true;

    // The decoded frame size and row stride; refuses frames smaller than the picture.
    private void ReadType()
    {
        reader!.GetCurrentMediaType(MediaFoundation.FirstVideoStream, out var current);
        try
        {
            current.GetUINT64(MediaFoundation.FrameSize, out ulong size);
            var (w, h) = MediaFoundation.Unpack(size);
            if (w < Width || h < Height || w > Width + 64 || h > Height + 64) throw new UserError(VideoUpscale.Unreadable);
            try { current.GetUINT32(MediaFoundation.DefaultStride, out uint s); stride = unchecked((int)s); }
            catch (COMException) { stride = (int)w * 4; }
            if (Math.Abs((long)stride) < (long)w * 4) stride = (int)w * 4;
            rows = (int)h;
        }
        finally { MediaFoundation.Release(current); }
    }

    /// The next frame into `bgra` (Width × Height × 4 bytes), with its time and length in 100 ns
    /// units; false at the end.
    public bool Read(byte[] bgra, out long time, out long duration, CancellationToken token)
    {
        if (bgra.Length != Width * Height * 4) throw new ArgumentException("Wrong frame buffer.");
        time = duration = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            MediaFoundation.IMFSample? sample = null;
            try
            {
                try { reader!.ReadSample(MediaFoundation.FirstVideoStream, 0, out _, out uint flags, out time, out sample);
                    if ((flags & MediaFoundation.ReaderError) != 0) throw new UserError(VideoUpscale.Unreadable);
                    if ((flags & MediaFoundation.CurrentTypeChanged) != 0) ReadType();
                    if (sample is null) { if ((flags & MediaFoundation.EndOfStream) != 0) return false; continue; }
                    try { sample.GetSampleDuration(out duration); } catch (COMException) { duration = 0; }
                    Copy(sample, bgra);
                }
                catch (Exception error) when (error is COMException or InvalidCastException) { throw new UserError(VideoUpscale.Unreadable); }
                return true;
            }
            finally { MediaFoundation.Release(sample); }
        }
    }

    private void Copy(MediaFoundation.IMFSample sample, byte[] bgra)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            if (buffer is MediaFoundation.IMF2DBuffer planar)
            {
                planar.Lock2D(out IntPtr scan0, out int pitch);
                try { CopyRows(scan0, pitch, null, bgra); }
                finally { planar.Unlock2D(); }
                return;
            }
            buffer.Lock(out IntPtr data, out _, out uint length);
            try
            {
                // A bottom-up buffer (negative stride) starts with its last row.
                IntPtr first = stride >= 0 ? data : data + (rows - 1) * -stride;
                CopyRows(first, stride, length, bgra);
            }
            finally { buffer.Unlock(); }
        }
        finally { MediaFoundation.Release(buffer); }
    }

    // `length` (a contiguous buffer's bytes) must hold every decoded row; a 2D buffer's rows are Windows'.
    private void CopyRows(IntPtr first, int pitch, long? length, byte[] bgra)
    {
        if (Math.Abs((long)pitch) < Width * 4L || length is { } bytes && Math.Abs((long)pitch) * rows > bytes) throw new UserError(VideoUpscale.Unreadable);
        for (int y = 0; y < Height; y++)
        {
            Marshal.Copy(first + (nint)y * pitch, bgra, y * Width * 4, Width * 4);
            for (int i = y * Width * 4 + 3; i < (y + 1) * Width * 4; i += 4) bgra[i] = 255;
        }
    }

    public void Dispose()
    {
        MediaFoundation.Release(reader); reader = null;
        started.Dispose();
    }
}

/// Encodes BGRA frames, top row first, to an H.264 MP4 at the times given.
internal sealed class VideoFrameWriter : IDisposable
{
    private MediaFoundation.IMFSinkWriter? writer;
    private readonly IDisposable started;
    private readonly uint stream;
    public int Width { get; }
    public int Height { get; }

    public VideoFrameWriter(string path, int width, int height, uint rateNumerator, uint rateDenominator, uint bitrate)
    {
        Width = width; Height = height;
        started = MediaFoundation.Start();
        MediaFoundation.IMFAttributes? attributes = null; MediaFoundation.IMFMediaType? output = null, input = null;
        try
        {
            try
            {
                Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateAttributes(out attributes, 1));
                attributes.SetGUID(MediaFoundation.ContainerType, MediaFoundation.ContainerMpeg4);
                Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out var opened));
                writer = opened;
                output = MediaFoundation.VideoType(MediaFoundation.H264, width, height, rateNumerator, rateDenominator);
                output.SetUINT32(MediaFoundation.AverageBitrate, bitrate);
                writer.AddStream(output, out stream);
                input = MediaFoundation.VideoType(MediaFoundation.Rgb32, width, height, rateNumerator, rateDenominator);
                input.SetUINT32(MediaFoundation.DefaultStride, (uint)(width * 4)); // positive: top row first
                input.SetUINT32(MediaFoundation.AllSamplesIndependent, 1);
                writer.SetInputMediaType(stream, input, null);
                writer.BeginWriting();
            }
            finally { MediaFoundation.Release(input); MediaFoundation.Release(output); MediaFoundation.Release(attributes); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
        {
            Dispose();
            throw new UserError(VideoUpscaler.Failed);
        }
        catch { Dispose(); throw; }
    }

    public void Write(byte[] bgra, long time, long duration)
    {
        if (bgra.Length != Width * Height * 4) throw new ArgumentException("Wrong frame buffer.");
        MediaFoundation.IMFMediaBuffer? buffer = null; MediaFoundation.IMFSample? sample = null;
        try
        {
            Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateMemoryBuffer((uint)bgra.Length, out buffer));
            buffer.Lock(out IntPtr data, out uint max, out _);
            try { if (max < bgra.Length) throw new UserError(VideoUpscaler.Failed); Marshal.Copy(bgra, 0, data, bgra.Length); }
            finally { buffer.Unlock(); }
            buffer.SetCurrentLength((uint)bgra.Length);
            Marshal.ThrowExceptionForHR(MediaFoundation.MFCreateSample(out sample));
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            writer!.WriteSample(stream, sample);
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { throw new UserError(VideoUpscaler.Failed); }
        finally { MediaFoundation.Release(sample); MediaFoundation.Release(buffer); }
    }

    /// Writes the MP4's index; without this the file is unusable (and it is then discarded).
    public void Finish()
    {
        try { writer!.FinishWriting(); }
        catch (COMException) { throw new UserError(VideoUpscaler.Failed); }
    }

    public void Dispose()
    {
        MediaFoundation.Release(writer); writer = null;
        started.Dispose();
    }
}
