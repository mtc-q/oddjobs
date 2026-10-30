using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OddJobs.Binder
{
    public sealed class PdfImage
    {
        public byte[] Data;
        public bool IsJpeg;           // DCTDecode; otherwise zlib-compressed raw pixels (FlateDecode)
        public int Width;
        public int Height;
        public int Components = 3;    // 1 = grey, 3 = RGB
    }

    public sealed class PdfPage
    {
        public double Width;          // points (1/72 inch)
        public double Height;
        public PdfImage Image;
        public double X, Y, DrawWidth, DrawHeight;
    }

    /// <summary>
    /// Writes a minimal, standards-conforming PDF 1.4 file with one image per page.
    /// No fonts, no scripts, no forms, no links: the output is just pictures on pages.
    /// </summary>
    public static class PdfWriter
    {
        public static void Write(Stream output, IList<PdfPage> pages)
        {
            var w = new CountingWriter(output);
            var offsets = new List<long> { 0 };   // object 0 is the free-list head

            w.Ascii("%PDF-1.4\n");
            w.Bytes(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });   // marks the file as binary

            // Object numbers: 1 catalog, 2 page tree, 3 info, then 3 per page (page, contents, image).
            int First(int page) => 4 + page * 3;

            Begin(w, offsets, 1);
            w.Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            Begin(w, offsets, 2);
            var kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++) kids.Append(First(i)).Append(" 0 R ");
            w.Ascii("<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count + " >>\nendobj\n");

            Begin(w, offsets, 3);
            w.Ascii("<< /Producer (Binder) >>\nendobj\n");

            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                int pageObj = First(i), contentObj = pageObj + 1, imageObj = pageObj + 2;

                Begin(w, offsets, pageObj);
                w.Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + N(p.Width) + " " + N(p.Height) + "] " +
                        "/Resources << /XObject << /Im0 " + imageObj + " 0 R >> /ProcSet [/PDF /ImageB /ImageC] >> " +
                        "/Contents " + contentObj + " 0 R >>\nendobj\n");

                // Place the image: scale the unit square to the drawing size, then move it into position.
                var content = Encoding.ASCII.GetBytes("q " + N(p.DrawWidth) + " 0 0 " + N(p.DrawHeight) + " " + N(p.X) + " " + N(p.Y) + " cm /Im0 Do Q\n");
                Begin(w, offsets, contentObj);
                w.Ascii("<< /Length " + content.Length + " >>\nstream\n");
                w.Bytes(content);
                w.Ascii("\nendstream\nendobj\n");

                var img = p.Image;
                Begin(w, offsets, imageObj);
                w.Ascii("<< /Type /XObject /Subtype /Image /Width " + img.Width + " /Height " + img.Height +
                        " /ColorSpace " + (img.Components == 1 ? "/DeviceGray" : "/DeviceRGB") +
                        " /BitsPerComponent 8 /Filter " + (img.IsJpeg ? "/DCTDecode" : "/FlateDecode") +
                        " /Length " + img.Data.Length + " >>\nstream\n");
                w.Bytes(img.Data);
                w.Ascii("\nendstream\nendobj\n");
            }

            long xref = w.Position;
            w.Ascii("xref\n0 " + offsets.Count + "\n");
            w.Ascii("0000000000 65535 f\r\n");
            for (int i = 1; i < offsets.Count; i++) w.Ascii(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n");
            w.Ascii("trailer\n<< /Size " + offsets.Count + " /Root 1 0 R /Info 3 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }

        static void Begin(CountingWriter w, List<long> offsets, int number)
        {
            while (offsets.Count <= number) offsets.Add(0);
            offsets[number] = w.Position;
            w.Ascii(number + " 0 obj\n");
        }

        static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Compresses raw pixels into the zlib format PDF's FlateDecode expects.</summary>
        public static byte[] Zlib(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    deflate.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte x in raw)
                {
                    a = (a + x) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        sealed class CountingWriter
        {
            readonly Stream _s;
            public long Position { get; private set; }
            public CountingWriter(Stream s) { _s = s; }

            public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

            public void Bytes(byte[] data)
            {
                _s.Write(data, 0, data.Length);
                Position += data.Length;
            }
        }
    }
}
