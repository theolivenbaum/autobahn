namespace Autobahn.Stats;

/// <summary>A report file format Autobahn can write at the end of a run.</summary>
public enum ReportFormat
{
    Txt = 0,
    Html = 1,
    Csv = 2,
    Md = 3,

    /// <summary>
    /// The versioned run artifact: the whole result as one machine-readable document. Every
    /// other format is a rendering of it.
    /// </summary>
    Json = 4
}
