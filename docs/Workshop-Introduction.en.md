# Quality of Probably Stolen | 更好的体验

As the shop grows, finding stock, making space and tending machines take more of your time. Quality of Probably Stolen brings those everyday jobs into a set of optional tools, leaving you more time to run the shop.

There are **41 features**, from item markers and context-menu arranging to rule-based storage, machine maintenance, stock searches and module layouts. They are divided into three tiers by how much they change the way you manage the shop.

## Choose how much help you want

| Tier | Scope | Initial state |
| --- | --- | --- |
| **Tier 1: fewer clicks, less searching, fewer missed details** | Preserves the gameplay, item ownership, trade settlement and production outcomes while improving information and manual controls. | 20 features; 8 on by default |
| **Tier 2: you set the rules, the mod handles the batch** | You still choose the goods and destinations; the mod carries out repetitive transfers, slightly changing the pace of running the shop. | 15 features; all off |
| **Tier 3: view and configure across containers and machines** | Reduces inventory searches, machine-by-machine checks and manual layout planning. Suits later-game players who want to shorten these tasks substantially. | 6 features; all off |

The eight Tier 1 features enabled initially are **container and machine names, customer demand highlights, known-risk inspection highlights, context-menu arranging, public item labels, known stolen-goods markers, group rotation and precise placement**. Turn on the other Tier 1 features when you want them.

**All Tier 2 and Tier 3 features start off.** Higher-tier options inside Tier 1 features also start off: make room on drop belongs to Tier 2, and revealing hidden inspection risks belongs to Tier 3. Enabling their parent feature does not enable these options.

Defaults below apply to a fresh configuration or a settings reset. Previously saved choices are retained. The counts refer to primary features; higher-tier suboptions are marked under their parent feature.

Game options, the Mods settings panel and feature search all use the same three tiers. Within each tier, settings are grouped by purpose: Items & storage, Trade & requests, Machines & production, Calendar & workspace and Fast playback. Higher-tier suboptions appear in their own tier and name their parent feature.

The screenshots below use the Chinese interface; English is also available in the mod.

## Tier 1: fewer clicks, less searching, fewer missed details

Preserves the gameplay, item ownership, trade settlement and production outcomes while improving information and manual controls.

Tier 1 arranging, rotation and precise placement change the layout you control by hand. Trade settlement and production outputs continue to follow the native rules.

### Show container and machine names (F02)

**On by default**

Show names on visible storage containers and machines, and in suitable untitled window headers. Default names, position and text size can be adjusted.

| Option | Default / range | What it does |
| --- | --- | --- |
| Show default container and machine names | Off | Also show default names for containers and machines without custom names. |
| Container and machine name background | Off | Add a dark background behind the name to improve contrast with the item icon. |
| Move container and machine names upward | Off | Place names near the top of items instead of the center. |
| Container and machine name size | 14 (6–24) | Set the name text size. It follows game scaling; long names shrink or truncate. |

![The custom name appears on the bag, so you can recognize it before opening it.](<images/f02.png>)

*The custom name appears on the bag, so you can recognize it before opening it.*

### Highlight goods the customer wants (F03)

**On by default**

Highlight goods matching the current customer. Green means ordinary goods, amber means stolen goods and red means contraband.

| Option | Default / range | What it does |
| --- | --- | --- |
| Do not highlight customers who buy everything | On | Do not outline every sellable item for a customer who buys everything. |
| Mark risks in customer goods | Off | Outline stolen customer goods in amber and contraband in red. |

![Milk and a bag containing suitable food are highlighted for a food-buying customer.](<images/f03.png>)

*Milk and a bag containing suitable food are highlighted for a food-buying customer.*

### Highlight known risks during inspections (F04)

**On by default**

When an inspector arrives, highlight known stolen goods, contraband and containers where they were previously seen. Choose risk categories or enable extra detection of undisclosed risks.

| Option | Default / range | What it does |
| --- | --- | --- |
| Mark stolen goods during inspections | On | With inspection highlighting enabled, mark known stolen goods and containers where they were seen. Extra detection also includes undisclosed risks. |
| Mark contraband during inspections | On | With inspection highlighting enabled, mark known contraband and containers where it was seen. Extra detection also includes undisclosed risks. |
| Detect hidden risks during inspections (Tier 3 option) | Off | With inspection highlighting enabled, also check visible items and accessible closed ordinary containers for stolen goods and contraband even if not yet identified. Risk category switches still apply. |

![During an inspection, markers distinguish known risky goods from the bag containing them.](<images/f04.png>)

*During an inspection, markers distinguish known risky goods from the bag containing them.*

### Arrange container items from the context menu (F05)

