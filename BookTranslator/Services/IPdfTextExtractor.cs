namespace BookTranslator.Services;

public interface IPdfTextExtractor
{
    string Extract(string pdfPath);
}