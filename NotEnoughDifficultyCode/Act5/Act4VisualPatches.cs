using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Random;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4 的地图染色（用户口径：<i>"遭遇不同层级敌人时，会根据其层级文字颜色而变色，
///     直到战斗结束回到紫色"</i> + <i>"踩入问号、商店、宝箱、火堆变回紫色"</i>）
///     与地图节点图标改色。
///
/// ## ① 染色源 = 敌人**自己所属的 act**（不是"第几层"）
/// <i>"为什么 ACT 1 的 2 个子变种层级一样的颜色？"</i>
/// —— base game 同一层可以有多个 act（层 1 = Overgrowth / Underdocks，各 3 个精英）。
/// 精确到 act 见 <see cref="RunProgress.FindActOfEncounter" />。
/// 战斗背景的切换不在这里（走 BaseLib 钩子，见 <see cref="Act4FightSource" />）。
///
/// ## ② 地图节点图标的黄色 → 淡紫
/// <i>"问号，商店，火堆，精英，BOSS都要把底图黄色改成淡紫"</i>
/// 地图节点的图标是 <c>NNormalMapPoint</c> 用图集里的 <c>map_*</c> 贴图渲染的（黄色底），
/// 在 <c>NMapScreen.SetMap</c> postfix 里**延迟一帧**（节点贴图是在各自 <c>_Ready</c> 里挂的）
/// 遍历所有节点，把黄色像素转成淡紫。
/// </summary>
internal static class Act4EliteBackgroundByLayerPatch
{

    /// <summary>
    ///     按"当前房间"决定地图该染什么色（用户口径）：
    ///     <i>"踩入问号、商店、宝箱、火堆变回紫色，其他时候保持变色颜色直到其变色"</i>
    ///     ⇒ 战斗房（精英/小怪/BOSS）按其**来源 act** 染色；非战斗房（?/商店/宝箱/火堆）回到紫色。
    ///
    /// ⚠️ 染色源是**敌人自己所属的 act**，不是"它属于第几层"：
    /// 用户："<i>为什么 ACT 1 的 2 个子变种层级一样的颜色？</i>"
    /// —— 层 1 同时有 Overgrowth 和 Underdocks（各有 3 个精英），
    /// 用"层代表 act"会让两个变体同色。见 <see cref="RunProgress.FindActOfEncounter" />。
    /// </summary>
    internal static void ApplyTintForCurrentRoom(RunState? state)
    {
        try
        {
            var room = state?.CurrentRoom;

            if (room is CombatRoom combat)
            {
                var id = combat.Encounter?.Id?.Entry;
                var sourceAct = RunProgress.FindActOfEncounter(id, out var layer);
                if (sourceAct != null)
                {
                    // 色相取"该 act 自己的招牌色"（从它的地图美术统计，见 ActVisualTheme.SignatureHue）——
                    // 不用 MapTraveledColor：那三个是近黑 UI 色，暗港(266°)与默认紫(278°)肉眼看不出差别。
                    var hue = ActVisualTheme.SignatureHue(sourceAct);
                    Act4MapStripeTint.SetLayer(layer, hue);

                    MainFile.DebugLog(
                        $"[Act4] 地图染色 ← '{id}' 属于 {sourceAct.GetType().Name}（层 {layer}）色相 {hue:0}°");

                    return;
                }
            }

            // 非战斗房（问号/商店/宝箱/火堆）或认不出 act → 回紫
            Act4MapStripeTint.SetLayer(0, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Act4] 判定地图染色失败: {ex.Message}");
        }
    }

    private static string Hex(Color c) =>
        $"{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";

}

/// <summary>act4 地图节点图标：黄色 → 淡紫（延迟一帧，等节点自己把贴图挂上）。</summary>
internal static class Act4MapIconRecolor
{
    internal static void Schedule(NMapScreen screen)
    {
        Callable.From(() =>
        {
            try
            {
                var pointDict = Traverse.Create(screen)
                    .Field("_mapPointDictionary")
                    .GetValue<Dictionary<MegaCrit.Sts2.Core.Map.MapCoord, NMapPoint>>();
                if (pointDict == null) return;

                var count = 0;
                foreach (var node in pointDict.Values)
                {
                    if (node == null) continue;
                    count += ActVisualTheme.RecolorTreeYellowToLavender(node);
                }

                MainFile.DebugLog($"[Act4] 地图节点图标黄→淡紫：改了 {count} 张贴图");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Act4] 节点图标改色失败: {ex.Message}");
            }
        }).CallDeferred();
    }
}
