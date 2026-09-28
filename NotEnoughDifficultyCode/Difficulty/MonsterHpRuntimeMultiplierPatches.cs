using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     运行时 HP 倍率 patch——补充 spawn-time 的 <see cref="MonsterHpMultiplierPatch" />，处理战斗中怪物
///     MaxHp 被改变的场景：TestSubject 复活、ToughEgg 孵化、千足虫复活（Heal）等。
///     我们 patch CreatureCmd 三个高层 API 的 prefix，把 amount 加倍并 clamp 到 999_999_999。
///     ## 防御 1：Clamp 到 999_999_999
///     SetMaxHpInternal 内部用 <c>(int)amount</c> cast，amount 大于 int.MaxValue 时 OverflowException。
///     Doormaker / WaterfallGiant 等 boss 的"无敌阶段"会传 999_999_999m，乘以 mult 后远超 int.MaxValue
///     → crash。Clamp 到上限杜绝溢出。
///     ## 防御 2：ShowsInfiniteHp 检查（避免 reset 时双重加倍）
///     Doormaker / WaterfallGiant 模式：
///     1) AfterAddedToRoom: OriginalHp = MaxHp（已是 spawn-time 加倍后的值），
///     SetMaxAndCurrentHp(999_999_999m), ShowsInfiniteHp=true
///     2) DramaticOpen / 类似阶段切换: SetMaxAndCurrentHp(OriginalHp)（仍 ShowsInfiniteHp=true）,
///     之后 ShowsInfiniteHp=false
///     步骤 2 传的 OriginalHp 已经是加倍值，再加倍就是双重缩放。
///     ShowsInfiniteHp=true 期间统一跳过加倍。
///     ## 防御 3 (v0.4.1)：SetMaxAndCurrentHp 内层 SetMaxHp 嵌套抑制
///     <c>SetMaxAndCurrentHp(amount)</c> 内部实现是 <c>SetMaxHp(amount); SetCurrentHp(amount);</c>。
///     我们 prefix 把 amount ×m，原方法又调 SetMaxHp 触发 SetMaxHp prefix 再 ×m，
///     让 MaxHp = base × m²、CurrentHp = base × m，比例 1:m 显示成"满血但 MaxHp 巨大"。
///     修复：thread-local 计数器，<see cref="CreatureCmdSetMaxAndCurrentHpPatch" /> prefix +1, postfix -1，
///     内层 SetMaxHp 看到 &gt; 0 就跳过缩放。
///     ## 防御 4 (v0.4.2)：AfterAddedToRoom 整段嵌套抑制 ★ 新增
///     千足虫节段的 <c>AfterAddedToRoom</c>（base game <c>DecimillipedeSegment.cs:119-143</c>）
///     自己会做 spawn 后的 HP 微调：
///     <code>
///   decimal maxHp = base.Creature.MaxHp;  // 已是 spawn-time ×m 后值
///   if (maxHp % 2m == 1m) maxHp++;
///   // ... dedup with other segments
///   await CreatureCmd.SetMaxAndCurrentHp(base.Creature, maxHp);
/// </code>
///     我们 spawn-time 已经把 MaxHp 设为 <c>base × m</c>。AfterAddedToRoom 拿到这个值做微调（+1 / dedup）
///     后再 <c>SetMaxAndCurrentHp(maxHp = base × m + 微调)</c>，我们的 patch 又 ×m → 最终 MaxHp
///     = <c>(base × m + 微调) × m ≈ base × m²</c>，远超 mod 设计意图的 ×m。
///     后果链：
///     - MaxHp 是 base × m²（截图 1080 ≈ 43 × 25）
///     - <c>ReattachPower</c>.Amount 在 base game 是写死的 25，复活时 <c>Heal(creature, 25)</c>
///     - 我们 Heal patch ×m → 125。复活只回 11.6% 血（截图 125/1080）
///     - 用户感知："复活数值没有受到 HP 倍率加成"
///     修复：patch <c>Creature.AfterAddedToRoom</c> prefix + postfix 加 EnterNestedSuppression /
///     ExitNestedSuppression。期间所有 CreatureCmd 调用都不会再缩放——直接保持 base game 内部
///     设的值（base game 里 maxHp 已经基于 spawn-time ×m 后的值算的微调）。
///     结果：
///     - 千足虫节段 MaxHp = base × m + 微调 ≈ 215（不再是 1080）
///     - <c>ReattachPower.Heal(creature, 25)</c> → patch ×m → 125
///     - 复活后 125 / 215 ≈ 58%，跟 base game 单人 25/46 ≈ 54% 一致 ✓
///     ## 对其他怪物的影响
///     - 大部分怪物 AfterAddedToRoom 是空实现（base MonsterModel.AfterAddedToRoom），不调
///     CreatureCmd，新 patch 是 no-op
///     - 千足虫节段：MaxHp 不再 ×m²，回归 mod 设计的 ×m ✓
///     - Doormaker / WaterfallGiant：AfterAddedToRoom 内 SetMaxAndCurrentHp(999...)、设
///     ShowsInfiniteHp=true。我们 suppress → 直接传 999...。base game SetMaxHpInternal
///     clamp 到 999_999_999。OK
///     - ToughEgg / TestSubject：AfterAddedToRoom 内不调 SetMax* 类（Hatch / Revive 在战斗中调，
///     不在此 scope 内），正常被 patch ×m。OK
///     - 召唤物（亡灵契约师 Osty 等）：side=Player，TryScaleAmount 第二行检查就早返。
///     新 patch 不影响。OK
///     ## 异步方法的 postfix 时机
///     AfterAddedToRoom 是 async Task。Harmony 对 async 方法的 postfix 在外层 stub 返回 Task 时跑
///     （不等 Task 完成）。但 SetMaxAndCurrentHp 的 prefix 是同步触发的——stub 同步执行直到第一个
///     真异步 await。AfterAddedToRoom 内的 SetMaxAndCurrentHp 调用顺序是 await SetMaxAndCurrentHp
///     → 在 SetMaxAndCurrentHp 内部 await SetMaxHp → 同步触发 SetMaxHp prefix → SetMaxHpInternal
///     同步 → 完成。然后 await SetCurrentHp → SetCurrentHpInternal → 可能 await Hook → 真异步
///     暂停。这时控制流返回，AfterAddedToRoom stub 也返回 Task，我们 postfix 跑 ExitNestedSuppression。
///     关键：所有需要被抑制的 prefix（SetMaxAndCurrentHp、SetMaxHp、Heal）<b>都在异步暂停前同步触发</b>
///     完毕。postfix 跑得"早"不影响正确性。
///     ## 已知 corner case
///     CreatureCmd.GainMaxHp / LoseMaxHp 内部调 SetMaxHp+Heal，amount 已经基于已加倍的 MaxHp，
///     再加倍会双重缩放。但搜了所有 Monsters/Powers，仅 PaperCutsPower 用 LoseMaxHp 且 target 是 player
///     （被 creature.Monster == null 跳过）；其他所有 GainMaxHp 调用 target 都是 player。所以实际不触发。
/// </summary>
internal static class MonsterRuntimeHpHelper
{
    /// <summary>
    ///     跟 SetMaxHpInternal 内部 <c>Math.Min((int)amount, 999999999)</c> 的上限对齐。
    ///     把缩放后的 amount clamp 到这个值，避免 Decimal -&gt; Int32 cast 时 OverflowException。
    /// </summary>
    public const decimal HpAmountCeiling = 999_999_999m;

