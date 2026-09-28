using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT5 的程序化视觉特效（**不依赖任何外部素材/着色器文件**）。
///
/// ## 用户口径
/// <list type="bullet">
///   <item><i>"纹路要会流动，是不是有金光顺着纹路一闪而过"</i>
///         ⇒ 给地图三层背景挂一个**运行时构造的 canvas_item 着色器**：
///         只对"金色纹路像素"叠加一条沿 Y 轴移动的高光带（金光滑过）。</item>
///   <item><i>"传奇BOSS还是红了，严格来说BOSS要黑金为主，BOSS底图里面带红色"</i>
///         ⇒ 传奇档把 BOSS 地图图标重新调色：**暗部压成近黑、中间调染成金**，
///         **红/橙像素原样保留**（"底图里面带红色"）。</item>
/// </list>
///
/// 着色器代码是**字符串**（`new Shader { Code = ... }`），所以不用往 pck 里塞 .gdshader，
/// 也不受"mod 不能带资源"的限制。
/// </summary>
internal static class Act5VisualEffects
{
    /// <summary>金光流动：只让金色像素亮一条带子扫过。</summary>
    private const string ShimmerShaderCode = @"
shader_type canvas_item;

uniform float shimmer_speed = 0.35;
uniform float shimmer_width = 0.10;
uniform float shimmer_strength = 0.85;
uniform vec4 shimmer_color : source_color = vec4(1.0, 0.86, 0.45, 1.0);
uniform float gold_r = 0.62;
uniform float gold_g_min = 0.45;
uniform float gold_b_max = 0.70;

// ── 卷云：若隐若现的丝缕（每次爬楼换样式 ⇒ 只改 pattern_seed，不重算贴图）──
uniform float pattern_seed = 0.0;
uniform float cirrus_strength = 0.22;
uniform vec4 cirrus_color : source_color = vec4(1.0, 0.95, 0.75, 1.0);

// ── 层色：遭遇某层敌人时把**纹路**染成该层颜色（战斗结束恢复紫色）──
uniform float tint_mix = 0.0;
uniform vec4 tint_color : source_color = vec4(0.74, 0.58, 0.96, 1.0);

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32 + pattern_seed);
    return fract(p.x * p.y);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void fragment() {
    vec4 c = texture(TEXTURE, UV);

    // ① 纹路识别（金纹 / 血纹都靠 高R+低B 这一条）
    float on_vein = step(gold_r, c.r) * step(gold_g_min, c.g) * step(c.b, gold_b_max);

    // ② 层色：把纹路整体往该层颜色混（tint_mix=0 时保持原色=紫）
    if (on_vein > 0.5 && tint_mix > 0.001) {
        c.rgb = mix(c.rgb, tint_color.rgb * (0.55 + 0.65 * c.r), tint_mix);
    }

    // ③ 卷云：各向异性拉伸的噪声 ⇒ 丝缕状；低振幅 + 大尺度遮罩 ⇒ 若隐若现
    vec2 uv = UV * vec2(3.0, 9.0) + vec2(pattern_seed * 7.0, pattern_seed * 3.0);
    float wisp = vnoise(uv) * 0.6 + vnoise(uv * 2.3) * 0.4;
    float veil = smoothstep(0.55, 0.95, vnoise(UV * 1.7 + pattern_seed));
    float cirrus = smoothstep(0.55, 0.85, wisp) * veil * cirrus_strength;
    c.rgb += cirrus_color.rgb * cirrus;

    // ④ 金光/血光扫过（只扫纹路）
    if (on_vein > 0.5) {
        float t = fract(UV.y * 1.35 - TIME * shimmer_speed);
        float band = smoothstep(1.0 - shimmer_width, 1.0, t) + smoothstep(shimmer_width, 0.0, t);
        c.rgb += shimmer_color.rgb * band * shimmer_strength;
    }

    COLOR = c;
}
";

    private static Shader? _shimmerShader;

    /// <summary>已挂到地图背景上的材质（改 uniform 用）。</summary>
    private static readonly List<ShaderMaterial> _materials = new();

    /// <summary>给地图三层背景挂上"金光流动"（已经挂过就跳过）。</summary>
    internal static void AttachMapShimmer(NMapScreen screen, bool myth)
    {
        try
        {
            var bg = Traverse.Create(screen).Field("_mapBgContainer").GetValue<Node>();
            if (bg == null)
            {
                MainFile.DebugLog("[Theme] 取不到 _mapBgContainer，跳过金光流动");
                return;
            }

            _shimmerShader ??= new Shader { Code = ShimmerShaderCode };

            // ★ 编译自检：着色器代码是**运行时字符串**，语法错了 Godot 只在控制台报错、不抛异常；
            //   而 Material 一旦挂上去、编译失败时地图底图可能整块不渲染。
            //   所以先问一次 uniform 列表：本 shader 有一堆 uniform，返回空 = 没编译成功 ⇒
            //   宁可不挂（回退原版渲染），也不赌一个可能全黑的底图。
            if (_shimmerShader == null || _shimmerShader.GetShaderUniformList().Count == 0)
            {
                MainFile.Logger.Warn(
                    "[Theme] 金光流动着色器未通过编译校验（uniform 列表为空），跳过挂载 —— 地图保持原版渲染");
                _shimmerShader = null;      // 下次进来重新构造
                return;
            }

            var attached = 0;
            foreach (var name in new[] { "_mapTop", "_mapMid", "_mapBot" })
            {
                if (Traverse.Create(bg).Field(name).GetValue<TextureRect>() is not { } rect) continue;

                if (rect.Material is ShaderMaterial existing)
                {
                    // 已经有材质（原版可能用它做染色）→ 只改参数，不覆盖
                    ApplyShimmerParams(existing, myth);
                    if (!_materials.Contains(existing)) _materials.Add(existing);
                    attached++;
                    continue;
                }

                if (rect.Material != null)
                {
                    MainFile.Logger.Warn($"[Theme] {name} 已有非 ShaderMaterial 材质，跳过金光流动");
                    continue;
                }

                var mat = new ShaderMaterial { Shader = _shimmerShader };
                ApplyShimmerParams(mat, myth);
                rect.Material = mat;
                _materials.Add(mat);
                attached++;
            }

            MainFile.Logger.Info($"[Theme] 金光流动已挂到 {attached} 层地图背景（神话档={myth}）");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] 挂金光流动失败: {ex.Message}");
        }
    }

    // ============================================================
    // 每次爬楼换卷云样式 / 遭遇某层敌人时把纹路染成该层颜色
    // ============================================================

    private static float _patternSeed;
    private static int _lastFloor = int.MinValue;

    /// <summary>楼层变了就换一套卷云样式（只改 uniform，不重算贴图）。</summary>
    internal static void UpdatePatternForFloor(int floor)
    {
        if (floor == _lastFloor) return;
        _lastFloor = floor;
        _patternSeed = (floor % 97) / 97f + 0.13f;

        foreach (var mat in _materials)
            mat.SetShaderParameter("pattern_seed", _patternSeed);

        MainFile.DebugLog($"[Theme] 卷云样式换新（楼层={floor} seed={_patternSeed:F3}）");
    }

    /// <summary>遭遇某层敌人：把**地图纹理**染成该层颜色（战斗结束传 null 恢复紫色）。</summary>
    internal static void SetLayerTint(int layer)
    {
        // 层色：沿用各层主题色（act1 密林=绿 / act2 巢穴=青 / act3 荣耀=金）；0=恢复紫色
        var color = layer switch
        {
            1 => new Color(0.45f, 0.86f, 0.45f),
            2 => new Color(0.36f, 0.86f, 0.88f),
            3 => new Color(0.96f, 0.84f, 0.38f),
            _ => new Color(0.74f, 0.58f, 0.96f),      // 紫（默认）
        };
        var mix = layer is >= 1 and <= 3 ? 0.85f : 0f;

        foreach (var mat in _materials)
        {
            mat.SetShaderParameter("tint_color", color);
            mat.SetShaderParameter("tint_mix", mix);
        }

        MainFile.DebugLog(layer is >= 1 and <= 3
            ? $"[Theme] 地图纹路染成第 {layer} 层色 {color}"
            : "[Theme] 地图纹路恢复紫色");
    }
    private static void ApplyShimmerParams(ShaderMaterial mat, bool myth)
    {
        // 神话档：不闪金光，改成暗红"血光"慢速流过
        mat.SetShaderParameter("shimmer_color", myth
            ? new Color(0.85f, 0.10f, 0.10f)
            : new Color(1.00f, 0.86f, 0.45f));
        mat.SetShaderParameter("shimmer_speed", myth ? 0.26f : 0.35f);
        mat.SetShaderParameter("shimmer_strength", myth ? 0.60f : 0.85f);
        mat.SetShaderParameter("shimmer_width", myth ? 0.16f : 0.10f);

        // 神话档：只让**血管纹路**（亮红）流动，外围凝固的深红保持静止
        mat.SetShaderParameter("gold_r", myth ? 0.55f : 0.62f);
        mat.SetShaderParameter("gold_g_min", myth ? 0.0f : 0.45f);
        mat.SetShaderParameter("gold_b_max", myth ? 0.35f : 0.70f);
    }

    // ============================================================
    // 传奇档：BOSS 地图图标改黑金（保留里面的红）
    // ============================================================

    /// <summary>把一棵子树里的 TextureRect 贴图按"黑金为主、红色保留"重调色。</summary>
    internal static void RecolorBossIcon(Node? root, bool myth)
    {
        if (root == null) return;

        try
        {
            foreach (var rect in FindAll<TextureRect>(root))
            {
                if (rect.Texture == null) continue;

                var recolored = ActVisualTheme.RecolorForBossIcon(rect.Texture, myth);
                if (recolored != null) rect.Texture = recolored;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] BOSS 图标改色失败: {ex.Message}");
        }
    }

    private static IEnumerable<T> FindAll<T>(Node root) where T : class
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T typed) yield return typed;
            foreach (var deep in FindAll<T>(child)) yield return deep;
        }
    }
}
