using System;
using System.Linq;
using ShopAssist;

internal static class CalendarTests
{
    private static void Main()
    {
        foreach (int today in new[] { 1, 2, 7, 8, 14, 15, 365 })
        {
            Reset(today);
            var days = DoorCalendar.Read(today);
            int first = today - (today - 1) % 7;
            Check(days.Count == 14 && days.Select(day => day.Number).SequenceEqual(Enumerable.Range(first, 14)),
                "Fourteen dates start on this week's Monday: " + today);
        }

        Reset(5);
        var calendar = DoorCalendar.Read(5);
        Check(Has(calendar, 7, "rent_day") && Has(calendar, 14, "rent_day") && !Has(calendar, 6, "rent_day"),
            "Rent is projected on future Sundays only");
        Check(calendar.SelectMany(d => d.Events).Where(e => e.Id == "rent_day").All(e => e.Event == null && e.Name == "event_rent_due"),
            "Projected rent is read-only and localized");

        PlayerStore.Instance.isPayingMortgage = true;
        calendar = DoorCalendar.Read(5);
        Check(Has(calendar, 7, "mortgage_payment") && !Has(calendar, 7, "rent_day"), "Mortgage replaces rent");
        PlayerStore.Instance.isPayingMortgage = false; PlayerStore.Instance.IsPropertyPaid = true;
        calendar = DoorCalendar.Read(5);
        Check(!calendar.SelectMany(d => d.Events).Any(e => e.Id == "rent_day" || e.Id == "mortgage_payment"), "Paid property has no due event");

        Reset(1); PlayerStore.Instance.chemist = true;
        calendar = DoorCalendar.Read(1);
        Check(Has(calendar, 1, "cartel_visit") && Has(calendar, 8, "cartel_visit") && !Has(calendar, 7, "cartel_visit"),
            "Chemist visits are Mondays");
        PlayerStore.Instance.isCartelExited = true;
        Check(!DoorCalendar.Read(1).SelectMany(d => d.Events).Any(e => e.Id == "cartel_visit"), "Exited cartel has no projected visit");

        Reset(5);
        var manager = StoreStation.Instance.storeEventManager;
        var known = new StoreEvent { identifier = "rent_day", displayName = "actual rent", startDay = 7, IsKnown = true };
        manager.future.Add(known); manager.futureDay.Add(2);
        var ordinary = new StoreEvent { identifier = "shortage", displayName = "known shortage", startDay = 6, totalDuration = 3, effect = "+5%" };
        manager.future.Add(ordinary); manager.futureDay.Add(1);
        manager.future.Add(new StoreEvent { identifier = "secret", displayName = "unknown", startDay = 7, IsKnown = false }); manager.futureDay.Add(2);
        manager.future.Add(new StoreEvent { identifier = "cosmetic", displayName = "cosmetic", startDay = 7, Cosmetic = true }); manager.futureDay.Add(2);
        calendar = DoorCalendar.Read(5);
        var rent = calendar.Single(d => d.Number == 7).Events.Where(e => e.Id == "rent_day").ToArray();
        Check(rent.Length == 1 && ReferenceEquals(rent[0].Event, known) && rent[0].Name == "actual rent", "Known native event takes priority");
        Check(!calendar.SelectMany(d => d.Events).Any(e => e.Id == "secret" || e.Id == "cosmetic"), "Unknown and cosmetic events stay hidden");
        Check(calendar.Single(d => d.Number == 6).Events.Single(e => e.Id == "shortage").DayType == StoreEventManager.EventDayType.FirstDay &&
            calendar.Single(d => d.Number == 7).Events.Single(e => e.Id == "shortage").DayType == StoreEventManager.EventDayType.MiddleDay &&
            calendar.Single(d => d.Number == 8).Events.Single(e => e.Id == "shortage").DayType == StoreEventManager.EventDayType.LastDay,
            "Multi-day native event retains its start, middle and end");
        Check(manager.active.Count == 0 && manager.future.Count == 4 && manager.futureDay.SequenceEqual(new[] { 2, 1, 2, 2 }),
            "Reading the door does not mutate native event queues");

        Reset(5); manager = StoreStation.Instance.storeEventManager;
        manager.future.Add(new StoreEvent { identifier = "rent_day", startDay = 7, IsKnown = false }); manager.futureDay.Add(2);
        calendar = DoorCalendar.Read(5);
        Check(!Has(calendar, 7, "rent_day") && Has(calendar, 14, "rent_day"), "A hidden queued event is not disclosed");
        Reset(7); manager = StoreStation.Instance.storeEventManager;
        manager.active.Add(new StoreEvent { identifier = "rent_day", startDay = 7, IsKnown = false });
        calendar = DoorCalendar.Read(7);
        Check(!Has(calendar, 7, "rent_day") && !Has(calendar, 14, "rent_day"), "A hidden active event is not disclosed");

        Console.WriteLine("Door calendar tests passed.");
    }

    private static bool Has(System.Collections.Generic.List<DoorCalendar.Day> days, int day, string id) =>
        days.Single(d => d.Number == day).Events.Any(e => e.Id == id);
    private static void Reset(int today)
    {
        PlayerStore.Instance = new PlayerStore();
        StoreStation.Instance = new StoreStation { dayCounter = today };
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
