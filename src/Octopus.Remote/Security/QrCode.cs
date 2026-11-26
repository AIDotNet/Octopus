using System.Text;

namespace Octopus.Remote.Security;

/// <summary>Error correction level of a QR Code (ISO/IEC 18004).</summary>
public enum QrErrorCorrectionLevel
{
    /// <summary>Recovers up to ~7% damaged codewords.</summary>
    Low = 0,
    /// <summary>Recovers up to ~15% damaged codewords.</summary>
    Medium = 1,
    /// <summary>Recovers up to ~25% damaged codewords.</summary>
    Quartile = 2,
    /// <summary>Recovers up to ~30% damaged codewords.</summary>
    High = 3,
}

/// <summary>
/// An immutable QR Code matrix: <see cref="Size"/> × <see cref="Size"/> dark/light modules without the quiet zone.
/// </summary>
public sealed class QrCodeMatrix
{
    private readonly bool[] _modules;

    internal QrCodeMatrix(int version, int size, bool[] modules)
    {
        Version = version;
        Size = size;
        _modules = modules;
    }

    /// <summary>QR Code version (1 to 40).</summary>
    public int Version { get; }

    /// <summary>Width and height in modules (<c>17 + 4 * Version</c>).</summary>
    public int Size { get; }

    /// <summary>True when the module at (<paramref name="x"/>, <paramref name="y"/>) is dark.</summary>
    public bool this[int x, int y] => (uint)x < (uint)Size && (uint)y < (uint)Size && _modules[y * Size + x];
}

/// <summary>
/// Pure managed, NativeAOT-safe ISO/IEC 18004 QR Code encoder (UTF-8 Byte mode, versions 1–40).
/// Used by the desktop host to show pairing and self-hosted relay links as scannable QR codes with zero third-party
/// dependencies.
/// </summary>
public static class QrCodeEncoder
{
    private const int MinVersion = 1;
    private const int MaxVersion = 40;

    // Format bits for ECC levels: Low=01, Medium=00, Quartile=11, High=10.
    private static readonly int[] EccFormatBits = [1, 0, 3, 2];

    // ISO/IEC 18004 Table 9: ECC codewords per block [eccLevel][version].
    private static readonly sbyte[][] EccCodewordsPerBlock =
    [
        // Low
        [-1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        // Medium
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        // Quartile
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        // High
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    // ISO/IEC 18004 Table 9: Number of error correction blocks [eccLevel][version].
    private static readonly sbyte[][] NumErrorCorrectionBlocks =
    [
        // Low
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        // Medium
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        // Quartile
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        // High
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    /// <summary>Encodes UTF-8 text into a QR Code matrix using Byte mode.</summary>
    public static QrCodeMatrix Encode(string text, QrErrorCorrectionLevel ecc = QrErrorCorrectionLevel.Medium)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(Encoding.UTF8.GetBytes(text), ecc);
    }

    /// <summary>Encodes arbitrary binary bytes into a QR Code matrix using Byte mode.</summary>
    public static QrCodeMatrix Encode(ReadOnlySpan<byte> data, QrErrorCorrectionLevel ecc = QrErrorCorrectionLevel.Medium)
    {
        var eccIndex = (int)ecc;
        if ((uint)eccIndex > 3) throw new ArgumentOutOfRangeException(nameof(ecc));

        // Pick the smallest version whose data capacity fits the Byte-mode header + payload.
        var version = -1;
        var dataBits = 0;
        for (var v = MinVersion; v <= MaxVersion; v++)
        {
            var countBits = v <= 9 ? 8 : 16;
            var neededBits = 4 + countBits + data.Length * 8;
            var capacityBits = GetNumDataCodewords(v, eccIndex) * 8;
            if (neededBits <= capacityBits)
            {
                version = v;
                dataBits = capacityBits;
                break;
            }
        }
        if (version < 0) throw new ArgumentException($"Payload of {data.Length} bytes exceeds maximum QR Code capacity.", nameof(data));

        // Upgrade ECC level for free if the chosen version still fits a stronger ECC level.
        for (var stronger = 3; stronger > eccIndex; stronger--)
        {
            var countBits = version <= 9 ? 8 : 16;
            var neededBits = 4 + countBits + data.Length * 8;
            if (neededBits <= GetNumDataCodewords(version, stronger) * 8)
            {
                eccIndex = stronger;
                dataBits = GetNumDataCodewords(version, stronger) * 8;
                break;
            }
        }

        var dataCodewords = BuildDataCodewords(data, version, dataBits);
        var allCodewords = AddEccAndInterleave(dataCodewords, version, eccIndex);

        var size = version * 4 + 17;
        var modules = new bool[size * size];
        var isFunction = new bool[size * size];

        DrawFunctionPatterns(modules, isFunction, version, size, eccIndex);
        DrawCodewords(modules, isFunction, size, allCodewords);

        // Evaluate all 8 mask patterns and keep the one with the lowest ISO penalty score.
        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, size, mask);
            DrawFormatBits(modules, isFunction, size, eccIndex, mask);
            var penalty = ComputePenaltyScore(modules, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
            ApplyMask(modules, isFunction, size, mask);
        }

        ApplyMask(modules, isFunction, size, bestMask);
        DrawFormatBits(modules, isFunction, size, eccIndex, bestMask);
        return new QrCodeMatrix(version, size, modules);
    }

    private static byte[] BuildDataCodewords(ReadOnlySpan<byte> data, int version, int capacityBits)
    {
        var buffer = new List<byte>(capacityBits / 8);
        var bitBuf = 0;
        var bitLen = 0;

        void AppendBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bitBuf = (bitBuf << 1) | ((value >> i) & 1);
                bitLen++;
                if (bitLen == 8)
                {
                    buffer.Add((byte)bitBuf);
                    bitBuf = 0;
                    bitLen = 0;
                }
            }
        }

