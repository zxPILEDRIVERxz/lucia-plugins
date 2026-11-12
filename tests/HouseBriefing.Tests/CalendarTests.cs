namespace HouseBriefing.Tests;

/// <summary>
/// The calendar section: today's events, one compact clause. Timed events sort by start
/// time and read "dentist at 1 PM"; all-day events read "yoga (all day)" and sort last;
/// an event currently in progress is marked. At most five are listed — the true count is
/// kept for the honest lead sentence. No events = null line (the section is omitted, not
/// announced). Time labels come pre-formatted from the glue layer (it owns time zones);
/// core only selects, orders, caps, and assembles.
/// </summary>
public sealed class CalendarTests
{
    // "Now" is local noon-ish; today's local day spans 05:00Z..06:00Z+24h (US/Eastern in winter).
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TodayStart = new(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TodayEnd = new(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);

    private static HouseBriefingCore.CalEvent Ev(
        string summary,
        int startHour = 0, int startMinute = 0,
        int endHour = -1, int endMinute = 0,
        bool allDay = false,
        string? label = null) => new(
        summary,
        TodayStart.AddHours(startHour).AddMinutes(startMinute),
        endHour < 0 ? TodayStart.AddHours(startHour).AddMinutes(startMinute).AddHours(1) : TodayStart.AddHours(endHour).AddMinutes(endMinute),
        allDay,
        label);

    private static HouseBriefingCore.CalendarResult Calendar(params HouseBriefingCore.CalEvent[] events)
        => HouseBriefingCore.BuildCalendarLine(events, Now, TodayStart, TodayEnd);

    [Fact]
    public void TimedEventToday_IsListedWithItsLabel()
    {
        var result = Calendar(Ev("dentist", 8, label: "1 PM"));

        Assert.Equal(1, result.Total);
        Assert.Equal("dentist at 1 PM", result.Line);
    }

    [Fact]
    public void AllDayEvent_IsWordedAllDay()
    {
        var result = Calendar(Ev("yoga", allDay: true));

        Assert.Equal(1, result.Total);
        Assert.Equal("yoga (all day)", result.Line);
    }

    [Fact]
    public void OngoingEvent_IsMarkedInProgress()
    {
        // An event straddling Now with no usable time label: started 30 min ago, ends in 45.
        var ev = new HouseBriefingCore.CalEvent("gym", Now.AddMinutes(-30), Now.AddMinutes(45), false, null);

        var result = Calendar(ev);

        Assert.Equal(1, result.Total);
        Assert.Equal("gym (in progress)", result.Line);
    }

    [Fact]
    public void TimedInProgress_KeepsItsTime()
    {
        var ev = new HouseBriefingCore.CalEvent("standup", Now.AddMinutes(-10), Now.AddMinutes(20), false, "11:50 AM");

        var result = Calendar(ev);

        Assert.Equal("standup at 11:50 AM (in progress)", result.Line);
    }

    [Fact]
    public void EventEndingBeforeToday_IsExcluded()
    {
        var ev = new HouseBriefingCore.CalEvent("yesterday", TodayStart.AddHours(-4), TodayStart.AddMinutes(-30), false, "late");

        Assert.Equal(0, Calendar(ev).Total);
    }

    [Fact]
    public void EventStartingTomorrow_IsExcluded()
    {
        var ev = Ev("tomorrow", 24, label: "9 AM"); // todayEnd

        Assert.Equal(0, Calendar(ev).Total);
    }

    [Theory]
    [InlineData(true)]   // starts exactly at midnight (local) — it is today
    [InlineData(false)]  // starts exactly at end of day — it is tomorrow
    public void BoundaryStarts_AreHalfOpen(bool included)
    {
        var ev = new HouseBriefingCore.CalEvent(
            "edge",
            included ? TodayStart : TodayEnd,
            (included ? TodayStart : TodayEnd).AddHours(1),
            false, "t");

        Assert.Equal(included ? 1 : 0, Calendar(ev).Total);
    }

    [Fact]
    public void Ordering_IsTimedByStartThenAllDayLast()
    {
        var result = Calendar(
            Ev("yoga", allDay: true),
            Ev("dentist", 8, label: "1 PM"),
            Ev("gym", 6, label: "11 AM"));

        Assert.Equal(3, result.Total);
        Assert.Equal("gym at 11 AM, dentist at 1 PM, yoga (all day)", result.Line);
    }

    [Fact]
    public void MoreThanFive_ListsOnlyFirstFiveButKeepsTrueCount()
    {
        var events = Enumerable.Range(0, 7)
            .Select(i => Ev($"e{i}", i, label: $"{i + 1} PM"))
            .ToArray();

        var result = Calendar(events);

        Assert.Equal(7, result.Total);
        Assert.Equal("e0 at 1 PM, e1 at 2 PM, e2 at 3 PM, e3 at 4 PM, e4 at 5 PM", result.Line);
    }

    [Fact]
    public void NoEvents_IsNullLineForOmission()
    {
        var result = Calendar();

        Assert.Equal(0, result.Total);
        Assert.Null(result.Line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSummary_FallsBackToCalendarEvent(string summary)
    {
        var ev = new HouseBriefingCore.CalEvent(summary, TodayStart.AddHours(2), TodayStart.AddHours(3), false, "7 AM");

        Assert.Equal("calendar event at 7 AM", Calendar(ev).Line);
    }

    // ── briefing integration ───────────────────────────────────────

    [Fact]
    public void Briefing_SitsBetweenHouseAndWeather()
    {
        var house = new HouseBriefingCore.HouseReport(10, []);
        var calendar = new HouseBriefingCore.CalendarResult(2, "gym at 6 AM, dentist at 2 PM");

        var text = HouseBriefingCore.FormatBriefing(house, calendar, "high 55°, low 40°");

        Assert.Equal(
            "The house looks quiet — 10 entities reporting normally. On the calendar: gym at 6 AM, dentist at 2 PM. Today: high 55°, low 40°.",
            text);
    }

    [Fact]
    public void Briefing_MoreThanCaped_UsesHonestLead()
    {
        var house = new HouseBriefingCore.HouseReport(10, []);
        var calendar = new HouseBriefingCore.CalendarResult(7, "a at 1 PM, b at 2 PM, c at 3 PM, d at 4 PM, e at 5 PM");

        var text = HouseBriefingCore.FormatBriefing(house, calendar, null);

        Assert.Equal(
            "The house looks quiet — 10 entities reporting normally. 7 things on the calendar today — starting with a at 1 PM, b at 2 PM, c at 3 PM, d at 4 PM, e at 5 PM.",
            text);
    }

    [Fact]
    public void Briefing_CalendarFailed_IsNamedPlainly()
    {
        var house = new HouseBriefingCore.HouseReport(10, []);

        var text = HouseBriefingCore.FormatBriefing(house, HouseBriefingCore.CalendarResult.Failure, null);

        Assert.Contains("The calendar section is missing — the calendar service was unreachable.", text);
    }

    [Fact]
    public void Briefing_NoEvents_OmitsSectionEntirely()
    {
        var house = new HouseBriefingCore.HouseReport(10, []);
        var empty = new HouseBriefingCore.CalendarResult(0, null);

        var text = HouseBriefingCore.FormatBriefing(house, empty, "high 60°, low 45°");

        Assert.Equal("The house looks quiet — 10 entities reporting normally. Today: high 60°, low 45°.", text);
    }

    [Fact]
    public void Briefing_EverythingMissing_StillOneHonestSentence()
    {
        var text = HouseBriefingCore.FormatBriefing(null, HouseBriefingCore.CalendarResult.Failure, null);

        Assert.Equal(
            "The briefing came up empty — Home Assistant and the weather service are both unreachable right now.",
            text);
    }
}
