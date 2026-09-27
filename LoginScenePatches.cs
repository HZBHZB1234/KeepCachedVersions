using System;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine.Events;

namespace KeepCachedVersions
{
    /// <summary>
    /// 开始页(LoginSceneManager)按钮改造:
    ///  1) 把 "Clear all caches" 按钮文案改为当前语言的 "Clear cache";
    ///  2) 把点击事件替换为本 mod 的处理函数(弹模态窗口), 原事件(打开游戏自带
    ///     ClearAllCachePopup)保留, 由模态窗口的"清除全部缓存"触发。
    /// </summary>
    public static class LoginScenePatches
    {
        internal static LoginSceneManager CurrentScene;

        // LoginSceneManager.Start 后置补丁: 改文案 + 换事件
        public static void StartPostfix(LoginSceneManager __instance)
        {
            try
            {
                CurrentScene = __instance;

                var btn = __instance.btn_allCacheClear;
                if (btn == null)
                {
                    Plugin.LogWarning("[KeepCachedVersions] btn_allCacheClear not found");
                    return;
                }

                // 1) 按钮文案 -> 当前语言的 "Clear cache"
                var lang = Strings.DetectLanguage();
                var tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp == null)
                {
                    // 兜底: 在整个登录场景里找当前仍是"清除全部缓存"文案的 TMP
                    var all = __instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                    tmp = all.FirstOrDefault(t =>
                        t.text != null && (
                            t.text.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.text.IndexOf("캐시", StringComparison.Ordinal) >= 0 ||
                            t.text.IndexOf("キャッシュ", StringComparison.Ordinal) >= 0));
                }
                if (tmp != null)
                {
                    tmp.text = Strings.ClearCacheButton(lang);
                }
                else
                {
                    Plugin.LogWarning("[KeepCachedVersions] button label TMP not found; label left as-is");
                }

                // 2) 点击事件 -> 我们的处理函数(替换游戏原有的监听)
                var action = DelegateSupport.ConvertDelegate<UnityAction>(new Action(OnClearCacheClicked));
                if (action != null)
                {
                    btn.SetEventSafety(action, false);
                }
                else
                {
                    Plugin.LogWarning("[KeepCachedVersions] failed to build UnityAction; original handler kept");
                }
            }
            catch (Exception e)
            {
                Plugin.LogError($"[KeepCachedVersions] LoginSceneManager.Start postfix failed: {e}");
            }
        }

        // 我们的按钮点击: 打开模态窗口
        public static void OnClearCacheClicked()
        {
            try
            {
                if (CacheCleanupUI.Instance == null)
                {
                    Plugin.LogWarning("[KeepCachedVersions] UI not ready");
                    return;
                }
                CacheCleanupUI.Instance.OpenModal(CurrentScene);
            }
            catch (Exception e)
            {
                Plugin.LogError($"[KeepCachedVersions] OnClearCacheClicked failed: {e}");
            }
        }

        // 原事件: 打开游戏自带的 ClearAllCachePopup(其 OK 触发 AddressableManager.ClearAllCache 全清)
        public static void RunOriginalClearAll()
        {
            try
            {
                if (CurrentScene == null)
                {
                    Plugin.LogError("[KeepCachedVersions] no login scene reference");
                    return;
                }
                var popup = CurrentScene._clearCachePopup;
                if (popup == null)
                {
                    Plugin.LogError("[KeepCachedVersions] _clearCachePopup not found");
                    return;
                }
                popup.Open();
            }
            catch (Exception e)
            {
                Plugin.LogError($"[KeepCachedVersions] RunOriginalClearAll failed: {e}");
            }
        }
    }
}
