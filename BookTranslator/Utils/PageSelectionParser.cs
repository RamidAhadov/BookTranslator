namespace BookTranslator.Utils;

public static class PageSelectionParser
{
    public static IReadOnlyList<int> Parse(string? selection, int maxPageNumber)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return Array.Empty<int>();

        if (maxPageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPageNumber), "Max page number must be positive.");

        HashSet<int> pages = new();
        string[] tokens = selection
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            if (token.Contains('-', StringComparison.Ordinal))
            {
                string[] range = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (range.Length != 2 ||
                    !int.TryParse(range[0], out int start) ||
                    !int.TryParse(range[1], out int end))
                {
                    throw new FormatException($"Invalid page range token: '{token}'. Expected format like '5-9'.");
                }

                if (start <= 0 || end <= 0)
                    throw new FormatException($"Page numbers must be positive. Invalid token: '{token}'.");

                if (end < start)
                    throw new FormatException($"Range end is smaller than start in token: '{token}'.");

                for (int page = start; page <= end; page++)
                    AddPage(pages, page, maxPageNumber);

                continue;
            }

            if (!int.TryParse(token, out int pageNumber))
                throw new FormatException($"Invalid page token: '{token}'.");

            AddPage(pages, pageNumber, maxPageNumber);
        }

        return pages.OrderBy(x => x).ToArray();
    }

    public static string BuildDescriptor(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0)
            return "none";

        List<int> sorted = pages
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        List<string> parts = new();
        int rangeStart = sorted[0];
        int rangeEnd = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            int current = sorted[i];
            if (current == rangeEnd + 1)
            {
                rangeEnd = current;
                continue;
            }

            parts.Add(FormatRange(rangeStart, rangeEnd));
            rangeStart = current;
            rangeEnd = current;
        }

        parts.Add(FormatRange(rangeStart, rangeEnd));
        return string.Join("_", parts);
    }

    private static void AddPage(HashSet<int> pages, int pageNumber, int maxPageNumber)
    {
        if (pageNumber <= 0)
            throw new FormatException($"Page numbers must be positive. Invalid page: {pageNumber}.");

        if (pageNumber > maxPageNumber)
            throw new FormatException($"Page {pageNumber} exceeds max page number {maxPageNumber}.");

        pages.Add(pageNumber);
    }

    private static string FormatRange(int start, int end)
    {
        return start == end ? start.ToString() : $"{start}-{end}";
    }
}
