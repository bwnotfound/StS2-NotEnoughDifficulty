using MegaCrit.Sts2.Core.Models;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     「三层 BOSS 清点」诊断 —— 把模组当前对**一层/二层/三层 BOSS 的数量、ID、以及玩家是否攻略过**
///     的判断原样打印出来，便于核对需求口径。
///
/// ## 全部判据（不硬编码任何 boss 名）
/// <list type="number">
///   <item><b>层怎么认</b>：<c>RunProgress.GetActIndexFromModel(act)</c> —— 按
///         <c>ModelDb.ActsByIndex</c> / 本局 <c>Acts</c> / <c>act.Index</c> **数据判定**成 1~N
///         （**不按 act 类型**，否则模组 act 变体会认不出层）；
///         池子则按 <c>ModelDb.ActsByIndex</c> 的**索引分组**（索引 0/1/2 = 一/二/三层），
///         这样整幕类模组塞进来的 act 变体也会自动并入对应层。</item>
///   <item><b>BOSS 池</b>：<c>act.AllBossEncounters</c>（游戏自己的池，含别的模组注册的 boss）。</item>
///   <item><b>是否攻略过</b>：<c>RunState.MapPointHistory</c> —— 类型是
///         <c>List&lt;List&lt;MapPointHistoryEntry&gt;&gt;</c>（**按 act 分组**），
///         每条的 <c>Rooms</c> 里是 <c>MapPointRoomHistoryEntry</c>，
///         取 <c>RoomType ∈ {Monster, Elite, Boss}</c> 的 <c>ModelId.Entry</c> 收进"已打集合"。</item>
/// </list>
///
/// ## 已知的精度边界（重要）
/// <c>MapPointHistory</c> **只记录"已经走过的节点"**。所以：
/// <list type="bullet">
///   <item>"打过" = 走过该节点且有战斗记录 —— 这是准的</item>
///   <item>"看到过但绕开" 拿不到 —— 只能用"池差集"推算（拿不到精确的"见过"计数）</item>
///   <item>玩家中途加入 / 读档：history 里只有已完成的节点，当前正在打的那场还没进去</item>
/// </list>
/// 所以 act4/act5 的"未打池"都是**差集推算**，不是游戏直接告诉我们的。
/// </summary>
internal static class BossInventory
{
    /// <summary>把当前状态打一份完整清单到日志（需开 DebugLogging）。</summary>
    public static void Dump(MegaCrit.Sts2.Core.Runs.RunState? state)
    {
        try
        {
            if (state == null)
            {
                MainFile.Logger.Warn("[BossInventory] state 为空，无法清点");
                return;
            }

            var defeated = RunProgress.GetDefeatedEncounterIds(state);
            var byAct = RunProgress.GetBaseBossesByAct();

            MainFile.Logger.Info(
                $"[BossInventory] 当前层={RunProgress.GetActIndex(state)} " +
                $"| 已打 encounter 总数={defeated.Count}");

            // ── 逐层清点 ──
            for (var layer = 0; layer < byAct.Count; layer++)
            {
                var pool = byAct[layer];
                var lines = new List<string>();
                var beaten = 0;

                foreach (var boss in pool)
                {
                    var id = boss?.Id?.Entry ?? "null";
                    var isBeaten = defeated.Contains(id);
                    if (isBeaten) beaten++;

                    lines.Add($"{(isBeaten ? "已打" : "未打")}:{id}");
                }

                MainFile.Logger.Info(
                    $"[BossInventory] 第 {layer + 1} 层: 池 {pool.Count} 个（已打 {beaten} / 未打 {pool.Count - beaten}）");
                MainFile.Logger.Info($"[BossInventory]   第 {layer + 1} 层明细: {string.Join(" | ", lines)}");
            }

            // ── 各层"当前实际分配的那个 boss"（BossEncounter 槽位）──
            for (var i = 0; i < state.Acts.Count; i++)
            {
                var act = state.Acts[i];
                if (act == null) continue;

                var first = act.BossEncounter?.Id?.Entry ?? "null";
                var second = act.HasSecondBoss ? (act.SecondBossEncounter?.Id?.Entry ?? "null") : "(无)";
                MainFile.Logger.Info(
                    $"[BossInventory] act 索引 {i}（{act.GetType().Name}）: " +
                    $"BossEncounter='{first}' SecondBoss='{second}'");
            }

            // ── 差集结果：act4 顶端 boss 池 / act5 可用池 ──
            var act4Boss = state.Acts.OfType<Act4Model>().FirstOrDefault()?.BossEncounter?.Id?.Entry;
            var unvisited = RunProgress.GetUnvisitedBosses(state, excludeAct4Boss: true);

            MainFile.Logger.Info(
                $"[BossInventory] act4 顶端 boss='{act4Boss ?? "null"}' " +
                $"| act5 可用未打池 {unvisited.Count} 个: " +
                string.Join(", ", unvisited.Take(12).Select(b => b.Id.Entry)));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[BossInventory] 清点失败: {ex}");
        }
    }
}
