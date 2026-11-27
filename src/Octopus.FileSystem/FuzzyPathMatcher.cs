namespace Octopus.FileSystem;

/// <summary>
/// Compact, cache-friendly index record for one workspace file. Precomputes 64-bit character bitmasks,
/// filename offset, and depth once during indexing so per-keystroke queries reject non-matches in nanoseconds.
/// </summary>
public readonly struct IndexedFileEntry
{
    public IndexedFileEntry(string relativePath, bool isIgnored = false)
    {
        RelativePath = relativePath;
        IsIgnored = isIgnored;
        var lastSlash = relativePath.LastIndexOf('/');
        FileNameOffset = lastSlash >= 0 ? lastSlash + 1 : 0;
        var depth = 0;
        for (var i = 0; i < relativePath.Length; i++)
        {
            if (relativePath[i] == '/') depth++;
        }
        Depth = (ushort)Math.Min(depth, ushort.MaxValue);
        PathCharMask = FuzzyPathMatcher.ComputeCharMask(relativePath.AsSpan());
        NameCharMask = lastSlash >= 0
            ? FuzzyPathMatcher.ComputeCharMask(relativePath.AsSpan(FileNameOffset))
            : PathCharMask;
    }

    public string RelativePath { get; }
    public ulong PathCharMask { get; }
    public ulong NameCharMask { get; }
    public int FileNameOffset { get; }
    public ushort Depth { get; }
    public bool IsIgnored { get; }
}

/// <summary>
/// Preprocessed search query for <see cref="FuzzyPathMatcher"/>: normalized path separators, bitmasks,
/// and filename-tail metadata computed once per keystroke.
/// </summary>
public readonly struct PreparedPathQuery
{
    internal PreparedPathQuery(string normalized, ulong mask, int lastSeparatorIndex, ulong tailMask)
    {
        Normalized = normalized;
        Mask = mask;
        LastSeparatorIndex = lastSeparatorIndex;
        TailMask = tailMask;
    }

    public string Normalized { get; }
    public ulong Mask { get; }
    public int LastSeparatorIndex { get; }
    public ulong TailMask { get; }
    public bool HasSeparator => LastSeparatorIndex >= 0;
    public bool IsEmpty => Normalized.Length == 0;
}

/// <summary>
/// Zero-allocation, bitmask-accelerated fuzzy path matcher and scorer for workspace file search.
/// Ranks exact filename matches, filename prefixes, word-boundary substrings, filename fuzzy subsequences,
/// and path-segment matches ahead of scattered directory matches.
/// </summary>
public static class FuzzyPathMatcher
{
    private const int MaxDpTextLength = 256;
    private const int MaxDpQueryLength = 64;
    private const int NegInf = int.MinValue / 4;

    /// <summary>Normalizes a user query and precomputes its character bitmasks.</summary>
    public static PreparedPathQuery Prepare(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new PreparedPathQuery("", 0UL, -1, 0UL);
        }

