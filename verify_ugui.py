#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
verify_ugui.py —— KeepCachedVersions 构建前校验。

两件事：

1. **uGUI / TMP 依赖是否被剥离**
   读 Il2CppDumper 的 script.json，逐项确认本插件用到的成员存在且 Address != 0。
   背景：本作 IL2CPP 把 IMGUI 的原生实现整段剥掉了，托管签名却还在 ——
   编译得过、运行期必抛 `System.NotSupportedException: Method unstripping failed`。
   （v0.2.0 的 OnGUI 就是这么翻车的，见 docs/KeepCachedVersions-IMGUI-STRIPPED-FIX-PLAN.md）

   ⚠️ 口径提醒：本脚本只能回答「元数据里有没有这个方法、Address 是不是 0」。
   本作还存在第二种失效模式：条目在、Address 非 0，但指向**共享空桩**
   （见 mods/VirtualPatchProbe/README.md，0x834160 被 3 万多个方法共用）。
   所以「静态通过」不等于「运行期一定可用」，最终仍需实机日志确认。

2. **源码里不许再出现 IMGUI**
   扫描本目录 *.cs，命中 OnGUI / GUI. / GUILayout / GUIStyle / GUISkin / GUIContent /
   FontStyle 等关键字直接 FAIL。

用法：
  python verify_ugui.py
