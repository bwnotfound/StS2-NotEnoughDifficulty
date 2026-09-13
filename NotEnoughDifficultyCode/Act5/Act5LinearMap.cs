using MegaCrit.Sts2.Core.Map;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     第 5 层的地图（**用户方案**）。
///
/// ## 结构（按编排动态铺）
/// <code>
///   考验档： Ancient(3,0) → 伪BOSS → 伪BOSS → BossMapPoint
///   极限档： Ancient(3,0) → 伪BOSS → **火堆** → 伪BOSS → **火堆** → … → BossMapPoint   （火堆数 = 场数 − 1）
/// </code>
/// 伪装 BOSS 的点 = <c>PointType.Monster</c>（内容由 <c>ActModel.PullNextEncounter</c> 的钩子给），
/// 火堆的点 = <c>PointType.RestSite</c>（**原版自己造节点，我们不动它**）。
/// 全部在列 3 上按行号依次排开（行 0 = 先古之名，最后一行 = BossMapPoint）。
///
/// ## ⚠️ 这些点**必须放进网格**（踩过大坑：黑屏）
/// 原版 <c>RunManager.EnterMapCoordInternal</c> 的 IL：
/// <code>
///   var point = State.Map.GetPoint(coord);                              // 网格里没有 ⇒ null
///   return EnterMapPointInternal(coord.row + 1, point.PointType, ...);  // ⇒ NullReferenceException
/// </code>
/// 只靠 <c>AddChildPoint</c> 串链、不放进网格 ⇒ 出发动画播完、房间永远建不出来 = 黑屏。
///
/// ## 放网格的副作用
/// <c>NMapScreen.SetMap</c> 会给每个网格点先建一个普通节点（<c>NNormalMapPoint</c>）：
/// 火堆节点的外观正是我们要的（`map_rest` 图标）⇒ 留着；
/// 伪装 BOSS 节点则被 <see cref="Act5MidBoss" /> 撤掉、换成我们自己的 <see cref="Act5BossNode" />。
///
/// ## 串链必须等注入之后
/// <c>SetMap</c> 建完节点会遍历起点的 children 去画连线，那时注入节点还不存在 ⇒ KeyNotFound。
/// 所以串链（先古之名 → 第一个点 → … → BossMapPoint）与连线都由 <see cref="Act5MidBoss" /> 补。
///
/// ## 明确不用：N10 / 双重 BOSS 机制
/// <c>SecondBossMapPoint</c> **不设**（基类默认 null），最终 BOSS 就是 <c>BossMapPoint</c>。
/// </summary>
internal sealed class Act5LinearMap : ActMap
{
    private const int Columns = 7;
    private const int MiddleColumn = 3;

    /// <summary>行数下限：先古之名(0) + 11 个链位(1..11) + BOSS(12)（= 原来的固定 13）。</summary>
    private const int MinRows = 13;

    private readonly MapPoint[,] _grid;
    private readonly MapPoint _starting;
    private readonly MapPoint _boss;
    private readonly List<MapPoint> _chain = new();      // 按行号升序：伪装BOSS / 火堆 交替
    private readonly List<MapPoint> _bossPoints = new();

    public Act5LinearMap()
    {
        var bossCount = Math.Max(1, Act5BossDisplay.DisguisedCount);
        var hearthCount = Act5BossDisplay.HearthsBetween ? Math.Max(0, bossCount - 1) : 0;
        var chainCount = bossCount + hearthCount;

        var rows = Math.Max(MinRows, chainCount + 2);   // 留出起点(行0)与BOSS(最后一行)

        _grid = new MapPoint[Columns, rows];

        _starting = new MapPoint(MiddleColumn, 0) { PointType = MapPointType.Ancient };
        startMapPoints.Add(_starting);

        _boss = new MapPoint(MiddleColumn, rows - 1) { PointType = MapPointType.Boss };

        for (var i = 0; i < chainCount; i++)
        {
            var row = i + 1;
            var isHearth = Act5BossDisplay.HearthsBetween && i % 2 == 1;

            var point = new MapPoint(MiddleColumn, row)
            {
                // 火堆：RestSite（原版自己建房、自己造节点，无需池子）
                // 伪装BOSS：Monster（内容由 PullNextEncounter 钩子给；图标由 Act5BossNode 自己上）
                PointType = isHearth ? MapPointType.RestSite : MapPointType.Monster,
                CanBeModified = false
            };

            _grid[MiddleColumn, row] = point;
            _chain.Add(point);
            if (!isHearth) _bossPoints.Add(point);
        }

        MainFile.DebugLog(
            $"[Act5LinearMap] 建图: 网格 {Columns}x{rows} | 先古之名(3,0) | " +
            $"链位 {chainCount}（伪装BOSS {_bossPoints.Count} + 火堆 {chainCount - _bossPoints.Count}） | " +
            $"BOSS(3,{rows - 1}) = BossMapPoint | 链位: " +
            string.Join(",", _chain.Select(p => $"{(p.PointType == MapPointType.RestSite ? "火" : "B")}{p.coord.row}")));
    }

    /// <summary>伪装 BOSS 的点（按行号升序 = 玩家遇到的顺序）。</summary>
    public IReadOnlyList<MapPoint> DisguisedPoints => _bossPoints;

    /// <summary>整条链（伪装BOSS / 火堆 交替，按行号升序）—— 定位与可通行性都用它。</summary>
    public IReadOnlyList<MapPoint> ChainPoints => _chain;

    protected override MapPoint[,] Grid => _grid;

    public override MapPoint StartingMapPoint => _starting;

    public override MapPoint BossMapPoint => _boss;
}
