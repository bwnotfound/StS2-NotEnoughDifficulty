using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     跑图进度 + 难度强化的统一查询点（第二轮改造核心）。
///
/// ## 进度从哪来
/// <c>RunState.MapPointHistory</c>：每个已走过的地图节点一条 <see cref="MapPointHistoryEntry" />，
/// 带 <c>MapPointType</c>（Monster/Elite/Boss/…）和 <c>Rooms</c>（每场战斗的 <c>RoomType</c> + <c>ModelId</c>）。
/// 官方自己的 <c>ScoreUtility.GetElitesKilledCount(history)</c> 也是这么数的，所以这是权威口径。
///
/// ## 需求实现要点
/// - <b>act4 未打精英数</b>：act1~3 的精英池 − 历史里打过的精英 encounter → 差集大小 = act4 战斗房数
/// - <b>act5 未打 boss 池</b>：act1~3 的 boss 池 − 历史里打过的 boss  − act4 顶端 boss
///
/// ## 确定性（多人硬要求）
/// 全部计算只依赖 <c>RunState</c> 里已同步的数据（history / Acts / ActFloor），
/// <b>不引入任何新的随机源</b>——否则 host/client 地图不一致 → desync。
/// </summary>
internal static class RunProgress
{
    /// <summary>当前 act 序号（1~5）。认不出返回 -1。</summary>
    public static int GetActIndex(RunState? state) => GetActIndexFromModel(state?.Act, state);

    /// <summary>把 ActModel 实例映射到 1-based act 序号（用当前 run 的列表兜底）。认不出返回 -1。</summary>
    public static int GetActIndexFromModel(ActModel? act) =>
        GetActIndexFromModel(act, RunStateAccessor.GetCurrentState());

