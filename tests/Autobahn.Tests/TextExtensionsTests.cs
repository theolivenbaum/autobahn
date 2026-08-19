using Autobahn.Internal;

namespace Autobahn.Tests;

public class TextExtensionsTests
{
    [Test]
    public async Task Strings_are_joined_with_a_comma()
    {
        await Assert.That(new[] { "foo", "bar", "baz" }.ConcatWithComma()).IsEqualTo("foo, bar, baz");
        await Assert.That(new[] { "foo" }.ConcatWithComma()).IsEqualTo("foo");
        await Assert.That(Array.Empty<string>().ConcatWithComma()).IsEqualTo("");
    }

    [Test]
    public async Task Duplicates_are_reported_once_each_in_first_seen_order()
    {
        var duplicates = new[] { "a", "b", "a", "c", "b", "a" }.FilterDuplicates().ToArray();

        await Assert.That(duplicates).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task A_set_with_no_duplicates_reports_none()
    {
        await Assert.That(new[] { "a", "b", "c" }.FilterDuplicates()).IsEmpty();
    }

    [Test]
    [Arguments(0L, 0.0)]
    [Arguments(1024L, 1.0)]
    [Arguments(1536L, 1.5)]
    public async Task Bytes_convert_to_kilobytes(long bytes, double expected)
    {
        await Assert.That(Converter.FromBytesToKb(bytes)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_duration_rounds_down_to_whole_seconds_for_display()
    {
        var duration = new TimeSpan(0, 1, 2, 3, 456);

        await Assert.That(Converter.RoundDuration(duration)).IsEqualTo(new TimeSpan(0, 1, 2, 3));
    }
}
