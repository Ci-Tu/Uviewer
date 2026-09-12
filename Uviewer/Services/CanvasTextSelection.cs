using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace Uviewer.Services
{
    /// <summary>캔버스에 그려진 텍스트 블록 안의 캐럿 위치(블록 인덱스 + 문자 인덱스)입니다.</summary>
    internal readonly record struct CanvasTextCaret(int BlockIndex, int CharIndex);

    /// <summary>블록 하나에 대한 선택 범위입니다.</summary>
    internal readonly record struct CanvasTextRange(int BlockIndex, int Start, int Length);

    /// <summary>한 번의 렌더 패스에서 그려진 블록 하나의 레이아웃 정보입니다.</summary>
    internal sealed class CanvasTextBlockGeometry
    {
        public CanvasTextBlockGeometry(
            int blockIndex,
            string text,
            float drawX,
            float drawY,
            float fontSize,
            CanvasTextLayout layout)
        {
            BlockIndex = blockIndex;
            Text = text;
            DrawX = drawX;
            DrawY = drawY;
            FontSize = fontSize;
            Layout = layout;
        }

        public int BlockIndex { get; }
        public string Text { get; }
        public float DrawX { get; }
        public float DrawY { get; }
        public float FontSize { get; }
        public CanvasTextLayout Layout { get; }
    }

    /// <summary>
    /// 한 번의 렌더 패스에서 그려진 블록들의 레이아웃을 보관합니다.
    /// Ctrl+드래그 선택 시 포인터 위치를 문자 위치로 변환하는 데 사용됩니다.
    /// </summary>
    internal sealed class CanvasTextGeometry : IDisposable
    {
        private readonly List<CanvasTextBlockGeometry> _blocks = new();
        private bool _disposed;

        public CanvasTextGeometry(object pageToken)
        {
            PageToken = pageToken;
        }

        /// <summary>현재 페이지를 식별하는 토큰(보통 페이지 블록 리스트 참조)입니다.</summary>
        public object PageToken { get; }

        public IReadOnlyList<CanvasTextBlockGeometry> Blocks => _blocks;

        public void AddBlock(int blockIndex, string text, float drawX, float drawY, float fontSize, CanvasTextLayout layout)
        {
            if (_disposed) return;
            _blocks.Add(new CanvasTextBlockGeometry(blockIndex, text, drawX, drawY, fontSize, layout));
        }

        public bool TryHitTest(Vector2 point, out CanvasTextCaret caret)
        {
            caret = default;
            if (_disposed || _blocks.Count == 0) return false;

            try
            {
                CanvasTextBlockGeometry? best = null;
                double bestDistance = double.MaxValue;

                foreach (var block in _blocks)
                {
                    var bounds = block.Layout.LayoutBounds;
                    var local = new Vector2(point.X - block.DrawX, point.Y - block.DrawY);
                    double distance = DistanceToRect(local, bounds);
                    if (distance <= 0)
                    {
                        best = block;
                        bestDistance = 0;
                        break;
                    }

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = block;
                    }
                }

                if (best == null) return false;

                caret = new CanvasTextCaret(best.BlockIndex, ResolveCharacterIndex(best, point));
                return true;
            }
            catch
            {
                // 장치 분실 등으로 레이아웃을 더 이상 사용할 수 없으면 선택을 시작하지 않습니다.
                return false;
            }
        }

        private static int ResolveCharacterIndex(CanvasTextBlockGeometry block, Vector2 point)
        {
            int length = block.Text.Length;
            if (length <= 0) return 0;

            var bounds = block.Layout.LayoutBounds;
            float x = (float)Math.Clamp(
                point.X - block.DrawX,
                bounds.Left + 0.5,
                Math.Max(bounds.Left + 0.5, bounds.Right - 0.5));
            float y = (float)Math.Clamp(
                point.Y - block.DrawY,
                bounds.Top + 0.5,
                Math.Max(bounds.Top + 0.5, bounds.Bottom - 0.5));

            try
            {
                if (block.Layout.HitTest(new Vector2(x, y), out var region, out bool trailingSide))
                {
                    int index = region.CharacterIndex + (trailingSide ? Math.Max(0, region.CharacterCount - 1) : 0);
                    return Math.Clamp(index, 0, length - 1);
                }
            }
            catch
            {
                // 장치 분실 등으로 레이아웃을 사용할 수 없으면 아래 폴백을 사용합니다.
            }

            return y <= bounds.Top + (bounds.Height / 2) ? 0 : length - 1;
        }

        private static double DistanceToRect(Vector2 point, Rect bounds)
        {
            double dx = 0;
            double dy = 0;

            if (point.X < bounds.Left) dx = bounds.Left - point.X;
            else if (point.X > bounds.Right) dx = point.X - bounds.Right;

            if (point.Y < bounds.Top) dy = bounds.Top - point.Y;
            else if (point.Y > bounds.Bottom) dy = point.Y - bounds.Bottom;

            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var block in _blocks)
            {
                try { block.Layout.Dispose(); } catch { }
            }

            _blocks.Clear();
        }
    }

    /// <summary>캔버스 텍스트 선택 상태(앵커/포커스)입니다.</summary>
    internal sealed class CanvasTextSelectionState
    {
        public CanvasTextCaret? Anchor { get; set; }
        public CanvasTextCaret? Focus { get; set; }
        public bool IsDragging { get; set; }
        public uint PointerId { get; set; }
        public object? PageToken { get; set; }
        public bool HasSelection => Anchor.HasValue && Focus.HasValue;

        public void Reset()
        {
            Anchor = null;
            Focus = null;
            IsDragging = false;
            PointerId = 0;
            PageToken = null;
        }
    }

    internal static class CanvasTextSelectionHelper
    {
        /// <summary>현재 Ctrl 키가 눌려 있는지 확인합니다.</summary>
        public static bool IsControlKeyDown() =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        /// <summary>그리기용 선택 범위를 계산합니다. 페이지가 바뀌었으면 null을 반환합니다.</summary>
        public static IReadOnlyList<CanvasTextRange>? BuildRangesForDraw(
            CanvasTextSelectionState state,
            CanvasTextGeometry? geometry,
            object pageToken)
        {
            if (!state.HasSelection || geometry == null || geometry.Blocks.Count == 0) return null;
            if (!ReferenceEquals(state.PageToken, pageToken)) return null;

            var ranges = BuildRanges(geometry.Blocks, state.Anchor!.Value, state.Focus!.Value);
            return ranges.Count > 0 ? ranges : null;
        }

        /// <summary>새 렌더 패스의 지오메트리로 교체합니다. 페이지가 바뀌면 선택을 초기화합니다.</summary>
        public static void ApplyGeometry(
            ref CanvasTextGeometry? slot,
            CanvasTextGeometry geometry,
            CanvasTextSelectionState state,
            object pageToken)
        {
            if (state.HasSelection && !ReferenceEquals(state.PageToken, pageToken))
            {
                state.Reset();
            }

            var previous = slot;
            slot = geometry;
            previous?.Dispose();
        }

        public static List<CanvasTextRange> BuildRanges(
            IReadOnlyList<CanvasTextBlockGeometry> blocks,
            CanvasTextCaret anchor,
            CanvasTextCaret focus)
        {
            var ranges = new List<CanvasTextRange>();
            int firstListIndex = IndexOfBlock(blocks, anchor.BlockIndex);
            int secondListIndex = IndexOfBlock(blocks, focus.BlockIndex);
            if (firstListIndex < 0 || secondListIndex < 0) return ranges;

            bool reversed = firstListIndex > secondListIndex;
            if (reversed)
            {
                (firstListIndex, secondListIndex) = (secondListIndex, firstListIndex);
            }

            var startCaret = reversed ? focus : anchor;
            var endCaret = reversed ? anchor : focus;

            for (int i = firstListIndex; i <= secondListIndex; i++)
            {
                var block = blocks[i];
                int length = block.Text.Length;
                if (length <= 0) continue;

                int from = 0;
                int to = length - 1;
                if (i == firstListIndex) from = Math.Clamp(startCaret.CharIndex, 0, length - 1);
                if (i == secondListIndex) to = Math.Clamp(endCaret.CharIndex, 0, length - 1);
                if (to < from) (from, to) = (to, from);

                ranges.Add(new CanvasTextRange(block.BlockIndex, from, to - from + 1));
            }

            return ranges;
        }

        public static string ExtractText(
            IReadOnlyList<CanvasTextBlockGeometry> blocks,
            CanvasTextCaret anchor,
            CanvasTextCaret focus)
        {
            var sb = new StringBuilder();

            foreach (var range in BuildRanges(blocks, anchor, focus))
            {
                var block = FindBlock(blocks, range.BlockIndex);
                if (block == null || range.Length <= 0) continue;

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(block.Text, range.Start, range.Length);
            }

            return sb.ToString();
        }

        private static int IndexOfBlock(IReadOnlyList<CanvasTextBlockGeometry> blocks, int blockIndex)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].BlockIndex == blockIndex) return i;
            }

            return -1;
        }

        private static CanvasTextBlockGeometry? FindBlock(IReadOnlyList<CanvasTextBlockGeometry> blocks, int blockIndex)
        {
            int index = IndexOfBlock(blocks, blockIndex);
            return index < 0 ? null : blocks[index];
        }
    }

    /// <summary>텍스트 선택 하이라이트 색상입니다.</summary>
    internal static class TextSelectionVisuals
    {
        public static readonly Color SelectionColor = ColorHelper.FromArgb(112, 0, 120, 215);
    }

    /// <summary>선택한 텍스트를 클립보드로 복사합니다.</summary>
    internal static class TextSelectionClipboard
    {
        public static bool TryCopy(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                var dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);

                try { Clipboard.Flush(); } catch { }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard copy failed: {ex.Message}");
                return false;
            }
        }
    }
}
