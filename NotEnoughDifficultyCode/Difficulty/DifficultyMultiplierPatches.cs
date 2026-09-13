using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     Encounter → 源 act 反查。
///     ## 缓存策略：run-scoped
///     旧实现是<b>永久缓存</b>——首次构建后永不重建。问题在于其他 mod 可能在 run 启动后才注册
///     自己的 encounter 到 ModelDb（比如 BaseLib 的 lazy registration），这些 encounter 没进
///     我们的缓存，永远查不到源 act。
///     新实现：缓存在每次 run 开始时（RunManager.GenerateRooms 的 prefix）失效。这保证：
///     - 缓存在 run 内不重建——查询是 hot path（每个 ModifyDamage / CreateCreature 调用）
///     - 不同 run 之间允许 ModelDb 变化（例如重启游戏前装了新 mod）
///     缓存失效由 <see cref="InvalidateSourceActResolverCachePatch" /> 触发。
/// </summary>
internal static class SourceActResolver
{
    private static Dictionary<string, int>? _cache;

    /// <summary>Run 开始时调用，清空缓存以便下次 GetSourceActIndex 重建。</summary>
    public static void Invalidate()
    {
        _cache = null;
    }

    public static int? GetSourceActIndex(EncounterModel? encounter)
    {
        if (encounter == null) return null;

        try
        {
            var entry = encounter.Id?.Entry;
            if (entry == null) return null;

            var cache = GetOrBuildCache();
            return cache.TryGetValue(entry, out var idx) ? idx : null;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"SourceActResolver.GetSourceActIndex failed: {ex}");
            return null;
        }
    }

    private static Dictionary<string, int> GetOrBuildCache()
    {
        if (_cache != null) return _cache;

        var c = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            // 按层聚合（走 ActsByIndex，含模组加的 act 变体）——不再按具体 act 类型取
            for (var layer = 1; layer <= 3; layer++)
                foreach (var e in RunProgress.CollectFromLayer(layer, a => a.AllEncounters))
                {
                    var entry = e?.Id?.Entry;
                    if (entry != null) c.TryAdd(entry, layer);
                }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"SourceActResolver cache build failed: {ex}");
            // 即使 build 失败也缓存空 dict，避免每次查询都重试
        }

        _cache = c;
        return c;
    }

    private static void AddAct(Dictionary<string, int> cache, ActModel? act, int idx)
    {
        if (act == null) return;
        foreach (var e in act.AllEncounters)
        {
            var entry = e?.Id?.Entry;
            if (entry != null) cache.TryAdd(entry, idx);
        }
    }
}

/// <summary>
///     Run 开始时清空 SourceActResolver 缓存。
///     patch 在 BaseLib 的 GenerateRooms patch <b>之前</b>跑（Priority.High），确保缓存先失效，
///     让任何后续依赖 SourceActResolver 的 patch 拿到最新结果。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
public static class InvalidateSourceActResolverCachePatch
{
    [HarmonyPriority(Priority.High)]
    [HarmonyPrefix]
    public static void InvalidateCache()
    {
        PatchScope.Run(nameof(InvalidateSourceActResolverCachePatch), SourceActResolver.Invalidate);
    }
}

/// <summary>
///     数值强化公共逻辑（第二轮改造后）。
///
/// ## 新公式（需求5）
/// <code>
///   血量倍率 = 1 + ActFloor × 0.1  × Y / 100
///   攻击倍率 = 1 + ActFloor × 0.05 × X / 100
/// </code>
/// 该层是否启用由 <c>Act{N}_ExtraScaling</c> 决定（1~3 层默认关、4~5 层默认开）。
/// 未启用的层返回 (1.0, 1.0)，等于完全不改原值。
///
/// ## 与旧实现的关系
/// 旧的「全局倍率(层内线性插值) × 来源 act 倍率 × 总倍率」三层链、boss/普通敌人分开的旋钮、
/// 以及 <see cref="SourceActResolver" /> 的源 act 反查<b>全部作废</b>——新公式只依赖爬塔层数，
/// 与"这个怪原本属于哪个 act"无关。
/// </summary>
internal static class DifficultyMultiplierContext
{
    public static (double hp, double dmg) GetCurrentMultipliers(IRunState state, EncounterModel? encounter)
    {
        // RunProgress 需要具体 RunState（要读 MapPointHistory / Acts）。
        // IRunState 也可能是 NullRunState，as 失败即返回 1.0（不影响游戏）。
        var real = state as RunState;
        if (real == null) return (1.0, 1.0);

        var hp = RunProgress.GetHpMultiplier(real);
        var dmg = RunProgress.GetDmgMultiplier(real);
        return (hp, dmg);
    }
}

