using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     **我们自己写的地图 BOSS 节点**（用户口径：<i>"直接写一个新类…去复刻"</i>）。
///
/// ## 为什么必须自己写
/// 原版 <c>NBossMapPoint._Ready</c> 的图标是这么定的（IL 实测）：
/// <code>
///   e = (Point == map.SecondBossMapPoint) ? Act.SecondBossEncounter : Act.BossEncounter
/// </code>
/// 也就是说**整张地图只有两个 BOSS 图标槽位**，节点自己说了不算。
/// ACT5 有三个 BOSS 图标节点（灵魂异鱼 / 知识恶魔 / 最终BOSS），
/// 用原版节点必然"串图标"（两个伪装 BOSS 显示同一个图标）—— 做不到"各显各得"。
/// 所以我们复刻一个：**图标由节点自己携带的 encounter 决定**。
///
/// ## 复刻范围（对照原版 <c>NBossMapPoint</c> 逐项自己实现）
/// <list type="table">
///   <item><term>场景</term><description>美术子树从原版场景 <c>ui/boss_map_point</c> 搬过来
///         （**只搬美术**：donor 从不入树，所以原版 <c>_Ready</c> 永远不会跑）</description></item>
///   <item><term>取图标</term><description>自己写：有 spine 骨架 → 用骨架；否则用
///         <c>BossNodePath + ".png" / "_outline.png"</c> 贴图</description></item>
///   <item><term>着色</term><description>自己写：spine 走 shader 参数 <c>map_color</c>/<c>black_layer_color</c>，
///         贴图走 <c>SelfModulate</c></description></item>
///   <item><term>信号</term><description>自己接：原版 <c>NMapPoint.ConnectSignals</c> 会
///         <c>GetNode("%MapPointVoteContainer")</c>，我们用自己认领的引用</description></item>
///   <item><term>点击出发</term><description>复用基类被 <c>sealed</c> 的
///         <c>NMapPoint.OnRelease()</c>（原版"点节点就出发"的那段，无法也不需要重写），
///         按压/释放的手感按原版 <c>NClickableControl</c> 的判定逐条复刻</description></item>
/// </list>
///
/// ## 明确不做
/// 不引 <c>NBossMapPoint</c> 这个类（连场景路径都自己写字面量），
/// 不碰 <c>SecondBossEncounter</c> / N10 双重 BOSS 机制。
/// </summary>
internal sealed partial class Act5BossNode : NMapPoint
{
    /// <summary>原版 <c>NBossMapPoint.BossMapPointPath</c> 的字面量（自己写，不反射那个类）。</summary>
    private const string BossSceneKey = "ui/boss_map_point";

    private static readonly StringName MapColorParam = new("map_color");
    private static readonly StringName BlackLayerParam = new("black_layer_color");
    private static readonly StringName FocusStylebox = new("focus");

    // ── 全部是自己的字段（原版那些 private 字段我们碰不到，也不需要碰）──
    private ActModel? _act;
    private EncounterModel? _encounter;
    private bool _usesSpine;
    private Node2D? _spriteContainer;
    private Node2D? _spineSprite;
    private MegaSprite? _animController;
    private ShaderMaterial? _material;
    private TextureRect? _iconImage;
    private TextureRect? _iconOutline;
    private Tween? _scaleTween;

    /// <summary>本节点要显示的 encounter（★ 各显各得的关键：图标跟着它走）。</summary>
    internal EncounterModel? Encounter => _encounter;

    /// <summary>本节点坐标（日志/调试用）。</summary>
    internal MapCoord Coord => Point?.coord ?? default;

    // ============================================================
    // 造节点（自己 new + 自己搬美术子树，全程不碰原版节点类）
    // ============================================================

    internal static Act5BossNode Create(MapPoint point, NMapScreen screen, IRunState runState,
        EncounterModel encounter)
    {
        var node = new Act5BossNode
        {
            Name = $"Act5BossNode_{point.coord.col}_{point.coord.row}",
            Point = point,
            _screen = screen,
            _runState = runState,
            _act = runState.Act,
            _encounter = encounter
        };

        node.GraftSceneVisuals();
        return node;
    }

