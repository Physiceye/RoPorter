using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Rbx2Source.Export
{
    // Unpacks a WOFF2 font into a plain TrueType one, which Skia can read (W3C WOFF2: a Brotli stream of the font's tables, with
    // the glyphs packed into streams of their own). Outlines only need to draw, so table checksums are left at zero.
    public static class Woff2
    {
        private static readonly string[] KnownTags =
        {
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern",
            "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "sbix", "acnt", "avar", "bdat", "bloc", "bsln",
            "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "EBSC"
        };

        // Reads big-endian values and WOFF2's variable-length numbers.
        private sealed class Reader
        {
            private readonly byte[] data;
            public int Position;

            public Reader(byte[] data, int position = 0)
            {
                this.data = data;
                Position = position;
            }

            public byte U8() => data[Position++];
            public int U16() => U8() << 8 | U8();
            public short I16() => (short)U16();
            public uint U32() => (uint)U16() << 16 | (uint)U16();

            public byte[] Bytes(int count)
            {
                byte[] bytes = data.Skip(Position).Take(count).ToArray();
                Position += count;
                return bytes;
            }

            public uint Base128()
            {
                uint value = 0;
                for (int i = 0; i < 5; i++)
                {
                    byte b = U8();
                    value = value << 7 | (uint)(b & 0x7F);
                    if ((b & 0x80) == 0)
                        return value;
                }
                throw new InvalidDataException("Bad number in a WOFF2 font.");
            }

            public int U255()
            {
                int code = U8();
                switch (code)
                {
                    case 253: return U16();
                    case 254: return U8() + 506;
                    case 255: return U8() + 253;
                    default: return code;
                }
            }
        }

        private sealed class Writer
        {
            public readonly MemoryStream Stream = new MemoryStream();

            public void U16(int value)
            {
                Stream.WriteByte((byte)(value >> 8));
                Stream.WriteByte((byte)value);
            }

            public void U32(long value)
            {
                U16((int)(value >> 16) & 0xFFFF);
                U16((int)value & 0xFFFF);
            }

            public void Bytes(byte[] bytes) => Stream.Write(bytes, 0, bytes.Length);

            public void Pad()
            {
                while (Stream.Length % 4 != 0)
                    Stream.WriteByte(0);
            }
        }

        public static byte[] ToTrueType(byte[] woff2)
        {
            var header = new Reader(woff2);
            if (header.U32() != 0x774F4632)
                throw new InvalidDataException("Not a WOFF2 font.");
            uint flavor = header.U32();
            header.U32();
            int tableCount = header.U16();
            header.Position = 48;
            var directory = new List<(string Tag, int Version, int Length)>();
            int compressed = 0;
            for (int i = 0; i < tableCount; i++)
            {
                byte flags = header.U8();
                string tag = (flags & 0x3F) == 63 ? Encoding.ASCII.GetString(header.Bytes(4)) : KnownTags[flags & 0x3F];
                int version = flags >> 6, length = (int)header.Base128();
                if (tag == "glyf" || tag == "loca" ? version == 0 : version != 0)
                    length = (int)header.Base128();
                directory.Add((tag, version, length));
            }
            compressed = header.Position;

            byte[] packed;
            using (var input = new BrotliStream(new MemoryStream(woff2, compressed, woff2.Length - compressed), CompressionMode.Decompress))
            using (var unpacked = new MemoryStream())
            {
                input.CopyTo(unpacked);
                packed = unpacked.ToArray();
            }

            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            int offset = 0;
            foreach (var (tag, version, length) in directory)
            {
                if (tag == "loca" && version == 0)
                    continue;
                byte[] table = packed.Skip(offset).Take(length).ToArray();
                offset += length;
                if (tag == "glyf" && version == 0)
                {
                    (tables["glyf"], tables["loca"]) = Glyphs(table);
                    continue;
                }
                if (tag != "loca" || !tables.ContainsKey("loca"))
                    tables[tag] = table;
            }
            // The loca table written here always holds long offsets.
            if (tables.TryGetValue("head", out byte[] head))
            {
                head[50] = 0;
                head[51] = 1;
            }

            var font = new Writer();
            int count = tables.Count, power = 1 << (int)Math.Floor(Math.Log2(count));
            font.U32(flavor);
            font.U16(count);
            font.U16(power * 16);
            font.U16((int)Math.Log2(power));
            font.U16(count * 16 - power * 16);
            long position = 12 + count * 16L;
            foreach (var table in tables)
            {
                font.Bytes(Encoding.ASCII.GetBytes(table.Key));
                font.U32(0);
                font.U32(position);
                font.U32(table.Value.Length);
                position += (table.Value.Length + 3) & ~3;
            }
            foreach (byte[] table in tables.Values)
            {
                font.Bytes(table);
                font.Pad();
            }
            return font.Stream.ToArray();
        }

        // Rebuilds the glyf and loca tables from the transformed glyf table.
        private static (byte[] Glyf, byte[] Loca) Glyphs(byte[] table)
        {
            var read = new Reader(table);
            read.U16();
            int options = read.U16(), glyphCount = read.U16();
            read.U16();
            int[] sizes = Enumerable.Range(0, 7).Select(i => (int)read.U32()).ToArray();
            Reader Stream(int index) => new Reader(table, 36 + sizes.Take(index).Sum());
            Reader contours = Stream(0), points = Stream(1), flags = Stream(2), triplets = Stream(3), composites = Stream(4), instructions = Stream(6);
            int boxMap = 36 + sizes.Take(5).Sum();
            var boxes = new Reader(table, boxMap + (((glyphCount + 31) >> 5) << 2));
            bool HasBox(int glyph) => (table[boxMap + (glyph >> 3)] & (0x80 >> (glyph & 7))) != 0;

            var glyf = new Writer();
            var loca = new Writer();
            for (int glyph = 0; glyph < glyphCount; glyph++)
            {
                loca.U32(glyf.Stream.Length);
                int contourCount = contours.I16();
                if (contourCount == 0)
                    continue;
                if (contourCount == -1)
                {
                    glyf.U16(0xFFFF);
                    glyf.Bytes(boxes.Bytes(8));
                    bool more = true, hint = false;
                    while (more)
                    {
                        int componentFlags = composites.U16();
                        more = (componentFlags & 0x20) != 0;
                        hint |= (componentFlags & 0x100) != 0;
                        int size = 2 + ((componentFlags & 1) != 0 ? 4 : 2) + ((componentFlags & 8) != 0 ? 2 : (componentFlags & 0x40) != 0 ? 4 : (componentFlags & 0x80) != 0 ? 8 : 0);
                        glyf.U16(componentFlags);
                        glyf.Bytes(composites.Bytes(size));
                    }
                    if (hint)
                    {
                        int length = triplets.U255();
                        glyf.U16(length);
                        glyf.Bytes(instructions.Bytes(length));
                    }
                    glyf.Pad();
                    continue;
                }

                var ends = new int[contourCount];
                int total = 0;
                for (int i = 0; i < contourCount; i++)
                {
                    total += points.U255();
                    ends[i] = total - 1;
                }
                var x = new int[total];
                var y = new int[total];
                var on = new bool[total];
                int cx = 0, cy = 0;
                for (int i = 0; i < total; i++)
                {
                    int f = flags.U8();
                    on[i] = f >> 7 == 0;
                    f &= 0x7F;
                    var (dx, dy) = Triplet(f, triplets);
                    cx += dx;
                    cy += dy;
                    x[i] = cx;
                    y[i] = cy;
                }
                int instructionLength = triplets.U255();
                glyf.U16(contourCount);
                if (HasBox(glyph))
                    glyf.Bytes(boxes.Bytes(8));
                else
                    foreach (int value in new[] { total > 0 ? x.Min() : 0, total > 0 ? y.Min() : 0, total > 0 ? x.Max() : 0, total > 0 ? y.Max() : 0 })
                        glyf.U16(value & 0xFFFF);
                foreach (int end in ends)
                    glyf.U16(end);
                glyf.U16(instructionLength);
                glyf.Bytes(instructions.Bytes(instructionLength));
                // Every delta as 16 bits: larger than the game's own encoding, but simple.
                for (int i = 0; i < total; i++)
                    glyf.Stream.WriteByte((byte)(on[i] ? 1 : 0));
                for (int i = 0; i < total; i++)
                    glyf.U16((i == 0 ? x[0] : x[i] - x[i - 1]) & 0xFFFF);
                for (int i = 0; i < total; i++)
                    glyf.U16((i == 0 ? y[0] : y[i] - y[i - 1]) & 0xFFFF);
                glyf.Pad();
            }
            loca.U32(glyf.Stream.Length);
            return (glyf.Stream.ToArray(), loca.Stream.ToArray());
        }

        // One point's offset from the last: the flag says how it's packed in the following bytes.
        private static (int X, int Y) Triplet(int f, Reader data)
        {
            int Sign(int flag, int value) => (flag & 1) != 0 ? value : -value;
            if (f < 10)
                return (0, Sign(f, ((f & 14) << 7) + data.U8()));
            if (f < 20)
                return (Sign(f, (((f - 10) & 14) << 7) + data.U8()), 0);
            if (f < 84)
            {
                int b0 = f - 20, b1 = data.U8();
                return (Sign(f, 1 + (b0 & 0x30) + (b1 >> 4)), Sign(f >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
            }
            if (f < 120)
            {
                int b0 = f - 84, b1 = data.U8(), b2 = data.U8();
                return (Sign(f, 1 + ((b0 / 12) << 8) + b1), Sign(f >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2));
            }
            if (f < 124)
            {
                int b1 = data.U8(), b2 = data.U8(), b3 = data.U8();
                return (Sign(f, (b1 << 4) + (b2 >> 4)), Sign(f >> 1, ((b2 & 0x0F) << 8) + b3));
            }
            int a = data.U8(), b = data.U8(), c = data.U8(), d = data.U8();
            return (Sign(f, (a << 8) + b), Sign(f >> 1, (c << 8) + d));
        }
    }
}
