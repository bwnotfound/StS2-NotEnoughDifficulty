using BaseLib.Abstracts;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     第 5 层。
///     视觉/音乐：背景借 act 3 (Glory)，BGM 也是 act 3。
///     关卡（用户方案）：**先古之名 → 灵魂异鱼(伪装BOSS) → 知识恶魔(伪装BOSS) → 最终BOSS**。
///     两个伪装 BOSS 的节点是 <see cref="Act5BossNode" />（我们自己复刻的节点类，
///     图标跟着各自的 encounter 走），房间内容是普通战斗房 ——
///     由 <see cref="Act5EncounterPoolPatch" /> 通过 <c>ActModel.PullNextEncounter</c> 供给。
///     最终 BOSS 走原生 <c>BossEncounter</c> + 地图 <c>BossMapPoint</c>，不用双 BOSS 机制。
///     Encounter 池：
///     - 不混合 AllBossEncounters（保持 = Glory boss 池），因为需求 5.3 要求最终 boss 必须从 act3 抽
///     - 普通遭遇池**故意为空**：act5 的"小怪"就是两个伪装 BOSS，由上面那个钩子按进度给
///     Event 池：跟 Act4 同样，用 BuildWeightedFlatList 加权混合 act1+2+3 的事件池。
///     Ancient 池：复用 Glory。这是必需的——map 起始节点是 MapPointType.Ancient，渲染时需要
///     _runState.Act.Ancient 不为 null，否则进入 act 时 UI 卡死。
///     顶端最终 boss 不与前 4 层 boss 重复 + 应用 boss 池过滤开关（如 ExcludeDoormakerFromBossPool）
///     由 DeduplicateCustomActBossesPatch 保证。
/// </summary>
public class Act5Model : CustomActModel
{
    public Act5Model() : base(-1)
    {
    }

    // 地图背景图：Glory
    protected override string CustomMapTopBgPath =>
        ImageHelper.GetImagePath("packed/map/map_bgs/glory/map_top_glory.png");

    // ============================================================
    // 地图染色：第 5 层改成红色（需求："地图的黄色染色改成红色"）
    //
    // base game 的黄色来自 act 默认的 MapTraveledColor / MapUntraveledColor。
    // 这里把未走过的路径染成偏红的暗红、走过的染成亮红，整体呈"血路"观感，
    // 与"boss 直线"的压迫感一致。
    //
    // 只改颜色，不动任何逻辑；颜色是纯静态值，多人两端天然一致。
    // ============================================================

    /// <summary>已走过的路径：亮红。</summary>
    public override Color MapTraveledColor => new(0.86f, 0.20f, 0.18f);

    /// <summary>未走过的路径：暗红（保留可辨识度，不至于看不见）。</summary>
    public override Color MapUntraveledColor => new(0.42f, 0.11f, 0.13f);

    protected override string CustomMapMidBgPath =>
        ImageHelper.GetImagePath("packed/map/map_bgs/glory/map_middle_glory.png");

    protected override string CustomMapBotBgPath =>
        ImageHelper.GetImagePath("packed/map/map_bgs/glory/map_bottom_glory.png");

    protected override string CustomRestSiteBackgroundPath =>
        SceneHelper.GetScenePath("rest_site/glory_rest_site");

    public override string[] BgMusicOptions =>
        new[] { "event:/music/act3_a1_v1", "event:/music/act3_a2_v1" };

    /// <summary>
    ///     曲库：**加载 act1+2+3 全部 6 个 bank**（与 act4 同样的理由）。
    ///
    /// 之前只挂 act3 的两个 bank → 伪装BOSS 若来自一层/二层，它自己的
    /// <c>EncounterModel.CustomBgm</c>（例如 <c>event:/music/act1_b_boss_soul_fysh</c>）不在内存里，
    /// audio engine 报 "cannot find music path" ⇒ **BOSS 战没音乐**，只能靠
    /// <see cref="CustomActMissingBgmFallbackPatch" /> 兜成 act3 的曲子。
    /// bank 不大，全加载最稳 —— 这样每场伪装BOSS 都播它自己那首，
    /// 背景也会跟着它的 <c>EncounterModel.CreateBackground</c> 走（和 act4 精英一致）。
    /// </summary>
    public override string[] MusicBankPaths => RunProgress.CollectMusicBanksForBaseLayers();

    public override string AmbientSfx => "event:/sfx/ambience/act3_ambience";

