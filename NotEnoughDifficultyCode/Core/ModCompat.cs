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
    ///     便捷判断：这个方法上有没有别的 mod 在动手。
    ///     <paramref name="context" /> 只用于日志，说明我们在哪一步做的检测。
    /// </summary>
    public static bool HasCompetingMod(MethodBase? target, string context)
    {
        var owners = GetCompetingOwners(target);
        if (owners.Count == 0) return false;

        MainFile.DebugLog(
            $"[ModCompat] {context}: 检测到其它 mod 也在改 {target?.DeclaringType?.Name}.{target?.Name} " +
            $"[{string.Join(", ", owners)}] —— 本功能放权跳过，避免打架");
        return true;
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

    /// <summary>地图界面（合成火堆的视觉注入走这里）。</summary>
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