    /// <summary>
    ///     把原版 BOSS 节点场景的**美术子树**搬到自己身上。
    ///
    /// donor 永远不入树 ⇒ 原版脚本的 <c>_Ready</c> 不会执行（图标也就不会被它按槽位覆盖）。
    /// 引用在搬运**之前**从 donor 身上认领（那时它自己的"唯名表"还是完好的），
    /// 搬完以后引用依然指向同一批节点。
    /// </summary>
    private void GraftSceneVisuals()
    {
        var scene = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath(BossSceneKey));
        if (scene == null)
        {
            MainFile.Logger.Error($"[Act5BossNode] 取不到场景 '{BossSceneKey}'，节点将没有美术");
            return;
        }

        var donor = scene.Instantiate();
        if (donor == null)
        {
            MainFile.Logger.Error($"[Act5BossNode] 场景 '{BossSceneKey}' 实例化失败");
            return;
        }

        // 布局参数照搬（尺寸/pivot 我们自己不猜，路径连线要用 Size/2 算端点）
        if (donor is Control donorControl)
        {
            Size = donorControl.Size;
            CustomMinimumSize = donorControl.CustomMinimumSize;
            PivotOffset = donorControl.PivotOffset;
            MouseFilter = donorControl.MouseFilter;
            FocusMode = donorControl.FocusMode;
        }

        _spriteContainer = Take<Node2D>(donor, "SpriteContainer");
        _spineSprite = Take<Node2D>(donor, "SpineSprite");
        _iconImage = Take<TextureRect>(donor, "PlaceholderImage");
        _iconOutline = Take<TextureRect>(donor, "PlaceholderOutline");

        // 这两个是基类（NMapPoint）要用的：准星/选票容器。
        // 认领以后就不用走原版那条 GetNode("%…") 的路。
        var reticle = Take<NSelectionReticle>(donor, "SelectionReticle");
        if (reticle != null) _controllerSelectionReticle = reticle;

        var vote = Take<NMultiplayerVoteContainer>(donor, "MapPointVoteContainer");
        if (vote != null) VoteContainer = vote;

        var children = donor.GetChildren();
        foreach (var child in children)
        {
            donor.RemoveChild(child);
            AddChild(child);
        }

        donor.Free();