**On by default**

Right-click an owned, accessible ordinary inventory grid in the shop or an item in it and choose Smart Arrange. It arranges the selected items in that grid, or all items when there is no selection. Use the game's Pin and Unpin actions to keep items in place: pins persist with the save and also restrict ordinary movement. Arranging and making room avoid pinned and request-reserved items. Undo is available until a related inventory changes.

| Option | Default / range | What it does |
| --- | --- | --- |
| Allow item rotation during arrangement | On | Let Smart Arrange rotate eligible items to pack them more tightly. |
| Keep arrange menu open after moving away | On | Return to the menu after moving away; click outside or press Escape to close it. |
| Make room when a dragged item does not fit (Tier 2 option) | Off | Try rearranging an ordinary storage grid when a dragged item does not fit and allow the operation to be undone. |
| Arrange current container | Unbound; assign it in Key Bindings | Arrange the accessible container under the pointer, or the most recently focused accessible container when the pointer is not over one. With Smart Inventory Arrange enabled, bind the shortcut in its settings. |

![Choose Smart Arrange from the native context menu.](<images/f05.png>)

*Choose Smart Arrange from the native context menu.*

### Close item windows and manage groups (F13)

**Off by default**

Close eligible item windows in the store with one action. Near an accessible storage coordinator, save window groups and their layouts, then reopen and restore them.

| Option | Default / range | What it does |
| --- | --- | --- |
| Close item windows | Unbound; assign it in Key Bindings | Close the eligible item windows currently open. |
| Open window groups | Unbound; assign it in Key Bindings | Open or close window groups in the store near an accessible storage coordinator. |

- **Window groups:** Requires an available Storage Coordinator. Save the current group and layout, then restore it when needed.

![Save a group of windows for restocking and restore that layout when needed.](<images/f13.png>)

*Save a group of windows for restocking and restore that layout when needed.*

### Move and dock the bargain panel (F14)

**Off by default**

During bargaining, drag the panel by its title bar or close it with its close button. Choose which side of the trade window it docks to; dragged positions are saved with the current game.

| Option | Default / range | What it does |
| --- | --- | --- |
| Default bargain panel side | Automatic · Automatic / Left / Right | Choose which side of the trade window the bargain panel starts on. Automatic chooses based on available screen space. |

![Dock the bargaining panel beside the trade window or drag it to a convenient position.](<images/f14.png>)

*Dock the bargaining panel beside the trade window or drag it to a convenient position.*

### Show the two-week calendar on the shutter (F15)

**Off by default**

When the shutter is down, show dates and public events for this week and next week directly on it. Hover a date for event information, or select it to show that day's details on the door. The display moves away when the shutter opens.

- **Calendar range:** This week and next week: 14 days. Event details use publicly available native events.

![Read this week and next week on the shutter. To-dos and deadlines have their own switches.](<images/f15.png>)

*Read this week and next week on the shutter. To-dos and deadlines have their own switches.*

### Show a trade receipt after a transaction (F16)

**Off by default**

Briefly show the cash or reputation change after a transaction. Choose what appears, how long it stays and whether it makes a sound. No receipt appears when there is no selected change to show.

| Option | Default / range | What it does |
| --- | --- | --- |
| Show cash changes on trade receipts | On | Show the actual cash received or spent on the trade receipt. No amount appears when cash does not change. |
| Show reputation changes on trade receipts | On | Show changes to the customer's faction reputation on the trade receipt. No value appears when reputation does not change. |
| Play a trade receipt sound | Off | Play the game's register sound when a trade receipt appears. |
| Trade receipt sound volume (%) | 50 (0–100) | Adjust the trade receipt sound relative to game volume. Zero mutes it; higher values make it louder. |
| Trade receipt duration (seconds) | 4 (1–12) | Set how long the trade receipt stays on screen; higher values keep it visible longer. |

![Cash and faction reputation changes appear briefly near the trade.](<images/f16.png>)

*Cash and faction reputation changes appear briefly near the trade.*

### Show a checklist on the shutter (F17)

**Off by default**

Tick tasks on the closed shutter. While trading, click the housing above the customer counter to open the note. Each task has one schedule: once, daily, day or night. Use the + on the note or shutter to add and edit tasks; tasks are saved with the current game.

| Option | Default / range | What it does |
| --- | --- | --- |
| Remind about tasks at day and night changes | Off | When the day or night phase changes, show tasks for the new phase that are still unchecked. |
| Open or close checklist | Unbound; assign it in Key Bindings | Press the shortcut in the store while not typing to open or close the checklist. |

- **Tasks and recurrence:** Add, edit and check tasks; choose one schedule from once, daily, day or night. Up to 64 tasks and 96 characters per task.

