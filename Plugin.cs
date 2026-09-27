using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace KeepCachedVersions
{
    /// <summary>
    /// 让官服与私服(Lethe)的 bundle 缓存在同一缓存根下共存：
    /// 把下载流程里所有"删旧版本"的入口全部打成 no-op，两服各自的内容哈希版本都保留，
    /// 切换服务器无需重新下载。
    ///
    /// 本插件只做"阻止删除"这一件事，不含任何 UI / 主动清理逻辑
    /// （v0.2.0 的模态窗口与"清除无用缓存"已移除，需要时见 git 历史 d6a871d）。
    ///
    /// 三层拦截：
    ///   1) UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)  -> no-op
    ///      下载完成后清掉同名 bundle 其他版本（= 另一服的 inner 目录）的调用点。
    ///   2) Addressable.AddressableManager.ClearOldCache()                   -> no-op  ★根因路径
    ///      遍历 Caching.GetCachedVersions() 删掉所有"不在当前 catalog 里"的版本 ——
    ///      双服场景下另一服的 bundle 天然不在当前 catalog，必然被删。它在
    ///      DownloadProcess / PrevDownloadProcess 内被调用，且走 2 参
    ///      ClearCachedVersionInternal，完全绕过 1)，这才是真正的元凶。
    ///   3) UnityEngine.Caching.ClearCachedVersionInternal(string, Hash128)  -> no-op  (兜底)
    ///      覆盖 WebRequestOperationCompleted 重试分支等其它调用点。
    /// </summary>
    [BepInPlugin("com.limbusmods.keepcachedversions", "KeepCachedVersions", "0.3.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource LogInstance;
        private static int _suppressed;
        private static int _suppressedOldCache;
        private static int _suppressedInternal;

        public override void Load()
        {
            LogInstance = base.Log;

            try
            {
                var harmony = new Harmony("com.limbusmods.keepcachedversions");

                // 1) 原有的 no-delete 补丁: 3 参 ClearCachedVersions
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

                // 2) ★根因路径: AddressableManager.ClearOldCache()
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

                // 3) 兜底: 2 参 ClearCachedVersionInternal 也打成 no-op
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

        // ★Addressable.AddressableManager.ClearOldCache()
        //   不直接 typeof(AddressableManager), 而是按全名在各程序集里运行时找 ——
        //   这样编译期不需要 Assembly-CSharp 引用（保证插件不依赖任何游戏类型），
        //   命名空间/程序集变化也不会编译失败; 找不到只是不打补丁(降级为旧行为)。
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

        // ClearOldCache 为 void 实例方法: 直接跳过原方法体。
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
