using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Content;

namespace Uviewer.Services
{
    /// <summary>
    /// PDF 페이지 문자의 컬럼/줄 배치와 읽기 순서를 담습니다.
    /// 세로로 컬럼이 나뉜 문서에서 드래그한 컬럼 안의 텍스트만 선택되도록 하는 데 사용됩니다.
    /// </summary>
    internal sealed class PdfSelectionLayout
    {
        public static readonly PdfSelectionLayout Empty = new();

        /// <summary>문자 맵 인덱스 → 읽기 순서 위치 (-1이면 읽기 순서에 포함되지 않음).</summary>
        public int[] OrderPosition { get; init; } = Array.Empty<int>();

        /// <summary>읽기 순서 위치 → 문자 맵 인덱스.</summary>
        public int[] ReadingOrder { get; init; } = Array.Empty<int>();

        /// <summary>문자 맵 인덱스 → 줄 인덱스 (위에서 아래로 증가, -1이면 없음).</summary>
        public int[] LineIndex { get; init; } = Array.Empty<int>();

        /// <summary>문자 맵 인덱스 → 컬럼 인덱스 (왼쪽에서 오른쪽으로 증가, -1이면 없음).</summary>
        public int[] ColumnIndex { get; init; } = Array.Empty<int>();

        /// <summary>페이지에서 감지된 컬럼 수(최소 1).</summary>
        public int ColumnCount { get; init; } = 1;

        public bool HasReadingOrder => ReadingOrder.Length > 0;
    }

    /// <summary>
    /// PDF 문자 맵에서 컬럼(세로 단) 구조를 감지하고 컬럼 → 줄 → X 순서의 읽기 순서를 계산합니다.
    /// 컬럼이 감지되지 않으면 기존과 동일한 줄 순서를 사용합니다.
    /// </summary>
    internal static class PdfSelectionLayoutBuilder
    {
        /// <summary>페이지 폭 대비 컬럼 사이 최소 간격 비율입니다.</summary>
        private const double MinColumnGapRatio = 0.015;

        /// <summary>이 비율보다 넓은 줄 조각은 전폭 요소(제목 등)로 보고 컬럼 판정에서 제외합니다.</summary>
        private const double MaxColumnSegmentWidthRatio = 0.55;

        /// <summary>같은 컬럼으로 묶기 위한 최소 가로 겹침 비율입니다.</summary>
        private const double ClusterOverlapRatio = 0.5;

        public static PdfSelectionLayout Build(List<Letter?> letters, double pageWidth)
        {
            if (letters.Count == 0) return PdfSelectionLayout.Empty;

            var distinct = new List<Letter>();
            var seen = new HashSet<Letter>(ReferenceEqualityComparer.Instance);
            foreach (var letter in letters)
            {
                if (letter == null) continue;
                if (seen.Add(letter)) distinct.Add(letter);
            }

            if (distinct.Count == 0) return PdfSelectionLayout.Empty;

            var lines = SearchHighlightService.GroupLettersByLine(distinct);
            var lineOf = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < lines.Count; i++)
            {
                foreach (var letter in lines[i])
                {
                    lineOf[letter] = i;
                }
            }

            // 컬럼 후보를 찾고, 각 줄 조각(segment)에 컬럼을 배정합니다.
            var columns = DetectColumns(lines, pageWidth, out var lineSegments);

            var orderPosition = CreateFilledArray(letters.Count, -1);
            var lineIndices = CreateFilledArray(letters.Count, -1);
            var columnIndices = CreateFilledArray(letters.Count, -1);

            var order = new List<int>(letters.Count);
            for (int index = 0; index < letters.Count; index++)
            {
                var letter = letters[index];
                if (letter == null) continue;

                int lineIndex = lineOf.TryGetValue(letter, out int line) ? line : 0;
                lineIndices[index] = lineIndex;
                columnIndices[index] = ResolveColumn(columns, lineSegments, lineIndex, letter);
                order.Add(index);
            }

            order.Sort((a, b) =>
            {
                int compare = columnIndices[a].CompareTo(columnIndices[b]);
                if (compare != 0) return compare;

                compare = lineIndices[a].CompareTo(lineIndices[b]);
                if (compare != 0) return compare;

                compare = StartX(letters[a]!).CompareTo(StartX(letters[b]!));
                if (compare != 0) return compare;

                return a.CompareTo(b);
            });

            for (int position = 0; position < order.Count; position++)
            {
                orderPosition[order[position]] = position;
            }

