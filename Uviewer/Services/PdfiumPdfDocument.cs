using PDFtoImage;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Uviewer.Services
{
    /// <summary>
    /// PDFium-backed PDF access used for encrypted PDFs that Windows.Data.Pdf cannot
    /// load, including AES-256/R6 documents.
    /// </summary>
    internal sealed class PdfiumPdfDocument : IDisposable
    {
        private readonly byte[] _pdfBytes;
        private readonly string? _password;
        private readonly IReadOnlyList<PdfiumPageSize> _pageSizes;
        private bool _disposed;

        private PdfiumPdfDocument(
            byte[] pdfBytes,
            string? password,
            IReadOnlyList<PdfiumPageSize> pageSizes)
        {
            _pdfBytes = pdfBytes;
            _password = password;
            _pageSizes = pageSizes;
        }

        public int PageCount => _pageSizes.Count;

        public static PdfiumPdfDocument Open(string path, string? password)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("PDF path is required.", nameof(path));
            }

            var bytes = File.ReadAllBytes(path);
            try
            {
                var pageSizes = Conversion.GetPageSizes(bytes, password)
                    .Select(size => new PdfiumPageSize(size.Width, size.Height))
                    .ToArray();

                return new PdfiumPdfDocument(bytes, password, pageSizes);
            }
            catch
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw;
            }
        }

        public PdfiumPageSize GetPageSize(uint pageIndex)
        {
            ThrowIfDisposed();
            if (pageIndex >= _pageSizes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }

            return _pageSizes[(int)pageIndex];
        }

        public SKBitmap Render(
            uint pageIndex,
            uint width,
            uint height,
            CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();

            if (pageIndex >= _pageSizes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }

            var options = new RenderOptions(
                Dpi: 72,
                Width: checked((int)width),
                Height: checked((int)height),
                WithAnnotations: true,
                UseTiling: true);

            return Conversion.ToImage(
                _pdfBytes,
                checked((int)pageIndex),
                _password,
                options);
        }

        public void Dispose()
        {
            _disposed = true;
            // Do not clear _pdfBytes here. A foreground render can hold a reference
            // while shutdown is waiting for its native call to return.
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PdfiumPdfDocument));
            }
        }
    }

    internal readonly record struct PdfiumPageSize(double Width, double Height);
}
