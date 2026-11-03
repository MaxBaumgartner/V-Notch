using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// One A4 page per JPEG, as the planner dashboard builds its hand-in PDF in the browser: the photo fitted inside an
/// 18 pt margin, landscape photos on a landscape page, the JPEG embedded as it is (DCTDecode, nothing re-encoded).
/// </summary>
internal static class PdfPages
{
    private const double A4Width = 595.28, A4Height = 841.89, Margin = 18;

    /// <summary>Null when a page is not a JPEG whose size can be read.</summary>
    public static byte[]? FromJpegs(IReadOnlyList<byte[]> jpegs)
    {
        if (jpegs.Count == 0) return null;
        var sizes = new List<(int Width, int Height, int Components)>(jpegs.Count);
        foreach (var jpeg in jpegs)
        {
            if (JpegSize(jpeg) is not { } size) return null;
            sizes.Add(size);
        }

        using var pdf = new MemoryStream();
        var offsets = new long[3 + jpegs.Count * 3];
        void Write(string text) { byte[] b = Encoding.Latin1.GetBytes(text); pdf.Write(b, 0, b.Length); }
        void Object(int number, Action body)
        {
            offsets[number] = pdf.Position;
            Write($"{number} 0 obj\n");
            body();
            Write("\nendobj\n");
        }
        static int PageObject(int i) => 3 + i * 3;
        static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        Write("%PDF-1.4\n%âãÏÓ\n");
        Object(1, () => Write("<< /Type /Catalog /Pages 2 0 R >>"));
        var kids = new StringBuilder();
        for (int i = 0; i < jpegs.Count; i++) kids.Append(i == 0 ? "" : " ").Append(PageObject(i)).Append(" 0 R");
        Object(2, () => Write($"<< /Type /Pages /Count {jpegs.Count} /Kids [{kids}] >>"));

        for (int i = 0; i < jpegs.Count; i++)
        {
            var (width, height, components) = sizes[i];
            bool landscape = width > height;
            double pageWidth = landscape ? A4Height : A4Width, pageHeight = landscape ? A4Width : A4Height;
            double scale = Math.Min((pageWidth - 2 * Margin) / width, (pageHeight - 2 * Margin) / height);
            double drawWidth = width * scale, drawHeight = height * scale;
            double x = (pageWidth - drawWidth) / 2, y = (pageHeight - drawHeight) / 2;
            string content = $"q {F(drawWidth)} 0 0 {F(drawHeight)} {F(x)} {F(y)} cm /Im0 Do Q";
            int page = PageObject(i);
            byte[] jpeg = jpegs[i];

            Object(page, () => Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidth)} {F(pageHeight)}] " +
                $"/Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>"));
            Object(page + 1, () => Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
            Object(page + 2, () =>
            {
                string colors = components == 1 ? "/DeviceGray" : "/DeviceRGB";
                Write($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colors} " +
                      $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
                pdf.Write(jpeg, 0, jpeg.Length);
                Write("\nendstream");
            });
        }

        int total = offsets.Length;
        long xref = pdf.Position;
        Write($"xref\n0 {total}\n0000000000 65535 f \n");
        for (int i = 1; i < total; i++) Write($"{offsets[i].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>Width, height and colour components from the JPEG's frame header (SOF0–SOF15 without DHT, JPG, DAC).</summary>
    internal static (int Width, int Height, int Components)? JpegSize(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return null;
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; }
            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (length < 2 || pos + 2 + length > jpeg.Length) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 8) return null;
                int height = (jpeg[pos + 5] << 8) | jpeg[pos + 6];
                int width = (jpeg[pos + 7] << 8) | jpeg[pos + 8];
                int components = jpeg[pos + 9];
                return width > 0 && height > 0 ? (width, height, components) : null;
            }
            if (marker == 0xDA) return null;
            pos += 2 + length;
        }
        return null;
    }
}