        // Mode indicator: Byte mode (0100).
        AppendBits(0b0100, 4);
        AppendBits(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) AppendBits(b, 8);

        // Terminator (up to 4 zero bits) and pad to byte boundary.
        var usedBits = buffer.Count * 8 + bitLen;
        AppendBits(0, Math.Min(4, capacityBits - usedBits));
        if (bitLen > 0) AppendBits(0, 8 - bitLen);

        // Pad alternating bytes 0xEC, 0x11.
        var totalBytes = capacityBits / 8;
        for (var pad = 0; buffer.Count < totalBytes; pad ^= 1)
        {
            buffer.Add(pad == 0 ? (byte)0xEC : (byte)0x11);
        }

        return buffer.ToArray();
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, int eccIndex)
    {
        var numBlocks = NumErrorCorrectionBlocks[eccIndex][version];
        var blockEccLen = EccCodewordsPerBlock[eccIndex][version];
        var rawCodewords = GetNumRawDataModules(version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var rsDiv = ComputeReedSolomonDivisor(blockEccLen);
        var blocks = new byte[numBlocks][];
        var offset = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            var dataLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var blockData = data.AsSpan(offset, dataLen);
            offset += dataLen;
            var ecc = ComputeReedSolomonRemainder(blockData, rsDiv);
            var full = new byte[shortBlockLen + 1];
            blockData.CopyTo(full);
            ecc.CopyTo(full.AsSpan(dataLen));
            blocks[i] = full;
        }

        var result = new byte[rawCodewords];
        var pos = 0;
        var maxDataLen = shortBlockLen - blockEccLen + 1;
        for (var col = 0; col < maxDataLen; col++)
        {
            for (var b = 0; b < numBlocks; b++)
            {
                var dataLen = shortBlockLen - blockEccLen + (b < numShortBlocks ? 0 : 1);
                if (col < dataLen) result[pos++] = blocks[b][col];
            }
        }
        for (var col = 0; col < blockEccLen; col++)
        {
            for (var b = 0; b < numBlocks; b++)
            {
                var dataLen = shortBlockLen - blockEccLen + (b < numShortBlocks ? 0 : 1);
                result[pos++] = blocks[b][dataLen + col];
            }
        }
        return result;
    }

    private static byte[] ComputeReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = MultiplyGf256(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = MultiplyGf256(root, 0x02);
        }
        return result;
    }

    private static byte[] ComputeReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < divisor.Length; i++)
            {
                result[i] ^= MultiplyGf256(divisor[i], factor);
            }
        }
        return result;
    }

    private static byte MultiplyGf256(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static int GetNumRawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int GetNumDataCodewords(int version, int eccIndex) =>
        GetNumRawDataModules(version) / 8 - EccCodewordsPerBlock[eccIndex][version] * NumErrorCorrectionBlocks[eccIndex][version];

    private static void DrawFunctionPatterns(bool[] modules, bool[] isFunction, int version, int size, int eccIndex)
    {
        // Timing patterns.
        for (var i = 0; i < size; i++)
        {
            SetFunction(modules, isFunction, size, 6, i, i % 2 == 0);
            SetFunction(modules, isFunction, size, i, 6, i % 2 == 0);
        }

        // Three finder patterns.
        DrawFinderPattern(modules, isFunction, size, 3, 3);
        DrawFinderPattern(modules, isFunction, size, size - 4, 3);
        DrawFinderPattern(modules, isFunction, size, 3, size - 4);

        // Alignment patterns.
        var alignPos = GetAlignmentPatternPositions(version, size);
        var numAlign = alignPos.Length;
        for (var i = 0; i < numAlign; i++)
        {
            for (var j = 0; j < numAlign; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == numAlign - 1) || (i == numAlign - 1 && j == 0)) continue;
                DrawAlignmentPattern(modules, isFunction, size, alignPos[i], alignPos[j]);
            }
        }

        // Reserve format bits (dummy mask 0) and draw version bits (version >= 7).
        DrawFormatBits(modules, isFunction, size, eccIndex, 0);
        DrawVersionBits(modules, isFunction, version, size);
    }

    private static void DrawFinderPattern(bool[] modules, bool[] isFunction, int size, int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(modules, isFunction, size, x, y, dist != 2 && dist != 4);
            }
        }
    }

    private static void DrawAlignmentPattern(bool[] modules, bool[] isFunction, int size, int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(modules, isFunction, size, cx + dx, cy + dy, dist != 1);
            }
        }
    }

    private static int[] GetAlignmentPatternPositions(int version, int size)
    {
        if (version == 1) return [];
        var numAlign = version / 7 + 2;
        var step = version == 32 ? 26 : (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }
        return result;
    }

    private static void DrawFormatBits(bool[] modules, bool[] isFunction, int size, int eccIndex, int mask)
    {
        var data = (EccFormatBits[eccIndex] << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;

        // First copy (around top-left finder).
        for (var i = 0; i <= 5; i++) SetFunction(modules, isFunction, size, 8, i, ((bits >> i) & 1) != 0);
        SetFunction(modules, isFunction, size, 8, 7, ((bits >> 6) & 1) != 0);
        SetFunction(modules, isFunction, size, 8, 8, ((bits >> 7) & 1) != 0);
        SetFunction(modules, isFunction, size, 7, 8, ((bits >> 8) & 1) != 0);
        for (var i = 9; i < 15; i++) SetFunction(modules, isFunction, size, 14 - i, 8, ((bits >> i) & 1) != 0);

        // Second copy (split between bottom-left and top-right finders).
        for (var i = 0; i < 8; i++) SetFunction(modules, isFunction, size, size - 1 - i, 8, ((bits >> i) & 1) != 0);
        for (var i = 8; i < 15; i++) SetFunction(modules, isFunction, size, 8, size - 15 + i, ((bits >> i) & 1) != 0);
        SetFunction(modules, isFunction, size, 8, size - 8, true); // always dark
    }

    private static void DrawVersionBits(bool[] modules, bool[] isFunction, int version, int size)
    {
        if (version < 7) return;
        var rem = version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = (version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0;
            var a = size - 11 + i % 3;
            var b = i / 3;
            SetFunction(modules, isFunction, size, a, b, dark);
            SetFunction(modules, isFunction, size, b, a, dark);
        }
    }

    private static void DrawCodewords(bool[] modules, bool[] isFunction, int size, byte[] data)
    {
        var bitIndex = 0;
        var totalBits = data.Length * 8;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    var idx = y * size + x;
                    if (!isFunction[idx] && bitIndex < totalBits)
                    {
                        modules[idx] = ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        bitIndex++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[] modules, bool[] isFunction, int size, int mask)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var idx = y * size + x;
                if (isFunction[idx]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => false,
                };
                if (invert) modules[idx] = !modules[idx];
            }
        }
    }

    private static int ComputePenaltyScore(bool[] modules, int size)
    {
        var score = 0;

        // Rule 1 & Rule 3: adjacent same-color runs and finder-like patterns in rows.
        Span<int> history = stackalloc int[7];
        for (var y = 0; y < size; y++)
        {
            var runColor = false;
            var runX = 0;
            history.Clear();
            for (var x = 0; x < size; x++)
            {
                var dark = modules[y * size + x];
                if (dark == runColor)
                {
                    runX++;
                    if (runX == 5) score += 3;
                    else if (runX > 5) score += 1;
                }
                else
                {
                    AddHistory(runX, history);
                    if (!runColor) score += CountFinderPatterns(history, size) * 40;
                    runColor = dark;
                    runX = 1;
                }
            }
            score += TerminateHistory(runColor, runX, history, size) * 40;
        }

        // Rule 1 & Rule 3 in columns.
        for (var x = 0; x < size; x++)
        {
            var runColor = false;
            var runY = 0;
            history.Clear();
            for (var y = 0; y < size; y++)
            {
                var dark = modules[y * size + x];
                if (dark == runColor)
                {
                    runY++;
                    if (runY == 5) score += 3;
                    else if (runY > 5) score += 1;
                }
                else
                {
                    AddHistory(runY, history);
                    if (!runColor) score += CountFinderPatterns(history, size) * 40;
                    runColor = dark;
                    runY = 1;
                }
            }
            score += TerminateHistory(runColor, runY, history, size) * 40;
        }

        // Rule 2: 2x2 blocks of same color.
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var color = modules[y * size + x];
                if (color == modules[y * size + x + 1]
                    && color == modules[(y + 1) * size + x]
                    && color == modules[(y + 1) * size + x + 1])
                {
                    score += 3;
                }
            }
        }

        // Rule 4: balance of dark and light modules.
        var darkCount = 0;
        foreach (var m in modules)
        {
            if (m) darkCount++;
        }
        var total = size * size;
        var k = (Math.Abs(darkCount * 20 - total * 10) + total - 1) / total - 1;
        score += k * 10;
        return score;
    }

    private static void AddHistory(int runLength, Span<int> history)
    {
        for (var i = history.Length - 1; i >= 1; i--) history[i] = history[i - 1];
        history[0] = runLength;
    }

    private static int TerminateHistory(bool currentRunColor, int currentRunLength, Span<int> history, int size)
    {
        if (currentRunColor)
        {
            AddHistory(currentRunLength, history);
            currentRunLength = 0;
        }
        currentRunLength += size;
        AddHistory(currentRunLength, history);
        return CountFinderPatterns(history, size);
    }

    private static int CountFinderPatterns(ReadOnlySpan<int> history, int size)
    {
        var n = history[1];
        if (n <= 0) return 0;
        var core = history[2] == n && history[3] == 3 * n && history[4] == n && history[5] == n;
        if (!core) return 0;
        return (history[0] >= 4 * n && history[6] >= n ? 1 : 0)
             + (history[6] >= 4 * n && history[0] >= n ? 1 : 0);
    }

    private static void SetFunction(bool[] modules, bool[] isFunction, int size, int x, int y, bool dark)
    {
        var idx = y * size + x;
        modules[idx] = dark;
        isFunction[idx] = true;
    }
}