![Write down restocking or filter checks, tick them off, and edit each task separately.](<images/f17.png>)

*Write down restocking or filter checks, tick them off, and edit each task separately.*

![After closing the editor, tasks can also be ticked on the shutter.](<images/f17-door.png>)

*After closing the editor, tasks can also be ticked on the shutter.*

### Adjust the appraisal lens and image (F18)

**Off by default**

During appraisal, adjust image magnification and the opacity of the frame and tray. The lens position is remembered after dragging; the view can be reset.

| Option | Default / range | What it does |
| --- | --- | --- |
| Appraisal image magnification (%) | 100 (100–300) | The original image size is 100%; higher values enlarge it. |
| Appraisal frame opacity (%) | 100 (0–100) | Relative to the original opacity: 0 is transparent and 100 leaves it unchanged. |
| Appraisal tray opacity (%) | 100 (0–100) | Relative to the original opacity: 0 is transparent and 100 leaves it unchanged. |

![Adjust the zoom and position of the inspection view.](<images/f18.png>)

*Adjust the zoom and position of the inspection view.*

### Enhance water and chemical scan results (F19)

**Off by default**

View component shares in water and chemical scanners. Select a component for treatments or its star to pin it.

| Option | Default / range | What it does |
| --- | --- | --- |
| Show component share bars | On | Show a share bar beneath each component. |
| Show component color marks | Off | Distinguish components with color marks. |
| Sort scan results by share | On | Turn off to use the native component order. Pinned components remain first. |
| Component row text size (pt) | 14 (8–28) | Set the text size for component names and values. |

- **Multiple pins:** Click the stars. Depleted pinned components stay at zero for that sample; changing samples clears the old measurements.
- **Treatment methods:** Select a component and expand a method card for its parameters and conditions: filters, UV, tablets or machines.

![Select organic waste to see the applicable treatment methods.](<images/f19.png>)

*Select organic waste to see the applicable treatment methods.*

### Preview container contents on hover (F20)

**Off by default**

In the store, hover over an accessible ordinary container to preview its contents. Locked, hidden or sealed contents still follow the game's normal opening rules.

| Option | Default / range | What it does |
| --- | --- | --- |
| Container preview delay (ms) | 500 (100–2000) | Time to hover over a container before its preview appears; lower values show it sooner. |
| Container preview side | Automatic · Automatic / Left / Right | Choose which side of the pointer shows the preview. Automatic chooses based on available screen space. |

![Hover over a container to preview the contents you are allowed to see.](<images/f20.png>)

*Hover over a container to preview the contents you are allowed to see.*

### Show deadlines on the shutter (F21)

**Off by default**

Show days remaining for rent or mortgage and tracked requests at the bottom of the shutter. Set how soon approaching deadlines are highlighted.

| Option | Default / range | What it does |
| --- | --- | --- |
| Days before deadlines are highlighted | 1 (0–3) | Highlight an item when its remaining days are at or below this value. Other deadlines still appear; 0 highlights only deadlines due today. |

- **Tracked deadlines:** Rent, mortgage and tracked requests. Untracked requests are not added automatically.

![The shutter reminder shows approaching deadlines and their remaining days.](<images/f21.png>)

*The shutter reminder shows approaching deadlines and their remaining days.*

### Use an appraisal worksheet (F23)

**Off by default**

Open the worksheet beside the appraisal tools and enter observations and amounts yourself. Enable worksheet calculation to copy the total into the appraisal estimate field.

| Option | Default / range | What it does |
| --- | --- | --- |
| Calculate the appraisal worksheet total | Off | Add the base amount and checked adjustments in the appraisal worksheet. A valid total can be copied into the estimate field. |
| Open appraisal worksheet | Unbound; assign it in Key Bindings | While appraising a customer's item, press the shortcut to open or close its worksheet. |

- **Worksheet entries:** Enter a base value and adjustments, then select which rows contribute. Calculation does not automatically identify authenticity.

![Check the base value, adjustments and total on the same worksheet.](<images/f23.png>)

*Check the base value, adjustments and total on the same worksheet.*

### Record and review daily cash flow (F28)

**Off by default**

Record daily cash changes after enabling this feature. Open the ledger beside the computer to review transaction types and current cash. Sales with known purchase costs show gross profit; unknown costs are counted separately.

- **What is recorded:** Records from the time it is enabled. Daily/type totals include known-cost margin and a count of transactions with unknown costs; older history is not reconstructed.

![Review daily income and expenses by type, followed by net change and profit for goods with known costs.](<images/f28.png>)

*Review daily income and expenses by type, followed by net change and profit for goods with known costs.*

