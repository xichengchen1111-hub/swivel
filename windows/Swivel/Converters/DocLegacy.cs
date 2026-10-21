using System.Text;

namespace Swivel;

/// Reads the text of legacy Word documents (.doc, Word 97–2003 binary format, [MS-DOC]) from the compound file's
/// WordDocument stream and its piece table. Only the text comes across (paragraphs, line breaks, tables as tabs,
/// field results); character formatting would need the full CHPX/style machinery, and Windows has no built-in .doc
/// reader to lean on (the Mac app uses Cocoa's). Word 6/95 files and encrypted files are refused.
static class DocLegacy
{
    public static DocModel Read(byte[] data, string name)
    {
        var cfb = new CompoundFile(data, name);
        var word = cfb.Stream("WordDocument") ?? throw new ToolError(L("Couldn't read this document"));
        if (word.Length < 0x1AA || BitConverter.ToUInt16(word, 0) != 0xA5EC) throw new ToolError(L("Couldn't read this document"));
        int nFib = BitConverter.ToUInt16(word, 2);
        ushort flags = BitConverter.ToUInt16(word, 0x0A);
        if ((flags & 0x0100) != 0) throw new ToolError(L("%@ is password protected", name));
        if (nFib < 101) throw new ToolError(L("Couldn't read this document"));
        var table = cfb.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new ToolError(L("Couldn't read this document"));

        // FibBase (32) + csw + fibRgW + cslw + fibRgLw + cbRgFcLcb, then the FcLcb pairs.
        int pos = 32;
        int csw = BitConverter.ToUInt16(word, pos);
        pos += 2 + csw * 2;
        int cslw = BitConverter.ToUInt16(word, pos);
        int rgLw = pos + 2;
        pos += 2 + cslw * 4;
        pos += 2;
        int ccpText = BitConverter.ToInt32(word, rgLw + 3 * 4);
        int fcClx = BitConverter.ToInt32(word, pos + 33 * 8);
        int lcbClx = BitConverter.ToInt32(word, pos + 33 * 8 + 4);
        if (fcClx < 0 || lcbClx <= 0 || fcClx + lcbClx > table.Length) throw new ToolError(L("Couldn't read this document"));

        // Clx: skip Prc entries (0x01), then the piece table (0x02).
        int c = fcClx, end = fcClx + lcbClx;
        while (c < end && table[c] == 0x01) c += 3 + BitConverter.ToInt16(table, c + 1);
        if (c >= end || table[c] != 0x02) throw new ToolError(L("Couldn't read this document"));
        int lcb = BitConverter.ToInt32(table, c + 1);
        int plc = c + 5;
        int pieces = (lcb - 4) / 12;
        var text = new StringBuilder(Math.Max(16, ccpText));
        var cp1252 = DocEncoding.CodePage(1252);
        for (int i = 0; i < pieces; i++)
        {
            int cpStart = BitConverter.ToInt32(table, plc + i * 4);
            int cpEnd = BitConverter.ToInt32(table, plc + (i + 1) * 4);
            if (cpStart >= ccpText) break;
            cpEnd = Math.Min(cpEnd, ccpText);
            int pcd = plc + (pieces + 1) * 4 + i * 8;
            uint fc = BitConverter.ToUInt32(table, pcd + 2);
            bool compressed = (fc & 0x40000000) != 0;
            int count = cpEnd - cpStart;
            if (count <= 0) continue;
            if (compressed)
            {
                int offset = (int)((fc & ~0x40000000u) / 2);
                if (offset + count > word.Length) count = Math.Max(0, word.Length - offset);
                text.Append(cp1252.GetString(word, offset, count));
            }
            else
            {
                int offset = (int)fc;
                if (offset + count * 2 > word.Length) count = Math.Max(0, (word.Length - offset) / 2);
                text.Append(Encoding.Unicode.GetString(word, offset, count * 2));
            }
        }
        return ToModel(text.ToString());
    }

