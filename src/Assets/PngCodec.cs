using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DynamicMapsExtended
{
    // Small self-contained PNG reader/writer used only by the one-time asset installer.
    // Tarkov.dev source tiles captured for this release are non-interlaced PNGs using
    // grayscale, RGB, indexed-colour, or RGBA samples at 1/2/4/8-bit depth.
    internal static class PngCodec
    {
        internal sealed class Image
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly byte[] Rgba;
            internal Image(int width, int height, byte[] rgba) { Width = width; Height = height; Rgba = rgba; }
        }

        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        internal static bool TryReadDimensions(string path, out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                using var fs = File.OpenRead(path);
                var head = new byte[24];
                if (fs.Read(head, 0, head.Length) != head.Length) return false;
                for (var i = 0; i < Signature.Length; i++) if (head[i] != Signature[i]) return false;
                if (Encoding.ASCII.GetString(head, 12, 4) != "IHDR") return false;
                width = ReadInt32BE(head, 16);
                height = ReadInt32BE(head, 20);
                return width > 0 && height > 0;
            }
            catch { return false; }
        }

        internal static Image DecodeFile(string path) => Decode(File.ReadAllBytes(path));

        internal static Image Decode(byte[] png)
        {
            if (png == null || png.Length < 33) throw new InvalidDataException("PNG is truncated.");
            for (var i = 0; i < Signature.Length; i++) if (png[i] != Signature[i]) throw new InvalidDataException("PNG signature is invalid.");

            var offset = 8;
            var width = 0; var height = 0; var bitDepth = 0; var colorType = 0; var interlace = 0;
            byte[] palette = null;
            byte[] transparency = null;
            using var idat = new MemoryStream();

            while (offset + 12 <= png.Length)
            {
                var length = ReadInt32BE(png, offset); offset += 4;
                if (length < 0 || offset + 8 + length > png.Length) throw new InvalidDataException("PNG chunk is invalid.");
                var type = Encoding.ASCII.GetString(png, offset, 4); offset += 4;
                if (type == "IHDR")
                {
                    if (length != 13) throw new InvalidDataException("PNG IHDR is invalid.");
                    width = ReadInt32BE(png, offset);
                    height = ReadInt32BE(png, offset + 4);
                    bitDepth = png[offset + 8];
                    colorType = png[offset + 9];
                    if (png[offset + 10] != 0 || png[offset + 11] != 0) throw new InvalidDataException("Unsupported PNG compression/filter method.");
                    interlace = png[offset + 12];
                }
                else if (type == "PLTE")
                {
                    palette = new byte[length]; Buffer.BlockCopy(png, offset, palette, 0, length);
                }
                else if (type == "tRNS")
                {
                    transparency = new byte[length]; Buffer.BlockCopy(png, offset, transparency, 0, length);
                }
                else if (type == "IDAT")
                {
                    idat.Write(png, offset, length);
                }
                offset += length + 4; // data + CRC
                if (type == "IEND") break;
            }

            if (width <= 0 || height <= 0 || idat.Length == 0) throw new InvalidDataException("PNG has no image data.");
            if (interlace != 0) throw new InvalidDataException("Interlaced PNG is not supported by the local asset builder.");

            var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Unsupported PNG color type: " + colorType) };
            if (colorType == 0 || colorType == 3)
            {
                if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8)
                    throw new InvalidDataException("Unsupported PNG bit depth: " + bitDepth);
            }
            else if (bitDepth != 8)
            {
                throw new InvalidDataException("Unsupported PNG bit depth/color combination.");
            }

            var rowBytes = checked((width * channels * bitDepth + 7) / 8);
            var expectedInflated = checked((rowBytes + 1) * height);
            var compressed = idat.ToArray();
            var inflated = InflateZlib(compressed, expectedInflated);
            if (inflated.Length < expectedInflated) throw new InvalidDataException("PNG decompressed data is truncated.");

            var raw = new byte[checked(rowBytes * height)];
            var previous = new byte[rowBytes];
            var current = new byte[rowBytes];
            var srcOffset = 0;
            var bytesPerPixel = Math.Max(1, (channels * bitDepth + 7) / 8);
            for (var y = 0; y < height; y++)
            {
                var filter = inflated[srcOffset++];
                Buffer.BlockCopy(inflated, srcOffset, current, 0, rowBytes); srcOffset += rowBytes;
                Unfilter(current, previous, filter, bytesPerPixel);
                Buffer.BlockCopy(current, 0, raw, y * rowBytes, rowBytes);
                var swap = previous; previous = current; current = swap;
            }

            var rgba = new byte[checked(width * height * 4)];
            ConvertToRgba(raw, width, height, bitDepth, colorType, palette, transparency, rgba);
            return new Image(width, height, rgba);
        }

        internal static void EncodeRgba(string path, int width, int height, byte[] rgba)
        {
            if (width <= 0 || height <= 0 || rgba == null || rgba.Length != checked(width * height * 4))
                throw new ArgumentException("Invalid RGBA image.");

            var raw = new byte[checked((width * 4 + 1) * height)];
            var src = 0; var dst = 0;
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                raw[dst++] = 1; // PNG Sub filter: substantially smaller map previews than filter None.
                for (var i = 0; i < rowBytes; i++)
                {
                    var left = i >= 4 ? rgba[src + i - 4] : 0;
                    raw[dst + i] = unchecked((byte)(rgba[src + i] - left));
                }
                src += rowBytes; dst += rowBytes;
            }

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }

            var adler = Adler32(raw);
            using var zlib = new MemoryStream(deflated.Length + 6);
            if (LooksLikeCompleteZlib(deflated, adler))
            {
                zlib.Write(deflated, 0, deflated.Length);
            }
            else
            {
                zlib.WriteByte(0x78); zlib.WriteByte(0x9C);
                zlib.Write(deflated, 0, deflated.Length);
                WriteUInt32BE(zlib, adler);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            WriteInt32BE(ihdr, 0, width); WriteInt32BE(ihdr, 4, height);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(fs, "IHDR", ihdr);
            WriteChunk(fs, "IDAT", zlib.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static byte[] InflateZlib(byte[] zlib, int expected)
        {
            if (zlib == null || zlib.Length < 6) throw new InvalidDataException("zlib stream is truncated.");
            var start = 2;
            if ((zlib[1] & 0x20) != 0) start += 4;
            var length = zlib.Length - start - 4;
            if (length <= 0) throw new InvalidDataException("zlib stream has no DEFLATE payload.");

            Exception first = null;
            try
            {
                var data = InflateRange(zlib, start, length, expected);
                if (expected <= 0 || data.Length >= expected) return data;
                first = new InvalidDataException("Raw DEFLATE payload produced too little data.");
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                first = ex;
            }

            try
            {
                var data = InflateRange(zlib, 0, zlib.Length, expected);
                if (expected <= 0 || data.Length >= expected) return data;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                throw new InvalidDataException("Could not decompress PNG zlib stream.", first ?? ex);
            }

            throw new InvalidDataException("Could not decompress PNG zlib stream.", first);
        }

        private static byte[] InflateRange(byte[] bytes, int offset, int length, int expected)
        {
            using var input = new MemoryStream(bytes, offset, length, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected > 0 ? expected : 0);
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private static void Unfilter(byte[] row, byte[] previous, int filter, int bpp)
        {
            switch (filter)
            {
                case 0: return;
                case 1:
                    for (var i = bpp; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + row[i - bpp]));
                    return;
                case 2:
                    for (var i = 0; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + previous[i]));
                    return;
                case 3:
                    for (var i = 0; i < row.Length; i++)
                    {
                        var left = i >= bpp ? row[i - bpp] : 0;
                        var up = previous[i];
                        row[i] = unchecked((byte)(row[i] + ((left + up) >> 1)));
                    }
                    return;
                case 4:
                    for (var i = 0; i < row.Length; i++)
                    {
                        var a = i >= bpp ? row[i - bpp] : 0;
                        var b = previous[i];
                        var c = i >= bpp ? previous[i - bpp] : 0;
                        row[i] = unchecked((byte)(row[i] + Paeth(a, b, c)));
                    }
                    return;
                default: throw new InvalidDataException("Unsupported PNG filter: " + filter);
            }
        }

        private static byte Paeth(int a, int b, int c)
        {
            var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
            return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
        }

        private static void ConvertToRgba(byte[] raw, int width, int height, int bitDepth, int colorType, byte[] palette, byte[] trns, byte[] rgba)
        {
            var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            var rowBytes = (width * channels * bitDepth + 7) / 8;
            for (var y = 0; y < height; y++)
            {
                var row = y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    byte r, g, b, a = 255;
                    if (colorType == 6)
                    {
                        var p = row + x * 4; r = raw[p]; g = raw[p + 1]; b = raw[p + 2]; a = raw[p + 3];
                    }
                    else if (colorType == 2)
                    {
                        var p = row + x * 3; r = raw[p]; g = raw[p + 1]; b = raw[p + 2];
                        if (trns != null && trns.Length >= 6 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0;
                    }
                    else if (colorType == 4)
                    {
                        var p = row + x * 2; r = g = b = raw[p]; a = raw[p + 1];
                    }
                    else if (colorType == 0)
                    {
                        var sample = ReadPackedSample(raw, row, x, bitDepth);
                        var max = (1 << bitDepth) - 1;
                        var v = (byte)((sample * 255 + max / 2) / max);
                        r = g = b = v;
                        if (trns != null && trns.Length >= 2)
                        {
                            var transparent = (trns[0] << 8) | trns[1];
                            if (sample == transparent) a = 0;
                        }
                    }
                    else if (colorType == 3)
                    {
                        var index = ReadPackedSample(raw, row, x, bitDepth);
                        var p = index * 3;
                        if (palette == null || p + 2 >= palette.Length) throw new InvalidDataException("PNG palette index is invalid.");
                        r = palette[p]; g = palette[p + 1]; b = palette[p + 2];
                        if (trns != null && index < trns.Length) a = trns[index];
                    }
                    else throw new InvalidDataException("Unsupported PNG color type.");

                    var q = (y * width + x) * 4;
                    rgba[q] = r; rgba[q + 1] = g; rgba[q + 2] = b; rgba[q + 3] = a;
                }
            }
        }

        private static int ReadPackedSample(byte[] raw, int rowOffset, int x, int bitDepth)
        {
            if (bitDepth == 8) return raw[rowOffset + x];
            var bit = x * bitDepth;
            var b = raw[rowOffset + (bit >> 3)];
            var shift = 8 - bitDepth - (bit & 7);
            return (b >> shift) & ((1 << bitDepth) - 1);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            WriteUInt32BE(stream, (uint)data.Length);
            stream.Write(typeBytes, 0, 4);
            if (data.Length > 0) stream.Write(data, 0, data.Length);
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < 4; i++) crc = CrcTable[(crc ^ typeBytes[i]) & 0xFF] ^ (crc >> 8);
            for (var i = 0; i < data.Length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            WriteUInt32BE(stream, crc ^ 0xFFFFFFFFu);
        }


        private static bool LooksLikeCompleteZlib(byte[] bytes, uint expectedAdler)
        {
            if (bytes == null || bytes.Length < 6) return false;
            var cmf = bytes[0]; var flg = bytes[1];
            if ((cmf & 0x0F) != 8 || (((cmf << 8) | flg) % 31) != 0) return false;
            var n = bytes.Length;
            var adler = ((uint)bytes[n - 4] << 24) | ((uint)bytes[n - 3] << 16) | ((uint)bytes[n - 2] << 8) | bytes[n - 1];
            return adler == expectedAdler;
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521; uint a = 1, b = 0;
            for (var i = 0; i < data.Length; i++) { a = (a + data[i]) % mod; b = (b + a) % mod; }
            return (b << 16) | a;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static int ReadInt32BE(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

        private static void WriteInt32BE(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }
    }
}