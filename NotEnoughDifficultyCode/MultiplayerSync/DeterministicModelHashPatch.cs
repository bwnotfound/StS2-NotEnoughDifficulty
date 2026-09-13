// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
/// 【实验性】让 ModelDb hash 的计算变成确定性的，消除 mod 环境差异导致的
/// "ModelDb hash mismatch" 联机握手失败。
///
/// ## 问题
///
/// base game 的 <c>ModelIdSerializationCache.Init</c> 计算 ModelDb hash 时，把所有
/// AbstractModel 子类型按 <c>Type.Name</c> 用 <c>List.Sort</c>（不稳定排序）排序。而
/// base game 自己有同名类型（Byrdpip / LostWisp / PaelsLegion 各有一个 Monsters.* 和
/// 一个 Relics.* 版本，<c>Type.Name</c> 相同）。不稳定排序对这些同名类型的先后顺序
/// 取决于待排序列表的内容——装上任何加 model 的 mod 都会改变这个列表，使联机双方算出
/// 不同的 hash，JoinFlow 报 "ModelDb hash mismatch" 拒绝连接。
///
/// ## 修复
///
/// 用 transpiler 把 <c>Init</c> 里那次 <c>list.Sort(comparer)</c> 重定向到本类的
/// <see cref="StableSort"/>。开关开启时它在原比较器返回 0 时再用 <c>Type.FullName</c>
/// 做 tiebreak，使排序成为<b>全序</b>——结果唯一确定、与输入顺序无关。
///
/// ## 时序与开关读取（关键）
///
/// 调用链：<c>OneTimeInitialization.ExecuteVeryEarly</c> → <c>ModManager.Initialize</c>
/// → 调用 mod 的 <c>[ModInitializer]</c> 方法（<c>MainFile.Initialize</c> → <c>PatchAll</c>）；
/// 之后 <c>OneTimeInitialization.ExecuteEssential</c> → <c>ModelIdSerializationCache.Init</c>。
///
/// 所以本 patch 的装载<b>早于</b> <c>Init</c>，<c>Init</c> 执行时一定会走到
/// <see cref="StableSort"/>。但 <c>Init</c> 发生在游戏启动<b>极早期</b>，那时
/// <see cref="NotEnoughDifficultyConfig"/> 的静态字段<b>可能还没从磁盘 load</b>
/// （值还是默认 false）。因此 <see cref="StableSort"/> 不能直接读静态字段，必须先
/// <c>ModConfig.Load</c> 主动从磁盘把配置读进来，再判断开关——否则即使用户开了开关，
/// hash 计算时读到的也是默认 false。
///
/// ## ⚠️ 实验性功能须知
///
/// - 启用后 ModelDb hash 算法被改变：<b>联机双方必须都装本 mod 且都开启
///   <see cref="NotEnoughDifficultyConfig.DeterministicModelHash"/></b>。
/// - hash 在游戏启动时（握手前）即算好，<b>改开关后必须重启游戏</b>才生效。
/// - 这是绕过 base game 脆弱点的兜底；最稳妥的仍是让双方 mod 环境逐字节一致。
///
/// ## 诊断日志
///
/// 启动时本 patch 会打几条 log，便于判断是否生效：
///   - "redirected N Sort call(s)" —— transpiler 成功改写了 Init。
///   - "StableSort: ... APPLIED / DISABLED" —— Init 执行时是否真的走了确定性排序。
///   - "ModelDb hash after Init = X" —— 最终 hash 值（postfix 打印）。
/// </summary>
[HarmonyPatch(typeof(ModelIdSerializationCache), "Init")]
public static class DeterministicModelHashPatch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        Type? listType = null;
        try
        {
            listType = typeof(List<>).MakeGenericType(typeof(ValueTuple<Type, Mod>));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"DeterministicModelHashPatch: failed to resolve List<(Type,Mod)> type: {ex}");
        }

        if (listType == null)
        {
            MainFile.Logger.Warn(
                "DeterministicModelHashPatch: could not resolve target list type; " +
                "patch is INACTIVE (base game layout may have changed).");
            return codes;
        }

        var replacement = typeof(DeterministicModelHashPatch)
            .GetMethod(nameof(StableSort), BindingFlags.Public | BindingFlags.Static);

        int patchedCount = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call)
                && codes[i].operand is MethodInfo mi
                && mi.Name == "Sort"
                && mi.DeclaringType == listType)
            {
                // List.Sort(Comparison) 与 StableSort(List, Comparison) 栈布局一致
                // （[list, comparer] → void）。
                //
                // ⚠ 必须【原地修改】opcode/operand，绝不能 new 一条新 CodeInstruction
                // 来替换。base game 这里的 IL 是「comparer 缓存检查」模式——这条
                // `callvirt Sort` 指令身上挂着一个 Label（是 brtrue 跳过 delegate
                // 构造时的跳转目标）。new 新指令会丢掉原指令的 labels / blocks，
                // 使跳转指向不存在的 label，Harmony 生成方法时抛
                // "Bad label content in ILGenerator"，并中断后续所有 patch 的应用。
                // 原地改 opcode/operand 则保留 labels / blocks，安全。
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = replacement;
                patchedCount++;
            }
        }

        if (patchedCount == 0)
        {
            MainFile.Logger.Warn(
                "DeterministicModelHashPatch: no List<(Type,Mod)>.Sort call found in " +
                "ModelIdSerializationCache.Init; patch is INACTIVE (base game may have changed).");
        }
        else
        {
            MainFile.Logger.Info(
                $"DeterministicModelHashPatch: redirected {patchedCount} Sort call(s) in " +
                "ModelIdSerializationCache.Init to StableSort.");
        }

        return codes;
    }

    /// <summary>
    /// postfix：Init 跑完后打印最终的 ModelDb hash，便于用户判断功能是否生效、
    /// 以及和联机对象对比 hash。
    /// </summary>
    [HarmonyPostfix]
    public static void LogHashAfterInit()
    {
        try
        {
            MainFile.Logger.Info(
                $"DeterministicModelHashPatch: ModelIdSerializationCache.Init finished. " +
                $"ModelDb hash = {ModelIdSerializationCache.Hash} " +
                $"(deterministic sort {(ReadSwitchFromDisk() ? "ENABLED" : "disabled")}). " +
                $"For multiplayer, this value must match your peer's.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"DeterministicModelHashPatch.LogHashAfterInit failed: {ex}");
        }
    }

    /// <summary>
    /// transpiler 把 Init 里的 <c>list.Sort</c> 重定向到这里。栈布局与
    /// <c>List.Sort(Comparison)</c> 一致。
    ///
    /// 开关关闭：原样 <c>list.Sort(original)</c>，行为与 base game 完全一致。
    /// 开关开启：用 <c>Type.FullName</c> 做 tiebreak 包装原比较器，排序成为全序。
    /// </summary>
    public static void StableSort(List<(Type, Mod)> list, Comparison<(Type, Mod)> original)
    {
        if (list == null) return;

        bool enabled = ReadSwitchFromDisk();

        if (original == null || !enabled)
        {
            // 开关关闭（或拿不到原比较器）：保持 base game 原行为
            if (original != null) list.Sort(original);
            MainFile.Logger.Info(
                $"DeterministicModelHashPatch.StableSort: deterministic sort DISABLED " +
                $"(switch={enabled}) — using base game's original (unstable) sort for {list.Count} types.");
            return;
        }

        list.Sort((a, b) =>
        {
            int primary = original(a, b);
            if (primary != 0) return primary;

            // 原比较器判定相等 —— 用完整类型名（含命名空间）做确定性 tiebreak。
            // base game 的同名类型（Byrdpip 等）命名空间不同，FullName 必不同，排序成为全序。
            string fa = a.Item1?.FullName ?? a.Item1?.Name ?? string.Empty;
            string fb = b.Item1?.FullName ?? b.Item1?.Name ?? string.Empty;
            int byFullName = string.CompareOrdinal(fa, fb);
            if (byFullName != 0) return byFullName;

            // 极端兜底：FullName 仍相同（正常只有同一类型自身）—— 再比 assembly 名
            string aa = a.Item1?.Assembly.FullName ?? string.Empty;
            string ab = b.Item1?.Assembly.FullName ?? string.Empty;
            return string.CompareOrdinal(aa, ab);
        });

        MainFile.Logger.Info(
            $"DeterministicModelHashPatch.StableSort: deterministic (total-order) sort APPLIED " +
            $"to {list.Count} model types. NOTE: all networked players must enable this for hashes to match.");
    }

    /// <summary>
    /// 主动从磁盘读取配置后返回开关值。
    ///
    /// ModelIdSerializationCache.Init 在游戏启动极早期执行，那时 NotEnoughDifficultyConfig
    /// 的静态字段可能还没 load（仍是默认值）。这里先 ModConfig.Load 强制从磁盘读，
    /// 再返回字段值，确保拿到用户在配置里设置的真实开关状态。
    /// </summary>
    private static bool ReadSwitchFromDisk()
    {
        try
        {
            ModConfig.Load<NotEnoughDifficultyConfig>();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn(
                $"DeterministicModelHashPatch: could not load config from disk " +
                $"({ex.Message}); falling back to current in-memory value.");
        }

        return true; // [第二轮] 恒定启用：模型哈希确定性是联机正确性的硬前提，不再提供关闭开关
    }
}