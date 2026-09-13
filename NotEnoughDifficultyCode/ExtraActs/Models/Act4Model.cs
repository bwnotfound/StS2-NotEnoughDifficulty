using BaseLib.Abstracts;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Unlocks;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     第 4 层。
///     视觉/音乐：背景借 act 2 (Hive)，平时 BGM 用 act 2，同时加载 act 3 banks 给 boss CustomBgm 用。
///     关卡：所有节点 PointType 强制为 Elite（由 MapPointTypeFixupPatch 实现），战斗实际抽 eliteEncounters。
///     Encounter 池（在本类中处理）：
///     - non-boss encounters: 复用 Glory 的（实际 elite 战斗内容由 CustomActEncounterReplacementPatch 用
///     加权混合池替换，所以这里返回什么不重要）
///     - boss encounters: 把 act1+2+3 的 boss encounter 按权重重复添加进 AllEncounters，
///     这样 act.AllBossEncounters（继承自 ActModel 的 filter 属性）自带权重，
///     DeduplicateCustomActBossesPatch 用 rng.NextItem 抽样时等价加权抽样
///     Event 池：用 EncounterListBuilder.BuildWeightedFlatList 加权混合 act1+2+3 的事件池。
///     Ancient 池：复用 Glory（每个 act 的起始节点都是 MapPointType.Ancient，UI 渲染为 NAncientMapPoint,
///     它在 _Ready 里需要读 _runState.Act.Ancient.MapIcon。如果 AllAncients 为空，
///     _rooms.Ancient 会未填充，渲染时 NullReferenceException 卡住地图）。
///     顶端 boss 不与前 3 层 boss 重复 + 应用 boss 池过滤开关（如 ExcludeDoormakerFromBossPool）
///     由 DeduplicateCustomActBossesPatch 保证。注意 boss 池过滤**不能放在 GenerateAllEncounters 里**——
///     ActModel.AllEncounters 是 lazy 缓存，加载时跑一次后不再重新生成，运行时改开关不会生效。
/// </summary>
public class Act4Model : CustomActModel
{
    /// <summary>上一次打过的来源 act（只为日志去重，防止预载/每帧调用刷屏）。</summary>
    private static string? _lastLoggedSource;

    public Act4Model() : base(-1)
    {
    }

    // ============================================================
    // 视觉主题：按「未打精英的主导来源层」动态决定（需求：第四层精英背景按来源层变化）
    //
    // ⚠️ 战斗背景/地图染色**不再走这里**：那两处已精确到"敌人自己所属的 act"
    //    （见 Act4FightSource / Act4VisualPatches）——同一层可能有多个 act 变体
    //    （层 1 = Overgrowth / Underdocks），按层取代表 act 会让两个变体长一样。
    //    下面这套"主导来源层"只用于**整个 act 的底色**：地图拼接底图 / 篝火背景 / BGM / 环境音。
    // ============================================================

