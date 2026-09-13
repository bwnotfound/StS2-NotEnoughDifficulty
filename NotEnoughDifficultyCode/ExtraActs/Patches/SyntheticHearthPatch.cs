using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

/// ## 参考实现声明
/// 本模组"双 BOSS 之间的合成火堆 / 商店"这一功能，早期因技术尚不成熟，**参考了创意工坊模组
/// Boss Gauntlet（v0.1.3）的实现思路与做法**（双 BOSS 的实现与火堆添加均受益于此）。
///
/// 如果原作者认为不妥，请联系 @Coll-ed（https://github.com/Coll-ed）：
/// 我会立即移除相关实现，并改用自行重新设计的节点注入方案来补齐功能。

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     双 boss 之间的「合成火堆」——灵感来自工坊模组 <c>Boss Gauntlet</c>（我们按它的公开行为自行实现，分析见拆解笔记）。
///
/// ## 为什么不能改原版节点
/// 我最初的做法是"找到第二个 boss 的父节点，把 PointType 改成 RestSite"。
/// 实测必然失败：<c>map.GetAllMapPoints()</c> 枚举的是 <c>Grid</c>，而
/// <c>SecondBossMapPoint</c> 不在 <c>Grid</c> 里，所以**找不到它的父节点**
/// （日志原话：<c>act3 找不到第二个 boss 的父节点，火堆未插入</c>）。
/// 更深层的原因：原版地图压根没为"两个 boss 中间多一站"设计过连通关系。
///
/// ## 正确做法（拆自 BossGauntlet v0.1.3）
/// **保留原版地图一个字节不动**，在「地图外的虚拟坐标」新建合成节点：
/// <code>
///   col = map.BossMapPoint.coord.col      + 50 + index
///   row = map.SecondBossMapPoint.coord.row + 50 + index
/// </code>
/// 然后用 <c>NNormalMapPoint.Create(...)</c> 造视觉节点挂到 <c>NMapScreen._points</c>，
/// 写进 <c>_mapPointDictionary</c>，删掉两 boss 之间的直连，重新串链并逐条画。
///
/// ## 关键私有字段（Traverse 取，名字来自 BossGauntlet 的 IL）
/// <c>_bossPointNode</c> / <c>_secondBossPointNode</c> / <c>_points</c> /
/// <c>_mapPointDictionary</c> / <c>_paths</c>
/// </summary>
internal static class SyntheticHearth
{
    /// <summary>虚拟坐标偏移（BossGauntlet 用 50，越出正常地图范围）。</summary>
    private const int CoordOffset = 50;

    /// <summary>
    ///     本层要插的合成房间类型序列（**从配置读，不写死**）。
    ///     顺序：火堆 → 商店（先休息再购物），与 Boss Gauntlet 的口径一致。
    ///     两个开关都关时不插任何东西（玩家可以选择"双 boss 但中间不休息"）。
    /// </summary>
    public static IReadOnlyList<MapPointType> RoomTypes
    {
        get
        {
            var list = new List<MapPointType>();
            if (NotEnoughDifficultyConfig.InterBossHearth) list.Add(MapPointType.RestSite);
            if (NotEnoughDifficultyConfig.InterBossShop) list.Add(MapPointType.Shop);
            return list;
        }
    }

    /// <summary>合成坐标：第 index 个。</summary>
    public static MapCoord GetCoord(ActMap map, int index)
    {
        var boss = map.BossMapPoint?.coord ?? default;
        var second = map.SecondBossMapPoint?.coord ?? default;
        return new MapCoord(boss.col + CoordOffset + index, second.row + CoordOffset + index);
    }

    /// <summary>由坐标反查合成房间序号；不是合成坐标则返回 -1。</summary>
    public static int GetRoomIndex(ActMap map, int roomCount, MapCoord coord)
    {
        for (var i = 0; i < roomCount; i++)
            if (GetCoord(map, i) == coord) return i;
        return -1;
    }

