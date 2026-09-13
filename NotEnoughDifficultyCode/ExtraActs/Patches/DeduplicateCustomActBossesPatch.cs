using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     让 <b>act4</b> 的顶端 boss 不与前 3 层已分配的 boss 重复（第二轮改造后语义收窄）。
///
/// ## 职责变化
/// 旧实现同时管 act4 与 act5 的顶端 boss 去重。第二轮改造后：
/// - <b>act5 不再需要</b>——它的 boss 名单由 <see cref="Act5RosterPlanner" /> 从"剩余未打池"编排，
///   本身就已经排除了已打过的与 act4 拿走的那一个，天然无重复。
/// - act4 继续需要：它的 boss 是从 act1~3 boss 池<b>随机抽一个</b>，必须避开前 3 层已经出现过的。
///
/// ## 为什么过滤不能放在 GenerateAllEncounters 里
/// <c>ActModel.AllEncounters</c> 是 lazy 缓存（_allEncounters ?? 生成 + 缓存）——
/// 也就是说 <c>Act4Model.GenerateAllEncounters</c> <b>只在 mod 加载时跑一次</b>，结果存进缓存后不再重建。
/// 把"避开已用 boss"这种<b>依赖 run 进度</b>的判断放进去，第二次 run 也不会重新算。
/// 所以这里在每次 <c>RunManager.GenerateRooms</c>（每个新 run / 每层都会跑）时动态过滤。
///
/// ## Harmony ordering
/// <c>[HarmonyAfter("BaseLib")]</c>：BaseLib 的 ActModelGenerateRoomsPatch 也 patch ActModel.GenerateRooms
/// 处理 Ancient 注入；我们 patch 的是 RunManager.GenerateRooms，按理不冲突，显式声明表达意图。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
[HarmonyAfter("BaseLib")]
public static class DeduplicateCustomActBossesPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void DeduplicateBosses(RunManager __instance)
    {
        // ⚠️ 已停用（用户实测定位）。
        //
        // 这个 postfix 挂在 GenerateRooms 上，会在**建图之后**再改一次 act4 的顶端 boss。
        // 而地图节点的图标是在**建图时**按当时的值绘制的 —— 于是出现
        // "图标是沙漏、实际打的是幕布巨兽"（图标与内容互换）。
        //
        // act4 的顶端 boss 现在由 EnsureAct4TopBoss 在建图**之前**一次定稿
        // （BossGauntletStylePatches 的 GenerateMap prefix 调用）。
        // 这里直接返回，绝不再改，保证图标与实际内容一致。
        return;

#pragma warning disable CS0162 // 下面是保留的旧实现，仅供对照
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(DeduplicateCustomActBossesPatch), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state?.Acts == null) return;

            // 收集 act1~3 已经用掉的 boss（按 acts 顺序，act4 之前的那些）
            var usedBossIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var act in state.Acts)
            {
                if (act is Act4Model or Act5Model) continue;
                var id = act?.BossEncounter?.Id?.Entry;
                if (!string.IsNullOrEmpty(id)) usedBossIds.Add(id);
                var second = act?.SecondBossEncounter?.Id?.Entry;
                if (!string.IsNullOrEmpty(second)) usedBossIds.Add(second);
            }

            var act4 = state.Acts.OfType<Act4Model>().FirstOrDefault();
            if (act4 == null) return;

            var pool = act4.AllBossEncounters?
                .Where(b => b?.Id?.Entry is { } id && !usedBossIds.Contains(id))
                .ToList();

            if (pool == null || pool.Count == 0)
            {
                MainFile.Logger.Warn(
                    "[Act4] 去重后 boss 池为空，保留默认顶端 boss " +
                    $"'{act4.BossEncounter?.Id?.Entry}'（可能与前面层重复）");
                return;
            }

            var rng = state.Rng?.UpFront;
            var picked = rng != null ? rng.NextItem(pool) : pool[0];
            var oldId = act4.BossEncounter?.Id?.Entry;
            act4.SetBossEncounter(picked!);

            MainFile.DebugLog(
                $"[Act4] 顶端 boss: '{oldId}' -> '{picked!.Id.Entry}' " +
                $"(已避开 [{string.Join(", ", usedBossIds)}]，去重后池 {pool.Count})");
        });
    }

    /// <summary>
    ///     在建图**之前**把 act4 的顶端 boss 定稿（供 BossGauntletStylePatches 的
    ///     GenerateMap prefix 调用）。与 Harmony postfix 里的逻辑同一份，避免两处漂移。
    /// </summary>
    public static void EnsureAct4TopBoss(MegaCrit.Sts2.Core.Models.ActModel act4, RunState state)
    {
        try
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in state.Acts)
            {
                if (a is Act4Model or Act5Model) continue;
                var id = a?.BossEncounter?.Id?.Entry;
                if (!string.IsNullOrEmpty(id)) used.Add(id);
                var second = a?.SecondBossEncounter?.Id?.Entry;
                if (!string.IsNullOrEmpty(second)) used.Add(second);
            }

            // ★ 同一层自己的**第二 boss** 也算"已用"。
            //
            // 实测 bug（用户反馈："ACT 4 直接抽取了 2 个沙漏"），日志顺序：
            //   [Gauntlet] 建图前补救: runState.Act 补设第二 boss='AEONGLASS_BOSS'（该层池 26 个）
            //   [Act4] 建图前定稿顶端 boss: 'KNOWLEDGE_DEMON_BOSS' -> 'AEONGLASS_BOSS'   ← 撞了
            // 根因：上面的 used 只收了 act1~3 的 boss，没算"同层已经定好的第二 boss"。
            // 时序上 DoubleBossConfigPatch / BossGauntletStylePatches 的补救都在本函数**之前**
            // （本函数由 GenerateMap prefix 调用），所以此刻能读到它。
            //
            // ⚠️ 这里**不做**任何"保护某一层"的硬编码（早期版本曾硬性排除 act3 的 boss，
            //    被用户否掉："不要硬编码啊!"）。去重只依据**实际已经分配的 boss**，纯动态。
            var act4Second = act4.SecondBossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(act4Second)) used.Add(act4Second);

            var pool = (act4.AllBossEncounters ?? Enumerable.Empty<MegaCrit.Sts2.Core.Models.EncounterModel>())
                .Where(b => b?.Id?.Entry is { } id && !used.Contains(id))
                .OrderBy(b => b.Id.Entry, StringComparer.Ordinal)
                .ToList();

            if (pool.Count == 0) return;

            var old = act4.BossEncounter?.Id?.Entry;
            if (old != null && !used.Contains(old)) return;   // 本来就合法，不用改

            // 确定性：种子里混入 act 标识，不消耗 rng
            var key = $"{state.Rng?.StringSeed}|act4top|{pool.Count}";
            uint hash = 2166136261u;
            unchecked { foreach (var ch in key) { hash ^= ch; hash *= 16777619u; } }

            var picked = pool[(int)(hash % (uint)pool.Count)];
            act4.SetBossEncounter(picked);
            MainFile.DebugLog($"[Act4] 建图前定稿顶端 boss: '{old}' -> '{picked.Id.Entry}'");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act4] EnsureAct4TopBoss 失败: {ex}");
        }
    }
}
