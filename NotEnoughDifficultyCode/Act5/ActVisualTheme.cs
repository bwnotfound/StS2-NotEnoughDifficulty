using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     视觉主题（**全程序化，不依赖任何外部素材**）。
///
/// ## 用户口径（逐字）
/// <code>
/// 传奇的地图是密林的地图，为羊皮纸风格，金色纹路每局完全随机，用程序做
/// 神话的地图为巢穴的地图，周围的黑色变成血液的颜色，浓度与鲜艳不同，地图内部部分则被血液沾染，整体显压抑
/// ACT ４的地图是荣耀的地图，地图周围的青蓝色纹路变成紫色，
/// 遭遇不同层级敌人时，会根据其层级文字颜色而变色，直到战斗结束回到紫色
/// </code>
///
/// ## 为什么要 patch 而不是 override（IL 实证）
/// <c>ActModel.get_MapTopBg / get_MapMidBg / get_MapBotBg</c> **不是 virtual**
/// （签名只有 <c>public</c>），<c>act5/act4</c> 只能通过 Harmony postfix 换掉返回值。
/// 底图输入直接读**原版贴图**：
/// <list type="bullet">
///   <item>密林（Overgrowth，层 1）→ 传奇档底图</item>
///   <item>巢穴（Hive，层 2）→ 神话档底图</item>
///   <item>荣耀（Glory，层 3）→ act4 底图</item>
/// </list>
/// 处理完的结果是 <c>ImageTexture</c>，**每局只算一次**并缓存（<c>get_MapTopBg</c> 可能被每帧调用）。
///
/// ## 三种处理
/// <list type="table">
///   <item><term>羊皮纸 + 金纹</term><description>去饱和 → 羊皮纸色阶；叠加**每局随机**（run seed 派生）的
///         金色纹路（值噪声阈值成的细纹 + 细密斑点）</description></item>
///   <item><term>血染</term><description>暗部（原本的黑）→ 血液色，浓度/鲜艳度由低频噪声决定；
///         亮部（地图内部）加血色偏色 + 低频"血渍"斑块</description></item>
///   <item><term>纹路换色相（act4）</term><description>只对色相落在荣耀底图"青蓝纹路"区间的像素做色相旋转（保留明暗/饱和），
///         其余像素不动 ⇒ "周围的青蓝色纹路变成紫色"；目标色相由 <see cref="Act4MapStripeTint"/> 按**当前层**给
///         （默认紫 = 278°，遭遇某层敌人时 = 该层 act 的 <c>MapTraveledColor</c> 色相）</description></item>
/// </list>
/// </summary>
internal static class ActVisualTheme
{
    /// <summary>已生成的主题贴图缓存（键含模式/层/seed ⇒ 每局每层只算一次）。</summary>
    private static readonly Dictionary<string, ImageTexture> _cache = new(StringComparer.Ordinal);

    public static void Reset()
    {
        _cache.Clear();
        _signatureHue.Clear();
        Act4MapStripeTint.Reset();
    }

    // ============================================================
    // 对外入口：给 patch 用
    // ============================================================

    /// <summary>act5：传奇=密林底图做羊皮纸+金纹；神话=巢穴底图做血染。</summary>
    public static Texture2D? ForAct5(int layer, Texture2D? original)
    {
        try
        {
            var extreme = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme;
            var baseAct = RunProgress.GetRepresentativeAct(extreme ? 2 : 1);   // 2=巢穴, 1=密林
            if (baseAct == null) return original;

            var baseTex = layer switch
            {
                0 => baseAct.MapTopBg,
                1 => baseAct.MapMidBg,
                _ => baseAct.MapBotBg,
            };
            if (baseTex == null) return original;

            var seed = RunStateAccessor.GetCurrentState()?.Rng.Seed ?? 0UL;
            var key = $"act5|{(extreme ? "myth" : "legend")}|{layer}|{seed}";

            return GetOrBuild(key, baseTex, img =>
            {
                if (extreme) Blood(img, seed);
                else Parchment(img, seed);
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] act5 底图生成失败（用原图）: {ex}");
            return original;
        }
    }

    /// <summary>act4：荣耀底图 → 青蓝纹路转紫。</summary>
    public static Texture2D? ForAct4(int layer, Texture2D? original)
    {
        try
        {
            var baseAct = RunProgress.GetRepresentativeAct(3);                  // 3=荣耀
            if (baseAct == null) return original;

            var baseTex = layer switch
            {
                0 => baseAct.MapTopBg,
                1 => baseAct.MapMidBg,
                _ => baseAct.MapBotBg,
            };
            if (baseTex == null) return original;

            // 键含**当前目标色相** ⇒ 换层就换图（着色状态在 Act4MapStripeTint 里，单一数据源）
            return GetOrBuild($"act4|{Act4MapStripeTint.CacheKey}|{layer}", baseTex,
                img => RepaintVeinHue(img, Act4MapStripeTint.Hue));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] act4 底图生成失败（用原图）: {ex}");
            return original;
        }
    }

