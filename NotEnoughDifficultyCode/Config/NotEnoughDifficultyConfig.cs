using BaseLib.Config;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     NotEnoughDifficulty 的 Mod 配置类。
///
/// ## 总开关
/// <see cref="Enabled" /> 是 mod 行为总开关。关闭后所有 patch 在入口都早返，
/// 等价于"mod 进入睡眠"——但 patch 仍然绑定在 base game 方法上，所以你能在主菜单切换
/// 而不必重启。注意：自定义 act 已经被 BaseLib 注册到了 ModelDb，关闭 Enabled 不会让
/// act4/5 从 act 列表里消失（会影响 multiplayer mod 校验），如果想完全卸载 mod 请在
/// mod 列表禁用本 mod。
///
/// ## 难度模型（第二轮改造后）
/// 旧的「全局倍率 × 来源倍率 × 总倍率」三层链、四个难度预设、按层地图长度、敌人移除列表
/// <b>全部删除</b>。现在只有一套按爬塔进度线性叠加的强化：
/// <code>
///   血量额外强化% = 当前 ActFloor × 0.1 × Y     （Y = HpScaleFactor）
///   攻击额外强化% = 当前 ActFloor × 0.05 × X    （X = DmgScaleFactor）
/// </code>
/// 是否对某层生效由 <c>Act{N}_ExtraScaling</c> 开关决定（1~3 层默认关、4~5 层默认开）。
/// 详见 <c>NotEnoughDifficultyConfig.Difficulty.cs</c>。
///
/// ## Partial 拆分
/// - .cs（本文件）：类头 + Enabled
/// - .Difficulty.cs：难度公式系数 X/Y、各层额外强化开关、层构成（双 boss / 第5层档位）
/// - .Behaviors.cs：行为开关
/// - .Speed.cs：速度倍率（[ConfigSyncIgnore]）
///
/// ⚠️ 本轮为破坏性变更：删除了大量旧字段。旧 cfg 文件里残留的键在加载时会被忽略（不会崩），
/// 但玩家之前调过的倍率/长度设置不再生效。
/// </summary>
internal partial class NotEnoughDifficultyConfig : SimpleModConfig
{
    /// <summary>
    ///     Mod 行为总开关。false 时所有自定义 act 相关 patch（数值强化、池子混合、UI 修正等）
    ///     都跳过；patch 仍然存在但不工作。
    /// </summary>
    [ConfigSection("General")]
    public static bool Enabled { get; set; } = true;

    /// <summary>
    ///     日志调试开关（默认<b>关闭</b>）。
    ///     打开后才会输出地图结构、boss 抽取、合成节点等诊断日志；
    ///     关闭时只保留错误日志，避免刷屏影响正常游玩。
    ///     排查问题时打开它，把 log 发给开发者即可。
    /// </summary>
    public static bool DebugLogging { get; set; } = false;
}
