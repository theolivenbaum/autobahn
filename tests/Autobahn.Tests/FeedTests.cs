using Autobahn.Feeds;
using Autobahn.Internal.Domain.Feeds;

namespace Autobahn.Tests;

internal class CircularFeedTests
{
    [Test]
    public async Task Every_item_is_used_before_any_is_reused()
    {
        var feed = Feed.Circular("ids", [1, 2, 3, 4]);

        var first = Enumerable.Range(0, 4).Select(_ => feed.Next()).ToArray();
        var second = Enumerable.Range(0, 4).Select(_ => feed.Next()).ToArray();

        await Assert.That(first).IsEquivalentTo(new[] { 1, 2, 3, 4 });
        await Assert.That(second).IsEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task Concurrent_readers_between_them_see_every_item_exactly_once_per_lap()
    {
        var items = Enumerable.Range(0, 1_000).ToArray();
        var feed = Feed.Circular("ids", items);
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1_000; i++) seen.Add(feed.Next());
        });

        // Eight laps of a thousand items: every item exactly eight times, whoever got it.
        var counts = seen.GroupBy(x => x).Select(g => g.Count()).Distinct().ToArray();

        await Assert.That(seen.Count).IsEqualTo(8_000);
        await Assert.That(counts).IsEquivalentTo(new[] { 8 });
    }

    [Test]
    public async Task A_feed_set_to_fail_says_so_rather_than_quietly_repeating_itself()
    {
        var feed = Feed.Circular("ids", [1, 2], FeedExhaustion.Fail);

        feed.Next();
        feed.Next();

        var ex = Assert.Throws<FeedExhaustedException>(() => feed.Next());

        await Assert.That(ex!.FeedName).IsEqualTo("ids");
        await Assert.That(ex.ItemCount).IsEqualTo(2);
        await Assert.That(ex.Message).Contains("ids");
    }

    [Test]
    public async Task An_empty_feed_is_refused_at_construction_rather_than_on_the_first_iteration()
    {
        var ex = Assert.Throws<AutobahnException>(() => Feed.Circular<int>("ids", []));

        await Assert.That(ex!.Message).Contains("ids");
        await Assert.That(ex.Message).Contains("at least one");
    }

    [Test]
    public async Task A_feed_needs_a_name()
    {
        await Assert.That(Assert.Throws<AutobahnException>(() => Feed.Circular(" ", [1]))).IsNotNull();
    }
}

internal class ConstantAndRandomFeedTests
{
    [Test]
    public async Task A_constant_feed_hands_every_iteration_the_same_item()
    {
        var feed = Feed.Constant("ids", [1, 2, 3]);

        var seen = Enumerable.Range(0, 50).Select(_ => feed.Next()).Distinct().ToArray();

        await Assert.That(seen.Length).IsEqualTo(1);
        await Assert.That(new[] { 1, 2, 3 }).Contains(seen[0]);
    }