    // ============================================================
    // 一个 act 的"招牌色相"：从**它自己的地图美术**里统计出来
    // ============================================================

    /// <summary>已算过的招牌色相（键 = act 的 Id.Entry，稳定且可读；一局只算一次）。</summary>
    private static readonly Dictionary<string, float> _signatureHue = new(StringComparer.Ordinal);

    /// <summary>
    ///     取"这个 act 的招牌色相" = 它**地图美术**的主色相（饱和且不暗的像素做 5° 直方图，取峰值桶）。
    ///
    /// ## 为什么不直接用 <c>MapTraveledColor</c>（用户实测反馈）
    /// 那三个颜色是**UI 用的近黑色**（各有实测值）：
    /// <code>
    ///   Overgrowth 28231D(33°)  Underdocks 180F24(266°)  Hive 27221C(33°)  Glory 1D1E2F(231°)
    /// </code>
    /// 用户："<i>暗港不知道为什么不能提取到其颜色，变成默认的紫色</i>" ——
    /// 暗港的 266° 与 act4 的默认紫 278° 只差 12°，肉眼看就是没变。
    /// **美术的主色**才是玩家眼里"这层的颜色"，所以改从底图统计（不写死任何色值）。
    ///
    /// 统计口径：忽略透明、太灰（s &lt; 0.18）与太暗（v &lt; 0.18）的像素，按饱和度加权投票；
    /// 美术读不到时回退 <c>MapTraveledColor</c> 的色相。结果按 act 缓存（一局只算一次）。
    /// </summary>
    internal static float SignatureHue(ActModel act)
    {
        var key = act.Id.Entry;
        if (_signatureHue.TryGetValue(key, out var cached)) return cached;

        var fallback = HueOf(act.MapTraveledColor);
        var hue = fallback;

        try
        {
            var tex = act.MapTopBg;
            var img = tex?.GetImage();
            if (img != null)
            {
                if (img.IsCompressed()) img.Decompress();
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);

                var w = img.GetWidth();
                var h = img.GetHeight();
                var data = img.GetData();
                if (data.Length >= w * h * 4)
                {
                    var bins = new float[72];                     // 每桶 5°
                    for (var i = 0; i < w * h * 4; i += 16)        // 每 4 个像素采一个（够统计、够快）
                    {
                        if (data[i + 3] == 0) continue;
                        RgbToHsv(data[i], data[i + 1], data[i + 2], out var ph, out var ps, out var pv);
                        if (ps < 0.18f || pv < 0.18f) continue;
                        bins[(int)(ph / 5f) % bins.Length] += ps;
                    }

                    var best = -1;
                    var bestWeight = 0f;
                    for (var b = 0; b < bins.Length; b++)
                        if (bins[b] > bestWeight) { bestWeight = bins[b]; best = b; }

                    if (best >= 0 && bestWeight > 0f) hue = best * 5f + 2.5f;
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] 提取 '{act.Id.Entry}' 招牌色失败（用 UI 色兜底）: {ex.Message}");
        }

        _signatureHue[key] = hue;
        MainFile.DebugLog(
            $"[Theme] '{act.Id.Entry}' 招牌色相 = {hue:0}°（UI 旅行色 {fallback:0}°{(Mathf.Abs(hue - fallback) < 1f ? "，美术读不到" : "")}）");
        return hue;
    }

    private static float HueOf(Color c)
    {
        RgbToHsv((byte)(c.R * 255f), (byte)(c.G * 255f), (byte)(c.B * 255f), out var hue, out _, out _);
        return hue;
    }

