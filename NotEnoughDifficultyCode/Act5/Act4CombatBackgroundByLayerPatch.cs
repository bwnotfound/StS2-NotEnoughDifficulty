using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4：把**战斗背景切到该敌人自己所属的 act**。
///
/// ## 机制（走 BaseLib 官方钩子，不是反射）
/// <list type="number">
///   <item><c>NCombatRoom.SetUpBackground</c> 的 **prefix** 把"本场来源 act"写进
///         <see cref="Act4FightSource" />（同一次调用链，时序天然正确）；</item>
///   <item><c>Act4Model.CustomGenerateBackgroundAssets</c> 读它，返回
///         <c>该 act.GenerateBackgroundAssets(rng)</c> ⇒ 背景就是那个 act 的。</item>
/// </list>
///
/// ## 走过的两条弯路（别再回去）
/// <b>① 换 <c>EncounterModel.CreateBackground</c> 的入参 <c>parentAct</c></b>：日志打出了"背景切到 Overgrowth"，
/// 画面不变 —— 精英/首领 encounter 走的是**自定义背景分支**（<c>HasCustomBackground</c>），根本不看这个入参。
/// <b>② 在 <c>SetUpBackground</c> 的 postfix 里反射换掉 <c>NCombatRoom.Background</c></b>：
/// 日志证明换了、在树里、7 个图层、尺寸也继承了 —— **画面还是不变**（原版在更晚的地方又按 <c>_visuals.Act</c> 生成了一次）。
/// </summary>
/// <summary>
///     当前这一场战斗的**来源 act**（null = 未知/不切）。
///     由 <c>NCombatRoom.SetUpBackground</c> 的 prefix 写入，供
///     <c>Act4Model.CustomGenerateBackgroundAssets</c> 读取 —— 二者在同一次调用链里，时序天然正确。
///
/// ## 为什么存 act 而不是"层号"
/// 用户："<i>为什么 ACT 1 的 2 个子变种层级一样的颜色？</i>"
/// base game 的**同一层可以有多个 act**（层 1 = Overgrowth 和 Underdocks）。
/// 之前存层号、再用"层里第一个 act"当主题 ⇒ 两个子变体看起来完全一样。
/// 现在存**这个敌人自己所属的 act**（<see cref="RunProgress.FindActOfEncounter" /> 反查），
/// 背景与地图染色都跟着它走 ⇒ 每个变体各有各的样。
/// </summary>
internal static class Act4FightSource
{
    /// <summary>本场敌人的来源 act（上一次解析结果，仅作兜底）。</summary>
    internal static ActModel? Act;

    /// <summary>该 act 属于第几层（1/2/3；0 = 未知）。仅用于日志与提示。</summary>
    internal static int Layer;

    /// <summary>
    ///     这一场是不是**精英战**（用户口径："检测到处于 ACT 5/4 场景且为精英的时候才会走拦截，否则就放过去"）。
    ///
    /// ⚠️ 为什么必须区分：背景折返会把 `parentAct` 换成"敌人自己的幕"，
    ///    对精英是本意（精英就该带来源幕的场景），但**对本幕自己的 BOSS 是灾难** ——
    ///    BOSS 场景会被来源幕顶掉（用户实测："ACT 5/6 的 BOSS 场景没了"）。
    ///    所以只有精英才走拦截，BOSS / 伪装BOSS（小怪房）/ 其它一律放过去。
    /// </summary>
    internal static bool IsEliteFight()
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();

            if (state?.CurrentRoom is CombatRoom room && room.Encounter != null)
                return room.Encounter.RoomType == RoomType.Elite;

            // 兜底：房间信息还没就绪时看地图点类型
            return state?.CurrentMapPoint?.PointType == MapPointType.Elite;
        }
        catch
        {
            return false;      // 判不出来就不拦截，宁可少切也不能顶掉 BOSS 场景
        }
    }

    /// <summary>
    ///     解析"这一场是谁的背景"，**以当前房间为准**。
    ///
    /// ## 为什么不能靠 <c>SetUpBackground</c> 的 prefix 写入（实测踩过：慢一场）
    /// 日志实证（背景总是取到上一场）：
    /// <code>
    ///   [Act4] 战斗房 (3,1) → 指定精英 'BYRDONIS_ELITE'
    ///   [Act4] 战斗背景资产取 'Overgrowth'      ← 生成发生在这里，早于 prefix
    ///   [Act4] 本场来源 = 'BYRDONIS_ELITE' → Overgrowth
    /// </code>
    /// 原因是**预载**也会生成背景资产：<c>EncounterModel.GetAssetPaths</c> → <c>GetBackgroundAssets</c>
    /// （与 <c>NCombatRoom.SetUpBackground</c> 同一个钩子），而预载发生在房间创建之后、
    /// <c>SetUpBackground</c> 之前 ⇒ prefix 写的值永远是上一场的。
    ///
    /// 正解：问 <c>RunState.CurrentRoom</c>（IL：<c>= _currentRooms.LastOrDefault()</c>，
    /// <c>RunManager.CreateRoom</c> 一造好房间就入栈）⇒ 任何生成时机拿到的都是**本场**。
    /// </summary>
    internal static ActModel? Current()
    {
        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.CurrentRoom is CombatRoom room)
            {
                var act = RunProgress.FindActOfEncounter(room.Encounter?.Id?.Entry, out var layer);
                // 解析结果**绝不能是本模组自己的幕**（聚合池会把任何敌人搜成"我们的"），
                // 否则下游 source.GenerateBackgroundAssets ↔ 本 hook 会自我递归（实测刷屏崩溃）。
                if (act != null && act is not (Act4Model or Act5Model) && !ReferenceEquals(act, state.Act))
                {
                    Act = act;
                    Layer = layer;
                    return act;
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Act4] 解析本场来源 act 失败: {ex.Message}");
        }

        return Act;      // 兜底：房间信息拿不到时用上一次解析结果
    }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.SetUpBackground))]
internal static class Act4SetFightLayerPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCombatRoom __instance, IRunState state)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(Act4SetFightLayerPatch), () =>
        {
            if (state?.Act is not Act4Model) return;

            var id = Traverse.Create(__instance).Field("_visuals").GetValue<ICombatRoomVisuals>()?.Encounter?.Id?.Entry;
            var sourceAct = Act4FightSource.Current();     // 以当前房间为准（见 Current 的注释）
            if (sourceAct == null) return;

            MainFile.DebugLog(
                $"[Act4] 本场来源 = '{id}' → act 层 {Act4FightSource.Layer} / '{sourceAct.GetType().Name}'" +
                $"（旅行色 {Hex(sourceAct.MapTraveledColor)} / 招牌色相 {ActVisualTheme.SignatureHue(sourceAct):0}°）");
        });
    }

    private static string Hex(Color c) =>
        $"{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";
}

