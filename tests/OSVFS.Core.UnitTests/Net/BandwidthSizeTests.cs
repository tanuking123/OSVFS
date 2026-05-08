using OSVFS.Net;
using Xunit;

namespace OSVFS.Core.UnitTests.Net;

/// <summary>
/// Covers <see cref="BandwidthSize.Parse"/>'s suffix table, alias forms, and
/// disabled-/invalid-input behaviors.
/// </summary>
public class BandwidthSizeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0K")]
    public void Parse_returns_null_for_disabled_input(string? raw)
    {
        Assert.Null(BandwidthSize.Parse(raw));
    }

    [Theory]
    [InlineData("500", 500L)]
    [InlineData("500B", 500L)]
    [InlineData("1K", 1024L)]
    [InlineData("5K", 5L * 1024)]
    [InlineData("5KiB", 5L * 1024)]
    [InlineData("5KB", 5L * 1024)]
    [InlineData("5M", 5L * 1024 * 1024)]
    [InlineData("5MiB", 5L * 1024 * 1024)]
    [InlineData("10M", 10L * 1024 * 1024)]
    [InlineData("1G", 1L * 1024 * 1024 * 1024)]
    [InlineData("1GiB", 1L * 1024 * 1024 * 1024)]
    [InlineData("2g", 2L * 1024 * 1024 * 1024)]
    public void Parse_recognizes_known_suffixes(string raw, long expected)
    {
        Assert.Equal(expected, BandwidthSize.Parse(raw));
    }

    [Fact]
    public void Parse_accepts_decimal_values()
    {
        Assert.Equal((long)Math.Round(1.5 * 1024 * 1024), BandwidthSize.Parse("1.5M"));
    }

    [Fact]
    public void Parse_trims_whitespace()
    {
        Assert.Equal(5L * 1024 * 1024, BandwidthSize.Parse("  5M  "));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5M")]
    [InlineData("5X")]
    [InlineData("M")]
    public void Parse_throws_on_garbage(string raw)
    {
        Assert.Throws<FormatException>(() => BandwidthSize.Parse(raw));
    }
}