    private static ImageTexture? GetOrBuild(string key, Texture2D baseTex, Action<Image> process)
    {
        if (_cache.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;

        var img = baseTex.GetImage();
        if (img == null) return null;

        // ⚠️ 地图背景是 **VRAM 压缩纹理**：直接 GetData() 拿到的是压缩数据，
        //    长度远小于 宽×高×4 → 按像素索引会 "Index was outside the bounds of the array"（实测）。
        //    标准三步：先解压、再转 RGBA8、再按实际长度做保护。
        if (img.IsCompressed()) img.Decompress();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);

        var need = img.GetWidth() * img.GetHeight() * 4;
        var actual = img.GetData().Length;
        if (actual < need)
        {
            MainFile.Logger.Warn(
                $"[Theme] '{key}' 像素数据不足（需要 {need}，实际 {actual}，格式={img.GetFormat()}）→ 用原图");
            return null;
        }

        process(img);

        var tex = ImageTexture.CreateFromImage(img);
        _cache[key] = tex;
        MainFile.DebugLog($"[Theme] 生成主题底图 '{key}'（{img.GetWidth()}x{img.GetHeight()}）");
        return tex;
    }

    // ============================================================
    // 处理 ①：荣耀底图的青蓝纹路 → 目标色相（act4；默认紫，按层变色时给该层色相）
    // ============================================================

    /// <summary>
    ///     把荣耀底图里**那圈青蓝纹路**的色相换成 <paramref name="targetHue"/>，明暗/饱和度保留。
    ///     色相区间与目标色相都由 <see cref="Act4MapStripeTint"/> 给（不在这里写死）。
    /// </summary>
    private static void RepaintVeinHue(Image img, float targetHue)
    {
        var w = img.GetWidth();
        var h = img.GetHeight();
        var data = img.GetData();

        var changed = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            RgbToHsv(data[i], data[i + 1], data[i + 2], out var hue, out var sat, out var val);
            if (sat < 0.12f) continue;                       // 灰/黑不动
            if (hue < Act4MapStripeTint.VeinHueFrom || hue > Act4MapStripeTint.VeinHueTo) continue;

            HsvToRgb(targetHue, sat, val, out var r, out var g, out var b);
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            changed++;
        }

