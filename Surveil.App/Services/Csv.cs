namespace Surveil.App.Services;

/// <summary>Minimal RFC-4180 CSV field/line formatting (quote when a field contains a comma,
/// quote, or newline; double embedded quotes).</summary>
public static class Csv
{
    public static string Field(string value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string Line(params string[] fields) => string.Join(",", fields.Select(Field));
}
