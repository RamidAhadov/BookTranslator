using System.Text;

namespace BookTranslator.Utils;

public static class AtomicFile
{
    public static async Task WriteAllTextAtomicAsync(string path, string content, Encoding encoding, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";

        await File.WriteAllTextAsync(tmp, content, encoding, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAllBytesAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";

        await File.WriteAllBytesAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}