        img.SetData(w, h, false, Image.Format.Rgba8, data);
        MainFile.DebugLog($"[Theme] 纹路色相 → {targetHue:0}°，命中 {changed} 像素");
    }

    // ============================================================
    // 处理 ②：羊皮纸 + 每局随机金纹（act5 传奇）
    // ============================================================

    private static void Parchment(Image img, ulong seed)
    {
        var w = img.GetWidth();
        var h = img.GetHeight();
        var data = img.GetData();
        var s = (int)(seed & 0x7fffffff);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;

            // ① 羊皮纸化：按亮度映射到暖色阶，保留一点原始纹理起伏
            var lum = (data[i] * 0.299f + data[i + 1] * 0.587f + data[i + 2] * 0.114f) / 255f;
            var grain = (ValueNoise(x * 0.35f, y * 0.35f, s) - 0.5f) * 0.06f;   // 纸张颗粒
            var t = Mathf.Clamp(lum * 0.75f + 0.25f + grain, 0f, 1f);

            var r = (byte)(150 + 95 * t);      // 150..245
            var g = (byte)(122 + 88 * t);      // 122..210
            var b = (byte)(82 + 70 * t);       //  82..152  ⇒ 暖色羊皮纸

            // ② 金色纹路：每局随机 —— 两张低频噪声相减取"细脊"，再叠一层细密斑点
            var ridge = Mathf.Abs(ValueNoise(x * 0.012f, y * 0.012f, s) - ValueNoise(x * 0.012f, y * 0.012f, s + 7919));
            var vein = ridge < 0.028f ? 1f : 0f;
            var dot = ValueNoise(x * 0.6f, y * 0.6f, s + 104729) > 0.983f ? 0.55f : 0f;
            var gold = Mathf.Clamp(vein + dot, 0f, 1f);

            if (gold > 0f)
            {
                var gr = 232f; var gg = 190f; var gb = 96f;      // 金
                var a = gold * 0.85f;
                r = (byte)Mathf.Clamp(r + (gr - r) * a, 0f, 255f);
                g = (byte)Mathf.Clamp(g + (gg - g) * a, 0f, 255f);
                b = (byte)Mathf.Clamp(b + (gb - b) * a, 0f, 255f);
            }

            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
        }

        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    // ============================================================
    // 处理 ③：黑 → 血色 + 内部血渍（act5 神话）
    // ============================================================

    private static void Blood(Image img, ulong seed)
    {
        var w = img.GetWidth();
        var h = img.GetHeight();
        var data = img.GetData();
        var s = (int)(seed & 0x7fffffff);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var lum = (data[i] * 0.299f + data[i + 1] * 0.587f + data[i + 2] * 0.114f) / 255f;

            // 低频噪声：决定"浓度与鲜艳不同"
            var dens = ValueNoise(x * 0.004f, y * 0.004f, s);
            var vivid = 0.55f + 0.45f * ValueNoise(x * 0.010f, y * 0.010f, s + 3571);

            if (lum < 0.35f)
            {
                // ① 地图**外围**（卷轴之外那圈背景）：**熔岩 / 鲜血** ——
                //    域扭曲的低频噪声做出流动纹路，再叠"热斑"（只有最热处泛橙），整体高饱和深红。
                var warp = ValueNoise(x * 0.004f, y * 0.004f, s + 8191) * 2f - 1f;
                var hot = ValueNoise(x * 0.006f + warp * 6f, y * 0.006f + warp * 6f, s + 12345);
                var heat = Mathf.Pow(Mathf.Clamp(hot, 0f, 1f), 1.6f);

                var k = 1f - lum / 0.35f;
                var r = 48f + 150f * k + 120f * heat;
                var g = 6f + 10f * k + 46f * heat * heat;      // 只有热斑泛橙
                var b = 8f + 10f * k + 12f * heat;

                data[i] = (byte)Mathf.Clamp(r, 0f, 255f);
                data[i + 1] = (byte)Mathf.Clamp(g, 0f, 255f);
                data[i + 2] = (byte)Mathf.Clamp(b, 0f, 255f);
            }
            else
            {
                // ② 地图**内部**：压成近黑深红底，再画**流动不息的血管纹路**
                var baseR = data[i] * 0.30f;
                var baseG = data[i + 1] * 0.16f;
                var baseB = data[i + 2] * 0.20f;

                // 血管：两张低频噪声相减取"细脊"（同一套做法，频率更低更纤细 ⇒ 像血管而不是大理石纹）
                var n1 = ValueNoise(x * 0.009f, y * 0.009f, s + 104729);
                var n2 = ValueNoise(x * 0.009f, y * 0.009f, s + 224737);
                var ridge = Mathf.Abs(n1 - n2);

                // 支脉：再叠一层高频细脊，做分叉感
                var t1 = ValueNoise(x * 0.028f, y * 0.028f, s + 350377);
                var t2 = ValueNoise(x * 0.028f, y * 0.028f, s + 479001);
                var branch = Mathf.Abs(t1 - t2);

                var vein = ridge < 0.020f ? 1f : (branch < 0.010f ? 0.65f : 0f);
                vein *= 0.75f + 0.25f * dens;                        // 明暗随位置起伏

                var r = baseR + 175f * vein;
                var g = baseG + 26f * vein;
                var b = baseB + 34f * vein;

                data[i] = (byte)Mathf.Clamp(r, 0f, 255f);
                data[i + 1] = (byte)Mathf.Clamp(g, 0f, 255f);
                data[i + 2] = (byte)Mathf.Clamp(b, 0f, 255f);
            }
        }

        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    // ============================================================
    // 处理 ④：BOSS 地图图标改色（传奇 = 黑金为主 + 保留红；神话 = 血色）
    // ============================================================

    /// <summary>把一张贴图按"BOSS 图标配色"重调；失败返回 null（调用方保留原贴图）。</summary>
    public static ImageTexture? RecolorForBossIcon(Texture2D src, bool myth)
    {
        try
        {
            return GetOrBuild($"icon|{(myth ? "myth" : "legend")}|{src.GetInstanceId()}", src, img =>
            {
                if (myth) BloodIcon(img);
                else BlackGold(img);
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] BOSS 图标重调色失败（用原图）: {ex}");
            return null;
        }
    }

    /// <summary>
    ///     传奇档：**黑金为主、里面的红保留**。
    ///     红/橙（色相 ≤35° 或 ≥335° 且够饱和）原样不动，其余按亮度映射到"近黑 → 金"的色阶。
    /// </summary>
    private static void BlackGold(Image img)
    {
        var w = img.GetWidth();
        var h = img.GetHeight();
        var data = img.GetData();
        if (data.Length < w * h * 4) return;

        for (var i = 0; i < w * h * 4; i += 4)
        {
            if (data[i + 3] == 0) continue;                       // 透明不动

            // ⚠️ 用户口径修正（"底图不要红色！"）：**不保留任何红色** ——
            //    传奇档 BOSS 图标整体黑金，红色一律跟着亮度映射进黑金色阶。
            var val = (data[i] * 0.299f + data[i + 1] * 0.587f + data[i + 2] * 0.114f) / 255f;
            var t = Mathf.Clamp(val, 0f, 1f);
            float r, g, b;
            if (t < 0.45f)
            {
                r = 18f + 40f * t; g = 16f + 34f * t; b = 12f + 22f * t;                 // 黑
            }
            else
            {
                var k = (t - 0.45f) / 0.55f;
                r = 58f + 175f * k; g = 50f + 140f * k; b = 34f + 60f * k;               // 金
            }

            data[i] = (byte)Mathf.Clamp(r, 0f, 255f);
            data[i + 1] = (byte)Mathf.Clamp(g, 0f, 255f);
            data[i + 2] = (byte)Mathf.Clamp(b, 0f, 255f);
        }

        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    /// <summary>神话档：BOSS 图标压成血色（暗部更深、亮部暗红）。</summary>
    private static void BloodIcon(Image img)
    {
        var w = img.GetWidth();
        var h = img.GetHeight();
        var data = img.GetData();
        if (data.Length < w * h * 4) return;

        for (var i = 0; i < w * h * 4; i += 4)
        {
            if (data[i + 3] == 0) continue;
            var val = (data[i] * 0.299f + data[i + 1] * 0.587f + data[i + 2] * 0.114f) / 255f;
            data[i] = (byte)Mathf.Clamp(30f + 150f * val, 0f, 255f);
            data[i + 1] = (byte)Mathf.Clamp(6f + 22f * val, 0f, 255f);
            data[i + 2] = (byte)Mathf.Clamp(10f + 26f * val, 0f, 255f);
        }

        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }
    // ============================================================
    // 处理 ⑤：地图节点图标 黄色 → 淡紫（act4 用户口径）
    // ============================================================

    /// <summary>把一棵子树里的 TextureRect 贴图做"黄→淡紫"，返回改掉的贴图数。</summary>
    public static int RecolorTreeYellowToLavender(Node root)
    {
        var count = 0;
        foreach (var rect in FindTextures(root))
        {
            if (rect.Texture == null) continue;
            var themed = RecolorYellowToLavender(rect.Texture);
            if (themed == null) continue;
            rect.Texture = themed;
            count++;
        }

        return count;
    }

    private static IEnumerable<TextureRect> FindTextures(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is TextureRect rect) yield return rect;
            foreach (var deep in FindTextures(child)) yield return deep;
        }
    }

    /// <summary>黄色系像素 → 淡紫（保留明暗，色相旋到 275°、降一点饱和）。其它颜色不动。</summary>
    public static ImageTexture? RecolorYellowToLavender(Texture2D src)
    {
        try
        {
            return GetOrBuild($"icon|act4lav|{src.GetInstanceId()}", src, img =>
            {
                var w = img.GetWidth();
                var h = img.GetHeight();
                var data = img.GetData();
                if (data.Length < w * h * 4) return;

                for (var i = 0; i < w * h * 4; i += 4)
                {
                    if (data[i + 3] == 0) continue;
                    RgbToHsv(data[i], data[i + 1], data[i + 2], out var hue, out var sat, out var val);
                    if (sat < 0.18f) continue;                       // 灰/白不动（描边）
                    if (hue < 25f || hue > 70f) continue;            // 只动黄/橙区间

                    HsvToRgb(275f, Mathf.Clamp(sat * 0.72f, 0f, 1f), val, out var r, out var g, out var b);
                    data[i] = r;
                    data[i + 1] = g;
                    data[i + 2] = b;
                }

                img.SetData(w, h, false, Image.Format.Rgba8, data);
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Theme] 黄→淡紫失败: {ex.Message}");
            return null;
        }
    }
    // ============================================================
    // 小工具：值噪声 + HSV
    // ============================================================

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            var n = x * 374761393 + y * 668265263 + seed * 1274126177;
            n = (n ^ (n >> 13)) * 1274126177;
            return ((n ^ (n >> 16)) & 0x7fffffff) / (float)0x7fffffff;
        }
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        var xi = Mathf.FloorToInt(x);
        var yi = Mathf.FloorToInt(y);
        var xf = x - xi;
        var yf = y - yi;

        var u = xf * xf * (3f - 2f * xf);
        var v = yf * yf * (3f - 2f * yf);

        var a = Hash(xi, yi, seed);
        var b = Hash(xi + 1, yi, seed);
        var c = Hash(xi, yi + 1, seed);
        var d = Hash(xi + 1, yi + 1, seed);

        return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
    }

    internal static void RgbToHsv(byte r, byte g, byte b, out float hue, out float sat, out float val)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        var max = Mathf.Max(rf, Mathf.Max(gf, bf));
        var min = Mathf.Min(rf, Mathf.Min(gf, bf));
        var delta = max - min;

        val = max;
        sat = max <= 0f ? 0f : delta / max;

        if (delta <= 0f) { hue = 0f; return; }

        if (Mathf.IsEqualApprox(max, rf)) hue = 60f * (((gf - bf) / delta) % 6f);
        else if (Mathf.IsEqualApprox(max, gf)) hue = 60f * ((bf - rf) / delta + 2f);
        else hue = 60f * ((rf - gf) / delta + 4f);

        if (hue < 0f) hue += 360f;
    }

    private static void HsvToRgb(float hue, float sat, float val, out byte r, out byte g, out byte b)
    {
        var c = val * sat;
        var x = c * (1f - Mathf.Abs((hue / 60f) % 2f - 1f));
        var m = val - c;

        float rf, gf, bf;
        if (hue < 60f) { rf = c; gf = x; bf = 0f; }
        else if (hue < 120f) { rf = x; gf = c; bf = 0f; }
        else if (hue < 180f) { rf = 0f; gf = c; bf = x; }
        else if (hue < 240f) { rf = 0f; gf = x; bf = c; }
        else if (hue < 300f) { rf = x; gf = 0f; bf = c; }
        else { rf = c; gf = 0f; bf = x; }

        r = (byte)Mathf.Clamp((rf + m) * 255f, 0f, 255f);
        g = (byte)Mathf.Clamp((gf + m) * 255f, 0f, 255f);
        b = (byte)Mathf.Clamp((bf + m) * 255f, 0f, 255f);
    }
}

