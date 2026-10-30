using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// HEIC/HEIF/AVIF pixels come only from Microsoft's HEIF decoder in WIC (CLSID_WICHeifDecoder),
/// created by its CLSID after its component info names Microsoft as vendor and author; WIC is
/// never asked to pick a codec by content, so a third-party HEIF codec is never run. The file has
/// already passed Swivel's own box gate (HeifImage.Inspect). The decoder needs Microsoft's HEIF
/// Image Extensions plus an HEVC (HEIC) or AV1 (AVIF) video decoder from the Microsoft Store;
/// their absence is reported, never installed.
internal static class HeifDecoder
{
    internal static readonly Guid HeifDecoderClsid = new("E9A4A80A-44FE-4DE4-8971-7150B10A5199");
    internal static readonly Guid HeifEncoderClsid = new("0DBECEC1-9EB3-4860-9C6F-DDBE86634575");
    internal static readonly Guid ContainerHeif = new("E1E62521-6787-405B-A339-500715B5763F");
    internal static readonly Guid VendorMicrosoft = new("F0E749CA-EDEF-4589-A73A-EE0E626A2A2B");
    private static readonly Guid FactoryClsid = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    internal static readonly Guid Bgra32 = Format(0x0F);
    private static Guid Format(byte last) => new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, last);
    // 8-bit SDR formats; the ones with an alpha channel are listed separately.
    private static readonly Guid[] AlphaFormats = { Format(0x0F), Format(0x10), new("F5C7AD2D-6A8D-43DD-A7A8-A29935261AE9") };
    private static readonly Guid[] OpaqueFormats = { Format(0x08), Format(0x0C), Format(0x0D), Format(0x0E), new("D98C6B95-3EFE-47D6-BB25-EB1748AB0CF1") };

    internal const string NotMicrosoft = "The HEIF decoder registered in Windows is not Microsoft's, so Swivel doesn't use it.";
    internal const string ProfileFailed = "Windows couldn't apply this picture's colour profile.";

    /// Tests replace the codec check to exercise the not-installed messages on any machine.
    internal static Func<HeifCodec, bool>? AvailabilityOverride;

    internal sealed record DecoderFacts(bool Present, Guid Clsid, Guid Vendor, string Author, string FriendlyName, string Version, string Extensions, Guid Container, string? Problem)
    {
        internal bool Trusted => Present && Problem is null;
    }

    /// What Windows reports about its HEIF decoder, without decoding anything.
    internal static DecoderFacts Describe()
    {
        IWICImagingFactory? factory = null; object? info = null;
        try
        {
            factory = Factory();
            try { factory.CreateComponentInfo(HeifDecoderClsid, out info); }
            catch (COMException) { return new(false, Guid.Empty, Guid.Empty, "", "", "", "", Guid.Empty, "not registered"); }
            var decoder = (IWICBitmapDecoderInfo)info;
            decoder.GetCLSID(out Guid clsid); decoder.GetVendorGUID(out Guid vendor); decoder.GetContainerFormat(out Guid container);
            string author = Text(decoder.GetAuthor), name = Text(decoder.GetFriendlyName), version = Text(decoder.GetVersion), extensions = Text(decoder.GetFileExtensions);
            string? problem = clsid != HeifDecoderClsid ? "unexpected CLSID" : vendor != VendorMicrosoft ? "vendor is not Microsoft"
                : !author.StartsWith("Microsoft", StringComparison.Ordinal) ? "author is not Microsoft" : container != ContainerHeif ? "container is not HEIF" : null;
            return new(true, clsid, vendor, author, name, version, extensions, container, problem);
        }
        finally { Release(info); Release(factory); }
    }

    /// Whether Media Foundation lists a video decoder for HEVC or AV1 (Microsoft documents this
    /// check for the HEIF codec's dependencies).
    internal static int VideoDecoders(HeifCodec codec) => Transforms(new("D6C02D4B-6833-45B4-971A-05A4B04BAB91"), codec, input: true);
    internal static int VideoEncoders(HeifCodec codec) => Transforms(new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A"), codec, input: false);

    private static int Transforms(Guid category, HeifCodec codec, bool input)
    {
        var type = new RegisterTypeInfo { Major = new("73646976-0000-0010-8000-00AA00389B71"),
            Sub = codec == HeifCodec.Hevc ? new("43564548-0000-0010-8000-00AA00389B71") : new("31305641-0000-0010-8000-00AA00389B71") };
        // MFT_ENUM_FLAG_SYNCMFT | ASYNCMFT | HARDWARE | SORTANDFILTER
        int hr = input ? MFTEnumExInput(category, 0x47, ref type, IntPtr.Zero, out IntPtr activates, out uint count)
            : MFTEnumExOutput(category, 0x47, IntPtr.Zero, ref type, out activates, out count);
        if (hr < 0) return 0;
        try { for (int i = 0; i < count; i++) { IntPtr item = Marshal.ReadIntPtr(activates, i * IntPtr.Size); if (item != IntPtr.Zero) Marshal.Release(item); } }
        finally { if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates); }
        return (int)count;
    }

    private static bool Available(HeifCodec codec) => AvailabilityOverride?.Invoke(codec) ?? VideoDecoders(codec) > 0;

    /// Decodes the primary image to straight BGRA in sRGB, as stored (before rotation). The caller
    /// applies the orientation only when `oriented` is false.
    internal static PixelImage Read(Stream stream, HeifInfo info, bool readOnly, CancellationToken token, out bool oriented)
    {
        token.ThrowIfCancellationRequested();
        string missing = info.Codec == HeifCodec.Hevc ? HeifImage.HeicMissing : HeifImage.AvifMissing;
        if (!Available(info.Codec)) throw new UserError(missing);
        var facts = Describe();
        if (!facts.Present) throw new UserError(missing);
        if (!facts.Trusted) throw new UserError(NotMicrosoft);
        // An older HEIF extension without AV1 support doesn't list .avif.
        if (info.Codec == HeifCodec.Av1 && !facts.Extensions.Split(',').Any(e => e.Trim().Equals(".avif", StringComparison.OrdinalIgnoreCase))) throw new UserError(HeifImage.AvifMissing);

        byte[] file = new byte[stream.Length];
        stream.Position = 0; stream.ReadExactly(file);
        var handle = GCHandle.Alloc(file, GCHandleType.Pinned);
        var owned = new List<object?>();
        T Own<T>(T value) { owned.Add(value); return value; }
        try
        {
            var factory = Own(Factory());
            factory.CreateComponentInfo(HeifDecoderClsid, out object infoObject); Own(infoObject);
            var decoderInfo = (IWICBitmapDecoderInfo)infoObject;
            decoderInfo.CreateInstance(out IWICBitmapDecoder decoder); Own(decoder);
            factory.CreateStream(out IWICStream wicStream); Own(wicStream);
            wicStream.InitializeFromMemory(handle.AddrOfPinnedObject(), (uint)file.Length);
            token.ThrowIfCancellationRequested();
            IWICBitmapFrameDecode frame;
            try
            {
                decoder.Initialize(wicStream, 0 /* WICDecodeMetadataCacheOnDemand */);
                decoder.GetFrameCount(out uint frames);
                if (frames != 1) throw new UserError("Multi-frame images are not supported yet.");
                decoder.GetFrame(0, out frame); Own(frame);
            }
            catch (COMException) { throw new UserError(HeifImage.DecodeFailed); }
            frame.GetSize(out uint width, out uint height);
            // The decoder either returns the coded size, or applies a quarter-turn itself.
            if (width == info.Width && height == info.Height) oriented = info.Orientation == 1;
            else if (width == info.Height && height == info.Width && HeifImage.SwapsSides(info.Orientation)) oriented = true;
            else throw new UserError(HeifImage.Unsupported);
            frame.GetPixelFormat(out Guid format);
            bool alpha = AlphaFormats.Contains(format), sdr = alpha || OpaqueFormats.Contains(format);
            if (!readOnly && !sdr) throw new UserError(HeifImage.HighDepth);
            if (!readOnly && info.HasAlpha && !alpha) throw new UserError(HeifImage.AlphaUnsupported);
            token.ThrowIfCancellationRequested();
            IWICBitmapSource source = frame;
            try { if (Profile(factory, frame, owned) is { } transform) source = transform; }
            catch (COMException) when (readOnly) { }
            catch (COMException) { throw new UserError(ProfileFailed); }
            factory.CreateFormatConverter(out IWICFormatConverter converter); Own(converter);
            Guid target = Bgra32;
            converter.Initialize(source, ref target, 0, IntPtr.Zero, 0, 0);
            int stride = checked((int)width * 4);
            var bgra = new byte[checked(stride * (int)height)];
            var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int y = 0; y < height; y += 256)
                {
                    token.ThrowIfCancellationRequested();
                    int rows = (int)Math.Min(256, height - y);
                    var rect = new WicRect { X = 0, Y = y, Width = (int)width, Height = rows };
                    try { converter.CopyPixels(ref rect, (uint)stride, (uint)(rows * stride), pinned.AddrOfPinnedObject() + y * stride); }
                    catch (COMException) { throw new UserError(HeifImage.DecodeFailed); }
                }
            }
            finally { pinned.Free(); }
            return new PixelImage((int)width, (int)height, bgra);
        }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--) Release(owned[i]);
            handle.Free();
        }
    }

    /// A transform from the frame's embedded ICC profile to sRGB, or null without one.
    private static IWICColorTransform? Profile(IWICImagingFactory factory, IWICBitmapFrameDecode frame, List<object?> owned)
    {
        uint count;
        try { frame.GetColorContexts(0, null, out count); } catch (COMException) { return null; }
        if (count == 0 || count > 16) return null;
        var contexts = new IWICColorContext[count];
        for (int i = 0; i < count; i++) { factory.CreateColorContext(out contexts[i]); owned.Add(contexts[i]); }
        frame.GetColorContexts(count, contexts, out uint actual);
        IWICColorContext? profile = null;
        for (int i = 0; i < Math.Min(actual, count) && profile is null; i++) { contexts[i].GetContextType(out int type); if (type == 1 /* WICColorContextProfile */) profile = contexts[i]; }
        if (profile is null) return null;
        factory.CreateColorContext(out IWICColorContext srgb); owned.Add(srgb);
        srgb.InitializeFromExifColorSpace(1);
        factory.CreateColorTransformer(out IWICColorTransform transform); owned.Add(transform);
        Guid target = Bgra32;
        transform.Initialize(frame, profile, srgb, ref target);
        return transform;
    }

    /// Encodes BGRA pixels with Microsoft's HEIF encoder (test fixtures only; Swivel never writes HEIF).
    internal static byte[]? EncodeForTests(PixelImage image)
    {
        IWICImagingFactory? factory = null; IWICBitmapEncoder? encoder = null; IWICBitmapFrameEncode? frame = null; IWICStream? stream = null;
        var buffer = new byte[image.Bgra.Length * 2 + 65536];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            factory = Factory();
            // Microsoft's HEIF encoder only, chosen by container and vendor.
            try { factory.CreateEncoder(ContainerHeif, VendorMicrosoft, out encoder); } catch (COMException) { return null; }
            factory.CreateStream(out stream);
            stream.InitializeFromMemory(handle.AddrOfPinnedObject(), (uint)buffer.Length);
            encoder.Initialize(stream, 2 /* WICBitmapEncoderNoCache */);
            encoder.CreateNewFrame(out frame, IntPtr.Zero);
            frame.Initialize(IntPtr.Zero);
            frame.SetSize((uint)image.Width, (uint)image.Height);
            Guid format = Bgra32; frame.SetPixelFormat(ref format);
            var pixels = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
            try { frame.WritePixels((uint)image.Height, (uint)image.Width * 4, (uint)image.Bgra.Length, pixels.AddrOfPinnedObject()); }
            finally { pixels.Free(); }
            frame.Commit(); encoder.Commit();
            stream.Seek(0, 1 /* STREAM_SEEK_CUR */, out ulong length);
            return buffer.AsSpan(0, (int)length).ToArray();
        }
        catch (COMException) { return null; }
        finally { Release(frame); Release(encoder); Release(stream); Release(factory); handle.Free(); }
    }

    private static IWICImagingFactory Factory() =>
        (IWICImagingFactory)(Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClsid, true)!) ?? throw new UserError(HeifImage.DecodeFailed));

    private delegate void TextGetter(uint length, char[]? buffer, out uint actual);
    private static string Text(TextGetter get)
    {
        try
        {
            get(0, null, out uint length);
            if (length == 0 || length > 4096) return "";
            var buffer = new char[length];
            get(length, buffer, out uint actual);
            return new string(buffer, 0, (int)Math.Min(actual, length)).TrimEnd('\0');
        }
        catch (COMException) { return ""; }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [StructLayout(LayoutKind.Sequential)] private struct RegisterTypeInfo { public Guid Major; public Guid Sub; }
    [StructLayout(LayoutKind.Sequential)] internal struct WicRect { public int X, Y, Width, Height; }

    [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MFTEnumExInput(Guid category, uint flags, ref RegisterTypeInfo input, IntPtr output, out IntPtr activates, out uint count);
    [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MFTEnumExOutput(Guid category, uint flags, IntPtr input, ref RegisterTypeInfo output, out IntPtr activates, out uint count);

    // ---- WIC interfaces, every method in vtable order (unused ones are placeholders) ----

    [ComImport, Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICImagingFactory
    {
        void CreateDecoderFromFilename(); void CreateDecoderFromStream(); void CreateDecoderFromFileHandle();
        void CreateComponentInfo([In] in Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] out object info);
        void CreateDecoder();
        void CreateEncoder([In] in Guid container, [In] in Guid vendor, out IWICBitmapEncoder encoder);
        void CreatePalette();
        void CreateFormatConverter(out IWICFormatConverter converter);
        void CreateBitmapScaler(); void CreateBitmapClipper(); void CreateBitmapFlipRotator();
        void CreateStream(out IWICStream stream);
        void CreateColorContext(out IWICColorContext context);
        void CreateColorTransformer(out IWICColorTransform transform);
    }

    [ComImport, Guid("D8CD007F-D08F-4191-9BFC-236EA7F0E4B5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapDecoderInfo
    {
        void GetComponentType(out int type);
        void GetCLSID(out Guid clsid);
        void GetSigningStatus(out uint status);
        void GetAuthor(uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        void GetVendorGUID(out Guid vendor);
        void GetVersion(uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        void GetSpecVersion(uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        void GetFriendlyName(uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        void GetContainerFormat(out Guid container);
        void GetPixelFormats(); void GetColorManagementVersion(); void GetDeviceManufacturer(); void GetDeviceModels(); void GetMimeTypes();
        void GetFileExtensions(uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[]? buffer, out uint actual);
        void DoesSupportAnimation(); void DoesSupportChromakey(); void DoesSupportLossless(); void DoesSupportMultiframe(); void MatchesMimeType();
        void GetPatterns(); void MatchesPattern();
        void CreateInstance(out IWICBitmapDecoder decoder);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize(IWICStream stream, int options);
        void GetContainerFormat(); void GetDecoderInfo(); void CopyPalette(); void GetMetadataQueryReader(); void GetPreview(); void GetColorContexts(); void GetThumbnail();
        void GetFrameCount(out uint count);
        void GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    [ComImport, Guid("00000120-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
    }

    [ComImport, Guid("3B16811B-6A43-4EC9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapFrameDecode : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        void GetResolution(); void CopyPalette(); void CopyPixels(); void GetMetadataQueryReader();
        void GetColorContexts(uint count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWICColorContext[]? contexts, out uint actual);
    }

    [ComImport, Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICFormatConverter : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        void GetResolution(); void CopyPalette();
        void CopyPixels(ref WicRect rect, uint stride, uint size, IntPtr buffer);
        void Initialize(IWICBitmapSource source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
    }

    [ComImport, Guid("B66F034F-D0E2-40AB-B436-6DE39E321A94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICColorTransform : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        void GetResolution(); void CopyPalette(); void CopyPixels();
        void Initialize(IWICBitmapSource source, IWICColorContext from, IWICColorContext to, ref Guid format);
    }

    [ComImport, Guid("3C613A02-34B2-44EA-9A7C-45AEA9C6FD6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICColorContext
    {
        void InitializeFromFilename(); void InitializeFromMemory();
        void InitializeFromExifColorSpace(uint value);
        void GetContextType(out int type);
    }

    [ComImport, Guid("135FF860-22B7-4DDF-B0F6-218F4F299A43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICStream
    {
        void Read(); void Write();
        void Seek(long move, int origin, out ulong position);
        void SetSize(); void CopyTo(); void Commit(); void Revert(); void LockRegion(); void UnlockRegion(); void Stat(); void Clone();
        void InitializeFromIStream(); void InitializeFromFilename();
        void InitializeFromMemory(IntPtr buffer, uint size);
    }

    [ComImport, Guid("00000103-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapEncoder
    {
        void Initialize(IWICStream stream, int cache);
        void GetContainerFormat(); void GetEncoderInfo(); void SetColorContexts(); void SetPalette(); void SetThumbnail(); void SetPreview();
        void CreateNewFrame(out IWICBitmapFrameEncode frame, IntPtr options);
        void Commit();
    }

    [ComImport, Guid("00000105-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapFrameEncode
    {
        void Initialize(IntPtr options);
        void SetSize(uint width, uint height);
        void SetResolution();
        void SetPixelFormat(ref Guid format);
        void SetColorContexts(); void SetPalette(); void SetThumbnail();
        void WritePixels(uint lines, uint stride, uint size, IntPtr pixels);
        void WriteSource();
        void Commit();
    }
}