/// <summary>
///     怪物 spawn 时 HP 倍率：patch <see cref="CombatManager.SetUpCombat" /> 的 postfix。
///     ## 为什么 patch SetUpCombat 而不是 CombatState.CreateCreature postfix（v0.4.0 旧实现）
///     旧 patch 在 <c>CombatState.CreateCreature</c> postfix 跑：每个 creature spawn 完<b>立即</b>
///     把它 MaxHp ×m。但 base game 在 <c>CreateCreature</c> 内调 <c>SetUniqueMonsterHpValue</c>
///     给每个怪物分配 <b>[MinInitialHp, MaxInitialHp]</b> 范围内的<b>唯一</b>整数 MaxHp。这个
///     检查依赖"已 spawn 的同队怪物当前 MaxHp"——而我们的 patch 已经把那些 MaxHp 改成了
///     200+ 数量级，跟新怪物想从 [40,46] 抽的范围完全没交集，<b>uniqueness 检查失效</b>。
///     失效结果：多个节段抽到相同 base value（如三个都 43）→ spawn-time ×m 后也相同
///     （215, 215, 215）→ 千足虫 <c>AfterAddedToRoom</c> 内 dedup 逻辑发现重复 → maxHp 在
///     while 循环里被重置到 <c>ScaleHpForMultiplayer(MinInitialHp, ..., count, actIdx)</c>（单人
///     直接等于 MinInitialHp=40）→ <c>SetMaxAndCurrentHp(creature, 40)</c> → 我们 runtime patch
///     又 ×m → 节段最终 MaxHp = 40×5 = <b>200</b>（截图中间节段症状）。
///     修复：推迟到 <c>CombatManager.SetUpCombat</c> postfix——所有 creature 已经 spawn 完，
///     <c>SetUniqueMonsterHpValue</c> 已经基于原始 [40,46] 值跑完，uniqueness 保持。然后我们
///     一次性遍历 enemies 批量加倍。
///     ## SetUpCombat 的位置
///     看 <c>CombatRoom.cs</c>: foreach CreateCreature → AddCreature → <c>SetUpCombat(state)</c>
///     → <c>AfterCombatRoomLoaded</c>（触发 <c>AfterCreatureAdded</c> 循环，含 <c>AfterAddedToRoom</c>）。
///     我们 patch SetUpCombat postfix → 在 AfterAddedToRoom 之前完成所有缩放 → AfterAddedToRoom
///     内调 SetMaxAndCurrentHp 时其他 creature MaxHp 已经一致地 ×m 过，dedup 基于一致数据。
///     ## 早返优化
///     整个 act 不是 4/5 时立即早返不遍历，零开销。
/// </summary>
[HarmonyPatch(typeof(CombatManager),
    nameof(CombatManager.SetUpCombat))]
public static class MonsterHpMultiplierPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void ApplyHpMultiplierToAllEnemies(CombatState state)
    {
        if (!PatchScope.IsEnabled) return;
        if (state == null) return;

        PatchScope.Run(nameof(MonsterHpMultiplierPatch), () =>
        {
            // [第二轮改造] 不再限制 act4/5——强化改为按层开关控制（1~3 层默认关闭）。
            // 未启用的层 GetHpMultiplier 返回 1.0，下面立即早返。
            var runState = state.RunState;
            if (runState == null) return;

            // 注意：DifficultyMultiplierContext 在每个 creature 上算出来的 mult 是相同的
            // （取决于 state.Act / state.Map / state.CurrentMapCoord / encounter，跟 creature 个体无关）。
            // 算一次即可，然后给所有 enemy 应用。
            var (hpMult, _) = DifficultyMultiplierContext.GetCurrentMultipliers(
                runState, state.Encounter);

            if (Math.Abs(hpMult - 1.0) < 1e-6) return;

            var mult = (decimal)hpMult;

            foreach (var creature in state.Creatures)
            {
                if (creature == null) continue;
                if (creature.Side != CombatSide.Enemy) continue;
                if (creature.Monster == null) continue;
                if (creature.HpDisplay.IsInfinite()) continue; // 无敌阶段不动

                var scaled = creature.MaxHp * mult;
                if (scaled > MonsterRuntimeHpHelper.HpAmountCeiling) scaled = MonsterRuntimeHpHelper.HpAmountCeiling;
                if (scaled < 1m) scaled = 1m;

                creature.SetMaxHpInternal(scaled);
                creature.SetCurrentHpInternal(scaled);
            }
        });
    }
}

/// <summary>
///     怪物伤害倍率：patch Hook.ModifyDamage 的 postfix。
///     战斗实际伤害和 AttackIntent 显示都走这条线，一个 patch 同时搞定两个场景。
///     <b>这是 hot path</b>——每次玩家计算攻击伤害预览、敌人意图显示、敌人实际攻击都触发。
///     早返检查按"最常 false" 排序：
///     1. PatchScope.IsEnabled 检查（极快——读静态字段）
///     2. dealer 是不是 enemy monster（早返掉所有玩家攻击的调用，绝大多数）
///     3. 倍率是否 ≈1（act1-3 总是 true，直接早返不读 RunState）
///     4. 业务计算
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
public static class MonsterDamageMultiplierPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void ApplyDamageMultiplier(
        IRunState runState,
        ICombatState? combatState,
        Creature? dealer,
        ref decimal __result)
    {
        if (!PatchScope.IsEnabled) return;

        // dealer 检查放最前面：玩家攻击的调用占绝大多数，全部走这里早返
        if (dealer?.Monster == null) return;
        // 召唤物 / 玩家 pet（如 Osty）有 Monster 字段但属于 Player side。
        // 它们的攻击不应被敌人伤害倍率放大；只放大真正的敌人攻击。
        if (dealer.Side != CombatSide.Enemy) return;

        // [第二轮改造] 不再限制 act4/5——由每层的 ExtraScaling 开关决定是否生效。
        // 未启用的层返回 1.0，下面早返（对 hot path 的开销就是一次 RunState 判空 + 一次层开关读取）。
        try
        {
            var (_, dmgMult) = DifficultyMultiplierContext.GetCurrentMultipliers(
                runState, combatState?.Encounter);

            if (Math.Abs(dmgMult - 1.0) < 1e-6) return;

            __result *= (decimal)dmgMult;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"MonsterDamageMultiplierPatch failed: {ex}");
        }
    }
}