### Show revealed labels on items (F38)

**On by default**

Show the game's revealed item attributes on visible items, such as counterfeit or laboratory grade. Hover to read the full text. Unrevealed information is not shown.

| Option | Default / range | What it does |
| --- | --- | --- |
| Item label text size | 9 (6–16) | Higher values make item label text larger. |

![Public condition and label information appears on items without hovering over each one.](<images/f38.png>)

*Public condition and label information appears on items without hovering over each one.*

### Mark known stolen items and their containers (F39)

**On by default**

Show badges on visible items to distinguish known stolen items, containers known to contain stolen items, and items that are both. Closed containers are not inspected for this feature.

| Option | Default / range | What it does |
| --- | --- | --- |
| Stolen badge size | 11 (6–20) | Higher values make the badge larger; it may shrink when space is limited. |
| Preferred stolen badge corner | Top left · Top left / Top right / Bottom left / Bottom right | Choose the preferred corner for the stolen badge. It may move to another corner when space is limited. |
| Stolen badge style | Filled · Filled / Outline | Choose a filled or outlined stolen badge. |

![Corner markers flag known stolen goods and containers holding them.](<images/f39.png>)

*Corner markers flag known stolen goods and containers holding them.*

### Rotate selected items as a group (F40)

**On by default**

In an owned ordinary inventory grid in the store, select multiple items from the same grid and drag them. Use the rotation keys or mouse wheel to rotate the group. To preserve the rotated formation in another container, enable precise group placement or hold Shift.

| Option | Default | What it does |
| --- | --- | --- |
| Start selection on occupied cells | On | With routing, group rotation or precise placement enabled, hold the game's selection key and Alt to start a selection on an occupied cell. |

- **Keep the formation across containers:** Enable precise placement F41 or hold the native Shift modifier to keep the formation shown in the preview.

![Before rotation, the selected group keeps its relative spacing.](<images/f40-before.png>)

*Before rotation, the selected group keeps its relative spacing.*

![Rotating the group turns the items’ relative layout together.](<images/f40.png>)

*Rotating the group turns the items’ relative layout together.*

### Place selected items precisely across containers (F41)

**On by default**

When dragging items selected from one owned ordinary inventory grid in the shop into another container, place them in the previewed formation. Green means the group fits; red means a collision or an out-of-bounds placement. When disabled, cross-container drops use automatic placement; holding Shift still allows precise placement. Unsupported or mixed-source selections retain native behavior.

- **Drop preview:** Green means it fits; red indicates collision or bounds. Turning this off restores native automatic placement; Shift still enables precise placement.

![A green preview shows a position where the whole group fits.](<images/f41-valid.png>)

*A green preview shows a position where the whole group fits.*

![A red placement warning means the group will not fit; the mod does not scatter part of the selection.](<images/f41.png>)

*A red placement warning means the group will not fit; the mod does not scatter part of the selection.*

### Fast playback (F42)

**Off by default**

Use a shortcut to speed through dialogue and presentation such as shutters and transitions.

| Option | Default / range | What it does |
| --- | --- | --- |
| Shortcut behavior | Hold to fast-forward · Hold to fast-forward / Press to toggle | Choose hold or toggle playback. |
| Playback speed | 8× · 2× / 4× / 8× / 16× | Adjust fast playback speed. |
| Fast playback shortcut | Unbound; assign it in Key Bindings | Hold to fast-forward or press to toggle according to the selected mode. |

- **Pauses and scope:** Pauses for choices, confirmation and text entry. Speeds up supported presentation sequences; dates, production and actual loading/saving retain native rules.

![The top-right indicator shows 8× fast play. Adjust the speed and key behavior in settings.](<images/f42.png>)

*The top-right indicator shows 8× fast play. Adjust the speed and key behavior in settings.*

## Tier 2: you set the rules, the mod handles the batch

You still choose the goods and destinations; the mod carries out repetitive transfers, slightly changing the pace of running the shop.

This tier includes batches you confirm by hand and recurring jobs such as replanting or replacing parts after you choose their targets and conditions. Each feature is enabled separately.

Every feature in this tier starts off. Enable what you want, then choose its sources, destinations or plan.

### Keep normal purchases on the back counter (F01)

**Off by default**

After a normal purchase in the shop, paid goods already on the back counter stay there. Wholesale has a separate switch. The game's automatic storage setting remains under your control; scratch cards, lottery tickets, barter and purchases outside the shop retain native behavior.