    /// Turns Word's special characters into paragraphs, line breaks and tabs; keeps only field results.
    static DocModel ToModel(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        // Field nesting: true while inside a field's instructions (before its separator).
        var fields = new Stack<bool>();
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '\u0013': fields.Push(true); continue;
                case '\u0014': if (fields.Count > 0) { fields.Pop(); fields.Push(false); } continue;
                case '\u0015': if (fields.Count > 0) fields.Pop(); continue;
            }
            if (fields.Count > 0 && fields.Peek()) continue;
            switch (ch)
            {
                case '\r' or '\u000C' or '\u000E': sb.Append('\r'); break;
                case '\u000B': sb.Append('\n'); break;
                case '\u0007': sb.Append('\u0007'); break;
                case '\t': sb.Append('\t'); break;
                case '\u001E': sb.Append('‑'); break;
                case ' ': sb.Append(' '); break;
                default:
                    if (ch >= ' ') sb.Append(ch);
                    break;
            }
        }
        var model = new DocModel();
        // Paragraphs end with CR. In tables each cell ends with 0x07 instead, and each row with one more 0x07.
        DocTable? table = null;
        List<DocCell>? row = null;
        var cellParagraphs = new List<string>();
        bool lastWasCellMark = false, lastWasRowEnd = false;
        var text = sb.ToString();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\r' or '\u0007')) continue;
            var unit = text[start..i];
            bool cellMark = i < text.Length && text[i] == '\u0007';
            start = i + 1;
            if (i == text.Length && unit.Length == 0) break;
            if (cellMark)
            {
                if (row != null && unit.Length == 0 && cellParagraphs.Count == 0 && lastWasCellMark)
                {
                    table!.Rows.Add(row);
                    row = null;
                    lastWasCellMark = false;
                    lastWasRowEnd = true;
                    continue;
                }
                if (row == null)
                {
                    row = [];
                    if (table == null || !lastWasRowEnd)
                    {
                        table = new DocTable();
                        model.Blocks.Add(table);
                    }
                }
                var cell = new DocCell();
                foreach (var p in cellParagraphs.Append(unit)) cell.Blocks.Add(MakeParagraph(p));
                row.Add(cell);
                cellParagraphs.Clear();
                lastWasCellMark = true;
                lastWasRowEnd = false;
                continue;
            }
            if (row != null)
            {
                // A paragraph inside a cell (the cell's last one ends with the cell mark).
                cellParagraphs.Add(unit);
                lastWasCellMark = false;
                continue;
            }
            table = null;
            lastWasRowEnd = lastWasCellMark = false;
            model.Blocks.Add(MakeParagraph(unit));
        }
        if (row is { Count: > 0 }) table?.Rows.Add(row);
        if (model.Blocks.Count == 0) model.Blocks.Add(new DocParagraph());
        model.Normalize();
        return model;
    }

    static DocParagraph MakeParagraph(string text) => new(text);

    /// A minimal reader for the Compound File Binary format ([MS-CFB]).
    sealed class CompoundFile
    {
        readonly byte[] data;
        readonly int sectorSize, miniSectorSize;
        readonly uint miniCutoff;
        readonly List<uint> fat = [];
        readonly List<uint> miniFat = [];
        readonly List<(string Name, int Type, uint Start, long Size)> entries = [];
        byte[]? miniStream;

        static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

        public CompoundFile(byte[] data, string name)
        {
            this.data = data;
            if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new ToolError(L("Couldn't read this document"));
            sectorSize = 1 << BitConverter.ToUInt16(data, 0x1E);
            miniSectorSize = 1 << BitConverter.ToUInt16(data, 0x20);
            if (sectorSize is not (512 or 4096) || miniSectorSize != 64) throw new ToolError(L("Couldn't read this document"));
            int fatSectors = BitConverter.ToInt32(data, 0x2C);
            uint firstDir = BitConverter.ToUInt32(data, 0x30);
            miniCutoff = BitConverter.ToUInt32(data, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(data, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(data, 0x44);

            var fatSectorList = new List<uint>();
            for (int i = 0; i < 109 && fatSectorList.Count < fatSectors; i++) fatSectorList.Add(BitConverter.ToUInt32(data, 0x4C + i * 4));
            uint difat = firstDifat;
            int guard = 0;
            while (difat < 0xFFFFFFFA && fatSectorList.Count < fatSectors && guard++ < 10000)
            {
                long off = Offset(difat);
                int perSector = sectorSize / 4 - 1;
                for (int i = 0; i < perSector && fatSectorList.Count < fatSectors; i++) fatSectorList.Add(BitConverter.ToUInt32(data, (int)off + i * 4));
                difat = BitConverter.ToUInt32(data, (int)off + perSector * 4);
            }
            foreach (var s in fatSectorList)
            {
                long off = Offset(s);
                if (off + sectorSize > data.Length) break;
                for (int i = 0; i < sectorSize / 4; i++) fat.Add(BitConverter.ToUInt32(data, (int)off + i * 4));
            }
            var miniFatBytes = Chain(firstMiniFat, long.MaxValue);
            for (int i = 0; i + 4 <= miniFatBytes.Length; i += 4) miniFat.Add(BitConverter.ToUInt32(miniFatBytes, i));
            var dir = Chain(firstDir, long.MaxValue);
            for (int i = 0; i + 128 <= dir.Length; i += 128)
            {
                int nameLength = BitConverter.ToUInt16(dir, i + 64);
                var entryName = nameLength >= 2 ? Encoding.Unicode.GetString(dir, i, Math.Min(64, nameLength) - 2) : "";
                int type = dir[i + 66];
                uint start = BitConverter.ToUInt32(dir, i + 116);
                long size = sectorSize == 512 ? BitConverter.ToUInt32(dir, i + 120) : (long)BitConverter.ToUInt64(dir, i + 120);
                entries.Add((entryName, type, start, size));
            }
        }

        long Offset(uint sector) => (sector + 1L) * sectorSize;

        byte[] Chain(uint start, long size)
        {
            var output = new MemoryStream();
            var seen = new HashSet<uint>();
            uint sector = start;
            while (sector < 0xFFFFFFFA && seen.Add(sector) && output.Length < size)
            {
                long off = Offset(sector);
                if (off + sectorSize > data.Length) break;
                output.Write(data, (int)off, sectorSize);
                sector = sector < fat.Count ? fat[(int)sector] : 0xFFFFFFFE;
            }
            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        byte[] MiniChain(uint start, long size)
        {
            if (miniStream == null)
            {
                var root = entries.FirstOrDefault(e => e.Type == 5);
                miniStream = Chain(root.Start, root.Size);
            }
            var output = new MemoryStream();
            var seen = new HashSet<uint>();
            uint sector = start;
            while (sector < 0xFFFFFFFA && seen.Add(sector) && output.Length < size)
            {
                long off = (long)sector * miniSectorSize;
                if (off + miniSectorSize > miniStream.Length) break;
                output.Write(miniStream, (int)off, miniSectorSize);
                sector = sector < miniFat.Count ? miniFat[(int)sector] : 0xFFFFFFFE;
            }
            var bytes = output.ToArray();
            return size < bytes.Length ? bytes[..(int)size] : bytes;
        }

        public byte[]? Stream(string name)
        {
            foreach (var e in entries)
            {
                if (e.Type != 2 || !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return e.Size < miniCutoff ? MiniChain(e.Start, e.Size) : Chain(e.Start, e.Size);
            }
            return null;
        }
    }
}
