using Microsoft.UI.Xaml.Input;
using System.Numerics;
using Windows.Foundation;

namespace Uviewer.Services
{
    /// <summary>
    /// EPUB 뷰어의 Ctrl+드래그 텍스트 선택 기능입니다.
    /// EpubTextCanvas에 그려진 블록의 레이아웃 지오메트리로 문자 위치를 계산합니다.
    /// </summary>
    internal sealed partial class EpubReaderController
    {
        private CanvasTextGeometry? _epubSelectionGeometry;
        private readonly CanvasTextSelectionState _epubSelection = new();

        internal bool TryBeginEpubTextSelection(PointerRoutedEventArgs e)
        {
            if (!_isEpubMode) return false;
            if (!CanvasTextSelectionHelper.IsControlKeyDown()) return false;
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return false;

            var point = e.GetCurrentPoint(EpubTouchOverlay);
            if (!point.Properties.IsLeftButtonPressed) return false;

            _epubSelection.Reset();
            _epubSelection.PageToken = _epubSelectionGeometry?.PageToken;
            _epubSelection.PointerId = e.Pointer.PointerId;
            _epubSelection.IsDragging = true;

            if (_epubSelectionGeometry != null &&
                _epubSelectionGeometry.TryHitTest(GetEpubCanvasPoint(point.Position), out var caret))
            {
                _epubSelection.Anchor = caret;
                _epubSelection.Focus = caret;
            }

            EpubTouchOverlay.CapturePointer(e.Pointer);
            EpubTextCanvas?.Invalidate();
            return true;
        }

        internal void EpubTouchOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_epubSelection.IsDragging || _epubSelection.PointerId != e.Pointer.PointerId) return;

            e.Handled = true;
            if (!_epubSelection.Anchor.HasValue || _epubSelectionGeometry == null) return;

            var point = e.GetCurrentPoint(EpubTouchOverlay);
            if (!point.Properties.IsLeftButtonPressed) return;

            if (!_epubSelectionGeometry.TryHitTest(GetEpubCanvasPoint(point.Position), out var caret)) return;
            if (_epubSelection.Focus.HasValue && _epubSelection.Focus.Value.Equals(caret)) return;

            _epubSelection.Focus = caret;
            EpubTextCanvas?.Invalidate();
        }

        internal void EpubTouchOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_epubSelection.IsDragging || _epubSelection.PointerId != e.Pointer.PointerId) return;

            _epubSelection.IsDragging = false;
            EpubTouchOverlay.ReleasePointerCaptures();
            e.Handled = true;

            if (!_epubSelection.HasSelection || _epubSelectionGeometry == null) return;

            string text = CanvasTextSelectionHelper.ExtractText(
                _epubSelectionGeometry.Blocks,
                _epubSelection.Anchor!.Value,
                _epubSelection.Focus!.Value);

            if (string.IsNullOrWhiteSpace(text)) return;

            if (TextSelectionClipboard.TryCopy(text))
            {
                ShowNotification(Strings.TextSelectionCopied, "\uE8C8", "Gold");
            }
        }

        private void ClearEpubSelection()
        {
            var previous = _epubSelectionGeometry;
            _epubSelectionGeometry = null;
            previous?.Dispose();
            _epubSelection.Reset();
        }

        private Vector2 GetEpubCanvasPoint(Point overlayPoint)
        {
            var canvas = EpubTextCanvas;
            if (canvas == null) return new Vector2((float)overlayPoint.X, (float)overlayPoint.Y);

            try
            {
                var transformed = EpubTouchOverlay.TransformToVisual(canvas).TransformPoint(overlayPoint);
                return new Vector2((float)transformed.X, (float)transformed.Y);
            }
            catch
            {
                return new Vector2((float)overlayPoint.X, (float)overlayPoint.Y);
            }
        }
    }
}
