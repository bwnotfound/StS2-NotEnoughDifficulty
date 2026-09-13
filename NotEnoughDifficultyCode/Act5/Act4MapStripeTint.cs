using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4 地图**边缘纹路（条纹）**按层变色。
///
/// ## 关键事实（IL 实证 → 决定了实现方式）
/// 地图三段底图的**唯一**贴图入口是 <c>NMapBg.OnVisibilityChanged()</c>：
/// <code>
/// _mapTop.Texture = _runState.Act.MapTopBg;   // 地图每次可见性变化都会重新取一遍
/// _mapMid.Texture = _runState.Act.MapMidBg;
/// _mapBot.Texture = _runState.Act.MapBotBg;
/// </code>
/// ⇒ 想改这三张贴图，**只能让 act 的 getter 返回别的图**。两条弯路（都实测死掉）：
/// <list type="bullet">
///   <item>直接 <c>rect.Texture = 变色图</c> —— 日志"替换 3 张贴图"是真的，画面纹丝不动，
///         因为随后 <c>OnVisibilityChanged</c> 又按 act 取了原图盖回来；</item>
///   <item>改 <c>rect.Material</c> 的 shader 参数 —— 实测这三个 rect 的 <c>Material</c> **全是 null**
///         （日志 <c>材质=无</c>），根本没有参数可改。</item>
/// </list>
///
/// ## 现在的做法（单一数据源）
/// <list type="number">
///   <item><see cref="SetLayer"/> 只更新**状态**（当前层 + 目标色相）；</item>
///   <item><c>ActVisualTheme.ForAct4</c> 生成底图时读该状态 ⇒ 缓存键含色相，换层即换图；</item>
///   <item>再调一次原版的 <c>OnVisibilityChanged</c> 让它重新取图 ⇒ 当帧换色。</item>
/// </list>
/// **色相**取自该层代表 act 的 <c>MapTraveledColor</c>（不写死绿/青/金，整幕模组的 act 变体也算数），
/// 明暗/饱和度沿用底图像素本身 ⇒ 只染纹路的色，不改纹路的形状。
/// </summary>
internal static class Act4MapStripeTint
{
    /// <summary>荣耀底图里那圈纹路的原始色相区间（青蓝）—— 只染这个区间，其余像素不动。</summary>
    internal const float VeinHueFrom = 150f;
    internal const float VeinHueTo = 265f;

    /// <summary>默认目标色相 = 紫（act4 的纹路色）。</summary>
    internal const float PurpleHue = 278f;

    /// <summary>当前染色层（0 = 回紫）。-1 = 还没定过。</summary>
    private static int _layer = -1;

    /// <summary>当前目标色相（层色相；回紫时 = <see cref="PurpleHue"/>）。</summary>
    private static float _hue = PurpleHue;

    /// <summary>act4 底图生成时读的**目标色相**。</summary>
    internal static float Hue => _layer is >= 1 and <= 3 ? _hue : PurpleHue;

    /// <summary>进 <c>ActVisualTheme</c> 缓存键的片段 ⇒ 每个色相各生成一张底图。</summary>
    internal static string CacheKey => $"h{(int)Mathf.Round(Hue)}";

    /// <summary>新的一局：回到紫。</summary>
    internal static void Reset()
    {
        _layer = -1;
        _hue = PurpleHue;
    }

    /// <summary>按层染色（layer 0 或 1~3 之外 = 回紫）。色相由调用方给（= 该 act 的招牌色相）。</summary>
    internal static void SetLayer(int layer, float? layerHue)
    {
        var hue = layer is >= 1 and <= 3 && layerHue.HasValue
            ? Mathf.PosMod(layerHue.Value, 360f)
            : PurpleHue;

        // 状态没变 ⇒ 底图本来就是这个色，不用重挂
        if (layer == _layer && Mathf.IsEqualApprox(hue, _hue)) return;

        _layer = layer;
        _hue = hue;
        Refresh();
    }

    /// <summary>让 <c>NMapBg</c> 重新从 act 取三张贴图（= 原版唯一的贴图入口）。</summary>
    private static void Refresh()
    {
        try
        {
            var screen = NMapScreen.Instance;
            if (screen == null) return;

            var bg = Traverse.Create(screen).Field("_mapBgContainer").GetValue<NMapBg>();
            if (bg == null)
            {
                MainFile.DebugLog("[Stripe] 取不到 _mapBgContainer，跳过纹路染色");
                return;
            }

            // 私有方法，无参 ⇒ Traverse 直接调
            Traverse.Create(bg).Method("OnVisibilityChanged").GetValue();

            MainFile.DebugLog(
                $"[Stripe] 地图纹路 → {(Environment(_layer))}，色相 {Hue:0}°（重挂 3 张底图）");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Stripe] 纹路重挂失败: {ex.Message}");
        }
    }

    private static string Environment(int layer) =>
        layer is >= 1 and <= 3 ? $"层 {layer}" : "紫";
}
