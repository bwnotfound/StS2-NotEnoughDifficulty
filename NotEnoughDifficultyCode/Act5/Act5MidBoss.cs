using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT5 的伪装 BOSS 节点注入 + 整条链的定位/连线/可通行性。
///
/// ## 路线（见 <see cref="Act5BossDisplay" /> 的编排）
/// <code>
///   考验： 先古之名 → 伪BOSS → 伪BOSS → 最终BOSS
///   极限： 先古之名 → 伪BOSS → 火堆 → 伪BOSS → 火堆 → … → 最终BOSS
/// </code>
///
/// ## 各角色
/// <list type="bullet">
///   <item>伪装 BOSS 的点：<c>PointType.Monster</c>，节点换成我们自己的 <see cref="Act5BossNode" />
///         （每场图标跟着自己的 encounter 走 = 各显各得）；
///         房间内容由 <c>ActModel.PullNextEncounter</c> 的钩子给（<see cref="Act5EncounterPoolPatch" />）。</item>
///   <item>火堆的点：<c>PointType.RestSite</c>，**节点是原版自己造的**（图标/进入/恢复全是原版），
///         我们只负责把它排到正确的位置。</item>
///   <item>最终 BOSS：地图 <c>BossMapPoint</c>，原版节点，打赢才收尾。</item>
/// </list>
///
/// ## 位置：锁在"起点 → BOSS"之间等分，挤不下就**等比缩小**（用户口径）
/// 原版把起点放在 y≈+720、BOSS 节点硬编码在 y≈−1980 —— 那个 y 就是**地图的下边界**。
/// ⚠️ 试过"把 BOSS 节点往上挪来拉长地图"，实测**直接突破地图背景**（用户："空隙太长了,突破地图了"），
/// 所以跨度不拉：链位在两点之间等分，节点高 × scale ≤ 行距×0.95，靠**等比缩小**避免重叠
/// （下限 <see cref="MinScale" />，到下限仍挤就接受略微重叠并在日志里说清楚）。
///
/// ## 其余硬约束（都踩过）
/// <list type="bullet">
///   <item>点必须在网格里（否则 <c>EnterMapCoordInternal</c> 回查为 null ⇒ 黑屏）</item>
///   <item>伪装BOSS 的点，原版已建过普通节点 ⇒ 撤掉换成我们的</item>
///   <item>串链/连线必须等注入之后（<c>SetMap</c> 建节点时会遍历起点 children 画线）</item>
///   <item>读档续玩地图是 <c>SavedActMap</c> ⇒ 判据用数据（扫网格）而不是类型</item>
/// </list>
/// </summary>
internal static class Act5MidBoss
{
    /// <summary>链位所在列（与起点/BOSS 点同一条竖线）。</summary>
    private const int Column = 3;

    /// <summary>补点时优先选的行（行号越小越靠下；行 0 是起点、最后一行是 BOSS）。</summary>
    private static readonly int[] RepairRows = { 1, 3, 5, 7, 9, 11, 2, 4, 6, 8, 10 };

    /// <summary>等比缩小的下限（再小就看不清了；到下限时允许略微重叠）。</summary>
    private const float MinScale = 0.4f;

    // ============================================================
    // 点：全部从地图数据里扫（读档续玩时地图是 SavedActMap，不能按类型取）
    // ============================================================

    /// <summary>扫全网格，按行号升序收集链位（伪装BOSS=Monster + 火堆=RestSite）。</summary>
    internal static List<MapPoint> ChainOf(ActMap? map)
    {
        var found = new List<MapPoint>();
        if (map == null) return found;

        for (var row = 0; row < map.GetRowCount(); row++)
        for (var col = 0; col < map.GetColumnCount(); col++)
        {
            if (map.GetPoint(col, row) is { } point
                && point.PointType is MapPointType.Monster or MapPointType.RestSite)
                found.Add(point);
        }

        found.Sort((a, b) => a.coord.row.CompareTo(b.coord.row));
        return found;
    }

