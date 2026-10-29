using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Golan.Gdm.Integration.Remote;

/// <summary>
/// Minimal ISO/IEC 18004 QR Code encoder supporting Byte mode, Error Correction Level M, and Versions 1–10,
/// with direct rendering to a frozen WPF <see cref="BitmapSource"/>.
/// </summary>
public static class QrCode
{
    sealed record VersionSpec(int Version, int TotalDataCodewords, int EcCodewordsPerBlock, int Group1Blocks, int Group1DataCodewords, int Group2Blocks, int Group2DataCodewords, int[] AlignmentCenters)
    {
        public int Size => 17 + 4 * Version;
        public int CharCountBits => Version <= 9 ? 8 : 16;
        public int MaxByteCapacity => (TotalDataCodewords * 8 - 4 - CharCountBits) / 8;
    }

    // ISO/IEC 18004 Table 7 & Table 9 (ECC Level M, Versions 1..10)
    static readonly VersionSpec[] Specs =
    [
        new(1,  16, 10, 1, 16, 0,  0, []),
        new(2,  28, 16, 1, 28, 0,  0, [6, 18]),
        new(3,  44, 26, 1, 44, 0,  0, [6, 22]),
        new(4,  64, 18, 2, 32, 0,  0, [6, 26]),
        new(5,  86, 24, 2, 43, 0,  0, [6, 30]),
        new(6, 108, 16, 4, 27, 0,  0, [6, 34]),
        new(7, 124, 18, 4, 31, 0,  0, [6, 22, 38]),
        new(8, 154, 22, 2, 38, 2, 39, [6, 24, 42]),
        new(9, 182, 22, 3, 36, 2, 37, [6, 26, 46]),
        new(10, 216, 26, 4, 43, 1, 44, [6, 28, 50]),
    ];

    public static int SelectVersion(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Byte count must be non-negative.");
        foreach (var spec in Specs)
        {
            if (byteCount <= spec.MaxByteCapacity)
                return spec.Version;
        }
        throw new ArgumentOutOfRangeException(nameof(byteCount), $"Payload of {byteCount} bytes exceeds QR Version 10 (ECC M) maximum capacity of {Specs[^1].MaxByteCapacity} bytes.");
    }

    /// <summary>Computes the 15-bit BCH format information for ECC Level M (00) and the given mask (0..7).</summary>
    public static int ComputeFormatBits(int maskPattern)
    {
        if (maskPattern is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(maskPattern));
        // ECC Level M indicator in QR format info is 00
        int data = (0b00 << 3) | maskPattern;
        int rem = data << 10;
        for (int i = 14; i >= 10; i--)
        {
            if (((rem >> i) & 1) != 0)
                rem ^= 0x537 << (i - 10);
        }
        return ((data << 10) | rem) ^ 0x5412;
    }

