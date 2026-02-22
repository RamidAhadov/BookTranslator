using BookTranslator.Models.Layout;

namespace BookTranslator.Services;

public interface IPdfLayoutAnalyzer
{
    Task<IReadOnlyList<PageObject>> AnalyzeAsync(string pdfPath, CancellationToken ct);
}