        var trimmed = query.Trim();
        var normalized = trimmed.Contains('\\') ? trimmed.Replace('\\', '/') : trimmed;
        var mask = ComputeCharMask(normalized.AsSpan());
        var lastSlash = normalized.LastIndexOf('/');
        var tailMask = lastSlash >= 0 && lastSlash + 1 < normalized.Length
            ? ComputeCharMask(normalized.AsSpan(lastSlash + 1))
            : 0UL;
        return new PreparedPathQuery(normalized, mask, lastSlash, tailMask);
    }

    /// <summary>
    /// Computes a 64-bit case-insensitive character presence bitmask.
    /// Any valid subsequence match is guaranteed to satisfy <c>(textMask &amp; queryMask) == queryMask</c>.
    /// </summary>
    public static ulong ComputeCharMask(ReadOnlySpan<char> text)
    {
        var mask = 0UL;
        for (var i = 0; i < text.Length; i++)
        {
            mask |= CharBit(text[i]);
        }
        return mask;
    }

    /// <summary>
    /// Searches <paramref name="entries"/> and returns up to <paramref name="maxResults"/> top-ranked matches.
    /// For an empty query, returns the first <paramref name="maxResults"/> entries in their breadth-first index order.
    /// </summary>
    public static IReadOnlyList<WorkspaceFileMatch> Search(
        ReadOnlySpan<IndexedFileEntry> entries,
        string root,
        string? query,
        int maxResults)
    {
        if (entries.IsEmpty || maxResults <= 0) return [];

        var prepared = Prepare(query);
        if (prepared.IsEmpty)
        {
            var count = Math.Min(maxResults, entries.Length);
            var list = new List<WorkspaceFileMatch>(count);
            for (var i = 0; i < count; i++)
            {
                var rel = entries[i].RelativePath;
                list.Add(new WorkspaceFileMatch(rel, CombineFullPath(root, rel), 0));
            }
            return list;
        }

        // Maintain a small sorted top-K buffer so scanning tens of thousands of entries allocates almost nothing.
        var topCount = 0;
        var topScores = new int[maxResults];
        var topIndices = new int[maxResults];

        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            if (!TryScore(in entry, in prepared, out var score)) continue;

            if (topCount == maxResults)
            {
                var worstScore = topScores[topCount - 1];
                if (score < worstScore) continue;
                if (score == worstScore && !IsBetterTieBreak(entry.RelativePath, entries[topIndices[topCount - 1]].RelativePath))
                {
                    continue;
                }
            }

            // Find insertion position in the tiny sorted buffer (descending score, ascending tie-break).
            var insertAt = topCount < maxResults ? topCount : maxResults - 1;
            while (insertAt > 0)
            {
                var prevScore = topScores[insertAt - 1];
                if (score < prevScore) break;
                if (score == prevScore && !IsBetterTieBreak(entry.RelativePath, entries[topIndices[insertAt - 1]].RelativePath))
                {
                    break;
                }
                insertAt--;
            }

            if (topCount < maxResults) topCount++;
            for (var move = topCount - 1; move > insertAt; move--)
            {
                topScores[move] = topScores[move - 1];
                topIndices[move] = topIndices[move - 1];
            }
            topScores[insertAt] = score;
            topIndices[insertAt] = i;
        }

        if (topCount == 0) return [];

        var result = new List<WorkspaceFileMatch>(topCount);
        for (var i = 0; i < topCount; i++)
        {
            var rel = entries[topIndices[i]].RelativePath;
            result.Add(new WorkspaceFileMatch(rel, CombineFullPath(root, rel), topScores[i]));
        }
        return result;
    }

    /// <summary>Scores a single indexed file entry against a prepared query.</summary>
    public static bool TryScore(in IndexedFileEntry entry, in PreparedPathQuery prepared, out int score)
    {
        if (prepared.IsEmpty)
        {
            score = 0;
            return true;
        }

        if ((entry.PathCharMask & prepared.Mask) != prepared.Mask)
        {
            score = 0;
            return false;
        }

        var path = entry.RelativePath.AsSpan();
        var query = prepared.Normalized.AsSpan();
        var fileName = path[entry.FileNameOffset..];
        int baseScore;

        if (!prepared.HasSeparator)
        {
            if ((entry.NameCharMask & prepared.Mask) == prepared.Mask
                && TryScoreFileName(fileName, query, out baseScore))
            {
                // Filename match: highest priority tiers (40,000 .. 100,000).
            }
            else if (TryScorePathWithoutSeparator(path, entry.FileNameOffset, query, out baseScore))
            {
                // Directory or cross-segment match (5,000 .. 30,000).
            }
            else
            {
                score = 0;
                return false;
            }
        }
        else
        {
            if (!TryScorePathWithSeparator(path, fileName, entry.FileNameOffset, entry.NameCharMask, in prepared, out baseScore))
            {
                score = 0;
                return false;
            }
        }

        // Universal gentle tie-breakers that never overturn major match tiers.
        if (entry.IsIgnored) baseScore -= 500;
        baseScore -= Math.Min((int)entry.Depth, 32) * 12;
        baseScore -= Math.Min(path.Length, 400);

        score = baseScore;
        return true;
    }

    private static bool TryScoreFileName(ReadOnlySpan<char> fileName, ReadOnlySpan<char> query, out int score)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score = 100_000 + (fileName.Equals(query, StringComparison.Ordinal) ? 200 : 0);
            return true;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            if (fileName.Length > query.Length && fileName[query.Length] == '.')
            {
                score = 95_000 - Math.Min(fileName.Length - query.Length, 100) * 4;
                return true;
            }
            score = 85_000 - Math.Min(fileName.Length - query.Length, 200) * 8;
            return true;
        }

        if (TryFindBestSubstring(fileName, query, out var subPos, out var atBoundary))
        {
            score = (atBoundary ? 72_000 : 60_000)
                - Math.Min(subPos, 100) * 20
                - Math.Min(fileName.Length - query.Length, 200) * 6;
            return true;
        }

        if (TryFuzzyScore(fileName, query, fileNameOffset: 0, out var fuzzy))
        {
            score = 40_000 + fuzzy - Math.Min(fileName.Length - query.Length, 200) * 4;
            return true;
        }

        score = 0;
        return false;
    }

    private static bool TryScorePathWithoutSeparator(
        ReadOnlySpan<char> path,
        int fileNameOffset,
        ReadOnlySpan<char> query,
        out int score)
    {
        if (TryFindBestSubstring(path, query, out var subPos, out var atBoundary))
        {
            var atSegmentStart = subPos == 0 || path[subPos - 1] == '/';
            var tier = atSegmentStart ? 30_000 : atBoundary ? 24_000 : 18_000;
            score = tier
                - Math.Min(subPos, 200) * 10
                - Math.Min(path.Length - query.Length, 300) * 3;
            return true;
        }

        if (TryFuzzyScore(path, query, fileNameOffset, out var fuzzy))
        {
            score = 5_000 + fuzzy - Math.Min(path.Length - query.Length, 300) * 2;
            return true;
        }

        score = 0;
        return false;
    }

    private static bool TryScorePathWithSeparator(
        ReadOnlySpan<char> path,
        ReadOnlySpan<char> fileName,
        int fileNameOffset,
        ulong nameCharMask,
        in PreparedPathQuery prepared,
        out int score)
    {
        var query = prepared.Normalized.AsSpan();

        if (path.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score = 100_000;
            return true;
        }

        // Split "dir/tail" queries (e.g. "py/fast" or "em/ff40"): a match where the directory prefix matches the
        // directory part AND the tail matches the filename itself should always outrank directory-only matches
        // such as "py/fast_utils/helper.py".
        var tail = query[(prepared.LastSeparatorIndex + 1)..];
        var dirQuery = query[..prepared.LastSeparatorIndex];
        var hasTail = !tail.IsEmpty;

        if (hasTail
            && fileNameOffset > 0
            && (nameCharMask & prepared.TailMask) == prepared.TailMask
            && TryScoreFileName(fileName, tail, out var tailScore))
        {
            var dirPart = path[..(fileNameOffset - 1)];
            var dirBonus = 0;
            if (dirQuery.IsEmpty || TryScoreDirectoryPrefix(dirPart, dirQuery, out dirBonus))
            {
                var bonus = dirBonus;
                if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    bonus += 10_000;
                }
                else if (path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bonus += 5_000;
                }

                // Keep true dir+filename matches in the high tier (55,000 .. 98,000).
                score = Math.Min(98_000, (tailScore * 3 / 4) + 18_000 + bonus);
                return true;
            }
        }

        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score = (hasTail ? 45_000 : 85_000) - Math.Min(path.Length - query.Length, 300) * 4;
            return true;
        }

        if (TryFindBestSubstring(path, query, out var subPos, out _))
        {
            var atSegmentStart = subPos == 0 || path[subPos - 1] == '/';
            var tier = hasTail
                ? (atSegmentStart ? 38_000 : 32_000)
                : (atSegmentStart ? 72_000 : 62_000);
            score = tier
                - Math.Min(subPos, 200) * 10
                - Math.Min(path.Length - query.Length, 300) * 3;
            return true;
        }

        if (TryFuzzyScore(path, query, fileNameOffset, out var fuzzy))
        {
            score = 15_000 + fuzzy - Math.Min(path.Length - query.Length, 300) * 2;
            return true;
        }

        score = 0;
        return false;
    }

    private static bool TryScoreDirectoryPrefix(ReadOnlySpan<char> dirPart, ReadOnlySpan<char> dirQuery, out int bonus)
    {
        if (dirPart.Equals(dirQuery, StringComparison.OrdinalIgnoreCase))
        {
            bonus = 8_000;
            return true;
        }
        if (dirPart.StartsWith(dirQuery, StringComparison.OrdinalIgnoreCase))
        {
            bonus = 6_000;
            return true;
        }
        if (TryFindBestSubstring(dirPart, dirQuery, out var pos, out _))
        {
            var atSegmentStart = pos == 0 || dirPart[pos - 1] == '/';
            bonus = atSegmentStart ? 4_500 : 3_000;
            return true;
        }
        if (TryFuzzyScore(dirPart, dirQuery, fileNameOffset: 0, out var fuzzy))
        {
            bonus = Math.Clamp(fuzzy / 2, 0, 2_500);
            return true;
        }
        bonus = 0;
        return false;
    }

    /// <summary>
    /// Finds the best case-insensitive contiguous occurrence of <paramref name="query"/> in <paramref name="text"/>,
    /// preferring occurrences that start on a path or word boundary.
    /// </summary>
    private static bool TryFindBestSubstring(
        ReadOnlySpan<char> text,
        ReadOnlySpan<char> query,
        out int bestPos,
        out bool bestAtBoundary)
    {
        var first = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (first < 0)
        {
            bestPos = -1;
            bestAtBoundary = false;
            return false;
        }

        bestPos = first;
        bestAtBoundary = first == 0 || IsWordBoundary(text, first);
        if (bestAtBoundary && (first == 0 || text[first - 1] == '/'))
        {
            return true;
        }

        var searchFrom = first + 1;
        while (searchFrom + query.Length <= text.Length)
        {
            var nextRel = text[searchFrom..].IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (nextRel < 0) break;
            var pos = searchFrom + nextRel;
            var atSlash = text[pos - 1] == '/';
            var atBoundary = atSlash || IsWordBoundary(text, pos);
            if (atSlash)
            {
                bestPos = pos;
                bestAtBoundary = true;
                return true;
            }
            if (atBoundary && !bestAtBoundary)
            {
                bestPos = pos;
                bestAtBoundary = true;
            }
            searchFrom = pos + 1;
        }

        return true;
    }

    /// <summary>
    /// Zero-allocation Smith-Waterman / fzf-style subsequence scorer.
    /// Uses a fast O(N) subsequence gate first, then stack-allocated 2-row DP for optimal boundary &amp; run alignment.
    /// </summary>
    private static bool TryFuzzyScore(
        ReadOnlySpan<char> text,
        ReadOnlySpan<char> query,
        int fileNameOffset,
        out int score)
    {
        if (query.Length == 0 || query.Length > text.Length || !IsSubsequence(text, query))
        {
            score = 0;
            return false;
        }

        if (text.Length > MaxDpTextLength || query.Length > MaxDpQueryLength)
        {
            score = ScoreGreedySubsequence(text, query, fileNameOffset);
            return true;
        }

        var n = text.Length;
        var m = query.Length;

        Span<short> bonuses = stackalloc short[n];
        for (var j = 0; j < n; j++)
        {
            bonuses[j] = ComputePositionBonus(text, j, fileNameOffset);
        }

        Span<int> prevMatch = stackalloc int[n];
        Span<int> prevBest = stackalloc int[n];
        Span<int> currMatch = stackalloc int[n];
        Span<int> currBest = stackalloc int[n];

        var q0 = query[0];
        var runningBest = NegInf;
        for (var j = 0; j < n; j++)
        {
            var matchVal = CharsEqualIgnoreCase(q0, text[j])
                ? 16 + bonuses[j] + (q0 == text[j] ? 2 : 0) - Math.Min(j, 24)
                : NegInf;
            prevMatch[j] = matchVal;
            var decayed = runningBest > NegInf ? runningBest - (j < fileNameOffset ? 1 : 2) : NegInf;
            runningBest = Math.Max(matchVal, decayed);
            prevBest[j] = runningBest;
        }

        for (var i = 1; i < m; i++)
        {
            var qi = query[i];
            runningBest = NegInf;
            for (var j = 0; j < i; j++)
            {
                currMatch[j] = NegInf;
                currBest[j] = NegInf;
            }
            for (var j = i; j < n; j++)
            {
                var matchVal = NegInf;
                if (CharsEqualIgnoreCase(qi, text[j]))
                {
                    var charBonus = 16 + bonuses[j] + (qi == text[j] ? 2 : 0);
                    var fromConsecutive = prevMatch[j - 1] > NegInf ? prevMatch[j - 1] + charBonus + 28 : NegInf;
                    var fromGap = prevBest[j - 1] > NegInf ? prevBest[j - 1] + charBonus - 8 : NegInf;
                    matchVal = Math.Max(fromConsecutive, fromGap);
                }
                currMatch[j] = matchVal;
                var decayed = runningBest > NegInf ? runningBest - (j < fileNameOffset ? 1 : 2) : NegInf;
                runningBest = Math.Max(matchVal, decayed);
                currBest[j] = runningBest;
            }

            var tmpMatch = prevMatch;
            prevMatch = currMatch;
            currMatch = tmpMatch;

            var tmpBest = prevBest;
            prevBest = currBest;
            currBest = tmpBest;
        }

        var best = NegInf;
        for (var j = m - 1; j < n; j++)
        {
            if (prevMatch[j] > best) best = prevMatch[j];
        }

        score = best;
        return best > NegInf;
    }

    private static int ScoreGreedySubsequence(ReadOnlySpan<char> text, ReadOnlySpan<char> query, int fileNameOffset)
    {
        var score = 0;
        var prevIndex = -2;
        var qi = 0;
        for (var j = 0; j < text.Length && qi < query.Length; j++)
        {
            if (!CharsEqualIgnoreCase(query[qi], text[j])) continue;
            score += 16 + ComputePositionBonus(text, j, fileNameOffset);
            if (j == prevIndex + 1)
            {
                score += 28;
            }
            else if (prevIndex >= 0)
            {
                score -= 8 + Math.Min(j - prevIndex - 1, 32) * 2;
            }
            prevIndex = j;
            qi++;
        }
        return score;
    }

    private static short ComputePositionBonus(ReadOnlySpan<char> text, int index, int fileNameOffset)
    {
        var bonus = index >= fileNameOffset ? 6 : 0;
        if (index == fileNameOffset) return (short)(bonus + 48);
        if (index == 0) return (short)(bonus + 40);

        var prev = text[index - 1];
        if (prev == '/') return (short)(bonus + 36);
        if (prev is '_' or '-' or '.' or ' ') return (short)(bonus + 32);

        var curr = text[index];
        if (char.IsLower(prev) && char.IsUpper(curr)) return (short)(bonus + 28);
        if ((char.IsLetter(prev) && char.IsDigit(curr)) || (char.IsDigit(prev) && char.IsLetter(curr)))
        {
            return (short)(bonus + 20);
        }
        return (short)bonus;
    }

    private static bool IsWordBoundary(ReadOnlySpan<char> text, int index)
    {
        if (index <= 0) return true;
        var prev = text[index - 1];
        if (prev is '/' or '_' or '-' or '.' or ' ') return true;
        var curr = text[index];
        return (char.IsLower(prev) && char.IsUpper(curr))
            || (char.IsLetter(prev) && char.IsDigit(curr))
            || (char.IsDigit(prev) && char.IsLetter(curr));
    }

    private static bool IsSubsequence(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        var qi = 0;
        for (var i = 0; i < text.Length && qi < query.Length; i++)
        {
            if (CharsEqualIgnoreCase(query[qi], text[i])) qi++;
        }
        return qi == query.Length;
    }

    private static bool CharsEqualIgnoreCase(char a, char b)
    {
        if (a == b) return true;
        if (a < 128 && b < 128)
        {
            return FoldAscii(a) == FoldAscii(b);
        }
        return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }

    private static char FoldAscii(char c) => (uint)(c - 'A') <= ('Z' - 'A') ? (char)(c | 0x20) : c;

    private static ulong CharBit(char c)
    {
        if (c < 128)
        {
            var lower = FoldAscii(c);
            if ((uint)(lower - 'a') <= 25) return 1UL << (lower - 'a');
            if ((uint)(lower - '0') <= 9) return 1UL << (26 + (lower - '0'));
            return lower switch
            {
                '/' or '\\' => 1UL << 36,
                '.' => 1UL << 37,
                '_' => 1UL << 38,
                '-' => 1UL << 39,
                ' ' => 1UL << 40,
                _ => 1UL << (41 + (lower % 22)),
            };
        }
        return 1UL << 63;
    }

    private static bool IsBetterTieBreak(string candidate, string existing)
    {
        if (candidate.Length != existing.Length) return candidate.Length < existing.Length;
        return StringComparer.OrdinalIgnoreCase.Compare(candidate, existing) < 0;
    }

    private static string CombineFullPath(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(root)) return relativePath;
        var platformRel = Path.DirectorySeparatorChar == '/'
            ? relativePath
            : relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(root, platformRel);
    }
}