    /// <summary>
    ///     嵌套调用抑制计数器。外层 patch 进入时 +1，退出时 -1；TryScaleAmount 看到 &gt; 0 就跳过。
    ///     多个嵌套源（SetMaxAndCurrentHp + AfterAddedToRoom）共用同一计数器——OK，因为只要任一在
    ///     活动就该抑制，计数加加减减自我平衡。
    /// </summary>
    [ThreadStatic] private static int _nestedSuppressCount;

    /// <summary>当前是否处于"内层缩放抑制"状态。</summary>
    public static bool IsNestedSuppressed => _nestedSuppressCount > 0;

    /// <summary>外层 patch 进入时调，告诉嵌套调用"不要再缩放"。</summary>
    public static void EnterNestedSuppression()
    {
        _nestedSuppressCount++;
    }

    /// <summary>外层 patch 退出时调，配对 <see cref="EnterNestedSuppression" />。</summary>
    public static void ExitNestedSuppression()
    {
        if (_nestedSuppressCount > 0) _nestedSuppressCount--;
    }

    public static double GetHpMult(Creature creature)
    {
        var combatState = creature.CombatState;
        if (combatState == null) return 1.0;
        var (hp, _) = DifficultyMultiplierContext.GetCurrentMultipliers(
            combatState.RunState, combatState.Encounter);
        return hp;
    }

