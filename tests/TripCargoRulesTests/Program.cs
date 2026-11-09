using System;
using System.Collections.Generic;
using ShopAssist;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
Check(TripCargoRules.MigrateMode(false, 1) == 0 && TripCargoRules.MigrateMode(true, 1) == 1 &&
    TripCargoRules.MigrateMode(true, 2) == 2 && TripCargoRules.MigrateMode(true, 0) == 0 &&
    TripCargoRules.MigrateMode(true, null) == 2 && TripCargoRules.MigrateMode(null, null) == 0,
    "Legacy disabled and explicit modes must survive; enabling the old manual feature must never imply unloading old goods.");
Check(TripCargoRules.CanUnload(CargoOrigin.New, 5, 5) && !TripCargoRules.CanUnload(CargoOrigin.Old, 5, 5) &&
    TripCargoRules.CanUnload(CargoOrigin.Old, 5, 5, true) && !TripCargoRules.CanUnload(CargoOrigin.Unknown, 5, 5, true) &&
    !TripCargoRules.CanUnload(CargoOrigin.New, 5, 6, true), "Unknown origin and changed stacks cannot become confirmed cargo.");
Check(TripCargoRules.Merge(CargoOrigin.Old, CargoOrigin.New) == CargoOrigin.Unknown &&
    !TripCargoRules.CanTrustGround(false, false, CargoOrigin.New), "Mixed stacks and interrupted tracking must remain uncertain.");
Check(TripCargoRules.ContainedOrigin(true, CargoOrigin.Old, true, true) == CargoOrigin.Old &&
    TripCargoRules.ContainedOrigin(true, CargoOrigin.Unknown, true, true) == CargoOrigin.Unknown &&
    TripCargoRules.ContainedOrigin(false, CargoOrigin.Unknown, true, true) == CargoOrigin.New &&
    TripCargoRules.ContainedOrigin(false, CargoOrigin.Unknown, false, true) == CargoOrigin.Unknown &&
    TripCargoRules.ContainedOrigin(false, CargoOrigin.Unknown, true, false) == CargoOrigin.Unknown,
    "Visible contents from the ground become new while known and unobservable origins are preserved.");
Check(TripCargoRules.ExposeCargoDetails(true, true, false) && TripCargoRules.ExposeCargoDetails(true, false, true) &&
    !TripCargoRules.ExposeCargoDetails(true, false, false) && !TripCargoRules.ExposeCargoDetails(false, true, true),
    "Confirmed or visible cargo remains unloadable; unobserved and missing items do not expose details.");
Check(TripCargoRules.NeedsTripInspection(true, true) && TripCargoRules.NeedsTripInspection(false, false) &&
    !TripCargoRules.NeedsTripInspection(true, false),
    "Resolving a container or missing-source transfer must retain the entire trip for inspection, even if children moved during recovery.");
foreach (int mode in new[] { 1, 2 })
{
    Check(!TripCargoRules.ShouldUnload(mode, false, true, false, true, false), "A blocked transition must defer unloading.");
    Check(TripCargoRules.ShouldUnload(mode, false, true, true, true, false), "Clearing a temporary block must permit the retained request.");
    Check(!TripCargoRules.ShouldUnload(mode, true, true, true, true, false) &&
        !TripCargoRules.ShouldUnload(mode, false, true, true, true, true), "Returning and uncertain records must not unload.");
}
Check(!TripCargoRules.ShouldUnload(0, false, true, true, true, false), "Off must never unload.");
var stockRule = new RoutingRule { location = "root:stock", receive = true, categories = new List<string> { "FOOD" } };
var food = new[] { "FOOD" };
var tools = new[] { "TOOLS" };
Check(RoutingRules.AllowsReceiving(null, tools, true) && RoutingRules.AllowsReceiving(null, tools, false),
    "An area without a rule retains its existing default permission.");
Check(RoutingRules.AllowsReceiving(stockRule, food, false) && !RoutingRules.AllowsReceiving(stockRule, tools, false),
    "Ordinary receiving still requires a matching public category.");
Check(RoutingRules.AllowsReceiving(stockRule, tools, true) && RoutingRules.AllowsReceiving(stockRule, null, true),
    "An enabled main-stock fallback accepts an unmatched or unknown category.");
stockRule.receive = false;
Check(!RoutingRules.AllowsReceiving(stockRule, food, true) && !RoutingRules.AllowsReceiving(stockRule, tools, true),
    "Explicitly disabling receiving blocks the main-stock fallback even when the category matches.");
stockRule.receive = true;
Check(RoutingRules.AllowsReceiving(stockRule, tools, true) && !RoutingRules.AllowsReceiving(stockRule, tools, false),
    "The unmatched exception applies only to the explicitly selected fallback path.");
Console.WriteLine("PASS trip cargo migration, provenance, automatic return safety and receiving rules");

namespace Newtonsoft.Json { [AttributeUsage(AttributeTargets.Field)] public sealed class JsonRequiredAttribute : Attribute { } }
