using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Uviewer.Models;
using Windows.Foundation;

namespace Uviewer.Services
{
    /// <summary>일반 텍스트 모드(TextBlock)의 Ctrl+드래그 선택 상태입니다.</summary>
    internal sealed class PlainTextSelectionState
    {
        public bool IsDragging { get; set; }
        public uint PointerId { get; set; }
        public int Generation { get; set; } = -1;
        public int AnchorLine { get; set; } = -1;
        public int AnchorChar { get; set; }
        public int FocusLine { get; set; } = -1;
        public int FocusChar { get; set; }

        public bool HasAnchor => AnchorLine >= 0 && FocusLine >= 0;

        public void Reset()
        {
            IsDragging = false;
            PointerId = 0;
            Generation = -1;
            AnchorLine = -1;
            AnchorChar = 0;
            FocusLine = -1;
            FocusChar = 0;
        }

        /// <summary>앵커/포커스를 문서 순서(위→아래, 왼쪽→오른쪽)로 정렬합니다.</summary>
        public (int firstLine, int firstChar, int lastLine, int lastChar) GetOrderedPoints()
        {
            bool forward = AnchorLine < FocusLine ||
                           (AnchorLine == FocusLine && AnchorChar <= FocusChar);

            return forward
                ? (AnchorLine, AnchorChar, FocusLine, FocusChar)
                : (FocusLine, FocusChar, AnchorLine, AnchorChar);
        }
    }

    internal static class PlainTextSelectionHitTester
    {
        /// <summary>TextBlock에 표시되는 텍스트(**, __ 강조 마커 제거)를 만듭니다.</summary>
        public static string GetDisplayText(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            if (!content.Contains("**")) return content;

            var sb = new StringBuilder(content.Length);
            var parts = Regex.Split(content, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.Length >= 4 &&
                    part.StartsWith("**", StringComparison.Ordinal) &&
                    part.EndsWith("**", StringComparison.Ordinal))
                {
                    sb.Append(part, 2, part.Length - 4);
                }
                else
                {
                    sb.Append(part);
                }
            }

            return sb.ToString();
        }

        /// <summary>포인터 위치(TextItemsRepeater 기준)를 (줄 인덱스, 문자 인덱스)로 변환합니다.</summary>
        public static bool TryHitTest(
            ItemsRepeater? repeater,
            IReadOnlyList<TextLine> lines,
            Point position,
            Func<string, Windows.UI.Text.FontWeight> getFontWeight,
            out int lineIndex,
            out int charIndex)
        {
            lineIndex = -1;
            charIndex = 0;

            if (repeater == null || lines.Count == 0) return false;

            int childCount = VisualTreeHelper.GetChildrenCount(repeater);
            if (childCount == 0) return false;

            TextBlock? bestBlock = null;
            int bestIndex = -1;
            Rect bestBounds = default;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(repeater, i) is not TextBlock textBlock) continue;

                int index = repeater.GetElementIndex(textBlock);
                if (index < 0 || index >= lines.Count) continue;

