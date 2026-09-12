using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        /// <summary>컬럼/줄 기준 읽기 순서 정보입니다. 컬럼 단위 선택 제한에 사용됩니다.</summary>
        public PdfSelectionLayout Layout { get; init; } = PdfSelectionLayout.Empty;
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

        private string? _selectionPath;

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
            _selectionPath = _host.CurrentPdfPath;
            SelectionPageIndex = _host.CurrentPdfPageIndex;
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
                if (!string.Equals(path, _host.CurrentPdfPath, StringComparison.OrdinalIgnoreCase)) return;
                if (pageIndex != _host.CurrentPdfPageIndex) return;
                if (!TryMapPointToPdf(_startPoint, map, out double x, out double y)) return;

                int index = FindCharacterIndex(map, x, y);
                if (index < 0) return;

                AnchorIndex = index;
                FocusIndex = index;
                HasSelection = true;
                SelectionPageIndex = pageIndex;
                _selectionPath = path;
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
            ResetSelection();
            _host.MainCanvas?.Invalidate();
        }

        /// <summary>
        /// 현재 표시 중인 PDF 문서/페이지와 선택 상태를 동기화합니다.
        /// 다른 파일을 열거나 페이지가 바뀌어 선택이 더 이상 유효하지 않으면 오버레이를 지웁니다.
        /// (그리기 도중 호출되므로 다시 그리기를 요청하지 않습니다.)
        /// </summary>
        public void SyncDocument(string? pdfPath, int pageIndex)
        {
            if (!IsDragging && !HasSelection && _selectionPath == null) return;

            if (string.Equals(_selectionPath, pdfPath, StringComparison.OrdinalIgnoreCase) &&
                SelectionPageIndex == pageIndex)
            {
                return;
            }

            ResetSelection();
        }

        private void ResetSelection()
        {
            IsDragging = false;
            HasSelection = false;
            AnchorIndex = -1;
            FocusIndex = -1;
            SelectionPageIndex = -1;
            _selectionPath = null;
            _highlights.Clear();
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
                    double pageWidth = Math.Max(1.0, page.Width);
                    return new PdfTextSelectionMap
                    {
                        Text = text,
                        Letters = letters,
                        PageWidth = pageWidth,
                        PageHeight = Math.Max(1.0, page.Height),
                        Layout = PdfSelectionLayoutBuilder.Build(letters, pageWidth)
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

        /// <summary>
        /// 앵커와 포커스 사이에서 실제로 선택되는 문자 맵 인덱스 목록을 반환합니다.
        /// 컬럼이 감지된 페이지에서는 컬럼 → 줄 → X 읽기 순서를 사용하고,
        /// 드래그한 세로 범위 밖(다른 컬럼 영역 등)의 문자는 제외합니다.
        /// </summary>
        private List<int> BuildSelectedIndices(PdfTextSelectionMap map)
        {
            var selected = new List<int>();
            if (AnchorIndex < 0 || FocusIndex < 0) return selected;

            var layout = map.Layout;
            bool hasLayout =
                layout.HasReadingOrder &&
                layout.OrderPosition.Length == map.Letters.Count &&
                AnchorIndex < map.Letters.Count &&
                FocusIndex < map.Letters.Count;

            int anchorPosition = hasLayout ? layout.OrderPosition[AnchorIndex] : -1;
            int focusPosition = hasLayout ? layout.OrderPosition[FocusIndex] : -1;

            if (anchorPosition < 0 || focusPosition < 0)
            {
                // 레이아웃 정보가 없으면 기존 방식(맵 순서 범위)으로 대체합니다.
                int fallbackStart = Math.Max(0, Math.Min(AnchorIndex, FocusIndex));
                int fallbackEnd = Math.Min(map.Letters.Count - 1, Math.Max(AnchorIndex, FocusIndex));
                for (int i = fallbackStart; i <= fallbackEnd; i++) selected.Add(i);
                return selected;
            }

            int startPosition = Math.Min(anchorPosition, focusPosition);
            int endPosition = Math.Max(anchorPosition, focusPosition);

            int firstLine = Math.Min(layout.LineIndex[AnchorIndex], layout.LineIndex[FocusIndex]);
            int lastLine = Math.Max(layout.LineIndex[AnchorIndex], layout.LineIndex[FocusIndex]);

            for (int position = startPosition; position <= endPosition; position++)
            {
                int index = layout.ReadingOrder[position];
                int line = layout.LineIndex[index];

                // 다른 컬럼으로 드래그해도 드래그한 세로 범위의 텍스트만 선택합니다.
                if (line < firstLine || line > lastLine) continue;

                selected.Add(index);
            }

            return selected;
        }

        private static void AppendSpace(StringBuilder sb)
        {
            if (sb.Length == 0 || sb[^1] == ' ') return;
            sb.Append(' ');
        }

        private string ExtractSelectedText()
        {
            var map = _map;
            if (map == null || AnchorIndex < 0 || FocusIndex < 0) return string.Empty;

            if (!map.Layout.HasReadingOrder)
            {
                int fallbackStart = Math.Min(AnchorIndex, FocusIndex);
                int fallbackEnd = Math.Max(AnchorIndex, FocusIndex);
                if (fallbackStart >= map.Text.Length) return string.Empty;

                fallbackEnd = Math.Min(fallbackEnd, map.Text.Length - 1);
                return map.Text.Substring(fallbackStart, fallbackEnd - fallbackStart + 1).Trim();
            }

            var selected = BuildSelectedIndices(map);
            if (selected.Count == 0) return string.Empty;

            var layout = map.Layout;
            var sb = new StringBuilder(selected.Count + 8);
            Letter? previous = null;
            int previousLine = -1;

            foreach (int index in selected)
            {
                if (index < 0 || index >= map.Letters.Count || index >= map.Text.Length) continue;

                var letter = map.Letters[index];
                if (letter == null) continue;

                int line = layout.LineIndex[index];
                if (sb.Length > 0 &&
                    (line != previousLine || (previous != null && SearchHighlightService.ShouldInsertPdfSpace(previous, letter))))
                {
                    AppendSpace(sb);
                }

                char c = map.Text[index];
                if (char.IsWhiteSpace(c)) AppendSpace(sb);
                else sb.Append(c);

                previous = letter;
                previousLine = line;
            }

            return sb.ToString().Trim();
        }

        private void UpdateHighlights(PdfTextSelectionMap map)
        {
            _highlights.Clear();
            if (AnchorIndex < 0 || FocusIndex < 0) return;

            var letters = new List<Letter>();
            foreach (int index in BuildSelectedIndices(map))
            {
                if (index < 0 || index >= map.Letters.Count) continue;

                var letter = map.Letters[index];
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