    /// <summary>
    ///     对 monster amount 计算缩放值；返回 false 表示该跳过（不是 monster / 倍率=1 / 无敌阶段 /
    ///     嵌套调用抑制中）。
    /// </summary>
    public static bool TryScaleAmount(Creature creature, decimal originalAmount, out decimal scaled)
    {
        scaled = originalAmount;

        // ★ 关键防御 3 / 4：被外层 patch 标记为嵌套抑制中——跳过，避免双重缩放
        if (IsNestedSuppressed) return false;

        if (creature.Monster == null) return false; // 玩家本体（无 Monster 模型）

        // 关键：召唤物 / 玩家 pet（如亡灵契约师的 Osty）也有 Monster 字段非 null，但属于 Player side。
        // 这些不应被敌人 HP 倍率影响——否则它们的 MaxHp 会被 patch 加倍，再加上 base game 内 GainMaxHp
        // 走 SetMaxHp 路径（amount = MaxHp + delta），会指数级爆炸（每次召唤 ≈ ×倍率）。
        // 敌人召唤的 minion（如 Fabricator/Ovicopter 召的）走 CombatSide.Enemy，会正常加倍。
        if (creature.Side != CombatSide.Enemy) return false;

        // 关键：infinite-HP 阶段（Doormaker/WaterfallGiant 第一阶段）期间，
        // SetMaxAndCurrentHp(OriginalHp) 这种 reset 调用传的是已加倍值，不能再加倍
        if (creature.HpDisplay.IsInfinite()) return false;

        var mult = GetHpMult(creature);
        if (Math.Abs(mult - 1.0) < 1e-6) return false;

        scaled = originalAmount * (decimal)mult;
        if (scaled > HpAmountCeiling) scaled = HpAmountCeiling;
        if (scaled < 1m) scaled = 1m;
        return true;
    }
}

// ============================================================
// v0.4.2 新增：AfterAddedToRoom 整段嵌套抑制
// ============================================================