    /// <summary>Computes the 18-bit BCH version information for Versions 7..10.</summary>
    public static int ComputeVersionBits(int version)
    {
        if (version is < 7 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version));
        int rem = version << 12;
        for (int i = 17; i >= 12; i--)
        {
            if (((rem >> i) & 1) != 0)
                rem ^= 0x1F25 << (i - 12);
        }
        return (version << 12) | rem;
    }

    public static bool[,] Encode(string text) => EncodeWithDetails(text).Modules;

    public static (bool[,] Modules, int Version, int Mask) EncodeWithDetails(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var payload = Encoding.UTF8.GetBytes(text);
        int version = SelectVersion(payload.Length);
        var spec = Specs[version - 1];

        var dataCodewords = BuildDataCodewords(payload, spec);
        var allCodewords = BuildInterleavedCodewords(dataCodewords, spec);

        int size = spec.Size;
        var baseModules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(baseModules, isFunction, spec);
        DrawCodewords(baseModules, isFunction, allCodewords);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        bool[,]? bestMatrix = null;

        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone();
            ApplyMask(candidate, isFunction, mask);
            DrawFormatBits(candidate, isFunction, mask);
            int penalty = ComputePenalty(candidate);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestMatrix = candidate;
            }
        }

        return (bestMatrix!, version, bestMask);
    }

    public static BitmapSource RenderWpfBitmap(string text, int moduleScale = 8, int quietZone = 4)
    {
        if (moduleScale < 1) throw new ArgumentOutOfRangeException(nameof(moduleScale));
        if (quietZone < 0) throw new ArgumentOutOfRangeException(nameof(quietZone));

        var modules = Encode(text);
        int n = modules.GetLength(0);
        int totalModules = n + 2 * quietZone;
        int pxSize = totalModules * moduleScale;
        int stride = pxSize * 4;
        var pixels = new byte[pxSize * stride];

        // Fill background white (BGRA = 255, 255, 255, 255)
        Array.Fill(pixels, (byte)255);

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (!modules[r, c]) continue;
                int y0 = (r + quietZone) * moduleScale;
                int x0 = (c + quietZone) * moduleScale;
                for (int dy = 0; dy < moduleScale; dy++)
                {
                    int rowStart = (y0 + dy) * stride;
                    for (int dx = 0; dx < moduleScale; dx++)
                    {
                        int idx = rowStart + (x0 + dx) * 4;
                        pixels[idx] = 0x13;     // B (Golan Brand Ink)
                        pixels[idx + 1] = 0x13; // G
                        pixels[idx + 2] = 0x13; // R
                        pixels[idx + 3] = 0xFF; // A
                    }
                }
            }
        }

        var bmp = BitmapSource.Create(pxSize, pxSize, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    static byte[] BuildDataCodewords(byte[] payload, VersionSpec spec)
    {
        var bits = new List<bool>(spec.TotalDataCodewords * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode indicator
        AppendBits(bits, payload.Length, spec.CharCountBits);
        foreach (byte b in payload)
            AppendBits(bits, b, 8);

        int capacityBits = spec.TotalDataCodewords * 8;
        int terminator = Math.Min(4, capacityBits - bits.Count);
        AppendBits(bits, 0, terminator);

        while (bits.Count % 8 != 0)
            bits.Add(false);

        var outBytes = new byte[spec.TotalDataCodewords];
        for (int i = 0; i < bits.Count / 8; i++)
        {
            int val = 0;
            for (int b = 0; b < 8; b++)
                val = (val << 1) | (bits[i * 8 + b] ? 1 : 0);
            outBytes[i] = (byte)val;
        }

        byte[] padBytes = [0xEC, 0x11];
        int padIdx = 0;
        for (int i = bits.Count / 8; i < spec.TotalDataCodewords; i++)
            outBytes[i] = padBytes[padIdx++ & 1];

        return outBytes;
    }

    static void AppendBits(List<bool> list, int val, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
            list.Add(((val >> i) & 1) != 0);
    }

    static byte[] BuildInterleavedCodewords(byte[] data, VersionSpec spec)
    {
        int totalBlocks = spec.Group1Blocks + spec.Group2Blocks;
        var dataBlocks = new byte[totalBlocks][];
        var ecBlocks = new byte[totalBlocks][];
        var gen = BuildReedSolomonGenerator(spec.EcCodewordsPerBlock);

        int offset = 0;
        int blockIdx = 0;
        for (int i = 0; i < spec.Group1Blocks; i++, blockIdx++)
        {
            dataBlocks[blockIdx] = data.AsSpan(offset, spec.Group1DataCodewords).ToArray();
            ecBlocks[blockIdx] = ComputeReedSolomonRemainder(dataBlocks[blockIdx], gen);
            offset += spec.Group1DataCodewords;
        }
        for (int i = 0; i < spec.Group2Blocks; i++, blockIdx++)
        {
            dataBlocks[blockIdx] = data.AsSpan(offset, spec.Group2DataCodewords).ToArray();
            ecBlocks[blockIdx] = ComputeReedSolomonRemainder(dataBlocks[blockIdx], gen);
            offset += spec.Group2DataCodewords;
        }

        int maxDataLen = Math.Max(spec.Group1DataCodewords, spec.Group2DataCodewords);
        var result = new List<byte>(spec.TotalDataCodewords + totalBlocks * spec.EcCodewordsPerBlock);

        for (int i = 0; i < maxDataLen; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                if (i < dataBlocks[b].Length)
                    result.Add(dataBlocks[b][i]);
            }
        }
        for (int i = 0; i < spec.EcCodewordsPerBlock; i++)
        {
            for (int b = 0; b < totalBlocks; b++)
                result.Add(ecBlocks[b][i]);
        }

        return [.. result];
    }

    static byte[] BuildReedSolomonGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    static byte[] ComputeReedSolomonRemainder(byte[] data, byte[] generator)
    {
        var result = new byte[generator.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < generator.Length; i++)
                result[i] ^= GfMultiply(generator[i], factor);
        }
        return result;
    }

    static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, VersionSpec spec)
    {
        int n = spec.Size;

        // Timing patterns
        for (int i = 0; i < n; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
        }

        // 3 Finder patterns + white separators
        DrawFinderPattern(modules, isFunction, 3, 3);
        DrawFinderPattern(modules, isFunction, 3, n - 4);
        DrawFinderPattern(modules, isFunction, n - 4, 3);

        // Alignment patterns
        var centers = spec.AlignmentCenters;
        for (int i = 0; i < centers.Length; i++)
        {
            for (int j = 0; j < centers.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == centers.Length - 1) || (i == centers.Length - 1 && j == 0))
                    continue;
                DrawAlignmentPattern(modules, isFunction, centers[i], centers[j]);
            }
        }

        // Reserve format information areas
        DrawFormatBits(modules, isFunction, 0);

        // Version information areas (Versions 7..10)
        if (spec.Version >= 7)
        {
            int vbits = ComputeVersionBits(spec.Version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vbits >> i) & 1) != 0;
                int a = n - 11 + (i % 3);
                int b = i / 3;
                SetFunctionModule(modules, isFunction, b, a, bit);
                SetFunctionModule(modules, isFunction, a, b, bit);
            }
        }
    }

    static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int centerR, int centerC)
    {
        int n = modules.GetLength(0);
        for (int dr = -4; dr <= 4; dr++)
        {
            for (int dc = -4; dc <= 4; dc++)
            {
                int r = centerR + dr;
                int c = centerC + dc;
                if (r < 0 || r >= n || c < 0 || c >= n) continue;
                int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                SetFunctionModule(modules, isFunction, r, c, dist != 2 && dist != 4);
            }
        }
    }

    static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerR, int centerC)
    {
        for (int dr = -2; dr <= 2; dr++)
        {
            for (int dc = -2; dc <= 2; dc++)
            {
                int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                SetFunctionModule(modules, isFunction, centerR + dr, centerC + dc, dist != 1);
            }
        }
    }

    static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int mask)
    {
        int n = modules.GetLength(0);
        int bits = ComputeFormatBits(mask);

        for (int i = 0; i <= 5; i++)
            SetFunctionModule(modules, isFunction, 8, i, ((bits >> (14 - i)) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 7, ((bits >> 8) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 8, ((bits >> 7) & 1) != 0);
        SetFunctionModule(modules, isFunction, 7, 8, ((bits >> 6) & 1) != 0);
        for (int i = 9; i < 15; i++)
            SetFunctionModule(modules, isFunction, 14 - i, 8, ((bits >> (14 - i)) & 1) != 0);

        for (int i = 0; i < 8; i++)
            SetFunctionModule(modules, isFunction, 8, n - 1 - i, ((bits >> i) & 1) != 0);
        for (int i = 8; i < 15; i++)
            SetFunctionModule(modules, isFunction, n - 15 + i, 8, ((bits >> i) & 1) != 0);

        // Dark module at (4*V + 9, 8) == (n - 8, 8)
        SetFunctionModule(modules, isFunction, n - 8, 8, true);
    }

    static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int r, int c, bool dark)
    {
        modules[r, c] = dark;
        isFunction[r, c] = true;
    }

    static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
    {
        int n = modules.GetLength(0);
        int bitIdx = 0;
        int totalBits = codewords.Length * 8;

        for (int right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < n; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int c = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int r = upward ? n - 1 - vert : vert;
                    if (!isFunction[r, c] && bitIdx < totalBits)
                    {
                        modules[r, c] = ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        bitIdx++;
                    }
                }
            }
        }
    }

    static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
    {
        int n = modules.GetLength(0);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (isFunction[r, c]) continue;
                bool invert = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => r * c % 2 + r * c % 3 == 0,
                    6 => (r * c % 2 + r * c % 3) % 2 == 0,
                    7 => ((r + c) % 2 + r * c % 3) % 2 == 0,
                    _ => false,
                };
                if (invert) modules[r, c] = !modules[r, c];
            }
        }
    }

    static int ComputePenalty(bool[,] modules)
    {
        int n = modules.GetLength(0);
        int penalty = 0;

        // N1: 5+ consecutive same-color modules in row/col
        for (int r = 0; r < n; r++)
        {
            int runLen = 1;
            for (int c = 1; c < n; c++)
            {
                if (modules[r, c] == modules[r, c - 1]) runLen++;
                else { if (runLen >= 5) penalty += 3 + (runLen - 5); runLen = 1; }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }
        for (int c = 0; c < n; c++)
        {
            int runLen = 1;
            for (int r = 1; r < n; r++)
            {
                if (modules[r, c] == modules[r - 1, c]) runLen++;
                else { if (runLen >= 5) penalty += 3 + (runLen - 5); runLen = 1; }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        // N2: 2x2 blocks of same color
        for (int r = 0; r < n - 1; r++)
        {
            for (int c = 0; c < n - 1; c++)
            {
                bool v = modules[r, c];
                if (modules[r, c + 1] == v && modules[r + 1, c] == v && modules[r + 1, c + 1] == v)
                    penalty += 3;
            }
        }

        // N3: Finder-like 1:1:3:1:1 patterns surrounded by 4 light modules
        for (int r = 0; r < n; r++)
        {
            int bits = 0;
            for (int c = 0; c < n; c++)
            {
                bits = ((bits << 1) & 0x7FF) | (modules[r, c] ? 1 : 0);
                if (c >= 10 && (bits == 0b10111010000 || bits == 0b00001011101))
                    penalty += 40;
            }
        }
        for (int c = 0; c < n; c++)
        {
            int bits = 0;
            for (int r = 0; r < n; r++)
            {
                bits = ((bits << 1) & 0x7FF) | (modules[r, c] ? 1 : 0);
                if (r >= 10 && (bits == 0b10111010000 || bits == 0b00001011101))
                    penalty += 40;
            }
        }

        // N4: Dark module balance
        int dark = 0;
        foreach (bool m in modules) if (m) dark++;
        int total = n * n;
        int k = Math.Abs(dark * 20 - total * 10) / total;
        penalty += k * 10;

        return penalty;
    }
}