退出码 0 = 通过。
"""
import io
import json
import os
import re
import sys

SCRIPT = r"E:\desktop\work\LimbusDecompile\Il2CppDumper-release\script.json"
HERE = os.path.dirname(os.path.abspath(__file__))

# (类型, [方法名...])  —— 方法名传 None 表示"只看类型是否存在"
#
# ⚠️ 属性 setter 常声明在**基类**上（TMP_Text 声明 text/fontSize/color/font/alignment，
#    Graphic 声明 color/raycastTarget，Transform 声明 SetParent），只查派生类会误报缺失，
#    所以一律写**声明类型**。
NEEDS = [
    # 画布 / 输入
    ("UnityEngine.Canvas", ["set_renderMode", "set_sortingOrder"]),
    ("UnityEngine.UI.CanvasScaler", ["set_uiScaleMode", "set_scaleFactor"]),
    ("UnityEngine.UI.GraphicRaycaster", None),
    ("UnityEngine.EventSystems.EventSystem", None),
    ("UnityEngine.EventSystems.StandaloneInputModule", None),

    # Transform / RectTransform
    ("UnityEngine.Transform", ["SetParent", "get_childCount", "GetChild", "IsChildOf"]),
    ("UnityEngine.RectTransform", ["set_anchorMin", "set_anchorMax", "set_pivot",
                                   "set_anchoredPosition", "set_sizeDelta", "set_offsetMin",
                                   "set_offsetMax"]),
    ("UnityEngine.RectTransformUtility", ["RectangleContainsScreenPoint"]),

    # uGUI 组件（属性多来自基类 Graphic / Selectable / LayoutGroup）
    ("UnityEngine.UI.Graphic", ["set_color", "set_raycastTarget", "get_rectTransform"]),
    ("UnityEngine.UI.Image", ["set_sprite", "set_type"]),
    ("UnityEngine.UI.Selectable", ["set_targetGraphic", "set_colors", "set_navigation"]),
    ("UnityEngine.UI.Button", None),
    ("UnityEngine.UI.LayoutElement", ["set_minHeight", "set_preferredHeight"]),
    ("UnityEngine.UI.LayoutRebuilder", ["ForceRebuildLayoutImmediate"]),
    ("UnityEngine.UI.VerticalLayoutGroup", None),
    ("UnityEngine.UI.HorizontalOrVerticalLayoutGroup",
        ["set_spacing", "set_childControlWidth", "set_childControlHeight",
         "set_childForceExpandWidth", "set_childForceExpandHeight"]),
    ("UnityEngine.UI.ContentSizeFitter", ["set_verticalFit", "set_horizontalFit"]),
    ("UnityEngine.UI.LayoutGroup", ["set_padding", "set_childAlignment"]),

    # 贴图 / Sprite
    ("UnityEngine.Sprite", ["Create"]),
    ("UnityEngine.Texture2D", ["SetPixel", "Apply", "get_whiteTexture"]),
    ("UnityEngine.Resources", ["FindObjectsOfTypeAll"]),
    ("UnityEngine.Object", ["DontDestroyOnLoad", "FindObjectOfType", "Destroy", "set_hideFlags"]),
    ("UnityEngine.GameObject", ["AddComponent", "SetActive", "get_activeInHierarchy"]),
    ("UnityEngine.Component", ["get_gameObject", "get_transform"]),

    # TMP（text/fontSize/color/font/alignment 都在 TMP_Text 上；raycastTarget 在 Graphic 上）
    ("TMPro.TMP_FontAsset", ["CreateFontAsset"]),
    ("TMPro.TMP_Settings", ["get_defaultFontAsset"]),
    ("TMPro.TMP_Text", ["set_text", "set_fontSize", "set_color", "set_font", "set_alignment",
                        "set_enableWordWrapping", "set_overflowMode"]),
    ("TMPro.TextMeshProUGUI", None),

    # 运行时
    ("UnityEngine.Application", ["get_dataPath"]),
    ("UnityEngine.Input", ["GetKeyDown", "get_mousePosition", "GetMouseButton",
                           "GetMouseButtonDown"]),
    ("UnityEngine.Mathf", ["Max", "Min", "Sqrt", "Clamp01"]),
    ("UnityEngine.Screen", ["get_width", "get_height"]),

    # 游戏侧（字体解析 / 场景）
    # ⚠️ FontManager : SingletonBehavior<FontManager>，Instance 访问器声明在**泛型基类**上，
    #    所以查 SingletonBehavior<object>$$get_Instance，不能查 FontManager$$get_Instance。
    ("SingletonBehavior<object>", ["get_Instance"]),
    ("UtilityUI.FontManager", ["GetByFontCategory"]),
    ("ProjectMoon.CustomLocalization.CustomLocalizeManager", ["TryLoadFont"]),
]

# 源码里出现即判失败的 IMGUI 关键字（正则）。
#
# ⚠️ TextAnchor **不在此列**：它是 UnityEngine.CoreModule 的文本对齐枚举
#    （与 TextAlignmentOptions / TextMeshProUGUI 同属 uGUI 阵营），不是 IMGUI 的。
#    早期本脚本误把 IMGUI 的 TextAnchor 当禁词，已修正。
FORBIDDEN = [
    (r"\bOnGUI\s*\(", "OnGUI 回调"),
    (r"\bGUILayout\b", "GUILayout"),
    (r"\bGUIStyle\b", "GUIStyle"),
    (r"\bGUISkin\b", "GUISkin"),
    (r"\bGUIContent\b", "GUIContent"),
    (r"\bGUIStyleState\b", "GUIStyleState"),
    (r"\bGUI\s*\.", "GUI.* 静态调用"),
    (r"\bFontStyle\b", "FontStyle"),
    (r"\bEvent\s*\.\s*current\b", "Event.current"),
    (r"\bMonoBehaviour\s*\.\s*OnGUI\b", "MonoBehaviour.OnGUI"),
]

SKIP_DIRS = {"bin", "obj", ".git"}


def load_script():
    with io.open(SCRIPT, "r", encoding="utf-8", errors="replace") as f:
        return json.load(f)


def check_api(idx):
    missing = []
    for typ, methods in NEEDS:
        if methods is None:
            exists = any(k.startswith(typ + "$$") for k in idx)
            print(f"[{'OK  ' if exists else 'MISS'}] {typ}")
            if not exists:
                missing.append(typ)
            continue

        bad = [m for m in methods if not idx.get(f"{typ}$${m}")]
        tag = "OK  " if not bad else "MISS"
        print(f"[{tag}] {typ}  ({len(methods) - len(bad)}/{len(methods)})")
        for b in bad:
            print(f"         !! 缺失/被剥离: {b}")
            missing.append(f"{typ}.{b}")
    return missing


def iter_sources():
    for root, dirs, files in os.walk(HERE):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for fn in files:
            if fn.endswith(".cs"):
                yield os.path.join(root, fn)


def check_declaring_types(idx):
    """
    自检 NEEDS 里写的**声明类型**是否真的存在于元数据。

    加这一条是因为本脚本第一版踩过坑：把 `get_rectTransform` 写在 `Image` 下、
    把 `set_childAlignment` 写在 `HorizontalOrVerticalLayoutGroup` 下、
    把 `get_Instance` 写在 `FontManager` 下 —— 这些成员其实分别声明在
    基类 `Graphic` / `LayoutGroup` / 泛型基类 `SingletonBehavior<object>` 上，
    于是全部误报「缺失/被剥离」，白白浪费一轮排查。

    成员缺失是真问题；**整个类型名都查不到**则八成是这里写错了，先怀疑本脚本。
    """
    bad = []
    for typ, _ in NEEDS:
        if not any(k.startswith(typ + "$$") for k in idx):
            bad.append(typ)
    return bad


def check_sources():
    hits = []
    for path in iter_sources():
        try:
            with io.open(path, "r", encoding="utf-8", errors="replace") as f:
                for lineno, line in enumerate(f, 1):
                    # 跳过注释行，允许在注释里讨论 IMGUI
                    stripped = line.lstrip()
                    if (stripped.startswith("//") or stripped.startswith("*")
                            or stripped.startswith("/*")):
                        continue
                    for pat, label in FORBIDDEN:
                        if re.search(pat, line):
                            hits.append((os.path.relpath(path, HERE), lineno, label, line.strip()))
        except Exception as e:
            print(f"  (读取失败 {path}: {e})")
    return hits


def main():
    print("=" * 62)
    print("[1/2] 校验 uGUI / TMP 依赖（script.json）")
    print("=" * 62)
    idx = {}
    for m in load_script()["ScriptMethod"]:
        n = m.get("Name")
        if n:
            idx[n] = m.get("Address") or 0

    missing = check_api(idx)

    print()
    print("=" * 62)
    print("[1b/2] 自检：NEEDS 里的声明类型是否存在")
    print("=" * 62)
    bad_types = check_declaring_types(idx)
    if bad_types:
        for t in bad_types:
            print(f"  !! 元数据里找不到类型: {t}")
            print(f"     → 八成是本脚本写错了声明类型（属性常声明在基类/泛型基类上）")
    else:
        print("  声明类型全部存在 ✓")

    print()
    print("=" * 62)
    print("[2/2] 反查源码里的 IMGUI 残留")
    print("=" * 62)
    hits = check_sources()
    if hits:
        for rel, lineno, label, text in hits:
            print(f"  !! {rel}:{lineno}  [{label}]  {text}")
    else:
        print("  未发现 IMGUI 用法 ✓")

    print()
    if missing or hits or bad_types:
        if missing:
            print(f"缺失/被剥离 {len(missing)} 项：")
            for m in missing:
                print("  -", m)
        if bad_types:
            print(f"声明类型写错 {len(bad_types)} 项：{', '.join(bad_types)}")
        if hits:
            print(f"IMGUI 残留 {len(hits)} 处（本作 IMGUI 已被 IL2CPP 剥离，必抛 NotSupportedException）")
        return 1

    print("全部可用 ✓  （提醒：静态通过 ≠ 运行期可用，仍需实机日志确认）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
