using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT5 的**收尾闸门**（用户口径：
///     <i>"直接给判定呗,带着某个标记的BOSS房击败才能进入终局,否则无法进入建筑师剧情呗?"</i>）。
///
/// ## 为什么需要
/// 原版 <c>NRewardsScreen.OnProceedButtonPressed</c> 的 IL：
/// <code>
///   if (_isTerminal &amp;&amp; (CurrentRoom.RoomType == Boss || CurrentRoom.IsVictoryRoom)) {
///       if (map.SecondBossMapPoint != null &amp;&amp; CurrentMapCoord == map.BossMapPoint.coord)
///            ProceedFromTerminalRewardsScreen();                    // ← 双BOSS"中间那一步"：关奖励界面 + 开地图
///       else { _proceedButton.Disable(); ActChangeSynchronizer.SetLocalPlayerReady(); }   // ← 结束本幕
///   }
/// </code>
/// <c>SetLocalPlayerReady</c> → <c>VoteToMoveToNextActAction</c> → <c>MoveToNextAct</c>
/// → <c>RunManager.EnterNextAct</c>；act5 已经是最后一层 ⇒ **直接进结局（建筑师）**。
/// 实测日志：
/// <code>
///   has won against encounter ENCOUNTER.SOUL_FYSH_BOSS
///   [BaseLib] Checking for additional interactions with THE_ARCHITECT
///   Creating NCombatRoom with mode=VisualOnly encounter=THE_ARCHITECT_EVENT_ENCOUNTER
/// </code>
///
/// ## 判定
/// 只有**真最终 BOSS 房**（当前坐标 == 地图 <c>BossMapPoint</c>）才交回原版收尾；
/// 两个伪装 BOSS（灵魂异鱼 / 知识恶魔）打完一律走"关掉奖励界面、回地图"那条路。
///
/// ## ⚠️ 别想着"把 _isTerminal 压掉，让原版走非终局分支"（试过，不行）
/// 那条分支是 <c>SkipLocalRewardsSet() + NOverlayStack.Remove(this)</c>，
/// 而这个**终局**奖励界面根本不在同步器栈里，于是：
/// <code>
///   InvalidOperationException: Tried to skip reward set for player 1,
///   but they are not currently viewing any reward set!
/// </code>
/// 界面关不掉 ⇒ 表现成"前进键按了没反应"。
/// 正解就是复用原版自己那条 <c>ProceedFromTerminalRewardsScreen()</c>（双BOSS中间步用它）。
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
internal static class Act5DisguisedNoEndingPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
    {
        if (!PatchScope.IsEnabled) return true;

        var ours = PatchScope.Run(nameof(Act5DisguisedNoEndingPatch), IsDisguisedAct5Room, false);
        if (!ours) return true;   // 不是伪装BOSS（或就是真最终BOSS）→ 原版照旧

        PatchScope.Run(nameof(Act5DisguisedNoEndingPatch), () =>
        {
            MainFile.DebugLog("[Act5] 伪装BOSS 奖励界面「继续」→ 不结束本幕：按原版双BOSS中间那步关界面回地图");
            TaskHelper.RunSafely(RunManager.Instance.ProceedFromTerminalRewardsScreen());
        });

        return false;   // 跳过原版（否则它会 SetLocalPlayerReady → 进结局）
    }

    /// <summary>
    ///     当前这场是不是"act5 的伪装BOSS 房、且不是真最终BOSS 点"。
    ///
    /// ⚠️ **判据是坐标，不是"名字在不在伪装名单里"**（用户实测："打一个BOSS直接进建筑师"）：
    /// 名单版判据只要在某一刻返回 false（例如那个 BOSS 恰好又是本幕 boss 槽的同一个 encounter），
    /// 这一场就走原版终局分支 → <c>SetLocalPlayerReady</c>（IL 实证：**全游戏只有这一个调用点**）
    /// → 本幕结束 → 最后一幕 → 建筑师。
    /// 现在的口径：**只要不在最终 BOSS 坐标，就不允许结束本幕**。
    /// </summary>
    private static bool IsDisguisedAct5Room()
    {
        var state = RunStateAccessor.GetCurrentState();
        if (state?.Act is not Act5Model) return false;
        if (state.CurrentRoom is not CombatRoom) return false;

        // 真最终 BOSS（地图 BossMapPoint 的坐标）→ 交回原版收尾
        return !Act5BossDisplay.IsAtFinalBossNode(state);
    }
}