        MainFile.DebugLog(
            $"[Act5BossNode] ({Coord.col},{Coord.row}) 美术子树已搬入 | size={Size} | " +
            $"sprite容器={( _spriteContainer != null)} spine={( _spineSprite != null)} " +
            $"贴图={( _iconImage != null)} 外框={( _iconOutline != null)} 准星={(reticle != null)}");
    }

    /// <summary>先在 donor 身上按"唯名"取，取不到就按节点名递归找（两条路都留日志）。</summary>
    private static T? Take<T>(Node donor, string uniqueName) where T : class
    {
        try
        {
            var byUniqueName = donor.GetNodeOrNull<T>("%" + uniqueName);
            if (byUniqueName != null) return byUniqueName;
        }
        catch (Exception ex)
        {
            MainFile.DebugLog($"[Act5BossNode] 唯名查询 '%{uniqueName}' 失败（改按名字找）: {ex.Message}");
        }

        return FindByName<T>(donor, uniqueName);
    }

    private static T? FindByName<T>(Node root, string nodeName) where T : class
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T typed && child.Name.ToString() == nodeName) return typed;

            var deep = FindByName<T>(child, nodeName);
            if (deep != null) return deep;
        }

        return null;
    }

    // ============================================================
    // 生命周期
    // ============================================================

    /// <summary>
    ///     原版 <c>NMapPoint._Ready</c> 会直接抛
    ///     <c>"Don't call base._Ready()! Call ConnectSignals() instead."</c>，
    ///     所以子类的规矩就是：**自己重写 _Ready，自己接信号**。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            ConnectSignals();
            Disable();              // 原版一致：先禁用，等 RecalculateTravelability 判可通行
            ApplyEncounterIcon();   // ★ 上自己的图标
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act5BossNode] ({Coord.col},{Coord.row}) _Ready 失败: {ex}");
        }

        MainFile.DebugLog(
            $"[Act5BossNode] ({Coord.col},{Coord.row}) 就绪 | '{_encounter?.Id.Entry}' | " +
            $"图标来源={(_usesSpine ? "spine骨架" : "贴图")}");
    }

    /// <summary>
    ///     复刻 <c>NClickableControl.ConnectSignals</c>（原版那条会把信号接到它的 private 处理函数上，
    ///     我们接自己的）。区别只有一点：<c>%MapPointVoteContainer</c> / <c>%SelectionReticle</c>
    ///     用的是我们自己从美术子树里认领的引用，不走 GetNode("%…")。
    /// </summary>
    protected override void ConnectSignals()
    {
        Connect(Control.SignalName.FocusEntered, Callable.From(() => SafeFocus(true)));
        Connect(Control.SignalName.FocusExited, Callable.From(() => SafeFocus(false)));
        Connect(Control.SignalName.MouseEntered, Callable.From(OnMouseEntered));
        Connect(Control.SignalName.MouseExited, Callable.From(OnMouseExited));

        // 这两个信号是 NClickableControl._GuiInput 自己发射的（不是场景连线），
        // 所以"鼠标点击"这条路完全不依赖 .tscn 里的 [connection]。
        Connect(NClickableControl.SignalName.MousePressed, Callable.From<InputEvent>(OnMousePressed));
        Connect(NClickableControl.SignalName.MouseReleased, Callable.From<InputEvent>(OnMouseReleased));

        // 原版会塞一个空 focus stylebox，免得焦点框露出来
        AddThemeStyleboxOverride(FocusStylebox, new StyleBoxEmpty());
    }

    // ============================================================
    // 交互（逐条复刻原版判定）
    // ============================================================

    private void OnMouseEntered()
    {
        if (GetTree()?.Paused == true && NGame.IsReleaseGame()) return;   // 原版：暂停时不响应悬停
        SafeFocus(true);
    }

    private void OnMouseExited()
    {
        if (GetTree()?.Paused == true && NGame.IsReleaseGame()) return;
        SafeFocus(false);
    }

    private void OnMousePressed(InputEvent inputEvent)
    {
        if (!IsEnabled || !IsVisibleInTree()) return;
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left }) return;
        if (!IsActuallyTravelable()) return;      // 见 IsActuallyTravelable 的注释

        LogClick("按下");

        OnPressHandler();      // protected：置 _isPressed 并调 OnPress()
    }

    private void OnMouseReleased(InputEvent inputEvent)
    {
        if (!IsEnabled || !IsVisibleInTree()) return;
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left }) return;
        if (!IsActuallyTravelable()) return;

        LogClick("抬起");

        // ★ 这一步落到基类 **sealed** 的 NMapPoint.OnRelease() —— 原版"点节点就出发"
        //   那整段（可通行判断 / 屏幕外判断 / 交给 NMapScreen）都在里面，直接复用。
        OnReleaseHandler();
    }

    /// <summary>
    ///     ⚠️ 不能用基类的 <c>IsTravelable</c> 来判"这个点现在能不能点"：
    ///     <c>NMapPoint.get_IsTravelable()</c> 的**第一分支**是
    ///     <code>if (_screen != null &amp;&amp; _screen.IsDebugTravelEnabled &amp;&amp; !_screen.IsTraveling) return true;</code>
    ///     —— 开发者旅行（控制台 <c>travel</c> / 调试开关）一开，**任何**节点（包括自己正站着的那个）
    ///     都算"可通行" ⇒ 点自己 → 投一个"去当前坐标"的票 → 同坐标进不去 → 玩家卡在地图里出不来
    ///     （用户实测："可以点击自己所在的地点，导致卡在这里无法出去"；日志里确实投出了
    ///      <c>->MapVote (gen: 5 coord: (3, 1))</c>）。
    ///
    ///     所以这里只认原始状态机：**只有 <c>Travelable</c> 才响应点击**。
    /// </summary>
    private bool IsActuallyTravelable()
    {
        if (State == MapPointState.Travelable) return true;

        MainFile.DebugLog($"[Act5BossNode] ({Coord.col},{Coord.row}) 点击被忽略：State={State}（非可通行）");
        return false;
    }

    /// <summary>
    ///     ⚠️ 这里**故意不判断** <c>IsFocused</c>（原版 <c>HandleMousePress/Release</c> 里有这道判断）：
    ///     原版那个 <c>IsFocused</c> 读的是 <c>NClickableControl</c> 的 private <c>_isFocused</c>，
    ///     由 private 的 <c>RefreshFocus()</c> 依据同样是 private 的 <c>_isHovered</c>/<c>_isControllerFocused</c> 维护。
    ///     我们的类走自己的信号接线，那几个 private 状态永远是 false ⇒ 照抄这道判断会让**点击全被丢掉**
    ///     （实测现象：节点可见可用、可通行、但点了毫无反应，日志里连一次投票都没有）。
    ///     而 <c>_GuiInput</c> 能把 <c>MousePressed/MouseReleased</c> 发到我们这里，
    ///     前提本来就是"鼠标在这个控件上"，所以 enabled + 可见 + 左键三条件足够。
    /// </summary>
    private void LogClick(string phase)
    {
        MainFile.DebugLog(
            $"[Act5BossNode] ({Coord.col},{Coord.row}) 鼠标{phase} | enabled={IsEnabled} 可见={IsVisibleInTree()} " +
            $"State={State} 屏幕可通行={_screen?.IsTravelEnabled} 在屏幕上={(_screen != null && _screen.IsNodeOnScreen(this))}");
    }

    private void SafeFocus(bool focused)
    {
        try
        {
            if (focused) OnFocus();
            else OnUnfocus();
        }
        catch (Exception ex)
        {
            // 基类 OnFocus 会碰准星（手柄导航时才用），万一缺件也不该把地图搞崩
            MainFile.Logger.Warn($"[Act5BossNode] ({Coord.col},{Coord.row}) 悬停处理异常: {ex.Message}");
        }
    }

    /// <summary>选中 = 标记"已走过"（复刻原版 BOSS 节点的 OnSelected）。</summary>
    public override void OnSelected() => State = MapPointState.Traveled;

    protected override void OnFocus()
    {
        base.OnFocus();                                   // 基类：悬停提示 + 手柄准星

        if (!IsInputAllowed() || !IsTravelable) return;
        TweenScale(HoverScale, 0.05);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        TweenScale(Vector2.One, 0.5, Tween.EaseType.Out, Tween.TransitionType.Elastic);
    }

    protected override void OnPress()
    {
        if (!IsTravelable) return;
        TweenScale(DownScale, 0.3, Tween.EaseType.Out, Tween.TransitionType.Cubic);
    }

    private void TweenScale(Vector2 target, double duration, Tween.EaseType? ease = null,
        Tween.TransitionType? transition = null)
    {
        if (_spriteContainer == null) return;

        _scaleTween?.Kill();
        _scaleTween = CreateTween().SetParallel(true);

        var tweener = _scaleTween.TweenProperty(_spriteContainer, "scale", target, duration);
        if (ease.HasValue) tweener.SetEase(ease.Value);
        if (transition.HasValue) tweener.SetTrans(transition.Value);
    }

    // ============================================================
    // 颜色 / 缩放：原版 BOSS 节点的阈值自己写一遍
    // ============================================================

    protected override Color TraveledColor => StsColors.pathDotTraveled;

    protected override Color UntravelableColor => StsColors.red;

    protected override Color HoveredColor => StsColors.pathDotTraveled;

    protected override Vector2 HoverScale => Vector2.One * 1.05f;

    protected override Vector2 DownScale => Vector2.One * 1.02f;

    protected override void RefreshColorInstantly()
    {
        // 原版口径：State ∈ {Travelable, Traveled} → 算"已通"（走走过颜色）
        var traveled = State is MapPointState.Travelable or MapPointState.Traveled;

        if (_usesSpine)
        {
            if (_material == null) return;
            _material.SetShaderParameter(BlackLayerParam,
                traveled ? ActColor(a => a.MapTraveledColor) : ActColor(a => a.MapUntraveledColor));
            _material.SetShaderParameter(MapColorParam, ActColor(a => a.MapBgColor));
            return;
        }

        if (_iconImage != null)
            _iconImage.SelfModulate = traveled ? ActColor(a => a.MapTraveledColor) : ActColor(a => a.MapUntraveledColor);

        if (_iconOutline != null)
            _iconOutline.SelfModulate = ActColor(a => a.MapBgColor);
    }

    private Color ActColor(Func<ActModel, Color> pick)
    {
        var act = _act ?? _runState?.Act;
        return act != null ? pick(act) : Colors.White;
    }

    // ============================================================
    // ★ 图标：本节点自己的 encounter（各显各得）
    // ============================================================

    private void ApplyEncounterIcon()
    {
        var spineData = _encounter?.BossNodeSpineResource;

        if (spineData != null && _spineSprite != null)
        {
            _usesSpine = true;
            _spineSprite.Visible = true;
            if (_iconImage != null) _iconImage.Visible = false;
            if (_iconOutline != null) _iconOutline.Visible = false;

            Variant spineNode = _spineSprite;
            _animController = new MegaSprite(spineNode);
            _animController.SetSkeletonDataRes(spineData);
            _animController.GetAnimationState().AddAnimation("animation", 0f, true, 0);
            _material = _animController.GetNormalMaterial() as ShaderMaterial;

            MainFile.DebugLog($"[Act5BossNode] ({Coord.col},{Coord.row}) '{_encounter?.Id.Entry}' 用 spine 骨架图标");
        }
        else
        {
            _usesSpine = false;
            if (_spineSprite != null) _spineSprite.Visible = false;

            var basePath = _encounter?.BossNodePath;
            if (string.IsNullOrEmpty(basePath))
            {
                MainFile.Logger.Warn(
                    $"[Act5BossNode] ({Coord.col},{Coord.row}) '{_encounter?.Id.Entry}' 既无 spine 也无图标路径，" +
                    "节点会只剩外框");
            }
            else
            {
                if (_iconImage != null)
                {
                    _iconImage.Texture = PreloadManager.Cache.GetAsset<Texture2D>(basePath + ".png");
                    _iconImage.Visible = true;
                }

                if (_iconOutline != null)
                {
                    _iconOutline.Texture = PreloadManager.Cache.GetAsset<Texture2D>(basePath + "_outline.png");
                    _iconOutline.Visible = true;
                }

                MainFile.DebugLog(
                    $"[Act5BossNode] ({Coord.col},{Coord.row}) '{_encounter?.Id.Entry}' 用贴图图标 '{basePath}.png'");
            }
        }

        RefreshColorInstantly();

        // ★ 视觉主题：BOSS 地图图标改色要在**贴图挂上之后**做
        //   （之前放在 SetMap postfix 里，那时节点还没跑 _Ready、贴图还是空的 ⇒ 改色等于没改，
        //     你看到的就是"黄描边+红身体"的原图）
        if (_act is Act5Model)
        {
            var myth = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme;
            foreach (var rect in new[] { _iconImage, _iconOutline })
            {
                if (rect?.Texture == null) continue;
                var themed = ActVisualTheme.RecolorForBossIcon(rect.Texture, myth);
                if (themed != null) rect.Texture = themed;
            }

            MainFile.DebugLog($"[Theme] BOSS 图标改色（{(myth ? "神话=血色" : "传奇=黑金")}）");
        }
    }

    /// <summary>别处（调试/诊断）想确认某个节点挂了什么图标时用。</summary>
    internal string IconDebugText() =>
        $"{(_encounter?.Id.Entry ?? "<无>")} [{(_usesSpine ? "spine" : "贴图")}]";
}
