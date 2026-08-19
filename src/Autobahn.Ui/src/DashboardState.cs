using System;
using System.Collections.Generic;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui
{
    /// <summary>
    /// Everything the views read, as observables the frames feed.
    /// </summary>
    /// <remarks>
    /// One place holds the run's state and the components subscribe to the parts they care
    /// about, so a frame arriving appends a point to the charts that are bound to it and
    /// nothing else re-renders. That is what keeps a five-second tick from rebuilding the page.
    ///
    /// The charts are handed <c>ChartSeries[]</c> observables rather than raw arrays because a
    /// live run's X axis grows: an observable of values alone would keep re-plotting new points
    /// against the old positions. Building the series here also means the per-scenario charts
    /// and the overview charts are fed by the same pass over the same frames.
    /// </remarks>
    public sealed class DashboardState
    {
        private readonly List<LiveFrame> _frames = new List<LiveFrame>();
        private readonly Dictionary<string, ScenarioSeries> _scenarios = new Dictionary<string, ScenarioSeries>();

        public DashboardState()
        {
            Connected = SettableObservable.For(false);
            Run = SettableObservable.For(new RunDescriptor());
            Latest = SettableObservable.For<LiveFrame>(null);
            Reports = SettableObservable.For(new ReportDescriptor[0]);
            Paused = SettableObservable.For(false);
            WallClock = SettableObservable.For(false);

            Throughput = Series();
            Latency = Series();
            Load = Series();
            Processor = Series();
            Memory = Series();
            ThreadPool = Series();
            Sockets = Series();
            StatusCodes = Series();

            Errors = SettableObservable.For(new ErrorGroup[0]);
            Thresholds = SettableObservable.For(new ThresholdFrame[0]);

            Logs = new ObservableList<LogLine>();
            LogLevel = SettableObservable.For("");
            LogSearch = SettableObservable.For("");
            ErrorSearch = SettableObservable.For("");
        }

        public SettableObservable<bool> Connected { get; }
        public SettableObservable<RunDescriptor> Run { get; }
        public SettableObservable<LiveFrame> Latest { get; }
        public SettableObservable<ReportDescriptor[]> Reports { get; }

        /// <summary>
        /// Whether the live view is frozen.
        /// </summary>
        /// <remarks>
        /// Frames keep arriving and keep being kept while this is set - only the observables the
        /// charts read stop being written. Unpausing publishes everything that happened in the
        /// meantime, because a paused chart that lost its data would be a worse tool than one
        /// that moved under the cursor.
        /// </remarks>
        public SettableObservable<bool> Paused { get; }

        /// <summary>False for elapsed-since-start on the time axis, true for time of day.</summary>
        public SettableObservable<bool> WallClock { get; }

        public SettableObservable<ChartSeries[]> Throughput { get; }
        public SettableObservable<ChartSeries[]> Latency { get; }
        public SettableObservable<ChartSeries[]> Load { get; }
        public SettableObservable<ChartSeries[]> Processor { get; }
        public SettableObservable<ChartSeries[]> Memory { get; }
        public SettableObservable<ChartSeries[]> ThreadPool { get; }
        public SettableObservable<ChartSeries[]> Sockets { get; }
        public SettableObservable<ChartSeries[]> StatusCodes { get; }

        /// <summary>Failures grouped by what they were, newest state, ordered by count.</summary>
        public SettableObservable<ErrorGroup[]> Errors { get; }

        /// <summary>Where each threshold stands, as of the most recent frame that checked it.</summary>
        public SettableObservable<ThresholdFrame[]> Thresholds { get; }

        public ObservableList<LogLine> Logs { get; }

        /// <summary>The one level the log tail is showing, or empty for all of them.</summary>
        public SettableObservable<string> LogLevel { get; }

        /// <summary>The text the log tail is filtered by.</summary>
        public SettableObservable<string> LogSearch { get; }

        /// <summary>The text the failure list is filtered by.</summary>
        public SettableObservable<string> ErrorSearch { get; }

        /// <summary>The highest sequence number seen, so a gap is detectable.</summary>
        public double LastSequence { get; private set; }

        /// <summary>The frame before the latest, for the delta a KPI tile shows.</summary>
        public LiveFrame Previous { get; private set; }

        /// <summary>Every frame so far, oldest first.</summary>
        public IReadOnlyList<LiveFrame> Frames => _frames;

        /// <summary>Replaces everything with a snapshot from the host.</summary>
        public void Apply(RunSnapshot snapshot)
        {
            Run.Value = snapshot.Run;
            Reports.Value = snapshot.Reports ?? new ReportDescriptor[0];

            _frames.Clear();
            LastSequence = 0;
            Previous = null;

            if (snapshot.History != null)
            {
                for (var i = 0; i < snapshot.History.Length; i++) AppendCore(snapshot.History[i]);
            }

            if (snapshot.Latest != null && snapshot.Latest.Sequence > LastSequence)
                AppendCore(snapshot.Latest);

            Publish();
        }

        /// <summary>Adds one frame and tells whatever is watching.</summary>
        public void Append(LiveFrame frame)
        {
            // Backfill and the live socket can both deliver the same frame; the sequence
            // number is what makes that harmless rather than a duplicated chart point.
            if (frame.Sequence <= LastSequence) return;

            AppendCore(frame);

            if (!Paused.Value) Publish();
        }

        /// <summary>Freezes or resumes the live view, catching up on resume.</summary>
        public void SetPaused(bool paused)
        {
            Paused.Value = paused;
            if (!paused) Publish();
        }

        private void AppendCore(LiveFrame frame)
        {
            Previous = _frames.Count > 0 ? _frames[_frames.Count - 1] : null;

            _frames.Add(frame);
            LastSequence = frame.Sequence;

            if (frame.Logs == null) return;

            for (var i = 0; i < frame.Logs.Length; i++)
            {
                Logs.Add(frame.Logs[i]);

                // A long run's log is unbounded and a browser's memory is not.
                if (Logs.Count > 500) Logs.RemoveAt(0);
            }
        }

        /// <summary>The x positions every series is plotted against, in the axis currently chosen.</summary>
        public double[] Axis()
        {
            var wall = WallClock.Value;
            var axis = new double[_frames.Count];

            for (var i = 0; i < _frames.Count; i++)
            {
                // Unix *seconds* for wall clock: that is what the chart's own time axis reads.
                axis[i] = wall ? _frames[i].TimestampEpochMs / 1000.0 : _frames[i].ElapsedSeconds;
            }

            return axis;
        }

        /// <summary>One value per frame, for a sparkline or a series.</summary>
        public double[] Points(Func<LiveFrame, double> read)
        {
            var points = new double[_frames.Count];

            for (var i = 0; i < _frames.Count; i++) points[i] = read(_frames[i]);

            return points;
        }

        /// <summary>A running total, one point per frame, for a tile that counts rather than rates.</summary>
        public double[] Cumulative(Func<LiveFrame, double> read)
        {
            var points = new double[_frames.Count];
            var total = 0.0;

            for (var i = 0; i < _frames.Count; i++)
            {
                total += read(_frames[i]);
                points[i] = total;
            }

            return points;
        }

        /// <summary>The live series for one scenario, created the first time it is asked for.</summary>
        public ScenarioSeries Scenario(string scenarioName)
        {
            ScenarioSeries series;

            if (!_scenarios.TryGetValue(scenarioName, out series))
            {
                series = new ScenarioSeries();
                _scenarios[scenarioName] = series;

                // Created mid-run, so it starts with everything that already happened rather
                // than waiting for the next interval to have anything to draw.
                PublishScenario(scenarioName, series);
            }

            return series;
        }

        private void Publish()
        {
            var axis = Axis();

            Throughput.Value = new[]
            {
                new ChartSeries("ok/sec", axis, Points(f => Sum(f, s => s.Ok.Rps)), Theme.Colors.Green600),
                new ChartSeries("failed/sec", axis, Points(f => Sum(f, s => s.Fail.Rps)), Theme.Colors.Red500)
            };

            Latency.Value = new[]
            {
                new ChartSeries("p50", axis, Points(f => Worst(f, s => s.Ok.P50Ms)), Theme.Colors.Blue400),
                new ChartSeries("p75", axis, Points(f => Worst(f, s => s.Ok.P75Ms)), Theme.Colors.Blue600),
                new ChartSeries("p95", axis, Points(f => Worst(f, s => s.Ok.P95Ms)), Theme.Colors.Orange600),
                new ChartSeries("p99", axis, Points(f => Worst(f, s => s.Ok.P99Ms)), Theme.Colors.Red600)
            };

            Load.Value = new[]
            {
                new ChartSeries("scheduled", axis, Points(f => Sum(f, s => s.ScheduledCopies)), Theme.Colors.Purple400),
                new ChartSeries("actual", axis, Points(f => Sum(f, s => s.ActualCopies)), Theme.Colors.Purple700)
            };

            Processor.Value = new[]
            {
                new ChartSeries("cpu %", axis, Points(f => Metric(f, "runtime.cpu")), Theme.Colors.Teal600)
            };

            Memory.Value = new[]
            {
                new ChartSeries("working set", axis, Points(f => Metric(f, "runtime.working_set")), Theme.Colors.Blue600),
                new ChartSeries("gc heap", axis, Points(f => Metric(f, "runtime.gc_heap")), Theme.Colors.Magenta600)
            };

            ThreadPool.Value = new[]
            {
                new ChartSeries("queue", axis, Points(f => Metric(f, "runtime.threadpool_queue")), Theme.Colors.Orange600),
                new ChartSeries("threads", axis, Points(f => Metric(f, "runtime.threadpool_threads")), Theme.Colors.Neutral600)
            };

            Sockets.Value = new[]
            {
                new ChartSeries("bytes sent", axis, Points(f => Metric(f, "runtime.socket_sent")), Theme.Colors.Green600),
                new ChartSeries("bytes received", axis, Points(f => Metric(f, "runtime.socket_received")), Theme.Colors.Blue600)
            };

            StatusCodes.Value = StatusSeries(axis);
            Errors.Value = ErrorGroups();
            Thresholds.Value = LatestThresholds();

            foreach (var entry in _scenarios) PublishScenario(entry.Key, entry.Value);

            Latest.Value = _frames.Count > 0 ? _frames[_frames.Count - 1] : null;
        }

        private void PublishScenario(string scenarioName, ScenarioSeries series)
        {
            var axis = Axis();

            series.Throughput.Value = new[]
            {
                new ChartSeries("ok/sec", axis, Points(f => Of(f, scenarioName, s => s.Ok.Rps)), Theme.Colors.Green600),
                new ChartSeries("failed/sec", axis, Points(f => Of(f, scenarioName, s => s.Fail.Rps)), Theme.Colors.Red500)
            };

            series.Latency.Value = new[]
            {
                new ChartSeries("p50", axis, Points(f => Of(f, scenarioName, s => s.Ok.P50Ms)), Theme.Colors.Blue400),
                new ChartSeries("p75", axis, Points(f => Of(f, scenarioName, s => s.Ok.P75Ms)), Theme.Colors.Blue600),
                new ChartSeries("p95", axis, Points(f => Of(f, scenarioName, s => s.Ok.P95Ms)), Theme.Colors.Orange600),
                new ChartSeries("p99", axis, Points(f => Of(f, scenarioName, s => s.Ok.P99Ms)), Theme.Colors.Red600)
            };

            series.Load.Value = new[]
            {
                new ChartSeries("scheduled", axis, Points(f => Of(f, scenarioName, s => s.ScheduledCopies)), Theme.Colors.Purple400),
                new ChartSeries("actual", axis, Points(f => Of(f, scenarioName, s => s.ActualCopies)), Theme.Colors.Purple700)
            };
        }

        /// <summary>
        /// One series per status code seen, over time.
        /// </summary>
        /// <remarks>
        /// Every code gets a value in every interval, zero included: a stacked bar chart with
        /// ragged series would stack the wrong things on top of each other.
        /// </remarks>
        private ChartSeries[] StatusSeries(double[] axis)
        {
            var codes = new List<string>();
            var isError = new Dictionary<string, bool>();

            for (var i = 0; i < _frames.Count; i++)
            {
                var scenarios = _frames[i].Scenarios;
                if (scenarios == null) continue;

                for (var s = 0; s < scenarios.Length; s++)
                {
                    var statuses = scenarios[s].StatusCodes;
                    if (statuses == null) continue;

                    for (var c = 0; c < statuses.Length; c++)
                    {
                        var code = Label(statuses[c]);

                        if (!isError.ContainsKey(code))
                        {
                            isError[code] = statuses[c].IsError;
                            codes.Add(code);
                        }
                    }
                }
            }

            var series = new ChartSeries[codes.Count];

            for (var c = 0; c < codes.Count; c++)
            {
                var code = codes[c];
                var values = new double[_frames.Count];

                for (var i = 0; i < _frames.Count; i++) values[i] = CountOf(_frames[i], code);

                series[c] = new ChartSeries(
                    code, axis, values, isError[code] ? Theme.Colors.Red500 : null);
            }

            return series;
        }

        /// <summary>Every failure seen so far, grouped by what it was.</summary>
        private ErrorGroup[] ErrorGroups()
        {
            var groups = new Dictionary<string, ErrorGroup>();
            var ordered = new List<ErrorGroup>();

            for (var i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                var scenarios = frame.Scenarios;
                if (scenarios == null) continue;

                for (var s = 0; s < scenarios.Length; s++)
                {
                    var statuses = scenarios[s].StatusCodes;
                    if (statuses == null) continue;

                    for (var c = 0; c < statuses.Length; c++)
                    {
                        var status = statuses[c];
                        if (!status.IsError || status.Count == 0) continue;

                        var key = scenarios[s].ScenarioName + " " + Label(status);
                        ErrorGroup group;

                        if (!groups.TryGetValue(key, out group))
                        {
                            group = new ErrorGroup
                            {
                                ScenarioName = scenarios[s].ScenarioName,
                                StatusCode = status.StatusCode,
                                Message = status.Message,
                                FirstSeenSeconds = frame.ElapsedSeconds
                            };

                            groups[key] = group;
                            ordered.Add(group);
                        }

                        group.Count += status.Count;
                        group.LastSeenSeconds = frame.ElapsedSeconds;
                        group.Intervals.Add(frame.ElapsedSeconds);
                    }
                }
            }

            var total = 0;
            for (var i = 0; i < ordered.Count; i++) total += ordered[i].Count;

            var result = ordered.ToArray();

            for (var i = 0; i < result.Length; i++) result[i].Share = total == 0 ? 0 : (double)result[i].Count / total;

            Array.Sort(result, (a, b) => b.Count.CompareTo(a.Count));

            return result;
        }

        /// <summary>The most recent state of every threshold, from whichever frame last carried it.</summary>
        private ThresholdFrame[] LatestThresholds()
        {
            var byName = new Dictionary<string, ThresholdFrame>();
            var ordered = new List<string>();

            for (var i = 0; i < _frames.Count; i++)
            {
                var thresholds = _frames[i].Thresholds;
                if (thresholds == null) continue;

                for (var t = 0; t < thresholds.Length; t++)
                {
                    var key = Key(thresholds[t]);
                    if (!byName.ContainsKey(key)) ordered.Add(key);

                    byName[key] = thresholds[t];
                }
            }

            var result = new ThresholdFrame[ordered.Count];
            for (var i = 0; i < ordered.Count; i++) result[i] = byName[ordered[i]];

            return result;
        }

        /// <summary>
        /// How one threshold stood in every interval, for the pass/fail strip.
        /// </summary>
        /// <remarks>
        /// A threshold that passed, failed for a minute and recovered reads at a glance from
        /// this and not at all from its final verdict, which is the whole reason the frames keep
        /// their threshold states rather than only the run's last word on them.
        /// </remarks>
        public ThresholdFrame[] ThresholdHistory(ThresholdFrame threshold)
        {
            var key = Key(threshold);
            var history = new List<ThresholdFrame>();

            for (var i = 0; i < _frames.Count; i++)
            {
                var thresholds = _frames[i].Thresholds;
                if (thresholds == null) continue;

                for (var t = 0; t < thresholds.Length; t++)
                {
                    if (Key(thresholds[t]) == key) history.Add(thresholds[t]);
                }
            }

            return history.ToArray();
        }

        private static string Key(ThresholdFrame threshold) => threshold.ScenarioName + " " + threshold.Name;

        private static string Label(StatusCodeFrame status) =>
            string.IsNullOrEmpty(status.StatusCode) ? status.Message : status.StatusCode;

        private static double CountOf(LiveFrame frame, string code)
        {
            var total = 0.0;
            if (frame.Scenarios == null) return total;

            for (var s = 0; s < frame.Scenarios.Length; s++)
            {
                var statuses = frame.Scenarios[s].StatusCodes;
                if (statuses == null) continue;

                for (var c = 0; c < statuses.Length; c++)
                {
                    if (Label(statuses[c]) == code) total += statuses[c].Count;
                }
            }

            return total;
        }

        public static double Sum(LiveFrame frame, Func<ScenarioFrame, double> read)
        {
            var total = 0.0;
            if (frame == null || frame.Scenarios == null) return total;

            for (var i = 0; i < frame.Scenarios.Length; i++) total += read(frame.Scenarios[i]);

            return total;
        }

        /// <summary>
        /// The worst value across scenarios, which for a percentile is the only honest summary.
        /// </summary>
        /// <remarks>
        /// Not a mean: averaging four scenarios' p95s hides the one that is in trouble behind
        /// the three that are not, and the reason to look at a p95 at all is to find that one.
        /// </remarks>
        public static double Worst(LiveFrame frame, Func<ScenarioFrame, double> read)
        {
            var highest = 0.0;
            if (frame == null || frame.Scenarios == null) return highest;

            for (var i = 0; i < frame.Scenarios.Length; i++)
            {
                var value = read(frame.Scenarios[i]);
                if (value > highest) highest = value;
            }

            return highest;
        }

        public static double Of(LiveFrame frame, string scenarioName, Func<ScenarioFrame, double> read)
        {
            if (frame == null || frame.Scenarios == null) return 0;

            for (var i = 0; i < frame.Scenarios.Length; i++)
            {
                if (frame.Scenarios[i].ScenarioName == scenarioName) return read(frame.Scenarios[i]);
            }

            return 0;
        }

        public static double Metric(LiveFrame frame, string name)
        {
            if (frame == null || frame.Metrics == null) return 0;

            for (var i = 0; i < frame.Metrics.Length; i++)
            {
                if (frame.Metrics[i].Name == name) return frame.Metrics[i].Current;
            }

            return 0;
        }

        /// <summary>The run's totals so far, which no single frame carries.</summary>
        public RunTotals Totals()
        {
            var totals = new RunTotals();

            for (var i = 0; i < _frames.Count; i++)
            {
                var scenarios = _frames[i].Scenarios;
                if (scenarios == null) continue;

                for (var s = 0; s < scenarios.Length; s++)
                {
                    totals.Ok += scenarios[s].Ok.Count;
                    totals.Fail += scenarios[s].Fail.Count;
                    totals.Bytes += scenarios[s].Ok.Bytes + scenarios[s].Fail.Bytes;
                }
            }

            return totals;
        }

        private static SettableObservable<ChartSeries[]> Series() =>
            SettableObservable.For(new ChartSeries[0]);
    }

    /// <summary>One scenario's own charts, fed by the same pass that feeds the overview.</summary>
    public sealed class ScenarioSeries
    {
        public ScenarioSeries()
        {
            Throughput = SettableObservable.For(new ChartSeries[0]);
            Latency = SettableObservable.For(new ChartSeries[0]);
            Load = SettableObservable.For(new ChartSeries[0]);
        }

        public SettableObservable<ChartSeries[]> Throughput { get; }
        public SettableObservable<ChartSeries[]> Latency { get; }
        public SettableObservable<ChartSeries[]> Load { get; }
    }

    /// <summary>Every failure that was the same failure, counted together.</summary>
    public sealed class ErrorGroup
    {
        public string ScenarioName = "";
        public string StatusCode = "";
        public string Message = "";

        public int Count;
        public double Share;

        public double FirstSeenSeconds;
        public double LastSeenSeconds;

        /// <summary>
        /// Which intervals this group was active in.
        /// </summary>
        /// <remarks>
        /// Kept so the timeline strip can show it: a burst of errors confined to one thirty
        /// second window is a different problem from a steady two percent, and a total flattens
        /// one into the other.
        /// </remarks>
        public List<double> Intervals = new List<double>();

        public string Describe() => string.IsNullOrEmpty(StatusCode) ? Message : StatusCode;
    }

    /// <summary>What the run has done in total, accumulated across every interval.</summary>
    public sealed class RunTotals
    {
        public int Ok;
        public int Fail;
        public double Bytes;

        public int All => Ok + Fail;

        public double ErrorRate => All == 0 ? 0 : (double)Fail / All;
    }
}
