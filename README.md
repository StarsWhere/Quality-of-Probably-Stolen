# Probably Stolen 经营助手

[42 项功能与游戏内用法](Probably%20Stolen%20体验MOD功能设计.md) · [游戏机制与技术边界](Probably%20Stolen%20体验MOD技术实现方案.md)

## 安装

从 `releases/<版本>/` 取发布 ZIP，解压后把整个 `ShopAssist` 文件夹放到游戏安装目录的 `Mods` 下。`manifest.xml` 与 `ShopAssist.dll` 同级。主菜单 **Mods → 经营助手 / Shop Assist** 启用，按原版提示重启。

游戏内从 **选项 → 物品与仓储、交易与委托、机器与生产、日历与工作台、快速播放** 调整功能并点击 **应用**；通过 **查找功能与设置** 搜索名称、用途或功能号。常用操作在机器、顾客、鉴定、电脑及物品附近，主仓标题的 **店铺工具** 提供分类入口和 **待处理**。

## 构建与安装命令

使用 PowerShell 7 和 .NET SDK 8，指定正式版游戏目录：

```powershell
$env:PROBABLY_STOLEN_GAME_DIR = 'D:\SteamLibrary\steamapps\common\Probably Stolen'
pwsh -File scripts/Build-Mod.ps1
pwsh -File scripts/Build-Mod.ps1 -Install -Release
```

安装前退出游戏。构建校验中英文键、清单、原生本地与工坊扫描。发布包与 SHA-256 文件进入 `releases/<版本>/`。安装或发布已验证暂存包时使用 `-FromPackage <目录>`，避免重新构建不同的二进制。

## 验证命令

```powershell
dotnet run --project tests/CalendarTests -c Release
dotnet run --project tests/AreaLabelTests -c Release
dotnet run --project tests/InventorySortPlanTests -c Release
dotnet run --project tests/GroupPlacementTests -c Release
dotnet run --project tests/ItemGridPackingTests -c Release
dotnet run --project tests/CustomerInventoryTests -c Release
dotnet run --project tests/DesktopTests -c Release
dotnet run --project tests/PublicIndicatorTests -c Release
dotnet run --project tests/PublicClueTests -c Release
dotnet run --project tests/LiquidDisplayRulesTests -c Release
dotnet run --project tests/RoutingPlanTests -c Release
dotnet run --project tests/RoutingRulesTests -c Release
dotnet run --project tests/FeatureCatalogTests -c Release
dotnet run --project tests/ReplantTaskRulesTests -c Release
```

隔离原版 Player 使用独立测试身份和存档；首次准备后从原生扫描审批开始。渲染验证需要 `-Render`，在独立且不可见的 Windows 桌面运行，不切换当前桌面、不抢占前台或注入系统鼠标键盘；无渲染验证使用后台批处理模式。并行任务可使用不同 `-Profile <名称>`，同一配置的 Prepare、Next 和 Cleanup 须传入相同名称；可用 `-ScreenWidth` 与 `-ScreenHeight` 指定渲染分辨率。

```powershell
pwsh -File scripts/player-test/Invoke-PlayerTest.ps1 -Action Prepare
pwsh -File scripts/player-test/Invoke-PlayerTest.ps1 -Action Release -Render
pwsh -File scripts/player-test/Invoke-PlayerTest.ps1 -Action Cleanup
```

性能场景为 `PerformanceSmallScenario` 和 `PerformanceLargeScenario`，分别使用 100 和 1000 件物品，须带 `-Render`。比较不同 MOD 包时，准备阶段可用 `-ProbeAssembly <PlayerProbe.dll>` 固定同一份探针；结果写入隔离数据目录的 `qa-performance-inventory-*.csv`。指标区分 MOD 更新、原生组拖更新及同步交互；分配计数未通过运行时校准时记为 `-1`。

快速播放的等待耗时与模块开销使用 `-Action Next -Scenario FastPlaybackMotionPerformanceScenario -Render` 验证，结果写入隔离数据目录的 `qa-fastplay-performance.csv`；正常播放与 8 倍播放使用同一转场夹具。

针对已安装工坊物品验证两种加载顺序：

```powershell
$ids = '3828868413','3828878417','3828938065','3829053332','3829206666','3829360413','3829415336'
foreach ($order in 'First','Last') {
    & ./scripts/player-test/Invoke-PlayerTest.ps1 -Action Prepare -WorkshopIds $ids -ShopAssistOrder $order
    & ./scripts/player-test/Invoke-PlayerTest.ps1 -Action Coexist -Render
    foreach ($case in 'CoexistInventoryScenario','CoexistTradeScenario','CoexistLiquidScenario','CustomerCounterDropScenario','DailyGroupSwitchScenario','AllFeaturesScenario') {
        & ./scripts/player-test/Invoke-PlayerTest.ps1 -Action Next -Scenario $case -Render
    }
}
& ./scripts/player-test/Invoke-PlayerTest.ps1 -Action Cleanup
```