/// <summary>
///     千足虫节段类怪物在 <c>AfterAddedToRoom</c> 内部调 <c>CreatureCmd.SetMaxAndCurrentHp</c>
///     重设 HP（详见 base game <c>DecimillipedeSegment.cs:119-143</c>）。spawn-time 我们已经 ×m，
///     这里再 ×m 会让 MaxHp = base × m²。
///     修复：进入 AfterAddedToRoom 时设嵌套抑制，期间所有 CreatureCmd patch 都跳过缩放——
///     让 base game 的微调（+1 奇变偶、dedup 等）直接生效，但不再 ×m。
///     patch <c>Creature.AfterAddedToRoom</c> 而不是各个具体 Monster 的 override，因为：
///     1. Creature.AfterAddedToRoom 是统一入口，所有怪物都经过这里调 Monster.AfterAddedToRoom
///     2. base game 写死了 <c>if (Side == Enemy) await Monster.AfterAddedToRoom()</c>，
///     我们 prefix 在判断之前跑，对所有 creature 都 set 一次 flag——但只有 Enemy 才真正
///     调 Monster.AfterAddedToRoom，玩家 creature 的 prefix→postfix 是空 set/clear，无害
///     <c>[HarmonyPriority(Priority.High)]</c> 让我们 prefix 比其他 mod 早跑，确保 flag 在
///     Monster.AfterAddedToRoom 开始前就 set。postfix 用 Low 让 flag 晚清。
/// </summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.AfterAddedToRoom))]
public static class CreatureAfterAddedToRoomSuppressPatch
{
    [HarmonyPriority(Priority.High)]
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (!PatchScope.IsEnabled) return;
        MonsterRuntimeHpHelper.EnterNestedSuppression();
    }

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!PatchScope.IsEnabled) return;
        try
        {
            MonsterRuntimeHpHelper.ExitNestedSuppression();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"CreatureAfterAddedToRoomSuppressPatch.Postfix failed: {ex}");
        }
    }
}

/// <summary>
///     双重保险——同时 patch <see cref="MegaCrit.Sts2.Core.Combat.CombatManager.AfterCreatureAdded" />。
///     为什么：base game 既有 <c>Creature.AfterAddedToRoom</c>（async Task instance method），又有
///     <c>CombatManager.AfterCreatureAdded</c>（async Task instance method 包装它）。两者都是
///     async method，Harmony 对 async method 的 patch 在某些罕见情况下可能没有生效（编译器/JIT
///     因素 / 其他 mod 的 transpiler 影响），所以两层都 patch 增加鲁棒性。
///     计数器是嵌套的——多套一层抑制无副作用：内层 patch 进 +1 后是 2，外层 +1 后是 1，相互独立的
///     EnterNestedSuppression / ExitNestedSuppression 配对让 counter 永远不会负数 / 残留。
///
/// ⚠️ <b>必须显式给出参数类型</b>（2026-09-22 修）：<c>CombatManager.AfterCreatureAdded</c> 有**两个重载**
/// （<c>public Task AfterCreatureAdded(Creature)</c> 与 <c>private static Task AfterCreatureAdded(Creature, CombatState)</c>），
/// 只写方法名会让 Harmony 抛 <c>Ambiguous match</c>，整个 patch 类每次启动都被跳过
/// —— 游戏日志里的原话：<c>Patch class ...CombatManagerAfterCreatureAddedSuppressPatch failed (skipped):
/// HarmonyException: Ambiguous match for HarmonyMethod[...]</c>。这里钉住公开的那个单参重载。
/// </summary>
[HarmonyPatch(typeof(CombatManager),
    nameof(CombatManager.AfterCreatureAdded),
    new[] { typeof(Creature) })]
public static class CombatManagerAfterCreatureAddedSuppressPatch
{
    [HarmonyPriority(Priority.High)]
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (!PatchScope.IsEnabled) return;
        MonsterRuntimeHpHelper.EnterNestedSuppression();
    }

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!PatchScope.IsEnabled) return;
        try
        {
            MonsterRuntimeHpHelper.ExitNestedSuppression();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"CombatManagerAfterCreatureAddedSuppressPatch.Postfix failed: {ex}");
        }
    }
}

// ============================================================
// CreatureCmd patches
// ============================================================

/// <summary>
///     CreatureCmd.SetMaxHp(creature, amount) 的 prefix：amount 加倍 + clamp。
///     用于 TestSubject.Revive 等"重设 MaxHp"路径。
///     当被 <see cref="CreatureCmdSetMaxAndCurrentHpPatch" /> 间接调用，或在 AfterAddedToRoom
///     scope 内调用时，TryScaleAmount 会因为 IsNestedSuppressed=true 早返不动 amount。
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.SetMaxHp))]
public static class CreatureCmdSetMaxHpPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPrefix]
    public static void Prefix(Creature creature, ref decimal amount)
    {
        if (!PatchScope.IsEnabled) return;
        if (creature == null) return;

        var localAmount = amount;
        var result = PatchScope.Run(nameof(CreatureCmdSetMaxHpPatch), () =>
        {
            return MonsterRuntimeHpHelper.TryScaleAmount(creature, localAmount, out var s)
                ? (decimal?)s
                : null;
        });

        if (result.HasValue) amount = result.Value;
    }
}

