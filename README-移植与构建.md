# NotEnoughDifficulty（还不够难）· 源码包

> 把《Slay the Spire 2》原版 **3 层塔扩成 5（+1）层**，并提供按层难度/地图/敌人池配置的 mod。
> 本包是**源码 + 文档 + 自研工具**的干净副本（不含编译产物、不含游戏与 BaseLib 二进制）。

打包时间：**2026-09-13**　·　目标游戏：**StS2 public beta v0.111.0**（MegaDot/Godot 4.5.1）　·　BaseLib：**3.4.7**
源码规模：**67 个 .cs / 约 536 KB**　·　当前状态：**编译 0 错 0 警**

---

## 一、目录结构

```
NotEnoughDifficulty-源码-2026-09-13/
├─ README-源码包说明.md            ← 本文件
├─ 杀戮尖塔2-mod写作踩坑指南.md      ★ 34 条实战坑 + §3.x 专题（改不动原版时怎么找钩子、时序、兼容）
├─ 待办与交接-第三阶段.md           ★ 当前进度 / 待办 / 血泪硬事实（新会话的唯一入口）
├─ BossGauntlet-合成节点法-拆解笔记.md  （合成火堆/商店节点的逆向笔记）
├─ 历史文档/                        早期移植阶段的方案与交接（存档用）
├─ src/                            ★ mod 源码（= 原工程 build/ 去掉产物）
│   ├─ NotEnoughDifficulty.csproj  Godot.NET.Sdk 4.5.1 工程文件（引用 _refs\ 下的游戏/BaseLib 程序集）
│   ├─ nuget.config                离线包源（指向 _refs\nuget-feed）
│   ├─ project.godot / export_presets.cfg / NotEnoughDifficulty.json / LICENSE / README.md / docs/
│   ├─ NotEnoughDifficultyCode/    业务代码（12 个子目录，见下）
│   └─ NotEnoughDifficulty/        随包资源：localization/{zhs,eng}/*.json、mod_image.png
└─ tools/                          自研工具源码（离线构建与逆向都用得上）
    ├─ MakePck/                    ★ 程序化生成 .pck（免装 Godot，把 localization 打进包里）
    ├─ DumpIl/                     反汇编 dll（读游戏/模组真实行为，本项目的"显微镜"）
    ├─ DumpApi/                    dump 类型/成员清单（查 API 签名）
    ├─ DumpStrings/                提取 dll 内字符串（找类型名/事件名）
    ├─ FindRef/                    调用点搜索（谁调用了某方法）
    ├─ EnumProbe/                  枚举值探测
    └─ check-patch-targets.ps1     校验所有 Harmony patch 目标在目标 dll 里真实存在
```

### `src/NotEnoughDifficultyCode/` 目录职责

| 目录 | 内容 |
|---|---|
| `Core/` | `RunProgress`（进度/池子/层号判定的唯一入口）、`ActLayout`（第 4 幕被占用时顺延 5/6）、`ActBlueprint` |
| `Config/` | `NotEnoughDifficultyConfig*`（BaseLib 配置项，按 section 分文件）、`ExtraActsConfig`（权重/档位语义化 API） |
| `ExtraActs/` | 第 4/5 幕本体：`Models/Act4Model`、`Act5Model`；`Patches/`（双 boss、合成火堆、精英落位 `Act4ElitePlan`…）、`Bootstrap/ExpandActListPatch`（追加 act 到 run 列表）、`Compat/`、`Pool/` |
| `Act5/` | 第 5 幕（传奇/神话）连战：直线地图、自研 BOSS 节点类 `Act5BossNode`、背景/滤镜与配色（`ActVisualTheme`、`HomeActBackgroundRedirectPatch`…） |
| `Difficulty/` `MapLength/` `RemovalList/` `SpeedControl/` `SaveCompat/` | 难度公式、地图长度、敌人移除列表、额外加速、存档兼容 |
| `MultiplayerSync/` `SettingsInjection/` | 联机配置同步、官方设置界面注入 |

---

## 二、怎么构建（离线、自包含）

### 1. 依赖（本包不含，需自备）

