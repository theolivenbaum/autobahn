using System.Diagnostics;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Infra;
using Autobahn.Internal.Services.Reports;
using Autobahn.Metrics;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Autobahn.Internal.Services.TestHost;

/// <summary>Everything the run prints to the console while it is happening.</summary>
internal static class TestHostConsole
{
    public static void PrintTargetScenarios(IGlobalDependency dep, IEnumerable<RuntimeScenario> targetScns) =>
        dep.LogInfo($"Target scenarios: {targetScns.Select(x => x.ScenarioName).ConcatWithComma()}");

    public static void PrintWarmUpScenarios(IGlobalDependency dep, IEnumerable<RuntimeScenario> warmUpScns) =>
        dep.LogInfo($"Warm up for scenarios: {warmUpScns.Select(x => x.ScenarioName).ConcatWithComma()}");

    /// <summary>Runs an action under a console spinner when there is a console to spin on.</summary>
    public static Task<T> DisplayStatus<T>(IGlobalDependency dep, string msg, Func<StatusContext?, Task<T>> runAction)
    {
        if (dep.ApplicationType != ApplicationType.Console)
        {
            dep.LogInfo(msg);
            return runAction(null);
        }

        return AnsiConsole.Status().StartAsync(msg, ctx => runAction(ctx));
    }

    /// <summary>
    /// Every effective setting and the layer its value came from.
    /// </summary>
    /// <remarks>
    /// Printed through the ordinary logger rather than as a table, so it lands in the log file
    /// too - which is where somebody reading a CI run afterwards will look for it.
    /// </remarks>
    public static void PrintEffectiveConfig(IGlobalDependency dep, SessionArgs sessionArgs)
    {
        if (sessionArgs.EffectiveSettings.Count == 0) return;

        dep.LogInfo("Effective configuration:");

        var width = sessionArgs.EffectiveSettings.Max(x => x.Name.Length);

        foreach (var setting in sessionArgs.EffectiveSettings)
            dep.LogInfo($"  {setting.Name.PadRight(width)}  {setting.Value}  [{setting.Source}]");
    }

    public static void PrintContextInfo(IGlobalDependency dep, SessionArgs sessionArgs)
    {
        dep.LogInfo($"Reports folder: {ReportHelper.GetFullReportsFolderPath(sessionArgs)}");
        dep.Logger.ZLogTrace($"AutobahnConfig: {dep.Config}");

        if (dep.WorkerPlugins.Count == 0)
        {
            dep.LogInfo("Plugins: no plugins were loaded");
            return;
        }

        foreach (var plugin in dep.WorkerPlugins)
            dep.LogInfo($"Plugin loaded: {plugin.PluginName}");
    }

    /// <summary>The live statistics table drawn while a run is in flight.</summary>
    public static class LiveStatusTable
    {
        private static Table BuildTable()
        {
            var table = new Table { Border = TableBorder.Square };

            table.AddColumn(new TableColumn("scenario"));
            table.AddColumn(new TableColumn("step"));
            table.AddColumn(new TableColumn("load simulation"));
            table.AddColumn(new TableColumn("ok latency (ms)"));
            table.AddColumn(new TableColumn("fail latency (ms)"));
            table.AddColumn(new TableColumn("ok data transfer (KB)"));

            return table;
        }

        /// <summary>
        /// The load generator's own health, beside the scenario numbers. Live, this is the
        /// column that says whether a sagging throughput is the target's fault or the
        /// generator's, which is the whole reason the runtime metrics exist.
        /// </summary>
        private static Table BuildMetricsTable()
        {
            var table = new Table { Border = TableBorder.Square };

            table.AddColumn(new TableColumn("metric"));
            table.AddColumn(new TableColumn("current"));
            table.AddColumn(new TableColumn("min"));
            table.AddColumn(new TableColumn("mean"));
            table.AddColumn(new TableColumn("max"));

            table.Caption = new TableTitle("load generator");

            return table;
        }

