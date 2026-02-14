using System.Text;

namespace BookTranslator.Services;

public sealed class TxtOutputWriter : IOutputWriter
{
    public async Task WriteAsync(string content, string path, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
    }
}