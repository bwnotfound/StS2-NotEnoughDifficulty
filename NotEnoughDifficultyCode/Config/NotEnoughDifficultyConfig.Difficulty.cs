using BaseLib.Config;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     Partial: 难度模型（第二轮改造）。
///
/// ## 难度公式（取代旧的「全局倍率 × 来源倍率 × 总倍率」三层链）
///
/// 全部倍率字段已删除，改为**按玩家实际爬塔进度**线性叠加：
/// <code>
///   血量额外强化% = 当前 ActFloor × 0.1 × HpScaleFactor(Y)
///   攻击额外强化% = 当前 ActFloor × 0.05 × DmgScaleFactor(X)
///
///   HpMult  = 1 + ActFloor × 0.1  × Y / 100
///   DmgMult = 1 + ActFloor × 0.05 × X / 100
/// </code>
///
/// 其中 <c>ActFloor</c> 用 <c>RunState.ActFloor</c> 的原生语义（act 内层数，每 act 重新起算）。
/// 公式**对所有 act 统一计算**，是否真正生效由每层的 <see cref="Act1_ExtraScaling" /> 等开关决定：
/// 开关关闭的层，两个倍率都被强制返回 1.0（= 完全不影响该层）。
///
/// ## 为什么 X / Y 默认 1
///
/// 玩家改这两个数就能整体放大/缩小强化幅度，而不用逐个 act 调。
/// Y=X=1 时：act 第 10 层 → 血量 +100%、攻击 +50%。
///
/// ## 多人同步
///
/// 这些字段都是 <c>public static</c> 且未标 <c>[ConfigSyncIgnore]</c>，会被 ConfigSync 纳入 lobby 同步。
/// 血量/伤害直接进战斗 checksum，两端必须一致，否则 desync——所以同步是硬要求。
/// </summary>
internal partial class NotEnoughDifficultyConfig
{
    /// <summary>
    ///     每层血量强化系数（Y）。额外血量% = ActFloor × 0.1 × Y。
    ///     1.0 = 每层 +0.1% 基数（即第 10 层 +1%×10 = +10%？见公式：ActFloor×0.1×Y 是**百分比**，
    ///     所以第 10 层 = 10 × 0.1 × 1 = 1.0 → 额外 +100%）。
    /// </summary>
    [ConfigSection("Difficulty")]
    [ConfigSlider(0, 20, 0.05)]
    public static double HpScaleFactor { get; set; } = 1.0;

    /// <summary>
    ///     每层攻击强化系数（X）。额外攻击% = ActFloor × 0.05 × X。
    ///     第 10 层、X=1 时 = 10 × 0.05 = 0.5 → 额外 +50%。
    /// </summary>
    [ConfigSlider(0, 20, 0.05)]
    public static double DmgScaleFactor { get; set; } = 1.0;

    /// <summary>
    ///     第 1 层是否启用「额外强化」。默认**关闭**——1~3 层默认与原版一致，
    ///     强化只作用于本 mod 的深层（act4/5）。
    /// </summary>
    [ConfigSection("ExtraScalingPerAct")]
    public static bool Act1_ExtraScaling { get; set; } = false;

    /// <summary>第 2 层是否启用「额外强化」。默认关闭。</summary>
    public static bool Act2_ExtraScaling { get; set; } = false;

    /// <summary>第 3 层是否启用「额外强化」。默认关闭。</summary>
    public static bool Act3_ExtraScaling { get; set; } = false;

    /// <summary>第 4 层是否启用「额外强化」。默认**开启**（本 mod 的深层，深考验）。</summary>
    public static bool Act4_ExtraScaling { get; set; } = true;

    /// <summary>第 5 层是否启用「额外强化」。默认**开启**。</summary>
    public static bool Act5_ExtraScaling { get; set; } = true;

    // ============================================================
    // 层构成（第二轮新增）
    // ============================================================

    /// <summary>
    ///     第 1 层是否启用双 boss。开启后该层的第二个 boss 会从本层 boss 池里另取一个不重复的。
    ///     默认关闭。act5 不需要（它本身就是多 boss 直线，见 Act5Difficulty）。
    ///     注意与进阶 10 的 <c>AscensionLevel.DoubleBoss</c> 的关系：游戏原本只在最后一个 act 出双 boss，
    ///     本 mod 把 act4/5 追加到 act 列表后，那个判定会落到 act5 上；本开关让"哪一层出双 boss"重新由玩家决定。
    /// </summary>
    [ConfigSection("ActComposition")]
    public static bool Act1_DoubleBoss { get; set; } = false;

    /// <summary>第 2 层是否启用双 boss。默认关闭。</summary>
    public static bool Act2_DoubleBoss { get; set; } = false;

    /// <summary>第 3 层是否启用双 boss。默认关闭。</summary>
    public static bool Act3_DoubleBoss { get; set; } = false;

    /// <summary>第 4 层是否启用双 boss（顶端 boss 之后再打一个）。默认关闭。</summary>
    public static bool Act4_DoubleBoss { get; set; } = false;

    /// <summary>
    ///     双 boss 之间是否插入<b>火堆</b>。默认开启。
    ///     实现走「合成节点法」：在地图外的虚拟坐标新建节点（见 SyntheticHearthPatch），
    ///     不动原版地图结构。
    ///
    /// 第 5 层同样受它控制：自定义地图只放一个一层 BOSS，第二个 BOSS 由双重 BOSS 机制追加
    /// （见 <see cref="Act5BossDisplay" />），两者之间的火堆就是靠这里注入的。
    /// </summary>
    public static bool InterBossHearth { get; set; } = true;

    /// <summary>
    ///     双 boss 之间是否插入<b>商店</b>。默认关闭。
    ///     与火堆同时开启时，顺序为 火堆 → 商店（先休息再购物）。
    /// </summary>
    public static bool InterBossShop { get; set; } = false;

    /// <summary>
    ///     第 5 层的难度档位。
    ///
    /// ⚠️ <b>本层最多只能打 2 场 boss</b>（游戏原生只有 <c>BossEncounter</c> +
    /// <c>SecondBossEncounter</c> 两条通道，见 <see cref="Act5BossDisplay" /> 的 IL 说明），
    /// 所以两档的差别是**名单怎么取**，不是场数：
    /// - 0 = <b>考验</b>：从"剩余未打 boss 池"里取 2 个，尽量分散到不同 act。
    /// - 1 = <b>极限</b>：把剩余未打的 boss 全取走（实际也只有前 2 个进得了本层，
    ///   其余的会在日志里警告被丢弃）。
    ///
    /// boss 之间是否夹火堆由 <see cref="InterBossHearth" /> 统一控制（默认开）。
    /// 默认 0（考验）。
    /// </summary>
    [ConfigSlider(0, 1, 1)]
    public static double Act5Difficulty { get; set; } = 0;

    /// <summary>
}
