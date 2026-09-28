using Godot;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     全局游戏速度控制器：把 <see cref="NotEnoughDifficultyConfig.SpeedMultiplier" /> 应用到
///     <see cref="Godot.Engine.TimeScale" />，实现整个 Godot 引擎层面的统一加速。
///     ## 为什么是 static class 而不是 Node 子类（重要的历史背景）
///     早期版本（v0.4.4 initial）是 <c>partial class SpeedMultiplierController : Node</c>，每帧用
///     <c>_Process</c> 同步 config。运行时炸：
///     <code>
/// System.ArgumentException: Value does not fall within the expected range.
///   at MonoMod.Core.Interop.CoreCLR.V60.InvokeCompileMethod(...)
///   at NotEnoughDifficulty.NotEnoughDifficultyCode.SpeedMultiplierController.InvokeGodotClassMethod(...)
///   at Godot.Bridge.CSharpInstanceBridge.Call(...)
/// </code>
///     <b>根因</b>：Godot Source Generator 会为<b>每个</b> <c>partial class : GodotObject</c>（含 Node 子类）
///     自动生成 <c>InvokeGodotClassMethod</c> / <c>HasGodotClassMethod</c> / <c>SetGodotClassPropertyValue</c>
///     等 method dispatcher。这些 method 用 <c>ref godot_string_name</c> 这种含 <c>byref</c> 到 unmanaged
///     struct 的 signature——<b>MonoMod 的 JIT hook（Harmony 底层）处理不了</b>，在
///     <c>CompileMethodHook</c> 阶段抛 ArgumentException，从此该 class 的所有调用都炸。
///     因为我们 mod 用 Harmony，整个进程的 JIT 被 hook，所以任何继承 GodotObject 的新 partial class
///     都会触发这个问题——这是 BepInEx-style mod + Godot C# 的<b>已知不兼容</b>。
///     <b>解决</b>：完全不继承 Node。改用 <c>SceneTree.ProcessFrame</c> signal 实现每帧回调——
///     signal 连接走 Godot 的 delegate 机制，<b>不依赖 Source Generator 给我们的类生成代码</b>。
///     ## 通过 Engine.TimeScale 的核心机制（跟之前一致）
///     <c>Engine.TimeScale</c> 是 Godot 引擎层面的全局乘数，自动乘到：
///     - <c>SceneTreeTimer</c>（<c>Cmd.Wait</c> 的底层实现）
///     - <c>Tween</c>（所有卡牌动画 / 移动 / 淡入淡出）
///     - <c>AnimationPlayer</c>（角色 / 怪物 spine 动画）
///     - <c>_Process / _PhysicsProcess</c> 的 delta（任何基于时间的逻辑）
///     <b>不影响</b>：真实时间（系统时间）、UI 输入响应（Godot 输入事件实时分发）、网络包发送时序、
///     <b>FMOD 音频</b>（base game 用 FMOD Studio，所有 <c>event:/...</c> 路径都走 FMOD 独立音频线程，
///     FMOD 用 wall-clock 时间播放——音效和 BGM 都保持原速，不会出现"音调升高"的加速感）。
///     ## 跟 FastMode 的关系（独立叠加）
///     我们不动 <c>SaveManager.Instance.PrefsSave.FastMode</c>，它仍按用户原设置在各处独立判断。
///     例如 <c>Cmd.CustomScaledWait(0.2f, 0.3f)</c>：
///     - FastMode=Fast → 选 0.2 秒
///     - 我们 SpeedMultiplier=2.0 → Engine.TimeScale=2.0
///     - 实际玩家感受：0.2 / 2.0 = 0.1 秒
///     ## 动态生效
///     通过 <see cref="OnProcessFrame" /> 每帧检查 config 跟 Engine.TimeScale 是否一致——不一致就更新。
///     拖 slider &lt; 16ms 内生效。其他 patch 通过 <c>NotEnoughDifficultyConfig.SpeedMultiplier = X</c>
///     直接修改也立即生效。
///     ## 不参与联机同步（按设计）
///     <c>EnableSpeedMultiplier</c> / <c>SpeedMultiplier</c> 标记了 <c>[ConfigSyncIgnore]</c>——
///     host 不广播，client 不被覆盖。每个玩家独立设置自己看到的速度。
///     这是安全的：Engine.TimeScale 是纯客户端表现层，影响本地动画/timer 速度，跟游戏 state 演进无关：
///     - <c>ChecksumTracker</c> 触发点都是回合事件（"After player turn start" 等），不依赖时间
///     - 网络消息走 <c>Time.GetTicksMsec()</c>（wall-clock），不受 TimeScale 影响
///     - <c>PeerInputSynchronizer</c> / <c>HeartbeatTracker</c> 也走 wall-clock
///     各玩家本地看到的速度不同 = 各自看到动画跑得快/慢，但事件最终都触发、state 同步。
///     ## 边界保护
///     Engine.TimeScale &lt;= 0 会让游戏卡死或异常。clamp 到 [0.01, 100]。
/// </summary>
public static class SpeedMultiplierController
{
    private const double MinTimeScale = 0.01;
    private const double MaxTimeScale = 100.0;
    private const double Epsilon = 1e-4;

