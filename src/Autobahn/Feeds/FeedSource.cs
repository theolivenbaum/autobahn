using System.Text.Json;
using Autobahn.Internal.Domain.Feeds;
using Autobahn.Internal.Json;

namespace Autobahn.Feeds;

/// <summary>
/// Where a feed's items come from: a CSV file, a JSON file, or a sequence you already have.
/// </summary>
/// <remarks>
/// A source is separate from the order a feed hands items out in, so any source composes with
/// any of the feeds on <see cref="Feed"/>. The <c>Stream…</c> methods return a factory rather
/// than a sequence, because a streaming feed that restarts has to reopen the file - rewinding
/// an enumerator is not something the interface can promise.
/// </remarks>
public static class FeedSource
{
    /// <summary>Reads a whole JSON array into memory and deserializes it.</summary>
    public static IReadOnlyList<T> FromJson<T>(string filePath) =>
        AutobahnJson.Deserialize<List<T>>(File.ReadAllText(RequireFile(filePath)));

    /// <summary>
    /// Reads a JSON array one element at a time, without holding the document in memory.
    /// </summary>
    public static Func<IEnumerable<T>> StreamJson<T>(string filePath)
    {
        var path = RequireFile(filePath);
        return () => ReadJson<T>(path);
    }

    /// <summary>
    /// Reads a whole CSV into memory as one dictionary per row, keyed by the header.
    /// </summary>
    /// <remarks>
    /// Rows come back as dictionaries rather than a typed object because a feed's shape is
    /// usually the test's business rather than the library's; map them yourself, or use
    /// <see cref="FromCsv{T}"/> and hand over the mapping.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> FromCsv(string filePath) =>
        ReadCsv(RequireFile(filePath)).ToArray();

    /// <summary>Reads a whole CSV into memory, mapping each row as it goes.</summary>
    public static IReadOnlyList<T> FromCsv<T>(string filePath, Func<IReadOnlyDictionary<string, string>, T> map) =>
        ReadCsv(RequireFile(filePath)).Select(map).ToArray();

    /// <summary>Reads a CSV row by row, without holding the file in memory.</summary>
    public static Func<IEnumerable<IReadOnlyDictionary<string, string>>> StreamCsv(string filePath)
    {
        var path = RequireFile(filePath);
        return () => ReadCsv(path);
    }

    /// <summary>Reads a CSV row by row, mapping each row as it goes.</summary>
    public static Func<IEnumerable<T>> StreamCsv<T>(
        string filePath, Func<IReadOnlyDictionary<string, string>, T> map)
    {
        var path = RequireFile(filePath);
        return () => ReadCsv(path).Select(map);
    }

    private static string RequireFile(string filePath) =>
        File.Exists(filePath)
            ? filePath
            : throw new AutobahnException($"Feed source file not found: '{filePath}'.");

    private static IEnumerable<T> ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);

        // The whole point of streaming is not holding the document, so this walks the array
        // element by element rather than deserializing it in one go.
        foreach (var item in JsonSerializer.Deserialize<IEnumerable<T>>(stream, AutobahnJson.Config) ?? [])
            yield return item;
    }

    /// <summary>
    /// A CSV reader that handles quoted fields, embedded commas and doubled quotes.
    /// </summary>
    /// <remarks>
    /// Thirty lines rather than a dependency, which is the repository's standing trade. It
    /// does not handle a newline inside a quoted field - a feed file with one is unusual
    /// enough that failing to parse it visibly beats pulling in a parser for it.
    /// </remarks>
    private static IEnumerable<IReadOnlyDictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);

        var headerLine = reader.ReadLine();
        if (headerLine is null) yield break;

        var headers = SplitCsvLine(headerLine);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var values = SplitCsvLine(line);
            var row = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < headers.Count; i++)
                row[headers[i]] = i < values.Count ? values[i] : string.Empty;

            yield return row;
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }

                // A doubled quote inside a quoted field is one literal quote.
                if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
