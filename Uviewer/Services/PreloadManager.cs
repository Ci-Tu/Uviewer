using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Uviewer.Models;
using Uviewer.Services;

namespace Uviewer.Services
{
    public class PreloadManager : IDisposable
    {
        private readonly ImageCacheManager _imageCache;
        private readonly DispatcherQueue _dispatcherQueue;
        private const int DefaultPreloadCount = 5;

        private CancellationTokenSource? _preloadCts;
        private PdfPreloadRequest? _pdfRequest;
        private Task? _pdfPreloadTask;

        private sealed record PdfPreloadRequest(
            int CurrentIndex, List<ImageEntry> Entries, double Zoom, int Generation,
            CanvasBitmap? CurrentBitmap, CanvasBitmap? LeftBitmap, CanvasBitmap? RightBitmap,
            Func<ImageEntry, CancellationToken, Task<CanvasBitmap?>> Load,
            Action Invalidate, bool PrioritizeNext);

        public PreloadManager(ImageCacheManager imageCache, DispatcherQueue dispatcherQueue)
        {
            _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
            _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        }

        // 기존 프리로드 작업 취소
        public void CancelAll()
        {
            _pdfRequest = null;
            _pdfPreloadTask = null;
            _preloadCts?.Cancel();
            _preloadCts?.Dispose();
            _preloadCts = null;
        }

