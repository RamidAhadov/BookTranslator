namespace BookTranslator.Services;

public interface IOutputWriter
{
    Task WriteAsync(string content, string path, string sourcePdfPath, CancellationToken ct);
}
