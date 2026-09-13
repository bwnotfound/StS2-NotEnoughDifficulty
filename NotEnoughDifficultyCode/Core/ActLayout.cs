using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     本模组两个"额外幕"在 run 里的**落位**，以及对"第 4 幕被别的模组占用"的兼容
///     （用户原话：<i>"得兼容ACT ４心脏啊，加个配置－检测到ACT 4心脏的时候自动将本模组改为ACT 5与ACT 6"</i>）。
///
/// ## 为什么会有这个问题
/// 本模组的两个幕是用 <c>Index = -1</c> 注册、再**追加到 act 列表末尾**的
/// （见 <see cref="ExpandActListPatch" />）。而别的模组（例如"ACT 4 心脏"）是用
/// <c>CustomActModel(3)</c> 注册进 <c>ModelDb.ActsByIndex[3]</c> 的 ——
/// 游戏自己的 <c>ActModel.GetRandomList</c> 会遍历 **ActsByIndex 的每一层**各取一个 act
/// （IL 实证），所以那种 act **本来就会自动排在第四个**，本模组的两个幕自然落到第 5、6 个。
///
/// 但下面三件事必须由本模组自己做对，否则"顺延"只是碰巧：
/// <list type="number">
///   <item><b>列表末尾保证</b>：本模组的幕必须始终排在所有其它 act 之后
///         （见 <see cref="EnsureOursAreLast" />；万一别的模组的 postfix 排在我们之后，
///         run 创建时再纠正一次）；</item>
///   <item><b>配置槽位</b>：<c>Act4_*</c> / <c>Act5_*</c> 这些"按层"开关必须跟着
///         <b>本模组的幕</b>走（见 <see cref="ConfigLayerOf" />），而不是绝对层号 ——
///         否则第 4 幕变成心脏之后，玩家的开关会突然去管别人的幕；</item>
///   <item><b>可见性</b>：检测结果必须打进日志（<see cref="Detect" />），
///         否则"为什么我的 act 编号变了"永远是个谜。</item>
/// </list>
///
/// 关闭开关（<c>Act4HeartAutoShift = false</c>）时只禁用"顺延"这套判断（配置槽位仍按本模组的幕绑定）。
/// </summary>
internal static class ActLayout
{
    private static bool _detected;
    private static ActModel? _foreignAct4;

    /// <summary>检测到的、占用第 4 幕的别人的 act（null = 第 4 幕空着，本模组照旧用第 4/5 幕）。</summary>
    internal static ActModel? ForeignAct4
    {
        get
        {
            Detect();
            return _foreignAct4;
        }
    }

    /// <summary>本模组的幕是否顺延（配置允许 + 真的检测到第 4 幕被占）。</summary>
    internal static bool Shifted => NotEnoughDifficultyConfig.Act4HeartAutoShift && ForeignAct4 != null;

    /// <summary>本模组第一个额外幕的层号（4，或顺延后的 5）。</summary>
    internal static int OurFirstLayer => Shifted ? 4 + 1 : 4;

    /// <summary>本模组第二个额外幕的层号。</summary>
    internal static int OurSecondLayer => OurFirstLayer + 1;

    /// <summary>新的一局 / 重载时重新检测（act 列表可能因模组集合变化而变）。</summary>
    internal static void Reset()
    {
        _detected = false;
        _foreignAct4 = null;
    }

    /// <summary>
    ///     检测"第 4 幕是否已被别的模组占用"。三个口径（都不写死具体类型）：
    ///     <list type="number">
    ///       <item><c>ModelDb.ActsByIndex[3]</c> 里除本模组外的 act —— 声明自己就是第 4 幕的；</item>
    ///       <item><c>ModelDb.Acts</c> 里 <c>Index &gt;= 3</c> 的 act（BaseLib 追加进总表、
    ///             但 ActsByIndex 还没重建时也能抓到）；</item>
    ///       <item>名字/id 里带 <c>heart</c>/<c>心脏</c> 的（模组没声明 Index 也能认出来，兜底）。</item>
    ///     </list>
    ///     注意 <c>IsDefault</c> **不能**用来区分"基础 act"：暗港（Underdocks）是基础 act 但
    ///     <c>IsDefault = false</c>（实测 IL），拿它过滤会误判。
    /// </summary>
    internal static void Detect()
    {
        if (_detected) return;
        _detected = true;
        _foreignAct4 = FindForeignAct4();

        if (_foreignAct4 == null)
        {
            MainFile.DebugLog("[ActLayout] 第 4 幕没有被其它模组占用 ⇒ 本模组仍为第 4/5 幕");
            return;
        }

        var id = $"{_foreignAct4.Id?.Entry}（{_foreignAct4.GetType().Name}）";
        if (NotEnoughDifficultyConfig.Act4HeartAutoShift)
        {
            MainFile.Logger.Info(
                $"[ActLayout] 检测到第 4 幕已被 '{id}' 占用 ⇒ 本模组自动顺延为第 {OurFirstLayer}/{OurSecondLayer} 幕" +
                "（= 本模组的精英幕 / 传奇·神话幕）");
        }
        else
        {
            MainFile.Logger.Warn(
                $"[ActLayout] 检测到第 4 幕已被 '{id}' 占用，但 Act4HeartAutoShift 关闭 ⇒ 不顺延" +
                "（两个模组会同时塞第 4 幕，可能互相覆盖；建议打开该开关）");
        }
    }