    /// <summary>链位里的伪装 BOSS 点（Monster），按行号升序 = 玩家遇到的顺序。</summary>
    internal static List<MapPoint> Points(ActMap? map) =>
        ChainOf(map).Where(p => p.PointType == MapPointType.Monster).ToList();

    /// <summary>保证地图上有足够多的伪装 BOSS 点（旧存档/读档缺就现场补）。</summary>
    private static List<MapPoint> EnsurePoints(ActMap map)
    {
        var found = Points(map);
        if (found.Count >= Act5BossDisplay.DisguisedCount) return found;

        var grid = GridOf(map);
        if (grid == null)
        {
            MainFile.Logger.Error("[Act5Mid] 拿不到地图的 Grid 数组，无法补齐伪装BOSS点");
            return found;
        }

        var cols = grid.GetLength(0);
        var rows = grid.GetLength(1);

        foreach (var row in RepairRows)
        {
            if (found.Count >= Act5BossDisplay.DisguisedCount) break;
            if (Column >= cols || row < 0 || row >= rows || grid[Column, row] != null) continue;

            var point = new MapPoint(Column, row) { PointType = MapPointType.Monster, CanBeModified = false };
            grid[Column, row] = point;
            found.Add(point);

            MainFile.Logger.Warn(
                $"[Act5Mid] 这张地图（读档/旧存档）里伪装BOSS点不够，已在 ({Column},{row}) 补一个");
        }

        found.Sort((a, b) => a.coord.row.CompareTo(b.coord.row));
        return found;
    }

