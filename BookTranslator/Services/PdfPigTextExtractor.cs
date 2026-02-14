using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace BookTranslator.Services;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public string Extract(string pdfPath)
    {
        StringBuilder sb = new StringBuilder();

        using PdfDocument doc = PdfDocument.Open(pdfPath);
        foreach (Page page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}