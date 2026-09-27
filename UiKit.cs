using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace KeepCachedVersions
{
    /// <summary>
    /// 运行时 uGUI 构建工具（本插件的最小集）。
    ///
    /// <b>为什么不能用 IMGUI（OnGUI）</b>
    /// 本作的 IL2CPP 构建把大量 IMGUI 方法**连原生实现一起剥掉**了，
    /// 托管侧签名还在（所以编译得过），但运行期调用会抛
    /// <c>System.NotSupportedException: Method unstripping failed</c>。
    ///
    /// 实测（<c>tools/_probe_imgui.py</c> 查 Il2CppDumper 的 script.json）：
    ///   - <c>UnityEngine.GUIStyle</c>：只剩内部方法，<c>padding/margin/border/font/fontSize/
    ///     wordWrap/alignment/normal</c> 等属性访问器**条目不存在**；
    ///   - <c>UnityEngine.GUIStyleState</c>：只剩 <c>set_textColor</c>，<c>background</c> 不存在；
    ///   - <c>UnityEngine.GUILayout</c>：<c>FlexibleSpace</c> 条目不存在；
    ///   - <c>UnityEngine.GUI</c>：<c>DrawTexture</c> 条目不存在。
    ///
    /// 本插件 v0.2.0 正是因此翻车：<c>CacheCleanupUI.OnGUI()</c> 每帧抛异常，
    /// 模态窗口永远画不出来，日志被刷了 2520 行。
    ///
    /// 而 uGUI（Canvas / Image / TextMeshPro）是游戏自己的 UI 体系，方法全部健在，
    /// 因此一律改用 uGUI。判定方法见 <c>verify_ugui.py</c>。
    /// </summary>
    internal static class UiKit
    {
        // ------------------------------------------------------------------
        // 配色（与 RPGHelper / LimiNexDebug 保持同一套观感）
        // ------------------------------------------------------------------

        internal static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.62f);
        internal static readonly Color PanelBg = new Color(0.09f, 0.095f, 0.115f, 0.98f);
        internal static readonly Color PanelEdge = new Color(0.22f, 0.23f, 0.27f, 1f);
        internal static readonly Color BtnBg = new Color(0.15f, 0.16f, 0.19f, 1f);
        internal static readonly Color BtnBgPrimary = new Color(0.30f, 0.13f, 0.14f, 1f);
        internal static readonly Color BtnBgDanger = new Color(0.36f, 0.15f, 0.16f, 1f);
        internal static readonly Color TextCol = new Color(0.93f, 0.94f, 0.96f);
        internal static readonly Color TextDim = new Color(0.66f, 0.69f, 0.76f);
        internal static readonly Color Accent = new Color(0.85f, 0.28f, 0.30f);

        // ------------------------------------------------------------------
        // 资产：Sprite
        // ------------------------------------------------------------------

        private static Sprite _white;
        private static Sprite _rounded;

        /// <summary>1×1 纯白 sprite，配 <c>Image.color</c> 得到任意纯色矩形。</summary>
        internal static Sprite White
        {
            get
            {
                if (_white == null) _white = MakeWhite();
                return _white;
            }
        }

        /// <summary>圆角矩形 sprite（9-slice），面板与按钮用。</summary>
        internal static Sprite Rounded
        {
            get
            {
                if (_rounded == null) _rounded = MakeRounded(24, 6);
                return _rounded;
            }
        }

        private static Sprite MakeWhite()
        {
            try
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
                var s = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
                if (s != null) s.hideFlags = HideFlags.HideAndDontSave;
                return s;
            }
            catch (Exception e)
            {
                // 兜底：拿引擎内置白贴图，至少不至于整块 UI 透明
                Plugin.LogWarning("[UiKit] 自绘白块失败，改用 Texture2D.whiteTexture: " + e.Message);
                try
                {
                    var tex = Texture2D.whiteTexture;
                    if (tex != null)
                        return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                }
                catch { }
                return null;
            }
        }

        private static Sprite MakeRounded(int size, int radius)
        {
            try
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float r = radius;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // 到最近圆角的距离决定 alpha，0.5 过渡带做简单抗锯齿
                        float dx = Mathf.Max(r - x, x - (size - 1 - r), 0f);
                        float dy = Mathf.Max(r - y, y - (size - 1 - r), 0f);
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f)));
                    }
                }
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;

                var border = new Vector4(radius, radius, radius, radius);
                var s = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                    100f, 0, SpriteMeshType.FullRect, border);
                if (s != null) s.hideFlags = HideFlags.HideAndDontSave;
                return s;
            }
            catch (Exception e)
            {
                Plugin.LogWarning("[UiKit] 圆角贴图生成失败，退化成白块: " + e.Message);
                return White;
            }
        }

        // ------------------------------------------------------------------
        // 画布
        // ------------------------------------------------------------------

        private static Canvas _canvas;

        /// <summary>根 Canvas（ScreenSpaceOverlay，sortingOrder 高于游戏与其它模组）。</summary>
        internal static Canvas EnsureCanvas()
        {
            if (_canvas != null) return _canvas;

            var go = new GameObject("KeepCachedVersions_Canvas");
            Object.DontDestroyOnLoad(go);
            go.hideFlags |= HideFlags.HideAndDontSave;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 32000 > RPGHelper(30000) > 游戏 UI；排序高者先被 GraphicRaycaster 命中，
            // 因此我们的全屏遮罩能挡住游戏自己的按钮。
            canvas.sortingOrder = 32000;

            var scaler = go.AddComponent<CanvasScaler>();
            // ConstantPixelSize + scaleFactor 1 → 画布坐标 == 屏幕像素，换算最简单
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            go.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            _canvas = canvas;
            return _canvas;
        }

        /// <summary>
        /// 保证场景里有 EventSystem。登录场景大概率自带（游戏按钮能用），
        /// 但缺失会让点击判定与遮罩完全失效，所以必须兜底。
        /// </summary>
        internal static void EnsureEventSystem()
        {
            try
            {
                if (Object.FindObjectOfType<EventSystem>() != null) return;
                var go = new GameObject("KeepCachedVersions_EventSystem");
                Object.DontDestroyOnLoad(go);
                go.hideFlags |= HideFlags.HideAndDontSave;
                go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
                Plugin.LogInfo("[UiKit] 场景内无 EventSystem，已补建");
            }
            catch (Exception e)
            {
                Plugin.LogWarning("[UiKit] EventSystem 创建失败（点击可能不可用）: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 字体：按界面语言解析 TMP_FontAsset
        // ------------------------------------------------------------------

        private static TMP_FontAsset _font;
        private static string _fontKey;
        private static bool _noLangDir;
        private static readonly List<TMP_Text> _texts = new List<TMP_Text>();

        /// <summary>
        /// 当前语言对应的 TMP 字体。语言变化会自动重解析并回填所有已建文本。
        /// 拿不到时返回 null（TMP 会用默认字体，可能豆腐块，但不崩）。
        /// </summary>
        internal static TMP_FontAsset UiFont
        {
            get
            {
                GameLang lang = Strings.DetectLanguage();
                string key = ResolveFontKey(lang, out var resolved);
                if (resolved == null) return _font;

                if (key != _fontKey)
                {
                    _font = resolved;
                    _fontKey = key;
                    Plugin.LogInfo($"[UiKit] TMP 字体 -> {_font.name}（字形 {GlyphCount(_font)}，来源 {key}）");
                    ApplyFontToAll();
                }
                else
                {
                    _font = resolved;
                }
                return _font;
            }
        }

        private static void ApplyFontToAll()
        {
            for (int i = _texts.Count - 1; i >= 0; i--)
            {
                var t = _texts[i];
                if (t == null) { _texts.RemoveAt(i); continue; }
                try { t.font = _font; } catch { /* 单个失败忽略 */ }
            }
        }

        /// <summary>清理已销毁的文本登记（面板重建时调用）。</summary>
        internal static void PruneTexts() => _texts.RemoveAll(t => t == null);

        /// <summary>
        /// 按优先级解析字体，返回来源标识（用于缓存判定；null = 一个都没找到）。
        ///
        /// ① 汉化包正文字体（中文 + 装了汉化包时最优，且不额外占显存）
        /// ② 游戏当前语言字体 <c>FontManager.GetByFontCategory</c>
        ///    ⚠️ 只有 **1 个参数**的重载（返回 <c>FontWithMaterial</c>）；2 参版在
        ///    <c>FontManagerScriptableObject</c> 上、返回 <c>UnityEngine.TextCore.Text.FontAsset</c>，
        ///    **不能直接给 TMP 文本用**。
        /// ③ 扫描已加载 TMP 字体，按目标语言 Unicode 区段给 <c>characterTable</c> 打分
        /// ④ ★ 兜底：用 <c>TMP_FontAsset.CreateFontAsset</c> 从**系统字体**造一个
        ///    —— 登录场景此刻可能还没加载任何 CJK 字体，这一级让没装汉化包的玩家也不出豆腐块
        /// ⑤ 最后交给 <c>TMP_Settings.defaultFontAsset</c>
        /// </summary>
        private static string ResolveFontKey(GameLang lang, out TMP_FontAsset font)
        {
            font = null;

            // ① 汉化包字体
            try
            {
                var custom = TryLoadLocalizeFont();
                if (custom != null)
                {
                    font = custom;
                    return "custom:" + custom.name;
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning("[UiKit] 汉化字体读取失败: " + e.Message);
            }

            // ② 游戏自己的按语言字体（权威来源）
            try
            {
                var manager = UtilityUI.FontManager.Instance;
                if (manager != null)
                {
                    var pair = manager.GetByFontCategory(UtilityUI.FontTypesCategory.SubDefault);
                    var asset = pair.Font;
                    if (asset != null && GlyphCount(asset) > 0 && ProbeScore(asset, lang) > 0)
                        return $"fm:{lang}:{asset.name}";
                }
            }
            catch (Exception e)
            {
                LogOnce("fm:" + lang, "[UiKit] 取游戏当前语言字体失败，改用扫描: " + e.Message);
            }

            // ③ 扫描打分
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                TMP_FontAsset best = null;
                int bestScore = -1, bestTotal = -1;

                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var f = all[i];
                        if (f == null) continue;
                        int total = GlyphCount(f);
                        int score = ProbeScore(f, lang);
                        if (score > bestScore || (score == bestScore && total > bestTotal))
                        {
                            bestScore = score;
                            bestTotal = total;
                            best = f;
                        }
                    }
                }

                if (best != null && bestScore > 0)
                {
                    font = best;
                    return $"scan:{lang}:{best.name}";
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning("[UiKit] 字体枚举失败: " + e.Message);
            }

            // ④ 系统字体兜底（登录场景最可能走到这里）
            try
            {
                var made = MakeSystemFont(lang);
                if (made != null)
                {
                    font = made;
                    return "sys:" + lang + ":" + made.name;
                }
            }
            catch (Exception e)
            {
                LogOnce("sys:" + lang, "[UiKit] 系统字体兜底失败: " + e.Message);
            }

            // ⑤ TMP 默认
            try
            {
                var def = TMP_Settings.defaultFontAsset;
                if (def != null)
                {
                    font = def;
                    return "tmp:" + def.name;
                }
            }
            catch { }

            return null;
        }

        /// <summary>从汉化包加载中文正文字体（复用游戏自己的公开加载入口）。</summary>
        private static TMP_FontAsset TryLoadLocalizeFont()
        {
            if (_noLangDir) return null;   // 没装汉化包就永久跳过，别每帧白扫目录

            string root;
            try { root = Path.Combine(Application.dataPath, "lang"); }
            catch { return null; }

            if (!Directory.Exists(root)) { _noLangDir = true; return null; }

            string[] packs;
            try { packs = Directory.GetDirectories(root); }
            catch { return null; }

            foreach (var pack in packs)
            {
                foreach (var sub in new[] { "Context", "Title" })
                {
                    string p = Path.Combine(pack, "Font", sub, "ChineseFont.ttf");
                    if (!File.Exists(p)) continue;
                    try
                    {
                        // 参数取自游戏 GetByLastSelected 的反编译结果：78 / 5 / 2048
                        if (ProjectMoon.CustomLocalization.CustomLocalizeManager
                                .TryLoadFont(p, 78, 5, 2048, out var font) && font != null)
                        {
                            Plugin.LogInfo("[UiKit] 已加载汉化字体: " + p);
                            return font;
                        }
                    }
                    catch (Exception e)
                    {
                        LogOnce("ttf:" + p, $"[UiKit] TryLoadFont 失败({p}): {e.Message}");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 用系统字体现造一个 TMP 字体资产。
        /// <c>TMP_FontAsset.CreateFontAsset(string familyName, string styleName, int pointSize)</c>
        /// 已在 script.json 中确认有原生实现（0x68BE910 等）。
        /// </summary>
        private static TMP_FontAsset MakeSystemFont(GameLang lang)
        {
            string family = lang == GameLang.KR ? "Malgun Gothic"
                          : lang == GameLang.JP ? "Yu Gothic UI"
                          : lang == GameLang.ZH ? "Microsoft YaHei"
                          : null;
            if (family == null) return null;   // 英文不需要额外字体

            var f = TMP_FontAsset.CreateFontAsset(family, "Regular", 90);
            if (f != null) Plugin.LogInfo($"[UiKit] 已从系统字体创建 TMP 字体: {family}");
            return f;
        }

        private static int GlyphCount(TMP_FontAsset f)
        {
            try { return f?.characterTable == null ? 0 : (int)f.characterTable.Count; }
            catch { return 0; }
        }

        /// <summary>
        /// 统计字体里属于目标语言 Unicode 区段的字形数。
        /// 只读 <c>characterTable</c>，**不用 <c>HasCharacter</c>** ——
        /// 后者对 Dynamic 图集字体会真的把字形渲染进图集，属于有副作用的探测。
        /// </summary>
        private static int ProbeScore(TMP_FontAsset f, GameLang lang)
        {
            if (f == null) return -1;
            try
            {
                var table = f.characterTable;
                if (table == null) return 0;

                int n = table.Count, hit = 0;
                for (int i = 0; i < n; i++)
                {
                    var ch = table[i];
                    if (ch == null) continue;
                    if (InRange(ch.unicode, lang)) hit++;
                }
                return hit;
            }
            catch { return -1; }
        }

        private static bool InRange(uint u, GameLang lang)
        {
            switch (lang)
            {
                case GameLang.KR:
                    return (u >= 0xAC00 && u <= 0xD7A3)      // 谚文音节
                        || (u >= 0x1100 && u <= 0x11FF)      // 谚文字母
                        || (u >= 0x3130 && u <= 0x318F);     // 谚文兼容字母
                case GameLang.JP:
                    return (u >= 0x3040 && u <= 0x30FF)      // 平假名 + 片假名
                        || (u >= 0x4E00 && u <= 0x9FFF);     // 汉字
                case GameLang.ZH:
                    return u >= 0x4E00 && u <= 0x9FFF;       // 汉字
                default:
                    return u >= 0x0020 && u <= 0x007E;       // 基本拉丁
            }
        }

        private static readonly HashSet<string> _logged = new HashSet<string>();

        private static void LogOnce(string tag, string msg)
        {
            if (_logged.Add(tag)) Plugin.LogInfo(msg);
        }

        // ------------------------------------------------------------------
        // 点击：不用 UnityEvent，自己轮询
        // ------------------------------------------------------------------

        private sealed class ClickEntry
        {
            internal RectTransform Rt;
            internal Action Action;
            internal Transform Owner;
        }

        private static readonly List<ClickEntry> _clicks = new List<ClickEntry>();

        /// <summary>
        /// 登记一个可点击区域。
        /// <paramref name="owner"/> 用于整片注销（见 <see cref="ClearClicks"/>），不传就用自己。
        ///
        /// ⚠️ 不用 <c>Button.onClick.AddListener</c>：UnityEvent 是 Il2Cpp 委托，
        /// 塞托管 lambda 进去在 IL2CPP 下**不保证被原生侧回调**（RPGHelper / LimiNexDebug
        /// 都实测过「不报错但永不回调」），所以一律自己轮询。
        /// </summary>
        internal static void RegisterClick(RectTransform rt, Action action, Transform owner = null)
        {
            if (rt == null || action == null) return;
            _clicks.Add(new ClickEntry { Rt = rt, Action = action, Owner = owner ?? rt });
        }

        /// <summary>注销某棵子树上的全部点击登记（面板重建时必须调，否则会泄漏 + 命中已销毁对象）。</summary>
        internal static void ClearClicks(Transform root)
        {
            if (root == null) return;
            for (int i = _clicks.Count - 1; i >= 0; i--)
            {
                var o = _clicks[i].Owner;
                if (o == null || o == root || o.IsChildOf(root)) _clicks.RemoveAt(i);
            }
        }

        /// <summary>
        /// 每帧调用一次：鼠标左键刚按下时，**倒序**（后画的在上）找第一个命中的可点击区域并执行。
        /// 返回 true 表示这次按下已被消费。
        /// </summary>
        internal static bool TickClicks()
        {
            if (_clicks.Count == 0) return false;
            if (!Input.GetMouseButtonDown(0)) return false;

            Vector3 mp = Input.mousePosition;
            for (int i = _clicks.Count - 1; i >= 0; i--)
            {
                var e = _clicks[i];
                try
                {
                    if (e.Rt == null) { _clicks.RemoveAt(i); continue; }
                    if (!e.Rt.gameObject.activeInHierarchy) continue;
                    // Overlay 画布 → camera 传 null
                    if (!RectTransformUtility.RectangleContainsScreenPoint(e.Rt, mp, null)) continue;
                }
                catch
                {
                    _clicks.RemoveAt(i);   // 对象已销毁 / 判定炸了 → 直接注销
                    continue;
                }

                try { e.Action(); }
                catch (Exception ex) { Plugin.LogError("[UiKit] 点击回调异常: " + ex); }
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 基础构件
        // ------------------------------------------------------------------

        /// <summary>
        /// 建一个带 RectTransform 的空节点。
        /// ⚠️ IL2CPP 下 <c>new GameObject(name, typeof(RectTransform))</c> 编译不过，必须分两步。
        /// </summary>
        internal static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            if (parent != null) go.transform.SetParent(parent, false);
            return rt;
        }

        /// <summary>铺满父节点。</summary>
        internal static void Stretch(RectTransform rt, float pad = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        internal static Image NewImage(string name, Transform parent, Color color, bool rounded = false)
        {
            var rt = NewRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            var sp = rounded ? Rounded : White;
            img.sprite = sp;                 // sprite 为 null 时 Image 画纯色矩形，是安全退化路径
            img.color = color;
            img.type = (rounded && sp != null) ? Image.Type.Sliced : Image.Type.Simple;
            img.raycastTarget = true;
            return img;
        }

        internal static TextMeshProUGUI NewText(string name, Transform parent, string text,
                                               float size, Color color,
                                               TextAlignmentOptions align = TextAlignmentOptions.TopLeft,
                                               bool wrap = true)
        {
            var rt = NewRect(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text ?? "";
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.enableWordWrapping = wrap;
            t.overflowMode = TextOverflowModes.Overflow;

            var f = UiFont;
            if (f != null) t.font = f;

            _texts.Add(t);      // 登记，字体换到 CJK 后统一回填
            return t;
        }

        /// <summary>占位/间距节点。</summary>
        internal static LayoutElement NewSpacer(Transform parent, float height)
        {
            var rt = NewRect("spacer", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return le;
        }

        /// <summary>定高节点（给 Text / Image 用，配合 VerticalLayoutGroup）。</summary>
        internal static LayoutElement SetHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return le;
        }

        /// <summary>
        /// 粗估一段文字在给定宽度下要几行 —— 用来给 Label 定高。
        /// 不这么做的话，长文案（如「只获取到一台服务器的缓存索引…」那段）
        /// 在 TMP 开了 wordWrap + Overflow 时会**溢出压到下面的控件上**。
        /// </summary>
        internal static float EstimateHeight(string s, float size, float width)
        {
            if (string.IsNullOrEmpty(s)) return size + 8f;
            if (width <= 1f) width = 600f;

            float charW = size * 0.56f;      // 拉丁字符宽度
            float cjkW = size * 1.02f;       // 中日韩全角
            int lines = 1;
            float cur = 0f;
            foreach (char ch in s)
            {
                if (ch == '\n') { lines++; cur = 0f; continue; }
                float w = ch >= '\u2000' ? cjkW : charW;
                cur += w;
                if (cur > width) { lines++; cur = w; }
            }
            return lines * (size * 1.34f) + 8f;
        }

        /// <summary>
        /// 建一个「圆角底 + Button 组件 + 居中 TMP 文本」的按钮。
        /// Button 组件本身保留（它的按下变色是原生实现，视觉反馈还能用），
        /// 但**点击一律走 <see cref="RegisterClick"/>**，onClick 里什么都不挂。
        /// </summary>
        internal static Button NewButton(Transform parent, string label, float height,
                                        Color bg, Action onClick, Transform owner = null)
        {
            var img = NewImage("btn:" + label, parent, bg, rounded: true);
            var rt = img.rectTransform;
            SetHeight(img.gameObject, height);

            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.22f, 1.22f, 1.22f, 1f);
            colors.pressedColor = new Color(0.76f, 0.76f, 0.76f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.4f, 0.4f, 0.4f, 0.7f);
            colors.fadeDuration = 0.06f;
            btn.colors = colors;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };

            var t = NewText("label", img.transform, label, 16f, TextCol, TextAlignmentOptions.Center);
            Stretch(t.rectTransform, 6f);

            if (onClick != null) RegisterClick(rt, onClick, owner);
            return btn;
        }

        /// <summary>
        /// 建一个纵向内容容器：VerticalLayoutGroup + ContentSizeFitter（高度随内容撑开）。
        /// 返回容器节点，往它下面塞东西即可。
        /// </summary>
        internal static RectTransform NewVBox(string name, Transform parent, float spacing,
                                             RectOffset padding = null, bool fitHeight = true)
        {
            var rt = NewRect(name, parent);
            var vlg = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = spacing;
            vlg.padding = padding ?? new RectOffset(0, 0, 0, 0);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            if (fitHeight)
            {
                var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            return rt;
        }
    }
}
