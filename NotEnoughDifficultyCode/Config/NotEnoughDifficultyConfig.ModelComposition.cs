using BaseLib.Config;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     Partial: 内容构成相关配置（第二轮保留/新增）。
///
/// 保留：act4 的池子混合权重与事件池权重（act4 仍然是有普通/精英战斗的层，
/// 它的战斗内容与地图问号事件仍按权重从 act1~3 混合抽取）。
///
/// 删除（需求4）：
/// - 旧 Act5_BossWeight_*：act5 的 boss 不再按权重抽，改由 <see cref="Act5BossDisplay" />
///   直接指定两个槽位（伪 BOSS = 灵魂异鱼；最终 BOSS = 第 3 层的 BOSS）
/// - 旧的全部倍率字段（Overall / NormalEnemy / Boss / Src / 难度预设 / 地图长度）
/// - 旧的 Experimental.DeterministicModelHash 开关：多模型哈希确定性是联机正确性的硬前提，
///   不该给玩家关掉的选项，已改为恒定启用（见 DeterministicModelHashPatch）
/// </summary>
internal partial class NotEnoughDifficultyConfig
{
    [ConfigSection("Act4_EncWeights")]
    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EncWeight_Act1 { get; set; } = 0.25;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EncWeight_Act2 { get; set; } = 0.35;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EncWeight_Act3 { get; set; } = 0.40;

    [ConfigSection("Act4_EventWeights")]
    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EventWeight_Act1 { get; set; } = 0.25;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EventWeight_Act2 { get; set; } = 0.35;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_EventWeight_Act3 { get; set; } = 0.40;

    // 注意：第 5 层的事件池权重已删除——第 5 层是 boss 直线考验，不再有事件/问号节点，
    // 留着那些滑块只会让玩家困惑（需求："第五层为什么还有事件池权重？"）。

    [ConfigSection("Act4_BossWeights")]
    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_BossWeight_Act1 { get; set; } = 0.25;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_BossWeight_Act2 { get; set; } = 0.35;

    [ConfigSlider(0, 1, 0.05)]
    public static double Act4_BossWeight_Act3 { get; set; } = 0.40;

    /// <summary>
    ///     第 5 层「自定义直线地图」开关（默认 <b>开启</b>）。
    ///
    /// ## 现在这张自定义地图长什么样
    /// **只有两个节点**：<c>Ancient → BOSS</c>。BOSS 节点是玩家在本层打的**第一场**，
    /// 打完之后由游戏原生的**双重 BOSS 流程**把玩家送到第二场
    /// （<c>SecondBossEncounter</c>，中间夹合成火堆/商店）。
    /// 口径见 <see cref="Act5BossDisplay" />。
    ///
    /// ## 为什么现在敢默认开
    /// 以前默认关，是因为旧版在自定义地图里**自己摆 N 个 BOSS 节点**，
    /// 实测出问题（只出一个 boss、图标错误、第三个房间进不去）。
    /// 现在地图只摆**一个** BOSS 节点，其余全交给原生双 boss 通道 —— 那套机制在 act1~4 已实测可用。
    ///
    /// 关掉本开关 = act5 用原版地图结构（boss 名单仍然按同样口径落到两条通道上）。
    /// </summary>
    [ConfigSection("Act5Map")]
    public static bool Act5_LinearMap { get; set; } = true;

    /// <summary>
    ///     **与"第 4 幕被别的模组占用"的兼容**（例如 ACT 4 心脏模组）。
    ///
    /// 打开（默认）：检测到第 4 幕已被别的模组占用时，本模组的两个幕自动顺延为
    /// <b>第 5、6 幕</b>（精英幕 → 第 5 幕，传奇/神话幕 → 第 6 幕），并保证它们排在 act 列表末尾。
    /// 检测口径与实现见 <see cref="ActLayout" />（读 <c>ModelDb.ActsByIndex</c> / <c>ModelDb.Acts</c>，
    /// 不写死任何具体 act 类型；连"没声明 Index、只按名字叫心脏"的模组也能认出）。
    ///
    /// 关闭：不顺延。两个模组会同时往第 4 幕塞内容（谁生效取决于加载顺序），**不建议**。
    ///
    /// 注意：<c>Act4_*</c> / <c>Act5_*</c> 这些"按层"开关**始终**对<b>本模组自己的两个幕</b>生效
    /// （不随顺延漂移），所以打开这个开关不会让你的旧配置失效。
    /// </summary>
    [ConfigSection("Compat")]
    public static bool Act4HeartAutoShift { get; set; } = true;

    /// <summary>
    ///     **自带背景的敌人**是否也按"来源幕"强制覆盖（**默认关**）。
    ///
    /// 背景资产的真实分支（IL 实证，见 <see cref="HomeActBackgroundRedirectPatch" />）：
    /// <c>EncounterModel.GetBackgroundAssets</c> 里 <c>HasCustomBackground == true</c> 的敌人
    /// 会用**它自己带的背景**，不会去问 act —— 这通常正是那个模组想要的美术效果
    /// （例如 ActsFromThePast 的自绘竞技场），所以默认**尊重**它、不覆盖。
    ///
    /// 打开 = 强制按来源幕重造背景（牺牲那个模组的美术，换取"背景一定跟着来源幕变"）。
    /// </summary>
    public static bool Act4_OverrideEncounterBackground { get; set; } = false;
}
