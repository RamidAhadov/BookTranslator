using System.Text;
using UglyToad.PdfPig;

namespace BookTranslator.Services;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public string Extract(string pdfPath)
    {
        var sb = new StringBuilder();

        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}