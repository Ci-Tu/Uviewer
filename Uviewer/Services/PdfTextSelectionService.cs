using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UglyToad.PdfPig.Content;
using Windows.Foundation;

namespace Uviewer.Services
{
    /// <summary>PDF 페이지 하나의 텍스트 레이어(PdfPig) 정보입니다.</summary>
    internal sealed class PdfTextSelectionMap
    {
        public string Text { get; init; } = string.Empty;
        public List<Letter?> Letters { get; init; } = new();
        public double PageWidth { get; init; }
        public double PageHeight { get; init; }
    }

    /// <summary>
    /// PDF 뷰어에서 Ctrl+드래그로 텍스트를 선택하고 클립보드로 복사하는 기능을 담당합니다.
    /// PdfPig의 문자(glyph) 좌표를 사용해 PDF 좌표계와 캔버스 좌표계를 상호 변환합니다.
    /// </summary>
    internal sealed class PdfTextSelectionService
    {
        private readonly IImageInputHost _host;
        private readonly List<PdfSearchHighlight> _highlights = new();

        private PdfTextSelectionMap? _map;
        private string? _mapPath;
        private string? _mapPassword;
        private int _mapPageIndex = -1;
        private int _mapGeneration;

        private Point _startPoint;

        public PdfTextSelectionService(IImageInputHost host)
        {
            _host = host;
        }

        public bool IsDragging { get; private set; }
        public bool HasSelection { get; private set; }
        public int SelectionPageIndex { get; private set; } = -1;
        public int AnchorIndex { get; private set; } = -1;
        public int FocusIndex { get; private set; } = -1;

        public IReadOnlyList<PdfSearchHighlight> Highlights => _highlights;

        public bool CanSelect =>
            _host.IsPdfMode &&
            !string.IsNullOrEmpty(_host.CurrentPdfPath) &&
            _host.CurrentBitmap != null;

        /// <summary>Ctrl+드래그 시작. 텍스트 맵 적재는 비동기로 진행됩니다.</summary>
        public void BeginSelection(Point canvasPoint)
        {
            if (!CanSelect) return;

            IsDragging = true;
            HasSelection = false;
            AnchorIndex = -1;
            FocusIndex = -1;
            _highlights.Clear();
            _startPoint = canvasPoint;
            _ = BeginSelectionCoreAsync();
        }

        private async Task BeginSelectionCoreAsync()
        {
            try
            {
                string? path = _host.CurrentPdfPath;
                if (string.IsNullOrEmpty(path)) return;

                int pageIndex = _host.CurrentPdfPageIndex;
                var map = await EnsureMapAsync(path, pageIndex);
                if (map == null || !IsDragging) return;
                if (pageIndex != _host.CurrentPdfPageIndex) return;
                if (!TryMapPointToPdf(_startPoint, map, out double x, out double y)) return;

                int index = FindCharacterIndex(map, x, y);
                if (index < 0) return;

                AnchorIndex = index;
                FocusIndex = index;
                HasSelection = true;
                SelectionPageIndex = pageIndex;
                UpdateHighlights(map);
                _host.MainCanvas?.Invalidate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PDF text selection begin failed: {ex.Message}");
            }
        }

        /// <summary>드래그 중 포커스(끝점) 갱신.</summary>
        public void Update(Point canvasPoint)
        {
            if (!IsDragging) return;

            var map = _map;
            if (map == null || !HasSelection) return;
            if (SelectionPageIndex != _host.CurrentPdfPageIndex) return;
            if (!TryMapPointToPdf(canvasPoint, map, out double x, out double y)) return;

            int index = FindCharacterIndex(map, x, y);
            if (index < 0 || index == FocusIndex) return;

            FocusIndex = index;
            UpdateHighlights(map);
            _host.MainCanvas?.Invalidate();
        }

        /// <summary>드래그 종료. 선택된 텍스트를 클립보드로 복사하고 하이라이트는 유지합니다.</summary>
        public void End()
        {
            if (!IsDragging) return;
            IsDragging = false;
            if (!HasSelection) return;

            string text = ExtractSelectedText();
            if (string.IsNullOrWhiteSpace(text)) return;

            if (TextSelectionClipboard.TryCopy(text))
            {
                _host.ShowNotification(Strings.TextSelectionCopied, "\uE8C8", "Gold");
            }
        }

        public void Cancel()
        {
            IsDragging = false;
            HasSelection = false;
            AnchorIndex = -1;
            FocusIndex = -1;
            _highlights.Clear();
            _host.MainCanvas?.Invalidate();
        }