| Option | Default / range | What it does |
| --- | --- | --- |
| Keep wholesale purchases on the back counter | Off | Check back-counter space before wholesale checkout, then move paid items still on the front counter to the back. If space is short, make room or use normal checkout. Batches containing scratch cards or lottery tickets follow the game's normal rules. This switch is independent of the ordinary-purchase switch. |

![Before purchase, the canned meat is still in the seller’s list.](<images/f01-before.png>)

*Before purchase, the canned meat is still in the seller’s list.*

![After the purchase, the canned meat stays in the back counter instead of taking a slot in the main stock.](<images/f01.png>)

*After the purchase, the canned meat stays in the back counter instead of taking a slot in the main stock.*

### Set receiving rules for storage areas (F07)

**Off by default**

Right-click a storage grid in the main-stock branch to choose accepted items, exclusions and priority. Routing uses these rules; each container page can have its own rules. Specific-item rules take precedence over category rules, and an unavailable target can be configured to prevent fallback to other areas.

- **Rules per storage area:** Choose accepted categories or items, exclusions and priority. Storage pages can have separate rules.

![Choose accepted categories, individual items and their priority for the food bag, then save the rule.](<images/f07.png>)

*Choose accepted categories, individual items and their priority for the food bag, then save the rule.*

### Route selected goods using storage rules (F08)

**Off by default**

Select goods in the main-stock branch or confirmed settled goods on the back counter, then route them with the context menu or a shortcut. Receiving rules choose the destination; unmatched, pinned, reserved or immovable goods stay where they are.

| Option | Default / range | What it does |
| --- | --- | --- |
| Route selected goods | Unbound; assign it in Key Bindings | Route the current selection. The routing preview settings determine whether a preview appears first. |

![Before routing, the food is in the main stock and the receiving bag is empty.](<images/f08-before.png>)

*Before routing, the food is in the main stock and the receiving bag is empty.*

![After routing, the two selected food items are in the food bag on the right.](<images/f08.png>)

*After routing, the two selected food items are in the food bag on the right.*

### Preview goods and destinations before routing (F09)

**Off by default**

Show destinations and reasons goods cannot move according to the selected preview mode. When disabled, routing proceeds directly.

| Option | Default / range | What it does |
| --- | --- | --- |
| Routing preview mode | Always preview routing · Always preview routing / Preview routing when goods will not move / Preview routing without moving goods | Choose to preview every time, only when goods will not move, or without moving any goods. |

![Review the destination and goods before confirming the transfer.](<images/f09.png>)

*Review the destination and goods before confirming the transfer.*

### Batch-label matching items (F22)

**Off by default**

First label a sample with a labeler, then right-click the sample or another matching item, or use the shortcut. The shortcut and sample menu preselect eligible marquee-selected items; another item's menu preselects that item. Other eligible visible items remain available. The blue labeler copies names; the ordinary labeler repeats feature labels.

| Option | Default / range | What it does |
| --- | --- | --- |
| Allow batch replacement of existing labels | Off | Allow the batch to replace existing custom names or feature labels. |
| Open batch labeling | Unbound; assign it in Key Bindings | Open batch labeling. Matching selected items are preselected; other visible matching items remain available. |

![Select matching goods and apply the sample label to the batch.](<images/f22.png>)

*Select matching goods and apply the sample label to the batch.*

### Choose where appraisal slips are delivered (F24)

**Off by default**

New appraisal slips can go to the front counter, a selected container or the trash. Right-click a container or use the picker to select it. If the destination is unavailable, the game's normal delivery is used.

| Option | Default / range | What it does |
| --- | --- | --- |
| Appraisal slip destination | Front counter · Front counter / Selected container / Trash | Send new appraisal slips to the front counter, a selected container or the trash. Selecting the container option also requires choosing a container. |

- **Receipt box:** Bind a container through its item menu or the destination picker. Unavailable destinations fall back to native delivery.

![Choose a container for appraisal receipts; here they will be collected in the receipts and requests bag.](<images/f24.png>)

*Choose a container for appraisal receipts; here they will be collected in the receipts and requests bag.*

### Unload goods brought back from a trip (F25)

**Off by default**

When enabled, the backpack is recorded at departure. After returning, use the backpack menu to choose newly acquired goods with confirmed provenance and a destination. Incomplete tracking, changed quantities or goods merged into an old stack require manual review. A new trip does not overwrite an earlier pending record.

- **Recording and unloading:** Enable before leaving to record carried goods. On return, select new goods and a destination, then confirm. Additions merged into an old stack need manual handling.

![Choose which goods from the trip to unload and where to put them.](<images/f25.png>)

*Choose which goods from the trip to unload and where to put them.*

### Pour multiple water sources into one container (F26)

**Off by default**

