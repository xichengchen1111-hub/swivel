using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Swivel.Core;

/// Adds the one AAC track of an M4A (made by Windows from the time-stretched sound) to a silent,
/// retimed MP4 with one video track, without re-encoding either. The output is the video file's
/// top-level boxes except its movie box, copied in order, then one new media data box holding the
/// M4A's sample data, then a new movie box: the video track (chunk offsets moved by where its data
/// now lies) and the audio track (new track ID, durations in the video's movie timescale, chunk
/// offsets into the new media data box, 64-bit when they need it). Sample tables, codec setup and
/// edit-list media times are kept byte for byte. Fragments and unknown top-level boxes are refused.
public static class Mp4AudioMux
{
    public const int MaxMovie = 64 << 20;
    public const string Failed = "Windows' sound for this speed could not be added to the video. No file was saved. Remove the sound to save the video without it.";
    private static readonly HashSet<string> TopLevel = new() { "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid" };
    private static readonly HashSet<string> Containers = new() { "trak", "mdia", "minf", "stbl", "edts" };

    private readonly record struct Box(string Type, long At, long Size, int Head, bool ToEnd);

    /// The movie's duration and timescale (mvhd) of a single-movie-box MP4.
    public static (ulong Duration, uint Timescale) Length(Stream stream)
    {
        var (_, movie) = Movie(stream, Top(stream));
        var (ms, me) = Body(movie, 0, movie.Length, "moov");
        foreach (var (type, s, _) in Children(movie, ms, me))
            if (type == "mvhd") return MovieHeader(movie, s);
        Fail(); return default;
    }