    public override IEnumerable<EventModel> AllEvents
    {
        get
        {
            var w = ExtraActsConfig.GetEventWeights(5);
            var weightedEventPools = new List<(IReadOnlyList<EventModel>, double)>
            {
                (RunProgress.CollectFromLayer(1, a => a.AllEvents), w.Act1),
                (RunProgress.CollectFromLayer(2, a => a.AllEvents), w.Act2),
                (RunProgress.CollectFromLayer(3, a => a.AllEvents), w.Act3)
            };
            return EncounterListBuilder.BuildWeightedFlatList(weightedEventPools);
        }
    }

    // Ancient 池：复用 Glory（同 Act4 注释，避免起始节点崩）
    public override IEnumerable<AncientEventModel> AllAncients =>
        RunProgress.CollectFromLayer(3, a => a.AllAncients);

    protected override int BaseNumberOfRooms => 13;

    // 关键：基础池 = Glory.AllEncounters，不混合 boss
    // Act5 最终 boss 必须从 act3 boss 池抽（需求 5.3）
    //
    // 注意这里**不**调用 ApplyBossPoolFilters——AllEncounters 是 lazy 缓存（mod 加载时跑一次后定型），
    // 在这里过滤会让运行时的开关变化不生效。boss 池过滤放在 DeduplicateCustomActBossesPatch 里做。
    public override IEnumerable<EncounterModel> GenerateAllEncounters()
    {
        return RunProgress.CollectFromLayer(3, a => a.AllEncounters);
    }

    public override IEnumerable<AncientEventModel> GetUnlockedAncients(UnlockState state)
    {
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

    /// <summary>
    ///     第 5 层的地图：**先古之名（起点）→ 两个伪装BOSS（注入）→ 最终BOSS（BossMapPoint）**。
    ///
    /// BaseLib 的 <c>CustomActModel</c> 已经替所有自定义 act 打好了
    /// <c>ActModel.CreateMap</c> 的 prefix：
    /// <code>
    ///   if (__instance is not CustomActModel customAct) return true;
    ///   __result = customAct.CustomCreateMap(runState, replaceTreasureWithElites);
    ///   return __result == null;
    /// </code>
    /// 所以这里只要覆盖本方法即可，**不需要**自己写 Harmony prefix 去抢 <c>CreateMap</c>。
    ///
    /// 只有最终 BOSS 占原生 BOSS 槽位（<c>BossEncounter</c>，由 <see cref="ActBlueprint" />
    /// 在黑屏窗口内定稿）；<c>SecondBossMapPoint</c> 不设 —— 不套 N10 / 双重 BOSS 机制。
    /// 两个伪装 BOSS 的节点与房间内容分别由 <see cref="Act5MidBoss" /> 与
    /// <see cref="Act5EncounterPoolPatch" /> 负责。
    /// </summary>
    protected override ActMap? CustomCreateMap(RunState runState, bool replaceTreasureWithElites)
    {
        try
        {
            // 配置开关（默认开）。关掉 = 第 5 层用原版地图结构（两个 BOSS 槽位仍然生效）。
            if (!NotEnoughDifficultyConfig.Act5_LinearMap)
            {
                MainFile.DebugLog("[Act5Model] Act5_LinearMap 已关，交回原版建图");
                return null;
            }

            // ★ 名单必须**在建图之前**定稿（用户实测："我神话的火堆怎么没了?"）。
            //   <see cref="Act5LinearMap" /> 的链位数量 = 伪装BOSS 数 + 火堆数（极限档 = 场数 − 1），
            //   而名单原本是在黑屏窗口里（<see cref="ActBlueprint" />）才排的 —— 建图若先发生，
            //   名单还是空的 ⇒ 建出"链位 1、火堆 0"的地图，之后再注入 BOSS 也没有火堆的位置了。
            //   实测日志：
            //     [Act5LinearMap] 建图: 链位 1（伪装BOSS 1 + 火堆 0）
            //     [Act5] BOSS 编排（极限）: 伪装=… 10 个 … | 火堆间隔=True      ← 名单晚了一步
            if (!Act5BossDisplay.HasPlan)
            {
                var extreme = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme;
                Act5BossDisplay.BuildPlan(runState, extreme);
                MainFile.DebugLog(
                    $"[Act5Model] 建图前定稿名单: 伪装BOSS {Act5BossDisplay.DisguisedCount} 场 | " +
                    $"火堆间隔={Act5BossDisplay.HearthsBetween} | 最终BOSS='{Act5BossDisplay.Finale?.Id?.Entry ?? "<空>"}'");
            }

            return new Act5LinearMap();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act5Model] CustomCreateMap 失败，交回原版建图: {ex}");
            return null;
        }
    }
}