// ================================================================
// patch：act5 标题按档位改名（传奇 / 神话）
// ================================================================

/// <summary>
///     <c>ActModel.get_Title()</c> **不是 virtual**（实现是 <c>new LocString("acts", Id.Entry + ".title")</c>），
///     所以按档位换名字只能 patch。键加在 <c>localization/&lt;lang&gt;/acts.json</c>。
/// </summary>
[HarmonyPatch(typeof(ActModel), "get_Title")]
internal static class Act5TitleByModePatch
{
    private const string LegendKey = "NOTENOUGHDIFFICULTY-ACT5_MODEL.title_legend";
    private const string MythKey = "NOTENOUGHDIFFICULTY-ACT5_MODEL.title_myth";

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ActModel __instance, ref LocString __result)
    {
        if (!PatchScope.IsEnabled) return;
        if (__instance is not Act5Model) return;

        var key = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme ? MythKey : LegendKey;
        __result = new LocString("acts", key);
    }
}

// ================================================================
// patch：地图三层底图换主题
// ================================================================

[HarmonyPatch(typeof(ActModel), "get_MapTopBg")]
internal static class ActMapTopBgThemePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ActModel __instance, ref Texture2D __result) =>
        __result = Themed(__instance, 0, __result);

    internal static Texture2D Themed(ActModel act, int layer, Texture2D original) => act switch
    {
        Act5Model => ActVisualTheme.ForAct5(layer, original) ?? original,
        Act4Model => ActVisualTheme.ForAct4(layer, original) ?? original,
        _ => original,
    };
}

[HarmonyPatch(typeof(ActModel), "get_MapMidBg")]
internal static class ActMapMidBgThemePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ActModel __instance, ref Texture2D __result) =>
        __result = ActMapTopBgThemePatch.Themed(__instance, 1, __result);
}

[HarmonyPatch(typeof(ActModel), "get_MapBotBg")]
internal static class ActMapBotBgThemePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ActModel __instance, ref Texture2D __result) =>
        __result = ActMapTopBgThemePatch.Themed(__instance, 2, __result);
}