        // Next/Prev 방향을 통합한 프리로드 시작 메서드
        public async Task StartPreloadAsync(
            int currentIndex,
            List<ImageEntry> entries,
            bool isPdfMode,
            double zoomLevel,
            CanvasBitmap? currentBitmap,
            CanvasBitmap? leftBitmap,
            CanvasBitmap? rightBitmap,
            Func<ImageEntry, CancellationToken, Task<CanvasBitmap?>> loadBitmapFunc,
            Action invalidateCanvasAction,
            bool prioritizeNext = true,
            bool requireSharpening = false)
        {
            try
            {
                if (isPdfMode)
                {
                    await StartPdfPreloadAsync(new PdfPreloadRequest(
                        currentIndex, entries, zoomLevel, _imageCache.Generation,
                        currentBitmap, leftBitmap, rightBitmap, loadBitmapFunc,
                        invalidateCanvasAction, prioritizeNext));
                    return;
                }

                CancelAll();

                _preloadCts = new CancellationTokenSource();
                var token = _preloadCts.Token;
                int generation = _imageCache.Generation;

                if (token.IsCancellationRequested || entries == null || entries.Count == 0) return;

                int preloadDist = isPdfMode ? 3 : DefaultPreloadCount;
                var tasks = new List<Task>();

                for (int d = 1; d <= preloadDist; d++)
                {
                    if (token.IsCancellationRequested) break;

                    // 우선순위 방향에 따라 배열 순서 변경
                    int[] targets = prioritizeNext
                        ? new[] { currentIndex + d, currentIndex - d }
                        : new[] { currentIndex - d, currentIndex + d };

                    foreach (int index in targets)
                    {
                        if (index < 0 || index >= entries.Count || index == currentIndex) continue;
                    
                        if (!FileExplorerService.IsNavigableImage(entries[index])) continue;

                        bool isPdfEntry = entries[index].IsPdfEntry && isPdfMode;

                        if (_imageCache.ShouldSkipPreload(index, isPdfEntry, zoomLevel, requireSharpening)) continue;
                        if (!_imageCache.TryMarkForLoading(index, generation)) continue;

                        var entry = entries[index];
                        var capturedIndex = index;

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                if (token.IsCancellationRequested) return;

                                // MainWindow에서 전달받은 디코딩 콜백 실행
                                CanvasBitmap? bitmap = await loadBitmapFunc(entry, token);

                                if (token.IsCancellationRequested)
                                {
                                    _imageCache.ReleaseBitmapIfUncached(bitmap);
                                    return;
                                }

                                if (bitmap != null)
                                {
                                    if (!_imageCache.UpdateCache(capturedIndex, bitmap, isPdfEntry, zoomLevel,
                                        currentBitmap, generation, token)) return;

                                    _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                                    {
                                        if (!token.IsCancellationRequested && generation == _imageCache.Generation)
                                            invalidateCanvasAction();
                                    });
                                }
                            }
                            catch { }
                            finally
                            {
                                _imageCache.UnmarkLoading(capturedIndex, generation);
                            }
                        })); // Run finally even if cancellation preceded scheduling.
                    }
                }

                await Task.WhenAll(tasks);

                if (!token.IsCancellationRequested && generation == _imageCache.Generation)
                {
                    _imageCache.CleanupOldPreloadedImages(currentIndex, isPdfMode, DefaultPreloadCount, currentBitmap, leftBitmap, rightBitmap);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preload error: {ex.Message}");
            }
        }

        private Task StartPdfPreloadAsync(PdfPreloadRequest request)
        {
            var previousTask = _pdfPreloadTask;
            // Navigation updates the desired window without cancelling useful
            // in-flight pages. Document/cache/zoom changes still cancel the scope.
            if (_pdfRequest == null ||
                !ReferenceEquals(_pdfRequest.Entries, request.Entries) ||
                _pdfRequest.Generation != request.Generation ||
                _pdfRequest.Zoom != request.Zoom)
            {
                CancelAll();
                _preloadCts = new CancellationTokenSource();
            }

            _pdfRequest = request;
            if (_pdfPreloadTask == null || _pdfPreloadTask.IsCompleted)
                _pdfPreloadTask = RunPdfPreloadAsync(_preloadCts!.Token, previousTask);
            return _pdfPreloadTask;
        }

        private async Task RunPdfPreloadAsync(CancellationToken token, Task? previousTask)
        {
            // Capture UI-owned canvas/context on the caller's dispatcher, not
            // inside Task.Run. Native rendering itself is asynchronous.
            await Task.Yield();
            // A replaced zoom scope must release its loading marks before the
            // new scope selects pages, otherwise the nearest pages can be skipped.
            if (previousTask != null)
            {
                try { await previousTask; }
                catch (OperationCanceledException) { }
            }
            var attempted = new HashSet<int>();
            PdfPreloadRequest? previousRequest = null;
            while (!token.IsCancellationRequested)
            {
                var request = _pdfRequest;
                if (request == null || request.Generation != _imageCache.Generation) return;
                if (!ReferenceEquals(previousRequest, request)) attempted.Clear();
                previousRequest = request;

                var batch = new List<Task>(2);
                for (int distance = 1; distance <= 3 && batch.Count < 2; distance++)
                {
                    int direction = request.PrioritizeNext ? 1 : -1;
                    foreach (int index in new[] {
                        request.CurrentIndex + distance * direction,
                        request.CurrentIndex - distance * direction })
                    {
                        if (batch.Count == 2) break;
                        if (index < 0 || index >= request.Entries.Count ||
                            !request.Entries[index].IsPdfEntry ||
                            _imageCache.GetPreloadedImage(index) != null ||
                            !attempted.Add(index)) continue;
                        batch.Add(LoadPdfPreviewAsync(request, index, token));
                    }
                }

                if (batch.Count == 0)
                {
                    _imageCache.CleanupOldPreloadedImages(request.CurrentIndex, true,
                        DefaultPreloadCount, request.CurrentBitmap, request.LeftBitmap, request.RightBitmap);
                    return;
                }

                // Only two pages are queued at a time. After each batch, read the
                // newest navigation direction instead of rendering a stale queue.
                await Task.WhenAll(batch);
            }
        }

        private async Task LoadPdfPreviewAsync(PdfPreloadRequest request, int index, CancellationToken token)
        {
            if (!_imageCache.TryMarkForLoading(index, request.Generation)) return;
            try
            {
                token.ThrowIfCancellationRequested();
                var bitmap = await request.Load(request.Entries[index], token);
                if (bitmap == null) return;
                if (_imageCache.UpdateCache(index, bitmap, true, request.Zoom,
                    _pdfRequest?.CurrentBitmap, request.Generation, token, preserveExisting: true))
                {
                    if (!token.IsCancellationRequested) request.Invalidate();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PDF preload error: {ex.Message}");
            }
            finally
            {
                _imageCache.UnmarkLoading(index, request.Generation);
            }
        }

        public void Dispose()
        {
            CancelAll();
        }
    }
}
