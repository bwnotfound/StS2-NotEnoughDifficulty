using BaseLib.Config;
using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     Mod 入口。负责注册配置、引导自定义 act、应用 Harmony patch。
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "NotEnoughDifficulty";

    // ModVersion 从 manifest 运行时读取，避免代码常量与 manifest 漂移。
    // 只缓存有效值——查不到时不缓存 "unknown"，允许后续重试。
    private static string? _cachedModVersion;

    public static string ModVersion
    {
        get
        {
            if (_cachedModVersion != null) return _cachedModVersion;
            try
            {
                var info = ModManager.Mods?.FirstOrDefault(m => m?.manifest?.id == ModId);
                var v = info?.manifest?.version;
                if (!string.IsNullOrEmpty(v))
                {
                    _cachedModVersion = v;
                    return v;
                }
            }
            catch
            {
                // 不缓存，让下次重试
            }

            return "unknown";
        }
    }

    public static Logger Logger { get; } =
        new(ModId, LogType.Generic);

    /// <summary>
    ///     诊断日志入口：**只在配置里打开「日志调试」时输出**。
    ///
    /// 为什么需要它：排查期我埋了大量地图结构/boss 抽取/合成节点日志，
    /// 正常游玩时它们会把有价值的提示淹没（玩家反馈"不去烦扰正常游玩的玩家"）。
    /// 所以：诊断日志一律走这里，错误日志（Error/Warn）仍直接输出、不受开关控制。
    /// </summary>
    public static void DebugLog(string message)
    {
        try
        {
            if (!NotEnoughDifficultyConfig.DebugLogging) return;
            Logger.Info(message);
        }
        catch
        {
            // 日志失败绝不影响游戏
        }
    }
    public static void Initialize()
    {
        Logger.Info($"Loading {ModId} {ModVersion}");

        // 注册自定义本地化表。新版 BaseLib（CustomLocTableManager）改为「登记制」：
        // 只有被注册的表名才会进入 LocManager 的加载列表并与 res://<id>/localization/<lang>/<表>.json
        // 做 merge。基础游戏没有的自定义表（settings_ui / acts）若不注册则永远不加载 → UI 显示原始 key。
        foreach (var table in new[] { "settings_ui.json", "acts.json", "cards.json" })
        {
            try
            {
                CustomLocTableManager.Register(table);
            }
            catch (Exception ex)
            {
                Logger.Error($"Register loc table {table} failed: {ex}");
            }
        }

        // ⚠️ 时机问题：LocManager 在创建/SetLanguage 时会把所有表 LoadTablesFromPath 后**缓存**。
        // 如果 mod 的 Initialize（含上面的 Register）发生在 LocManager 首次加载**之后**，则我们刚
        // 注册的表名没赶上那次加载 → 缓存里没有 mod 的 settings_ui/acts → UI 显示原始变量名。
        // 解决：注册后用当前语言强制 SetLanguage 一次，重建缓存（此时 ListLocalizationFiles 已含
        // 我们注册的表，会 merge res://NotEnoughDifficulty/localization/<lang>/<表>.json）。
        // 若 LocManager 尚未创建（mod 先于它加载），Instance 为 null → 跳过，稍后它自然带上已注册的表。
        try
        {
            var lm = LocManager.Instance;
            if (lm != null && !string.IsNullOrEmpty(lm.Language))
            {
                lm.SetLanguage(lm.Language);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Force loc reload failed: {ex}");
        }

        try
        {
            ModConfigRegistry.Register(ModId, new NotEnoughDifficultyConfig());
        }
        catch (Exception ex)
        {
            Logger.Error($"ModConfigRegistry.Register failed: {ex}");
        }

        try
        {
            ExtraActsBootstrap.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Error($"ExtraActsBootstrap.Initialize failed: {ex}");
        }

        // 逐类隔离 patch：Harmony.PatchAll() 在某个 patch 类目标解析/应用失败时会抛异常并
        // **中断剩余 patch 的应用**——一个坏 patch 能让后面所有 patch（如 MapLengthPatch）全部失效。
        // 改成对每个类型单独 CreateClassProcessor().Patch() 并各自 try/catch：失败的只记日志、跳过，
        // 不影响其它 patch。非 patch 类（无 [HarmonyPatch]）的 processor.Patch() 是 no-op，安全。
        try
        {
            var harmony = new Harmony(ModId);
            foreach (var type in System.Reflection.Assembly.GetExecutingAssembly().GetTypes())
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Patch class {type.FullName} failed (skipped): {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Harmony patching failed: {ex}");
        }

        // 兼容性报告：把所有"关键 patch 点上的其它 mod"打一份出来，
        // 便于多 mod 环境下定位"功能为什么没生效"（放权逻辑见 ModCompat）。
        try
        {
            ModCompat.LogCompatibilityReport();
        }
        catch (Exception ex)
        {
            Logger.Error($"ModCompat report failed: {ex}");
        }

        // 注册全局速度控制器：每帧把 SpeedMultiplier config 同步到 Engine.TimeScale。
        //
        // 早期版本（v0.4.4 initial）继承 Node 用 _Process，因为 Godot Source Generator 给
        // partial class : Node 生成的 InvokeGodotClassMethod 跟 MonoMod 不兼容导致每帧抛
        // ArgumentException。改成 static class + SceneTree.ProcessFrame signal 完全绕开
        // Source Generator。
        //
        // 全段 try/catch：速度控制器即使失败也不该让整个 mod 挂掉。
        try
        {
            SpeedMultiplierController.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Error($"SpeedMultiplierController.Initialize failed: {ex}");
        }
    }
}