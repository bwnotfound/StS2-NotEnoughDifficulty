using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     反射访问 RunManager.State（私有属性）的 helper。
///     为什么需要这个：
///     - RunManager.State 是 private RunState? State { get; set; }
///     - mod 项目的 csproj 没启用 publicize（&lt;Publicize ... Condition="False"&gt;）
///     - 直接访问会编译报错"不能在此处访问 private 属性 'State'"
///     BaseLib 自己也是用同样的反射方式（参见 CurrentGeneratingRunState）。
///     我们独立定义一份，避免依赖 BaseLib 内部 patch 的状态字段时序。
///     ## 性能
///     把 MethodInfo.Invoke 用 CreateDelegate 升级成强类型委托，调用开销和直接访问属性基本相同。
///     因为 PatchScope.TryEnter 在每个 patch 入口都会调用一次 GetState（包括 hot path 上的
///     CombatRoom.RoomType getter 等等），这里值得做一次性优化。
///     ## 错误处理
///     反射解析失败（base game 重命名属性等极端情况）只 log 一次，避免刷屏。
/// </summary>
internal static class RunStateAccessor
{
    private static readonly Func<RunManager, RunState?>? StateGetter = TryBuildGetter();

    /// <summary>解析失败时记录一次错误，之后静默。</summary>
    private static bool _loggedFailure;

    private static Func<RunManager, RunState?>? TryBuildGetter()
    {
        try
        {
            var mi = AccessTools.PropertyGetter(typeof(RunManager), "State");
            if (mi == null) return null;
            // 强类型委托避免每次调用都走 reflection invoke 路径
            return (Func<RunManager, RunState?>)Delegate.CreateDelegate(
                typeof(Func<RunManager, RunState?>), mi);
        }
        catch
        {
            return null;
        }
    }

    public static RunState? GetState(RunManager runManager)
    {
        if (StateGetter != null) return StateGetter(runManager);

        if (!_loggedFailure)
        {
            _loggedFailure = true;
            try
            {
                MainFile.Logger.Error(
                    "Could not resolve RunManager.State getter via reflection.");
            }
            catch
            {
                // logger 也挂了，没办法
            }
        }

        return null;
    }

    /// <summary>
    ///     便捷方法：直接从 <c>RunManager.Instance</c> 取当前 run state。
    ///     取不到（无 run / 反射失败）返回 null——调用方必须按 null 处理。
    /// </summary>
    public static RunState? GetCurrentState()
    {
        try
        {
            var rm = RunManager.Instance;
            return rm == null ? null : GetState(rm);
        }
        catch
        {
            return null;
        }
    }
}