    /// <summary>
    ///     往地图上注入合成火堆的**视觉与连通**（在 <c>NMapScreen.SetMap</c> 之后调）。
    ///     返回 false 表示条件不满足或失败（调用方无需处理，原版地图照常可用）。
    /// </summary>
    public static bool InjectVisuals(NMapScreen screen, ActMap map)
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state == null) return false;

            var actIdx = RunProgress.GetActIndex(state);
            if (actIdx < 1) return false;

            // 只对"启用了双 boss 且真的有第二个 boss"的层动手。
            //
            // ★ act3 在 N10 生效时**完全跳过**（用户方案）：
            //   第 3 层的双 boss 归 N10 安排，本 mod 的按层开关（Act3_DoubleBoss）不参与，
            //   合成火堆也不注入 —— 否则就变成"两套机制同时给 act3 放第二 boss + 多插一个火堆"。
            //   用户反馈原话："ACT 3 为什么会出现火堆!? …… 我的理念是 ACT3 的双重BOSS
            //   在检测到处于 N10 时，跳过自己的放置，因此让拦截正常运行！"
            if (actIdx == 3 && N10DoubleBossAtAct3Patch.IsN10Active())
            {
                MainFile.DebugLog("[SynthHearth] act3：N10 已接管双 boss，本 mod 不注入火堆");
                return false;
            }

            // ★ act5（本模组的传奇/神话幕）显式排除：那一幕的连战由 Act5BossSequence 自己发房推进，
            //   既没有 SecondBossMapPoint，也不该在两个 BOSS 之间夹火堆。
            //   判据用**类型**而不是层号 —— 第 4 幕被别的模组占用而顺延时，它排在第 6 幕，层号会变。
            if (state?.Act is Act5Model) return false;
            if (map.SecondBossMapPoint == null) return false;
            if (!DoubleBossConfigPatch.IsDoubleBossEnabled(ActLayout.ConfigLayerOf(state?.Act, state))) return false;

            var bossNode = Traverse.Create(screen).Field("_bossPointNode").GetValue<NBossMapPoint>();
            var secondNode = Traverse.Create(screen).Field("_secondBossPointNode").GetValue<NBossMapPoint>();
            var container = Traverse.Create(screen).Field("_points").GetValue<Control>();
            var pointDict = Traverse.Create(screen)
                .Field("_mapPointDictionary").GetValue<Dictionary<MapCoord, NMapPoint>>();
            var paths = Traverse.Create(screen)
                .Field("_paths")
                .GetValue<Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>>();

            if (bossNode == null || secondNode == null || container == null
                || pointDict == null || paths == null)
            {
                MainFile.Logger.Error(
                    "[SynthHearth] 取不到 NMapScreen 的地图节点字段（版本可能变了），跳过火堆注入");
                return false;
            }

            // 1) 删掉两个 boss 之间的直连，否则视觉上会有一条穿过去的线
            paths.Remove((map.BossMapPoint!.coord, map.SecondBossMapPoint.coord));

            // 2) 两端位置
            var start = bossNode.Position + bossNode.Size * 0.5f;
            var end = secondNode.Position + secondNode.Size * 0.5f;

            var types = RoomTypes;
            var points = new List<MapPoint>();
            var nodes = new List<NNormalMapPoint>();

            for (var i = 0; i < types.Count; i++)
            {
                var coord = GetCoord(map, i);
                var pt = new MapPoint(coord.col, coord.row) { PointType = types[i], CanBeModified = false };
                CoordField?.SetValue(pt, coord);

                var node = NNormalMapPoint.Create(pt, screen, state!);
                if (node == null) continue;

                container.AddChild(node);
                node.Position = start.Lerp(end, (i + 1f) / (types.Count + 1f)) - node.PivotOffset;
                node.Scale = Vector2.One * 0.85f;

                pointDict[coord] = node;   // 索引器赋值（Add 遇重复键会抛）
                points.Add(pt);
                nodes.Add(node);
            }

            if (points.Count == 0)
            {
                MainFile.Logger.Warn("[SynthHearth] 合成节点创建失败，未注入火堆");
                return false;
            }

            // 3) 重新串链：Boss -> 合成点 -> ... -> 第二个 Boss
            foreach (var pt in points) map.BossMapPoint.AddChildPoint(pt);
            for (var i = 0; i + 1 < points.Count; i++) points[i].AddChildPoint(points[i + 1]);
            points[^1].AddChildPoint(map.SecondBossMapPoint);

            // 4) 画连线
            try
            {
                var drawPaths = AccessTools.Method(typeof(NMapScreen), "DrawPaths");
                if (drawPaths != null)
                {
                    drawPaths.Invoke(screen, new object[] { bossNode, map.BossMapPoint });
                    for (var i = 0; i + 1 < nodes.Count; i++)
                        drawPaths.Invoke(screen, new object[] { nodes[i], points[i] });
                    drawPaths.Invoke(screen, new object[] { nodes[^1], points[^1] });
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"[SynthHearth] 画连线失败（火堆已注入，可能只是没线）: {ex}");
            }

            MainFile.DebugLog(
                $"[SynthHearth] 已在第 {actIdx} 层注入 {points.Count} 个合成火堆" +
                $"（坐标 {string.Join(", ", points.Select(p => $"({p.coord.col},{p.coord.row})"))}）");

            // 5) 重算可通行性（**关键**）
            // 原版的重算发生在 SetMap 内部、**早于**本 postfix，所以刚接上的连通关系
            // 没被算进去 → 节点画出来了但点不动。这里显式再算一次。
            //
            // ⚠️ RecalculateTravelability / RefreshState / IsTravelable 在元数据里
            // **看不到访问修饰符**，我一开始当公开成员直接调，编译报 CS1061/CS0122
            // —— 说明它们是 protected/private，必须反射调。
            InvokeInstanceNoArg(screen, "RecalculateTravelability", "NMapScreen");

            // 6) 逐个刷新节点状态（双保险）
            foreach (var n in nodes)
                InvokeInstanceNoArg(n, "RefreshState", "NMapPoint");

            // 7) 诊断：把每个合成节点最终的可通行状态打出来（属性也是 protected，走反射）
            var states = new List<string>();
            for (var i = 0; i < nodes.Count; i++)
                states.Add($"#{i} travelable={GetProp(nodes[i], "IsTravelable")} state={GetProp(nodes[i], "State")}");
            MainFile.DebugLog($"[SynthHearth] 节点状态: {string.Join(", ", states)}");

            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[SynthHearth] InjectVisuals 失败: {ex}");
            return false;
        }
    }

    /// <summary>调用一个无参实例方法（成员可能是 protected/private，只能反射）。</summary>
    private static void InvokeInstanceNoArg(object target, string methodName, string typeName)
    {
        try
        {
            var mi = AccessTools.Method(target.GetType(), methodName)
                     ?? AccessTools.Method(target.GetType().BaseType, methodName);
            if (mi == null)
            {
                MainFile.Logger.Warn($"[SynthHearth] {typeName}.{methodName} 不存在（版本可能变了）");
                return;
            }

            mi.Invoke(target, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[SynthHearth] {typeName}.{methodName} 调用失败: {ex.Message}");
        }
    }

    // ============================================================
    // 运行期状态（记录本层插了几个房间 / 当前进行到第几间）
    // ============================================================

    private static int _pendingActIndex = -1;
    private static int _pendingRoomCount;
    private static int _pendingStep = -1;

    /// <summary>新 run 时清状态。</summary>
    public static void ResetForNewRun()
    {
        _pendingActIndex = -1;
        _pendingRoomCount = 0;
        _pendingStep = -1;
    }

    /// <summary>建图前记录本层会插几个合成房间。</summary>
    public static void PrepareForAct(int actIdx, int roomCount)
    {
        _pendingActIndex = actIdx;
        _pendingRoomCount = roomCount;
        _pendingStep = -1;
        MainFile.DebugLog($"[Gauntlet] 第 {actIdx} 层准备合成房间: {roomCount} 个");
    }

    /// <summary>可通行性重算后，把合成节点补成可点（原版不知道它们存在）。</summary>
    public static void RefreshSyntheticTravelability(NMapScreen screen)
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            var map = state?.Map;
            if (map?.SecondBossMapPoint == null || _pendingRoomCount <= 0) return;

            var dict = Traverse.Create(screen)
                .Field("_mapPointDictionary").GetValue<Dictionary<MapCoord, NMapPoint>>();
            if (dict == null) return;

            var refreshed = 0;
            for (var i = 0; i < _pendingRoomCount; i++)
            {
                if (!dict.TryGetValue(GetCoord(map, i), out var node) || node == null) continue;
                InvokeInstanceNoArg(node, "RefreshState", "NMapPoint");
                refreshed++;
            }

            if (refreshed > 0)
                MainFile.DebugLog($"[Gauntlet] 已刷新 {refreshed} 个合成节点的状态");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Gauntlet] RefreshSyntheticTravelability 失败: {ex.Message}");
        }
    }

    /// <summary>第一个 boss 奖励界面点"继续"时调用（BossGauntlet 在这里推进到合成房间）。</summary>
    public static void OnFirstBossRewardProceeded(RunState state)
    {
        try
        {
            if (state.Map?.SecondBossMapPoint == null) return;
            if (_pendingRoomCount <= 0) return;

            _pendingStep = 0;
            MainFile.DebugLog("[Gauntlet] 第一个 boss 奖励已继续 → 进入合成房间序列");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Gauntlet] OnFirstBossRewardProceeded 失败: {ex.Message}");
        }
    }

    /// <summary>
    ///     玩家点到合成坐标 → 造对应房间。
    ///     用游戏公开的 <c>EnterMapPointInternal(actFloor, MapPointType, ...)</c>，
    ///     走原版房间流程（火堆就是真火堆），比复制 async 接管更稳。
    /// </summary>
    public static bool TryEnterSyntheticRoom(RunManager rm, RunState state, MapCoord coord, out Task? result)
    {
        result = null;
        var map = state.Map;
        if (map?.SecondBossMapPoint == null) return false;

        var types = RoomTypes;
        var index = GetRoomIndex(map, types.Count, coord);
        if (index < 0) return false;

        var actFloor = coord.row + 1;
        MainFile.DebugLog(
            $"[Gauntlet] 进入合成房间 index={index} coord=({coord.col},{coord.row}) type={types[index]}");

        result = rm.EnterMapPointInternal(actFloor, types[index], null, true);
        return result != null;
    }

    /// <summary>读档若停在合成节点上 → 恢复（返回 false 表示无需接管）。</summary>
    public static bool TryRestorePendingRoom(RunManager rm, RunState state, out Task? result)
    {
        result = null;
        var coordOpt = state.CurrentMapCoord;
        var map = state.Map;
        if (coordOpt == null || map?.SecondBossMapPoint == null) return false;

        var coord = coordOpt.Value;          // MapCoord 是 struct，先取出再传参
        var types = RoomTypes;
        var index = GetRoomIndex(map, types.Count, coord);
        if (index < 0) return false;

        MainFile.DebugLog($"[Gauntlet] 读档落在合成房间 index={index}，按原版流程恢复");
        result = rm.EnterMapPointInternal(coord.row + 1, types[index], null, false);
        return result != null;
    }

    /// <summary>多人投票：合成坐标不参与原版投票逻辑，直接清掉。</summary>
    public static void RemapVoteSource(ref MapLocation source)
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            var map = state?.Map;
            if (map?.SecondBossMapPoint == null) return;

            var types = RoomTypes;
            var coord = source.coord;          // MapLocation.coord 是 MapCoord?，先解包
            var c = coord ?? default;
            if (GetRoomIndex(map, types.Count, c) < 0) return;

            _pendingStep = -1;
            MainFile.DebugLog($"[Gauntlet] 合成房间坐标 ({c.col},{c.row}) 不参与投票，已忽略");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Gauntlet] RemapVoteSource 失败: {ex.Message}");
        }
    }

    /// <summary>离开合成房间时收尾。</summary>
    public static void CompleteSyntheticRoom(MapPointType type)
    {
        try
        {
            if (_pendingRoomCount <= 0) return;
            var state = RunStateAccessor.GetCurrentState();
            if (state?.Map?.SecondBossMapPoint == null) return;

            // 还有下一间 → 继续；否则整段结束
            var next = _pendingStep + 1;
            if (next < _pendingRoomCount)
            {
                _pendingStep = next;
                MainFile.DebugLog($"[Gauntlet] 合成房间 {type} 完成 → 下一间 index={next}");
            }
            else
            {
                _pendingStep = -1;
                MainFile.DebugLog($"[Gauntlet] 合成房间 {type} 完成 → 序列结束，可前往第二个 boss");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Gauntlet] CompleteSyntheticRoom 失败: {ex.Message}");
        }
    }
    /// <summary>读一个属性（可能是 protected/private，只能反射）。</summary>
    private static object? GetProp(object target, string propName)
    {
        try
        {
            var pi = AccessTools.Property(target.GetType(), propName)
                     ?? AccessTools.Property(target.GetType().BaseType, propName);
            return pi?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }
    /// <summary>MapPoint.coord 是私有字段且无 setter，只能反射写。</summary>
    private static readonly FieldInfo? CoordField = AccessTools.Field(typeof(MapPoint), "coord");
}

// ============================================================
// 注意：本文件只保留 SyntheticHearth 工具类。
// 所有 Harmony patch 入口都搬到了 BossGauntletStylePatches.cs
// （1:1 对齐工坊模组 Boss Gauntlet 的 10 个 patch 面）。
// ============================================================
