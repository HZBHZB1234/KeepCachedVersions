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
    // 让 lethe 与官服的 bundle 缓存在同一缓存根下共存:
    // 把 UnityEngine.Caching.ClearCachedVersions 打成 no-op, 下载新版本后不再删除"其他版本"
    // (= 另一服的 inner 目录)。这样两服各自的内容哈希版本都保留, 切换服务器无需重新下载。
    [BepInPlugin("com.limbusmods.keepcachedversions", "KeepCachedVersions", "0.1.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource LogInstance;
        private static int _suppressed;

        public override void Load()
        {
            LogInstance = base.Log;
            try
            {
                var harmony = new Harmony("com.limbusmods.keepcachedversions");
                var target = FindClearCachedVersions();
                if (target == null)
                {
                    LogInstance.LogError(
                        "[KeepCachedVersions] UnityEngine.Caching.ClearCachedVersions not found; patch NOT applied");
                    return;
                }

                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(PrefixClearCachedVersions)));
                LogInstance.LogInfo(
                    "[KeepCachedVersions] active: UnityEngine.Caching.ClearCachedVersions is now a no-op. " +
                    "Both servers' cached bundle versions are kept after download, so switching servers will not re-download.");
            }
            catch (Exception e)
            {
                LogInstance.LogError($"[KeepCachedVersions] failed to apply patch: {e}");
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

        // no-op 前缀: 跳过原方法, 不删除任何已缓存版本。__result=false 表示操作未执行。
        private static bool PrefixClearCachedVersions(ref bool __result)
        {
            var n = Interlocked.Increment(ref _suppressed);
            LogInstance.LogInfo($"[KeepCachedVersions] suppressed ClearCachedVersions (total {n})");
            __result = false;
            return false;
        }
    }
}
