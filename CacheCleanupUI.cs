using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KeepCachedVersions
{
    /// <summary>
    /// 注入到 IL2CPP 域的 MonoBehaviour，用 **uGUI + TextMeshPro** 画"清除缓存"模态窗口。
    /// 创建方式见 <c>Plugin.Load</c>：ClassInjector.RegisterTypeInIl2Cpp + GameObject.AddComponent。
    ///
    /// <b>为什么不是 OnGUI（IMGUI）</b>
    /// v0.2.0 原本用 IMGUI 写这个窗口，结果每帧抛
    /// <c>System.NotSupportedException: Method unstripping failed</c>，
    /// 窗口永远画不出来，日志被刷 2520 行。
    /// 根因是本作 IL2CPP 把 IMGUI 的原生实现整段剥掉了（<c>GUIStyle.padding/font/…</c>、
    /// <c>GUILayout.FlexibleSpace</c>、<c>GUI.DrawTexture</c> 等，见 <c>tools/_probe_imgui.py</c>），
    /// 而 uGUI 是游戏自己的 UI 体系、方法全部健在。
    /// 判定与校验见 <c>verify_ugui.py</c>。
    ///
    /// 线程模型：<c>CacheCleaner.Run</c> 在后台线程跑，只写 <see cref="CleanupState"/> 的
    /// volatile 字段；本类在 <c>Update()</c>（主线程）用脏标记轮询，变了才重建内容。
    /// **所有 Unity API 调用都在主线程。**
    /// </summary>
    public class CacheCleanupUI : MonoBehaviour
    {
        // Il2CppInterop 注入类型必需的外显构造函数
        public CacheCleanupUI(IntPtr ptr) : base(ptr) { }

        internal static CacheCleanupUI Instance;

        // ---- 布局常量 ----
        private const float PanelW = 720f;
        private const float PadX = 20f;
        private const float PadY = 16f;
        private const float ContentW = PanelW - PadX * 2f;

        // ---- 状态 ----
        private bool _open;
        private GameLang _lang;
        private readonly CleanupState _state = new CleanupState();

        // ---- 已构建的 UI ----
        private Canvas _canvas;
        private RectTransform _root;        // 遮罩（全屏，raycastTarget=true → 模态遮挡）
        private RectTransform _panel;       // 面板
        private RectTransform _body;        // 内容区（每次重建；同时兼作拖动把手）

        // ---- 脏标记：只在内容真的变了才重建 ----
        private bool _dirty;
        private int _shownPhase = -1;
        private int _shownScanned = -1;
        private int _shownDeleted = -1;
        private string _shownMessage = "";

        // ---- 面板拖动 ----
        private bool _dragging;
        private Vector3 _dragStartMouse;
        private Vector2 _dragStartPanel;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        internal bool IsOpen => _open;

        /// <summary>打开模态窗口（由按钮点击触发）。</summary>
        internal void OpenModal(LoginSceneManager scene)
        {
            try
            {
                LoginScenePatches.CurrentScene = scene;
                _lang = Strings.DetectLanguage();

                _state.Phase = (int)CleanupPhase.Idle;
                _state.Message = "";
                _state.NeedOtherIndex = false;
                _state.DryRun = Plugin.DryRun.Value;
                _state.TotalEntries = 0;
                _state.Scanned = 0;
                _state.DeletedEntries = 0;
                _state.DeletedBytes = 0;

                _open = true;
                _shownPhase = -1;       // 强制重建
                _dirty = true;
                EnsureShell();
                _root.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.LogError("[KeepCachedVersions] OpenModal failed: " + e);
            }
        }

        private void Update()
        {
            if (!_open) return;

            // 1) Esc 关闭（Input.GetKeyDown 已确认未被剥离）
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }

            // 2) 点击判定（主线程，手动矩形命中；见 UiKit.RegisterClick 的注释）
            //    返回 true = 这次按下打在按钮上，已被消费 → 不要再拿去起拖动
            bool consumed = UiKit.TickClicks();

            // 3) 后台进度变了 → 重建内容
            if (_state.Phase != _shownPhase
                || _state.Scanned != _shownScanned
                || _state.DeletedEntries != _shownDeleted
                || !string.Equals(_state.Message, _shownMessage, StringComparison.Ordinal))
            {
                _dirty = true;
            }

            if (_dirty) RebuildBody();

            // 4) 面板拖动（按住内容区空白处）
            HandleDrag(consumed);
        }

        private void Close()
        {
            _open = false;
            _dragging = false;
            if (_root != null) _root.gameObject.SetActive(false);
            UiKit.ClearClicks(_root);
            UiKit.PruneTexts();
        }

        // ------------------------------------------------------------------
        // 骨架：遮罩 + 面板 + 标题栏 + 内容区（只建一次）
        // ------------------------------------------------------------------

        private void EnsureShell()
        {
            if (_root != null) return;

            _canvas = UiKit.EnsureCanvas();
            if (_canvas == null) throw new InvalidOperationException("Canvas 不可用");

            // 全屏遮罩：raycastTarget = true → 挡住游戏 UI 的点击，天然实现模态语义
            var backdrop = UiKit.NewImage("Backdrop", _canvas.transform, UiKit.Backdrop, rounded: false);
            _root = backdrop.rectTransform;
            UiKit.Stretch(_root);

            // 面板：圆角底 + 竖排布局 + 高度自适应
            var panelImg = UiKit.NewImage("Panel", _root, UiKit.PanelBg, rounded: true);
            _panel = panelImg.rectTransform;
            _panel.anchorMin = new Vector2(0.5f, 0.5f);
            _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.anchoredPosition = Vector2.zero;
            _panel.sizeDelta = new Vector2(PanelW, 320f);

            var vlg = _panel.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f;
            vlg.padding = new RectOffset((int)PadX, (int)PadX, (int)PadY, (int)PadY);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            var fitter = _panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 内容区：兼作拖动把手（按住面板任意空白处即可拖动）
            _body = UiKit.NewVBox("Body", _panel, 6f, null, fitHeight: true);
        }

        // ------------------------------------------------------------------
        // 内容：按阶段重建
        // ------------------------------------------------------------------

        private void RebuildBody()
        {
            _dirty = false;

            _shownPhase = _state.Phase;
            _shownScanned = _state.Scanned;
            _shownDeleted = _state.DeletedEntries;
            _shownMessage = _state.Message;

            if (_body == null) return;

            // 清掉上一轮的内容与点击登记
            UiKit.ClearClicks(_body);
            for (int i = _body.childCount - 1; i >= 0; i--)
            {
                var c = _body.GetChild(i);
                c.SetParent(null);
                UnityEngine.Object.Destroy(c.gameObject);
            }
            UiKit.PruneTexts();

            int phase = _state.Phase;

            // ---- 标题 ----
            var title = UiKit.NewText("Title", _body, Strings.ModalTitle(_lang), 22f,
                UiKit.TextCol, TextAlignmentOptions.Center, wrap: false);
            UiKit.SetHeight(title.gameObject, 30f);
            UiKit.NewSpacer(_body, 12f);

            // ---- 正文 ----
            switch (phase)
            {
                case (int)CleanupPhase.Idle:
                    BuildIdle();
                    break;
                case (int)CleanupPhase.Fetching:
                    BuildLabel(Strings.PhaseFetching(_lang), 15f, UiKit.TextDim);
                    UiKit.NewSpacer(_body, 8f);
                    BuildHint(Strings.ResultIdleHint(_lang));
                    break;
                case (int)CleanupPhase.Deleting:
                    BuildLabel(Strings.PhaseDeleting(_lang), 15f, UiKit.TextDim);
                    UiKit.NewSpacer(_body, 6f);
                    BuildLabel($"    {_state.Scanned} / {_state.TotalEntries}", 15f, UiKit.TextCol);
                    break;
                case (int)CleanupPhase.Done:
                    BuildLabel(
                        _state.DryRun
                            ? Strings.ResultDry(_lang, _state.DeletedEntries, _state.DeletedBytes / 1048576)
                            : Strings.ResultDone(_lang, _state.DeletedEntries, _state.DeletedBytes / 1048576),
                        15f, UiKit.TextCol);
                    UiKit.NewSpacer(_body, 12f);
                    BuildCloseButton();
                    break;
                default: // Failed
                    BuildLabel(Strings.ResultFailed(_lang), 16f, UiKit.Accent);
                    UiKit.NewSpacer(_body, 6f);
                    BuildLabel(
                        _state.NeedOtherIndex ? Strings.NotifyNeedOtherIndex(_lang) : _state.Message,
                        14f, UiKit.TextDim);
                    UiKit.NewSpacer(_body, 12f);
                    BuildCloseButton();
                    break;
            }

            // ---- 让 ContentSizeFitter 立刻按新内容更新（否则面板高度要等下一帧）----
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(_panel); }
            catch { /* 布局重建失败不影响功能 */ }
        }

        private void BuildIdle()
        {
            bool dry = _state.DryRun;

            UiKit.NewButton(_body, Strings.BtnClearUseless(_lang), 46f, UiKit.BtnBgPrimary,
                StartUselessClear, _body);
            UiKit.NewSpacer(_body, 8f);

            UiKit.NewButton(_body, Strings.BtnClearAll(_lang), 46f, UiKit.BtnBgDanger, () =>
            {
                Close();
                LoginScenePatches.RunOriginalClearAll();   // 原事件：打开游戏自带确认弹窗
            }, _body);

            UiKit.NewSpacer(_body, 14f);

            if (dry) BuildHint(Strings.DryRunHint(_lang));

            BuildCloseButton();
        }

        private void BuildCloseButton()
        {
            UiKit.NewButton(_body, Strings.BtnClose(_lang), 36f, UiKit.BtnBg, Close, _body);
        }

        /// <summary>定高文本（按内容估算高度，避免长文案溢出压到下一个控件）。</summary>
        private TextMeshProUGUI BuildLabel(string text, float size, Color color)
        {
            var t = UiKit.NewText("Label", _body, text, size, color,
                TextAlignmentOptions.TopLeft, wrap: true);
            UiKit.SetHeight(t.gameObject, UiKit.EstimateHeight(text, size, ContentW));
            return t;
        }

        private void BuildHint(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var t = UiKit.NewText("Hint", _body, text, 12f, UiKit.TextDim,
                TextAlignmentOptions.TopLeft, wrap: true);
            UiKit.SetHeight(t.gameObject, UiKit.EstimateHeight(text, 12, ContentW));
        }

        // ------------------------------------------------------------------
        // 后台任务
        // ------------------------------------------------------------------

        private void StartUselessClear()
        {
            _state.Phase = (int)CleanupPhase.Fetching;
            _state.Message = "";
            _state.NeedOtherIndex = false;
            _state.Scanned = 0;
            _state.DeletedEntries = 0;
            _state.DeletedBytes = 0;
            _dirty = true;

            var st = _state;   // 后台线程只碰这个对象（字段全是 volatile）
            Task.Run(() => CacheCleaner.Run(st));
        }

        // ------------------------------------------------------------------
        // 拖动
        // ------------------------------------------------------------------

        private void HandleDrag(bool clickConsumed)
        {
            try
            {
                if (!_dragging)
                {
                    if (clickConsumed || !Input.GetMouseButtonDown(0) || _panel == null || _body == null)
                        return;
                    // 只在按住内容区时开始拖动（按钮的按下已被 UiKit.TickClicks 消费）
                    if (!RectTransformUtility.RectangleContainsScreenPoint(
                            _body, Input.mousePosition, null)) return;
                    _dragging = true;
                    _dragStartMouse = Input.mousePosition;
                    _dragStartPanel = _panel.anchoredPosition;
                    return;
                }

                if (!Input.GetMouseButton(0))
                {
                    _dragging = false;
                    return;
                }

                Vector3 d = Input.mousePosition - _dragStartMouse;
                _panel.anchoredPosition = new Vector2(_dragStartPanel.x + d.x, _dragStartPanel.y + d.y);
            }
            catch (Exception e)
            {
                _dragging = false;
                Plugin.LogWarning("[UiKit] 面板拖动异常: " + e.Message);
            }
        }
    }
}