    /// Writes video + audio to `output` and returns the bytes written.
    public static long Write(Stream video, Stream audio, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { audio.Position = 0; M4aAudio.Inspect(audio, token); } catch (UserError) { Fail(); }
        var videoTop = Top(video); var audioTop = Top(audio);
        var (_, videoMovie) = Movie(video, videoTop);
        var (_, audioMovie) = Movie(audio, audioTop);

        // New places of the copied video boxes.
        var placed = new List<(Box Box, long NewAt)>();
        long at = 0;
        foreach (var box in videoTop)
        {
            if (box.Type == "moov") continue;
            placed.Add((box, at)); at += box.Size;
        }
        // The new media data box: every audio mdat payload in order.
        var audioData = new List<(long Start, long End, long NewStart)>();
        long payload = 0;
        foreach (var box in audioTop) if (box.Type == "mdat") { audioData.Add((box.At + box.Head, box.At + box.Size, payload)); payload += box.Size - box.Head; }
        if (audioData.Count == 0) Fail();
        int audioHead = payload + 8 > uint.MaxValue ? 16 : 8;
        long audioPayloadAt = at + audioHead;

        long VideoOffset(ulong offset)
        {
            foreach (var (box, newAt) in placed)
                if (box.Type == "mdat" && offset >= (ulong)(box.At + box.Head) && offset < (ulong)(box.At + box.Size)) return (long)offset - box.At + newAt;
            Fail(); return 0;
        }
        long AudioOffset(ulong offset)
        {
            foreach (var (start, end, newStart) in audioData)
                if (offset >= (ulong)start && offset < (ulong)end) return audioPayloadAt + newStart + ((long)offset - start);
            Fail(); return 0;
        }

        // Video movie: one video track; its chunk offsets move in place (they never grow, since
        // only the movie box before them, if any, is removed).
        var (vs, ve) = Body(videoMovie, 0, videoMovie.Length, "moov");
        int? mvhd = null; int videoTrak = -1, videoTrakEnd = 0;
        foreach (var (type, s, e) in Children(videoMovie, vs, ve))
        {
            if (type == "mvhd") { if (mvhd is not null) Fail(); mvhd = s; }
            else if (type is "trak") { if (videoTrak >= 0) Fail(); videoTrak = s; videoTrakEnd = e; }
            else if (type is "mvex" or "meco") Fail();
        }
        if (mvhd is not { } mv || videoTrak < 0) { Fail(); return 0; }
        var (videoDuration, videoScale) = MovieHeader(videoMovie, mv);
        if (videoScale == 0) Fail();
        Track(videoMovie, videoTrak, videoTrakEnd, out var vt);
        if (vt.Handler != "vide") Fail();
        uint videoId = vt.Id;
        foreach (int field in Offsets(videoMovie, vt))
        {
            long moved = VideoOffset(Read(videoMovie, field, vt.Wide ? 8 : 4));
            if (!vt.Wide && moved > uint.MaxValue) Fail();
            Write(videoMovie, field, vt.Wide ? 8 : 4, (ulong)moved);
        }

        // Audio track, rebuilt with its new ID, durations and offsets.
        var (asStart, asEnd) = Body(audioMovie, 0, audioMovie.Length, "moov");
        int audioTrak = -1, audioTrakEnd = 0; uint audioScale = 0;
        foreach (var (type, s, e) in Children(audioMovie, asStart, asEnd))
        {
            if (type == "mvhd") audioScale = MovieHeader(audioMovie, s).Timescale;
            else if (type == "trak") { if (audioTrak >= 0) Fail(); audioTrak = s; audioTrakEnd = e; }
        }
        if (audioTrak < 0 || audioScale == 0) Fail();
        Track(audioMovie, audioTrak, audioTrakEnd, out var at2);
        if (at2.Handler != "soun") Fail();
        uint next = NextTrack(videoMovie, mv);
        uint audioId = next is 0 or uint.MaxValue || next == videoId ? videoId + 1 : next;
        if (audioId == 0 || audioId == uint.MaxValue) Fail();
        ulong Rescale(ulong value)
        {
            BigInteger r = ((BigInteger)value * videoScale + audioScale / 2) / audioScale;
            if (r >= uint.MaxValue) Fail();
            return (ulong)r;
        }
        var newOffsets = new List<long>();
        foreach (int field in Offsets(audioMovie, at2)) newOffsets.Add(AudioOffset(Read(audioMovie, field, at2.Wide ? 8 : 4)));
        bool wide = false; foreach (long o in newOffsets) if (o > uint.MaxValue) wide = true;
        ulong audioDuration = 0;
        byte[] Leaf(string type, int start, int end)
        {
            var copy = audioMovie.AsSpan(start - 8, end - start + 8).ToArray(); int body = 8;
            if (type == "tkhd")
            {
                bool v1 = copy[body] == 1; if (copy[body] > 1) Fail();
                copy[body + 3] |= 3; // enabled, in movie
                int id = body + (v1 ? 20 : 12), duration = body + (v1 ? 28 : 20);
                Need(copy, duration, v1 ? 8 : 4);
                Write(copy, id, 4, audioId);
                ulong d = Read(copy, duration, v1 ? 8 : 4); if (d == (v1 ? ulong.MaxValue : uint.MaxValue)) Fail();
                audioDuration = Rescale(d); Write(copy, duration, v1 ? 8 : 4, audioDuration);
            }
            else if (type == "elst")
            {
                Need(copy, body, 8); bool v1 = copy[body] == 1; if (copy[body] > 1) Fail();
                int entry = v1 ? 20 : 12; long n = Read(copy, body + 4, 4) is var c && c <= (ulong)((copy.Length - body - 8) / entry) ? (long)c : -1;
                if (n < 0) Fail();
                for (int i = 0; i < n; i++) { int e = body + 8 + i * entry; Write(copy, e, v1 ? 8 : 4, Rescale(Read(copy, e, v1 ? 8 : 4))); }
            }
            else if (type is "stco" or "co64")
            {
                var box = new byte[16 + newOffsets.Count * (wide ? 8 : 4)];
                Write(box, 0, 4, (ulong)box.Length); box[4] = (byte)'s'; box[5] = (byte)'t'; box[6] = (byte)'c'; box[7] = (byte)'o';
                if (wide) { box[4] = (byte)'c'; box[5] = (byte)'o'; box[6] = (byte)'6'; box[7] = (byte)'4'; }
                Write(box, 12, 4, (ulong)newOffsets.Count);
                for (int i = 0; i < newOffsets.Count; i++) Write(box, 16 + i * (wide ? 8 : 4), wide ? 8 : 4, (ulong)newOffsets[i]);
                return box;
            }
            else if (type == "tref") Fail();
            return copy;
        }
        byte[] audioTrack = Rebuild(audioMovie, "trak", audioTrak, audioTrakEnd, Leaf);
        if (audioDuration == 0) Fail();

        // Movie header: the longer duration and the next free track ID.
        {
            bool v1 = videoMovie[mv] == 1; int duration = mv + (v1 ? 24 : 16);
            ulong longer = Math.Max(videoDuration, audioDuration);
            if (!v1 && longer >= uint.MaxValue) Fail();
            Write(videoMovie, duration, v1 ? 8 : 4, longer);
            Write(videoMovie, mv + (v1 ? 108 : 96), 4, Math.Max(audioId, videoId) + 1UL);
        }
        // The audio track goes last in the movie box.
        long movieSize = (long)videoMovie.Length + audioTrack.Length;
        if (movieSize > MaxMovie || ve != videoMovie.Length) Fail();

        // Write: copied video boxes, audio media data, movie box.
        var buffer = new byte[1 << 20]; long written = 0;
        void Copy(Stream from, long start, long count)
        {
            from.Position = start;
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, count); from.ReadExactly(buffer, 0, n); output.Write(buffer, 0, n); count -= n; written += n;
            }
        }
        foreach (var (box, _) in placed)
        {
            if (box.ToEnd)
            {
                // A box "to the end of the file" gets its real size, since more boxes follow.
                if (box.Head != 8 || box.Size > uint.MaxValue) Fail();
                var header = new byte[8]; Write(header, 0, 4, (ulong)box.Size); video.Position = box.At + 4; video.ReadExactly(header, 4, 4);
                output.Write(header); written += 8; Copy(video, box.At + 8, box.Size - 8);
            }
            else Copy(video, box.At, box.Size);
        }
        var mdat = new byte[audioHead];
        if (audioHead == 8) Write(mdat, 0, 4, (ulong)(payload + 8)); else { Write(mdat, 0, 4, 1); Write(mdat, 8, 8, (ulong)(payload + 16)); }
        mdat[4] = (byte)'m'; mdat[5] = (byte)'d'; mdat[6] = (byte)'a'; mdat[7] = (byte)'t';
        output.Write(mdat); written += audioHead;
        foreach (var (start, end, _) in audioData) Copy(audio, start, end - start);
        if (written != audioPayloadAt + payload) Fail();
        Write(videoMovie, 0, 4, (ulong)movieSize);
        if (Read(videoMovie, 0, 4) != (ulong)movieSize || videoMovie[4] != 'm') Fail();
        output.Write(videoMovie); output.Write(audioTrack); written += movieSize;
        token.ThrowIfCancellationRequested();
        return written;
    }

    private sealed class TrackInfo
    {
        public string Handler = ""; public uint Id; public int ChunkOffsets = -1, ChunkOffsetsEnd; public bool Wide;
    }

    private static void Track(byte[] b, int start, int end, out TrackInfo info)
    {
        info = new TrackInfo(); int? tkhd = null;
        foreach (var (type, s, e) in Children(b, start, end))
        {
            if (type == "tkhd") { if (tkhd is not null) Fail(); tkhd = s; }
            else if (type == "mdia")
                foreach (var (t2, s2, e2) in Children(b, s, e))
                {
                    if (t2 == "hdlr") { Need(b, s2, 12); info.Handler = Type(b, s2 + 8); }
                    else if (t2 == "minf")
                        foreach (var (t3, s3, e3) in Children(b, s2, e2))
                            if (t3 == "stbl")
                                foreach (var (t4, s4, e4) in Children(b, s3, e3))
                                    if (t4 is "stco" or "co64") { if (info.ChunkOffsets >= 0) Fail(); info.ChunkOffsets = s4; info.ChunkOffsetsEnd = e4; info.Wide = t4 == "co64"; }
                }
        }
        if (tkhd is not { } th || info.ChunkOffsets < 0) { Fail(); return; }
        Need(b, th, 1); bool v1 = b[th] == 1; if (b[th] > 1) Fail();
        Need(b, th + (v1 ? 20 : 12), 4); info.Id = (uint)Read(b, th + (v1 ? 20 : 12), 4);
    }

    // Each chunk offset field of a track.
    private static List<int> Offsets(byte[] b, TrackInfo t)
    {
        Need(b, t.ChunkOffsets, 8); if (b[t.ChunkOffsets] != 0) Fail();
        ulong count = Read(b, t.ChunkOffsets + 4, 4); int width = t.Wide ? 8 : 4;
        if (count > (ulong)((t.ChunkOffsetsEnd - t.ChunkOffsets - 8) / width)) Fail();
        var list = new List<int>((int)count);
        for (int i = 0; i < (int)count; i++) list.Add(t.ChunkOffsets + 8 + i * width);
        return list;
    }

    private static (ulong Duration, uint Timescale) MovieHeader(byte[] b, int s)
    {
        Need(b, s, 1); bool v1 = b[s] == 1; if (b[s] > 1) Fail();
        Need(b, s + (v1 ? 108 : 96), 4);
        return (Read(b, s + (v1 ? 24 : 16), v1 ? 8 : 4), (uint)Read(b, s + (v1 ? 20 : 12), 4));
    }

    private static uint NextTrack(byte[] b, int s) => (uint)Read(b, s + (b[s] == 1 ? 108 : 96), 4);

    // The box [start - 8, end) rebuilt: containers recursively, leaves through `leaf`.
    private static byte[] Rebuild(byte[] b, string type, int start, int end, Func<string, int, int, byte[]> leaf)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[8]);
        foreach (var (t, s, e) in Children(b, start, end))
        {
            stream.Write(Containers.Contains(t) ? Rebuild(b, t, s, e, leaf) : leaf(t, s, e));
        }
        var bytes = stream.ToArray();
        Write(bytes, 0, 4, (ulong)bytes.Length);
        for (int i = 0; i < 4; i++) bytes[4 + i] = (byte)type[i];
        return bytes;
    }

    // Top-level boxes of a whole file.
    private static List<Box> Top(Stream stream)
    {
        var list = new List<Box>(); long length = stream.Length, at = 0; var header = new byte[16];
        while (at < length)
        {
            if (length - at < 8 || list.Count >= 10_000) Fail();
            stream.Position = at; stream.ReadExactly(header, 0, 8);
            long size = (long)Read(header, 0, 4); string type = Type(header, 4); int head = 8; bool toEnd = false;
            if (size == 1) { if (length - at < 16) Fail(); stream.ReadExactly(header, 8, 8); ulong large = Read(header, 8, 8); size = large > long.MaxValue ? -1 : (long)large; head = 16; }
            else if (size == 0) { size = length - at; toEnd = true; }
            if (size < head || size > length - at || !TopLevel.Contains(type)) Fail();
            list.Add(new Box(type, at, size, head, toEnd));
            at += size;
        }
        return list;
    }

    private static (long At, byte[] Bytes) Movie(Stream stream, List<Box> top)
    {
        Box? moov = null;
        foreach (var box in top) if (box.Type == "moov") { if (moov is not null) Fail(); moov = box; }
        if (moov is not { } m || m.Size > MaxMovie || m.Head != 8 || m.ToEnd) { Fail(); return default; }
        var bytes = new byte[m.Size]; stream.Position = m.At; stream.ReadExactly(bytes);
        return (m.At, bytes);
    }

    private static (int Start, int End) Body(byte[] b, int start, int end, string type)
    {
        var children = Children(b, start, end);
        if (children.Count != 1 || children[0].Type != type) Fail();
        return (children[0].Start, children[0].End);
    }

    private static List<(string Type, int Start, int End)> Children(byte[] b, int start, int end)
    {
        var list = new List<(string, int, int)>(); int at = start;
        while (at < end)
        {
            if (end - at < 8) Fail();
            // Inside a movie box (at most 64 MiB) every box has a 32-bit size, so each header is 8 bytes.
            long size = (long)Read(b, at, 4);
            if (size < 8 || size > end - at) Fail();
            list.Add((Type(b, at + 4), at + 8, at + (int)size));
            at += (int)size;
        }
        return list;
    }

    private static void Need(byte[] b, int at, int count) { if (at < 0 || count > b.Length - at) Fail(); }
    private static ulong Read(byte[] b, int at, int width)
    {
        Need(b, at, width); ulong v = 0;
        for (int i = 0; i < width; i++) v = v << 8 | b[at + i];
        return v;
    }
    private static void Write(byte[] b, int at, int width, ulong value)
    {
        Need(b, at, width);
        for (int i = width - 1; i >= 0; i--) { b[at + i] = (byte)value; value >>= 8; }
    }
    private static string Type(byte[] b, int at) { Need(b, at, 4); return string.Create(4, (b, at), (span, s) => { for (int i = 0; i < 4; i++) span[i] = (char)s.b[s.at + i]; }); }
    private static void Fail() => throw new UserError(Failed);
}
