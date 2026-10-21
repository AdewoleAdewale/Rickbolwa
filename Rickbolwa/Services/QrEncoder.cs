using System;
using System.Collections.Generic;
using System.Text;

namespace Rickbolwa.Services
{
    /// <summary>
    /// Minimal QR Code (Model 2) encoder: byte mode, error-correction level M,
    /// versions 1-40, automatic mask selection. No third-party dependencies.
    /// Returns a square boolean matrix (true = dark module) WITHOUT quiet zone.
    /// Used so the receipt QR can be printed as a raster image, which works on
    /// every ESC/POS printer that can print the logo/barcode (GS v 0), instead of
    /// relying on the printer's optional native QR command (GS ( k).
    /// </summary>
    public static class QrEncoder
    {
        // Error-correction level M tables, index = version (index 0 unused).
        private static readonly int[] EccPerBlock =
        {
            -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
            26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28
        };

        private static readonly int[] NumBlocks =
        {
            -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
            17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49
        };

        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);

            // ── pick the smallest version that fits ──────────────────────
            int version = 0;
            int dataCapacityBits = 0;
            for (int v = 1; v <= 40; v++)
            {
                int cap = NumDataCodewords(v) * 8;
                int ccBits = v <= 9 ? 8 : 16;
                if (4 + ccBits + data.Length * 8 <= cap) { version = v; dataCapacityBits = cap; break; }
            }
            if (version == 0) throw new ArgumentException("Data too long for a QR code.");

