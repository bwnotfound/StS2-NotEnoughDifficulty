using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     池子混合权重 (act1, act2, act3)。归一化后总和应当 = 1。
/// </summary>
internal readonly record struct PoolWeights(double Act1, double Act2, double Act3)
{
    public double Sum => Act1 + Act2 + Act3;
}

/// <summary>
///     第 5 层难度档位（需求：考验 / 极限）。
/// </summary>
internal enum Act5Mode
{
    /// <summary>考验：取 2 个 boss（本层场数上限），尽量分散到不同 act。</summary>
    Trial = 0,

    /// <summary>极限：把剩余未打的 boss 全部取走（实际只有前 2 个进得了本层）。</summary>
    Extreme = 1,
}

/// <summary>
///     把 <see cref="NotEnoughDifficultyConfig" /> 的 BaseLib slider 字段封装为语义化 API。
///     池权重 getter 返回值是<b>已归一化</b>的（即使磁盘上的原值不归一，运行时使用值总是 sum=1）。
///
/// 第二轮改造删除的内容：
/// - 敌人移除列表（ExcludedEncounterIdsCsv / ApplyRemovalFilter / ListEncounters / 层后缀映射）
/// - 旧的三层倍率访问器（GetNormalEnemy*/GetBoss*/GetOverall*/GetSource*）与 ScalingRange
/// - 废弃开关 ShouldShowAct5DisguisedBossWarning / ShouldAvoidAct5FinalBossEqualPenultimate
/// 现保留：池权重 + 第5层档位 + BGM 白名单。
/// </summary>
internal static class ExtraActsConfig
{
    // ---------- 池子混合权重 ----------

    /// <summary>
    ///     默认权重值，用于当前权重 sum=0 时的 fallback。
    ///     跟 NotEnoughDifficultyConfig 各权重字段的初始化值保持一致。
    /// </summary>
    public static readonly PoolWeights DefaultWeights = new(0.25, 0.35, 0.40);

    /// <summary>普通/精英战斗池权重。目前仅 act4 使用（act5 改为 boss 直线，不再混合普通池）。</summary>
    public static PoolWeights GetEncounterWeights(int actIdx)
    {
        var raw = actIdx switch
        {
            4 => new PoolWeights(
                NotEnoughDifficultyConfig.Act4_EncWeight_Act1,
                NotEnoughDifficultyConfig.Act4_EncWeight_Act2,
                NotEnoughDifficultyConfig.Act4_EncWeight_Act3),
            _ => DefaultWeights
        };
        return Normalize(raw);
    }

    /// <summary>事件池权重（act4/5 都会用：地图上的问号/事件节点抽什么事件）。</summary>
    public static PoolWeights GetEventWeights(int actIdx)
    {
        var raw = actIdx switch
        {
            4 => new PoolWeights(
                NotEnoughDifficultyConfig.Act4_EventWeight_Act1,
                NotEnoughDifficultyConfig.Act4_EventWeight_Act2,
                NotEnoughDifficultyConfig.Act4_EventWeight_Act3),
            // 第 5 层是 boss 直线考验，不再有事件/问号节点，所以没有它的事件权重
            // （对应配置项已删除，避免玩家看到无意义的滑块）。
            _ => DefaultWeights
        };
        return Normalize(raw);
    }

    /// <summary>
    ///     act4 顶端 boss 的抽取权重（从 act1/2/3 boss 池按权重混合后随机取一个）。
    ///     act5 不用权重——它的两个 BOSS 槽位由 <see cref="Act5BossDisplay" />
    ///     指定（伪 BOSS = 灵魂异鱼；最终 BOSS = 第 3 层的 BOSS）。
    /// </summary>
    public static PoolWeights GetBossWeights(int actIdx)
    {
        var raw = actIdx switch
        {
            4 => new PoolWeights(
                NotEnoughDifficultyConfig.Act4_BossWeight_Act1,
                NotEnoughDifficultyConfig.Act4_BossWeight_Act2,
                NotEnoughDifficultyConfig.Act4_BossWeight_Act3),
            _ => DefaultWeights
        };
        return Normalize(raw);
    }

    /// <summary>第 5 层难度档位（从 slider 的 0/1 映射到枚举）。</summary>
    public static Act5Mode GetAct5Mode()
    {
        return NotEnoughDifficultyConfig.Act5Difficulty >= 0.5 ? Act5Mode.Extreme : Act5Mode.Trial;
    }

    /// <summary>
    ///     归一化到 sum=1。sum 接近 0 时（用户手动改成全 0）回落到默认权重，避免除 0。
    /// </summary>
    private static PoolWeights Normalize(PoolWeights raw)
    {
        var sum = raw.Sum;
        if (sum <= 1e-9) return DefaultWeights;
        if (Math.Abs(sum - 1.0) < 1e-6) return raw;
        return new PoolWeights(raw.Act1 / sum, raw.Act2 / sum, raw.Act3 / sum);
    }

    // ============================================================
    // BGM Bank "额外可播放 event" 白名单
    // ============================================================
    //
    // 自定义 act（Act4）的 BGM bank 复用 act3 (Glory) 的 bank，所以 act3 boss 的 event ID
    // 必然包含在 bank 中。但混合战斗会拿 act1/2 的 boss encounter，act3 的 bank 不一定包含
    // 它们的 BgmEvent，调用 PlayCustomMusic 会失败。
    //
    // CustomActMissingBgmFallbackPatch 的策略：
    //   1. 优先尝试播放 encounter 自己的 BgmEvent
    //   2. 失败则回落到 act 的默认 boss BGM
    //
    // 白名单解决反向问题：act3 bank 包含但**不属于** act3 boss 的 event
    // （如 CeremonialBeast 是 act2 boss 却被 act3 bank 包含），这些可直接播放。

    /// <summary>
    ///     act3 bank（被自定义 act 复用）实际包含但不属于 act3 boss 的 event ID。
    /// </summary>
    public static readonly HashSet<string> Act3BankCrossActExtras =
        new(StringComparer.Ordinal)
        {
            "event:/music/boss/ceremonial_beast"
        };
}
