# English

**Title:** 🗂️ KeepCachedVersions — Stop Re-Downloading Bundles When Switching Limbus Servers

**Function**
KeepCachedVersions is a lightweight BepInEx plugin that stops Limbus Company from re-downloading gigabytes of asset bundles every time you switch between the private (Lethe) server and the official server. It makes both servers' cached bundle versions coexist in the same cache root, so switching servers requires no full re-download — saving time and bandwidth, with each server's cached content hashes kept intact.

**How It Works (technical)**

The plugin is a runtime HarmonyX patch over a single Unity caching API. Here is the full mechanism, root cause, and fix:

1. **Shared cache root.** Both the official server and the private (Lethe) server share the same Unity `Caching` directory — `%USERPROFILE%\AppData\LocalLow\Unity\ProjectMoon_LimbusCompany\`. There is no per-server cache separation (the Lethe `CachePath` redirect is dead code).

2. **On-disk layout.** Each cached bundle lives at `<cacheRoot>/<outer>/<inner>/`, where:
   - `inner` = the bundle's **content hash** (a 32-hex string, globally unique — the entry's fingerprint);
   - `outer` = the cache key derived from the asset-bundle name (stable across versions);
   - each leaf holds `__data` (raw bundle bytes) and `__info` (`-1\n<unix_ts>\n1\n__data\n`).
   Because the two servers use the same `outer` keys, **the other server's version sits in a different `inner` under the same `outer`**.

3. **Root cause of the re-download loop.** Cache-hit checks (`Caching.IsVersionCached` / `Cache::IsCached`) only test path existence + parse `__info` — they never delete. The real deletion happens **after a download completes**: in the IL2CPP managed callback `AssetBundleResource.WebRequestOperationCompleted`, when `AssetBundleRequestOptions.m_ClearOtherCachedVersionsWhenLoaded` is true, the game calls
   `UnityEngine.Caching.ClearCachedVersions(name, justDownloadedHash, keepInputVersion: true)`.
   Semantics: keep the version it just downloaded, **delete every other version under that bundle name** — i.e. the *other server's* `inner` folder. This call goes through the `Caching` API (eventually via vtable to native `Cache::RemoveCacheEntry` / `RemoveDirectoryW`), not a plain `Directory.Delete`.
   Result: switch server → current server downloads the missing bundle → wipes the other server's `inner` → switch back → missing again → re-download. Roughly ~34 divergent bundles (unit/enemy/shader/ui/story/skin) per switch.

4. **The fix.** The plugin targets `UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)` (internal static) and turns it into a no-op:
   - It locates the method by **reflection on name + 3-parameter count** (avoiding IL2CPP interop's `Hash128` ref/value mismatch that would break signature matching).
   - It applies a Harmony **Prefix** that returns `false` and sets `__result = false`, so the original method is skipped — every "clear other versions" call becomes a no-op. `__result = false` signals "operation not performed" to callers.

5. **Why it's safe / side-effect notes.**
   - **Global, not Lethe-only:** both servers, after downloading, stop clearing the other's `inner`. On the official server everything is already a cache hit, so the call is never even triggered → no gameplay side effects.
   - **Manual "Clear All Cache" still works:** the in-game button goes through `Caching.ClearCache()` (full wipe, a *different* method) plus `System.IO.Directory.Delete` for Project Moon's own data dirs — it does **not** call `ClearCachedVersions`, so it remains fully functional.
   - **Trade-off:** explicit cleanups that also route through `ClearCachedVersions` (e.g. `ClearDependencyCacheForKey`, `CleanBundleCacheOperation.RemoveCacheEntries`) become no-ops too. For a dual-server coexistence setup this is an acceptable compromise; players can still reclaim space via the full "Clear All Cache" button.
   - **Disk growth:** because neither version is deleted, the cache folder grows to hold both servers' assets. Clear it manually when you want the space back.

*Verified against the 2026-08-20 build (Unity 6000.3.12f1, metadata v39) via IL2CPP `dump.cs` + IDA call-chain analysis.*

---

# 中文翻译

**标题:** 🗂️ KeepCachedVersions —— 切换 Limbus 服务器不再重复下载资源包

**功能**
KeepCachedVersions 是一个轻量级 BepInEx 插件，解决每次在私服（Lethe）与官服之间切换时，Limbus Company 都要重新下载数 GB 资源包（bundle）的问题。它让两台服务器的缓存资源版本在同一缓存根目录下共存，切换服务器无需重新完整下载，节省时间与带宽，且各自的内容哈希版本都完整保留。

**原理（技术细节）**

本插件是对单个 Unity 缓存 API 的运行时 HarmonyX 补丁。以下是完整机制、根因与修复说明：

1. **共享缓存根目录。** 官服与私服（Lethe）共用同一个 Unity `Caching` 目录 —— `%USERPROFILE%\AppData\LocalLow\Unity\ProjectMoon_LimbusCompany\`。并不存在按服务器隔离的缓存（Lethe 的 `CachePath` 重定向是死代码，未启用）。

2. **磁盘上的布局。** 每个缓存的资源包位于 `<cacheRoot>/<outer>/<inner>/`，其中：
   - `inner` = 资源包的**内容哈希**（32 位十六进制字符串，全局唯一，作为条目的指纹）；
   - `outer` = 由资源包名算出的缓存键（跨版本稳定）；
   - 每个叶子目录包含 `__data`（bundle 原始字节）和 `__info`（`-1\n<unix时间戳>\n1\n__data\n`）。
   由于两服使用相同的 `outer` 键，**另一台服务器的版本就存放在同一个 `outer` 下、不同的 `inner` 里**。

3. **重复下载循环的根因。** 缓存命中判定（`Caching.IsVersionCached` / `Cache::IsCached`）只检查路径是否存在、解析 `__info`，**从不删除**。真正的删除发生在**下载完成之后**：在 IL2CPP 受管回调 `AssetBundleResource.WebRequestOperationCompleted` 中，当 `AssetBundleRequestOptions.m_ClearOtherCachedVersionsWhenLoaded` 为真时，游戏会调用
   `UnityEngine.Caching.ClearCachedVersions(name, 刚下载的hash, keepInputVersion: true)`。
   语义是：保留刚下载的版本，**删除该 bundle 名下所有其他版本** —— 也就是*另一台服务器*的 `inner` 目录。这个调用走 `Caching` API（最终经虚表落到原生 `Cache::RemoveCacheEntry` / `RemoveDirectoryW`），并非普通的 `Directory.Delete`。
   结果：切换服务器 → 当前服下载缺失包 → 清掉对方服的 `inner` → 切回对方又缺 → 再次下载。实测每次切换约 34 个差异包（unit/enemy/shader/ui/story/skin 等混合类型）。

4. **修复方式。** 插件以 `UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)`（internal static）为目标，将其打成空操作（no-op）：
   - 通过**反射按"方法名 + 参数个数(3)"**定位 `MethodInfo`，避开 IL2CPP interop 中 `Hash128` 的 ref/值 形态差异导致的签名匹配失败。
   - 应用一个 Harmony **Prefix**，返回 `false` 并将 `__result = false`，从而跳过原方法 —— 任何"清除其他版本"的调用都变成 no-op。`__result = false` 向调用方表示"操作未执行"。

5. **为何安全 / 副作用说明。**
   - **全局生效，不限于 Lethe：** 两服各自下载后都不再清除对方的 `inner`。官服正常游玩时一切本就是命中，该调用根本不会被触发 → 无游戏逻辑副作用。
   - **"清除全部缓存"按钮仍有效：** 游戏内按钮走 `Caching.ClearCache()`（全量清除，*另一个*方法）+ `System.IO.Directory.Delete` 删除 Project Moon 自己的数据目录 —— 它**不**调用 `ClearCachedVersions`，因此功能完全保留。
   - **取舍：** 同样经由 `ClearCachedVersions` 的显式清理（如 `ClearDependencyCacheForKey`、`CleanBundleCacheOperation.RemoveCacheEntries`）也会变成 no-op。对于双服共存场景，这是可接受的折中；玩家仍可用"清除全部缓存"按钮回收空间。
   - **磁盘增长：** 由于两个版本都不会被删，缓存文件夹会增长以容纳两台服务器的资源。若想释放空间，可手动清理缓存。

*以上结论已对照 2026-08-20 构建（Unity 6000.3.12f1、metadata v39），经 IL2CPP `dump.cs` 与 IDA 调用链分析验证。*
