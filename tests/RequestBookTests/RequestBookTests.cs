using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ShopAssist;

internal static class RequestBookTests
{
    private static void Need(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static ItemGrid.Entry Entry(int grid, GameItem item) => ItemGrid.Views[grid].Entries.Single(entry => entry.Item == item);
    private static void Select(int grid, GameItem item) => ItemGrid.Views[grid].Options.OnClick(Entry(grid, item));

    private static void Main()
    {
        var settings = new ShopSettings();
        var book = new RequestBook(settings, message => { });
        book.Start();
        settings.Set("f27.enabled", true);
        var source = new ShopInventory.Location { Name = "source", Id = "root:stock", Inventory = new GameInventory() };
        var target = new ShopInventory.Location { Name = "target", Id = "item:10:contents:-1", Inventory = new GameInventory(), Owner = new GameItem { uniqueId = 10 } };
        var later = new ShopInventory.Location { Name = "later", Id = "item:11:contents:-1", Inventory = new GameInventory(), Owner = new GameItem { uniqueId = 11 }, Open = false };
        ShopInventory.Locations.AddRange(new[] { source, target, later });
        var first = new GameItem { uniqueId = 1, name = "first", identifier = "goods", parentInventory = source.Inventory };
        var second = new GameItem { uniqueId = 2, name = "second", identifier = "goods", parentInventory = source.Inventory };
        var third = new GameItem { uniqueId = 3, name = "third", identifier = "goods", parentInventory = later.Inventory };
        source.Inventory.childItems.AddRange(new[] { first, second });
        later.Inventory.childItems.Add(third);
        var request = new StoreRequest { id = "order", sourceEventId = "source", requiredItem = "goods",
            requesterName = "requester", title = "order", requiredItemCount = 2, duration = 2 };
        PlayerStore.Instance.storeRequests.Add(request);
        typeof(RequestBook).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, new object[] { request });
        UiLayout.Window.Click("track");
        Need(VisualChoices.Choices().Count == 1, "Closed containers appeared as target choices");
        VisualChoices.Change(target);
        Select(1, first);
        Select(1, second);
        ShopInventory.NextMoveFailure = "inv.noSpace";
        UiLayout.Window.Click("prepare");
        Need(RequestBook.Reserved(first) && RequestBook.Reserved(second), "Failed move did not retain its saved reservation");
        Need(first.parentInventory == source.Inventory && second.parentInventory == target.Inventory,
            "Unexpected results after one move failed");
        Need(Entry(0, first).Badge == "f27.awaiting_move" && Entry(0, second).Badge == "f27.ready",
            "Reservation cards did not distinguish goods still needing migration");
        Need(ShopNotice.Result.Items.Any(item => item.ItemId == first.uniqueId && item.Reason == "inv.noSpace"),
            "Failed move did not produce its operation result");
        using (var saved = JsonDocument.Parse(ModHelper.GetModData(ModEntry.Id, "requests", null)))
        {
            var root = saved.RootElement;
            var reservations = root.GetProperty("orders")[0].GetProperty("reserved").EnumerateArray().ToArray();
            Need(root.GetProperty("version").GetInt32() == 2 && reservations.Length == 2 &&
                reservations.All(item => item.EnumerateObject().Count() == 1 && item.GetProperty("id").GetInt32() > 0),
                "Saved reservations did not use the current canonical model");
        }
        typeof(RequestBook).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, null);
        Need(RequestBook.Reserved(first) && RequestBook.Reserved(second), "Current reservations were lost after a reload");
        typeof(RequestBook).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, new object[] { request });

        Select(0, first);
        AttentionBook.Inspections.Add(first.uniqueId);
        UiLayout.Window.Click("release_selected");
        Need(RequestBook.Reserved(first) && AttentionBook.Opened, "An unresolved inspection did not block release");
        AttentionBook.Inspections.Clear();
        ModHelper.FailReadAfterWrite = true;
        UiLayout.Window.Click("release_selected");
        Need(!RequestBook.Reserved(first) && RequestBook.Reserved(second),
            "Release readback failure did not reload the saved result or retained the wrong item");
        Need(first.parentInventory == source.Inventory && second.parentInventory == target.Inventory,
            "Releasing a reservation moved goods");

        Select(1, first);
        later.Open = true;
        Need(VisualChoices.Choices().Count == 2, "Open container was not added to dynamic destination choices");
        VisibleInventoryViews.NotifyChanged();
        UnityEngine.Time.unscaledTime += 1;
        book.Tick();
        Need(Entry(1, first).Selected && Entry(1, third) != null, "Source refresh lost the selection or omitted newly opened goods");

        ShopInventory.NextMoveFailure = "inv.noSpace";
        UiLayout.Window.Click("prepare");
        Need(RequestBook.Reserved(first), "Retry fixture did not retain its source reservation");
        UiLayout.Window.Click("migrate");
        int previewBuilds = UiLayout.RebuildCount;
        VisibleInventoryViews.NotifyChanged();
        UnityEngine.Time.unscaledTime += 1;
        book.Tick();
        Need(UiLayout.RebuildCount == previewBuilds, "A container event replaced the migration preview");
        first.unitCount++;
        UiLayout.Window.Click("migrate_apply");
        Need(first.parentInventory == source.Inventory && ShopNotice.Result.Items.Any(item => item.Reason == "inv.changed"),
            "Migration did not reject a changed preview item");

        Select(0, first);
        ModHelper.FailWrite = true;
        UiLayout.Window.Click("release_selected");
        Need(RequestBook.Reserved(first) && RequestBook.Reserved(second), "Failed release write discarded a reservation");

        Select(0, first);
        UiLayout.Window.Click("release_selected");
        Select(1, first);
        ModHelper.FailReadAfterWrite = true;
        UiLayout.Window.Click("prepare");
        Need(RequestBook.Reserved(first) && first.parentInventory == source.Inventory &&
            ShopNotice.Result.Items.Any(item => item.ItemId == first.uniqueId && item.Reason == "f27.save_failed"),
            "An uncertain pre-move reservation was lost or allowed the item to move");

        const string damaged = "unreadable reservation data";
        ModHelper.SetModData(ModEntry.Id, "requests", damaged);
        typeof(RequestBook).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, null);
        var recovery = AttentionBook.Providers["f27"]().Single();
        Need(recovery.Resolve != null && RequestBook.Reserved(first), "Unreadable reservations must stay blocked and offer recovery");
        recovery.Resolve();
        Need(ModHelper.GetModData(ModEntry.Id, "requests", null) == damaged, "Opening the recovery prompt changed the original record");
        ModHelper.FailWrite = true;
        UiLayout.Window.Click("confirm");
        Need(ShopNotice.Last == "f27.reset_failed" && RequestBook.Reserved(first) &&
            ModHelper.GetModData(ModEntry.Id, "requests", null) == damaged,
            "A failed recovery must retain the original record and keep reservations blocked");
        AttentionBook.Inspections.Add(first.uniqueId);
        ModHelper.FailReadAfterKeyWrite = "requests";
        UiLayout.Window.Click("confirm");
        Need(ShopNotice.Last == "f27.reset_failed" && RequestBook.Reserved(first) &&
            ModHelper.GetModData(ModEntry.Id, "requests", null) == damaged &&
            ModHelper.GetModData(ModEntry.Id, "requestsUnreadableBackup", null) == damaged,
            "An uncertain readback must restore the original record and remain blocked");
        UiLayout.Window.Click("confirm");
        Need(!RequestBook.Reserved(first) && AttentionBook.Inspections.Contains(first.uniqueId) &&
            first.parentInventory == source.Inventory && ShopNotice.Last == "f27.reset_done",
            "Confirmed recovery must not move goods or clear independent inspections");
        AttentionBook.Inspections.Clear();

        ModHelper.SetModData(ModEntry.Id, "requests", damaged);
        typeof(RequestBook).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(book, null);
        AttentionBook.Providers["f27"]().Single().Resolve();
        ModHelper.SetModData(ModEntry.Id, "requests", "different unreadable data");
        UiLayout.Window.Click("confirm");
        Need(ModHelper.GetModData(ModEntry.Id, "requests", null) == "different unreadable data" && ShopNotice.Last == "inv.changed",
            "A changed record must not be overwritten by a stale recovery prompt");
        ModHelper.FailNextRead();
        recovery = AttentionBook.Providers["f27"]().Single();
        Need(recovery.Resolve == null && recovery.Retry != null,
            "A storage read error must allow retry without offering to overwrite unknown data");
        recovery.Retry();
        Need(AttentionBook.Providers["f27"]().Single().Resolve != null,
            "A successful retry that identifies invalid data must offer confirmed recovery");
        book.Dispose();
        Console.WriteLine("PASS reservation recovery, unreadable-data reset, inspection guard, dynamic containers, and migration revalidation");
    }
}
