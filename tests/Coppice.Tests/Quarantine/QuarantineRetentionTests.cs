using Coppice.Cli;
using Xunit;

namespace Coppice.Tests.Quarantine;

/// <summary>
/// T-052: the config may move the quarantine prune default, but only a positive value. An explicit
/// --older-than always wins, and zero keeps the 30-day safety default rather than meaning "delete
/// everything older than the epoch".
/// </summary>
public sealed class QuarantineRetentionTests
{
    [Fact]
    public void A_configured_positive_default_is_used_when_the_flag_is_absent()
    {
        QuarantineFlags flags = QuarantineFlags.Parse(["prune"], TimeSpan.FromDays(7));

        Assert.Equal(TimeSpan.FromDays(7), flags.OlderThan);
    }

    [Fact]
    public void An_explicit_older_than_wins_over_the_configured_default()
    {
        QuarantineFlags flags = QuarantineFlags.Parse(["prune", "--older-than", "2d"], TimeSpan.FromDays(7));

        Assert.Equal(TimeSpan.FromDays(2), flags.OlderThan);
    }

    [Fact]
    public void No_config_default_keeps_the_thirty_day_safety_floor()
    {
        QuarantineFlags flags = QuarantineFlags.Parse(["prune"]);

        Assert.Equal(TimeSpan.FromDays(30), flags.OlderThan);
    }
}
