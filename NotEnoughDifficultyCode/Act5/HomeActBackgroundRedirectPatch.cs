using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     本模组的幕里，战斗背景要"像是在敌人**自己那一幕**里打" —— 做法是**改写 parentAct**，
///     然后让原版 + 其它模组的钩子**照常跑**（而不是我们自己去造背景）。
///
/// ## 为什么必须"折返"而不是"自己造"（用 ActsFromThePast 的真实代码验证过）
/// 那个模组的背景是三层 patch（`ActsFromThePast.Patches.Acts.ActBackgroundPatches`）：
/// <code>
///   LegacyGenerateBackgroundAssetsPatch : Prefix(ActModel act, ref BackgroundAssets __result)  // act 级
///   LegacyEncounterBackgroundPatch      : Prefix(EncounterModel, ActModel parentAct, Rng, ...) // ★ 敌人级
///   LegacyBackgroundCreatePatch         : Prefix(BackgroundAssets bg, ref NCombatBackground __result)
/// </code>
/// 它的自绘竞技场节点（`TheBeyondBackground : NCombatBackground`）只在
/// **`parentAct` 是它自己的 act**（敌人级钩子）时才挂上；而"资产实例 → 背景节点"是靠
/// `LegacyBackgrounds` 这张**按对象引用**查的表，所以资产必须是它自己产出的那一个。
///
/// 我先前那版是 `__result = source.GenerateBackgroundAssets(rng)`（自己造）：
/// ① 那条 act 级调用会被 BaseLib 的 `CustomActModel` 前缀先截住 ⇒ 它的 act 级钩子根本不跑；
/// ② 就算跑出资产，也不保证是它表里登记的那个实例。
/// 结果：玩家看到的是**默认场景**（用户实测："走得不是模组自带的，而是荣耀三层"）。
///
/// ## 现在的做法
/// <code>
///   Prefix(EncounterModel enc, ref ActModel parentAct, Rng rng, ref BackgroundAssets __result)
///     parentAct 是本模组的幕（act4/act5）且解析出"敌人自己的幕" ⇒ **parentAct = 那个幕**，return true
///     ⇒ 原版继续跑，其它模组的**敌人级**钩子也照常跑（它们看到的就是"正确的那一幕"）
/// </code>
/// 我们**不改任何返回值**，只把"这是什么幕"这件事实纠正过来 —— 这是最兼容的做法：
/// 别的模组怎么写背景，就还是它自己那套。
///
/// ## 唯一例外：自带背景的敌人（配置项）
/// `HasCustomBackground == true` 的敌人原本会用它自带的背景；若玩家把
/// <c>Act4_OverrideEncounterBackground</c> 打开，则强制按来源幕覆盖（此时才由我们造资产）。
/// 默认关闭该覆盖 —— 尊重那个模组的美术设计。
///
/// ## 优先级
/// `Priority.First`：必须早于别的模组的同方法 prefix，否则它们看到的还是 act4/act5。
/// </summary>
[HarmonyPatch(typeof(EncounterModel), "GetBackgroundAssets")]
internal static class HomeActBackgroundRedirectPatch
{
    /// <summary>上一次打日志的键（(敌人, 来源幕)）—— 这个方法会被预载/多次调用，日志去重防刷屏。</summary>
    private static string? _lastLogged;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(EncounterModel __instance, ref ActModel parentAct, Rng rng,
        ref BackgroundAssets __result)
    {
        if (!PatchScope.IsEnabled) return true;
        if (parentAct is not (Act4Model or Act5Model)) return true;   // 只管本模组的幕

        // ★ 只拦截**精英**（用户口径："检测到处于 ACT 5/4 场景且为精英的时候才会走拦截，否则就放过去"）。
        //   BOSS / 伪装BOSS（小怪房）/ 其它一律放过去 —— 本幕自己的 BOSS 场景必须保住
        //   （踩过：不加这道判断，ACT 5/6 的 BOSS 场景被来源幕顶掉，用户实测"BOSS 场景没了"）。
        if (!Act4FightSource.IsEliteFight()) return true;

        var source = Act4FightSource.Current();
        if (source == null || source is Act4Model or Act5Model) return true;

        var custom = Traverse.Create(__instance).Property("HasCustomBackground").GetValue<bool>();

        // 例外：自带背景的敌人 —— 只有玩家显式要求"按来源幕覆盖"时才由我们造资产
        if (custom && NotEnoughDifficultyConfig.Act4_OverrideEncounterBackground)
        {
            var assets = PatchScope.Run(nameof(HomeActBackgroundRedirectPatch),
                () => source.GenerateBackgroundAssets(rng), (BackgroundAssets?)null);
            if (assets == null) return true;

            __result = assets;
            MainFile.DebugLog(
                $"[背景折返] '{__instance?.Id?.Entry}' 自带背景，但配置要求按来源幕覆盖 → '{source.GetType().Name}'");
            return false;
        }

        // 常规：只把"这一场属于哪一幕"改对，其余全交给原版与其它模组
        var logKey = $"{__instance?.Id?.Entry}|{source.GetType().Name}";
        if (logKey != _lastLogged)
        {
            _lastLogged = logKey;
            MainFile.DebugLog(
                $"[背景折返] '{__instance?.Id?.Entry}'：parentAct {parentAct.GetType().Name} → " +
                $"'{source.GetType().Name}'（自带背景={custom}，原版/别的模组的钩子照常生效）");
        }

        parentAct = source;
        return true;
    }
}
