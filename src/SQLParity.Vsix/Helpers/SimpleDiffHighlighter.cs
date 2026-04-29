using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SQLParity.Vsix.Helpers
{
    public static class SimpleDiffHighlighter
    {
        private static readonly SolidColorBrush AddedLineBrush = new SolidColorBrush(Color.FromRgb(230, 255, 236));
        private static readonly SolidColorBrush RemovedLineBrush = new SolidColorBrush(Color.FromRgb(255, 238, 240));
        private static readonly SolidColorBrush AddedInlineBrush = new SolidColorBrush(Color.FromRgb(171, 242, 188));
        private static readonly SolidColorBrush RemovedInlineBrush = new SolidColorBrush(Color.FromRgb(255, 200, 200));
        private static readonly SolidColorBrush ModifiedLineBrush = new SolidColorBrush(Color.FromRgb(255, 248, 220));
        private static readonly SolidColorBrush PaddingBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");

        static SimpleDiffHighlighter()
        {
            AddedLineBrush.Freeze();
            RemovedLineBrush.Freeze();
            AddedInlineBrush.Freeze();
            RemovedInlineBrush.Freeze();
            ModifiedLineBrush.Freeze();
            PaddingBrush.Freeze();
        }

        /// <summary>
        /// Creates aligned FlowDocuments for both sides using LCS-based diff.
        /// Both documents will have the same number of lines, with blank padding
        /// where one side has content the other doesn't (like WinMerge).
        /// </summary>
        private static readonly SolidColorBrush LineNumBrush = CreateFrozenBrush(Color.FromRgb(140, 140, 140));

        private static SolidColorBrush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static void CreateAlignedDiffDocuments(
            string ddlA, string ddlB,
            out FlowDocument docA, out FlowDocument docB)
        {
            docA = CreateBaseDocument();
            docB = CreateBaseDocument();

            var linesA = SplitLines(ddlA ?? string.Empty);
            var linesB = SplitLines(ddlB ?? string.Empty);
            int lineNumA = 0;
            int lineNumB = 0;

            // Compute LCS using normalized lines for matching
            var normedA = NormalizeLines(linesA);
            var normedB = NormalizeLines(linesB);
            var lcs = ComputeLcs(normedA, normedB);

            // Walk both sides and the LCS to produce aligned output
            int idxA = 0;
            int idxB = 0;
            int idxLcs = 0;

            while (idxA < linesA.Count || idxB < linesB.Count)
            {
                if (idxLcs < lcs.Count)
                {
                    // Emit lines from A that are before the next LCS match
                    bool aAtLcs = idxA < linesA.Count
                        && string.Equals(normedA[idxA], lcs[idxLcs], StringComparison.OrdinalIgnoreCase);
                    bool bAtLcs = idxB < linesB.Count
                        && string.Equals(normedB[idxB], lcs[idxLcs], StringComparison.OrdinalIgnoreCase);

                    if (aAtLcs && bAtLcs)
                    {
                        lineNumA++; lineNumB++;
                        docA.Blocks.Add(MakeNumberedParagraph(lineNumA, linesA[idxA], null));
                        docB.Blocks.Add(MakeNumberedParagraph(lineNumB, linesB[idxB], null));
                        idxA++;
                        idxB++;
                        idxLcs++;
                    }
                    else if (!aAtLcs && !bAtLcs)
                    {
                        if (idxA < linesA.Count && idxB < linesB.Count
                            && AreSimilar(normedA[idxA], normedB[idxB]))
                        {
                            lineNumA++; lineNumB++;
                            docA.Blocks.Add(MakeNumberedInlineDiff(lineNumA, linesA[idxA], linesB[idxB], true));
                            docB.Blocks.Add(MakeNumberedInlineDiff(lineNumB, linesB[idxB], linesA[idxA], false));
                            idxA++;
                            idxB++;
                        }
                        else if (idxA < linesA.Count)
                        {
                            lineNumA++;
                            docA.Blocks.Add(MakeNumberedParagraph(lineNumA, linesA[idxA], AddedLineBrush));
                            docB.Blocks.Add(MakePaddingParagraph());
                            idxA++;
                        }
                        else if (idxB < linesB.Count)
                        {
                            lineNumB++;
                            docA.Blocks.Add(MakePaddingParagraph());
                            docB.Blocks.Add(MakeNumberedParagraph(lineNumB, linesB[idxB], RemovedLineBrush));
                            idxB++;
                        }
                    }
                    else if (!aAtLcs)
                    {
                        lineNumA++;
                        docA.Blocks.Add(MakeNumberedParagraph(lineNumA, linesA[idxA], AddedLineBrush));
                        docB.Blocks.Add(MakePaddingParagraph());
                        idxA++;
                    }
                    else
                    {
                        lineNumB++;
                        docA.Blocks.Add(MakePaddingParagraph());
                        docB.Blocks.Add(MakeNumberedParagraph(lineNumB, linesB[idxB], RemovedLineBrush));
                        idxB++;
                    }
                }
                else
                {
                    // Past the end of LCS — remaining lines are extras
                    if (idxA < linesA.Count && idxB < linesB.Count
                        && AreSimilar(normedA[idxA], normedB[idxB]))
                    {
                        lineNumA++; lineNumB++;
                        docA.Blocks.Add(MakeNumberedInlineDiff(lineNumA, linesA[idxA], linesB[idxB], true));
                        docB.Blocks.Add(MakeNumberedInlineDiff(lineNumB, linesB[idxB], linesA[idxA], false));
                        idxA++;
                        idxB++;
                    }
                    else if (idxA < linesA.Count)
                    {
                        lineNumA++;
                        docA.Blocks.Add(MakeNumberedParagraph(lineNumA, linesA[idxA], AddedLineBrush));
                        docB.Blocks.Add(MakePaddingParagraph());
                        idxA++;
                    }
                    else if (idxB < linesB.Count)
                    {
                        lineNumB++;
                        docA.Blocks.Add(MakePaddingParagraph());
                        docB.Blocks.Add(MakeNumberedParagraph(lineNumB, linesB[idxB], RemovedLineBrush));
                        idxB++;
                    }
                }
            }
        }

        /// <summary>
        /// Legacy method for backward compatibility — creates a single side's document.
        /// Prefer CreateAlignedDiffDocuments for paired display.
        /// </summary>
        public static FlowDocument CreateDiffDocument(string thisSideDdl, string otherSideDdl, bool isSource)
        {
            FlowDocument docA, docB;
            if (isSource)
            {
                CreateAlignedDiffDocuments(thisSideDdl, otherSideDdl, out docA, out docB);
                return docA;
            }
            else
            {
                CreateAlignedDiffDocuments(otherSideDdl, thisSideDdl, out docA, out docB);
                return docB;
            }
        }

        #region Line Helpers

        private static List<string> SplitLines(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return new List<string>(lines);
        }

        private static List<string> NormalizeLines(List<string> lines)
        {
            var result = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                while (trimmed.Contains("  "))
                    trimmed = trimmed.Replace("  ", " ");
                result.Add(trimmed);
            }
            return result;
        }

        private static bool AreSimilar(string normA, string normB)
        {
            if (string.IsNullOrWhiteSpace(normA) || string.IsNullOrWhiteSpace(normB))
                return false;

            // Fast path: short common prefix → cheap accept for typical edits
            // at the end of a line. (e.g. trailing comma added).
            int prefix = CommonPrefixLength(normA, normB);
            int minLen = Math.Min(normA.Length, normB.Length);
            if (prefix >= Math.Max(minLen * 6 / 10, 4))
                return true;

            // General case: use character-level LCS ratio. This handles edits
            // in the middle of the line (e.g. inserting [brackets] around a
            // schema-qualified name) which a prefix check cannot detect.
            int lcs = ComputeCharLcsLength(normA, normB);
            int maxLen = Math.Max(normA.Length, normB.Length);
            return maxLen > 0 && lcs * 2 >= maxLen; // ratio >= 50%
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < len && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i]))
                i++;
            return i;
        }

        /// <summary>
        /// Returns the character-level LCS length between a and b (case-insensitive).
        /// Uses rolling rows to keep memory at O(min(n,m)). For pathological line
        /// pairs, falls back to a cheap common-prefix approximation.
        /// </summary>
        private static int ComputeCharLcsLength(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            if (n == 0 || m == 0) return 0;

            if ((long)n * m > 4_000_000)
                return CommonPrefixLength(a, b);

            // Iterate over the longer string in the outer loop and keep the
            // inner DP row sized to the shorter string for smaller allocations.
            string outer = n >= m ? a : b;
            string inner = n >= m ? b : a;
            int outerLen = outer.Length;
            int innerLen = inner.Length;

            var prev = new int[innerLen + 1];
            var curr = new int[innerLen + 1];
            for (int i = 1; i <= outerLen; i++)
            {
                char co = char.ToUpperInvariant(outer[i - 1]);
                for (int j = 1; j <= innerLen; j++)
                {
                    if (co == char.ToUpperInvariant(inner[j - 1]))
                        curr[j] = prev[j - 1] + 1;
                    else
                        curr[j] = curr[j - 1] >= prev[j] ? curr[j - 1] : prev[j];
                }
                var tmp = prev; prev = curr; curr = tmp;
                Array.Clear(curr, 0, curr.Length);
            }
            return prev[innerLen];
        }

        #endregion

        #region LCS Algorithm

        /// <summary>
        /// Computes the Longest Common Subsequence of two string lists (case-insensitive).
        /// Uses standard DP algorithm — O(n*m) time and space.
        /// </summary>
        private static List<string> ComputeLcs(List<string> a, List<string> b)
        {
            int n = a.Count;
            int m = b.Count;

            // For very large inputs, use a simplified approach to avoid memory issues
            if ((long)n * m > 10_000_000)
                return ComputeLcsGreedy(a, b);

            var dp = new int[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    if (string.Equals(a[i - 1], b[j - 1], StringComparison.OrdinalIgnoreCase))
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            // Backtrack to find the LCS
            var lcs = new List<string>();
            int x = n, y = m;
            while (x > 0 && y > 0)
            {
                if (string.Equals(a[x - 1], b[y - 1], StringComparison.OrdinalIgnoreCase))
                {
                    lcs.Add(a[x - 1]);
                    x--;
                    y--;
                }
                else if (dp[x - 1, y] > dp[x, y - 1])
                    x--;
                else
                    y--;
            }

            lcs.Reverse();
            return lcs;
        }

        /// <summary>
        /// Greedy LCS for very large inputs — not optimal but avoids O(n*m) memory.
        /// </summary>
        private static List<string> ComputeLcsGreedy(List<string> a, List<string> b)
        {
            var bIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < b.Count; j++)
            {
                if (!bIndex.ContainsKey(b[j]))
                    bIndex[b[j]] = new List<int>();
                bIndex[b[j]].Add(j);
            }

            var lcs = new List<string>();
            int lastMatchJ = -1;

            foreach (var lineA in a)
            {
                if (bIndex.TryGetValue(lineA, out var positions))
                {
                    foreach (var j in positions)
                    {
                        if (j > lastMatchJ)
                        {
                            lcs.Add(lineA);
                            lastMatchJ = j;
                            break;
                        }
                    }
                }
            }

            return lcs;
        }

        #endregion

        #region Paragraph Builders

        private static FlowDocument CreateBaseDocument()
        {
            return new FlowDocument
            {
                FontFamily = MonoFont,
                FontSize = 12,
                PagePadding = new Thickness(4),
            };
        }

        /// <summary>
        /// Set to false to hide line numbers. Controlled by the Options page.
        /// </summary>
        public static bool ShowLineNumbers { get; set; } = true;

        private static Paragraph MakeNumberedParagraph(int lineNum, string text, SolidColorBrush background)
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            if (background != null)
                para.Background = background;

            if (ShowLineNumbers)
            {
                para.Inlines.Add(new Run(lineNum.ToString().PadLeft(4) + " ")
                {
                    Foreground = LineNumBrush,
                    FontWeight = FontWeights.Normal,
                });
            }

            para.Inlines.Add(new Run(text));
            return para;
        }

        private static Paragraph MakeNormalParagraph(string text)
        {
            return new Paragraph(new Run(text)) { Margin = new Thickness(0) };
        }

        private static Paragraph MakeHighlightedParagraph(string text, SolidColorBrush brush)
        {
            return new Paragraph(new Run(text))
            {
                Margin = new Thickness(0),
                Background = brush,
            };
        }

        private static Paragraph MakePaddingParagraph()
        {
            // Empty line with a gray background to show alignment gap
            return new Paragraph(new Run(" "))
            {
                Margin = new Thickness(0),
                Background = PaddingBrush,
            };
        }

        private static Paragraph MakeNumberedInlineDiff(int lineNum, string thisLine, string otherLine, bool isSource)
        {
            var para = new Paragraph { Margin = new Thickness(0), Background = ModifiedLineBrush };
            var inlineBrush = isSource ? AddedInlineBrush : RemovedInlineBrush;

            if (ShowLineNumbers)
            {
                para.Inlines.Add(new Run(lineNum.ToString().PadLeft(4) + " ")
                {
                    Foreground = LineNumBrush,
                    FontWeight = FontWeights.Normal,
                });
            }

            AddInlineDiffRuns(para, thisLine, otherLine, inlineBrush);
            return para;
        }

        private static Paragraph MakeInlineDiffParagraph(string thisLine, string otherLine, bool isSource)
        {
            var para = new Paragraph { Margin = new Thickness(0), Background = ModifiedLineBrush };
            var inlineBrush = isSource ? AddedInlineBrush : RemovedInlineBrush;
            AddInlineDiffRuns(para, thisLine, otherLine, inlineBrush);
            return para;
        }

        private static void AddInlineDiffRuns(Paragraph para, string thisLine, string otherLine, SolidColorBrush inlineBrush)
        {
            var diffMask = ComputeCharDiffMask(thisLine, otherLine);

            int runStart = 0;
            bool runIsDiff = thisLine.Length > 0 && diffMask[0];

            for (int i = 0; i <= thisLine.Length; i++)
            {
                bool isDiff = i < thisLine.Length && diffMask[i];

                if (i == thisLine.Length || isDiff != runIsDiff)
                {
                    if (i > runStart)
                    {
                        var text = thisLine.Substring(runStart, i - runStart);
                        var run = new Run(text);
                        if (runIsDiff)
                        {
                            run.Background = inlineBrush;
                            run.FontWeight = FontWeights.SemiBold;
                        }
                        para.Inlines.Add(run);
                    }
                    runStart = i;
                    runIsDiff = isDiff;
                }
            }
        }

        /// <summary>
        /// Returns a mask of length thisLine.Length where mask[i] is true if thisLine[i]
        /// is NOT part of the longest common subsequence with otherLine. Uses standard
        /// LCS DP (case-insensitive) so that insertions/deletions on either side don't
        /// throw off positional alignment — e.g. only the inserted brackets get
        /// highlighted, not every character that follows them.
        /// </summary>
        private static bool[] ComputeCharDiffMask(string thisLine, string otherLine)
        {
            int n = thisLine.Length;
            int m = otherLine.Length;
            var mask = new bool[n];
            if (n == 0) return mask;
            for (int i = 0; i < n; i++) mask[i] = true;
            if (m == 0) return mask;

            // Bound memory for pathological lines; fall back to positional compare.
            if ((long)n * m > 4_000_000)
            {
                int common = Math.Min(n, m);
                for (int i = 0; i < n; i++)
                {
                    mask[i] = i >= m
                        || char.ToUpperInvariant(thisLine[i]) != char.ToUpperInvariant(otherLine[i]);
                }
                return mask;
            }

            var dp = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++)
            {
                char ca = char.ToUpperInvariant(thisLine[i - 1]);
                for (int j = 1; j <= m; j++)
                {
                    if (ca == char.ToUpperInvariant(otherLine[j - 1]))
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            int x = n, y = m;
            while (x > 0 && y > 0)
            {
                if (char.ToUpperInvariant(thisLine[x - 1]) == char.ToUpperInvariant(otherLine[y - 1]))
                {
                    mask[x - 1] = false;
                    x--; y--;
                }
                else if (dp[x - 1, y] > dp[x, y - 1])
                    x--;
                else
                    y--;
            }

            return mask;
        }

        #endregion
    }
}
