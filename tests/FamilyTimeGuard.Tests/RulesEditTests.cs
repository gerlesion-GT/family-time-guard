using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Tests;

public class RulesEditTests
{
    [Fact]
    public void Round_trip_without_changes_reports_nothing()
    {
        var chip = GuardConfig.CreateDefault().FindChildByName("Chip")!;
        Assert.Empty(RulesDto.From(chip).ApplyTo(chip));
    }

    [Fact]
    public void Applies_changes_and_lists_them()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var dto = RulesDto.From(chip);
        dto.SchoolScreen = 180;
        dto.SchoolGames = 60;
        dto.WeekendYouTube = -1;
        dto.SchoolLockAt = "21:30";
        var changes = dto.ApplyTo(chip);

        Assert.Equal(180, chip.SchoolDayBudget.ScreenMinutes);
        Assert.Equal(60, chip.SchoolDayBudget.GamesMinutes);
        Assert.Equal(-1, chip.WeekendBudget.YouTubeMinutes);
        Assert.Equal("21:30", chip.SchoolNights.LockAt);
        Assert.Equal(4, changes.Count);
        Assert.Contains("Screen time on school days: no limit → 3h", changes);
        Assert.Contains("Bedtime on school nights: 21:00 → 21:30", changes);
        Assert.Empty(cfg.Validate());
    }

    [Theory]
    [InlineData(2000, "06:30")]
    [InlineData(-5, "06:30")]
    [InlineData(60, "13:00")]
    public void Bad_values_are_refused_and_nothing_changes(int games, string unlock)
    {
        var chip = GuardConfig.CreateDefault().FindChildByName("Chip")!;
        var dto = RulesDto.From(chip);
        dto.SchoolGames = games;
        dto.SchoolUnlockAt = unlock;
        Assert.Throws<ConfigException>(() => dto.ApplyTo(chip));
        Assert.Equal(45, chip.SchoolDayBudget.GamesMinutes);
        Assert.Equal("06:30", chip.SchoolNights.UnlockAt);
    }
}