In the shop, right-click a water container or use the shortcut. Choose an empty target or one with an identified grade, and identified sources. Preview and confirm; remaining sources pause if the filter is exhausted.

| Option | Default / range | What it does |
| --- | --- | --- |
| Allow mixing different water grades | Off | Allow identified sources of different grades when gathering water. Mixing still requires confirmation. |
| Open water gathering | Unbound; assign it in Key Bindings | Open the water gathering window in the shop to choose available containers. |

- **Sources and target:** Select sources with known liquid grades and preview capacity. An exhausted filter pauses the operation; replace it and preview again, or explicitly choose in the preview to continue pouring unfiltered water.

![Pour 500 mL from an inspected bottle into the jug. Before confirming, see that the jug holds 1000 mL and has 5000 mL free.](<images/f26.png>)

*Pour 500 mL from an inspected bottle into the jug. Before confirming, see that the jug holds 1000 mL and has 5000 mL free.*

### Track requests and reserve delivery goods (F27)

**Off by default**

Use Prepare beside a market request to track its deadline and place selected goods in a preparation container. The destination count includes only goods reserved for that request already in the target. Submit the delivery yourself.

- **Reservations and relocation:** Reserve actual items for the request. Changing the preparation container does not move them: preview and confirm relocation. Submit the request yourself.

![The request’s one required item is reserved and already in the destination bag on the right.](<images/f27.png>)

*The request’s one required item is reserved and already in the destination bag on the right.*

### Restock the showcase using rules (F29)

**Off by default**

Right-click a source item to set the source, display target and source reserve. Preview and confirm restocking; ineligible goods and those that do not fit stay in place.

| Option | Default / range | What it does |
| --- | --- | --- |
| Restock only goods with a purchase price | On | When enabled, both selecting goods and applying existing rules require a purchase price record on each item. Turn it off to allow goods without that record. |
| Showcase target quantity (units) | 4 (1–99) | Set the desired showcase count for this item when saving or updating a restock rule. Save the rule again after changing this value. |
| Restock source reserve (units) | 2 (0–99) | Set the minimum count of this item to retain at the source when saving or updating a restock rule. Save the rule again after changing this value. |

- **Source and confirmation:** Bind a real source to the rule. Preview quantities taken, stock left and shortages, then confirm stocking. This is not unattended restocking.

![This preview can restock one item and is still three short. It explains shortages caused by available stock and source reserves.](<images/f29.png>)

*This preview can restock one item and is still three short. It explains shortages caused by available stock and source reserves.*

### Automatically replace hydroponic and printer batteries (F31)

**Off by default**

Open the maintenance controls beside a hydroponic machine or printer and choose a spare container and charge threshold. Before a night cycle, when the machine can produce and its installed ordinary battery is below the threshold, it attempts at most one exchange per day. The spare must be the same type, hold more charge and power the cycle; the old battery returns to the source. A 0% threshold never triggers.

- **Battery source per machine:** For supported hydroponic units and printers. Bind a source for each machine; the removed battery needs a valid return position.
- **Threshold and priority:** New rules default to below 20%; range 0–100%, with 0% disabling the trigger. Choose by container position or highest charge among matching batteries.
- **When it runs:** Checks at night when the machine can produce; at most one attempt per machine per day.

![Choose this machine’s spare-battery source and threshold. The old battery returns to the source.](<images/f31.png>)

*Choose this machine’s spare-battery source and threshold. The old battery returns to the source.*

### Automatically replant a chosen hydroponic seed (F32)

**Off by default**

Open the replant controls beside a hydroponic machine and choose a seed from its input or output. Turn on gene scanning to choose between strains. After a non-perennial harvest an unstacked matching seed is taken from that machine's output. If replanting cannot proceed fix the issue and retry.

- **Seed per machine:** Choose from the machine input/output. After harvesting a non-perennial plant, reuse one seed matching its plant and known genotype. Choosing among genotypes requires scanning.

![Choose the plant to replant; gene scanning lets you choose a strain as well.](<images/f32.png>)

*Choose the plant to replant; gene scanning lets you choose a strain as well.*

### Automatically supply and collect furnace batches (F33)

**Off by default**

Open the furnace controls and choose material, source, output container and 1–10 batches. Each batch takes two unstacked items from the source and collects the ingot after confirmed production. It stops at the limit and pauses for missing supplies or power, insufficient space, uncleared input or output, conflicting furnace modules or conveyors. Fix the issue and resume manually.

- **Batches and logistics:** Choose material, source, output container and 1–10 batches for the furnace. Each batch loads two unstacked ingredients. Missing material, power or space pauses the task.

![Choose the material, source and output bag; this rule schedules two furnace batches.](<images/f33.png>)

