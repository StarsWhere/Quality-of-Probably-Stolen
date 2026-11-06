using System;
using System.Collections.Generic;

namespace ShopAssist
{
    internal static class DoorCalendar
    {
        internal sealed class Entry
        {
            internal string Id, Name, Detail;
            internal StoreEvent Event;
            internal StoreEventManager.EventDayType DayType;
        }

        internal sealed class Day
        {
            internal int Number;
            internal readonly List<Entry> Events = new List<Entry>();
        }

        internal static List<Day> Read(int today)
        {
            var result = new List<Day>();
            var manager = StoreStation.Instance.storeEventManager;
            PlayerStore store = PlayerStore.Instance;
            int first = today - (today - 1) % 7;
            for (int number = first; number < first + 14; number++)
            {
                var day = new Day { Number = number };
                foreach (StoreEvent item in manager.GetActiveEventOnDay(number, true, true))
                {
                    if (item == null) continue;
                    day.Events.Add(new Entry
                    {
                        Id = item.identifier, Name = item.GetDisplayName(), Event = item,
                        Detail = item.GetEventPriceEffect(),
                        DayType = StoreEventManager.GetEventDayType(item, number)
                    });
                }
                // Only project future obligations from the current state.  Never expose a
                // hidden native event with the same identifier or mutate the event queue.
                if (number >= today)
                {
                    if (number % 7 == 0)
                    {
                        if (store.IsPayingRent()) AddVirtual(day, manager, today, "rent_day", "event_rent_due");
                        else if (store.isPayingMortgage) AddVirtual(day, manager, today, "mortgage_payment", "event_mortgage_payment");
                    }
                    if (number % 7 == 1 && store.IsChemistStart() && !store.isCartelExited)
                        AddVirtual(day, manager, today, "cartel_visit", "event_cartel_visit");
                }
                result.Add(day);
            }
            return result;
        }

        private static void AddVirtual(Day day, StoreEventManager manager, int today, string id, string key)
        {
            if (day.Events.Exists(e => e.Id == id) ||
                (manager.IsEventActive(id) && !manager.GetActiveEventOnDay(today, true, true).Exists(e => e.identifier == id)) ||
                manager.IsEventActiveInDay(id, day.Number - today)) return;
            day.Events.Add(new Entry { Id = id, Name = LocHelper.GetLocalizedMechanic(key),
                Detail = "", DayType = StoreEventManager.EventDayType.StartAndFinish });
        }
    }
}
