using System.Text.RegularExpressions;
using Autobahn.Internal.Infra;
using Autobahn.Internal.Json;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>
/// The self-contained HTML report: the embedded template with its own assets inlined and
/// the session result embedded as a JSON view model.
/// </summary>
internal static partial class HtmlReport
{
    private const string RemoveLineCommand = "<!-- remove-->";
    private const string IncludeViewModelCommand = "<!-- include view model -->";
    private const string IncludeAssetCommand = "<!-- include asset -->";

    [GeneratedRegex("""<link[/\s\w="\d]*href=['"]([\.\d\w\\/-]*)['"][\s\w="'/]*>""")]
    private static partial Regex StyleRegex();

    [GeneratedRegex("""<script[\s\w="'/]*src\s*=\s*['"]([\w/\.\d\s-]*)["']>""")]
    private static partial Regex ScriptRegex();

    public static string Print(ILogger logger, SessionResult sessionResult)
    {
        try
        {
            logger.ZLogTrace($"HtmlReport.print");

            var indexHtml = ResourceManager.ReadResource("index.html");
            if (indexHtml is null)
                return string.Empty;

            return RemoveDescription(indexHtml)
                .Replace("\r", "")
                .Split('\n')
                .Select(line => ApplyHtmlReplace(sessionResult, line))
                .ConcatLines();
        }
        catch (Exception ex)
        {
            logger.ZLogError($"HtmlReport.print failed: {ex}");
            return "Could not generate report";
        }
    }

    /// <summary>The template opens with a comment explaining its own commands; that is not part of the report.</summary>
    private static string RemoveDescription(string html) => html[html.IndexOf("<!DOCTYPE", StringComparison.Ordinal)..];

    private static string ApplyHtmlReplace(SessionResult sessionResult, string line)
    {
        if (line.Contains(RemoveLineCommand))
            return string.Empty;

        if (line.Contains(IncludeViewModelCommand))
            return $"const viewModel = {AutobahnJson.Serialize(sessionResult)};";

        if (!line.Contains(IncludeAssetCommand))
            return line;

        return TryIncludeAsset("style", StyleRegex(), line)
               ?? TryIncludeAsset("script", ScriptRegex(), line)
               ?? line;
    }

    /// <summary>Replaces a link or script tag with the embedded asset it points at.</summary>
    private static string? TryIncludeAsset(string tagName, Regex regex, string line)
    {
        var match = regex.Match(line);
        if (!match.Success)
            return null;

        var resourceName = match.Groups[1].Value.Replace("/", ".");
        var resource = ResourceManager.ReadResource(resourceName);

        return resource is null ? null : $"<{tagName}>{resource}</{tagName}>";
    }
}
