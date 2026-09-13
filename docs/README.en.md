# StS2-NotEnoughDifficulty ("Still Not Hard Enough!") · Beta Port Branch

English | [中文](../README.md)

> ### Branch identity
> - **Upstream (original design & implementation, copyright held by the original author)**:
>   <https://github.com/bwnotfound/StS2-NotEnoughDifficulty>
> - **Tooling & collaboration**

- **DeepSeek** (DeepSeek Harness / deepseek-flash) — the **AI collaborator** on this port: reading and
  refactoring the code, turning assembly-IL analysis into reusable conclusions, tracking down the in-game issues
  (background ownership, roster drift, rest-site ordering, the self-recursion crash, …), and writing the
  repository documentation (this README, the 37-pitfall guide, the handover notes).
**This branch**: `beta-0.111-port` — a port + expansion branch **commissioned by the original author**
> - **Target environment**: StS2 **Public Beta v0.111.0** (MegaDot / Godot 4.5.1) + **BaseLib 3.4.7**
> - **Ported by**: [@Coll-ed](https://github.com/Coll-ed); uses the upstream `LICENSE`

Turns the vanilla 3-act tower of *Slay the Spire 2* into a **5+1 act** run, with per-act control over
difficulty, maps and enemy pools. Works in single-player and co-op; in multiplayer the host's config is
broadcast to every player.

---

## 📊 Project size & delivery

| Item | Value |
|---|---|
| Code | **67 `.cs` files / 9,988 lines / 16 directories** |
| Change surface | **60 HarmonyPatch attributes** (~48 distinct game-side target methods), **313 logging sites** |
| Localization | Full Chinese + English (every config field has a title and a description) |
| Build quality | `dotnet build --no-incremental` — **0 errors, 0 warnings** |
| Artifacts | `.dll + .pdb + .pck + .json`; the `.pck` is produced by our own `tools/MakePck` — **no Godot install needed** |
| Self-contained build | Game / BaseLib assemblies come from a local `_refs\` folder, NuGet from a local offline feed ⇒ **builds with zero network access** |

---

## 🔗 Compatibility: why it is a first-class requirement here

In a modded setup the fragile part of an "extra acts" mod is not the gameplay — it is **fighting other
mods over the same ground**. This branch treats compatibility as a hard requirement:

| The "quick" way | What breaks | What this branch does | Player-visible effect |
|---|---|---|---|
| Detect the layer by **act type** (`is Hive => 2`) | Act variants from other mods are not recognised ⇒ that act's double boss / scaling **silently does nothing** | Layer index is **derived from data** (`ModelDb.ActsByIndex` / `RunState.Acts` / `act.Index`) | This mod keeps working inside a whole-act modpack |
| Two mods both claim **act 4** | Last loaded wins, or a black screen | If act 4 is taken, this mod's acts **shift to acts 5 and 6** | Coexists with mods such as Act 4 Heart |
| Roster computed from "what is left" | It **changes as you fight**: nodes no longer match rooms, and in the worst case "one boss ends the run" | Roster is computed from the record **before entering the act**; "is this a boss room / may it end the act" is decided by **coordinate** | Stable for the whole act, consistent across save/load |
| Build a fresh background asset and force it | Overwrites another mod's hand-drawn arena | **Rewrite `parentAct`** so the other mod's own hooks run | Their carefully made scene still shows |
| Fight over map nodes / links / travelability | Chained conflicts with other map mods | Replace only our own nodes; chaining and travelability stay with vanilla `RecalculateTravelability` | Coexists with other map-editing mods |
| One failing patch takes everything down | A single incompatibility kills the mod | Every patch class has its own try/catch; failures only print `Patch class X failed (skipped)` | The game still starts in extreme setups |
| No idea what else is loaded | Debugging becomes guesswork | `ModCompat` runtime detection, yielding, and a startup **compatibility report** | You can see at a glance which mods sit on the same patch points |

**The heavy mods this was actually validated against:**

| Mod | Size / nature | What it stresses |
|---|---|---|
| **ActsFromThePast** (+ its dependency **RitsuLib**) | Brings all three *Slay the Spire 1* acts in: its own elites / bosses and **hand-drawn combat backgrounds**, with a >100 MB asset pack | Its acts, elites and bosses feed this mod's act 4/5 draws; its hand-drawn backgrounds are exactly why we ended up **rewriting `parentAct`** instead of building our own assets |
| **YUI Spire Expansion** / **YUI Card Expansion** | Large content expansions (cards, relics, events, bosses) | Many new bosses enter the act-5 plan and new elites enter the act-4 roster; coexists with its own config pages |
| **Act 4 Heart** | Directly **occupies act 4** | Exercises the "shift to acts 5 and 6" path (`Act4HeartAutoShift`) |
| Skin / card-art / localization packs (e.g. Orca skin, Ironclad card art) | Tens of MB of asset replacements | Confirms localization and official-settings injection are not disturbed by asset mods |

**Field case: two consecutive days of full clears inside a 57-mod pack**

> With the mods below (54 of them confirmed initialised in this machine's log) **plus this mod** enabled at the
> same time, the pack was **cleared successfully on two consecutive days, stable, with no errors**.
> This is not theoretical compatibility - it is a run that actually happened.

<details><summary>Expand: the mods enabled in this test (54)</summary>

- [咲夜 Mod] Touhou Sakuya Mod [Sts2TouhouSakuya] (v0.3.5) workshopId=3792102938
- Act 4 Heart [Act4Heart] (1.1.7) workshopId=3747537811
- Acts from the Past [ActsFromThePast] (1.0.5) workshopId=3746969593
- AnimeWaifuSilent (v1.4.3) workshopId=3748864970
- BaseLib (v3.4.7) workshopId=3737335127
- BetterAnimation2 (0.6.1) workshopId=3777276296
- BetterSovereignBlade (0.1.0) workshopId=3747579880
- BossSkin (0.0.4) workshopId=3795439955
- Bullet Time: As Depicted [BulletTimeAnime] (0.3.3) workshopId=3772619739
- CharacterSkinManager (v0.0.3+sts2.0.108.0.2)
- Classic Mode [ClassicMode] (0.1.20) workshopId=3749025602
- CVC-跨版本兼容补丁 [CrossVersionCompat] (v0.9.29) workshopId=3783150604
- Gluton's Ascensions [GlutonsAscensions] (v1.4.6) workshopId=3747530530
- HeartShake (0.1.0) workshopId=3799286717
- Higher Resolution Cards [HighResolutionCards] (v0.1.1) workshopId=3749618558
- Infinite Defect Orb Slots [InfiniteDefectOrbSlots] (v0.2.5) workshopId=3753669351
- Merchant2CuteII (v1.5.3) workshopId=3748331824
- Minty Spire 2 [MintySpire2] (v1.2.0) workshopId=3737336234
- More Upgrades [MoreUpgrades] (v1.1.0) workshopId=3747695713
- MoreCharacterFX (v0.0.1) workshopId=3747579486
- MoreDollRelics (0.8-beta) workshopId=3747525077
- Multiplayer Potion View [STS2-MultiPlayerPotionView] (0.3.3) workshopId=3747606792
- NecrobinderFemPortraits (v0.1.3) workshopId=3747767062
- necrobinderSkin (0.9.1) workshopId=3747597614
- neowSkin (0.0.1) workshopId=3747611326
- RedMist (0.1.0) workshopId=3748217244
- RelicCombo (0.11.4) workshopId=3755470556
- RitsuLib [STS2-RitsuLib] (0.5.20) workshopId=3747602295
- Show Player Hand Cards [STS2-ShowPlayerHandCards] (0.6.4) workshopId=3747606660
- silentSkin (0.7.1) workshopId=3747591649
- SovereignBladeFullArt (v0.0.2) workshopId=3747611023
- StS2 Clone Optimizer [Sts2CloneOptimizer] (v0.1.0) workshopId=3747573917
- STS2 Skin Manager [Sts2SkinManager] (0.27.4) workshopId=3747513223
- VoiceMod [voicemod] (1.13.0) workshopId=3780124701
- Voltaic: As Depicted [VoltaicAnime] (0.1.2) workshopId=3773873081
- Watcher (0.9.28) workshopId=3747526116
- YUICardExpansion (1.0.1) workshopId=3747562908
- YUICore (1.0.1) workshopId=3747562679
- YUISpireExpansion (1.0.1) workshopId=3747563416
- 储君卡图娘化 [RegentFemPortraits] (v1.0) workshopId=3747751411
- 弹幕尖塔 DanmakuSpire [DanmakuSpire] (v1.2.1) workshopId=3779807977
- 更多的假遗物 [Fake Merchant Expansion] (1.2.6) workshopId=3750281026
- 更好的角色遗物 [BetterCharacterRelics] (1.1.4) workshopId=3747717457
- 还不够难！(Not Enough Difficulty) [NotEnoughDifficulty] (1.0.1)
- 尖塔：琐事 [STS2_Things] (1.9.5) workshopId=3747607944
- 奖励附魔 [RewardEnchants] (0.3.0) workshopId=3779541807
- 卡牌融合 [CardFusion] (1.4.6) workshopId=3747615796
- 可重复附魔 [RepeatableEnchantments] (0.2.0) workshopId=3749669062
- 门扉Mod [DoormakerMod] (1.0.0) workshopId=3747565151
- 模组配置 ModConfig [BonModConfig] (0.1.6) workshopId=3749062616
- 能指任何人 [TargetAnyone] (0.4.0) workshopId=3768183266
- 万象辉星[RegentFX] [RegentFX] (0.5.1) workshopId=3747497501
- 向建筑师投掷药水 [ThrowPotionsAtArchitect] (1.4.1) workshopId=3785664177
- 招架弹反 [ParryMod] (0.5.2) workshopId=3747666429

</details>

> Only mod **names** are listed for compatibility validation; none of their assets are included or redistributed.
In one line: **the goal is not "works on my save", it is "does not make a mess inside someone else's modpack".**

---
## ✨ Core features

### 1. Act 4 · Elite gauntlet (rebuilt)

The act is "fight every base elite in the game once" — independent of what you skipped earlier.

- **Dynamic roster**: all base elites of the current version (**12** in beta v0.111.0: Overgrowth 3 +
  Underdocks 3 + Hive 3 + Glory 3), de-duplicated, in a fixed order (layer 1→3, pool order inside an act).
  **No number or act type is hardcoded** — act variants added by whole-act mods, and their elites, are picked
  up automatically.
- **Coordinate placement**: after the map is generated, the roster is pinned onto the combat rooms
  "nearest-to-start first" (BFS depth → column) as a `Dictionary<MapCoord, Encounter>`; when you enter a room
  the encounter is looked up by `RunState.CurrentMapCoord`.
  ⇒ **fully deterministic, duplicates impossible**, and save/load safe (the coordinate is the identity).
- **Adaptive depth**: depth is increased one step at a time until "combat rooms ≥ roster size"; surplus combat
  slots become rest sites, and if we are short, question marks become elites. The depth cap scales with the
  roster, so large content modpacks still fit.
- **Icons & compatibility**: every combat room in this act uses the elite icon, and their count equals the
  roster size exactly. The encounter hook only takes over when "this act is ours **and** the coordinate has a
  placement"; everything else falls through to vanilla.

### 2. Act 5 · Legend / Myth gauntlet

Two modes, switched by the `Act5Difficulty` config:

- **Trial**: Ancient Name → 2 random bosses → final boss (the one currently assigned), no rest sites.
- **Myth**: cycles through bosses "not defeated **before entering this act**", the layer-3 pick headlines as
  the final boss, and **rest sites = fights − 1** (one between every two fights).

Implementation notes:

- **Custom map node `Act5BossNode`** (derived from `NMapPoint`, not a reused vanilla node): its icon follows
  its own encounter (spine skeleton preferred, otherwise `*_icon.png` / `*_icon_outline.png`); clicking is
  gated on the raw state machine (`State == Travelable`, because vanilla's `IsTravelable` is short-circuited
  by debug travel). Focus / press / release, the vote container and the reticle are all re-implemented.
- **Straight-line map `Act5LinearMap`**: start (Ancient Name) → chain slots (disguised boss / rest site
  alternating) → `BossMapPoint` (final boss). The number of slots is generated dynamically and the row count
  adapts.
- **Disguised bosses**: boss icon and boss encounter, but **settled as a monster room** (monster-level
  rewards, does not end the act).
- **Ending gate**: only a fight **on the final-boss node** ends the act (`IsAtFinalBossNode`) — decoupled from
  the roster, so it can never drift.
- **Layout**: ≤3 slots straight / 4–6 forked (rest site centered) / ≥7 serpentine columns; the whole chain is
  scaled and centered so it never leaves the map.
- **Procedural effects**: boss icons are recoloured (Legend = black & gold, Myth = blood), plus a flowing
  light effect on the map veins.

### 3. Double bosses and synthetic map nodes

- **Per-act switches**: `Act1_DoubleBoss` … `Act4_DoubleBoss` (off by default).
- **Stable sampling**: the second boss is drawn from that act's own pool (first one excluded) using an
  **FNV-1a stable hash** — it never consumes the game's RNG, so host and client compute the same result and
  save/load is consistent.
- **Synthetic nodes**: a synthetic rest site (`InterBossHearth`, on by default) or shop (`InterBossShop`, off
  by default) can be inserted between the two bosses. The technique is "virtual coordinate + vanilla node
  factory + remove direct link + re-chain + redraw paths + recompute travelability", inspired by the workshop
  mod *Boss Gauntlet* (see the teardown notes below).
- **Ascension 10 fix**: N10's double boss is pinned back to **act 3**, so it cannot drift to the end of the
  act list after this mod extends the run.

### 4. Fully procedural visuals (no third-party assets)

- **Combat background redirect**: elite fights inside this mod's acts use "the act the enemy belongs to".
  Implemented by **rewriting `parentAct`**, so vanilla and **third-party background hooks keep working** — we
  do not stomp on another mod's custom arena.
- **Map vein recolouring**: the hue comes from that act's own map art (a saturation-weighted hue histogram
  peak, i.e. its "signature hue"), not from the near-black UI colour; stepping on a question mark / shop /
  treasure / rest site returns to the default purple. The recolouring lives in
  `ActModel.get_MapTopBg/MidBg/BotBg` (cache key includes the hue) and the vanilla re-bind is triggered when
  the layer changes.
- **Act 5 backdrops**: Legend = parchment with **per-run random** golden veins (derived from the run seed);
  Myth = blood-soaked (blood-coloured rim, dark red interior with veins, density driven by low-frequency
  noise).

### 5. Difficulty system

- **New formula** (replacing the upstream "three-stage multiplier chain"): HP
  `1 + ActFloor × 0.1 × Y/100`, damage `1 + ActFloor × 0.05 × X/100`; only applied when that act's extra
  scaling is enabled (`ActX_ExtraScaling`, on by default for acts 4/5).
- **Runtime HP patch**: besides setting HP at spawn, it also intercepts **in-combat max-HP changes**
  (TestSubject revival, ToughEgg hatching, centipede-style revival through `Heal`/`SetMaxHp`) so the
  multiplier cannot be bypassed.
- **Multi-mod aggregation**: pools are aggregated through `ModelDb.ActsByIndex`, so act variants from
  whole-act mods are included.

### 6. Multiplayer guarantees

- **Ack-based config sync**: host broadcasts the config → each client applies it and acks → **the host only
  starts once every client has acked**. Timeouts / failures refuse to start and show a popup instead of
  starting with mismatched configs. Both `StartRunLobby` and `LoadRunLobby` share this flow.
- **Deterministic hash fallback**: makes the `ModelDb` hash computation deterministic, removing handshake
  failures caused by differing mod environments (experimental).
- **Local-only switches**: e.g. the extra-speed multiplier is marked `[ConfigSyncIgnore]`, so every player can
  set their own.

### 7. Official settings screen injection

This mod's switches (e.g. "enable extra speed" / "speed multiplier") are injected into the **game's own
settings screen**, right next to the Fast Mode row: a vanilla row is cloned as a template, given a unique
name, and the vanilla row factory / router is patched to create and refresh it. The official UI, the BaseLib
config UI and the on-disk cfg stay in sync in all three directions.

### 8. Extra speed

Engine-level `TimeScale` acceleration that stacks multiplicatively with the game's built-in Fast Mode. Purely
local, so it does not affect multiplayer sync. Implemented as a static controller rather than a Node subclass,
so it cannot be destroyed with the scene.

### 9. Compatibility layer (a hard requirement here)

| Situation | Strategy |
|---|---|
| Another mod adds act variants | The layer index is **derived from data** (`ModelDb.ActsByIndex` / `RunState.Acts` / `act.Index`) — never hardcoded by type |
| Another mod occupies act 4 | This mod's two acts **shift to acts 5 and 6** and stay at the end of the list; `Act4HeartAutoShift` can disable it; the `Act4_*` / `Act5_*` switches still only control **our own** acts |
| Another mod adds elites / bosses | They enter the act-4 roster / act-5 plan automatically (through each act's own pool) |
| The roster would change as you fight | The roster is computed from the record **before entering the act** ⇒ constant for the whole act, save/load consistent |
| Another mod draws its own background | We only rewrite `parentAct`; the other mod's hooks run as usual — no takeover |
| Another mod changes the map / nodes | We only replace our own nodes; chaining and travelability stay with vanilla `RecalculateTravelability` |
| Another mod patches the same method | `ModCompat` detects it at runtime, yields, and prints a **compatibility report** at startup |
| Old saves | Class and field names are kept; defensive load guards rebuild lost mod-act data deterministically |

### 10. Engineering & tooling

Our own tools (in `tools/`):

- **`MakePck`** — generates the `.pck` programmatically (no Godot install).
- **`DumpIl`** — a **read-only assembly analyser** built on **Mono.Cecil**: it reads the type / method
  signatures and IL of locally installed assemblies to confirm how the game behaves (standard practice when
  developing mods). Every "how the game actually works" conclusion in this project came from it plus real
  logs, not from guessing.
- **`DumpApi` / `DumpStrings` / `FindRef` / `EnumProbe`** — type listings, string-table extraction, call-site
  search and enum probing; also read-only analysis, used to understand behaviour. No game code is produced or
  bundled.
- **`check-patch-targets.ps1`** — verifies that every Harmony patch target really exists in the target DLL, so
  patches cannot fail silently.

> **Compliance note**: the tools above are **read-only** (they read metadata/IL of locally installed
> assemblies) and are used to confirm behaviour and compatibility. This repository **does not contain, modify
> or redistribute** the game itself or any third-party mod's code or assets.

Discipline:

- **Per-class patch isolation** — every patch class runs in its own try/catch; a failure only prints
  `Patch class X failed (skipped)` and never takes the whole mod down.
- **Diagnostics first** — every patch entry logs unconditionally, so you can tell "not called / early return /
  threw" apart at a glance.

### 11. Documentation index

| Document | Content |
|---|---|
| `杀戮尖塔2-mod写作踩坑指南.md` | 37 real-world pitfalls + 12 chapters of methodology (Chinese) |
| `待办与交接-第三阶段.md` | Progress, TODOs, hard facts (handover entry point) |
| `BossGauntlet-合成节点法-拆解笔记.md` | Full teardown of inserting custom nodes into the map |
| `README-移植与构建.md` | Porting differences and offline build steps |

---

## 🎨 Per-act visuals & music: each act uses **the whole set from its own layer**

Rather than assembling a patchwork, every act takes its **map art / rest-site scene / combat background / BGM /
ambience** as one coherent set — and the combat background follows the enemy **fight by fight**:

| Act | Map art | Rest site / scenes | Combat background | BGM / ambience |
|---|---|---|---|---|
| **Acts 1–3** | Vanilla | Vanilla | Vanilla | Vanilla (this mod does not touch them) |
| **Act 4 (elite gauntlet)** | The map art of **this run's dominant source layer** (whichever layer has the most undefeated elites) | That layer's rest-site scene | **Per fight**, the assets of **the act the enemy belongs to** (the top boss included) | That layer's BGM; `AmbientSfx` also switches to that layer |
| **Act 5 (Legend / Myth)** | **Procedurally repainted**: Legend = Overgrowth art → parchment + per-run golden veins; Myth = Hive art → blood-soaked | The matching layer's rest-site scene | Each disguised boss uses **its own source act**; the final boss uses this act's theme | Ambience = act 3's set; every boss plays **its own `CustomBgm`** |

Two deliberate decisions about music (otherwise bosses drawn from other acts would be silent):

- **Load every bank**: acts 4/5 load **all three base layers' music banks** (not just "dominant layer + act 3"),
  because an encounter's own `BgmEvent` (e.g. `event:/music/act2_a2_v2`) lives in the bank of *its* act;
  a missing bank means `cannot find music path` and a **silent** fight.
- **Fallback**: if a boss's `CustomBgm` is still not in memory, `CustomActMissingBgmFallbackPatch` falls back to
  that act's track instead of letting the fight run in silence.
- Conversely, **all of this mod's own visuals are procedurally generated** (parchment / blood / vein recolouring /
  icon recolouring) — no third-party assets are added or redistributed.
## 🔍 Debug logging (the most underrated part)

### Principles

1. **One switch**: `General → Debug logging` (`DebugLogging`, off by default). When on, every decision the mod
   makes leaves a readable log line.
2. **Log first**: every patch entry logs unconditionally, so "was it even called?" is answered immediately.
3. **Logs are the evidence chain**: every internal-mechanism conclusion came from logs + IL; the debugging flow
   is "search by tag → inspect the decision inputs → locate the branch".
4. **Flood warning**: a normal run is tens of KB; if the log jumps to MBs, something is looping (we once
   captured **9.3 MB / 68,000 lines**, of which **42,629 lines** were the same three lines repeating in a
   self-recursion crash).

### Log tag reference

| Tag | Answers | Example |
|---|---|---|
| `[Act4] 精英落位` | Act-4 roster and order for this run | `[Act4] 精英落位: 12 个战斗房 ← 名单 12 个 \| 由下往上 = BYGONE_EFFIGY_ELITE → … → SOUL_NEXUS_ELITE` |
| `[Act4] 战斗房 (c,r) → 指定精英` | Which enemy is on a given cell | `[Act4] 战斗房 (3,5) → 指定精英 'PHROG_PARASITE_ELITE'` |
| `[Act4] 本场来源` | Which act the enemy belongs to | `[Act4] 本场来源 = 'X' → act 层 1 / 'Underdocks'（旅行色 180F24 / 招牌色相 258°）` |
| `[ActDepth]` | Depth solving and convergence | `[ActDepth] 收敛: 深度 8 rooms → 战斗房 15 ≥ 目标 12（逐格 0 轮）` |
| `[Act5] BOSS 编排` | Act-5 boss sequence and mode | `[Act5] BOSS 编排（极限）: 伪装=A → B → … → J \| 火堆间隔=True \| 最终BOSS='AEONGLASS_BOSS'` |
| `[Act5LinearMap] 建图` | Map skeleton parameters | `[Act5LinearMap] 建图: 网格 7x21 \| 链位 19（伪装BOSS 10 + 火堆 9） \| BOSS(3,20) = BossMapPoint` |
| `[Act5Mid] 巡检` | Per-slot travelability and state machine | `[Act5Mid] 巡检 (3,5) Monster: 前一个已走=True … State=Travelable→Travelable 可点=False` |
| `[Act5BossNode]` | Custom node lifecycle | `(3,1) 美术子树已搬入 \| size=(374,306) …` |
| `[Act5] 伪装BOSS` | Room-type rewrite | `[Act5] 'X' 不在最终BOSS节点 ⇒ 房间类型 Boss → Monster（按小怪结算）` |
| `[Act5] …不结束本幕` | Ending gate interception | `[Act5] 伪装BOSS 奖励界面「继续」→ 不结束本幕…` |
| `[背景折返]` | Combat-background ownership | `[背景折返] 'X'：parentAct Act4Model → 'TheBeyondAct'（自带背景=False）` |
| `[Theme]` / `[Stripe]` | Procedural textures and hue | `[Theme] 'Underdocks' 招牌色相 = 258°` / `[Stripe] 地图纹路 → 层 1，色相 258°` |
| `[DoubleBoss]` | Per-act layer detection | `[DoubleBoss] 本局 act 分层判定：1=Overgrowth(Index=0) \| 2=某模组Act2(Index=1)` |
| `[ActLayout]` | Act shift detection | `[ActLayout] 检测到第 4 幕已被 'X（HeartAct）' 占用 ⇒ 本模组自动顺延为第 5/6 幕` |
| `[Gauntlet]` / `[SynthHearth]` | Synthetic node injection | `[Gauntlet] 注入结果 = True` |
| `[MapGuard]` | Stuck-click interception | `[MapGuard] 忽略对「当前所在坐标」的点击 (3,1) —— 不投票` |
| `[ModCompat]` | Startup compatibility report | `[ModCompat] 关键 patch 点上的其它 mod：…` |
| `[Act4] 地图染色` | Why the map veins are tinted this way | `[Act4] 地图染色 ← 'X' 属于 Underdocks（层 1）色相 258°` |
| `[Act4] 地图校正` | Final ledger of depth / combat rooms / rest sites | `[Act4] 地图校正: 目标精英(名单)=12 \| 地图战斗房=14 \| 富余战斗位→火堆=2 \| 最终：战斗房=12` |
| `[Act4Probe]` | Seed-predictor state | `[Act4Probe] 房间数覆盖 patch 已装载（只装一次）` |
| `[Act5Mid] 布局形态 / 定位参照` | Chain layout, scaling and margins | `[Act5Mid] 布局形态=蛇形（4 栏/行 × 3 行）… \| 行距=567px 缩放=1.00 顶部留白=383px` |
| `[Act5Mid] 注入完成` | Cell contents and icon source | `[Act5Mid] 注入完成: 链位 19 \| (3,1) X [贴图] \| (3,3) Y [spine]` |
| `[Theme]` | Procedural textures and hue | `[Theme] 'Underdocks' 招牌色相 = 258°` / `BOSS 图标改色（传奇=黑金）` |
| `[Stripe]` | Map vein recolouring | `[Stripe] 地图纹路 → 层 1，色相 258°（重挂 3 张底图）` |
| `[N10DoubleBoss]` | Ascension-10 double-boss fix | `[N10DoubleBoss] …：清掉 N 个被误加的第二个 boss（act5 的保留不动）` |
| `[Gauntlet]` | Synthetic-node hand-over | `[Gauntlet] 建图前检查: act=… HasSecondBoss=…` / `注入结果 = True` |
| `[SynthHearth]` | Synthetic rest-site injection | `[SynthHearth] act3：N10 已接管双 boss，本 mod 不注入火堆` |
| `[Blueprint]` | Per-act build-window check | `[Blueprint] 第 N 层已构筑过，跳过` |
| `[MapPathsFix]` | Duplicate-key repair | `[MapPathsFix] 已清空 NMapScreen._paths（避免 DrawPaths 重复键崩溃）` |
| `[RunProgress]` | Enemy-ownership resolution | `[RunProgress] 'X' 有 3 个候选 act：Glory(默认) / … ⇒ 取 '…'` |
| `[BossInventory]` | Boss inventory dump | `[BossInventory] 当前层=… BossEncounter=… SecondBoss=…` |
| `[Settings]` / `[Slider]` | Settings-screen injection | `Settings injection: slider row 'MO_ExtraSpeedMultiplierRow' inserted at index 6` |
| `[ConfigSync]` | Multiplayer sync state machine | every step of broadcast / ack / timeout / refusing to start || `[PatchScope]` | Patch isolation | `Patch class X failed (skipped)` |

Sample lines are kept in the original Chinese (that is what the game actually prints).

### A complete evidence chain

A single fight can be reconstructed end-to-end from the log:

```text
[ActDepth]      收敛: 深度 8 rooms → 战斗房 15 ≥ 目标 12（逐格 0 轮）
[Act4] 地图校正: 目标精英(名单)=12 | 地图战斗房=14 | 富余战斗位→火堆=2 | 最终：战斗房=12
[Act4] 精英落位: 12 个战斗房 ← 名单 12 个 | 由下往上 = BYGONE_EFFIGY_ELITE → … → SOUL_NEXUS_ELITE
[Act4] 战斗房 (3,5) → 指定精英 'PHROG_PARASITE_ELITE'
[背景折返] 'PHROG_PARASITE_ELITE'：parentAct Act4Model → 'Overgrowth'（自带背景=False…）
[Act4] 本场来源 = 'PHROG_PARASITE_ELITE' → act 层 1 / 'Overgrowth'（旅行色 28231D / 招牌色相 38°）
[Stripe] 地图纹路 → 层 1，色相 38°（重挂 3 张底图）
```

⇒ From the log alone you can answer: who was chosen for this fight, why, why that background / colour, and
whether the map was recoloured as intended.

### Cases where the logs saved the day

1. **"Background lags one fight"** — the log showed the asset call happening *before* the source act was
   written ⇒ the real cause was "two call sites for background assets, the preload runs earlier", not a logic
   error.
2. **Self-recursion crash** — 9.3 MB of log with 42,629 repeated lines ⇒ located in 30 seconds as "an
   aggregator act picked itself as the source act".
3. **"One boss and straight to the Architect"** — scanning the whole assembly for the single caller of
   `SetLocalPlayerReady` plus a numeric mismatch in the log (room-type rewrite 96 times vs ending-gate
   interception 3 times) ⇒ located the "roster drift".
4. **"Myth rest sites disappeared"** — two log lines disagreed (`[Act5LinearMap]` vs `[Act5]`) ⇒ suspicion went
   straight to ordering (map built before the roster was finalised).
5. **"Modded act variant has no double boss"** — `[DoubleBoss]` prints the detected layer of every act ⇒
   instantly visible that the modded act was not detected at all, pointing at a hardcoded type table.

---

## 🛠️ Engineering highlights

- **Robust patching**: 60 HarmonyPatch attributes, all isolated per class, plus a target-existence check script.
- **Determinism first**: stable hashing for double-boss picks; the roster only depends on synced data
  (seed + record) so both ends compute the same result — no new random source.
- **Compatibility first**: layer and ownership decisions are all data-driven, and we shift acts instead of
  fighting over them; with third-party background hooks we cooperate rather than override.
- **Verifiable first**: every feature has a log tag, so users can verify it themselves.
- **Zero asset dependency**: all visuals are procedurally generated.
- **Own toolchain**: even the `.pck` packer is ours — no Godot editor required.

---

## ⚠️ Differences from upstream: removed / replaced in this branch

> These were trimmed or replaced **after talking to the original author** (to avoid maintaining two parallel
> implementations). The code has been deleted — only Godot `.uid` leftovers remain. They are **not** current
> features; please do not read this as missing functionality.

| Item | Handling |
|---|---|
| Map length / room density | Removed (`MapLengthPatch`, `MapDensityScalingPatch`, `SkipPruningForLongMapsPatch`) |
| Enemy removal list | Removed (`RemovalListPopup`) |
| Difficulty presets | Removed; only the X/Y factors and per-act switches remain |
| Desync diagnostic patch | Removed (`DesyncDiagnosticPatch`) |
| Act-5 early implementation | **Replaced** by this branch's `Act5/*` (custom node + straight-line map + two modes) |
| Early "map edge mist" overlay | Deleted (disabled on request, then removed: `Act5/ActMapOverlay.cs`) |
| `AvoidAdjacentEncounterDuplicate` / `EncounterDeduplicator` | Deleted (the new "fixed roster + coordinate placement" cannot repeat anyway) |

---

## 📦 Installation

### Requirements

- Slay the Spire 2 **public beta v0.111.0**
- [BaseLib](https://github.com/Alchyr/BaseLib-StS2) **3.4.7** (required, must match the game version)

### Steps

1. Get `NotEnoughDifficulty.dll` / `.pdb` / `.pck` / `.json`;
2. Copy them together into `<game>\mods\NotEnoughDifficulty\`;
3. Start the game and enable the mod in the mod list.

Logs: `%APPDATA%\SlayTheSpire2\logs\godot.log`.

---

## 🔧 Building (offline, self-contained)

```powershell
# 1) Dependencies (not shipped with this repo)
#    <repo>\_refs\game\       <- sts2.dll / 0Harmony.dll / GodotSharp.dll / MonoMod.*.dll / sts2.*.json
#                                from the game's data_sts2_windows_x86_64 folder
#    <repo>\_refs\baselib\    <- BaseLib.dll
#    <repo>\_refs\nuget-feed\ <- the four Godot 4.5.1 nupkgs
# 2) Compile
dotnet build --no-incremental          # output -> .\dist\
# 3) Pack the .pck (localization etc.; no Godot needed)
dotnet run --project tools\MakePck -- .\dist\NotEnoughDifficulty.pck NotEnoughDifficulty .\NotEnoughDifficulty (Get-ChildItem .\NotEnoughDifficulty\localization -Recurse -Filter *.json | % FullName)
```

> See [`README-移植与构建.md`](README-移植与构建.md) for the differences from the upstream build.

---

## ⚙️ Configuration overview

| Section | Content |
|---|---|
| General | Master switch, debug logging |
| Difficulty | Global HP / damage factor X/Y |
| ExtraScalingPerAct | Per-act extra scaling (on by default for acts 4/5) |
| ActComposition | Per-act double boss, hearth / shop between bosses, act-5 mode |
| Act4_EncWeights / EventWeights / BossWeights | Act-4 pool weight mixing |
| Act5Map | Whether act 5 uses the custom straight-line map |
| Compat | Auto-shift when act 4 is taken; force source-act background over self-backed enemies |
| BehaviorToggles / Speed | Behaviour switches, extra speed |

---

## ⚠️ Known limitations

- This branch builds against a local `_refs\` folder and an offline NuGet feed; merging upstream may require
  switching the reference style back.
- Some fixes only apply to **newly generated maps** (an act already generated in an old save is not repaired,
  e.g. act-5 rest-site counts).
- This English README is a translation of the branch README; the upstream English README is older.
- Multiplayer has **no protocol-level backward compatibility** — everyone must upgrade together.
- With extreme mod combinations another mod may still lose data in the save/load chain (this mod adds
  defensive guards so it does not hard-crash).

---

## 📝 Version history

| Version / commit | Changes |
|---|---|
| `beta-0.111-port` | Ported to beta v0.111.0 + BaseLib 3.4.7; **act 4 rebuilt** as a fixed roster with coordinate placement; **act 5 rebuilt** (custom node class, straight-line map, Legend/Myth modes, rest sites = fights − 1, only the final boss ends the act); **combat background follows the enemy's own act** (by rewriting `parentAct`, third-party friendly); **map veins recoloured per source act** (hue taken from the act's own art); **multi-mod coexistence** (data-driven layer detection, auto-shift to acts 5/6 when act 4 is taken, double bosses for modded act variants); plus 11 classes of in-game issues found and fixed during the port |

---

## 🙏 Credits

**The original**

- **[bwnotfound](https://github.com/bwnotfound)** — the **design and the entire original implementation** of
  this mod. This branch is a beta port **commissioned by him**; **the copyright is his**, and releases /
  distribution should go through the upstream repository.

**Extension layer & tools**

- [Alchyr](https://github.com/Alchyr) for [BaseLib](https://github.com/Alchyr/BaseLib-StS2) and
  [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2)
- [Harmony](https://github.com/pardeike/Harmony)
- [GlitchedReme](https://github.com/GlitchedReme) for the Chinese StS2 modding tutorials
- The author of the workshop mod **Boss Gauntlet** (v0.1.3) — the "synthetic hearth / shop between the two bosses" feature was, at a stage when our own technique was not yet mature, **modelled on their implementation** (both the double-boss setup and the hearth insertion benefited from it). If the original author objects, please contact [@Coll-ed](https://github.com/Coll-ed) and the relevant implementation will be **removed immediately**, with the feature rebuilt using our own node-injection design.

**Interoperability (new in this branch)**

- **ritzukage** for **ActsFromThePast** and **RitsuLib** — their acts / elites / bosses are drawn into this
  mod's acts 4 and 5, and their hand-drawn background is why we switched to "rewrite `parentAct` and let
  **their own hooks** run"
- **YUI Spire / Card Expansion**, **Act 4 Heart** and friends — used to validate whole-act mod coexistence
  (this mod shifts to acts 5/6 when act 4 is occupied)
- **Skin / card-art / localization mods** — used to validate that localization and settings injection do not
  clash

**Tooling & collaboration**

- **DeepSeek** (DeepSeek Harness / deepseek-flash) — the **AI collaborator** on this port: reading and
  refactoring the code, turning assembly-IL analysis into reusable conclusions, tracking down the in-game issues
  (background ownership, roster drift, rest-site ordering, the self-recursion crash, …), and writing the
  repository documentation (this README, the 37-pitfall guide, the handover notes).
**This branch**

- [@Coll-ed](https://github.com/Coll-ed) — the beta v0.111.0 port, the act 4/5 rebuilds, the multi-mod
  compatibility work, all the debugging and the documentation. **No third-party assets were added**; all
  visuals are procedurally generated.
