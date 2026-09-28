using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

/// ## 参考实现声明
/// 本模组"双 BOSS 之间的合成火堆 / 商店"这一功能，早期因技术尚不成熟，**参考了创意工坊模组
/// Boss Gauntlet（v0.1.3）的实现思路与做法**（双 BOSS 的实现与火堆添加均受益于此）。
///
/// 如果原作者认为不妥，请联系 @Coll-ed（https://github.com/Coll-ed）：
/// 我会立即移除相关实现，并改用自行重新设计的节点注入方案来补齐功能。

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     双 boss 的「合成火堆/商店」——**实现思路对齐工坊模组 Boss Gauntlet v0.1.3（灵感来源）**。
///
/// ## 为什么沿用它的思路而不是自己另设计一套
/// 我在自己设计上连续失败多次，最后从 IL 里确认：往原版地图里插节点这件事，
/// **视觉、可通行性、焦点、进入、离开、读档、多人投票**七个环节都得接，
/// 少接一个就表现为"画出来了但点不动 / 进去卡住 / 读档崩"。
/// 作者（婉拒菲尔兹）已经把这条链路跑通并写在 README 里，所以这里逐条对齐它的 patch 面。
///
/// ## 与 BossGauntlet 的 patch 面对照（方法名一一对应）
/// | BossGauntlet | 本文件 | 作用 |
/// |---|---|---|
/// | `RunStateCreateForNewRunPostfix` | 同名 | 新 run 时把配置固化进 run data |
/// | `RunManagerGenerateMapPrefix` | 同名 | 地图生成前赋第二个 boss |
/// | `NMapScreenSetMapPostfix` | 同名 | 注入合成节点视觉 + 删直连 + 重串链 + 画线 |
/// | `NMapScreenRecalculateTravelabilityPostfix` | 同名 | 重算后补可通行性 |
/// | `RunManagerProceedFromTerminalRewardsScreenPrefix` | 同名 | 第一个 boss 打完的"继续" |
/// | `RunManagerEnterMapCoordPrefix` | 同名 | 进入合成房间（接管） |
/// | `MapSelectionSynchronizerPlayerVotedForMapCoordPrefix` | 同名 | 多人投票坐标重映射 |
/// | `RunManagerLoadIntoLatestMapCoordPrefix` | 同名 | 读档落在合成节点上 |
/// | `NRestSiteRoomOnProceedButtonReleasedPrefix` | 同名 | 离开火堆收尾 |
/// | `NMerchantRoomHideScreenPrefix` | 同名 | 离开商店收尾 |
///
/// ## 本模组额外加的一道闸（BossGauntlet 没有）
/// | 本文件 | 作用 |
/// |---|---|
/// | `NMapPointOnReleasePrefix` | **没进火堆也能进第二个 BOSS**：`OnRelease` 第一行就是 `if (!IsTravelable) return;`，
/// 这里在它之前把第二个 BOSS 节点提成 `Travelable`（见 <see cref="SyntheticHearth.TryAllowSecondBossClick" />） |
/// </summary>
[HarmonyPatch]
public static class BossGauntletStylePatches
{
    // ============================================================
    // 1) 新 run：把"这一局要不要双 boss / 插哪些房间"记进 run state
    //    （我们读配置即可，所以这里只做日志与启动顺序确认）
    // ============================================================