    /// <summary>
    ///     把 ActModel 实例映射到 1-based **层**号。认不出返回 -1。
    ///
    /// ## ⚠️ 这里**绝对不能**按 act 类型写死（用户实测抓到的 bug）
    /// 老实现是一张 <c>switch { Underdocks => 1, Overgrowth => 1, Hive => 2, Glory => 3, ... _ => -1 }</c>
    /// 表。后果：**别的模组的 act 变体一律 -1** ⇒ 该层的双 boss 开关、双 boss 之间的合成火堆、
    /// 难度按层强化……全部静默失效。用户原话：
    /// <i>"你硬编码了双重BOSS？我在模组里面的ACT ２变种没有看到双boss"</i>
    ///
    /// ## 三个数据来源（依次尝试，都不依赖具体类型）
    /// <list type="number">
    ///   <item><c>ModelDb.ActsByIndex</c> —— 游戏自己的"层 → 该层的全部 act 变体"分组，
    ///         是最权威的口径（base game 里层 1 = [Overgrowth, Underdocks]，
    ///         整幕模组把自己的 act 变体注册进哪一层，它就在哪一层）；</item>
    ///   <item>本局 <c>RunState.Acts</c> 里的位置 —— 整幕模组若只往 act 列表里 append
    ///         （不注册进 ActsByIndex），用它的运行位置当层号；</item>
    ///   <item><c>act.Index</c> —— act 自己声明的层号（base game 是 0-based：
    ///         Overgrowth/Underdocks=0、Hive=1、Glory=2，IL 实证）。</item>
    /// </list>
    /// 三条都认不出时打一条 warn（**不再静默 -1**）——这类"静默失效"正是上面那个 bug 藏了两轮的原因。
    /// </summary>
    public static int GetActIndexFromModel(ActModel? act, RunState? state)
    {
        if (act == null) return -1;

        // ① 游戏自己的层分组（同层多个 act 变体都在同一格里）
        try
        {
            var byIndex = ModelDb.ActsByIndex;
            for (var layer = 0; layer < byIndex.Count; layer++)
            {
                var variants = byIndex[layer];
                if (variants == null) continue;

                foreach (var candidate in variants)
                    if (ReferenceEquals(candidate, act)) return layer + 1;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读 ActsByIndex 判层失败（继续用其它口径）: {ex.Message}");
        }

        // ② 本局 act 列表里的位置
        try
        {
            var acts = state?.Acts;
            if (acts != null)
            {
                for (var i = 0; i < acts.Count; i++)
                    if (ReferenceEquals(acts[i], act)) return i + 1;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读 RunState.Acts 判层失败（继续用其它口径）: {ex.Message}");
        }

        // ③ act 自己声明的层号（base game 为 0-based；自定义 act 可能给 -1）
        try
        {
            if (act.Index >= 0) return act.Index + 1;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读 act.Index 判层失败: {ex.Message}");
        }

        MainFile.Logger.Warn(
            $"[RunProgress] 认不出 act '{act.Id?.Entry}' 属于第几层" +
            "（ActsByIndex / RunState.Acts / act.Index 三条口径都没有它）—— 该层的按层功能会跳过");
        return -1;
    }

    /// <summary>
    ///     遍历历史。注意 <c>RunState._mapPointHistory</c> 的真实类型是
    /// <c>List&lt;List&lt;MapPointHistoryEntry&gt;&gt;</c>——<b>按 act 分组的嵌套列表</b>
    /// （外层每个 act 一组，内层是该 act 走过的节点），所以必须两层遍历。
    /// 这一点是我从 IL 里读出来的（`get_MapPointHistory` 直接返回那个嵌套 List 字段），
    /// 单层遍历会编译报错/漏数据。
    /// </summary>
    private static void CollectFromHistory(RunState state, HashSet<string> ids)
    {
        var historyByAct = state.MapPointHistory;
        if (historyByAct == null) return;

        for (var act = 0; act < historyByAct.Count; act++)
        {
            var entries = historyByAct[act];
            if (entries == null) continue;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var rooms = entry?.Rooms;
                if (rooms == null) continue;

                for (var r = 0; r < rooms.Count; r++)
                {
                    var room = rooms[r];
                    if (room == null) continue;

                    // ⚠️ **不要**在这里加 RoomType 过滤（踩过坑）：
                    // 加了过滤会漏掉"房间类型被 mod 改过"的战斗（例如用 RoomType.Monster
                    // 造的 BOSS 房），打过的 boss 就会全部看不到。
                    // 只认 id：ModelId.Entry 就是那场遭遇的 encounter id（1 场战斗 = 1 个 id）。
                    var id = room.ModelId?.Entry;
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
        }
    }

    /// <summary>
    ///     读历史里**已经打过**的 encounter id 集合（**唯一口径**）。
    ///
    /// ## 判据：只认 id，不认房间类型
    /// <c>ModelId.Entry</c> 就是那场遭遇的 encounter id —— 1 场战斗 = 1 个 id，最准。
    ///
    /// ## 为什么不能加 <c>RoomType ∈ {Monster, Elite, Boss}</c> 过滤（踩过坑）
    /// 加了过滤会漏掉"房间类型被 mod 改过"的战斗（例如用 <c>RoomType.Monster</c>
    /// 造的 BOSS 房），打过的 boss 就会**全部看不到**。
    /// 判据只认 id、不认房间类型 → 不管房间被标成什么类型都能正确识别。
    ///
    /// ## 为什么不用 <c>MonsterIds</c>（本场出现过的敌人 id）
    /// 那个字段含召唤物/部件（例如女王场里的 <c>TORCH_HEAD_AMALGAM</c>），
    /// 拿它反查会误判（"打过部件"≠"打过本体"）。encounter id 才是 1:1 的权威口径。
    /// </summary>
    public static HashSet<string> GetDefeatedEncounterIds(RunState? state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (state == null) return ids;

        try
        {
            CollectFromHistory(state, ids);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[RunProgress] 读取 MapPointHistory 失败（按空集处理）: {ex}");
        }

        return ids;
    }

    /// <summary>
    ///     act1~3 全部精英池（去重，按 id）。
    ///     用 <c>ModelDb.ActsByIndex</c> 前 3 个 index，与 base game 的分层口径一致。
    /// </summary>
    /// <summary>
    ///     基础三层（act1~3）全部精英池（去重，按 id）。
    ///     走 <see cref="CollectByLayer" /> → 按 ActsByIndex 分组，含整幕类模组加进来的 act 变体。
    /// </summary>
    public static List<EncounterModel> GetAllBaseElites()
    {
        return CollectByLayer(act => act.AllEliteEncounters).SelectMany(x => x).ToList();
    }

    /// <summary>基础三层全部 boss 池（去重，按 id）。同样走 ActsByIndex，兼容整幕模组。</summary>
    public static List<EncounterModel> GetAllBaseBosses()
    {
        return CollectByLayer(act => act.AllBossEncounters).SelectMany(x => x).ToList();
    }

    /// <summary>按层分组的 boss 池：索引 0/1/2 对应第 1/2/3 层。用于"每层来一个"的分配。</summary>
    public static List<List<EncounterModel>> GetBaseBossesByAct()
    {
        return CollectByLayer(act => act.AllBossEncounters);
    }


    /// <summary>
    ///     **进入当前幕之前**打过的 encounter 集合（历史是按 act 分组的，见 <see cref="CollectFromHistory" />）。
    ///
    /// ## 为什么需要这个口径（踩过：名单漂移）
    /// <see cref="GetDefeatedEncounterIds" /> 是"到目前为止打过的全部"——
    /// 用它来排"第 5 幕要打哪些 boss"会**边打边变**：每打赢一个，名单就少一个，
    /// 于是地图上节点的名字/内容与实际房间对不上（`CELEMONIAL_BEAST` 那类 bug 的温床），
    /// 读档续玩时重建更是整份名单换掉。
    /// 用"进入本幕之前"的战绩 ⇒ 名单在整幕内**恒定**，读到同一份存档也恒定。
    /// </summary>
    public static HashSet<string> GetDefeatedEncounterIdsBeforeCurrentAct(RunState? state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (state == null) return ids;

        try
        {
            var historyByAct = state.MapPointHistory;
            if (historyByAct == null) return ids;

            var currentAct = state.CurrentActIndex;      // 0-based
            for (var act = 0; act < historyByAct.Count && act < currentAct; act++)
            {
                var entries = historyByAct[act];
                if (entries == null) continue;

                foreach (var entry in entries)
                {
                    var rooms = entry?.Rooms;
                    if (rooms == null) continue;

                    foreach (var room in rooms)
                    {
                        var id = room?.ModelId?.Entry;
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读「本幕之前」的战绩失败（按空集处理）: {ex.Message}");
        }

        return ids;
    }

    /// <summary>
    ///     这个 encounter 属于**哪一个 act**（不是"第几层"）。
    ///
    /// ## 为什么必须精确到 act，而不是"层代表 act"（用户实测反馈）
    /// 用户："<i>为什么 ACT 1 的 2 个子变种层级一样的颜色？</i>"
    /// —— base game 的**同一层可以有多个 act**：层 1 = Overgrowth **和** Underdocks
    /// （IL 实证：`ModelDb.ActsByIndex[0]` 里两个 act，各有 3 个精英）。
    /// 之前用 <see cref="GetRepresentativeAct" />（层里第一个 act）当配色来源，
    /// 于是 Underdocks 的精英被染成了 Overgrowth 的颜色 —— 两个子变体看起来一模一样。
    ///
    /// 这里按"谁的池子里有这个 encounter"反查，精确到 act ⇒ 每个变体用自己的颜色/背景。
    /// 扫描顺序 = 层 1→3、层内按 <c>ActsByIndex</c> 顺序、池内按池顺序（确定，两端一致）；
    /// 精英池优先于 boss 池（实践中两者不重叠）。整幕类模组塞进同层的 act 变体自动纳入。
    /// </summary>
    public static ActModel? FindActOfEncounter(string? encounterId, out int layer)
    {
        layer = -1;
        if (string.IsNullOrEmpty(encounterId)) return null;

        var state = RunStateAccessor.GetCurrentState();

        // ── ① 铁证：本局地图历史里，这个 encounter 是在**哪一幕**打的 ──
        //    MapPointHistory 的外层就是"按 act 分组"（见 CollectFromHistory 的 IL 说明），
        //    所以这是**游戏自己记下来的**归属，比"池子里搜到谁"精确得多。
        //    用户口径："走得不是模组自带的，而是荣耀三层" —— 池子里同时挂着模组 act 与荣耀时，
        //    老实现按 ActsByIndex 顺序取**第一个**，于是模组精英被判成了荣耀。
        //    注意跳过没打的精英在这里没有记录，会落到 ②。
        var home = ActFromHistory(state, encounterId!);
        if (home != null)
        {
            layer = GetActIndexFromModel(home, state);
            return home;
        }

        // ── ② 池子扫描：**收集全部候选**再挑 ──
        //    ⚠️ 必须排除**本模组自己的两个幕**：它们的池子是"聚合出来的"
        //    （act4 的 AllEncounters = 第三层所有 act 的并集），任何第三层精英都能在它们池子里搜到。
        //    把它们当候选会让"来源幕"解析成我们自己 ⇒ 背景折返变成自我递归
        //    （实测：日志 9.7MB 全是同一组行，游戏直接崩掉）。
        var candidates = new List<ActModel>();
        foreach (var act in AllActsForScan())
        {
            if (act is Act4Model or Act5Model) continue;

            if (PoolContains(act, a => a.AllEliteEncounters, encounterId!) ||
                PoolContains(act, a => a.AllBossEncounters, encounterId!) ||
                PoolContains(act, a => a.AllEncounters, encounterId!))
                candidates.Add(act);
        }

        if (candidates.Count == 0) return null;

        // 多个 act 都挂着它（典型：模组把自家精英同时注册进基础 act 与自家 act）：
        // 优先**非默认 act**（`IsDefault == false` = 模组 act / 变体 act），
        // 因为"模组自己注册的精英"理应归它自己的那一幕；基础 act（IsDefault=true）排后面。
        var chosen = candidates.FirstOrDefault(a => !SafeIsDefault(a)) ?? candidates[0];

        if (candidates.Count > 1)
        {
            MainFile.DebugLog(
                $"[RunProgress] '{encounterId}' 有 {candidates.Count} 个候选 act：" +
                string.Join(" / ", candidates.Select(a => $"{a.GetType().Name}{(SafeIsDefault(a) ? "(默认)" : "")}")) +
                $" ⇒ 取 '{chosen.GetType().Name}'");
        }

        layer = GetActIndexFromModel(chosen, state);
        return chosen;
    }

    /// <summary>地图历史里这个 encounter 归属的 act（跳过本模组自己的幕；没记录返回 null）。</summary>
    private static ActModel? ActFromHistory(RunState? state, string encounterId)
    {
        try
        {
            var historyByAct = state?.MapPointHistory;
            var acts = state?.Acts;
            if (historyByAct == null || acts == null) return null;

            for (var a = 0; a < historyByAct.Count; a++)
            {
                var actModel = a < acts.Count ? acts[a] : null;
                // 本模组自己的幕不作数：那里面装的本来就是"别人的敌人"（复用的池子）
                if (actModel == null || actModel is Act4Model or Act5Model) continue;

                var entries = historyByAct[a];
                if (entries == null) continue;

                foreach (var entry in entries)
                {
                    var rooms = entry?.Rooms;
                    if (rooms == null) continue;

                    foreach (var room in rooms)
                        if (string.Equals(room?.ModelId?.Entry, encounterId, StringComparison.Ordinal))
                            return actModel;
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 从历史判定 '{encounterId}' 归属失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>扫描用的 act 全集：ActsByIndex 各层（层号小的优先）+ 总表里的追加式 act。</summary>
    private static List<ActModel> AllActsForScan()
    {
        var seen = new List<ActModel>();

        try
        {
            var byIndex = ModelDb.ActsByIndex;
            for (var i = 0; i < byIndex.Count; i++)
            {
                var variants = byIndex[i];
                if (variants == null) continue;
                foreach (var act in variants)
                    if (act != null && !seen.Contains(act)) seen.Add(act);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读 ActsByIndex 失败（继续用总表）: {ex.Message}");
        }

        try
        {
            foreach (var act in ModelDb.Acts)
                if (act != null && !seen.Contains(act)) seen.Add(act);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunProgress] 读 ModelDb.Acts 失败: {ex.Message}");
        }

        return seen;
    }

    private static bool SafeIsDefault(ActModel act)
    {
        try { return act.IsDefault; } catch { return false; }
    }

    private static bool PoolContains(ActModel act, Func<ActModel, IEnumerable<EncounterModel>> selector, string id)
    {
        try
        {
            foreach (var e in selector(act))
                if (string.Equals(e?.Id?.Entry, id, StringComparison.Ordinal)) return true;
        }
        catch
        {
            // 单个 act 取池异常 → 当作不含
        }

        return false;
    }

    /// <summary>
    ///     未打过的精英（旧口径，act4 已不再使用；保留给其它玩法/诊断查询）。
    ///     = act1~3 精英池 − 已打过；已打过集合里可能有 act4/5 的 encounter（也复用 1~3 层内容），一并计入差集。
    /// </summary>
    public static List<EncounterModel> GetUnvisitedElites(RunState? state)
    {
        var defeated = GetDefeatedEncounterIds(state);
        return GetAllBaseElites()
            .Where(e => e?.Id?.Entry is { } id && !defeated.Contains(id))
            .ToList();
    }

    /// <summary>
    ///     未打过的 boss（供 act5 编排），已排除 act4 顶端 boss。
    ///     <paramref name="excludeAct4Boss" /> = true 时把当前 act4 的顶端 boss 也从池里剔除
    ///     （需求："第四层的BOSS是随机抽取的，因此也要记得减"）。
    /// </summary>
    public static List<EncounterModel> GetUnvisitedBosses(RunState? state, bool excludeAct4Boss)
    {
        var defeated = GetDefeatedEncounterIds(state);

        // act4 的顶端 boss 可能还没打过（玩家在 act4 中途），它不在 history 里，需要单独排除
        string? act4BossId = null;
        if (excludeAct4Boss)
        {
            try
            {
                act4BossId = state?.Acts?
                    .OfType<Act4Model>()
                    .FirstOrDefault()?
                    .BossEncounter?.Id?.Entry;
            }
            catch
            {
                // 取不到就当没有
            }
        }

        return GetAllBaseBosses()
            .Where(e =>
            {
                var id = e?.Id?.Entry;
                if (string.IsNullOrEmpty(id)) return false;

                // act4 顶端 boss 单独排除（玩家在 act4 中途时它还没进 history）
                if (string.Equals(id, act4BossId, StringComparison.Ordinal)) return false;

                // 唯一口径：encounter id 在"已打集合"里 → 打过
                return !defeated.Contains(id);
            })
            .ToList();
    }

    /// <summary>
    ///     取"基础三层"的 act 列表（索引 0/1/2 → 第 1/2/3 层）。
    ///
    /// ## 为什么不用 <c>ModelDb.Act&lt;Overgrowth&gt;()</c> 这类按具体类型取
    /// 装了整幕类模组（如 ActsFromThePast「塔1回归」）后，act 列表里会出现<b>更多 act 变体</b>
    /// （同一层可能有多个不同 act）。按固定类型取只会拿到基础那三个，
    /// <b>模组塞进来的 act 的 boss/精英池就永远进不了我们的池子</b>——这会让玩家的整合包玩法失效。
    /// 所以统一走 <c>ModelDb.ActsByIndex</c>：它按"层"分组，天然把同层的所有变体（含模组加的）都算进来。
    ///
    /// ## 兼容性
    /// - 某一层没有任何 act → 该层池子为空，不影响其它层
    /// - 模组新增的 act 若挂在索引 0/1/2 → 自动被纳入对应层
    /// - 模组加的"第 4/5/6 幕"（索引 ≥ 3）<b>不计入</b>基础三层池——它们的 boss 不该被我们抽走
    ///   （避免把别的模组的专属 boss 拉到 act4/5 里来，那会破坏那个模组的进度设计）
    /// </summary>
    private static List<IReadOnlyList<ActModel>> GetBaseLayers()
    {
        var result = new List<IReadOnlyList<ActModel>>();
        try
        {
            var byIndex = ModelDb.ActsByIndex;
            for (var i = 0; i < 3 && i < byIndex.Count; i++)
                result.Add(byIndex[i] ?? Array.Empty<ActModel>());
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[RunProgress] 读取 ActsByIndex 失败（按空处理）: {ex}");
        }

        return result;
    }

    /// <summary>把一个内容池按"层"分组收集（去重，按 id）。忽略索引 ≥ 3 的 act。</summary>
    private static List<List<T>> CollectByLayer<T>(
        Func<ActModel, IEnumerable<T>> selector)
        where T : class
    {
        var result = new List<List<T>> { new(), new(), new() };
        var layers = GetBaseLayers();

        for (var i = 0; i < layers.Count && i < 3; i++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var act in layers[i])
            {
                if (act == null) continue;
                try
                {
                    foreach (var item in selector(act))
                    {
                        if (item == null) continue;
                        // 有 ModelId 的按 id 去重（encounter / event / ancient 都有）
                        var id = (item as AbstractModel)?.Id?.Entry;
                        if (id == null) { result[i].Add(item); continue; }
                        if (seen.Add(id)) result[i].Add(item);
                    }
                }
                catch
                {
                    // 单个 act 取池异常 → 跳过该 act，不影响其它 act / 其它层
                }
            }
        }

        return result;
    }

    // ============================================================
    // 难度强化公式（需求5）
    // ============================================================

    /// <summary>
    ///     当前层是否启用额外强化。
    ///     1~3 层默认关闭（与原版一致），4~5 层默认开启——由配置项决定。
    /// </summary>
    public static bool IsExtraScalingEnabled(int actIdx)
    {
        return actIdx switch
        {
            1 => NotEnoughDifficultyConfig.Act1_ExtraScaling,
            2 => NotEnoughDifficultyConfig.Act2_ExtraScaling,
            3 => NotEnoughDifficultyConfig.Act3_ExtraScaling,
            4 => NotEnoughDifficultyConfig.Act4_ExtraScaling,
            5 => NotEnoughDifficultyConfig.Act5_ExtraScaling,
            _ => false,
        };
    }

    /// <summary>
    ///     血量倍率 = 1 + ActFloor × 0.1 × Y（<b>百分点</b>，与配置本地化文案一致）。
    ///
    /// ⚠️ <b>2026-09-23 修（用户："难度疑似没有提升怪物血量"）</b>：旧实现写成
    /// <c>1 + ActFloor × 0.1 × Y / 100</c>，于是 Y=1、第 10 层只得到 <b>+1%</b>，
    /// 而配置界面（<c>NOTENOUGHDIFFICULTY-HP_SCALE_FACTOR.description</c>）白纸黑字写着
    /// "第 10 层 → 10×0.1×1 = <b>+100%</b>，即血量翻倍"。**代码与文案差 100 倍** ⇒ 玩家配了 Y=1
    /// 却几乎看不到强化。现在按文案来：<c>1 + ActFloor × 0.1 × Y</c>。
    ///
    /// 该层未启用强化时返回 1.0（完全不改原值）。上限 clamp 到 100 倍，防呆
    /// （ActFloor 万一被外部写成异常大值时不至于把血量乘到溢出）。
    /// </summary>
    public static double GetHpMultiplier(RunState? state)
    {
        // ⚠️ 用"配置槽位"而不是绝对层号：本模组的两个幕被顺延到第 5/6 幕时，
        //    Act4_/Act5_ 开关仍要对**本模组的幕**生效（见 ActLayout）。
        var actIdx = ActLayout.ConfigLayerOf(state?.Act, state);
        if (actIdx < 1 || !IsExtraScalingEnabled(actIdx)) return 1.0;

        var floor = Math.Max(0, state!.ActFloor);
        var y = NotEnoughDifficultyConfig.HpScaleFactor;
        var mult = 1.0 + floor * 0.1 * y;      // 第 10 层、Y=1 → 2.0（+100%）
        if (mult < 1.0) mult = 1.0;
        if (mult > MaxExtraMultiplier) mult = MaxExtraMultiplier;
        return mult;
    }

    /// <summary>
    ///     攻击倍率 = 1 + ActFloor × 0.05 × X（<b>百分点</b>，同 <see cref="GetHpMultiplier" /> 的量纲修正）。
    ///     该层未启用强化时返回 1.0。
    /// </summary>
    public static double GetDmgMultiplier(RunState? state)
    {
        var actIdx = ActLayout.ConfigLayerOf(state?.Act, state);   // 配置槽位（见 GetHpMultiplier 注释）
        if (actIdx < 1 || !IsExtraScalingEnabled(actIdx)) return 1.0;

        var floor = Math.Max(0, state!.ActFloor);
        var x = NotEnoughDifficultyConfig.DmgScaleFactor;
        var mult = 1.0 + floor * 0.05 * x;     // 第 10 层、X=1 → 1.5（+50%）
        if (mult < 1.0) mult = 1.0;
        if (mult > MaxExtraMultiplier) mult = MaxExtraMultiplier;
        return mult;
    }

    /// <summary>额外强化的上限（防呆：ActFloor 被外部写成异常大值时不至于把血量乘爆）。</summary>
    private const double MaxExtraMultiplier = 100.0;

    /// <summary>
    ///     诊断用：把当前进度快照打成一行日志。
    ///     首次实现时用于**实测 ActFloor / TotalFloor 的真实语义与量级**（不靠猜），
    ///     同时也给 desync 排查留下两端可比对的指纹。
    /// </summary>
    public static void LogProgressSnapshot(string where)
    {
        try
        {
            var rm = RunManager.Instance;
            var state = rm == null ? null : RunStateAccessor.GetState(rm);
            if (state == null) return;

            MainFile.DebugLog(
                $"[RunProgress/{where}] act={GetActIndex(state)} ActFloor={state.ActFloor} " +
                $"TotalFloor={state.TotalFloor} history={state.MapPointHistory.Count} " +
                $"hpMult={GetHpMultiplier(state):F3} dmgMult={GetDmgMultiplier(state):F3} " +
                $"unvisitedElites={GetUnvisitedElites(state).Count} " +
                $"unvisitedBosses(exclAct4)={GetUnvisitedBosses(state, true).Count}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[RunProgress/{where}] snapshot failed: {ex}");
        }
    }

    /// <summary>
    ///     未打精英里的<b>主导来源层</b>（1/2/3）——act4 用它决定视觉主题。
    /// 取不到的层不计；全取不到时返回 2（Hive，= 原来的固定背景，保证有兜底）。
    /// 纯函数、只用同步数据 → 多人两端一致。
    /// </summary>
    public static int GetDominantEliteSourceAct(RunState? state)
    {
        try
        {
            var byAct = GetBaseElitePoolsByAct();
            var unvisited = GetUnvisitedElites(state);
            if (unvisited.Count == 0) return 2;

            var wanted = new HashSet<string>(unvisited.Select(e => e.Id.Entry), StringComparer.Ordinal);
            var counts = new int[4]; // 1..3
            for (var i = 0; i < byAct.Count && i < 3; i++)
                foreach (var e in byAct[i])
                    if (wanted.Contains(e.Id.Entry)) counts[i + 1]++;

            var best = 2;
            for (var act = 1; act <= 3; act++)
                if (counts[act] > counts[best]) best = act;

            MainFile.DebugLog(
                $"[Act4] 主导来源层 = act{best}（未打精英分布 act1={counts[1]} act2={counts[2]} act3={counts[3]}）");
            return best;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[RunProgress] 计算主导来源层失败（回退 act2）: {ex}");
            return 2;
        }
    }

    /// <summary>
    ///     把某一层所有 act 的某个属性聚合（去重、保序、单个 act 异常则跳过）。layer 为 1-based。
    ///     这是替代 <c>ModelDb.Act&lt;Overgrowth&gt;()</c> 这类写法的统一入口 —— 走 ActsByIndex，
    ///     所以整幕类模组往同一层塞的 act 变体也会被纳入。
    /// </summary>
    public static List<T> CollectFromLayer<T>(int layer, Func<ActModel, IEnumerable<T>> selector)
        where T : class
    {
        if (layer < 1) return new List<T>();
        var all = CollectByLayer(selector);
        var idx = layer - 1;
        return idx < all.Count ? all[idx] : new List<T>();
    }

    /// <summary>
    ///     一个"代表 act"，用于取与具体 act 无关的展示资源（地图背景 / 篝火场景）。
    ///     优先该层的第一个 base act；该层为空则回退任一层；最终回退 null。
    /// </summary>
    public static ActModel? GetRepresentativeAct(int layer)
    {
        try
        {
            var layers = GetBaseLayers();
            var idx = layer - 1;
            if (idx >= 0 && idx < layers.Count)
                foreach (var act in layers[idx])
                    if (act != null) return act;

            foreach (var l in layers)
                foreach (var act in l)
                    if (act != null) return act;
        }
        catch
        {
            // 取不到 → 回退 null，调用方用固定兜底
        }

        return null;
    }
    /// <summary>
    ///     基础三层（含整幕模组塞进同层的 act 变体）**全部本地化音乐 bank**（去重、保序）。
    ///     供 act4/5 的 <c>MusicBankPaths</c> 用 —— 不写死 <c>res://banks/...</c>，
    ///     否则别的模组换了 bank 布局、或给自家 act 挂了额外 bank 时就会漏加载（BOSS 战没音乐）。
    ///     单 act 取属性异常只跳过该 act。
    /// </summary>
    public static string[] CollectMusicBanksForBaseLayers()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layer in new[] { 1, 2, 3 })
        {
            foreach (var act in GetLayerActs(layer))
            {
                try
                {
                    foreach (var bank in act.MusicBankPaths)
                        if (!string.IsNullOrEmpty(bank) && seen.Add(bank)) result.Add(bank);
                }
                catch
                {
                    // 单个 act 取 bank 失败 → 跳过
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>某一层（1-based）的全部 base act 实例（含模组加的变体）。</summary>
    public static IReadOnlyList<ActModel> GetLayerActs(int layer)
    {
        var layers = GetBaseLayers();
        var idx = layer - 1;
        return idx >= 0 && idx < layers.Count ? layers[idx] : Array.Empty<ActModel>();
    }

    /// <summary>按层分组的精英池：索引 0/1/2 → 第 1/2/3 层（兼容整幕模组加的 act 变体）。</summary>
    public static List<List<EncounterModel>> GetBaseElitePoolsByAct()
    {
        return CollectByLayer(act => act.AllEliteEncounters);
    }

    // ---------- 内部 ----------

    private static List<EncounterModel> CollectBaseEncounters(
        Func<ActModel, IEnumerable<EncounterModel>> selector)
    {
        var result = new List<EncounterModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var byIndex = ModelDb.ActsByIndex;
            for (var i = 0; i < 3 && i < byIndex.Count; i++)
            {
                foreach (var act in byIndex[i])
                {
                    if (act == null) continue;
                    try
                    {
                        foreach (var e in selector(act))
                        {
                            if (e == null) continue;
                            var id = e.Id?.Entry;
                            if (!string.IsNullOrEmpty(id) && seen.Add(id)) result.Add(e);
                        }
                    }
                    catch
                    {
                        // 单个 act 取池异常 → 跳过
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[RunProgress] 收集 base encounter 池失败: {ex}");
        }

        return result;
    }
}