            return new PdfSelectionLayout
            {
                OrderPosition = orderPosition,
                ReadingOrder = order.ToArray(),
                LineIndex = lineIndices,
                ColumnIndex = columnIndices,
                ColumnCount = columns.Count
            };
        }

        /// <summary>
        /// 페이지의 줄 조각(가로로 띄어진 텍스트 덩어리)을 모아 세로 컬럼 영역을 추정하고,
        /// 각 줄 조각에 컬럼 인덱스를 배정합니다. 컬럼이 2개 미만이면 전체를 하나의 컬럼으로 봅니다.
        /// </summary>
        private static List<(double Left, double Right)> DetectColumns(
            IReadOnlyList<IReadOnlyList<Letter>> lines,
            double pageWidth,
            out List<List<(double Left, double Right, int Column)>> lineSegments)
        {
            double gapThreshold = CalculateSegmentGapThreshold(lines);

            var rawSegments = new List<List<(double Left, double Right)>>(lines.Count);
            foreach (var line in lines)
            {
                rawSegments.Add(BuildLineSegments(line, gapThreshold));
            }

            var columns = DetermineColumns(lines, rawSegments, pageWidth);

            lineSegments = new List<List<(double Left, double Right, int Column)>>(lines.Count);
            foreach (var segments in rawSegments)
            {
                var withColumns = new List<(double Left, double Right, int Column)>(segments.Count);
                foreach (var segment in segments)
                {
                    int column = FindNearestColumn(columns, (segment.Left + segment.Right) / 2);
                    withColumns.Add((segment.Left, segment.Right, column));
                }

                lineSegments.Add(withColumns);
            }

            return columns;
        }

        private static List<(double Left, double Right)> DetermineColumns(
            IReadOnlyList<IReadOnlyList<Letter>> lines,
            List<List<(double Left, double Right)>> rawSegments,
            double pageWidth)
        {
            var singleColumn = new List<(double Left, double Right)> { (0.0, Math.Max(1.0, pageWidth)) };
            if (lines.Count < 2 || pageWidth <= 1.0) return singleColumn;

            double minColumnGap = Math.Max(6.0, pageWidth * MinColumnGapRatio);
            double maxSegmentWidth = pageWidth * MaxColumnSegmentWidthRatio;
            int minClusterSegments = Math.Max(3, (int)Math.Round(lines.Count * 0.12));

            var clusters = new List<ColumnCluster>();

            for (int lineIndex = 0; lineIndex < rawSegments.Count; lineIndex++)
            {
                foreach (var segment in rawSegments[lineIndex])
                {
                    // 전폭에 가까운 줄 조각(제목, 전체 폭 초록 등)은 컬럼 경계 판정에서 제외합니다.
                    if (segment.Right - segment.Left > maxSegmentWidth) continue;

                    ColumnCluster? best = null;
                    double bestRatio = 0;

                    foreach (var cluster in clusters)
                    {
                        double ratio = OverlapRatio(cluster.Left, cluster.Right, segment.Left, segment.Right);
                        if (ratio >= ClusterOverlapRatio && ratio > bestRatio)
                        {
                            best = cluster;
                            bestRatio = ratio;
                        }
                    }

                    if (best == null)
                    {
                        clusters.Add(new ColumnCluster(segment.Left, segment.Right));
                    }
                    else
                    {
                        best.Add(segment.Left, segment.Right);
                    }
                }
            }

            var merged = new List<ColumnCluster>();
            foreach (var cluster in clusters
                .Where(cluster => cluster.SegmentCount >= minClusterSegments)
                .OrderBy(cluster => cluster.Left))
            {
                if (merged.Count > 0 && cluster.Left - merged[^1].Right < minColumnGap)
                {
                    merged[^1].Add(cluster.Left, cluster.Right, cluster.SegmentCount);
                    continue;
                }

                merged.Add(cluster);
            }

            if (merged.Count < 2) return singleColumn;

            return merged
                .Select(cluster => (cluster.Left, cluster.Right))
                .ToList();
        }

        /// <summary>
        /// 한 줄의 문자들을 가로 공백 기준으로 조각내어 좌우 컬럼 후보를 분리합니다.
        /// </summary>
        private static List<(double Left, double Right)> BuildLineSegments(
            IReadOnlyList<Letter> line,
            double gapThreshold)
        {
            var segments = new List<(double Left, double Right)>();
            if (line.Count == 0) return segments;

            var spans = line
                .Select(letter => (Left: LetterLeft(letter), Right: LetterRight(letter)))
                .Where(span => span.Right > span.Left)
                .OrderBy(span => span.Left)
                .ToList();

            if (spans.Count == 0) return segments;

            double left = spans[0].Left;
            double right = spans[0].Right;

            for (int i = 1; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.Left - right > gapThreshold)
                {
                    segments.Add((left, right));
                    left = span.Left;
                    right = span.Right;
                }
                else
                {
                    right = Math.Max(right, span.Right);
                }
            }