/// <summary>
///     战斗中召唤怪物的 HP 倍率：patch <see cref="MegaCrit.Sts2.Core.Commands.CreatureCmd" />.Add(Creature)
///     的 prefix。
///     ## 为什么需要这个 patch
///     战斗开始时的怪物通过 <see cref="MegaCrit.Sts2.Core.Combat.CombatManager.SetUpCombat" /> postfix
///     （<see cref="MonsterHpMultiplierPatch" />）批量加倍。
///     但战斗<b>过程中</b>召唤的怪物（如 SicEmPower、MinionPower、SummonNextTurnPower 等触发的）走
///     另一条路径：<c>CreatureCmd.Add&lt;T&gt;</c> → 内部调 <c>CombatState.CreateCreature</c>（spawn）→
///     调 <c>CreatureCmd.Add(Creature)</c>（底层入口）→ 调 <c>AfterCreatureAdded</c>。
///     这条路径<b>不经过</b> SetUpCombat，所以战斗中召唤的怪物 HP <b>不会被加倍</b>。
///     ## Hook 时机
///     选择 <c>CreatureCmd.Add(Creature)</c> 的 prefix：
///     - 这是<b>战斗中召唤</b>的明确入口——base game 实现里有 <c>if (!IsInProgress) throw</c>
///     保证只在战斗中调用
///     - CreateCreature 已经完成（SetUniqueMonsterHpValue 已经基于原始 base 范围跑完——战斗中
///     召唤通常一个怪物，uniqueness 失败也不会触发问题）
///     - AfterCreatureAdded（含 AfterAddedToRoom）还没跑——如果新怪物在 AfterAddedToRoom 内调
///     SetMaxAndCurrentHp，会被 <c>CreatureAfterAddedToRoomSuppressPatch</c> 嵌套抑制，
///     不会双重缩放
///     用 prefix 而不是 postfix：prefix 在 stub 入口同步触发，加倍发生在 AfterCreatureAdded 之前。
///     这样跟战斗开始时的语义一致（spawn 完立即加倍 → 然后 AfterAddedToRoom）。
///     ## ShowsInfiniteHp 早返
///     召唤出来如果立即调 SetMaxAndCurrentHp(999...) 进入无敌阶段（Doormaker/WaterfallGiant），
///     那 spawn-time MaxHp 不该被我们加倍——保留 999... 让 SetMaxAndCurrentHp prefix 自己处理
///     （ShowsInfiniteHp=true 早返）。
///     但 ShowsInfiniteHp 在 spawn 时通常是 false（设 true 是在 AfterAddedToRoom 内）。所以
///     prefix 跑时一般 ShowsInfiniteHp=false，正常加倍。
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Add), typeof(Creature))]
public static class CreatureAddSummonHpMultiplierPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPrefix]
    public static void Prefix(Creature creature)
    {
        if (!PatchScope.IsEnabled) return;
        if (creature == null) return;
        if (creature.Side != CombatSide.Enemy) return;
        if (creature.Monster == null) return;
        if (creature.HpDisplay.IsInfinite()) return; // 无敌阶段 - 让原方法保持

        PatchScope.Run(nameof(CreatureAddSummonHpMultiplierPatch), () =>
        {
            var combatState = creature.CombatState;
            if (combatState?.RunState == null) return;

            // [第二轮改造] 不再限制 act4/5——由每层的 ExtraScaling 开关决定是否生效。
            var (hpMult, _) = DifficultyMultiplierContext.GetCurrentMultipliers(
                combatState.RunState, combatState.Encounter);

            if (Math.Abs(hpMult - 1.0) < 1e-6) return;

            var scaled = creature.MaxHp * (decimal)hpMult;
            if (scaled > MonsterRuntimeHpHelper.HpAmountCeiling) scaled = MonsterRuntimeHpHelper.HpAmountCeiling;
            if (scaled < 1m) scaled = 1m;

            // 直接调 internal method，绕过 CreatureCmd.SetMaxHp 避免触发我们自己的 prefix（双重 scale）
            creature.SetMaxHpInternal(scaled);
            creature.SetCurrentHpInternal(scaled);
        });
    }
}