    /// <summary>反射取地图内部的 <c>MapPoint[,]</c>（SavedActMap 是 <c>&lt;Grid&gt;k__BackingField</c>）。</summary>
    private static MapPoint[,]? GridOf(ActMap map)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        for (var type = map.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != typeof(MapPoint[,])) continue;
                if (field.GetValue(map) is MapPoint[,] grid) return grid;
            }
        }

        return null;
    }

    /// <summary>
    ///     撤掉"旧存档带过来的第二 BOSS 点"：act5 只用 <c>BossMapPoint</c> 一个真 BOSS，
    ///     第二槽位的 encounter 已被清空 ⇒ 那个节点图标是空的，留着就是地图上一个坏节点。
    /// </summary>
    private static void DropStaleSecondBoss(ActMap map, Dictionary<MapCoord, NMapPoint> pointDict)
    {
        var second = map.SecondBossMapPoint;
        if (second == null) return;

        if (pointDict.TryGetValue(second.coord, out var node) && node != null)
        {
            node.QueueFree();
            pointDict.Remove(second.coord);
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var type = map.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            var field = type.GetFields(flags)
                .FirstOrDefault(f => f.FieldType == typeof(MapPoint) && f.Name.Contains("SecondBoss"));
            if (field == null) continue;

            field.SetValue(map, null);
            MainFile.Logger.Warn(
                $"[Act5Mid] 这张地图带着旧存档的第二 BOSS 点 ({second.coord.col},{second.coord.row})，已撤掉（act5 不用双 BOSS）");
            return;
        }
    }

    // ============================================================
    // 战斗内容：原版建房时来问"这一场是谁"
    // ============================================================

    /// <summary>
    ///     按玩家进度给下一场伪装 BOSS。
    ///
    /// ⚠️ 时序（<c>RunManager.EnterMapCoord</c> 的 IL）：
    /// <code>
    ///   if (!state.AddVisitedMapCoord(coord)) return;      // ← 先记"已走"
    ///   EnterMapPointInternal(...) → CreateRoom(...) → 本方法
    /// </code>
    /// 所以轮到我们时，**当前这个点的坐标已经算"已走"了** ——
    /// 已走的伪装BOSS点数 − 1 就是现在要打的那一场的序号。读档后 <c>VisitedMapCoords</c> 依然准确。
    /// </summary>
    internal static EncounterModel? NextDisguisedEncounter(RunState? state)
    {
        if (state == null) return null;

        var visited = state.VisitedMapCoords;
        var points = Points(state.Map);

        int index;
        if (points.Count > 0 && visited != null)
        {
            var visitedCount = points.Count(p => visited.Contains(p.coord));
            index = Math.Max(0, visitedCount - 1);
        }
        else
        {
            // 兜底（理论上到不了）：按"已经打赢过谁"数（战绩表来自地图历史，读档也在）
            var defeated = RunProgress.GetDefeatedEncounterIds(state);
            index = 0;
            for (var i = 0; i < Act5BossDisplay.DisguisedCount; i++)
            {
                var id = Act5BossDisplay.GetDisguised(i)?.Id?.Entry;
                if (!string.IsNullOrEmpty(id) && defeated.Contains(id)) index = i + 1;
            }

            MainFile.Logger.Warn($"[Act5Mid] 地图上找不到伪装BOSS点，按战绩表判断 → 第 {index + 1} 场");
        }

        var encounter = Act5BossDisplay.GetDisguised(index);
        MainFile.DebugLog(
            $"[Act5Mid] 下一场伪装BOSS（第 {index + 1}/{Act5BossDisplay.DisguisedCount} 场）= " +
            $"'{encounter?.Id.Entry ?? "<空>"}'");
        return encounter;
    }

    // ============================================================
    // 注入（SetMap 之后）
    // ============================================================

    /// <summary>在 <c>NMapScreen.SetMap</c> 之后：换掉伪装BOSS节点、排位置、串链、画线、重算可通行。</summary>
    internal static bool InjectVisuals(NMapScreen screen, ActMap map)
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.Act is not Act5Model) return false;

            // 读档续玩时编排是空的（BuildPlan 只在进 act5 那一刻跑）⇒ 这里补一次
            if (!Act5BossDisplay.HasPlan)
            {
                Act5BossDisplay.BuildPlan(state, ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme);
                MainFile.DebugLog("[Act5Mid] 读档续玩：BOSS 编排已重建");
            }

            var container = Traverse.Create(screen).Field("_points").GetValue<Godot.Control>();
            var pointDict = Traverse.Create(screen)
                .Field("_mapPointDictionary").GetValue<Dictionary<MapCoord, NMapPoint>>();
            var bossNode = Traverse.Create(screen).Field("_bossPointNode").GetValue<NMapPoint>();
            var startNode = Traverse.Create(screen).Field("_startingPointNode").GetValue<NMapPoint>();
            var pointsHost = Traverse.Create(screen).Field("_points").GetValue<Godot.Control>();

            if (container == null || pointDict == null)
            {
                MainFile.Logger.Error("[Act5Mid] 取不到 NMapScreen 的 _points/_mapPointDictionary，跳过注入");
                return false;
            }

            // act5 不用双 BOSS：旧存档可能带着一个第二 BOSS 点（encounter 已清空 ⇒ 图标是空的）
            DropStaleSecondBoss(map, pointDict);

            var disguisedPoints = EnsurePoints(map);
            if (disguisedPoints.Count == 0)
            {
                MainFile.Logger.Error("[Act5Mid] 地图上补不出伪装BOSS点，跳过注入");
                return false;
            }

            // ── ① 伪装 BOSS：撤掉原版建的普通节点，换成我们自己的 BOSS 节点 ──
            var bossNodes = new Dictionary<MapCoord, Act5BossNode>();
            for (var i = 0; i < disguisedPoints.Count && i < Act5BossDisplay.DisguisedCount; i++)
            {
                var point = disguisedPoints[i];
                var encounter = Act5BossDisplay.GetDisguised(i);
                if (encounter == null)
                {
                    MainFile.Logger.Warn($"[Act5Mid] 第 {i + 1} 场伪装BOSS 没排到，该节点跳过");
                    continue;
                }

                if (pointDict.TryGetValue(point.coord, out var stale) && stale != null)
                {
                    stale.QueueFree();
                    pointDict.Remove(point.coord);
                }

                var node = Act5BossNode.Create(point, screen, state, encounter);
                container.AddChild(node);
                pointDict[point.coord] = node;
                bossNodes[point.coord] = node;
            }

            if (bossNodes.Count == 0) return false;

            // ── ② 位置：三种形态（用户口径），横向空间换纵向空间 ──
            //
            // ⚠️ 两条实测约束：
            //  1) **不能拉长地图**：BOSS 节点就是地图的边界，把它往上挪会突破地图背景
            //     （用户："空隙太长了,突破地图了"）。
            //  2) **别让链位贴到 BOSS 图标上**：BOSS 图标大，紧挨它的链位（尤其火堆）会被盖住
            //     ⇒ 顶部留出 topMargin 的空白。
            //
            // 形态（场数 = 伪装BOSS 数）：
            //   1~3 场 → **直线**：全在同一条竖线上
            //   4~6 场 → **分叉**：BOSS 左/右交替，火堆留在正中（正好夹在两场之间）
            //   7 场+  → **蛇形**：一行 4 栏、逐行反向（用户给的形状）
            //            ``` 先古之名
            //                BOSS 火   BOSS 火        ← 行0 从左往右
            //                火   BOSS 火   BOSS       ← 行1 从右往左（接力） ```
            // 三种形态都由**节点高**决定缩放：分叉/蛇形时相邻链位横向错开一整个节点宽，
            // 纵向只需留出行距即可，所以能塞下更多场次而不用缩得很小。
            var chain = ChainOf(map);
            var nodeHeight = bossNodes.Values.Max(n => n.Size.Y);
            var nodeWidth = bossNodes.Values.Max(n => n.Size.X);

            var startX = startNode != null ? startNode.Position.X + startNode.Size.X * 0.5f : 0f;
            var cStart = (startNode?.Position.Y ?? 1320f) + (startNode?.Size.Y ?? nodeHeight) * 0.5f;
            var cBoss = (bossNode?.Position.Y ?? (cStart - 2600f)) + (bossNode?.Size.Y ?? nodeHeight) * 0.5f;
            var topMargin = nodeHeight * 1.25f;                       // 最上面的链位离 BOSS 图标留出的空隙
            var usable = Mathf.Max(nodeHeight, cStart - cBoss - topMargin);

            string shape;
            var spacing = usable / (chain.Count + 1);
            var scale = 1f;
            var centers = new Vector2[chain.Count];

            if (chain.Count >= 7)
            {
                // ── 蛇形：**栏位随场数增长**（上限不设），行数放不下就加栏，还不够就缩图标 ──
                //
                // 用户口径：<i>"蛇形是无限，缩放BOSS图标，每行继续增加栏位"</i>
                // 约束两条：
                //   ① 宽度：栏位总宽不能超过可视宽度（容器 `_points` 的 Size.X，实测 1920）
                //   ② 高度：行数 × 行距 ≤ 可用高（起点 → BOSS 之间，已扣顶部留白）
                // 解：从最大缩放开始往下试，取第一个两条都满足的方案（图标最大且不溢出）。
                var usableWidth = (pointsHost?.Size.X ?? 1920f) * 0.88f;

                var perRow = 4;
                for (var s = 1f; s >= MinScale; s -= 0.02f)
                {
                    var colSpacingTry = nodeWidth * s * 1.15f;
                    var perRowTry = Math.Max(4, (int)(usableWidth / Mathf.Max(1f, colSpacingTry)));
                    var rowsTry = (chain.Count + perRowTry - 1) / perRowTry;
                    var rowSpacingTry = usable / (rowsTry + 1);

                    scale = s;
                    perRow = perRowTry;
                    spacing = rowSpacingTry;

                    if (rowSpacingTry >= nodeHeight * s * 1.05f) break;
                }

                var rows = (chain.Count + perRow - 1) / perRow;
                var columnSpacing = nodeWidth * scale * 1.15f;
                shape = $"蛇形（{perRow} 栏/行 × {rows} 行）";

                for (var i = 0; i < chain.Count; i++)
                {
                    var row = i / perRow;
                    var colInRow = i % perRow;
                    var col = row % 2 == 0 ? colInRow : perRow - 1 - colInRow;   // 偶数行左→右，奇数行右→左
                    centers[i] = new Vector2(
                        startX + (col - (perRow - 1) * 0.5f) * columnSpacing,
                        cStart - spacing * (row + 1));
                }
            }
            else if (chain.Count >= 4)
            {
                // ── 分叉：BOSS 左右交替，火堆居中 ──
                var amplitude = nodeWidth * 0.5f;
                spacing = usable / (chain.Count + 1);
                scale = Mathf.Min(1f, spacing / (nodeHeight * 0.5f));
                shape = "分叉（BOSS 左右交替，火堆居中）";

                var bossIndex = 0;
                for (var i = 0; i < chain.Count; i++)
                {
                    var x = startX;
                    if (chain[i].PointType == MapPointType.Monster)
                    {
                        x += bossIndex % 2 == 0 ? -amplitude : amplitude;
                        bossIndex++;
                    }

                    centers[i] = new Vector2(x, cStart - spacing * (i + 1));
                }
            }
            else
            {
                // ── 直线 ──
                spacing = usable / (chain.Count + 1);
                scale = Mathf.Min(1f, spacing / (nodeHeight * 0.95f));
                shape = "直线";

                for (var i = 0; i < chain.Count; i++)
                    centers[i] = new Vector2(startX, cStart - spacing * (i + 1));
            }

            if (scale < MinScale)
            {
                scale = MinScale;   // 下限：再小就看不清了（这时会略微重叠，日志里说清楚）
                MainFile.Logger.Warn(
                    $"[Act5Mid] 链位 {chain.Count} 个、行距只有 {spacing:0}px，缩放已到下限 {MinScale:F2}（可能略微重叠）");
            }

            for (var i = 0; i < chain.Count; i++)
            {
                if (!pointDict.TryGetValue(chain[i].coord, out var node) || node == null) continue;

                // 缩放围绕中心（不同场景的 PivotOffset 不一样，显式设成中心最稳）
                node.PivotOffset = node.Size * 0.5f;
                node.Position = new Vector2(centers[i].X - node.Size.X * 0.5f, centers[i].Y - node.Size.Y * 0.5f);
                node.Scale = Vector2.One * scale;
            }

            MainFile.DebugLog(
                $"[Act5Mid] 布局形态={shape}（伪装BOSS {bossNodes.Count} 场 / 链位 {chain.Count}）| " +
                $"行距={spacing:0}px 缩放={scale:F2} 顶部留白={topMargin:0}px");

            // ── ③ 串链：先古之名 → 链位1 → 链位2 → … → 最终BOSS ──
            MapPoint? prev = map.StartingMapPoint;
            foreach (var point in chain)
            {
                Link(prev, point);
                prev = point;
            }

            Link(prev, map.BossMapPoint);

            // ── ④ 补画连线（原版建节点时这些边都还不存在）──
            DrawPaths(screen, startNode, map.StartingMapPoint);
            foreach (var point in chain)
                if (pointDict.TryGetValue(point.coord, out var node) && node != null)
                    DrawPaths(screen, node, point);

            Recalculate?.Invoke(screen, null);

            MainFile.DebugLog(
                $"[Act5Mid] 注入完成: 链位 {chain.Count}（伪装BOSS {bossNodes.Count} + 火堆 {chain.Count - bossNodes.Count}）" +
                $" | 行距={spacing:0}px 缩放={scale:F2} | " +
                string.Join(" | ", bossNodes.OrderBy(kv => kv.Key.row)
                    .Select(kv => $"({kv.Key.col},{kv.Key.row}) {kv.Value.IconDebugText()}")));

            MainFile.DebugLog(
                $"[Act5Mid] 定位参照: 起点 pos={startNode?.Position.ToString() ?? "?"} size={startNode?.Size.ToString() ?? "?"} " +
                $"| 容器 pos={pointsHost?.Position.ToString() ?? "?"} size={pointsHost?.Size.ToString() ?? "?"} " +
                $"| BOSS节点 pos={bossNode?.Position.ToString() ?? "?"} size={bossNode?.Size.ToString() ?? "?"} " +
                $"| 起点中心 y={cStart:0} BOSS中心 y={cBoss:0} 可用高={usable:0}");

            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act5Mid] InjectVisuals 失败: {ex}");
            return false;
        }
    }

    private static void Link(MapPoint? from, MapPoint? to)
    {
        if (from == null || to == null || ReferenceEquals(from, to)) return;
        if (from.Children != null && from.Children.Contains(to)) return;
        from.AddChildPoint(to);
    }

    private static void DrawPaths(NMapScreen screen, NMapPoint? node, MapPoint? point)
    {
        if (node == null || point == null) return;

        try
        {
            DrawPathsMethod?.Invoke(screen, new object[] { node, point });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Act5Mid] 补画连线失败（节点已注入）: {ex.Message}");
        }
    }

    // ============================================================
    // 可通行性：按"已走进度"直接定档 + 现场诊断
    //
    // 原版 <c>NMapScreen.RecalculateTravelability</c> 是这么推的（IL 实证）：
    //   已走过的坐标 → Traveled；最后一个坐标那个点的
    //   <c>MapTravel.GetTravelablePointsFrom(state, point)</c>（= point.Children）→ Travelable。
    // 我们的链位不在原版这条推导里，所以自己也推一遍并**兜底补上**，
    // 顺便把 State / 是否可点 打进日志（一眼看出卡在哪一环）。
    // ============================================================

    internal static void RefreshTravelability(NMapScreen screen)
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.Act is not Act5Model) return;

            var pointDict = Traverse.Create(screen)
                .Field("_mapPointDictionary").GetValue<Dictionary<MapCoord, NMapPoint>>();
            if (pointDict == null) return;

            var visited = state.VisitedMapCoords;
            if (visited == null) return;

            var chain = ChainOf(state.Map);
            if (chain.Count == 0) return;

            var startCoord = state.Map?.StartingMapPoint?.coord ?? default;
            var startDone = visited.Contains(startCoord);

            for (var i = 0; i < chain.Count; i++)
            {
                var coord = chain[i].coord;
                if (!pointDict.TryGetValue(coord, out var node) || node == null) continue;

                var done = visited.Contains(coord);
                var prevDone = i == 0 ? startDone : visited.Contains(chain[i - 1].coord);
                var shouldTravel = prevDone && !done;

                var before = node.State;
                if (shouldTravel && before != MapPointState.Travelable)
                {
                    node.State = MapPointState.Travelable;   // ← 兜底：原版没给就我们给
                    MainFile.Logger.Warn(
                        $"[Act5Mid] ({coord.col},{coord.row}) 原版没把它标成可通行（State={before}），已补上");
                }

                MainFile.DebugLog(
                    $"[Act5Mid] 巡检 ({coord.col},{coord.row}) {chain[i].PointType}: 前一个已走={prevDone} " +
                    $"本节点已走={done} 应可通行={shouldTravel} | State={before}→{node.State} 可点={node.IsEnabled}");
            }

            var boss = state.Map?.BossMapPoint;
            if (boss != null && pointDict.TryGetValue(boss.coord, out var bossNode2) && bossNode2 != null)
            {
                MainFile.DebugLog(
                    $"[Act5Mid] 巡检 BOSS({boss.coord.col},{boss.coord.row}): State={bossNode2.State} " +
                    $"可点={bossNode2.IsEnabled} | 最后一个链位已走={visited.Contains(chain[^1].coord)}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Act5Mid] RefreshTravelability 失败: {ex.Message}");
        }
    }

    private static readonly MethodInfo? DrawPathsMethod = AccessTools.Method(typeof(NMapScreen), "DrawPaths");

    private static readonly MethodInfo? Recalculate =
        AccessTools.Method(typeof(NMapScreen), "RecalculateTravelability");
}