        private static void RenderMetricsTable(Table table, IReadOnlyList<MetricStats> metrics)
        {
            table.Rows.Clear();

            foreach (var metric in metrics)
            {
                var unit = string.IsNullOrEmpty(metric.Unit) ? "" : $" {metric.Unit}";
                var isCounter = metric.Kind == MetricKind.Counter;

                table.AddRow(
                    ConsoleRender.EscapeMarkup(metric.Name),
                    $"{ConsoleRender.BlueColor(metric.Current)}{unit}",
                    isCounter ? "" : $"{ConsoleRender.BlueColor(metric.Min)}{unit}",
                    isCounter ? "" : $"{ConsoleRender.BlueColor(metric.Mean)}{unit}",
                    isCounter ? "" : $"{ConsoleRender.BlueColor(metric.Max)}{unit}");
            }
        }

        private static void RenderTable(Table table, IReadOnlyList<ScenarioStats> scenariosStats)
        {
            table.Rows.Clear();

            foreach (var scnStats in scenariosStats)
            {
                var simulation = scnStats.LoadSimulationStats.SimulationName == "pause"
                    ? "pause"
                    : $"{scnStats.LoadSimulationStats.SimulationName}: {ConsoleRender.BlueColor(scnStats.LoadSimulationStats.Value)}";

                foreach (var stepStats in scnStats.StepStats)
                {
                    var okR = stepStats.Ok.Request;
                    var okL = stepStats.Ok.Latency;
                    var data = stepStats.Ok.DataTransfer;
                    var failR = stepStats.Fail.Request;
                    var failL = stepStats.Fail.Latency;

                    table.AddRow(
                        ConsoleRender.EscapeMarkup(scnStats.ScenarioName),
                        ConsoleRender.EscapeMarkup(stepStats.StepName),
                        simulation,
                        $"ok: {ConsoleRender.OkColor(okR.Count)}, RPS: {ConsoleRender.OkColor(okR.RPS)}, "
                        + $"p50 = {ConsoleRender.OkColor(okL.Percent50)}, p99 = {ConsoleRender.OkColor(okL.Percent99)}",
                        $"fail: {ConsoleRender.ErrorColor(failR.Count)}, RPS: {ConsoleRender.ErrorColor(failR.RPS)}, "
                        + $"p50 = {ConsoleRender.ErrorColor(failL.Percent50)}, p99 = {ConsoleRender.ErrorColor(failL.Percent99)}",
                        $"min: {ConsoleRender.BlueColor(Converter.FromBytesToKb(data.MinBytes))}, "
                        + $"max: {ConsoleRender.BlueColor(Converter.FromBytesToKb(data.MaxBytes))}, "
                        + $"all: {ConsoleRender.BlueColor(Converter.FromBytesToMb(data.AllBytes))} MB");
                }
            }
        }

        /// <summary>
        /// How long the table should say the run will take, or null when the plan cannot say -
        /// which is the case as soon as one scenario is counted in iterations rather than timed.
        /// </summary>
        private static TimeSpan? GetMaxScnDuration(bool isWarmUp, IReadOnlyList<ScenarioScheduler> scnSchedulers)
        {
            if (isWarmUp) return ScenarioFactory.GetMaxWarmUpDuration(scnSchedulers.Select(x => x.Scenario));

            if (scnSchedulers.Any(x => x.Scenario.HasCountedSimulations)) return null;

            return ScenarioFactory.GetMaxDuration(scnSchedulers.Select(x => x.Scenario));
        }

        private static TableTitle DurationTitle(TimeSpan elapsed, TimeSpan? maxDuration) =>
            maxDuration is { } max
                ? new TableTitle($"duration: ({elapsed:hh\\:mm\\:ss} - {max:hh\\:mm\\:ss})")
                : new TableTitle($"duration: ({elapsed:hh\\:mm\\:ss})");

