using System.Reflection;
using iText.Kernel.Geom;

namespace BookTranslator.Helpers;

public static class EnumParser
{
    public static T Parse<T>(string value, T defaultValue = default) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
        {
            return result;
        }

        return defaultValue;
    }


    public static PageSize ParsePageSize(string? value, PageSize? defaultValue = null)
    {
        defaultValue ??= PageSize.A4;

        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var name = value.Trim().ToUpperInvariant();

        var field = typeof(PageSize).GetField(
            name,
            BindingFlags.Public | BindingFlags.Static
        );

        if (field?.GetValue(null) is PageSize ps)
            return ps;

        return defaultValue;
    }
}