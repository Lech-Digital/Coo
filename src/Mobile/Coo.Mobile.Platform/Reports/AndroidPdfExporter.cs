using Android.Graphics;
using Android.Graphics.Pdf;
using Android.Text;
using Coo.Mobile.Application.Reports;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Coo.Mobile.Platform.Reports;

/// <summary>
/// A <see cref="ReportDocument"/> as an A4 PDF through Android's own <see cref="PdfDocument"/>: no library, no fonts to
/// ship (Roboto has every letter of the seven languages). Text wraps; a table that runs over a page repeats its header.
/// The file goes to the cache and out through the share sheet; Coo keeps no copy.
/// </summary>
internal sealed class AndroidPdfExporter : IReportExporter
{
    private const int PageWidth = 595, PageHeight = 842, Margin = 40;
    private const int Ink = unchecked((int)0xFF2A2823), Ink2 = unchecked((int)0xFF6B665C), Accent = unchecked((int)0xFFBA4F28), Line = unchecked((int)0xFFDDD6C9), Tint = unchecked((int)0xFFF2E8DC);

    public async Task<bool> ShareAsync(ReportDocument document, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await Task.Run(() => Write(document, path), cancellationToken);
            await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = document.Title,
                File = new ShareFile(path, "application/pdf"),
            }));
            return true;
        }
#pragma warning disable CA1031 // A failed export is reported to the page, never thrown into it.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Report not shared: {e.GetType().Name}");
            return false;
        }
    }

    private static void Write(ReportDocument document, string path)
    {
        using var pdf = new PdfDocument();
        var writer = new PageWriter(pdf);

        writer.Paragraph(document.Title, 20, Ink, bold: true, after: 6);
        foreach (var line in document.Subtitle)
            writer.Paragraph(line, 11, Ink2, after: 2);
        writer.Space(10);

        foreach (var section in document.Sections)
        {
            // A heading stays with what follows it: its paragraphs, or its table's header and first rows.
            writer.KeepTogether(section.Table is null ? 48 : 110);
            writer.Paragraph(section.Heading, 14, Accent, bold: true, after: 6);
            foreach (var paragraph in section.Paragraphs)
                writer.Paragraph(paragraph, 10.5f, Ink, after: 3);
            if (section.Table is { } table)
                writer.Table(table);
            writer.Space(14);
        }

        writer.Footer(document.Footer);
        writer.Finish();
        using var stream = File.Create(path);
        pdf.WriteTo(stream);
    }

    /// <summary>Lays out top to bottom, starting a new page when the next block does not fit.</summary>
    private sealed class PageWriter(PdfDocument pdf)
    {
        private const float Bottom = PageHeight - Margin - 20;
        private PdfDocument.Page? _page;
        private float _y;
        private int _number;
        private string _footer = "";

        private Canvas Canvas
        {
            get
            {
                if (_page is null)
                    NewPage();
                return _page!.Canvas!;
            }
        }

        public void Space(float points) => _y += points;

        public void KeepTogether(float points)
        {
            if (_page is null || _y + points > Bottom)
                NewPage();
        }

        public void Paragraph(string text, float size, int color, bool bold = false, float after = 0)
        {
            var layout = Layout(text, size, color, bold, PageWidth - (2 * Margin));
            KeepTogether(layout.Height);
            Canvas.Save();
            Canvas.Translate(Margin, _y);
            layout.Draw(Canvas);
            Canvas.Restore();
            _y += layout.Height + after;
        }

        public void Table(ReportTable table)
        {
            var total = table.Columns.Sum(c => c.Width);
            var widths = table.Columns.Select(c => (PageWidth - (2 * Margin)) * c.Width / total).ToArray();

            void Row(IReadOnlyList<string> cells, bool header)
            {
                var layouts = cells.Select((cell, i) => Layout(cell, header ? 8.5f : 9f, header ? Ink2 : Ink, header, (int)widths[i] - 6)).ToList();
                var height = layouts.Max(l => l.Height) + 6;
                if (_y + height > Bottom)
                {
                    NewPage();
                    if (!header)
                        Row([.. table.Columns.Select(c => c.Header)], true);
                }

                using var fill = new Paint { Color = new Color(header ? Tint : unchecked((int)0xFFFFFFFF)) };
                Canvas.DrawRect(Margin, _y, PageWidth - Margin, _y + height, fill);
                var x = (float)Margin;
                for (var i = 0; i < layouts.Count; i++)
                {
                    Canvas.Save();
                    Canvas.Translate(x + 3, _y + 3);
                    layouts[i].Draw(Canvas);
                    Canvas.Restore();
                    x += widths[i];
                }

                using var rule = new Paint { Color = new Color(Line), StrokeWidth = 0.6f };
                Canvas.DrawLine(Margin, _y + height, PageWidth - Margin, _y + height, rule);
                _y += height;
            }

            KeepTogether(40);
            Row([.. table.Columns.Select(c => c.Header)], true);
            foreach (var row in table.Rows)
                Row(row, false);
        }

        public void Footer(string text) => _footer = text;

        public void Finish()
        {
            if (_page is not null)
                ClosePage();
        }

        private void NewPage()
        {
            if (_page is not null)
                ClosePage();
            _number++;
            using var info = new PdfDocument.PageInfo.Builder(PageWidth, PageHeight, _number).Create();
            _page = pdf.StartPage(info);
            _y = Margin;
        }

        private void ClosePage()
        {
            // Every page says where it came from, and which page it is.
            var footer = Layout($"{_footer}   {_number}", 8, Ink2, false, PageWidth - (2 * Margin));
            _page!.Canvas!.Save();
            _page.Canvas.Translate(Margin, PageHeight - Margin - footer.Height + 8);
            footer.Draw(_page.Canvas);
            _page.Canvas.Restore();
            pdf.FinishPage(_page);
            _page = null;
        }

        private static StaticLayout Layout(string text, float size, int color, bool bold, int width)
        {
            var paint = new TextPaint(PaintFlags.AntiAlias) { TextSize = size, Color = new Color(color) };
            paint.SetTypeface(Typeface.Create(Typeface.Default, bold ? TypefaceStyle.Bold : TypefaceStyle.Normal));
            return StaticLayout.Builder.Obtain(text, 0, text.Length, paint, Math.Max(10, width))!
                .SetAlignment(global::Android.Text.Layout.Alignment.AlignNormal!)!
                .SetLineSpacing(0, 1.1f)!
                .Build()!;
        }
    }
}