    private static bool _initialized;

    /// <summary>
    ///     保持对 OnProcessFrame 委托的强引用——signal 内部用 <c>WeakRef</c> 风格管理 callback 时
    ///     可能让 callback 被 GC，提前持有保险。也方便后续如果需要 disconnect 时取出。
    /// </summary>
    private static Action? _processCallback;

    /// <summary>
    ///     由 <c>MainFile.Initialize</c> 在 Harmony.PatchAll 之后调用。
    ///     幂等：重复 Initialize 不会重复注册。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree == null)
            {
                MainFile.Logger.Warn(
                    "SpeedMultiplierController.Initialize: Engine.GetMainLoop() is not SceneTree, " +
                    "speed multiplier won't take effect");
                return;
            }

            _processCallback = OnProcessFrame;
            sceneTree.ProcessFrame += _processCallback;
            _initialized = true;
            MainFile.Logger.Info("SpeedMultiplierController initialized (via SceneTree.ProcessFrame)");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"SpeedMultiplierController.Initialize failed: {ex}");
        }
    }

    /// <summary>
    ///     每帧由 SceneTree 触发。极轻量：两次 double 比较 + 偶尔一次赋值。
    ///     不能抛——signal callback 抛会污染 Godot 主循环。整段包 try/catch 兜底。
    ///
    /// ## ★ 2026-09-23 修：「进火堆后直接卡死」的元凶之一（不跟定帧 mod 抢 TimeScale）
    /// 旧实现是**每帧无条件**把 <c>Engine.TimeScale</c> 拉回自己的目标值。别的做"定帧 / hit-stop"
    /// 的 mod 会在这段时间把 TimeScale 置 0，于是变成**每帧互相覆盖**的拉锯：
    /// <code>
    /// [HitStop] 检测到 TimeScale 被修改为 1.4，强制恢复为 0     ← 实测日志里成片出现
    /// [HitStop] 检测到 TimeScale 被修改为 1.4，强制恢复为 0
    /// </code>
    /// 拉锯的后果不只是日志刷屏：**一旦 0 留在引擎里**，Godot 的 <c>Tween</c> / <c>SceneTreeTimer</c> /
    /// <c>AnimationPlayer</c> 全部停摆（它们都乘 TimeScale），而"进房 / 进火堆"的演出正是靠这些推进的
    /// ⇒ 画面停住不动 = 玩家说的"直接卡死"（输入还在，但流程永远走不完）。
    ///
    /// 现在的策略：**别人定帧（TimeScale ≤ <see cref="MinTimeScale" />）时一律不抢**，
    /// 让 hit-stop 正常播完、由它自己恢复；只在"定帧持续超过 <see cref="UnwedgeMs" /> 毫秒"
    /// （= 对面没恢复，游戏真的被卡在 0）时才强制拉回目标值并打一条 Warn 兜底。
    /// </summary>
    private static void OnProcessFrame()
    {
        try
        {
            double target;
            if (NotEnoughDifficultyConfig.EnableSpeedMultiplier
                && NotEnoughDifficultyConfig.Enabled)
            {
                target = NotEnoughDifficultyConfig.SpeedMultiplier;
                if (target < MinTimeScale) target = MinTimeScale;
                if (target > MaxTimeScale) target = MaxTimeScale;
            }
            else
            {
                target = 1.0;
            }

            var current = Engine.TimeScale;

            // ① 有人在做定帧（TimeScale 被压到 ~0）：**不抢**，只计时
            if (current <= MinTimeScale && Math.Abs(target - current) > Epsilon)
            {
                var now = Time.GetTicksMsec();
                if (_frozenSinceMsec == 0)
                {
                    _frozenSinceMsec = now;
                    MainFile.DebugLog(
                        $"[Speed] 检测到定帧（TimeScale={current:F3}）⇒ 暂停接管，等对面恢复");
                    return;
                }

                // ② 兜底：定帧久到不像定帧（对面没恢复）⇒ 强制拉回，避免整个游戏停摆
                if (now - _frozenSinceMsec > UnwedgeMs)
                {
                    _frozenSinceMsec = 0;
                    Engine.TimeScale = target;
                    MainFile.Logger.Warn(
                        $"[Speed] 定帧持续超过 {UnwedgeMs} ms（TimeScale 停在 {current:F3}）⇒ 强制恢复到 {target:F2}" +
                        "（防止游戏时间被永久冻住）");
                }

                return;
            }

            _frozenSinceMsec = 0;

            // ③ 常规：只在目标值确实不同时写一次（配置改动 < 16ms 内生效）
            if (Math.Abs(current - target) > Epsilon) Engine.TimeScale = target;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"SpeedMultiplierController.OnProcessFrame failed: {ex}");
        }
    }

    /// <summary>定帧开始的时刻（ms）；0 = 当前没有定帧。</summary>
    private static ulong _frozenSinceMsec;

    /// <summary>定帧持续多久就认为"对面没恢复、必须兜底"（毫秒）。正常 hit-stop 远小于它。</summary>
    private const double UnwedgeMs = 3000.0;
}