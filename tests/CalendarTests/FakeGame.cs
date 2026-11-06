using System.Collections.Generic;
using System.Linq;

public sealed class PlayerStore
{
    public static PlayerStore Instance;
    public bool isPayingMortgage, IsPropertyPaid, isCartelExited, chemist;
    public bool IsPayingRent() => !isPayingMortgage && !IsPropertyPaid;
    public bool IsChemistStart() => chemist;
}

public sealed class StoreStation
{
    public static StoreStation Instance;
    public int dayCounter;
    public StoreEventManager storeEventManager = new StoreEventManager();
}

public sealed class StoreEvent
{
    public string identifier, displayName, effect = "";
    public int startDay, totalDuration = 1;
    public bool IsKnown = true, Cosmetic;
    public string GetDisplayName() => displayName;
    public string GetEventPriceEffect() => effect;
}

public sealed class StoreEventManager
{
    public enum EventDayType { FirstDay, MiddleDay, LastDay, NotPartOfEvent, StartAndFinish }
    public readonly List<StoreEvent> active = new List<StoreEvent>();
    public readonly List<StoreEvent> future = new List<StoreEvent>();
    public readonly List<int> futureDay = new List<int>();

    public List<StoreEvent> GetActiveEventOnDay(int day, bool excludeCosmetic = true, bool excludeUnknown = true)
    {
        bool Visible(StoreEvent e) => (!excludeCosmetic || !e.Cosmetic) && (!excludeUnknown || e.IsKnown);
        var rows = active.Where(e => Visible(e) && day >= e.startDay && day < e.startDay + e.totalDuration).ToList();
        for (int i = 0; i < future.Count; i++)
            if (Visible(future[i]) && day >= StoreStation.Instance.dayCounter + futureDay[i] &&
                day < StoreStation.Instance.dayCounter + futureDay[i] + future[i].totalDuration) rows.Add(future[i]);
        return rows;
    }

    public bool IsEventActive(string id) => active.Any(e => e.identifier == id);
    public bool IsEventActiveInDay(string id, int offset) => future.Where((e, i) => futureDay[i] == offset).Any(e => e.identifier == id);
    public static EventDayType GetEventDayType(StoreEvent e, int day)
    {
        int last = e.startDay + e.totalDuration - 1;
        if (day < e.startDay || day > last) return EventDayType.NotPartOfEvent;
        if (e.totalDuration == 1) return EventDayType.StartAndFinish;
        if (day == e.startDay) return EventDayType.FirstDay;
        return day == last ? EventDayType.LastDay : EventDayType.MiddleDay;
    }
}

public static class LocHelper
{
    public static string GetLocalizedMechanic(string key, params object[] arguments) => key;
}
