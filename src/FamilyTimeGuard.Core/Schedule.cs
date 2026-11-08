namespace FamilyTimeGuard.Core;

/// <summary>Bedtime and budget calculations, all in the family's local time.</summary>
public static class Schedule
{
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);

    /// <summary>The rule for the night that ends on the morning of <paramref name="morning"/>.</summary>
    public static NightRule NightBefore(GuardConfig cfg, ChildProfile child, DateOnly morning) =>
        cfg.IsWeekendLike(morning) ? child.WeekendNights : child.SchoolNights;

    public static DailyBudget BudgetFor(GuardConfig cfg, ChildProfile child, DateOnly day) =>
        cfg.IsWeekendLike(day) ? child.WeekendBudget : child.SchoolDayBudget;

    /// <summary>Is the computer supposed to be locked right now?</summary>
    public static bool IsBedtime(GuardConfig cfg, ChildProfile child, DateTime local)
    {
        var today = DateOnly.FromDateTime(local);
        var t = local.TimeOfDay;

        // Early morning: still inside the night that ends this morning?
        var lastNight = NightBefore(cfg, child, today);
        if (t < lastNight.Unlock)
        {
            if (lastNight.Lock >= Noon) return true;      // locked since yesterday evening
            if (t >= lastNight.Lock) return true;         // lock time was after midnight (e.g. 00:30)
        }

        // Evening: has tonight's lock time passed?
        var tonight = NightBefore(cfg, child, today.AddDays(1));
        return tonight.Lock >= Noon && t >= tonight.Lock;
    }

    /// <summary>The next moment the computer will lock (local time). Null if it is bedtime already.</summary>
    public static DateTime? NextBedtime(GuardConfig cfg, ChildProfile child, DateTime local)
    {
        if (IsBedtime(cfg, child, local)) return null;
        var today = DateOnly.FromDateTime(local);
        DateTime? best = null;
        for (var d = -1; d <= 2; d++)
        {
            var evening = today.AddDays(d);
            var rule = NightBefore(cfg, child, evening.AddDays(1));
            var lockDay = rule.Lock >= Noon ? evening : evening.AddDays(1);
            var candidate = lockDay.ToDateTime(TimeOnly.FromTimeSpan(rule.Lock));
            if (candidate > local && (best is null || candidate < best)) best = candidate;
        }
        return best;
    }

    /// <summary>When bedtime ends (local time). Null if it is not bedtime.</summary>
    public static DateTime? UnlockTime(GuardConfig cfg, ChildProfile child, DateTime local)
    {
        if (!IsBedtime(cfg, child, local)) return null;
        var today = DateOnly.FromDateTime(local);
        var morning = local.TimeOfDay < NightBefore(cfg, child, today).Unlock ? today : today.AddDays(1);
        return morning.ToDateTime(TimeOnly.FromTimeSpan(NightBefore(cfg, child, morning).Unlock));
    }

    /// <summary>The next morning unlock time after <paramref name="local"/> (used for "/lock" without a duration).</summary>
    public static DateTime NextMorningUnlock(GuardConfig cfg, ChildProfile child, DateTime local)
    {
        var today = DateOnly.FromDateTime(local);
        var todayUnlock = today.ToDateTime(TimeOnly.FromTimeSpan(NightBefore(cfg, child, today).Unlock));
        if (local < todayUnlock) return todayUnlock;
        var tomorrow = today.AddDays(1);
        return tomorrow.ToDateTime(TimeOnly.FromTimeSpan(NightBefore(cfg, child, tomorrow).Unlock));
    }
}