    [Test]
    public async Task A_random_feed_stays_inside_its_items_and_never_exhausts()
    {
        var feed = Feed.Random("ids", [1, 2, 3]);

        var seen = Enumerable.Range(0, 500).Select(_ => feed.Next()).ToArray();

        await Assert.That(seen.All(x => x is >= 1 and <= 3)).IsTrue();
        await Assert.That(seen.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task A_seeded_random_feed_replays_the_same_sequence()
    {
        var items = Enumerable.Range(0, 100).ToArray();

        var first = Enumerable.Range(0, 50).Select(_ => Feed.Random("ids", items, seed: 42)).ToArray();

        var a = Feed.Random("ids", items, seed: 7);
        var b = Feed.Random("ids", items, seed: 7);

        var seqA = Enumerable.Range(0, 50).Select(_ => a.Next()).ToArray();
        var seqB = Enumerable.Range(0, 50).Select(_ => b.Next()).ToArray();

        await Assert.That(seqA).IsEquivalentTo(seqB);
        await Assert.That(first.Length).IsEqualTo(50);
    }
}

internal class BatchFeedTests
{
    [Test]
    public async Task A_batch_feed_hands_out_groups_in_order()
    {
        var feed = Feed.Batch("ids", [1, 2, 3, 4, 5, 6], batchSize: 2);

        await Assert.That(feed.BatchSize).IsEqualTo(2);
        await Assert.That(feed.Next()).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(feed.Next()).IsEquivalentTo(new[] { 3, 4 });
        await Assert.That(feed.Next()).IsEquivalentTo(new[] { 5, 6 });
        await Assert.That(feed.Next()).IsEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task The_last_batch_is_short_rather_than_dropped_or_padded()
    {
        var feed = Feed.Batch("ids", [1, 2, 3, 4, 5], batchSize: 2);

        var batches = Enumerable.Range(0, 3).Select(_ => feed.Next()).ToArray();

        await Assert.That(batches[2]).IsEquivalentTo(new[] { 5 });
        await Assert.That(batches.SelectMany(x => x)).IsEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public async Task A_batch_bigger_than_the_dataset_is_one_short_batch()
    {
        var feed = Feed.Batch("ids", [1, 2], batchSize: 10);

        await Assert.That(feed.Next()).IsEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task A_batch_size_below_one_is_refused()
    {
        var ex = Assert.Throws<AutobahnException>(() => Feed.Batch("ids", [1, 2], batchSize: 0));

        await Assert.That(ex!.Message).Contains("at least 1");
    }

    [Test]
    public async Task A_batch_feed_set_to_fail_stops_after_its_last_batch()
    {
        var feed = Feed.Batch("ids", [1, 2, 3], batchSize: 2, onExhausted: FeedExhaustion.Fail);

        feed.Next();
        feed.Next();

        await Assert.That(Assert.Throws<FeedExhaustedException>(() => feed.Next())).IsNotNull();
    }
}

internal class StreamingFeedTests
{
    private static IEnumerable<int> Source(int count)
    {
        for (var i = 0; i < count; i++) yield return i;
    }

    [Test]
    public async Task A_streaming_feed_pulls_items_lazily_and_in_order()
    {
        var opened = 0;

        var feed = Feed.Streaming("rows", () =>
        {
            opened++;
            return Source(4);
        });

        var seen = Enumerable.Range(0, 4).Select(_ => feed.Next()).ToArray();

        await Assert.That(seen).IsEquivalentTo(new[] { 0, 1, 2, 3 });
        await Assert.That(opened).IsEqualTo(1);
    }

    [Test]
    public async Task Restarting_a_streaming_feed_reopens_its_source()
    {
        var opened = 0;

        var feed = Feed.Streaming("rows", () =>
        {
            opened++;
            return Source(2);
        });

        var seen = Enumerable.Range(0, 5).Select(_ => feed.Next()).ToArray();

        await Assert.That(seen).IsEquivalentTo(new[] { 0, 1, 0, 1, 0 });
        await Assert.That(opened).IsEqualTo(3);
    }

    [Test]
    public async Task A_streaming_feed_set_to_fail_says_how_many_it_served()
    {
        var feed = Feed.Streaming("rows", () => Source(3), FeedExhaustion.Fail);

        feed.Next();
        feed.Next();
        feed.Next();

        var ex = Assert.Throws<FeedExhaustedException>(() => feed.Next());

        await Assert.That(ex!.ItemCount).IsEqualTo(3);
    }

    [Test]
    public async Task A_source_that_reopens_empty_fails_instead_of_looping_forever()
    {
        var opened = 0;

        var feed = Feed.Streaming("rows", () => opened++ == 0 ? Source(1) : Source(0));

        feed.Next();

        await Assert.That(Assert.Throws<FeedExhaustedException>(() => feed.Next())).IsNotNull();
    }

    [Test]
    public async Task Concurrent_readers_of_a_streaming_feed_between_them_see_every_item_once()
    {
        var feed = Feed.Streaming("rows", () => Source(4_000), FeedExhaustion.Fail);
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 500; i++) seen.Add(feed.Next());
        });

        await Assert.That(seen.Count).IsEqualTo(4_000);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(4_000);
    }
}

internal class FeedSourceTests
{
    private static string WriteTemp(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"autobahn_feed_{Guid.NewGuid():N}_{name}");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task A_csv_source_reads_rows_keyed_by_the_header()
    {
        var path = WriteTemp("users.csv", "id,name,city\n1,Ada,London\n2,Grace,New York\n");

        try
        {
            var rows = FeedSource.FromCsv(path);

            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0]["name"]).IsEqualTo("Ada");
            await Assert.That(rows[1]["city"]).IsEqualTo("New York");

            // Header lookup is case-insensitive: a config file's casing is not the test's problem.
            await Assert.That(rows[0]["ID"]).IsEqualTo("1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_csv_source_handles_quoted_fields_commas_and_doubled_quotes()
    {
        var path = WriteTemp("quoted.csv", "id,note\n1,\"a, b\"\n2,\"she said \"\"hi\"\"\"\n3,\n");

        try
        {
            var rows = FeedSource.FromCsv(path);

            await Assert.That(rows[0]["note"]).IsEqualTo("a, b");
            await Assert.That(rows[1]["note"]).IsEqualTo("she said \"hi\"");
            await Assert.That(rows[2]["note"]).IsEqualTo("");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_csv_source_can_map_rows_as_it_reads_them()
    {
        var path = WriteTemp("users.csv", "id,name\n1,Ada\n2,Grace\n");

        try
        {
            var names = FeedSource.FromCsv(path, row => row["name"]);

            await Assert.That(names).IsEquivalentTo(new[] { "Ada", "Grace" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_streaming_csv_source_reopens_the_file_when_the_feed_restarts()
    {
        var path = WriteTemp("users.csv", "id,name\n1,Ada\n2,Grace\n");

        try
        {
            var feed = Feed.Streaming("users", FeedSource.StreamCsv(path, row => row["name"]));

            var seen = Enumerable.Range(0, 5).Select(_ => feed.Next()).ToArray();

            await Assert.That(seen).IsEquivalentTo(new[] { "Ada", "Grace", "Ada", "Grace", "Ada" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_json_source_reads_an_array_of_objects()
    {
        var path = WriteTemp("users.json", """[{"Name":"Ada","Age":36},{"Name":"Grace","Age":45}]""");

        try
        {
            var users = FeedSource.FromJson<TestUser>(path);

            await Assert.That(users.Count).IsEqualTo(2);
            await Assert.That(users[0].Name).IsEqualTo("Ada");
            await Assert.That(users[1].Age).IsEqualTo(45);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_streaming_json_source_reads_the_same_array_lazily()
    {
        var path = WriteTemp("users.json", """[{"Name":"Ada","Age":36},{"Name":"Grace","Age":45}]""");

        try
        {
            var feed = Feed.Streaming("users", FeedSource.StreamJson<TestUser>(path), FeedExhaustion.Fail);

            await Assert.That(feed.Next().Name).IsEqualTo("Ada");
            await Assert.That(feed.Next().Name).IsEqualTo("Grace");
            await Assert.That(Assert.Throws<FeedExhaustedException>(() => feed.Next())).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_missing_source_file_says_which_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "autobahn_definitely_not_here.csv");

        var ex = Assert.Throws<AutobahnException>(() => FeedSource.FromCsv(missing));

        await Assert.That(ex!.Message).Contains("autobahn_definitely_not_here.csv");
    }

    public sealed record TestUser
    {
        public required string Name { get; init; }
        public required int Age { get; init; }
    }
}
