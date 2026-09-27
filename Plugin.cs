using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace KeepCachedVersions
{
    // 1) 双服 bundle 缓存共存: 阻止下载流程内的自动"删旧版本"行为:
    //    - UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)  -> no-op
    //    - Addressable.AddressableManager.ClearOldCache()                  -> no-op  ★本次修复的重点
    //    - UnityEngine.Caching.ClearCachedVersionInternal(string, Hash128)  -> no-op  (兜底)
    // 2) 开始页 "Clear all caches" 按钮 -> "Clear cache": 点击弹模态窗口,
    //    - 清除全部缓存: 调用原事件(游戏自带 ClearAllCachePopup);
    //    - 清除无用缓存: 请求官方+私服 catalog 索引, 删除两服索引都不包含的缓存条目。
    [BepInPlugin("com.limbusmods.keepcachedversions", "KeepCachedVersions", "0.2.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource LogInstance;
        private static int _suppressed;
        private static int _suppressedOldCache;
        private static int _suppressedInternal;

        internal static void LogInfo(string msg) => LogInstance?.LogInfo(msg);
        internal static void LogWarning(string msg) => LogInstance?.LogWarning(msg);
        internal static void LogError(string msg) => LogInstance?.LogError(msg);

        // ---- 配置 ----
        internal static ConfigEntry<string> OtherServerSettingsPath;
        internal static ConfigEntry<bool> AllowSingleIndex;
        internal static ConfigEntry<bool> DryRun;
        internal static ConfigEntry<string> CdnHeaderValue;

        public override void Load()
        {
            LogInstance = base.Log;

            OtherServerSettingsPath = Config.Bind("General", "OtherServerSettingsPath",
                @"E:\desktop\work\LimbusDecompile\LetheLauncher-Distribution-7\LimbusCompany_Data\StreamingAssets\aa\settings.json",
                "另一服(官服/私服)的 settings.json 路径; 可填文件、aa 目录或游戏根目录。留空则仅自动探测 Steam 标准安装路径。");
            AllowSingleIndex = Config.Bind("General", "AllowSingleIndex", false,
                "false: 必须同时拿到两服索引才执行删除(安全, 推荐); true: 仅凭一份索引也执行(会把另一服独有 bundle 当无用删掉)。");
            DryRun = Config.Bind("General", "DryRun", false,
                "true: 清除无用缓存时只统计将删除的条目, 不实际删除。");
            CdnHeaderValue = Config.Bind("General", "CdnHeaderValue", "this_is_header_value",
                "请求 CDN catalog 时的 X-Requested-With 值。");

            try
            {
                var harmony = new Harmony("com.limbusmods.keepcachedversions");

                // 1a) 原有的 no-delete 补丁: 3 参 ClearCachedVersions
                var target = FindClearCachedVersions();
                if (target == null)
                {
                    LogInstance.LogWarning(
                        "[KeepCachedVersions] UnityEngine.Caching.ClearCachedVersions not found; no-delete patch NOT applied");
                }
                else
                {
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(PrefixClearCachedVersions)));
                    LogInstance.LogInfo(
                        "[KeepCachedVersions] no-delete patch active: ClearCachedVersions is a no-op");
                }

                // 1b) ★本次修复的重点: AddressableManager.ClearOldCache()
                //     该函数遍历 Caching.GetCachedVersions(), 删掉所有"不在当前 catalog 里"的版本 ——
                //     而双服场景下另一服的 bundle 天然不在当前 catalog, 必然被删。
                //     它在 DownloadProcess / PrevDownloadProcess 内被调用, 且走的是 2 参
                //     ClearCachedVersionInternal, 完全绕过 1a 的补丁, 这才是真正的元凶。
                var oldCache = FindClearOldCache();
                if (oldCache == null)
                {
                    LogInstance.LogWarning(
                        "[KeepCachedVersions] AddressableManager.ClearOldCache not found; old-cache deletion NOT blocked");
                }
                else
                {
                    harmony.Patch(oldCache,
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(PrefixClearOldCache)));
                    LogInstance.LogInfo(
                        "[KeepCachedVersions] old-cache patch active: AddressableManager.ClearOldCache is a no-op");
                }

                // 1c) 兜底: 2 参 ClearCachedVersionInternal 也打成 no-op
                //     覆盖 WebRequestOperationCompleted 重试分支(1868872AF)等其它调用点。
                var internalTarget = FindClearCachedVersionInternal();
                if (internalTarget == null)
                {
                    LogInstance.LogWarning(
                        "[KeepCachedVersions] Caching.ClearCachedVersionInternal not found; fallback patch NOT applied");
                }
                else
                {
                    harmony.Patch(internalTarget,
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(PrefixClearCachedVersionInternal)));
                    LogInstance.LogInfo(
                        "[KeepCachedVersions] fallback patch active: ClearCachedVersionInternal is a no-op");
                }

                // 2) 开始页按钮改造
                var start = AccessTools.Method(typeof(LoginSceneManager), "Start");
                if (start != null)
                {
                    harmony.Patch(start,
                        postfix: new HarmonyMethod(typeof(LoginScenePatches), nameof(LoginScenePatches.StartPostfix)));
                    LogInstance.LogInfo(
                        "[KeepCachedVersions] login scene patch active: 'Clear cache' button + cleanup modal");
                }
                else
                {
                    LogInstance.LogError(
                        "[KeepCachedVersions] LoginSceneManager.Start not found; button patch NOT applied");
                }

                // 3) 模态窗口 UI（注入的 MonoBehaviour，uGUI + TMP）
                //    ⚠️ 不能用 IMGUI/OnGUI：本作 IL2CPP 把 IMGUI 的原生实现整段剥掉了
                //    （GUIStyle.padding/font、GUILayout.FlexibleSpace、GUI.DrawTexture …），
                //    v0.2.0 因此每帧抛 NotSupportedException: Method unstripping failed。
                //    详见 docs/KeepCachedVersions-IMGUI-STRIPPED-FIX-PLAN.md
                try
                {
                    ClassInjector.RegisterTypeInIl2Cpp<CacheCleanupUI>();
                    var go = new GameObject("KeepCachedVersions_CacheCleanupUI");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.hideFlags |= HideFlags.HideAndDontSave;
                    go.AddComponent<CacheCleanupUI>();
                }
                catch (Exception uiEx)
                {
                    LogInstance.LogError($"[KeepCachedVersions] UI setup failed: {uiEx}");
                }
            }
            catch (Exception e)
            {
                LogInstance.LogError($"[KeepCachedVersions] failed to apply patches: {e}");
            }
        }

        // 仅按名字 + 参数个数(3)定位, 避免 IL2CPP interop 中 Hash128 的 ref/值 形态差异导致匹配失败。
        // 目标: internal static bool ClearCachedVersions(string assetBundleName, Hash128 hash, bool keepInputVersion)
        private static MethodInfo FindClearCachedVersions()
        {
            var type = typeof(UnityEngine.Caching);
            return type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ClearCachedVersions" && m.GetParameters().Length == 3);
        }

        // 兜底目标: internal static bool ClearCachedVersionInternal(string assetBundleName, Hash128 hash)
        private static MethodInfo FindClearCachedVersionInternal()
        {
            var type = typeof(UnityEngine.Caching);
            return type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ClearCachedVersionInternal" && m.GetParameters().Length == 2);
        }

        // ★本次修复新增: Addressable.AddressableManager.ClearOldCache()
        //   不直接 typeof(AddressableManager), 而是按全名在 Assembly-CSharp 里找,
        //   以免命名空间/程序集变化时编译期就挂掉; 找不到只是不打补丁(降级为旧行为)。
        private static MethodInfo FindClearOldCache()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("Addressable.AddressableManager", false); }
                catch { continue; }
                if (t == null) continue;
                var m = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static)
                         .FirstOrDefault(x => x.Name == "ClearOldCache" && x.GetParameters().Length == 0);
                if (m != null) return m;
            }
            return null;
        }

        // no-op 前缀: 跳过原方法, 不删除任何已缓存版本。__result=false 表示操作未执行。
        private static bool PrefixClearCachedVersions(ref bool __result)
        {
            var n = Interlocked.Increment(ref _suppressed);
            if (n <= 20 || n % 100 == 0)
                LogInstance.LogInfo($"[KeepCachedVersions] suppressed ClearCachedVersions (total {n})");
            __result = false;
            return false;
        }

        // ★ClearOldCache 为 void 实例方法: 直接跳过原方法体。
        //   这是双服共存失效的根因路径, 日志不限流(调用次数本就很少)。
        private static bool PrefixClearOldCache()
        {
            var n = Interlocked.Increment(ref _suppressedOldCache);
            LogInstance.LogInfo($"[KeepCachedVersions] suppressed ClearOldCache (total {n}) " +
                                "-- this is the path that deleted the other server's bundles");
            return false;
        }

        private static bool PrefixClearCachedVersionInternal(ref bool __result)
        {
            var n = Interlocked.Increment(ref _suppressedInternal);
            if (n <= 20 || n % 100 == 0)
                LogInstance.LogInfo($"[KeepCachedVersions] suppressed ClearCachedVersionInternal (total {n})");
            __result = false;
            return false;
        }
    }
}