            segments.Add((left, right));
            return segments;
        }

        /// <summary>단어 사이 공백과 컬럼 거터를 구분하기 위한 최소 가로 간격입니다.</summary>
        private static double CalculateSegmentGapThreshold(IReadOnlyList<IReadOnlyList<Letter>> lines)
        {
            var heights = new List<double>();
            foreach (var line in lines)
            {
                foreach (var letter in line)
                {
                    double height = letter.GlyphRectangleLoose.Height;
                    if (height > 0.1 && height < 200) heights.Add(height);
                }
            }

            if (heights.Count == 0) return 9.0;

            heights.Sort();
            double medianHeight = heights[heights.Count / 2];

            return Math.Clamp(medianHeight * 0.8, 5.0, 24.0);
        }

        private static double OverlapRatio(double leftA, double rightA, double leftB, double rightB)
        {
            double overlap = Math.Min(rightA, rightB) - Math.Max(leftA, leftB);
            if (overlap <= 0) return 0;

            double minWidth = Math.Min(rightA - leftA, rightB - leftB);
            if (minWidth <= 0) return 0;

            return overlap / minWidth;
        }

        private static int FindNearestColumn(IReadOnlyList<(double Left, double Right)> columns, double x)
        {
            int bestIndex = 0;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < columns.Count; i++)
            {
                double distance = x < columns[i].Left
                    ? columns[i].Left - x
                    : x > columns[i].Right ? x - columns[i].Right : 0;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// 문자 하나가 속한 줄 조각의 컬럼을 돌려줍니다.
        /// 줄 조각을 통째로 같은 컬럼으로 묶어, 전폭에 가까운 본문 줄이 컬럼 경계에서 잘리지 않게 합니다.
        /// </summary>
        private static int ResolveColumn(
            IReadOnlyList<(double Left, double Right)> columns,
            IReadOnlyList<List<(double Left, double Right, int Column)>> lineSegments,
            int lineIndex,
            Letter letter)
        {
            double center = CenterX(letter);

            if (lineIndex >= 0 && lineIndex < lineSegments.Count)
            {
                var segments = lineSegments[lineIndex];

                foreach (var segment in segments)
                {
                    if (center >= segment.Left && center <= segment.Right) return segment.Column;
                }

                int bestColumn = -1;
                double bestDistance = double.MaxValue;
                foreach (var segment in segments)
                {
                    double distance = center < segment.Left
                        ? segment.Left - center
                        : center > segment.Right ? center - segment.Right : 0;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestColumn = segment.Column;
                    }
                }

                if (bestColumn >= 0) return bestColumn;
            }

            return FindNearestColumn(columns, center);
        }

        private static double CenterX(Letter letter)
        {
            double left = LetterLeft(letter);
            double right = LetterRight(letter);
            return right > left ? (left + right) / 2 : letter.StartBaseLine.X;
        }

        private static double StartX(Letter letter) => letter.StartBaseLine.X;

        private static double LetterLeft(Letter letter)
        {
            var rect = letter.GlyphRectangleLoose;
            if (rect.Right - rect.Left >= 0.1) return rect.Left;

            return Math.Min(letter.StartBaseLine.X, letter.EndBaseLine.X);
        }

        private static double LetterRight(Letter letter)
        {
            var rect = letter.GlyphRectangleLoose;
            if (rect.Right - rect.Left >= 0.1) return rect.Right;

            return Math.Max(letter.StartBaseLine.X, letter.EndBaseLine.X);
        }

        private static int[] CreateFilledArray(int length, int value)
        {
            var array = new int[length];
            Array.Fill(array, value);
            return array;
        }

        private sealed class ColumnCluster
        {
            public ColumnCluster(double left, double right)
            {
                Left = left;
                Right = right;
                SegmentCount = 1;
            }

            public double Left { get; private set; }
            public double Right { get; private set; }
            public int SegmentCount { get; private set; }

            public void Add(double left, double right, int segmentCount = 1)
            {
                Left = Math.Min(Left, left);
                Right = Math.Max(Right, right);
                SegmentCount += segmentCount;
            }
        }
    }
}