| 需要 | 从哪来 | 放到哪 |
|---|---|---|
| `.NET SDK 9` | 微软官网 | PATH |
| `sts2.dll` / `0Harmony.dll` / `GodotSharp.dll` / `MonoMod.*.dll` / `sts2.deps.json` / `sts2.runtimeconfig.json` | 游戏安装目录 `data_sts2_windows_x86_64\` | `<仓库根>\_refs\game\` |
| `BaseLib.dll` | Steam 创意工坊的 BaseLib（或作者发布页） | `<仓库根>\_refs\baselib\` |
| Godot 4.5.1 的 4 个 nupkg（Godot.NET.Sdk / GodotSharp / GodotSharpEditor / Godot.SourceGenerators） | 首次联网 `dotnet restore` 会缓存，或手动下载 | `<仓库根>\_refs\nuget-feed\` |

> `src/NotEnoughDifficulty.csproj` 里的 `HintPath` 写的是 `..\_refs\game\...` / `..\_refs\baselib\...`，
> 所以**保持这个相对层级**即可（例如把本包解到 `<某处>`，再建 `<某处>\_refs\`）。
> `nuget.config` 把包源限定为 `..\_refs\nuget-feed`，是刻意的"零网络依赖"设计。

### 2. 编译

```powershell
cd src
dotnet build --no-incremental          # 产物 → src\dist\（NotEnoughDifficulty.dll / .pdb）
```
> ⚠️ 用 `--no-incremental`：增量构建曾给出"0 错"的假结论（见指南 §0.4）。

### 3. 生成 .pck（本地化等资源）

```powershell
dotnet run --project tools\MakePck -- <src>\dist\NotEnoughDifficulty.pck NotEnoughDifficulty <src>\NotEnoughDifficulty (Get-ChildItem <src>\NotEnoughDifficulty\localization -Recurse -Filter *.json | % FullName)
```
（参数：输出 pck、包内根目录名、资源根、要打进去的文件清单。）

### 4. 部署

把 `NotEnoughDifficulty.{dll,pdb,pck,json}` 一起拷进
`<StS2>\mods\NotEnoughDifficulty\`，重启游戏。日志在 `%APPDATA%\SlayTheSpire2\logs\godot.log`。

---

## 三、当前功能与状态

| 项 | 状态 |
|---|---|
| 第 4 幕（精英连战：全部基础精英各一次、按坐标落位、不重复） | ✅ 实机验证 |
| 第 5 幕（传奇/神话两档：先古之名 → 伪装BOSS → 最终BOSS） | ✅ 实机验证 |
| 双 boss（按层开关 + 确定性抽取 + 中间夹合成火堆/商店） | ✅ 实机验证（含其它模组 act 变体） |
| 战斗背景按"敌人自己所属的幕"切换（精英限定） | ✅ 实机验证 |
| 地图纹路按来源幕变色（从该幕**美术**统计招牌色相） | ✅ 实机验证 |
| 与"第 4 幕被别的模组占用"兼容（自动顺延为第 5/6 幕，可关） | ✅ 已实现，待组合实测 |
| 与整幕类模组（ActsFromThePast 等）的精英/背景兼容 | ✅ 已实现（背景走"改写 parentAct"，让对方自己的钩子生效） |
| 本地化 | 中英双语，配置项 title + description 全覆盖，无重复/孤儿键 |

已知遗留（下一轮清理，详见 `待办与交接-第三阶段.md`）：
`AvoidAdjacentEncounterDuplicate` 配置项与 `ExtraActs/Pool/EncounterDeduplicator`（旧"相邻去重"思路，新落位表已不可能重复）；
`Act5/ActMapOverlay.cs`（早期"边缘雾气"overlay，用户已要求删除雾气，现仅剩 `SetLayerTint` 空转）。

---

## 四、先读哪几份文档

1. **`待办与交接-第三阶段.md`** —— 项目现状、待办、必须知道的硬事实（含"patch 入口先打日志""层号要按数据判"等）。
2. **`杀戮尖塔2-mod写作踩坑指南.md`** —— 34 条坑 + 12 章方法论：
   - §0 别猜，去读程序集（元数据 dump / IL / 形参名逐字匹配）
   - §3.5 属性 patch 要写 `get_X`；§3.6 改不动就找 BaseLib `Abstracts` 钩子
   - §3.8 改原版贴图前先找"唯一赋值点"；§3.10 生成物别赌时机，问"当前事实"
   - §3.11 `IsTravelable` 掺了调试短路；§3.13 判层号别按 act 类型
   - §3.15/3.17 兼容别的模组：**改写事实**而不是抢着造结果；**能 dump 就 dump 对方 dll**
   - §3.18 聚合容器当候选 = 自引用递归 + 日志洪泛 = 崩溃
3. **`BossGauntlet-合成节点法-拆解笔记.md`** —— 在地图里插自定义节点的完整手法（合成火堆/商店即由此而来）。

---

## 五、本次修复记录（2026-09-13，按发现顺序）

| # | 现象 | 根因 | 修法（文件） |
|---|---|---|---|
| 1 | 精英幕纹路不变色 | `NMapBg.OnVisibilityChanged` 是三张底图的**唯一赋值点**，直接改 `Texture` 必被覆盖；且三个 rect 的 `Material` 全是 null | 纹路色做进 `ActModel.get_MapTopBg/MidBg/BotBg`（缓存键含色相），层变化时 Traverse 调一次 `OnVisibilityChanged`（`Act4MapStripeTint` / `ActVisualTheme`） |
| 2 | 精英数量 9、还会重复 | 目标数用了"没打过的精英"（进度差集）；填池用 `i % count` 取模 ⇒ 池内重复 | 名单 = 全部基础精英（12 个各一次），地图生成时按坐标**落位**，取怪时查表（`Act4ElitePlan` + `ActDepthPatch` + `Act5MapPatch`） |
| 3 | 模组 act 变体没有双 boss | `GetActIndexFromModel` 是**按类型写死**的表 ⇒ 模组 act 认成 -1 | 按 `ActsByIndex` / `RunState.Acts` / `act.Index` 数据判定（`RunProgress`） |
| 4 | 战斗背景慢一场 | 背景资产有**两个调用点**（预载 `GetAssetPaths` 比 `SetUpBackground` 早） | 来源幕改为问"当前房间"（`Act4FightSource.Current`，时序无关） |
| 5 | 暗港颜色像默认紫 | 用了 `MapTraveledColor`（近黑 UI 色 266° vs 默认紫 278°） | 从该 act**自己的地图美术**统计招牌色相（`ActVisualTheme.SignatureHue`） |
| 6 | act5 点自己卡死 | `IsTravelable` 第一分支被调试旅行短路（绕过 State） | 只认 `State == Travelable` + 同坐标点击总闸（`Act5BossNode` / `Act5SameCoordClickGuardPatch`） |
| 7 | 模组精英背景不跟来源 | ① 自带背景的敌人绕过 act 钩子；② 归属判定"先到先得"；③ 自己造资产绕不过对方的钩子 | 依次修：截 `EncounterModel.GetBackgroundAssets` → 先查地图历史 → **改写 `parentAct` 让对方的钩子自己跑**（`HomeActBackgroundRedirectPatch`） |
| 8 | 崩 溃 | "聚合 act"被选成来源 ⇒ 自我递归 + 每层打日志 ⇒ 9.3MB 洪泛 | 候选排除自己 + 递归硬闸 + 日志去重（`RunProgress` / `Act4CombatBackgroundByLayerPatch` / `Act4Model`） |
| 9 | 本幕 BOSS 场景没了 | 背景折返**不分敌人类别** | 加 `IsEliteFight()` 闸：只精英走折返，BOSS/其它放过去（`Act4FightSource` / `HomeActBackgroundRedirectPatch`） |
| 10 | 打完一个 BOSS 直接进建筑师 | 用"名字在不在伪装名单里"判房间性质，而名单**边打边少** ⇒ 第 N 场被判成 BOSS 房 | 判据换成**坐标**（`IsAtFinalBossNode`），名单口径换成"进入本幕之前"（`Act5BossDisplay` / `Act5Disguised*Patch` / `RunProgress`） |
| 11 | 神话档火堆全没了 | 名单**晚于建图** ⇒ 地图按"链位 1、火堆 0"造出来 | `CustomCreateMap` 里建图前补一次定稿（幂等）（`Act5Model`） |

对应的方法论都写进了 `杀戮尖塔2-mod写作踩坑指南.md`（§3.8–§3.21 + 附录坑 22–37）。

---

## 六、许可

`src/LICENSE` 沿用原工程（上游 StS2-NotEnoughDifficulty）。`tools/` 下的自研工具可自由取用。
