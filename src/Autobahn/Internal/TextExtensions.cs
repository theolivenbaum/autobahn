namespace Autobahn.Internal;

/// <summary>Small string helpers shared by the reports and the error messages.</summary>
internal static class TextExtensions
{
    public static string ConcatLines(this IEnumerable<string> strings) =>
        string.Join(Environment.NewLine, strings);

    public static string ConcatWithComma(this IEnumerable<string> strings) =>
        string.Join(", ", strings);

    public static string AppendNewLine(this string str) => str + Environment.NewLine;

    /// <summary>The values that appear more than once, in first-seen order.</summary>
    public static IEnumerable<string> FilterDuplicates(this IEnumerable<string> data) =>
        data.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key);
}
