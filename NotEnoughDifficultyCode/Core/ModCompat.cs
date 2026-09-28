using System.Reflection;
using HarmonyLib;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     模组兼容检测层。
///
/// ## 设计原则（用户要求）
/// **检测到别的 mod 也在改同一处 → 直接放权跳过，并记日志。**
/// 宁可我们的功能不生效，也不要在多 mod 环境里互相打架、产生难以定位的怪现象。
///
/// ## 实现依据
/// Harmony 维护着"每个被 patch 的方法上挂了哪些 patch"的信息，
/// 每条 patch 都带 <c>owner</c>（发起 patch 的 Harmony 实例 id）。
/// 本 mod 用 <see cref="MainFile.ModId" /> 作为 Harmony id，所以
/// **owner != 我们 的 patch 就是别的 mod 的**。
///
/// 这样我们能在运行时回答："`RunManager.GenerateRooms` 上除了我们和 BaseLib，还有别人吗？"
/// —— 而不是靠硬编码一串可能冲突的模组名。
/// </summary>
internal static class ModCompat
{
    /// <summary>不算"竞争"的 owner：Harmony 自身、BaseLib（我们的硬依赖）、以及本 mod。</summary>
    private static readonly HashSet<string> ExpectedOwners =
        new(StringComparer.OrdinalIgnoreCase) { "BaseLib", "HarmonyLib", "0Harmony" };

    /// <summary>
    ///     返回"除本 mod 与预期依赖之外，还在 patch 这个方法"的 owner 列表。
    ///     空列表 = 没人跟我们抢这一处。
    /// </summary>
    public static List<string> GetCompetingOwners(MethodBase? target)
    {
        var result = new List<string>();
        if (target == null) return result;

        try
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Scan(IEnumerable<Patch>? patches)
            {
                if (patches == null) return;
                foreach (var p in patches)
                {
                    var owner = p?.owner;
                    if (string.IsNullOrEmpty(owner)) continue;
                    if (string.Equals(owner, MainFile.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ExpectedOwners.Contains(owner!)) continue;
                    if (seen.Add(owner!)) result.Add(owner!);
                }
            }

            Scan(info.Prefixes);
            Scan(info.Postfixes);
            Scan(info.Transpilers);
            Scan(info.Finalizers);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ModCompat] 查询 {target.Name} 的 patch 信息失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    ///     已经打过日志的 target（方法名）+ 场景，避免热路径重复刷屏。
    ///     key = "{context}|{DeclaringType}.{Name}|{surrender|coexist}"
    /// </summary>
    private static readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    private static bool NoteOnce(string context, MethodBase? target, string kind)
    {
        var key = $"{context}|{target?.DeclaringType?.FullName}.{target?.Name}|{kind}";
        lock (_noted) return _noted.Add(key);
    }

    private static string Describe(MethodBase? target) =>
        $"{target?.DeclaringType?.Name}.{target?.Name}";

    /// <summary>
    ///     便捷判断：这个方法上有没有别的 mod 在动手。
    ///     <paramref name="context" /> 只用于日志，说明我们在哪一步做的检测。
    ///
    /// ⚠️ <b>日志级别必须是 Warn（不能是 DebugLog）</b>——这类"静默放权"曾经让
    /// 「双BOSS中间火堆不见了」查了一整轮：<c>DebugLogging</c> 默认关，游戏日志里一个字都没有，
    /// 现象看起来就像"代码没写"。同一个目标只报一次，避免热路径刷屏。
    /// </summary>
    public static bool HasCompetingMod(MethodBase? target, string context)
    {
        var owners = GetCompetingOwners(target);
        if (owners.Count == 0) return false;

        if (NoteOnce(context, target, "surrender"))
        {
            MainFile.Logger.Warn(
                $"[ModCompat] {context}: 检测到其它 mod 也在改 {Describe(target)} " +
                $"[{string.Join(", ", owners)}] —— 本功能【放权跳过】（若该功能消失，先看这一行）");
        }

        return true;
    }

    /// <summary>
    ///     记录"这个方法上还有别的 mod 在动手"，但<b>不放弃本功能</b>（共存）。
    ///
    /// 用于"我们只做加法、与别人的 patch 语义不冲突"的接缝 —— 典型是
    /// <c>NMapScreen.SetMap</c>：我们只是在原方法跑完之后往 <c>_points</c> 容器里多挂几个节点，
    /// 别的 mod 挂不挂 postfix / 改不改 IL 都不影响这件事（Harmony 本身允许多个 patch 共存）。
    ///
    /// ## 为什么必须区分"放权"与"共存"（2026-09-22 实际事故）
    /// 创意工坊模组 <b>Ascension100</b>（Steam 3801607408）为"进阶 11 的地图火焰特效"
    /// 挂了一个 <c>[HarmonyPatch(typeof(NMapScreen), "SetMap")]</c> 的空壳 postfix
    /// （只做 <c>CallDeferred(MapFlames.Refresh)</c>，纯加法）。它一装上，
    /// 我们这边"有人在改 SetMap ⇒ 整套放权"就把**合成火堆的注入整个跳过**了
    /// —— 而双 boss 本身还在（由 <c>RunManager.GenerateMap</c> 前缀那条无放权判定的路径补的），
    /// 于是玩家看到的现象就是「双BOSS中间火堆没有了」。
    /// </summary>
    public static void NoteCoexistence(string context, MethodBase? target)
    {
        var owners = GetCompetingOwners(target);
        if (owners.Count == 0) return;

        if (NoteOnce(context, target, "coexist"))
        {
            MainFile.Logger.Info(
                $"[ModCompat] {context}: {Describe(target)} 上还有其它 mod 的 patch " +
                $"[{string.Join(", ", owners)}] —— 我们只做加法（多挂节点），【继续执行】，不放弃功能");
        }
    }

    // ---------- 常查的几个目标（带缓存，避免 hot path 反复反射） ----------

    private static readonly Lazy<MethodBase?> GenerateRooms =
        new(() => AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "GenerateRooms"));