            // ── bit stream: mode(0100) + length + data + terminator + pad ─
            var bits = new List<bool>();
            AppendBits(bits, 0x4, 4);
            AppendBits(bits, data.Length, version <= 9 ? 8 : 16);
            foreach (byte b in data) AppendBits(bits, b, 8);
            AppendBits(bits, 0, Math.Min(4, dataCapacityBits - bits.Count));
            while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < dataCapacityBits; pad ^= 0xEC ^ 0x11)
                AppendBits(bits, pad, 8);

            var dataCodewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) dataCodewords[i >> 3] |= (byte)(0x80 >> (i & 7));

            byte[] all = AddEccAndInterleave(dataCodewords, version);

            // ── draw ─────────────────────────────────────────────────────
            int size = version * 4 + 17;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];
            DrawFunctionPatterns(modules, isFunction, version);
            DrawCodewords(modules, isFunction, all);

            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunction, mask);
                DrawFormatBits(modules, isFunction, mask);
                int p = Penalty(modules);
                if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
                ApplyMask(modules, isFunction, mask); // XOR again = undo
            }
            ApplyMask(modules, isFunction, bestMask);
            DrawFormatBits(modules, isFunction, bestMask);
            return modules;
        }

        // ══════════════════════ codewords / Reed-Solomon ══════════════════════

        private static int NumRawDataModules(int ver)
        {
            int size = ver * 4 + 17;
            int result = size * size;
            result -= 8 * 8 * 3;               // finders + separators
            result -= 15 * 2 + 1;              // format info + dark module
            result -= (size - 16) * 2;         // timing
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (numAlign - 1) * (numAlign - 1) * 25;
                result -= (numAlign - 2) * 2 * 20;
                if (ver >= 7) result -= 6 * 3 * 2; // version info
            }
            return result;
        }

        private static int NumDataCodewords(int ver)
        {
            return NumRawDataModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];
        }

        private static byte[] AddEccAndInterleave(byte[] data, int ver)
        {
            int numBlocks = NumBlocks[ver];
            int blockEccLen = EccPerBlock[ver];
            int rawCodewords = NumRawDataModules(ver) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            var blocks = new byte[numBlocks][];
            byte[] rsDiv = ReedSolomonDivisor(blockEccLen);
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                byte[] ecc = ReedSolomonRemainder(dat, rsDiv);
                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, 0, block, 0, datLen);
                Array.Copy(ecc, 0, block, block.Length - blockEccLen, blockEccLen);
                blocks[i] = block;
            }

            var result = new byte[rawCodewords];
            for (int i = 0, k = 0; i < blocks[0].Length; i++)
            {
                for (int j = 0; j < blocks.Length; j++)
                {
                    // skip the padding slot in short blocks
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                        result[k++] = blocks[j][i];
                }
            }
            return result;
        }

        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = (byte)Multiply(result[j], root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++)
                    result[i] ^= (byte)Multiply(divisor[i], factor);
            }
            return result;
        }

        private static int Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        // ══════════════════════ drawing ══════════════════════

        private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int ver)
        {
            int size = m.GetLength(0);
            for (int i = 0; i < size; i++)
            {
                SetFunction(m, f, 6, i, i % 2 == 0);
                SetFunction(m, f, i, 6, i % 2 == 0);
            }
            DrawFinder(m, f, 3, 3);
            DrawFinder(m, f, size - 4, 3);
            DrawFinder(m, f, 3, size - 4);

            int[] alignPos = AlignmentPositions(ver, size);
            int n = alignPos.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    DrawAlignment(m, f, alignPos[i], alignPos[j]);
                }

            DrawFormatBits(m, f, 0); // reserve the area
            DrawVersion(m, f, ver);
        }

        private static int[] AlignmentPositions(int ver, int size)
        {
            if (ver == 1) return new int[0];
            int numAlign = ver / 7 + 2;
            int step = (ver == 32) ? 26 : (ver * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        private static void DrawFinder(bool[,] m, bool[,] f, int cx, int cy)
        {
            int size = m.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                        SetFunction(m, f, x, y, dist != 2 && dist != 4);
                }
        }

        private static void DrawAlignment(bool[,] m, bool[,] f, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(m, f, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private static void DrawFormatBits(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            int data = (0 << 3) | mask; // ECC level M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunction(m, f, 8, i, GetBit(bits, i));
            SetFunction(m, f, 8, 7, GetBit(bits, 6));
            SetFunction(m, f, 8, 8, GetBit(bits, 7));
            SetFunction(m, f, 7, 8, GetBit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(m, f, 14 - i, 8, GetBit(bits, i));

            for (int i = 0; i < 8; i++) SetFunction(m, f, size - 1 - i, 8, GetBit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(m, f, 8, size - 15 + i, GetBit(bits, i));
            SetFunction(m, f, 8, size - 8, true); // always-dark module
        }

        private static void DrawVersion(bool[,] m, bool[,] f, int ver)
        {
            if (ver < 7) return;
            int size = m.GetLength(0);
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (ver << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = GetBit(bits, i);
                int a = size - 11 + i % 3;
                int b = i / 3;
                SetFunction(m, f, a, b, bit);
                SetFunction(m, f, b, a, bit);
            }
        }

        private static void DrawCodewords(bool[,] m, bool[,] f, byte[] data)
        {
            int size = m.GetLength(0);
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!f[x, y] && i < data.Length * 8)
                        {
                            m[x, y] = GetBit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (!f[x, y] && invert) m[x, y] = !m[x, y];
                }
        }

        // ══════════════════════ mask penalty ══════════════════════

        private static int Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            int result = 0;

            // runs of same colour + finder-like patterns, rows then columns
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < size; a++)
                {
                    var line = new bool[size];
                    for (int b = 0; b < size; b++) line[b] = pass == 0 ? m[b, a] : m[a, b];

                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        if (line[b] == line[b - 1]) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                        else run = 1;
                    }
                    result += FinderPenalty(line);
                }
            }

            // 2x2 blocks
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = m[x, y];
                    if (c == m[x + 1, y] && c == m[x, y + 1] && c == m[x + 1, y + 1]) result += 3;
                }

            // dark/light balance
            int dark = 0;
            foreach (bool b in m) if (b) dark++;
            int total = size * size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += Math.Max(0, k) * 10;
            return result;
        }

        private static int FinderPenalty(bool[] line)
        {
            // 1:1:3:1:1 dark-light-dark-light-dark with 4 light modules on either side
            int n = line.Length;
            int count = 0;
            bool[] pat1 = { true, false, true, true, true, false, true, false, false, false, false };
            bool[] pat2 = { false, false, false, false, true, false, true, true, true, false, true };
            for (int i = 0; i + 11 <= n; i++)
            {
                bool m1 = true, m2 = true;
                for (int j = 0; j < 11; j++)
                {
                    if (line[i + j] != pat1[j]) m1 = false;
                    if (line[i + j] != pat2[j]) m2 = false;
                    if (!m1 && !m2) break;
                }
                if (m1) count++;
                if (m2) count++;
            }
            return count * 40;
        }

        // ══════════════════════ small helpers ══════════════════════

        private static void SetFunction(bool[,] m, bool[,] f, int x, int y, bool dark)
        {
            m[x, y] = dark;
            f[x, y] = true;
        }

        private static bool GetBit(int value, int index) { return ((value >> index) & 1) != 0; }

        private static void AppendBits(List<bool> list, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) list.Add(((value >> i) & 1) != 0);
        }
    }
}