# StS2-NotEnoughDifficulty（还不够难！）· Beta 移植分支

[English](docs/README.en.md) | 中文

> ### 分支身份
> - **上游（原始设计与实现，著作权归原作者）**：<https://github.com/bwnotfound/StS2-NotEnoughDifficulty>
> - **工具与协作**

- **DeepSeek**（DeepSeek Harness / deepseek-flash）—— 本移植的 **AI 协作方**：代码通读与重构、
  把程序集 IL 的分析结论整理成可复用方法、实机问题排查（背景归属 / 名单漂移 / 火堆时序 / 自递归崩溃等），
  以及仓库文档（README、37 条踩坑指南、待办与交接）的撰写与整理。
**本分支**：`beta-0.111-port` —— **受原作者委托**进行的移植 + 扩展分支
> - **适配环境**：StS2 **Public Beta v0.111.0**（MegaDot / Godot 4.5.1）+ **BaseLib 3.4.7**
> - **移植**：[@Coll-ed](https://github.com/Coll-ed)；沿用上游 `LICENSE`

给《Slay the Spire 2》把原版 3 层塔扩展为 **5 层**的深度 Mod（第 4 幕若被其它模组占用，
本模组的两个幕会自动顺延为**第 5、6 幕**）：单人 / 多人联机均可用，多人下 host 端配置会自动同步给所有玩家。

---

## 📊 项目规模与交付

| 项 | 数据 |
|---|---|
| 代码体量 | **67 个 `.cs` / 9,988 行 / 16 个目录** |
| 改造深度 | **60 处 HarmonyPatch 标注**（约 48 个不同的游戏侧目标方法）、**313 处日志埋点** |
| 本地化 | 完整中英双语（配置项 title + description 全覆盖） |
| 构建质量 | `dotnet build --no-incremental` **0 错误 0 警告** |
| 交付形态 | `.dll + .pdb + .pck + .json`；`.pck` 由自研打包器 `tools/MakePck` **程序化生成**，无需安装 Godot |
| 构建自包含 | 游戏 / BaseLib 程序集引用本地 `_refs\`，NuGet 指向本地离线 Feed ⇒ **零网络依赖构建** |

---

## 🔗 兼容性：为什么把它当成第一等需求（本分支的侧重点）

多模组环境下，一个"加幕"的 mod 最容易坏的不是玩法，而是**和别人的东西抢同一块地**。
本分支把兼容性当成硬指标，带来的直接影响：

| 如果按"顺手写法"做 | 后果 | 本分支的做法 | 玩家端的影响 |
|---|---|---|---|
| 按 act **类型**判层号（`is Hive => 2` 这种表） | 别人的 act 变体认不出来 ⇒ 该层双 boss / 难度强化**静默失效**（没报错、也没效果） | 层号按 `ModelDb.ActsByIndex` / `RunState.Acts` / `act.Index` **数据判定** | 整合包里装了整幕模组，本 mod 的功能照常生效 |
| 两个 mod 都想占**第 4 幕** | 谁加载晚谁覆盖，甚至黑屏 | 检测到第 4 幕被占 ⇒ 本模组两个幕**自动顺延为第 5、6 幕** | 可以和 ACT 4 心脏这类模组同时装 |
| 名单按"还剩谁没打"算 | **边打边变**：节点与房间对不上，严重时"打一个 BOSS 直接进结局" | 名单按"**进入本幕之前**"的战绩算；"算不算 BOSS 房/能不能收尾"一律用**坐标**判 | 一幕之内行为恒定，读档一致 |
| 自己 new 一份背景资产去顶 | 覆盖掉别人自绘的竞技场 | **改写 `parentAct`**，让对方自己的钩子照常跑 | 别的模组精心画的场景能正确显示 |
| 抢着改地图节点 / 连线 / 可通行性 | 与别的改地图模组互相打架 | 只替换自己的节点，连锁与可通行性交回原版 `RecalculateTravelability` | 与"也改地图"的模组共存 |
| patch 一处失败就整体崩 | 一个不兼容点废掉整个 mod | 每个 patch 类独立 try/catch，失败只打 `Patch class X failed (skipped)` | 极端组合下仍能进游戏 |
| 启动时不知道装了谁 | 出问题只能靠猜 | `ModCompat` 运行时检测 + 放权 + 启动**兼容性报告** | 排障时一眼看到"关键 patch 点上还有哪些 mod" |

**实际用来验证的"体量大"的模组**（本分支的兼容性不是纸上谈兵）：

| 模组 | 体量 / 性质 | 它对本 mod 的考验 |
|---|---|---|
| **ActsFromThePast**（+ 前置库 **RitsuLib**） | 把《杀戮尖塔 1》的三幕整体搬进来：自带精英 / BOSS / **自绘战斗背景**，资源包上百 MB | 它的 act / 精英 / BOSS 会进本 mod 第 4、5 幕的抽取；它的自绘背景逼出了"**改写 `parentAct`** 而非自己造背景"的做法 |
| **YUI Spire Expansion** / **YUI Card Expansion** | 大型内容扩展（新卡、遗物、事件、BOSS） | 大量新 BOSS 进第 5 幕编排、新精英进第 4 幕名单；与它的配置页/设置项共存 |
| **Act 4 Heart** | 直接**占用第 4 幕** | 触发"本模组自动顺延为第 5、6 幕"这条路径（`Act4HeartAutoShift`） |
| 皮肤 / 卡图 / 汉化类（如奥卡皮肤、战士卡图包） | 几十 MB 的资源替换包 | 验证本地化与官方设置界面注入不被资源类模组干扰 |

**实战案例：57 个模组的整合包里连续两天通关**

> 在下列模组（作者环境，本机日志可确认其中 **54 个**在本局完成初始化）+ **本模组**同时启用的情况下，
> **连续 2 天成功通关该整合，稳定不报错**。这不是"理论兼容"，是实打实跑出来的。

<details><summary>展开：实测同时启用的模组（54 个）</summary>

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

> 只列出用于**兼容性验证**的模组名，不包含也不分发它们的任何资源。
一句话：**本分支的目标不是"在我的存档里能跑"，而是"在别人的整合包里也不添乱"。**

---
## ✨ 核心功能体系

### 1. 第 4 幕 · 精英连战（重制层）

本层核心体验为"遍历游戏中所有基础精英"，与玩家历史战绩无关。

- **动态名单**：涵盖当前版本全部基础精英（Beta v0.111.0 为 **12 个**：密林 3 + 暗港 3 + 巢穴 3 + 荣耀 3）。
  全局去重、顺序固定（层 1→3，Act 内按池顺序）。**未硬编码任何数字或 Act 类型** —— 整幕类模组新增的
  Act 变体及其精英会**自动纳入名单**。
- **坐标落位机制**：地图生成后，将名单按"离起点由近到远"（BFS 深度 → 列）逐个绑定至战斗房
  （`Dictionary<MapCoord, Encounter>`）；进房时通过 `RunState.CurrentMapCoord` 查表取怪。
  ⇒ **确定性极高、不可能重复**；读档一致（以坐标为身份标识）。
- **深度自适应**：从最低深度**逐格加深**，直到"战斗房数 ≥ 名单长度"；富余战斗位转为火堆，
  不足则将问号房转为精英房。深度上限随名单长度动态伸缩，兼容大型内容模组。
- **视觉与兼容**：本层战斗房统一使用精英图标，数量精确等于名单长度；
  仅在"本层属于本模组 **且** 坐标有落位"时接管取怪逻辑，其余情况交还原版。

### 2. 第 5 幕 · 传奇 / 神话双档连战

通过配置项 `Act5Difficulty` 切换两种模式：

- **考验（Trial）**：先古之名 → 随机 BOSS ×2 → 最终 BOSS（当前分配），中间无火堆。
- **神话（Extreme）**：循环选取"**进入本幕前未打过**"的 BOSS，第三场压轴作为最终 BOSS；
  **火堆数 = 场数 − 1**（每两场之间插入一个火堆）。

实现要点：

- **自研地图节点 `Act5BossNode`**：继承 `NMapPoint`，图标跟随 Encounter（优先 Spine 骨架，否则
  `*_icon.png` / `*_icon_outline.png`）；点击判定**仅识别原始状态机 `State == Travelable`**
  （避免被调试旅行短路），完整复刻原版聚焦 / 按下 / 抬起、投票容器及准星交互。
- **直线地图 `Act5LinearMap`**：起点（先古之名）→ 链位（伪装 BOSS / 火堆交替）→ `BossMapPoint`（最终 BOSS）；
  链位数量动态生成、行数自适应。
- **伪装 BOSS**：显示 BOSS 图标与 Encounter，但**按小怪房结算**（小怪级奖励、不结束本幕）。
- **收尾闸门**：仅当踩在最终 BOSS 节点（`IsAtFinalBossNode`）时才结束本幕 —— 与名单**解耦**，永不漂移。
- **布局策略**：链位 ≤3 直线 / 4–6 分叉（火堆居中）/ ≥7 蛇形分栏；整条链等比缩放居中，不溢出地图。
- **程序化特效**：BOSS 图标改色（传奇 = 黑金，神话 = 血色）、地图纹路光效流动。

### 3. 双 Boss 与「合成节点」

- **独立开关**：1~4 层各自配置 `Act1_DoubleBoss` … `Act4_DoubleBoss`（默认关闭）。
- **稳定抽样**：第二个 Boss 从该层池中排除首个后选出，采用 **FNV-1a 稳定散列** ——
  不吃游戏随机流，Host / Client 各自计算一致，读档一致。
- **合成节点**：双 Boss 间可插入**合成火堆**（`InterBossHearth` 默认开）或**合成商店**
  （`InterBossShop` 默认关）。实现手法为"虚拟坐标 + 原版节点工厂 + 删直连 + 重串链 + 重画路径 + 重算可通行"
  （这套做法**参考了工坊模组 Boss Gauntlet**，说明见致谢；我们的分析笔记见下）。
- **N10 修正**：进阶 10 的双 Boss 强制**钉回第 3 层**，防止因 Act 列表延长而漂移至末尾。

### 4. 全程序化视觉主题（零外部素材）

- **战斗背景折返**：本模组幕中的精英战，背景切换为"敌人所属的那一幕"。通过**改写 `parentAct`** 实现，
  确保原版与**第三方模组的背景钩子照常生效**，不覆盖他人自定义竞技场。
- **地图纹路换色**：色相取自该 Act 地图美术的"**招牌色相**"（饱和度加权色相直方图峰值），
  而非 UI 近黑色；踩问号 / 商店 / 宝箱 / 火堆时恢复默认紫。底图着色集成于
  `ActModel.get_MapTopBg/MidBg/BotBg`（缓存键含色相），层变化时触发原版重挂。
- **第 5 幕专属底图**：传奇 = 羊皮纸 + **每局随机**金色纹路（Run Seed 派生噪声）；
  神话 = 血染风格（外围血液色、内部暗红 + 脉络，浓度由低频噪声控制）。

### 5. 难度系统

- **新公式**（取代上游旧版"三层倍率链"）：血量 `1 + ActFloor × 0.1 × Y/100`、
  伤害 `1 + ActFloor × 0.05 × X/100`；仅在启用对应层额外强化（`ActX_ExtraScaling`）时生效
  （第 4/5 幕默认开启）。
- **运行时 HP 补丁**：除出生设值外，还拦截**战斗中最大生命变更**（如 TestSubject 复活、ToughEgg 孵化、
  千足虫复活类 `Heal`/`SetMaxHp`），防止倍率被绕过。
- **多模组聚合**：通过 `ModelDb.ActsByIndex` 聚合各层池子，整幕模组的 Act 变体同样计入。

### 6. 多人联机保障

- **Ack-Based 配置同步**：Host 广播配置 → Client Apply 后回 Ack → **Host 收齐全 Ack 才开局**；
  超时 / 失败会拒绝开局并弹窗提示，杜绝"不同配置开局"。`StartRunLobby` 与 `LoadRunLobby` 共用此流程。
- **确定性 Hash 兜底**：使 `ModelDb` Hash 计算确定化，消除 Mod 环境差异导致的握手失败（实验性）。
- **本地开关隔离**：如"额外加速倍率"标记 `[ConfigSyncIgnore]`，允许每位玩家独立设置。

### 7. 官方设置界面注入

将本 Mod 开关（如"启用额外加速 / 加速倍率"）注入**官方设置页**，紧贴 FastMode 行：
复制 Base Game Row 为模板、赋予唯一 Name、Patch 原版 Row 工厂 / 路由进行创建与刷新。
实现**官方界面 ↔ BaseLib 配置界面 ↔ 磁盘 Cfg** 三处双向实时同步。

### 8. 额外加速

引擎级 `TimeScale` 全局加速，与游戏自带"快速模式"**相乘叠加**；纯本地表现，不影响联机同步。
采用 static 控制器而非 Node 子类，避免随场景销毁而失效。

### 9. 兼容层设计（硬指标）

| 场景 | 处理策略 |
|---|---|
| 其他模组增加 Act 变体 | 层号按 `ModelDb.ActsByIndex` / `RunState.Acts` / `act.Index` **数据判定**，绝不按类型写死 |
| 其他模组占用第 4 幕 | 本模组两幕**自动顺延为第 5、6 幕**并排在列表末尾；`Act4HeartAutoShift` 可关；`Act4_*` / `Act5_*` 开关仍只作用于本模组自己的幕 |
| 其他模组新增精英 / BOSS | 自动进入第 4 幕名单 / 第 5 幕编排（走各 Act 自有池子） |
| 名单"边打边变" | 名单按"**进入本幕之前**"的战绩计算 ⇒ 整幕内恒定、读档一致 |
| 其他模组自定义背景 | 仅改写 `parentAct`，对方钩子自行执行，不抢占 |
| 其他模组修改地图 / 节点 | 仅替换自身节点；连锁与可通行性交回原版 `RecalculateTravelability` |
| 其他模组 Patch 同一方法 | `ModCompat` 运行时检测 + 放权 + 启动时输出**兼容性报告** |
| 旧存档兼容 | 保留类名 / 字段名；读档防御兜底（Mod Act 数据丢失时按池确定性重建） |

### 10. 工程与工具链

自研工具集（`tools/`）：

- **`MakePck`**：程序化生成 `.pck`，免装 Godot。
- **`DumpIl`**：基于 **Mono.Cecil** 的**只读程序集分析器** —— 读取已安装程序集的**类型/方法签名与 IL**，
  用来确认游戏内部行为（mod 开发里确认公开 API 与行为的标准做法），本项目所有"游戏内部机制"结论
  都来自它 + 实机日志，而不是靠猜。
- **`DumpApi` / `DumpStrings` / `FindRef` / `EnumProbe`**：类型清单 / 字符串表提取 / 调用点搜索 / 枚举探测
  —— 同样是只读分析，服务于"把行为搞清楚"，不产出也不包含任何游戏代码。
- **`check-patch-targets.ps1`**：校验所有 Harmony Patch 目标在目标 DLL 中**真实存在**，防止静默失效。

> **合规说明**：上述工具只做**只读分析**（读取本地已安装程序集的元数据/IL），用于确认行为与兼容性；
> 本仓库**不包含、不修改、也不重新分发**游戏本体或任何第三方模组的代码与资源。

工程纪律：

- **逐类隔离 Patch**：每个 Patch 类单独 Try/Catch，失败仅打印 `Patch class X failed (skipped)`，
  单点故障不拖垮整体。
- **诊断优先**：每个 Patch 入口**无条件先打一行日志**，用于区分"未被调用 / 提前 Return / 抛异常"。

### 11. 仓库文档索引

| 文档 | 内容 |
|---|---|
| `杀戮尖塔2-mod写作踩坑指南.md` | **37 条实战坑 + 12 章方法论** |
| `待办与交接-第三阶段.md` | 进度 / 待办 / 硬事实（交接入口） |
| `BossGauntlet-合成节点法-拆解笔记.md` | 地图插入自定义节点的完整手法 |
| `README-移植与构建.md` | 移植差异与离线构建步骤 |

---

## 🎨 每层的视觉与音乐：**整层全部取自"对应的那一层"**

不想做"贴图拼盘"，所以每一幕的**地图美术 / 篝火场景 / 战斗背景 / BGM / 环境音**都按"这一层是谁"整套取用，
并且**逐场**跟着敌人的来源走：

| 幕 | 地图美术 | 篝火 / 场景 | 战斗背景 | BGM / 环境音 |
|---|---|---|---|---|
| **第 1~3 幕** | 原版 | 原版 | 原版 | 原版（本模组完全不碰） |
| **第 4 幕（精英连战）** | 取**本局主导来源层**那张地图美术（哪一层的未打精英最多就用哪一层） | 同主导来源层的篝火场景 | **逐场**用**敌人自己所属那一幕**的资产（连顶端 BOSS 也一样） | 同主导来源层；`AmbientSfx` 也切到该层的环境音 |
| **第 5 幕（传奇 / 神话）** | **程序化重绘**：传奇=密林底图→羊皮纸+每局随机金纹；神话=巢穴底图→血染 | 取对应主题那一层的篝火场景 | 每个伪装 BOSS 按**自己的来源幕**；最终 BOSS 用本幕主题 | 环境音 = act3 那一套；每个 BOSS 播**它自己的 `CustomBgm`** |

**音乐上特意做的两件事**（否则抽到别层的 BOSS 会没声音）：

- **bank 全量加载**：第 4/5 幕的 `MusicBankPaths` 把**三个基础层的音乐 bank 全部加载**
  （不是只加载"主导层 + act3"）—— 因为 encounter 自带的 `BgmEvent`（如 `event:/music/act2_a2_v2`）
  属于哪个 bank 由那个 act 决定，漏加载就会 `cannot find music path` 且**静音**。
- **兜底**：万一某个 BOSS 的 `CustomBgm` 仍不在内存里，`CustomActMissingBgmFallbackPatch` 会把它兜到该幕的曲子，
  而不是让战斗静默无声。
- 反之，**本模组自己的视觉全部是程序化生成**（羊皮纸 / 血染 / 纹路换色 / 图标改色），
  不引入、也不分发任何第三方素材。
## 🔍 调试日志体系（核心亮点）

### 设计原则

1. **单一开关**：`General → 日志调试`（`DebugLogging`，默认关），开启后 Mod 所有决策均留下可读中文日志。
2. **入口即记录**：Patch 入口无条件先打一行，快速判断功能是否被调用。
3. **日志即证据链**：所有内部机制结论源自日志 + IL；排障流程为"按标签搜日志 → 看决策输入 → 定位具体判断"。
4. **洪泛预警**：正常一轮几十 KB；若涨至 MB 级即为循环刷屏
   （曾捕获 **9.3 MB / 68,000 行**日志，其中 **42,629 行**为同一组三行重复的自递归崩溃）。

### 日志标签总表

| 标签 | 回答什么问题 | 典型日志示例 |
|---|---|---|
| `[Act4] 精英落位` | 本局第 4 幕精英名单与顺序 | `[Act4] 精英落位: 12 个战斗房 ← 名单 12 个 \| 由下往上 = BYGONE_EFFIGY_ELITE → … → SOUL_NEXUS_ELITE` |
| `[Act4] 战斗房 (c,r) → 指定精英` | 特定格子对应的敌人 | `[Act4] 战斗房 (3,5) → 指定精英 'PHROG_PARASITE_ELITE'` |
| `[Act4] 本场来源` | 敌人归属幕 | `[Act4] 本场来源 = 'X' → act 层 1 / 'Underdocks'（旅行色 180F24 / 招牌色相 258°）` |
| `[Act4] 地图染色` | 地图纹路染色依据 | `[Act4] 地图染色 ← 'X' 属于 Underdocks（层 1）色相 258°` |
| `[Act4] 地图校正` | 深度 / 战斗房 / 火堆最终账目 | `[Act4] 地图校正: 目标精英(名单)=12 \| 地图战斗房=14 \| 富余战斗位→火堆=2 \| 最终：战斗房=12` |
| `[ActDepth]` | 深度求解与收敛 | `[ActDepth] 收敛: 深度 8 rooms → 战斗房 15 ≥ 目标 12（逐格 0 轮）` |
| `[Act4Probe]` | 种子预测器状态 | `[Act4Probe] 房间数覆盖 patch 已装载（只装一次）` |
| `[Act5] BOSS 编排` | 第 5 幕 BOSS 序列与档位 | `[Act5] BOSS 编排（极限）: 伪装=A → B → … → J \| 火堆间隔=True \| 最终BOSS='AEONGLASS_BOSS'` |
| `[Act5LinearMap] 建图` | 地图骨架参数 | `[Act5LinearMap] 建图: 网格 7x21 \| 链位 19（伪装BOSS 10 + 火堆 9） \| BOSS(3,20) = BossMapPoint` |
| `[Act5Mid] 布局形态 / 定位参照` | 链位摆放与缩放 | `[Act5Mid] 布局形态=蛇形（4 栏/行 × 3 行）… \| 行距=567px 缩放=1.00 顶部留白=383px` |
| `[Act5Mid] 巡检` | 节点可通行性与状态机 | `[Act5Mid] 巡检 (3,5) Monster: 前一个已走=True … State=Travelable→Travelable 可点=False` |
| `[Act5Mid] 注入完成` | 格子内容与图标 | `[Act5Mid] 注入完成: 链位 19 \| (3,1) X [贴图] \| (3,3) Y [spine]` |
| `[Act5BossNode]` | 自研节点生命周期 | `(3,1) 美术子树已搬入 \| size=(374,306) …` / `鼠标按下 \| enabled=True State=Travelable` |
| `[Act5] 伪装BOSS` | 房间类型改写 | `[Act5] 'X' 不在最终BOSS节点 ⇒ 房间类型 Boss → Monster（按小怪结算）` |
| `[Act5] …不结束本幕` | 收尾闸门拦截 | `[Act5] 伪装BOSS 奖励界面「继续」→ 不结束本幕：按原版双BOSS中间那步关界面回地图` |
| `[背景折返]` | 战斗背景归属 | `[背景折返] 'X'：parentAct Act4Model → 'TheBeyondAct'（自带背景=False）` |
| `[Theme]` | 程序化贴图与色相 | `[Theme] 'Underdocks' 招牌色相 = 258°` / `BOSS 图标改色（传奇=黑金）` |
| `[Stripe]` | 地图纹路换色 | `[Stripe] 地图纹路 → 层 1，色相 258°（重挂 3 张底图）` |
| `[DoubleBoss]` | Act 分层判定 | `[DoubleBoss] 本局 act 分层判定：1=Overgrowth(Index=0) \| 2=某模组Act2(Index=1)` |
| `[ActLayout]` | 幕顺延检测 | `[ActLayout] 检测到第 4 幕已被 'X（HeartAct）' 占用 ⇒ 本模组自动顺延为第 5/6 幕` |
| `[N10DoubleBoss]` | N10 双 Boss 修正 | `[N10DoubleBoss] …：清掉 N 个被误加的第二个 boss（act5 的保留不动）` |
| `[Gauntlet]` | 合成节点接手 | `[Gauntlet] 建图前检查: act=… HasSecondBoss=…` / `注入结果 = True` |
| `[SynthHearth]` | 合成火堆注入 | `[SynthHearth] act3：N10 已接管双 boss，本 mod 不注入火堆` |
| `[Blueprint]` | 构筑窗口检查 | `[Blueprint] 第 N 层已构筑过，跳过` |
| `[MapGuard]` | 卡死点击拦截 | `[MapGuard] 忽略对「当前所在坐标」的点击 (3,1) —— 不投票` |
| `[MapPathsFix]` | 重复键修复 | `[MapPathsFix] 已清空 NMapScreen._paths（避免 DrawPaths 重复键崩溃）` |
| `[RunProgress]` | 敌人归属判定 | `[RunProgress] 'X' 有 3 个候选 act：Glory(默认) / … ⇒ 取 '…'` |
| `[ModCompat]` | 兼容性报告 | `[ModCompat] 关键 patch 点上的其它 mod：…` |
| `[BossInventory]` | BOSS 清点 | `[BossInventory] 当前层=… BossEncounter=… SecondBoss=…` |
| `[Settings]` / `[Slider]` | 设置页注入 | `Settings injection: slider row 'MO_ExtraSpeedMultiplierRow' inserted at index 6` |
| `[ConfigSync]` | 联机同步状态 | 广播 / ack / 超时 / 拒绝开局的每一步 |
| `[PatchScope]` | Patch 隔离 | `Patch class X failed (skipped)` |

### 完整证据链示例

同一场战斗可通过日志完整复现决策过程：

```text
[ActDepth]      收敛: 深度 8 rooms → 战斗房 15 ≥ 目标 12（逐格 0 轮）
[Act4] 地图校正: 目标精英(名单)=12 | 地图战斗房=14 | 富余战斗位→火堆=2 | 最终：战斗房=12
[Act4] 精英落位: 12 个战斗房 ← 名单 12 个 | 由下往上 = BYGONE_EFFIGY_ELITE → … → SOUL_NEXUS_ELITE
[Act4] 战斗房 (3,5) → 指定精英 'PHROG_PARASITE_ELITE'
[背景折返] 'PHROG_PARASITE_ELITE'：parentAct Act4Model → 'Overgrowth'（自带背景=False…）
[Act4] 本场来源 = 'PHROG_PARASITE_ELITE' → act 层 1 / 'Overgrowth'（旅行色 28231D / 招牌色相 38°）
[Act4] 地图染色 ← 'PHROG_PARASITE_ELITE' 属于 Overgrowth（层 1）色相 38°
[Stripe] 地图纹路 → 层 1，色相 38°（重挂 3 张底图）
```

⇒ 仅凭日志即可回答："这一场是谁决定的、为什么是他、背景 / 配色为什么是这个、地图是否按预期染色"。

### 日志实战救场案例

1. **"背景慢一场"**：日志显示"取资产"早于"写入本场来源" ⇒ 定位到"背景资产有**两个调用点**，
   预载比房间建立更早"，而非算法问题。
2. **自递归崩溃**：日志 9.3 MB、42,629 行重复 ⇒ 30 秒定位为"**聚合 Act 把自己选成了来源幕**"，
   无需读崩溃堆栈。
3. **"打完 BOSS 直接进建筑师"**：全库扫描 `SetLocalPlayerReady` **唯一调用点** + 日志数字差
   （房间类型改写 96 次 vs 收尾拦截 3 次）⇒ 定位"名单漂移"。
4. **"神话火堆消失"**：`[Act5LinearMap]` 与 `[Act5]` 两处数字不符 ⇒ 直接怀疑时序（建图早于名单定稿）。
5. **模组 Act 变体无双 Boss**：`[DoubleBoss]` 打出每个 Act 层号 ⇒ 一眼看出"模组 Act 未判出"，
   定位"按类型写死的映射表"。

---

## 🛠️ 工程卖点总结

- **稳健 Patch**：60 处 HarmonyPatch 标注，全部逐类隔离 + 目标存在性校验脚本。
- **确定性优先**：双 Boss 抽样用稳定散列，名单仅依赖同步数据（Seed + 战绩），多人两端计算一致，
  不引入新随机源。
- **兼容优先**：层号 / 归属全按数据判定，遇占用自动顺延；与第三方背景钩子**合作而非覆盖**。
- **可验证优先**：每个功能均有对应日志标签，用户可自行验证。
- **零素材依赖**：所有视觉效果均为程序化生成。
- **工具链自研**：连 `.pck` 打包器均为自研，摆脱 Godot 编辑器依赖。

---

## ⚠️ 与上游的差异：本分支移除 / 替换的功能

> 这些是移植过程中**经与原作者沟通后**一并精简/替换的部分（避免维护两份重复实现）；
> 代码里已删除，仅剩 Godot `.uid` 残留。**不是**现有功能，请勿据此判断缺失。

| 项 | 处理 |
|---|---|
| 地图长度 / 房间密度调节 | 移除（`MapLengthPatch`、`MapDensityScalingPatch`、`SkipPruningForLongMapsPatch`） |
| 敌人移除列表 | 移除（`RemovalListPopup`） |
| 难度预设（一键档位） | 移除，仅保留 X/Y 系数 + 各层开关 |
| Desync 诊断 Patch | 移除（`DesyncDiagnosticPatch`） |
| 第 5 幕的早期实现 | 由本分支的 `Act5/*`（自研节点 + 直线地图 + 双档连战）**替换** |
| 早期"地图边缘雾气"叠加层 | 删除（按需求停用后整文件清理：`Act5/ActMapOverlay.cs`） |
| `AvoidAdjacentEncounterDuplicate` / `EncounterDeduplicator` | 删除（新"确定名单 + 坐标落位"机制已不可能重复） |

---

## 📦 安装

### 依赖

- Slay the Spire 2 **public beta v0.111.0**
- [BaseLib](https://github.com/Alchyr/BaseLib-StS2) **3.4.7**（必须，且需与游戏版本匹配）

### 步骤

1. 下载 `NotEnoughDifficulty.dll` / `.pdb` / `.pck` / `.json`；
2. 一起放入 `<游戏目录>\mods\NotEnoughDifficulty\`；
3. 启动游戏并在 Mod 列表启用。

日志位于 `%APPDATA%\SlayTheSpire2\logs\godot.log`。

---

## 🔧 构建（离线 · 自包含）

```powershell
# 1) 依赖（本仓库不含，需自备）
#    <仓库根>\_refs\game\      ← 游戏 data_sts2_windows_x86_64 下的 sts2.dll / 0Harmony.dll /
#                                 GodotSharp.dll / MonoMod.*.dll / sts2.*.json
#    <仓库根>\_refs\baselib\   ← BaseLib.dll
#    <仓库根>\_refs\nuget-feed\ ← Godot 4.5.1 的 4 个 nupkg
# 2) 编译
dotnet build --no-incremental          # 产物 → .\dist\
# 3) 打包 .pck（本地化等资源；免装 Godot）
dotnet run --project tools\MakePck -- .\dist\NotEnoughDifficulty.pck NotEnoughDifficulty .\NotEnoughDifficulty (Get-ChildItem .\NotEnoughDifficulty\localization -Recurse -Filter *.json | % FullName)
```

> 与上游构建的差异见 [`README-移植与构建.md`](README-移植与构建.md)。

---

## ⚙️ 配置项一览

| 分区 | 内容 |
|---|---|
| General | 总开关、日志调试（排障时打开） |
| Difficulty | 全局 HP / 伤害倍率系数 X/Y |
| ExtraScalingPerAct | 各层额外强化开关（第 4/5 幕默认开） |
| ActComposition | 各层双 boss、双 boss 间是否插火堆 / 商店、第 5 幕档位 |
| Act4_EncWeights / EventWeights / BossWeights | 第 4 幕池子权重混合 |
| Act5Map | 第 5 幕是否使用自定义直线地图 |
| Compat | 第 4 幕被占用时自动顺延；自带背景的敌人是否强制按来源幕覆盖 |
| BehaviorToggles / Speed | 行为开关、额外加速 |

---

## ⚠️ 已知限制

- 本分支使用 `_refs\` 本地引用 + 离线 NuGet Feed 构建，合入上游前可能需切换引用方式。
- 部分修复**仅对新建地图生效**（旧存档已生成的幕不会补全，如第 5 幕火堆数量）。
- 英文 README（`docs/README.en.md`）是本分支中文 README 的译文；**上游**的英文 README 内容更旧。
- 多人模式**无协议层向后兼容**，升级需所有玩家同步。
- 极端多 Mod 组合下，其他模组仍可能在读档链丢数据（本 Mod 已加防御兜底避免硬崩）。
- 早期"地图边缘雾气"叠加层已**整文件删除**（`Act5/ActMapOverlay.cs`）：当前不会有雾气；
  想要"脉络流动"特效的话需要重新实现。

---

## 📝 版本历史

| 版本 / 提交 | 主要变化 |
|---|---|
| `beta-0.111-port` | 移植到 beta v0.111.0 + BaseLib 3.4.7；**第 4 幕重制**为"确定名单 + 坐标落位"；**第 5 幕重制**（自研节点类 / 直线地图 / 传奇·神话双档 / 火堆 = 场数 − 1 / 仅最终 BOSS 收尾）；**战斗背景按来源幕**（改写 `parentAct`，兼容第三方）；**地图纹路按来源幕换色**（美术统计招牌色相）；**多模组共存**（层号数据判定、第 4 幕被占自动顺延 5/6、双 boss 支持模组 Act 变体）；并修掉移植过程中的 11 类实机问题 |

---

## 🙏 致谢

**本体**

- **[bwnotfound](https://github.com/bwnotfound)** —— 本 Mod 的**设计与全部原始实现**；本分支是**受他委托**
  做的 beta 移植，**著作权归他**，发布 / 分发请以原仓库为准。

**扩展层与工具**

- [Alchyr](https://github.com/Alchyr) 的 [BaseLib](https://github.com/Alchyr/BaseLib-StS2) 与 [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2)
- [Harmony](https://github.com/pardeike/Harmony)
- [GlitchedReme](https://github.com/GlitchedReme) 的 [中文 STS2 modding 教程](https://github.com/GlitchedReme/SlayTheSpire2ModdingTutorials)
- 创意工坊模组 **Boss Gauntlet**（v0.1.3）的作者 —— 本模组"双 BOSS 之间的合成火堆 / 商店"这一功能，早期因技术尚不成熟，**参考了他的实现思路与做法**（双 BOSS 的实现与火堆添加均受益于此）。如果原作者认为不妥，请随时联系 [@Coll-ed](https://github.com/Coll-ed)：我会**立即移除相关实现**，并改用自行重新设计的节点注入方案来补齐功能。

**兼容性互操作（本分支新增的致谢）**

- **ritzukage** 的 **ActsFromThePast** 与 **RitsuLib** —— 它的 Act / 精英 / BOSS 会被本 Mod 抽取；
  它的自绘战斗背景促使本移植改为"改写 `parentAct`、让**它自己的钩子照常生效**"
- **YUI 系列扩展**、**Act 4 Heart** 等模组 —— 用于验证"与整幕模组共存"（第 4 幕被占用时自动顺延 5/6）
- **皮肤 / 卡图 / 汉化类模组** —— 用于验证本地化与设置界面注入不打架

**工具与协作**

- **DeepSeek**（DeepSeek Harness / deepseek-flash）—— 本移植的 **AI 协作方**：代码通读与重构、
  把程序集 IL 的分析结论整理成可复用方法、实机问题排查（背景归属 / 名单漂移 / 火堆时序 / 自递归崩溃等），
  以及仓库文档（README、37 条踩坑指南、待办与交接）的撰写与整理。
**本分支**

- [@Coll-ed](https://github.com/Coll-ed) —— beta v0.111.0 移植、第 4/5 幕重制、多模组共存改造、排查与文档。
  本分支**不新增任何第三方素材**，视觉均为程序化生成。