    private static readonly Lazy<MethodBase?> GenerateMap =
        new(() => AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "GenerateMap"));

    private static readonly Lazy<MethodBase?> CreateMap =
        new(() => AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.ActModel), "CreateMap"));

    private static readonly Lazy<MethodBase?> PullNextEncounter =
        new(() => AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.ActModel), "PullNextEncounter"));

    private static readonly Lazy<MethodBase?> NMapScreenSetMap =
        new(() => AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), "SetMap"));

    /// <summary>房间生成的 patch 目标（<c>RunManager.GenerateRooms</c>，供"共存"日志复用）。</summary>
    internal static MethodBase? RoomGenerationTarget => GenerateRooms.Value;

    /// <summary>地图界面的 patch 目标（<c>NMapScreen.SetMap</c>，供"共存"日志复用）。</summary>
    internal static MethodBase? MapScreenTarget => NMapScreenSetMap.Value;

    /// <summary>房间生成（我们在这里排双 boss 与 act4/5 的池子）。</summary>
    public static bool SomeoneElsePatchesRoomGeneration(string context) =>
        HasCompetingMod(GenerateRooms.Value, context);

    /// <summary>地图生成（我们在这里做 act4 扩深行转换）。</summary>
    public static bool SomeoneElsePatchesMapGeneration(string context) =>
        HasCompetingMod(GenerateMap.Value, context);

    /// <summary>建图（act5 直线地图从这里接管）。</summary>
    public static bool SomeoneElsePatchesCreateMap(string context) =>
        HasCompetingMod(CreateMap.Value, context);

    /// <summary>取怪（act4/5 的池子直接相关）。</summary>
    public static bool SomeoneElsePatchesEncounterPull(string context) =>
        HasCompetingMod(PullNextEncounter.Value, context);

    /// <summary>
    ///     地图界面（<c>NMapScreen.SetMap</c>）。
    ///
    /// ⚠️ <b>不要再拿它去否决合成火堆的注入</b>（见 <see cref="NoteCoexistence" /> 里 2026-09-22 的事故）：
    /// 那个接缝上我们只做加法，别人挂 postfix / 改 IL 都不冲突。此方法保留只作诊断用途
    /// （<see cref="LogCompatibilityReport" /> 就是直接查 owner 列表的）。
    /// </summary>
    public static bool SomeoneElsePatchesMapScreen(string context) =>
        HasCompetingMod(NMapScreenSetMap.Value, context);

    /// <summary>启动时打一份兼容性报告，便于用户一眼看出谁在抢同一处。</summary>
    public static void LogCompatibilityReport()
    {
        try
        {
            var lines = new List<string>
            {
                $"GenerateRooms: [{string.Join(", ", GetCompetingOwners(GenerateRooms.Value))}]",
                $"GenerateMap:   [{string.Join(", ", GetCompetingOwners(GenerateMap.Value))}]",
                $"CreateMap:     [{string.Join(", ", GetCompetingOwners(CreateMap.Value))}]",
                $"PullNextEnc:   [{string.Join(", ", GetCompetingOwners(PullNextEncounter.Value))}]",
                $"SetMap:        [{string.Join(", ", GetCompetingOwners(NMapScreenSetMap.Value))}]",
            };

            MainFile.DebugLog("[ModCompat] 关键 patch 点上的其它 mod：\n  " + string.Join("\n  ", lines));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ModCompat] 兼容性报告生成失败: {ex.Message}");
        }
    }
}