    /// <summary>1=Overgrowth(act1) / 2=Hive(act2) / 3=Glory(act3)。取不到时回退 2（Hive，原行为）。</summary>
    private static int DominantSourceAct()
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            return RunProgress.GetDominantEliteSourceAct(state);
        }
        catch
        {
            return 2;
        }
    }

    private static string ActSlug(int actIdx) => actIdx switch
    {
        1 => "overgrowth",
        3 => "glory",
        _ => "hive",
    };

    // 注意：BGM 的 event 版本号各 act 不一致（act1/act3 是 _v1、act2 是 _v2），
    // 所以音乐<b>不能</b>用统一模板拼；沿用原本已验证可用的固定值，只按主导层切换。
    private static string ActMusicSlug(int actIdx) => actIdx switch
    {
        1 => "act1",
        3 => "act3",
        _ => "act2",
    };

    private static string BgmFor(int actIdx, int idx) => (actIdx, idx) switch
    {
        (1, 1) => "event:/music/act1_a1_v1",
        (1, _) => "event:/music/act1_a2_v2",
        (3, 1) => "event:/music/act3_a1_v1",
        (3, _) => "event:/music/act3_a2_v1",
        (_, 1) => "event:/music/act2_a1_v2",
        (_, _) => "event:/music/act2_a2_v2",
    };

    /// <summary>
    ///     取"主导来源层"对应的 base act 模型（1=Overgrowth / 2=Hive / 3=Glory）。
    ///     用它的公开属性拿背景路径与篝火场景——<b>不猜任何资源路径</b>，
    ///     与每个 act 的运行时身份天然一致；拿不到时回退 null（调用方用原 Hive 固定值兜底）。
    /// </summary>
    private static ActModel? DominantSourceActModel()
    {
        try
        {
            return DominantSourceAct() switch
            {
                1 => RunProgress.GetRepresentativeAct(1),
                3 => RunProgress.GetRepresentativeAct(3),
                _ => RunProgress.GetRepresentativeAct(2),
            };
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // 地图配色：**淡紫**（用户口径："问号/商店/火堆/精英/BOSS 都要把底图黄色改成淡紫"）
    //
    // ⚠️ 别去换节点贴图（踩过：图标是**图集** AtlasTexture，换成整图会让节点渲染不出来 ⇒
    //    用户实测"精英/问号/商店/火堆全部消失"）。正路是改 act 的**地图染色**——
    //    原版节点图标就是被这几个颜色染的（`NNormalMapPoint._Ready` 把 _mapColor 传给图标 shader）。
    // ============================================================

    /// <summary>走过的路径/节点：亮紫。</summary>
    public override Godot.Color MapTraveledColor => new(0.74f, 0.58f, 0.96f);

    /// <summary>没走过的：淡紫（偏灰，避免过饱和）。</summary>
    public override Godot.Color MapUntraveledColor => new(0.52f, 0.42f, 0.72f);

    /// <summary>底色：带一点紫的深色。</summary>
    public override Godot.Color MapBgColor => new(0.30f, 0.24f, 0.42f);
    // ============================================================
    // 战斗背景：按"本场敌人的来源层"生成（**BaseLib 官方钩子**，不是反射换节点）
    //
    // BaseLib 的扩展点是 `CustomGenerateBackgroundAssets(Rng)`（`CustomActModel` 提供），
    // 战斗背景资产就是从这里出的。我们先前的做法是在 NCombatRoom.SetUpBackground 之后
    // 反射换掉 Background 对象 —— 日志显示确实换了、在树里、也有 7 个图层，但画面不变
    // （原版很可能在更晚的地方又按 `_visuals.Act` 生成一次）。改走官方钩子就没有这个时序问题。
    // ============================================================

    protected override BackgroundAssets CustomGenerateBackgroundAssets(Rng rng)
    {
        // 本场来源 = **该敌人自己所属的 act**（由当前房间解析，见 Act4FightSource.Current）。
        // 不按"层"取代表 act —— 同一层可能有多个 act（层 1 = Overgrowth / Underdocks），
        // 那样两个子变体会长得一模一样（用户实测反馈）。
        // ⚠️ **只对精英生效**：BOSS / 其它房间一律用本幕自己的默认背景 ——
        //    否则本幕的 BOSS 场景会被来源幕顶掉（用户实测："ACT 5/6 的 BOSS 场景没了"）。
        var source = Act4FightSource.IsEliteFight() ? Act4FightSource.Current() : null;

        // ⚠️ 防自我递归（踩过：日志 9.7MB 刷屏 → 崩溃）：
        //    本 act 是"聚合 act"（池子 = 第三层所有 act 的并集），一旦来源被解析成自己，
        //    再调 source.GenerateBackgroundAssets 就会回到本方法 ⇒ 无限递归。
        //    这里显式挡掉，并且 Act4FightSource/FindActOfEncounter 也各自有一道拦截。
        if (source != null && !ReferenceEquals(source, this))
        {
            // 同一种来源只打一次（这个方法可能被预载/每帧调用，刷屏会淹掉日志）
            var key = source.GetType().Name;
            if (_lastLoggedSource != key)
            {
                _lastLoggedSource = key;
                MainFile.DebugLog($"[Act4] 战斗背景资产取 '{key}'（层 {Act4FightSource.Layer}）");
            }

            return source.GenerateBackgroundAssets(rng);      // 公开 API：问该 act 要它自己的资产
        }

        return base.CustomGenerateBackgroundAssets(rng);
    }
    // 地图背景：优先用主导来源层 act 自己的背景，取不到则回退原来的 Hive 固定图
    protected override string CustomMapTopBgPath =>
        DominantSourceActModel()?.MapTopBgPath
        ?? ImageHelper.GetImagePath("packed/map/map_bgs/hive/map_top_hive.png");

    protected override string CustomMapMidBgPath =>
        DominantSourceActModel()?.MapMidBgPath
        ?? ImageHelper.GetImagePath("packed/map/map_bgs/hive/map_middle_hive.png");

    protected override string CustomMapBotBgPath =>
        DominantSourceActModel()?.MapBotBgPath
        ?? ImageHelper.GetImagePath("packed/map/map_bgs/hive/map_bottom_hive.png");

    // 篝火背景：同样跟随主导来源层
    protected override string CustomRestSiteBackgroundPath =>
        DominantSourceActModel()?.RestSiteBackgroundPath
        ?? SceneHelper.GetScenePath("rest_site/hive_rest_site");

    public override string[] BgMusicOptions
    {
        get
        {
            var a = DominantSourceAct();
            return new[] { BgmFor(a, 1), BgmFor(a, 2) };
        }
    }

    // banks：**加载 act1+2+3 全部**。
    // 之前只加载"主导层 + act3" → encounter 自带的 BgmEvent 若属于别的 bank（例如 act2 的曲子），
    // 运行时就会 "cannot find music path"（用户实测 act4 刷了一堆这种错误）。
    // 曲库不大，全加载最稳。
    public override string[] MusicBankPaths => RunProgress.CollectMusicBanksForBaseLayers();

    public override string AmbientSfx => $"event:/sfx/ambience/{ActMusicSlug(DominantSourceAct())}_ambience";

    public override IEnumerable<EventModel> AllEvents
    {
        get
        {
            var w = ExtraActsConfig.GetEventWeights(4);
            // 按层聚合事件池（走 ActsByIndex，含模组加的 act 变体）
            var weightedEventPools = new List<(IReadOnlyList<EventModel>, double)>
            {
                (RunProgress.CollectFromLayer(1, a => a.AllEvents), w.Act1),
                (RunProgress.CollectFromLayer(2, a => a.AllEvents), w.Act2),
                (RunProgress.CollectFromLayer(3, a => a.AllEvents), w.Act3)
            };
            return EncounterListBuilder.BuildWeightedFlatList(weightedEventPools);
        }
    }

    // Ancient 池：复用 Glory。这是必需的——map 起始节点是 MapPointType.Ancient，
    // 渲染时需要 _runState.Act.Ancient 不为 null。返回空集合会导致进入 act 时卡死。
    public override IEnumerable<AncientEventModel> AllAncients =>
        RunProgress.CollectFromLayer(3, a => a.AllAncients);

    protected override int BaseNumberOfRooms => 13;

    /// <summary>
    ///     <b>原本</b>深度（房间数），用于区分"原版长度内的行"与"因扩深多出来的行"。
    /// 与 <see cref="BaseNumberOfRooms" /> 同值，但语义不同——这个常量是给
    /// <see cref="Act5MapPatch" /> 判断"哪些行是扩深产物"用的（那里不能读实例属性）。
    /// </summary>
    internal const int BaseNumberOfRoomsForDepth = 13;

    // 注意：动态深度**不能**在这里 override GetNumberOfRooms——
    // IL/元数据确认 ActModel.GetNumberOfRooms 不是 virtual（base game 只在内部改 BaseNumberOfRooms）。
    // 所以深度改由 Harmony postfix 覆盖，见 ActDepthPatch。

    /// <summary>act4 战斗节点数下限（保证地图长度 ≥ 7 行，不触发 base game 数组越界）。</summary>
    internal const int MinAct4Rooms = 8;

    public override IEnumerable<EncounterModel> GenerateAllEncounters()
    {
        var result = new List<EncounterModel>();

        // 1) Non-boss encounters: 复用第 3 层全部 act 变体（实际 elite 战内容由 patch 替换）
        foreach (var e in RunProgress.CollectFromLayer(3, a => a.AllEncounters))
            if (e.RoomType != RoomType.Boss)
                result.Add(e);

        // 2) Boss encounters: 按权重重复添加 act1+2+3 的 boss
        // 注意这里**不**调用 ApplyBossPoolFilters——AllEncounters 是 lazy 缓存（mod 加载时跑一次后定型），
        // 在这里过滤会让运行时的开关变化不生效。boss 池过滤放在 DeduplicateCustomActBossesPatch 里做。
        var bossWeights = ExtraActsConfig.GetBossWeights(4);
        var weightedBossPools = new List<(IReadOnlyList<EncounterModel>, double)>
        {
            (RunProgress.CollectFromLayer(1, a => a.AllBossEncounters), bossWeights.Act1),
            (RunProgress.CollectFromLayer(2, a => a.AllBossEncounters), bossWeights.Act2),
            (RunProgress.CollectFromLayer(3, a => a.AllBossEncounters), bossWeights.Act3)
        };
        // baseFactor 取较小值（30）避免 boss 列表过长——boss 抽样只需要权重比例正确即可
        result.AddRange(EncounterListBuilder.BuildWeightedFlatList(weightedBossPools, 30));

        return result;
    }

    public override IEnumerable<AncientEventModel> GetUnlockedAncients(UnlockState state)
    {
        // 按层聚合先古池；GetUnlockedAncients 需要具体 act，这里逐层取第一个可用的
        foreach (var act in RunProgress.GetLayerActs(3))
        {
            try { return act.GetUnlockedAncients(state); } catch { /* 试下一个 */ }
        }
        return RunProgress.CollectFromLayer(3, a => a.AllAncients.Cast<AncientEventModel>()).Cast<AncientEventModel>();
    }

    public override MapPointTypeCounts GetMapPointTypes(Rng mapRng)
    {
        var restCount = mapRng.NextInt(5, 7);
        var unknownCount = MapPointTypeCounts.StandardRandomUnknownCount(mapRng) - 1;
        return new MapPointTypeCounts(unknownCount, restCount);
    }
}