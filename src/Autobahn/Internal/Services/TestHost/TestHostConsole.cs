using System.Diagnostics;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Infra;
using Autobahn.Internal.Services.Reports;
using Autobahn.Stats;

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

            var liveTable = AnsiConsole.Live(table);
            liveTable.AutoClear = false;
            liveTable.Overflow = VerticalOverflow.Ellipsis;
            liveTable.Cropping = VerticalOverflowCropping.Bottom;

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
                            RenderTable(table, scnSchedulers.Select(x => x.ConsoleScenarioStats).ToArray());

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
            });
        }
    }
}