        /// <summary>
        /// One interval's numbers as plain log lines, for when there is no terminal to draw a
        /// table on.
        /// </summary>
        /// <remarks>
        /// A CI log is the case that matters most and the one a live table serves worst: it
        /// scrolls, it has no cursor to move, and a redrawn table becomes hundreds of
        /// near-identical frames. So the same information goes out one line per scenario,
        /// through the ordinary logger, which is also what keeps it in the log file.
        /// </remarks>
        public static void PrintIntervalProgress(
            IGlobalDependency dep, TimeSpan elapsed, IReadOnlyList<ScenarioStats> scenariosStats)
        {
            if (dep.ApplicationType == ApplicationType.Console) return;

            foreach (var scn in scenariosStats)
            {
                var ok = scn.Ok.Request;
                var fail = scn.Fail.Request;
                var latency = scn.Ok.Latency;

                dep.LogInfo(
                    $"[{elapsed:hh\\:mm\\:ss}] {scn.ScenarioName}: "
                    + $"{scn.LoadSimulationStats.SimulationName} {scn.LoadSimulationStats.Value}, "
                    + $"ok {ok.Count} ({ok.RPS}/s), fail {fail.Count} ({fail.RPS}/s), "
                    + $"p50 {latency.Percent50} ms, p99 {latency.Percent99} ms");
            }
        }

        public static void Display(
            IGlobalDependency dep,
            CancellationToken cancelToken,
            bool isWarmUp,
            IReadOnlyList<ScenarioScheduler> scnSchedulers)
        {
            if (dep.ApplicationType != ApplicationType.Console) return;

            var maxDuration = GetMaxScnDuration(isWarmUp, scnSchedulers);
            var table = BuildTable();
            table.Caption = new TableTitle("real-time stats table");

            var metricsTable = BuildMetricsTable();

            var liveTable = AnsiConsole.Live(new Rows(table, metricsTable));
            liveTable.AutoClear = false;
            liveTable.Overflow = VerticalOverflow.Ellipsis;
            liveTable.Cropping = VerticalOverflowCropping.Bottom;

            // Nothing else may write to the terminal until the table comes down; log lines
            // raised in the meantime are replayed underneath it rather than through it.
            ConsoleRender.BeginLiveDisplay();

            var stopWatch = Stopwatch.StartNew();
            var refreshTableCounter = 0;

            _ = liveTable.StartAsync(async ctx =>
            {
                while (!cancelToken.IsCancellationRequested)
                {
                    try
                    {
                        var currentTime = stopWatch.Elapsed;
                        var withinPlan = maxDuration is null || currentTime <= maxDuration;

                        if (withinPlan && refreshTableCounter == 0)
                        {
                            RenderTable(table, scnSchedulers.Select(x => x.ConsoleScenarioStats).ToArray());

                            // The registry's own snapshot, not the manager's: closing an
                            // interval belongs to the reporting manager, and doing it here
                            // would take the numbers out of the timeline behind the report.
                            RenderMetricsTable(metricsTable, dep.Metrics.Registry.Global());
                        }

                        if (withinPlan)
                        {
                            table.Title = DurationTitle(currentTime, maxDuration);
                            ctx.Refresh();
                        }

                        await Task.Delay(1_000, cancelToken).ConfigureAwait(false);

                        refreshTableCounter++;
                        if (refreshTableCounter >= Constants.ConsoleRefreshTableCounter) refreshTableCounter = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        // The run finished; fall out of the loop and draw the final frame.
                    }
                    catch (Exception ex)
                    {
                        refreshTableCounter = 1;
                        dep.Logger.ZLogCritical($"{ex}");
                    }
                }

                table.Title = DurationTitle(maxDuration ?? stopWatch.Elapsed, maxDuration);
                ctx.Refresh();
            }).ContinueWith(
                static _ => ConsoleRender.EndLiveDisplay(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
