using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Rbx2Source.Export
{
    // A union's mesh (PartOperation / PartOperationAsset MeshData): "CSGMDL", stored scrambled by a repeating 31-byte XOR key.
    // Version 2 (and 4, which adds levels of detail after it; version 5 is packed, see ParseVersion5) holds a header (magic, version, two 16-byte digests), 84-byte
    // vertices (position, normal, RGBA colour, texture coordinates, tangent) and 32-bit indices. Positions are in studs, centred,
    // at the union's InitialSize.
    public sealed class CsgMesh
    {
        private static readonly byte[] Key =
        {
            0x56, 0x2E, 0x6E, 0x58, 0x31, 0x20, 0x30, 0x04, 0x34, 0x69, 0x0C, 0x77, 0x0C, 0x01, 0x5E, 0x00,
            0x1A, 0x60, 0x37, 0x69, 0x1D, 0x52, 0x2B, 0x07, 0x4F, 0x24, 0x59, 0x65, 0x53, 0x04, 0x7A
        };
        private const int HeaderSize = 42, MinimumStride = 28;

        public List<float[]> Positions { get; } = new List<float[]>();
        public List<float[]> Normals { get; } = new List<float[]>();
        // The colour of the part each vertex came from (shown when the union's UsePartColor is off).
        public List<float[]> Colors { get; } = new List<float[]>();
        public List<int[]> Faces { get; } = new List<int[]>();

        public static CsgMesh Parse(byte[] data)
        {
            if (data == null || data.Length < HeaderSize + 12)
                throw new InvalidDataException("The union's mesh data is empty or truncated.");
            byte[] plain;
            if (Encoding.ASCII.GetString(data, 0, 6) == "CSGMDL")
                plain = data;
            else
            {
                plain = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                    plain[i] = (byte)(data[i] ^ Key[i % Key.Length]);
                if (Encoding.ASCII.GetString(plain, 0, 6) != "CSGMDL")
                    throw new InvalidDataException("The union's mesh data is not in a format RoPorter knows.");
                // Version 5 scrambles only its header.
                if (BitConverter.ToInt32(plain, 6) == 5)
                    Array.Copy(data, 10, plain, 10, data.Length - 10);
            }
            int version = BitConverter.ToInt32(plain, 6);
            if (version == 5)
                return ParseVersion5(plain);
            if (version != 2 && version != 4)
                throw new NotSupportedException("Union mesh version " + version + " is not supported.");

            int offset = HeaderSize;
            int count = BitConverter.ToInt32(plain, offset), stride = BitConverter.ToInt32(plain, offset + 4);
            offset += 8;
            if (count <= 0 || stride < MinimumStride || (long)count * stride > plain.Length - offset - 4)
                throw new InvalidDataException("The union's mesh data has an invalid vertex list.");
            var mesh = new CsgMesh();
            for (int i = 0; i < count; i++, offset += stride)
            {
                float[] p = { BitConverter.ToSingle(plain, offset), BitConverter.ToSingle(plain, offset + 4), BitConverter.ToSingle(plain, offset + 8) };
                float[] n = { BitConverter.ToSingle(plain, offset + 12), BitConverter.ToSingle(plain, offset + 16), BitConverter.ToSingle(plain, offset + 20) };
                if (p.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    p = new float[3];
                if (n.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    n = new float[] { 0, 1, 0 };
                mesh.Positions.Add(p);
                mesh.Normals.Add(n);
                mesh.Colors.Add(new[] { plain[offset + 24] / 255f, plain[offset + 25] / 255f, plain[offset + 26] / 255f });
            }
            int indexCount = BitConverter.ToInt32(plain, offset);
            offset += 4;
            if (indexCount < 3 || (long)indexCount * 4 > plain.Length - offset)
                throw new InvalidDataException("The union's mesh data has an invalid index list.");
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = BitConverter.ToInt32(plain, offset + 4 * i);
            offset += 4 * indexCount;
            // Version 4 lists where each level of detail starts in the indices; the first is the full mesh.
            int begin = 0, end = indexCount - indexCount % 3;
            if (version == 4 && offset + 4 <= plain.Length)
            {
                int lods = BitConverter.ToInt32(plain, offset);
                if (lods >= 2 && offset + 4 + 4L * lods <= plain.Length)
                {
                    int first = BitConverter.ToInt32(plain, offset + 4), second = BitConverter.ToInt32(plain, offset + 8);
                    if (first >= 0 && second > first && second <= indexCount && (second - first) % 3 == 0)
                    {
                        begin = first;
                        end = second;
                    }
                }
            }
            for (int i = begin; i + 2 < end; i += 3)
            {
                int[] face = { indices[i], indices[i + 1], indices[i + 2] };
                if (face.All(index => index >= 0 && index < count))
                    mesh.Faces.Add(face);
            }
            if (mesh.Faces.Count == 0)
                throw new InvalidDataException("The union's mesh has no triangles.");
            return mesh;
        }

        // Version 5: after the 10-byte header, arrays each led by a 16-bit count: positions (3 floats), normals (a 32-bit byte
        // length, then 3 unsigned 16-bit values, 0 = -1 and 65534 = +1), RGBA colours, a byte per vertex (the face it came
        // from), texture coordinates (2 floats) and tangents (like normals). Then the index count, the encoded length, and the
        // indices as deltas: a byte under 0x80 is a 7-bit signed delta, one with its high bit set starts a 3-byte big-endian
        // 23-bit signed delta. Last, the levels of detail: a byte count and 32-bit offsets into the indices.
        private static CsgMesh ParseVersion5(byte[] data)
        {
            int offset = 10;
            void Need(long bytes)
            {
                if (bytes < 0 || offset + bytes > data.Length)
                    throw new InvalidDataException("The union's mesh data is truncated.");
            }
            int Count()
            {
                Need(2);
                int value = BitConverter.ToUInt16(data, offset);
                offset += 2;
                return value;
            }
            int Int()
            {
                Need(4);
                int value = BitConverter.ToInt32(data, offset);
                offset += 4;
                return value;
            }
            var mesh = new CsgMesh();
            int count = Count();
            Need(12L * count);
            for (int i = 0; i < count; i++, offset += 12)
            {
                float[] p = { BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8) };
                mesh.Positions.Add(p.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ? new float[3] : p);
            }
            if (Count() != count)
                throw new InvalidDataException("The union's mesh data has mismatched vertex arrays.");
            int normalBytes = Int();
            Need(normalBytes);
            for (int i = 0; i < count; i++)
            {
                int at = offset + 6 * i;
                mesh.Normals.Add(6 * i + 6 <= normalBytes
                    ? new[] { BitConverter.ToUInt16(data, at) / 32767f - 1, BitConverter.ToUInt16(data, at + 2) / 32767f - 1, BitConverter.ToUInt16(data, at + 4) / 32767f - 1 }
                    : new float[] { 0, 1, 0 });
            }
            offset += normalBytes;
            if (Count() != count)
                throw new InvalidDataException("The union's mesh data has mismatched vertex arrays.");
            Need(4L * count);
            for (int i = 0; i < count; i++, offset += 4)
                mesh.Colors.Add(new[] { data[offset] / 255f, data[offset + 1] / 255f, data[offset + 2] / 255f });
            int faces = Count();
            Need(faces);
            offset += faces;
            int uvs = Count();
            Need(8L * uvs);
            offset += 8 * uvs;
            Count();
            int tangentBytes = Int();
            Need(tangentBytes);
            offset += tangentBytes;

            int indexCount = Int(), encoded = Int();
            Need(encoded);
            int end = offset + encoded, previous = 0;
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                if (offset >= end)
                    throw new InvalidDataException("The union's mesh data has a truncated index list.");
                int first = data[offset++], delta;
                if (first < 0x80)
                    delta = (first & 0x40) != 0 ? first - 0x80 : first;
                else
                {
                    if (offset + 2 > end)
                        throw new InvalidDataException("The union's mesh data has a truncated index list.");
                    delta = ((first & 0x7F) << 16) | (data[offset] << 8) | data[offset + 1];
                    offset += 2;
                    if ((delta & 0x400000) != 0)
                        delta -= 0x800000;
                }
                indices[i] = previous += delta;
            }
            offset = end;
            // The first level of detail is the full mesh.
            int begin = 0, stop = indexCount - indexCount % 3;
            if (offset < data.Length)
            {
                int lods = data[offset++];
                if (lods >= 2 && offset + 8 <= data.Length)
                {
                    int a = BitConverter.ToInt32(data, offset), b = BitConverter.ToInt32(data, offset + 4);
                    if (a >= 0 && b > a && b <= indexCount && (b - a) % 3 == 0)
                    {
                        begin = a;
                        stop = b;
                    }
                }
            }
            for (int i = begin; i + 2 < stop; i += 3)
            {
                int[] face = { indices[i], indices[i + 1], indices[i + 2] };
                if (face.All(index => index >= 0 && index < count))
                    mesh.Faces.Add(face);
            }
            if (mesh.Faces.Count == 0)
                throw new InvalidDataException("The union's mesh has no triangles.");
            return mesh;
        }

        // The mesh of a union asset (a PartOperationAsset model) or of a union saved with its mesh inline.
        public static CsgMesh FromAsset(byte[] asset) =>
            Parse(RobloxModelFile.ReadInstances(asset).SelectMany(root => new[] { root }.Concat(root.Descendants()))
                .Select(instance => instance.Binary.TryGetValue("MeshData", out byte[] data) ? data : null)
                .FirstOrDefault(data => data != null && data.Length > 0) ?? throw new InvalidDataException("The union asset has no mesh data."));
    }
}