    [HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
    [HarmonyPostfix]
    public static void RunStateCreateForNewRunPostfix(RunState __result)
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(RunStateCreateForNewRunPostfix), () =>
        {
            SyntheticHearth.ResetForNewRun();
            ActBlueprint.Reset();             // 每层构筑记录归零
            ActLayout.Reset();                // 重新检测"第 4 幕是否被别的模组占用"

            // ★ 兼容 ACT 4 心脏这类模组：如果本模组的幕被别的模组插到了前面（Harmony 顺序不定），
            //   在 run 刚创建、还没有任何按 act 索引的数据时纠正为"排在末尾" ⇒ 稳定成为第 5/6 幕。
            ActLayout.EnsureOursAreLast(__result);

            MainFile.DebugLog(
                $"[Gauntlet] 新 run 初始化: 火堆={NotEnoughDifficultyConfig.InterBossHearth} " +
                $"商店={NotEnoughDifficultyConfig.InterBossShop} | " +
                $"本模组幕位=第 {ActLayout.OurFirstLayer}/{ActLayout.OurSecondLayer} 幕" +
                $"（第 4 幕占用者：{ActLayout.ForeignAct4?.Id?.Entry ?? "无"}）");

            // ★ Info 级：把**难度强化**的生效口径逐层打出来 —— 玩家反馈"强化没有正确启用、
            //   血量不变"时，这一行就能立刻区分"开关没开（含设置界面没保存）"与"开关开了却没生效"。
            MainFile.Logger.Info(
                "[难度] 本局强化开关: " +
                $"act1={NotEnoughDifficultyConfig.Act1_ExtraScaling} " +
                $"act2={NotEnoughDifficultyConfig.Act2_ExtraScaling} " +
                $"act3={NotEnoughDifficultyConfig.Act3_ExtraScaling} " +
                $"act4(本模组第1幕)={NotEnoughDifficultyConfig.Act4_ExtraScaling} " +
                $"act5(本模组第2幕)={NotEnoughDifficultyConfig.Act5_ExtraScaling} " +
                $"| 血量系数Y={NotEnoughDifficultyConfig.HpScaleFactor} " +
                $"攻击系数X={NotEnoughDifficultyConfig.DmgScaleFactor} " +
                "（血量 = 1 + ActFloor×0.1×Y；攻击 = 1 + ActFloor×0.05×X）");
        });
    }

    // ============================================================
    // 2) 地图生成前：赋第二个 boss（**只在建图前**，BossGauntlet 的关键时序）
    // ============================================================

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateMap))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static void RunManagerGenerateMapPrefix(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(RunManagerGenerateMapPrefix), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            var act = state?.Act;
            // 配置槽位（不是绝对层号）：本模组的幕永远读 4/5 号开关，见 ActLayout
            var actIdx = ActLayout.ConfigLayerOf(act, state);
            if (actIdx < 1 || state == null || act == null) return;

            // ★ 诊断 + 补救：建图前确认 runState.Act 的 HasSecondBoss。
            // StandardActMap.CreateFor 的 IL 是  hasSecondBoss: runState.Act.HasSecondBoss
            // —— 只要这个属性为 true，地图就会建出 SecondBossMapPoint。
            // 实测出现过"GenerateRooms 里设好了、建图时却是 false"（典型的实例身份问题），
            // 所以这里既打日志又做补救：直接对 runState.Act 设置，而不是对 __instance。
            MainFile.DebugLog(
                $"[Gauntlet] 建图前检查: act={act?.GetType().Name} idx={actIdx} " +
                $"Hash={act?.GetHashCode()} HasSecondBoss={act?.HasSecondBoss} " +
                $"second={(act?.SecondBossEncounter?.Id?.Entry ?? "null")}");

            if (act != null && !act.HasSecondBoss && DoubleBossConfigPatch.IsDoubleBossEnabled(actIdx))
            {
                var first = act.BossEncounter?.Id?.Entry;
                var pool = (act.AllBossEncounters ?? Enumerable.Empty<MegaCrit.Sts2.Core.Models.EncounterModel>())
                    .Where(b => b?.Id?.Entry is { } id && id != first)
                    .OrderBy(b => b.Id.Entry, StringComparer.Ordinal)
                    .ToList();

                if (pool.Count > 0)
                {
                    act.SetSecondBossEncounter(pool[0]);
                    MainFile.DebugLog(
                        $"[Gauntlet] 建图前补救: runState.Act 补设第二 boss='{pool[0].Id.Entry}'（该层池 {pool.Count} 个）");
                }
                else
                {
                    MainFile.Logger.Warn($"[Gauntlet] 建图前补救失败: 第 {actIdx} 层没有可用的第二 boss");
                }
            }

            // ★ 不变量兜底（2026-09-23）：同一层的第二个 boss **绝不能等于第一个**，
            //   否则第二场就是"再打一遍第一个 BOSS"（用户实测：act4 连续两场 KNOWLEDGE_DEMON_BOSS）。
            //   这里在建图前做一次最终校验：撞了就换池里第一个别的；池里没有别的才只能重复（并 Warn）。
            if (act != null && act.HasSecondBoss)
            {
                var firstId = act.BossEncounter?.Id?.Entry;
                var secondId = act.SecondBossEncounter?.Id?.Entry;

                if (firstId != null && string.Equals(firstId, secondId, StringComparison.Ordinal))
                {
                    var alt = (act.AllBossEncounters ?? Enumerable.Empty<MegaCrit.Sts2.Core.Models.EncounterModel>())
                        .Where(b => b?.Id?.Entry is { } id && id != firstId)
                        .OrderBy(b => b.Id.Entry, StringComparer.Ordinal)
                        .ToList();

                    if (alt.Count > 0)
                    {
                        act.SetSecondBossEncounter(alt[0]);
                        MainFile.Logger.Warn(
                            $"[DoubleBoss] 第 {actIdx} 层的第二 boss 与首个相同（'{firstId}'）" +
                            $"⇒ 建图前改设为 '{alt[0].Id.Entry}'（否则第二个 BOSS 会重复第一个）");
                    }
                    else
                    {
                        MainFile.Logger.Warn(
                            $"[DoubleBoss] 第 {actIdx} 层的第二 boss 与首个相同（'{firstId}'），" +
                            "且该层池里没有别的候选 ⇒ 只能重复");
                    }
                }
            }

            // ★ act5：第二 boss 槽位的清空已由 ActBlueprint 在黑屏内做好（见 Core/ActBlueprint.cs）。
            //   这里只保留诊断。
            if (act is Act5Model)
                MainFile.DebugLog(
                    $"[Gauntlet] 建图前 act5: HasSecondBoss={act.HasSecondBoss}" +
                    "（槽位清空已由 ActBlueprint 完成）");

            // ★ act4 的「顶端 boss 去重」不再在这里做 ——
            //   已收口到 ActBlueprint（黑屏窗口内、每层只算一次），见 Core/ActBlueprint.cs。
            //   这里只保留诊断，方便对照时序。
            if (act is Act4Model)
                MainFile.DebugLog(
                    $"[Gauntlet] 建图前 act4 顶端 boss='{act.BossEncounter?.Id?.Entry ?? "null"}'" +
                    "（定稿已由 ActBlueprint 完成）");

            // 记录本层要插几个合成房间（真正的注入在 SetMap 里按 map.SecondBossMapPoint 判定）
            SyntheticHearth.PrepareForAct(actIdx, SyntheticHearth.RoomTypes.Count);
        });
    }

    // ============================================================
    // 3) 地图界面建好后：注入合成节点（视觉 + 连通 + 连线）
    // ============================================================

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    public static void NMapScreenSetMapPostfix(NMapScreen __instance, ActMap map)
    {
        // 无条件第一行：区分"没被调用 / 提前 return / 抛异常"（我之前正是缺了这行才无法定位）
        MainFile.DebugLog(
            $"[Gauntlet] SetMap: map={(map == null ? "null" : map.GetType().Name)} " +
            $"secondBoss={(map?.SecondBossMapPoint == null ? "无" : "有")}");

        if (!PatchScope.IsEnabled) return;
        if (map == null) return;

        // ★★ 这里**不能**"有人也 patch 了 SetMap ⇒ 整套放权"（2026-09-22 实际事故）。
        //
        //   `SetMap` 上我们做的事是**纯加法**：原方法（含所有其它 mod 的 postfix / IL hook）跑完之后，
        //   往 `_points` 容器里多挂几个合成节点、写进 `_mapPointDictionary`、再补连线。
        //   别的 mod 挂 postfix 或改 IL 都不会让这件事失效 —— Harmony 本来就允许多个 patch 共存。
        //
        //   反例（真实发生过）：创意工坊模组 Ascension100（3801607408）为"进阶 11 地图火焰特效"
        //   挂了 `[HarmonyPatch(typeof(NMapScreen), "SetMap")]`（只 CallDeferred 一个纯视觉 Refresh）。
        //   旧代码用 `SomeoneElsePatchesMapScreen` 一票否决 ⇒ **合成火堆的注入整个被跳过**，
        //   而双 boss 本身还在（由 RunManager.GenerateMap 前缀那条无放权判定的路径补上），
        //   于是现象就是用户报的「双BOSS中间火堆没有了」。
        //
        //   注：Ascension100 的后缀里 `MapFlames.Refresh` 是**延迟一帧**跑的，会遍历整棵树对
        //   每个 NNormalMapPoint 挂火焰 —— 我们后注入的火堆节点也因此照样有火焰特效（实测更协调）。
        ModCompat.NoteCoexistence("合成节点注入", NMapScreenSetMapMethod);

        PatchScope.Run(nameof(NMapScreenSetMapPostfix), () =>
        {
            var st = RunStateAccessor.GetCurrentState();

            // act5：补上两个伪装 BOSS 节点（灵魂异鱼 / 知识恶魔）。
            // 它们用的是**我们自己的节点类**（图标跟着各自的 encounter 走），
            // 房间内容由 Act5EncounterPoolPatch 供给 —— 点节点后的流程全是原版的。
            if (st?.Act is Act5Model)
            {
                var okMid = Act5MidBoss.InjectVisuals(__instance, map);
                MainFile.DebugLog($"[Gauntlet] act5 伪装 BOSS 注入结果 = {okMid}");

                // 程序化特效（用户口径）：
                //   ① 地图纹路上有金光顺着流动（极限档是暗红血光）
                //   ② BOSS 地图图标改色：传奇=黑金为主+保留红；神话=血色
                var myth = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme;
                // 原版最终 BOSS 节点的贴图是在它自己的 _Ready 里挂的 ⇒ 延迟一帧再改色
                var bossPointNode = Traverse.Create(__instance).Field("_bossPointNode").GetValue<NMapPoint>();
                Callable.From(() => Act5VisualEffects.RecolorBossIcon(bossPointNode, myth)).CallDeferred();

                // ★ 2026-09-22「叫醒」：这两行以前**丢了** —— AttachMapShimmer / UpdatePatternForFloor
                //   在整棵源码树里零调用点（孤儿方法），所以"纹路金光流动 + 每次爬楼换卷云样式"
                //   从来没生效过（只有 BOSS 图标改色是活的）。挂材质是这两件事的唯一前提：
                //   不挂 rect.Material，后面所有 SetShaderParameter 都是往空气里改。
                Act5VisualEffects.AttachMapShimmer(__instance, myth);
                Act5VisualEffects.UpdatePatternForFloor(st.ActFloor);
                return;
            }

            var ok = SyntheticHearth.InjectVisuals(__instance, map);
            MainFile.DebugLog($"[Gauntlet] 注入结果 = {ok}");

            // ★ 2026-09-22「叫醒」：act4 地图节点图标"黄色 → 淡紫"（用户口径：问号/商店/火堆/精英/BOSS 的
            //   底图黄色都换成淡紫）。Act4MapIconRecolor.Schedule 同样是孤儿方法（零调用点），一并接回；
            //   它内部自己 CallDeferred（节点贴图是在各自 _Ready 里挂的，必须等一帧）。
            //
            //   注：act4 的**纹路**变色走另一条路（Act4MapStripeTint 状态 + ActVisualTheme.ForAct4 重绘底图，
            //   因为实测三个 rect 的 Material 默认是 null、改 uniform 无效），两条互不干扰。
            if (st?.Act is Act4Model) Act4MapIconRecolor.Schedule(__instance);
        });
    }

    /// <summary>
    ///     <c>NMapScreen.SetMap</c> 的 MethodInfo（<see cref="ModCompat.NoteCoexistence" /> 用来查谁也在 patch）。
    /// </summary>
    private static readonly MethodBase? NMapScreenSetMapMethod = ModCompat.MapScreenTarget;

    // ============================================================
    // 4) 可通行性重算之后：把合成节点补成可点
    // ============================================================

    [HarmonyPatch(typeof(NMapScreen), "RecalculateTravelability")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    public static void NMapScreenRecalculateTravelabilityPostfix(NMapScreen __instance)
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(NMapScreenRecalculateTravelabilityPostfix), () =>
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.Act is Act5Model)
            {
                Act5MidBoss.RefreshTravelability(__instance);
                return;
            }

            SyntheticHearth.RefreshSyntheticTravelability(__instance);

            // ★ 兜底：没进火堆也要能进第二个 BOSS（见 SyntheticHearth.EnsureSecondBossReachable）
            SyntheticHearth.EnsureSecondBossReachable(__instance);
        });
    }

    // ============================================================
    // 4b) 地图每次打开：act5 再兜一次可通行性
    //     （火堆/事件房结束后不一定重跑 RecalculateTravelability，
    //       而玩家正是这时候回来点下一个节点的）
    // ============================================================

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    public static void NMapScreenOpenPostfix(NMapScreen __instance)
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(NMapScreenOpenPostfix), () =>
        {
            Act5MidBoss.RefreshTravelability(__instance);

            // 每次回到地图 = 又爬了一层 ⇒ 换一套卷云样式；同时把层色恢复成紫
            var st = RunStateAccessor.GetCurrentState();
            if (st?.Act is Act4Model or Act5Model) Act4EliteBackgroundByLayerPatch.ApplyTintForCurrentRoom(st);

            // ★ act5 的卷云样式按楼层换（金光/血光流动是连续动画，不需要每次重挂）
            if (st?.Act is Act5Model) Act5VisualEffects.UpdatePatternForFloor(st.ActFloor);

            // ★ 兜底：进火堆/商店再出来时原版算不出第二个 BOSS 可通行 ⇒ 这里每次开地图都补一次
            SyntheticHearth.EnsureSecondBossReachable(__instance);
        });
    }

    // ============================================================
    // 4c) 点击那一刻的硬拦截：没进火堆也能进第二个 BOSS（2026-09-22 用户要求）
    //
    //     `NMapPoint.OnRelease` 的 IL 第一行就是 `if (!IsTravelable) return;`
    //     （原版：`IL_0001: call get_IsTravelable` → `IL_0006: brtrue` → 否则 `ret`），
    //     所以在它执行之前把"第二个 BOSS 节点"提成 Travelable，这一击就不会被吞。
    //
    //     ⚠️ 方法名用字符串字面量：`OnRelease` 是 protected override sealed，`nameof` 取不到
    //     （踩坑指南 §3.5 同源教训）。
    // ============================================================

    [HarmonyPatch(typeof(NMapPoint), "OnRelease")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static void NMapPointOnReleasePrefix(NMapPoint __instance)
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(NMapPointOnReleasePrefix),
            () => SyntheticHearth.TryAllowSecondBossClick(__instance));
    }

    // ============================================================
    // 5) 第一个 boss 打完点"继续"：合成房间（act1~4）在这里收尾
    //    （act5 的三个房间全走原生流程，不在这里特殊处理）
    // ============================================================

    [HarmonyPatch(typeof(RunManager), "ProceedFromTerminalRewardsScreen")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static bool RunManagerProceedFromTerminalRewardsScreenPrefix(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return true;

        PatchScope.Run(nameof(RunManagerProceedFromTerminalRewardsScreenPrefix), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state == null) return;
            SyntheticHearth.OnFirstBossRewardProceeded(state);
        });

        return true;
    }

    // ============================================================
    // 6) 点地图节点：**不接管** NMapScreen.TravelToMapCoord
    //
    // ★ 这里从"接管"改回"放行"，是为了修一个我亲手造成的回归：
    //   原版进房转场（画圈 + 渐黑）整个都在 TravelToMapCoord 里 ——
    //     IsTraveling=true → RecalculateTravelability → MapSplitVoteAnimation（画圈）
    //     → node.OnSelected() + NMapNodeSelectVfx + SfxCmd.Play("wipe_map")
    //     → RunManager.FadeOut()（渐黑）→ 沿 _paths 逐点点亮 → await EnterMapCoord(coord)
    //     → FadeIn + RefreshAllPointVisuals
    //   我之前在这里直接 return false + 自己去调 EnterMapPointInternal，等于把上面**全部**跳过，
    //   于是玩家看到的就是"点了火堆直接黑一下进房，没有画圈也没有渐黑"（用户实测反馈）。
    //
    //   转场需要的两样东西我们都齐：
    //     ① `_mapPointDictionary[coord]` 里有合成节点（InjectVisuals 已登记，且强制成了 Travelable）
    //     ② `_paths[(上一个已访问坐标, 合成坐标)]` 里有连线（InjectVisuals 里 DrawPaths 已画）
    //   所以放行即可恢复原版演出。
    //
    // ⚠️ 真正必须接管的点在下一节 RunManager.EnterMapCoord —— 原版实现是
    //      `MapPoint point = State.Map.GetPoint(coord); EnterMapPointInternal(coord.row + 1, point.PointType, ...)`，
    //    合成坐标在**网格外**，GetPoint 解析不到（null）会直接炸。
    // ============================================================

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static bool NMapScreenTravelToMapCoordPrefix(MapCoord coord)
    {
        if (!PatchScope.IsEnabled) return true;

        PatchScope.Run(nameof(NMapScreenTravelToMapCoordPrefix), () =>
        {
            var state = RunStateAccessor.GetCurrentState();
            if (!SyntheticHearth.IsSyntheticCoord(state, coord)) return;

            MainFile.DebugLog(
                $"[Gauntlet] 合成坐标 ({coord.col},{coord.row}) 走**原版旅行流程**" +
                "（保留画圈 + 渐黑转场；进房由 EnterMapCoord 接管）");
        });

        return true;   // 一律放行：转场演出全在原版实现里，接管它就等于删掉转场
    }

    // ============================================================
    // 6b) 兜底：直接进房的路子（Debug / 脚本 / 别的模组）仍然走 EnterMapCoord
    // ============================================================

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static bool RunManagerEnterMapCoordPrefix(RunManager __instance, MapCoord coord, ref Task __result)
    {
        if (!PatchScope.IsEnabled) return true;

        // 登记"正要进哪个坐标"：SecondBossEncounterPullPatch 靠它判断这一场是不是第二个 BOSS 节点
        // （原版 PullNextEncounter(Boss) 只会给 RoomSet 里那个固定 boss ⇒ 第二场会重复第一场）。
        SecondBossEntry.RecordEnteringCoord(coord);

        Task? takeover = null;
        var handled = PatchScope.Run(nameof(RunManagerEnterMapCoordPrefix), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state?.Map == null) return false;

            return SyntheticHearth.TryEnterSyntheticRoom(__instance, state, coord, out takeover);
        }, false);

        if (!handled || takeover == null) return true;

        __result = takeover;
        return false;
    }

    // ============================================================
    // 7) 多人：投票坐标若是合成坐标，重映射回合法坐标
    // ============================================================

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer),
        "PlayerVotedForMapCoord")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static void MapSelectionSynchronizerPlayerVotedForMapCoordPrefix(
        MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer __instance,
        ref MapLocation source)
    {
        if (!PatchScope.IsEnabled) return;

        // ref 参数不能进 lambda，所以用局部变量承载后再写回
        var loc = source;
        PatchScope.Run(nameof(MapSelectionSynchronizerPlayerVotedForMapCoordPrefix),
            () => SyntheticHearth.RemapVoteSource(ref loc));
        source = loc;
    }

    // ============================================================
    // 8) 读档：若存档停在合成节点上，恢复成合法状态
    // ============================================================

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.LoadIntoLatestMapCoord))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static bool RunManagerLoadIntoLatestMapCoordPrefix(RunManager __instance, ref Task __result)
    {
        if (!PatchScope.IsEnabled) return true;

        Task? takeover = null;
        var handled = PatchScope.Run(nameof(RunManagerLoadIntoLatestMapCoordPrefix), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state == null) return false;
            return SyntheticHearth.TryRestorePendingRoom(__instance, state, out takeover);
        }, false);

        if (!handled || takeover == null) return true;

        __result = takeover;
        return false;
    }

    // ============================================================
    // 9/10) 离开合成房间时的收尾
    // ============================================================

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom), "OnProceedButtonReleased")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static void NRestSiteRoomOnProceedButtonReleasedPrefix()
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(NRestSiteRoomOnProceedButtonReleasedPrefix),
            () => SyntheticHearth.CompleteSyntheticRoom(MapPointType.RestSite));
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom), "HideScreen")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static void NMerchantRoomHideScreenPrefix()
    {
        if (!PatchScope.IsEnabled) return;
        PatchScope.Run(nameof(NMerchantRoomHideScreenPrefix),
            () => SyntheticHearth.CompleteSyntheticRoom(MapPointType.Shop));
    }
}