                var bounds = GetBoundsInRepeater(textBlock, repeater);
                double distance = DistanceToRect(position, bounds);
                if (distance <= 0)
                {
                    bestBlock = textBlock;
                    bestIndex = index;
                    bestBounds = bounds;
                    break;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestBlock = textBlock;
                    bestIndex = index;
                    bestBounds = bounds;
                }
            }

            if (bestBlock == null) return false;

            lineIndex = bestIndex;
            charIndex = ResolveCharacterIndex(bestBlock, lines[bestIndex], position, bestBounds, getFontWeight);
            return true;
        }

        public static string ExtractText(IReadOnlyList<TextLine> lines, PlainTextSelectionState state)
        {
            if (!state.HasAnchor) return string.Empty;

            var (firstLine, firstChar, lastLine, lastChar) = state.GetOrderedPoints();
            var sb = new StringBuilder();

            for (int i = Math.Max(0, firstLine); i <= lastLine && i < lines.Count; i++)
            {
                var displayText = GetDisplayText(lines[i].Content);
                if (displayText.Length == 0) continue;

                int from = i == firstLine ? Math.Clamp(firstChar, 0, displayText.Length - 1) : 0;
                int to = i == lastLine ? Math.Clamp(lastChar, 0, displayText.Length - 1) : displayText.Length - 1;
                if (to < from) (from, to) = (to, from);

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(displayText, from, to - from + 1);
            }

            return sb.ToString();
        }

        private static int ResolveCharacterIndex(
            TextBlock textBlock,
            TextLine line,
            Point position,
            Rect bounds,
            Func<string, Windows.UI.Text.FontWeight> getFontWeight)
        {
            var displayText = GetDisplayText(line.Content);
            if (displayText.Length == 0) return 0;

            double localX = position.X - bounds.X - textBlock.Padding.Left;
            double localY = position.Y - bounds.Y - textBlock.Padding.Top;

            double lineHeight = textBlock.LineHeight > 0
                ? textBlock.LineHeight
                : Math.Ceiling(line.FontSize * 1.8);
            int rowHint = (int)Math.Floor(Math.Max(0, localY) / Math.Max(1, lineHeight));

            double wrapWidth = textBlock.ActualWidth - textBlock.Padding.Left - textBlock.Padding.Right;
            if (wrapWidth <= 1) wrapWidth = line.MaxWidth > 1 ? line.MaxWidth : 400;

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                using var format = new CanvasTextFormat
                {
                    FontSize = (float)line.FontSize,
                    FontFamily = line.FontFamily,
                    FontWeight = getFontWeight(line.FontFamily),
                    WordWrapping = CanvasWordWrapping.Wrap,
                    VerticalAlignment = CanvasVerticalAlignment.Top,
                    HorizontalAlignment = ToCanvasAlignment(line.TextAlignment)
                };

                using var layout = new CanvasTextLayout(device, displayText, format, (float)wrapWidth, 0.0f);
                if (layout.LineCount <= 0) return 0;

                int row = Math.Clamp(rowHint, 0, layout.LineCount - 1);
                int rowStart = 0;
                int rowEnd = displayText.Length;
                int cursor = 0;
                int currentRow = 0;

                foreach (var metric in layout.LineMetrics)
                {
                    int count = Math.Max(0, metric.CharacterCount);
                    if (currentRow == row)
                    {
                        rowStart = cursor;
                        rowEnd = cursor + count;
                        break;
                    }

                    cursor += count;
                    currentRow++;
                }

                rowStart = Math.Clamp(rowStart, 0, Math.Max(0, displayText.Length - 1));
                rowEnd = Math.Clamp(rowEnd, rowStart + 1, displayText.Length);

                int lo = rowStart;
                int hi = Math.Max(rowStart, rowEnd - 1);
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    double centerX = double.MaxValue;
                    var regions = layout.GetCharacterRegions(mid, 1);
                    if (regions.Length > 0)
                    {
                        var regionBounds = regions[0].LayoutBounds;
                        centerX = regionBounds.X + (regionBounds.Width / 2);
                    }

                    if (localX < centerX) hi = mid;
                    else lo = mid + 1;
                }

                return Math.Clamp(lo, 0, displayText.Length - 1);
            }
            catch
            {
                return 0;
            }
        }

        private static Rect GetBoundsInRepeater(FrameworkElement element, UIElement repeater)
        {
            try
            {
                var origin = element.TransformToVisual(repeater).TransformPoint(new Point(0, 0));
                return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
            }
            catch
            {
                return new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            }
        }

        private static double DistanceToRect(Point point, Rect bounds)
        {
            double dx = 0;
            double dy = 0;

            if (point.X < bounds.Left) dx = bounds.Left - point.X;
            else if (point.X > bounds.Right) dx = point.X - bounds.Right;

            if (point.Y < bounds.Top) dy = bounds.Top - point.Y;
            else if (point.Y > bounds.Bottom) dy = point.Y - bounds.Bottom;

            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static CanvasHorizontalAlignment ToCanvasAlignment(TextAlignment alignment) => alignment switch
        {
            TextAlignment.Center => CanvasHorizontalAlignment.Center,
            TextAlignment.Right => CanvasHorizontalAlignment.Right,
            _ => CanvasHorizontalAlignment.Left
        };
    }

    /// <summary>실현된 TextBlock에 선택 하이라이트를 적용/갱신합니다.</summary>
    internal sealed class PlainTextSelectionPresenter
    {
        private static readonly SolidColorBrush SelectionBrush = new(TextSelectionVisuals.SelectionColor);

        private readonly Dictionary<TextBlock, TextHighlighter> _highlighters = new();

        public void Refresh(ItemsRepeater? repeater, IReadOnlyList<TextLine> lines, PlainTextSelectionState state)
        {
            if (repeater == null) return;

            int childCount = VisualTreeHelper.GetChildrenCount(repeater);
            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(repeater, i) is not TextBlock textBlock) continue;

                int index = repeater.GetElementIndex(textBlock);
                if (index < 0 || index >= lines.Count) continue;

                ApplyToElement(textBlock, index, lines[index], state);
            }
        }

        public void ApplyToElement(TextBlock textBlock, int lineIndex, TextLine line, PlainTextSelectionState state)
        {
            var displayText = PlainTextSelectionHitTester.GetDisplayText(line.Content);
            var range = GetRangeForLine(lineIndex, displayText.Length, state);

            if (range.HasValue && displayText.Length > 0)
            {
                var highlighter = new TextHighlighter { Background = SelectionBrush };
                highlighter.Ranges.Add(new TextRange
                {
                    StartIndex = range.Value.start,
                    Length = range.Value.length
                });

                ReplaceHighlighter(textBlock, highlighter);
            }
            else
            {
                RemoveHighlighter(textBlock);
            }
        }

        /// <summary>가상화로 요소가 재사용될 때 이전 하이라이트 추적을 제거합니다.</summary>
        public void ForgetElement(TextBlock textBlock)
        {
            _highlighters.Remove(textBlock);
        }

        public void Clear()
        {
            foreach (var pair in _highlighters)
            {
                try { pair.Key.TextHighlighters.Remove(pair.Value); } catch { }
            }

            _highlighters.Clear();
        }

        private void ReplaceHighlighter(TextBlock textBlock, TextHighlighter highlighter)
        {
            if (_highlighters.TryGetValue(textBlock, out var existing))
            {
                textBlock.TextHighlighters.Remove(existing);
            }

            _highlighters[textBlock] = highlighter;
            textBlock.TextHighlighters.Add(highlighter);
        }

        private void RemoveHighlighter(TextBlock textBlock)
        {
            if (_highlighters.TryGetValue(textBlock, out var existing))
            {
                textBlock.TextHighlighters.Remove(existing);
                _highlighters.Remove(textBlock);
            }
        }

        private static (int start, int length)? GetRangeForLine(int lineIndex, int lineLength, PlainTextSelectionState state)
        {
            if (!state.HasAnchor || lineLength <= 0) return null;

            var (firstLine, firstChar, lastLine, lastChar) = state.GetOrderedPoints();
            if (lineIndex < firstLine || lineIndex > lastLine) return null;

            int from = lineIndex == firstLine ? Math.Clamp(firstChar, 0, lineLength - 1) : 0;
            int to = lineIndex == lastLine ? Math.Clamp(lastChar, 0, lineLength - 1) : lineLength - 1;
            if (to < from) (from, to) = (to, from);

            return (from, to - from + 1);
        }
    }
}