*Choose the material, source and output bag; this rule schedules two furnace batches.*

### Automatically replace pitcher and aquaculture filters (F35)

**Off by default**

Open the maintenance controls beside a pitcher or aquaculture system and choose a spare container, allowed filter types and a pore-size rule. Replacement triggers only when an enabled type is exhausted during filtration. New rules enable all three types and use the same type only; other enabled types can be allowed by pore size. The old filter must fit back in the source. Adjust the corresponding AquaSense option when it controls the pitcher.

- **Materials and replacement rule:** For supported water pitchers and RAS filter slots. Select allowed materials; default is the same type, optionally equivalent/finer or any pore size. The old filter must fit back into the source.

![Choose the pitcher’s spare source and allowed filter types. This rule uses matching filters only.](<images/f35.png>)

*Choose the pitcher’s spare source and allowed filter types. This rule uses matching filters only.*

### Return live yeast when brewing (F37)

**Off by default**

Remember the source when live yeast is moved from an ordinary shop grid into a bottle. When brewing starts, return it to its original cell if possible, otherwise find space in main stock, a counter or the display case. If none is available, make room or continue under the game's normal rules, which may lose the yeast.

- **Return order:** Remembers the original location and orientation. If blocked, it checks safe positions in the main stock, counters and display case.

![Before brewing, the active yeast is in the supply bag on the left.](<images/f37-before.png>)

*Before brewing, the active yeast is in the supply bag on the left.*

![After brewing, the same active yeast returns to its original supply bag.](<images/f37.png>)

*After brewing, the same active yeast returns to its original supply bag.*

## Tier 3: view and configure across containers and machines

Reduces inventory searches, machine-by-machine checks and manual layout planning. Suits later-game players who want to shorten these tasks substantially.

Every feature in this tier starts off. Enable what you want, then choose its sources, destinations or plan.

### Manually install and remove modules in the cabinet (F10)

**Off by default**

Open an owned machine's Module Cabinet to browse idle modules across the store and modules in other machines, and drag modules into a machine. Before removing a module, choose the main stock or an accessible ordinary container or storage page inside it as module storage.

| Option | Default / range | What it does |
| --- | --- | --- |
| Open the machine module cabinet | Unbound; assign it in Key Bindings | Press while an owned machine window is open to open its Module Cabinet. |

- **Storage destination:** Bind a real container or storage page. Modules dragged out of a machine into the idle area return there.

![Idle modules and the selected machine’s slots are shown together for manual installation and removal.](<images/f10.png>)

*Idle modules and the selected machine’s slots are shown together for manual installation and removal.*

### List goods matching the customer request (F11)

**Off by default**

Search accessible owned inventory by name beside the main stock, or find matching goods, sources, availability and available estimated sale prices from negotiation. Goods on the counter show their actual quote; optionally compare purchase records. Locate a source or drag eligible whole stacks into open ordinary inventories. To put goods on the counter, locate them first and use the game's own controls.

| Option | Default / range | What it does |
| --- | --- | --- |
| Compare purchase prices | Off | Show native purchase records alongside quotes in the customer list and budget picker. Off by default. Records are purchase totals; missing records stay unknown. Unit costs after splitting or merging are not inferred. |
| Open customer goods list | Unbound; assign it in Key Bindings | Open or close the goods list when a customer has a request. |

