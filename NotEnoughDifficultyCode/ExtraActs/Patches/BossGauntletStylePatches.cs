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
        if (ModCompat.SomeoneElsePatchesMapScreen("合成节点注入")) return;

        PatchScope.Run(nameof(NMapScreenSetMapPostfix), () =>
        {
            // act5：补上两个伪装 BOSS 节点（灵魂异鱼 / 知识恶魔）。
            // 它们用的是**我们自己的节点类**（图标跟着各自的 encounter 走），
            // 房间内容由 Act5EncounterPoolPatch 供给 —— 点节点后的流程全是原版的。
            var state5 = RunStateAccessor.GetCurrentState();
            if (state5?.Act is Act5Model)
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
                return;
            }

            var ok = SyntheticHearth.InjectVisuals(__instance, map);
            MainFile.DebugLog($"[Gauntlet] 注入结果 = {ok}");

            // ★ act4 也必须挂着色器材质！否则后面改 tint / pattern_seed 的 uniform
            //   全是往空气里改 —— 实测（用户三连反馈）：act4 不换卷云样式、纹路不按层变色，
            //   根因就是这里从来没挂过材质（attach 只在 act5 分支里调了）。

        });
    }

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
        });
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
    // 6) 进入地图节点：接管 **NMapScreen.TravelToMapCoord**
    //
    // ⚠️ 这里必须挂 TravelToMapCoord，不能挂 RunManager.EnterMapCoord（踩过大坑）：
    // 崩溃堆栈证明玩家点地图的真实路径是
    //     NMapScreen.TravelToMapCoord(coord)
    //       → RunManager.EnterMapCoord(coord)
    //       → RunManager.EnterMapPointInternal(actFloor, pointType, null, saveGame)
    //       → CreateRoom(RoomType, MapPointType, model: null)
    // 挂在 EnterMapCoord 上的钩子对"点地图"不生效 ——
    // 所以注入的合成节点点不动、合成房间也进不去。
    //
    // act5 的伪装 BOSS 节点**不走这里**：它们是原生 PointType.Monster 节点，
    // 房间里放谁由 Act5EncounterPoolPatch（ActModel.PullNextEncounter）决定。
    // ============================================================

    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    public static bool NMapScreenTravelToMapCoordPrefix(NMapScreen __instance, MapCoord coord, ref Task __result)
    {
        if (!PatchScope.IsEnabled) return true;

        Task? takeover = null;
        var handled = PatchScope.Run(nameof(NMapScreenTravelToMapCoordPrefix), () =>
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.Map == null) return false;

            var rm = RunManager.Instance;
            if (rm == null) return false;

            // 合成火堆/商店（act1~4）
            return SyntheticHearth.TryEnterSyntheticRoom(rm, state, coord, out takeover);
        }, false);

        if (!handled || takeover == null) return true;

        __result = takeover;
        return false;
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
