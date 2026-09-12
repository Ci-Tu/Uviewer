using System;
using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Uviewer.Services
{
    /// <summary>
    /// 비밀번호가 설정된 PDF도 PdfPig로 열 수 있도록 공통 진입점을 제공한다.
    /// </summary>
    internal static class PdfPigDocumentFactory
    {
        public static PdfDocument Open(string path, string? password)
        {
            return string.IsNullOrEmpty(password)
                ? PdfDocument.Open(path)
                : PdfDocument.Open(path, new ParsingOptions { Password = password });
        }

        /// <summary>비밀번호 없이 열 수 없는 암호화된 PDF인지 확인한다.</summary>
        public static bool IsEncrypted(string path)
        {
            // The encryption dictionary lives in the trailer and is available without
            // decrypting the document. Check it first because PdfPig can surface some
            // encryption revisions as a generic parsing exception.
            if (HasEncryptionMarker(path))
            {
                return true;
            }

            try
            {
                using var document = Open(path, null);
                return false;
            }
            catch (PdfDocumentEncryptedException)
            {
                return true;
            }
            catch
            {
                // Keep this fallback for PDFs whose trailer is not in the final read
                // window, but never classify an encryption marker as a normal failure.
                return HasEncryptionMarker(path);
            }
        }

        /// <summary>지정한 비밀번호로 문서를 열 수 있는지 확인한다.</summary>
        public static bool CanOpen(string path, string? password)
        {
            try
            {
                using var document = Open(path, password);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>PDF 마지막 부분의 /Encrypt 흔적으로 암호화 문서인지 확인한다(보조 수단).</summary>
        public static bool HasEncryptionMarker(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                int take = (int)Math.Min(8192L, stream.Length);
                if (take <= 0)
                {
                    return false;
                }

                stream.Seek(-take, SeekOrigin.End);
                var buffer = new byte[take];
                int read = stream.Read(buffer, 0, take);
                return buffer.AsSpan(0, read).IndexOf("/Encrypt"u8) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