![Search inventory by name; cards show each item's source and access status.](<images/f11.png>)

*Search inventory by name; cards show each item's source and access status.*

### Plan machine module layouts by attribute (F12)

**Off by default**

Enable the Module Cabinet. Select up to five compatible idle modules, or rearrange installed modules without adding candidates; the combined total is at most 12. Choose Arrange and select performance, efficiency or quality. Preview the layout and stat changes before applying the recommended modules. Existing modules stay installed but movable ones may change position; evaporators fill empty cells only.

- **Goal and candidates:** Compare performance, efficiency or quality using up to five idle candidates. Review the layout and stat changes before applying.

![Preview before applying: this layout raises performance from 0% to 16% while quality stays at 8%.](<images/f12.png>)

*Preview before applying: this layout raises performance from 0% to 16% while quality stays at 8%.*

### View machine supplies, power and output (F30)

**Off by default**

Open Machine Status in the store to review input, power and output for hydroponic machines, collectors, evaporators, printers and furnaces. Filter for machines needing attention. Select a card to see its location and bring its already-open inventory window forward. Up to 100 entries are shown.

| Option | Default / range | What it does |
| --- | --- | --- |
| Open machine status overview | Unbound; assign it in Key Bindings | Press in the store to open the machine status overview. |

- **Overview scope:** Shows adapted machines such as hydroponics, moisture farms, evaporators, printers and furnaces. Filter to machines needing attention. Output counts are stacks.

![See machine supplies, power and outputs together, then attend to what is missing.](<images/f30.png>)

*See machine supplies, power and outputs together, then attend to what is missing.*

### Choose customer goods by quote (F34)

**Off by default**

Compare goods using estimated sale prices and the customer's budget without staging them first. If the native bargaining panel does not display a budget, enter a limit yourself. Optional staging requires the bargaining panel open, unrelated goods removed from the counter and enough space; it stops if actual quotes change. Complete the sale yourself.

| Option | Default / range | What it does |
| --- | --- | --- |
| Allow staging the chosen goods on the counter | Off | After confirmation, place items one at a time and recheck each customer quote. Staging stops if quotes, budget or goods change; complete the sale yourself. |

- **Quotes and budget:** Estimate sale prices directly in storage and use the customer budget or an entered limit. Purchase-price comparison is optional and off by default. Actual counter quotes are rechecked; finish the sale manually.

![Compare a selection against the customer’s budget, including the total and amount left.](<images/f34.png>)

*Compare a selection against the customer’s budget, including the total and amount left.*

### Plan which scavenged goods to take (F36)

**Off by default**

Open the packing list at the dumping ground to select revealed goods, priorities and excluded categories, or pack in descending order of known value. Preview before packing into the backpack or pockets; goods that do not fit stay behind.

| Option | Default / range | What it does |
| --- | --- | --- |
| Scavenging backpack reserve (cells) | 2 (0–32) | Leave this many of the backpack’s existing empty cells free when planning a pack. Pockets are unaffected. |
| Open scavenging packing list | Unbound; assign it in Key Bindings | Open the scavenging packing list at the dumping ground. |

- **Selection for this trip:** Choose revealed items, priorities and excluded categories. Preview and confirm; items that do not fit stay where they are.

![Choose goods and priorities, then preview packing while preserving the reserved space.](<images/f36.png>)

*Choose goods and priorities, then preview packing while preserving the reserved space.*

## Setup and settings

Subscribe on Steam Workshop and wait for the download. In the main menu, enable **更好的体验 / Quality of Probably Stolen** under Mods and restart as prompted. For manual installation, place the complete `ShopAssist` folder from the ZIP in the game's `Mods` directory, with `manifest.xml` and `ShopAssist.dll` together. A local copy with the same mod ID overrides the Workshop copy. Adjust the features under the three in-game tiers, then select **Apply**.

Settings follow **tier → purpose group → feature → suboptions and parameters**. Actions appear in item menus and beside customers and machines. Search by name, purpose or the F numbers shown here, or filter by tier. Assign shortcuts in the game’s key bindings.

With **Smart Inventory Arrange** enabled, it handles arranging, making room and undo. If **AquaSense** handles pitcher filter replacement, adjust that option in AquaSense.

![Native settings use the same tiers and purpose groups.](<images/settings.png>)

## Tell me how it feels to use

The mod is in public beta. If something feels awkward, an explanation is unclear or you have an idea for a feature, I’d like to hear it. You can report problems on GitHub too.

When reporting a problem, a short description of what you were doing and how it happens again helps. Include the game and mod versions, and a screenshot if you have one.

[Suggest a feature or report an issue](https://github.com/StarsWhere/Quality-of-Probably-Stolen/issues) · [View the GitHub repository](https://github.com/StarsWhere/Quality-of-Probably-Stolen)

## References and thanks

The following mods were useful references while making this one. Their customer highlighting, inventory actions, trade feedback, scanner displays and tooltips helped shape these features:

- [客户需求高亮 / Client Demand Highlight](https://steamcommunity.com/sharedfiles/filedetails/?id=3828938065): Customer demand matching and the distinct outlines for ordinary, stolen and contraband goods.
- [Smart Inventory Arrange](https://steamcommunity.com/sharedfiles/filedetails/?id=3829206666): Context-menu arranging, keeping containers in place and the arrange/undo workflow.
- [Reputation Pop](https://steamcommunity.com/sharedfiles/filedetails/?id=3829053332): Reputation feedback beside the customer and faction-specific colours.
- [Better Liquid Scanner](https://steamcommunity.com/sharedfiles/filedetails/?id=3831472187): The scanner’s presentation and sorting of components, volumes and proportions.
- [Better Item Tooltips](https://steamcommunity.com/sharedfiles/filedetails/?id=3830180473): The organisation and readability of item tooltips.

Thanks to their authors; follow the links to see the original mods. Thanks also to the game developers for the [modding API and documentation](https://github.com/dragonpan2/probably-stolen-modding).
