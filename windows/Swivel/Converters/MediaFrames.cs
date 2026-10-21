using System.Runtime.InteropServices;

namespace Swivel;

/// Decodes video frames with Media Foundation's Source Reader, converted to RGB32 by its video processor: exact,
/// uncompressed frames for PNG Frame, GIF and thumbnails (MediaComposition's thumbnails come back JPEG-compressed).
/// The COM interfaces are called through their vtables (slot numbers from mfobjects.h / mfreadwrite.h).
sealed unsafe partial class MediaFrames : IDisposable
{
    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out IntPtr mediaType);
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(PropVariant* value);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    const uint FlagError = 0x1, FlagEndOfStream = 0x2, FlagCurrentTypeChanged = 0x20;

    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid VideoRotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    static readonly Guid DisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid Duration100ns = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    static readonly Lazy<bool> started = new(() =>
    {
        try { return MFStartup(0x00020070, 0) >= 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    });

    IntPtr reader;
    readonly string name;
    int width, height, stride;
    (int X, int Y, int W, int H) aperture;

    /// Clockwise degrees Swivel still turns the decoded frames to make them upright.
    public int Rotation { get; private set; }
    /// The track's display rotation in clockwise degrees (MF_MT_VIDEO_ROTATION).
    public int TrackRotation { get; private set; }
    /// The advanced video processor turns 180° tracks too (checked on Windows 11 by ConverterTests.RotatedVideoComesOutUpright);
    /// unlike 90° / 270° that can't be seen from the frame size.
    const bool ProcessorRotates180 = true;
    public TimeSpan Duration { get; private set; }
    /// The upright, displayed size.
    public int DisplayWidth => Rotation is 90 or 270 ? DisplayHeightRaw : DisplayWidthRaw;
    public int DisplayHeight => Rotation is 90 or 270 ? DisplayWidthRaw : DisplayHeightRaw;
    int DisplayWidthRaw, DisplayHeightRaw;
    double pixelAspect = 1;

    public MediaFrames(string path)
    {
        name = Path.GetFileName(path);
        if (!started.Value) throw new ToolError(L("Couldn't read %@", name));
        IntPtr attributes = IntPtr.Zero, type = IntPtr.Zero, current = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attributes, 1));
            Check(SetUInt32(attributes, AdvancedVideoProcessing, 1));
            Check(MFCreateSourceReaderFromURL(path, attributes, out reader));
            Check(SetStreamSelection(reader, AllStreams, false));
            Check(SetStreamSelection(reader, FirstVideoStream, true));

            // The track's display rotation (from the MP4 / MOV matrix) is only on the native type.
            uint degrees = 0;
            ulong nativeSize = 0;
            if (GetNativeMediaType(reader, FirstVideoStream, 0, out var native) >= 0)
            {
                if (GetUInt32(native, VideoRotation, out var d) >= 0) degrees = d % 360;
                GetUInt64(native, FrameSize, out nativeSize);
                Marshal.Release(native);
            }

            Check(MFCreateMediaType(out type));
            Check(SetGuid(type, MajorType, VideoMajor));
            Check(SetGuid(type, Subtype, Rgb32));
            Check(SetCurrentMediaType(reader, FirstVideoStream, type));
            ReadCurrentType();

            // The advanced video processor applies the rotation itself (the RGB frames come out turned, so their
            // size is swapped); otherwise Swivel turns them.
            bool swapped = nativeSize != 0 && (int)(nativeSize >> 32) == height && (int)(nativeSize & 0xFFFFFFFF) == width && width != height;
            bool applied = degrees is 90 or 270 ? swapped : degrees == 180 && ProcessorRotates180;
            TrackRotation = (int)degrees;
            Rotation = applied ? 0 : (int)degrees;

            var duration = new PropVariant();
            if (GetPresentationAttribute(reader, MediaSource, Duration100ns, &duration) >= 0)
            {
                Duration = TimeSpan.FromTicks(duration.Value);
                PropVariantClear(&duration);
            }
        }
        // DllNotFoundException: Windows "N" editions without the Media Feature Pack have no Media Foundation.
        catch (Exception e) when (e is COMException or ToolError or DllNotFoundException or EntryPointNotFoundException)
        {
            Dispose();
            throw new ToolError(L("Couldn't read %@", name));
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (type != IntPtr.Zero) Marshal.Release(type);
            if (current != IntPtr.Zero) Marshal.Release(current);
        }
    }

    void ReadCurrentType()
    {
        Check(GetCurrentMediaType(reader, FirstVideoStream, out var current));
        try
        {
            Check(GetUInt64(current, FrameSize, out var size));
            width = (int)(size >> 32); height = (int)(size & 0xFFFFFFFF);
            stride = GetUInt32(current, DefaultStride, out var s) >= 0 ? (int)s : width * 4;
            aperture = (0, 0, width, height);
            var area = stackalloc byte[16];
            uint got;
            if (GetBlob(current, DisplayAperture, area, 16, &got) >= 0 && got >= 16)
            {
                // MFVideoArea: MFOffset x, y (fixed 16.16: fract, value) then SIZE cx, cy.
                int x = *(short*)(area + 2), y = *(short*)(area + 6), cx = *(int*)(area + 8), cy = *(int*)(area + 12);
                if (cx > 0 && cy > 0 && x >= 0 && y >= 0 && x + cx <= width && y + cy <= height) aperture = (x, y, cx, cy);
            }
            if (GetUInt64(current, PixelAspect, out var ratio) >= 0)
            {
                double n = ratio >> 32, d = ratio & 0xFFFFFFFF;
                if (n > 0 && d > 0) pixelAspect = n / d;
            }
            DisplayWidthRaw = Math.Max(1, (int)Math.Round(aperture.W * pixelAspect));
            DisplayHeightRaw = aperture.H;
        }
        finally { Marshal.Release(current); }
    }

    /// The frame shown at `time` (the last frame starting at or before it), upright, long edge at most maxPixels.
    public Raster FrameAt(TimeSpan time, int? maxPixels = null)
    {
        Seek(time);
        Raster? last = null;
        long target = time.Ticks;
        while (Read(out var timestamp, out var duration, out var frame))
        {
            if (frame is null) continue;
            last = frame;
            if (timestamp + Math.Max(duration, 1) > target) break;
        }
        return Finish(last ?? throw new ToolError(L("Couldn't read %@", name)), maxPixels);
    }

    /// Frames at each of `times` (ascending), decoded in one pass from the first; `onFrame` gets index and frame.
    public int FramesAt(IReadOnlyList<TimeSpan> times, int? maxPixels, Action<int, Raster> onFrame)
    {
        if (times.Count == 0) return 0;
        Seek(times[0]);
        int index = 0;
        Raster? previous = null;
        while (index < times.Count && Read(out var timestamp, out var duration, out var frame))
        {
            if (frame is null) continue;
            long end = timestamp + Math.Max(duration, 1);
            Raster? prepared = null;
            while (index < times.Count && end > times[index].Ticks)
            {
                prepared ??= Finish(frame, maxPixels);
                onFrame(index++, prepared);
            }
            previous = frame;
        }
        // The stream ended early (durations are estimates): repeat the last frame for what's left.
        if (index < times.Count && previous != null)
        {
            var last = Finish(previous, maxPixels);
            while (index < times.Count) onFrame(index++, last);
        }
        return index;
    }

    Raster Finish(Raster frame, int? maxPixels)
    {
        var raster = frame.Cropped(aperture.X, aperture.Y, aperture.W, aperture.H);
        if (DisplayWidthRaw != raster.Width) raster = raster.Resized(DisplayWidthRaw, DisplayHeightRaw);
        raster = Rotation switch { 90 => raster.RotatedRight(), 180 => raster.Rotated180(), 270 => raster.RotatedLeft(), _ => raster };
        if (maxPixels is int max && Math.Max(raster.Width, raster.Height) > max)
        {
            var (w, h) = Images.Fit(raster.Width, raster.Height, max);
            raster = raster.Resized(w, h);
        }
        return raster;
    }

    void Seek(TimeSpan time)
    {
        var position = new PropVariant { Type = 20 /* VT_I8 */, Value = Math.Max(0, time.Ticks) };
        var format = Guid.Empty;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, PropVariant*, int>)Slot(reader, 8))(reader, &format, &position));
    }

    /// Reads the next sample; false at the end of the stream. `frame` is null for gaps (stream ticks).
    bool Read(out long timestamp, out long duration, out Raster? frame)
    {
        frame = null; timestamp = 0; duration = 0;
        uint actual, flags;
        long time;
        IntPtr sample;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
            reader, FirstVideoStream, 0, &actual, &flags, &time, &sample));
        if ((flags & FlagError) != 0) throw new ToolError(L("Couldn't read %@", name));
        if ((flags & FlagCurrentTypeChanged) != 0) ReadCurrentType();
        if (sample == IntPtr.Zero) return (flags & FlagEndOfStream) == 0;
        try
        {
            timestamp = time;
            long d;
            if (((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(sample, 37))(sample, &d) >= 0) duration = d;
            IntPtr buffer;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer));
            try
            {
                byte* data; uint max, length;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length));
                try { frame = Copy(data, length); }
                finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); }
            }
            finally { Marshal.Release(buffer); }
        }
        finally { Marshal.Release(sample); }
        return true;
    }

    Raster Copy(byte* data, uint length)
    {
        int rowBytes = width * 4, pitch = Math.Abs(stride);
        if (pitch < rowBytes) pitch = rowBytes;
        if ((long)pitch * height > length)
        {
            // Some decoders pad rows without reporting a stride: derive it from the buffer size.
            pitch = (int)(length / (uint)Math.Max(height, 1));
            if (pitch < rowBytes) throw new ToolError(L("Couldn't read %@", name));
        }
        var pixels = new int[width * height];
        fixed (int* target = pixels)
        {
            for (int y = 0; y < height; y++)
            {
                // A negative stride means the buffer starts with the bottom row.
                byte* row = stride < 0 ? data + (long)(height - 1 - y) * pitch : data + (long)y * pitch;
                Buffer.MemoryCopy(row, target + (long)y * width, rowBytes, rowBytes);
            }
            // RGB32's fourth byte is padding: make every pixel opaque.
            for (long i = 0; i < pixels.Length; i++) target[i] |= unchecked((int)0xFF000000);
        }
        return new Raster(width, height, pixels, false);
    }

    public void Dispose()
    {
        if (reader != IntPtr.Zero) { Marshal.Release(reader); reader = IntPtr.Zero; }
    }

    // MARK: vtable calls

    static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    static void Check(int hr)
    {
        if (hr < 0) throw new COMException("Media Foundation", hr);
    }

    // IMFAttributes: GetUINT32 7, GetUINT64 8, GetBlob 15, SetUINT32 21, SetGUID 24.
    static int GetUInt32(IntPtr a, Guid key, out uint value)
    {
        uint v; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(a, 7))(a, &key, &v);
        value = v; return hr;
    }

    static int GetUInt64(IntPtr a, Guid key, out ulong value)
    {
        ulong v; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(a, 8))(a, &key, &v);
        value = v; return hr;
    }

    static int GetBlob(IntPtr a, Guid key, byte* buffer, uint size, uint* got) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)Slot(a, 15))(a, &key, buffer, size, got);

    static int SetUInt32(IntPtr a, Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &key, value);

    static int SetGuid(IntPtr a, Guid key, Guid value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &key, &value);

    // IMFSourceReader: SetStreamSelection 4, GetNativeMediaType 5, GetCurrentMediaType 6, SetCurrentMediaType 7,
    // SetCurrentPosition 8, ReadSample 9, GetPresentationAttribute 12.
    static int SetStreamSelection(IntPtr r, uint stream, bool selected) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(r, 4))(r, stream, selected ? 1 : 0);

    static int GetNativeMediaType(IntPtr r, uint stream, uint index, out IntPtr type)
    {
        IntPtr t; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Slot(r, 5))(r, stream, index, &t);
        type = t; return hr;
    }

    static int GetCurrentMediaType(IntPtr r, uint stream, out IntPtr type)
    {
        IntPtr t; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(r, 6))(r, stream, &t);
        type = t; return hr;
    }

    static int SetCurrentMediaType(IntPtr r, uint stream, IntPtr type) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Slot(r, 7))(r, stream, null, type);

    static int GetPresentationAttribute(IntPtr r, uint stream, Guid key, PropVariant* value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, PropVariant*, int>)Slot(r, 12))(r, stream, &key, value);
}
