using NoisLogTray;

namespace NoisLogTray.Tests;

// Mirrors __tests__/time-slots.test.ts to guarantee parity with the web app.
public class TimeSlotsTests
{
    private static int TotalMinutes(IEnumerable<TimeSlot> slots)
    {
        var sum = 0;
        foreach (var s in slots)
        {
            var (sh, sm) = Parse(s.Start);
            var (eh, em) = Parse(s.End);
            sum += (eh * 60 + em) - (sh * 60 + sm);
        }
        return sum;
    }

    private static (int H, int M) Parse(string hhmm)
    {
        var parts = hhmm.Split(':');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    [Fact]
    public void ReturnsFullDayForOneTicket()
    {
        var slots = TimeSlots.Get(1, 0);
        Assert.Equal(new[] { new TimeSlot("09:00", "12:00"), new TimeSlot("13:00", "18:00") }, slots);
        Assert.Equal(480, TotalMinutes(slots));
    }

    [Fact]
    public void SplitsTwoTicketsEvenly()
    {
        var t0 = TimeSlots.Get(2, 0);
        var t1 = TimeSlots.Get(2, 1);

        Assert.Equal(240, TotalMinutes(t0));
        Assert.Equal(240, TotalMinutes(t1));
        Assert.Equal(new[] { new TimeSlot("09:00", "12:00"), new TimeSlot("13:00", "14:00") }, t0);
        Assert.Equal(new[] { new TimeSlot("14:00", "18:00") }, t1);
    }

    [Fact]
    public void SplitsThreeTicketsEvenly()
    {
        var durations = new[] { 0, 1, 2 }.Select(i => TotalMinutes(TimeSlots.Get(3, i))).ToArray();
        Assert.All(durations, d => Assert.Equal(160, d));
        Assert.Equal(480, durations.Sum());
    }

    [Fact]
    public void SplitsFourTicketsEvenly()
    {
        var durations = new[] { 0, 1, 2, 3 }.Select(i => TotalMinutes(TimeSlots.Get(4, i))).ToArray();
        Assert.All(durations, d => Assert.Equal(120, d));
        Assert.Equal(480, durations.Sum());
    }

    [Fact]
    public void SplitsFiveTicketsApproximatelyEvenly()
    {
        var durations = new[] { 0, 1, 2, 3, 4 }.Select(i => TotalMinutes(TimeSlots.Get(5, i))).ToArray();
        Assert.Equal(480, durations.Sum());
        Assert.All(durations, d => Assert.InRange(d, 90, 105));
    }

    [Fact]
    public void SplitsALunchStraddlingSlotIntoTwoRows()
    {
        var slots = TimeSlots.Get(2, 0);
        Assert.Equal(2, slots.Count);
        Assert.Equal("12:00", slots[0].End);
        Assert.Equal("13:00", slots[1].Start);
    }

    [Fact]
    public void RoundsTimesToFiveMinuteBoundaries()
    {
        for (var count = 1; count <= 5; count++)
        for (var idx = 0; idx < count; idx++)
        foreach (var slot in TimeSlots.Get(count, idx))
        {
            Assert.Equal(0, Parse(slot.Start).M % 5);
            Assert.Equal(0, Parse(slot.End).M % 5);
        }
    }

    [Fact]
    public void CoversFullWorkdayWithNoGapsForThreeTickets()
    {
        var flat = new[] { 0, 1, 2 }.SelectMany(i => TimeSlots.Get(3, i)).ToArray();
        Assert.Equal("09:00", flat[0].Start);
        Assert.Equal("18:00", flat[^1].End);

        for (var i = 1; i < flat.Length; i++)
        {
            var prevEnd = flat[i - 1].End;
            var currStart = flat[i].Start;
            if (prevEnd == "12:00") Assert.Equal("13:00", currStart);
            else Assert.Equal(prevEnd, currStart);
        }
    }

    [Fact]
    public void CustomMinutesLayOutSixThenTwoHours()
    {
        var minutes = new[] { 360, 120 };
        Assert.Equal(new[] { new TimeSlot("09:00", "12:00"), new TimeSlot("13:00", "16:00") },
            TimeSlots.Get(minutes, 0));
        Assert.Equal(new[] { new TimeSlot("16:00", "18:00") }, TimeSlots.Get(minutes, 1));
        Assert.Equal(360, TotalMinutes(TimeSlots.Get(minutes, 0)));
        Assert.Equal(120, TotalMinutes(TimeSlots.Get(minutes, 1)));
    }

    [Fact]
    public void PartialDayStopsEarlyWithoutSwallowingLunch()
    {
        // 3h + 2h = 5h: the second ticket starts exactly at the lunch boundary and must
        // map to 13:00 (not 12:00); the remaining 15:00-18:00 is simply not logged.
        var minutes = new[] { 180, 120 };
        Assert.Equal(new[] { new TimeSlot("09:00", "12:00") }, TimeSlots.Get(minutes, 0));
        Assert.Equal(new[] { new TimeSlot("13:00", "15:00") }, TimeSlots.Get(minutes, 1));
        Assert.Equal(300, TotalMinutes(TimeSlots.Get(minutes, 0)) + TotalMinutes(TimeSlots.Get(minutes, 1)));
    }

    [Fact]
    public void EvenSplitMatchesTheDefaultOverload()
    {
        Assert.Equal(new[] { 240, 240 }, TimeSlots.EvenSplit(2));
        for (var count = 1; count <= 5; count++)
        {
            Assert.Equal(480, TimeSlots.EvenSplit(count).Sum());
            for (var idx = 0; idx < count; idx++)
                Assert.Equal(TimeSlots.Get(count, idx), TimeSlots.Get(TimeSlots.EvenSplit(count), idx));
        }
    }
}