    /// <summary>
    ///     "按层配置"的槽位。**本模组的两个幕永远读 4 / 5 号开关**
    ///     （语义 = 本模组的第 1 / 第 2 个额外幕，与它们实际排在第 4/5 还是第 5/6 无关）；
    ///     基础三层按绝对层号；别人的额外幕（第 4 幕及以后）返回 -1 ——
    ///     本模组不去改别的模组的幕。
    /// </summary>
    internal static int ConfigLayerOf(ActModel? act, RunState? state)
    {
        if (act is Act4Model) return 4;
        if (act is Act5Model) return 5;

        var idx = RunProgress.GetActIndexFromModel(act, state);
        return idx is >= 1 and <= 3 ? idx : -1;
    }

    /// <summary>
    ///     保证本模组的两个幕排在 act 列表**最末尾**（其余 act 保持原顺序）。
    ///     只有"检测到第 4 幕被占 + 本模组的幕不在末尾"时才动，避免干涉无关的模组组合。
    ///     返回是否真的改过。
    /// </summary>
    internal static bool EnsureOursAreLast(RunState? state)
    {
        try
        {
            var acts = state?.Acts;
            if (acts == null || acts.Count == 0) return false;
            if (ForeignAct4 == null) return false;                 // 没人占第 4 幕 → 不需要纠正

            var list = acts.ToList();
            var ours = list.Where(a => a is Act4Model or Act5Model).ToList();
            if (ours.Count == 0) return false;

            // 已经都在末尾？
            var firstOurIndex = list.FindIndex(a => a is Act4Model or Act5Model);
            if (firstOurIndex == list.Count - ours.Count) return false;

            var rest = list.Where(a => a is not (Act4Model or Act5Model)).ToList();
            var reordered = rest.Concat(ours).ToList();

            // Acts 是 private setter ⇒ 用 Traverse 换掉整个列表（run 刚创建，还没有任何按 act 索引的数据）
            Traverse.Create(state).Property("Acts").SetValue(reordered);

            MainFile.Logger.Info(
                "[ActLayout] 本模组的幕被别的模组插到了前面 ⇒ 已重排到末尾：" +
                string.Join(" -> ", reordered.Select(a => a?.Id?.Entry)));
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ActLayout] 重排 act 列表失败（保持原顺序）: {ex.Message}");
            return false;
        }
    }

    private static ActModel? FindForeignAct4()
    {
        // ① ActsByIndex 的第 4 格（Index = 3）
        try
        {
            var byIndex = ModelDb.ActsByIndex;
            if (byIndex.Count > 3)
            {
                foreach (var act in byIndex[3] ?? Array.Empty<ActModel>())
                    if (act != null && !IsOurs(act)) return act;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ActLayout] 读 ActsByIndex[3] 失败: {ex.Message}");
        }

        // ② 总表里声明 Index >= 3 的
        try
        {
            foreach (var act in ModelDb.Acts)
                if (act != null && !IsOurs(act) && act.Index >= 3) return act;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ActLayout] 扫 ModelDb.Acts 失败: {ex.Message}");
        }

        // ③ 兜底：名字像"心脏"的（模组可能没声明 Index）
        try
        {
            foreach (var act in ModelDb.Acts)
            {
                if (act == null || IsOurs(act)) continue;

                var key = $"{act.Id?.Entry}|{act.GetType().Name}";
                if (key.Contains("heart", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("心脏", StringComparison.Ordinal))
                    return act;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ActLayout] 扫心脏类 act 失败: {ex.Message}");
        }

        return null;
    }

    private static bool IsOurs(ActModel act) => act is Act4Model or Act5Model;
}