        private async Task<PdfTextSelectionMap?> EnsureMapAsync(string path, int pageIndex)
        {
            if (_map != null &&
                _mapPageIndex == pageIndex &&
                string.Equals(_mapPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _map;
            }

            string? password = _host.CurrentPdfPassword;
            int generation = ++_mapGeneration;
            string mapPath = path;
            int mapPageIndex = pageIndex;

            var map = await Task.Run(() =>
            {
                try
                {
                    using var document = PdfPigDocumentFactory.Open(mapPath, password);
                    var page = document.GetPage(mapPageIndex + 1);
                    var (text, letters) = SearchHighlightService.BuildPdfTextMap(page);
                    return new PdfTextSelectionMap
                    {
                        Text = text,
                        Letters = letters,
                        PageWidth = Math.Max(1.0, page.Width),
                        PageHeight = Math.Max(1.0, page.Height)
                    };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"PDF text map failed: {ex.Message}");
                    return null;
                }
            });

            if (generation != _mapGeneration || map == null) return null;

            _map = map;
            _mapPath = mapPath;
            _mapPassword = password;
            _mapPageIndex = mapPageIndex;
            return map;
        }

        private bool TryMapPointToPdf(Point canvasPoint, PdfTextSelectionMap map, out double pdfX, out double pdfY)
        {
            pdfX = 0;
            pdfY = 0;

            var canvas = _host.MainCanvas;
            if (canvas == null) return false;

            if (!PdfPageLayout.TryGetPageRect(
                    _host.CurrentBitmap,
                    canvas.Size,
                    _host.ZoomLevel,
                    _host.ImageViewportNavigationService.PanX,
                    _host.ImageViewportNavigationService.PanY,
                    out var pageRect))
            {
                return false;
            }

            double normalizedX = (canvasPoint.X - pageRect.X) / pageRect.Width;
            double normalizedY = (canvasPoint.Y - pageRect.Y) / pageRect.Height;
            pdfX = normalizedX * map.PageWidth;
            pdfY = map.PageHeight - (normalizedY * map.PageHeight);
            return true;
        }

        private static int FindCharacterIndex(PdfTextSelectionMap map, double x, double y)
        {
            if (map.Text.Length == 0) return -1;

            int bestIndex = -1;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < map.Text.Length; i++)
            {
                var letter = map.Letters[i];
                if (letter == null) continue;

                var rect = letter.GlyphRectangleLoose;
                double dx = x < rect.Left ? rect.Left - x : (x > rect.Right ? x - rect.Right : 0);
                double dy = y < rect.Bottom ? rect.Bottom - y : (y > rect.Top ? y - rect.Top : 0);
                double distance = (dx * dx) + (dy * dy);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }

                if (bestDistance <= 0) break;
            }

            if (bestIndex < 0) return -1;

            // 같은 Letter 객체가 여러 문자를 담는 경우(합자 등)에는 X 위치로 문자를 세분화합니다.
            var source = map.Letters[bestIndex];
            if (source != null)
            {
                int start = bestIndex;
                int end = bestIndex;
                while (start - 1 >= 0 && ReferenceEquals(map.Letters[start - 1], source)) start--;
                while (end + 1 < map.Letters.Count && ReferenceEquals(map.Letters[end + 1], source)) end++;

                double startX = source.StartBaseLine.X;
                double endX = source.EndBaseLine.X;
                if (end > start && endX > startX)
                {
                    double ratio = Math.Clamp((x - startX) / (endX - startX), 0, 1);
                    int offset = (int)Math.Round(ratio * (end - start));
                    return Math.Clamp(start + offset, 0, map.Text.Length - 1);
                }
            }

            return bestIndex;
        }

        private string ExtractSelectedText()
        {
            var map = _map;
            if (map == null || AnchorIndex < 0 || FocusIndex < 0) return string.Empty;

            int start = Math.Min(AnchorIndex, FocusIndex);
            int end = Math.Max(AnchorIndex, FocusIndex);
            if (start >= map.Text.Length) return string.Empty;

            end = Math.Min(end, map.Text.Length - 1);
            return map.Text.Substring(start, end - start + 1).Trim();
        }

        private void UpdateHighlights(PdfTextSelectionMap map)
        {
            _highlights.Clear();
            if (AnchorIndex < 0 || FocusIndex < 0) return;

            int start = Math.Min(AnchorIndex, FocusIndex);
            int end = Math.Max(AnchorIndex, FocusIndex);

            var letters = new List<Letter>();
            for (int i = start; i <= end && i < map.Letters.Count; i++)
            {
                var letter = map.Letters[i];
                if (letter != null) letters.Add(letter);
            }

            if (letters.Count == 0) return;

            foreach (var line in SearchHighlightService.GroupLettersByLine(letters))
            {
                if (line.Count == 0) continue;

                double left = line.Min(letter => letter.GlyphRectangleLoose.Left);
                double right = line.Max(letter => letter.GlyphRectangleLoose.Right);
                double bottom = line.Min(letter => letter.GlyphRectangleLoose.Bottom);
                double top = line.Max(letter => letter.GlyphRectangleLoose.Top);

                if (right <= left || top <= bottom) continue;
                _highlights.Add(new PdfSearchHighlight(left, bottom, right, top, map.PageWidth, map.PageHeight, 0));
            }
        }
    }
}