/// <summary>
///     CreatureCmd.SetMaxAndCurrentHp(creature, amount) 的 prefix + postfix：
///     - prefix: amount 加倍 + clamp，然后<b>进入嵌套抑制</b>
///     - postfix: <b>退出嵌套抑制</b>
///     防止 base game 实现 <c>SetMaxHp(amount); SetCurrentHp(amount);</c> 内层 SetMaxHp 再被
///     我们 prefix 加倍一次（MaxHp ×m²、CurrentHp ×m，比例 1:m 的 bug）。
///     async 方法的 postfix 时机：见类级别 doc"防御 4"小节。
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.SetMaxAndCurrentHp))]
public static class CreatureCmdSetMaxAndCurrentHpPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPrefix]
    public static void Prefix(Creature creature, ref decimal amount)
    {
        if (!PatchScope.IsEnabled) return;
        if (creature == null) return;

        var localAmount = amount;
        var result = PatchScope.Run(nameof(CreatureCmdSetMaxAndCurrentHpPatch), () =>
        {
            return MonsterRuntimeHpHelper.TryScaleAmount(creature, localAmount, out var s)
                ? (decimal?)s
                : null;
        });

        if (result.HasValue) amount = result.Value;

        // ⚠️ 无论 amount 是否被改，进入这个 patch 都要抑制内层重缩放（配对 EnterNestedSuppression
        // 跟 postfix 的 ExitNestedSuppression 永远配对，避免计数器错位）
        MonsterRuntimeHpHelper.EnterNestedSuppression();
    }

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!PatchScope.IsEnabled) return;

        // postfix 不能抛——配对计数器递减，包在 try 里防御
        try
        {
            MonsterRuntimeHpHelper.ExitNestedSuppression();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"CreatureCmdSetMaxAndCurrentHpPatch.Postfix failed: {ex}");
        }
    }
}

/// <summary>
///     CreatureCmd.Heal(creature, amount) 的 prefix：amount 加倍 + clamp。
///     用于：
///     - 千足虫 <c>ReattachPower.DoReattach</c>：复活时 <c>Heal(creature, base.Amount=25)</c>，
///     patch 后 Heal 25×m。复活后 CurrentHp 跟 MaxHp 比例跟 base game 一致（约 58%）
///     - <c>RegenPower</c>：Regen 时 <c>Heal(creature, base.Amount)</c>，patch 后 Heal amount×m
///     - <c>IllusionPower</c>（Parafright 复活）：<c>Heal(creature, MaxHp - CurrentHp)</c>，
///     patch 后是 (MaxHp - CurrentHp)×m，被 SetCurrentHpInternal clamp 到 MaxHp，等效满血复活
///     - <c>TestSubject.Revive</c>：<c>SetMaxHp(scaledHp); Heal(scaledHp);</c>，两个 patch 都 ×m
///     在 AfterAddedToRoom scope 内调用时 TryScaleAmount 返回 false 跳过——但 base game 没有怪物
///     在 AfterAddedToRoom 内调 Heal，所以实际无影响。
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
public static class CreatureCmdHealPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPrefix]
    public static void Prefix(Creature creature, ref decimal amount)
    {
        if (!PatchScope.IsEnabled) return;
        if (creature == null) return;

        var localAmount = amount;
        var result = PatchScope.Run(nameof(CreatureCmdHealPatch), () =>
        {
            return MonsterRuntimeHpHelper.TryScaleAmount(creature, localAmount, out var s)
                ? (decimal?)s
                : null;
        });

        if (result.HasValue) amount = result.Value;
    }
}