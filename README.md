# 更好的体验 | Quality of Probably Stolen

《Probably Stolen》的玩家体验模组。功能、入口与使用方式见[功能与使用说明](Probably%20Stolen%20体验MOD功能设计.md)，游戏机制及维护约束见[技术说明](Probably%20Stolen%20体验MOD技术实现方案.md)。问题与建议可提交至[项目问题区](https://github.com/StarsWhere/Quality-of-Probably-Stolen/issues)。

## 构建与验证

使用 PowerShell 7 和 .NET SDK；将游戏目录替换为本机安装位置，也可设置 `PROBABLY_STOLEN_GAME_DIR`。

```powershell
$game = 'D:\SteamLibrary\steamapps\common\Probably Stolen'
dotnet build ./src/ShopAssist/ShopAssist.csproj -c Release "-p:GameDir=$game"
dotnet run --project ./tests/FeatureCatalogTests -c Release
dotnet run --project ./tests/CustomerInventoryTests/CustomerInventoryTests.csproj -c Release
dotnet run --project ./tests/RoutingPlanTests -c Release
./scripts/Build-Mod.ps1 -GameDir $game
```

`Build-Mod.ps1` 构建并检查原生模组包；添加 `-Install` 可安装到本机游戏，添加 `-Release` 可生成 `releases/<版本>/` 下的发布包。隔离游戏验证使用独立副本及存档：

```powershell
./scripts/player-test/Invoke-PlayerTest.ps1 -Action Prepare -GameDir $game -Profile player-test
./scripts/player-test/Invoke-PlayerTest.ps1 -Action Next -GameDir $game -Profile player-test -Scenario ContextShortcutScenario -Render
./scripts/player-test/Invoke-PlayerTest.ps1 -Action Cleanup -Profile player-test
```
