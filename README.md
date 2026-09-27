# 🗂️ KeepCachedVersions

**Stop re-downloading gigabytes of asset bundles when switching Limbus Company servers.**

A lightweight [BepInEx](https://docs.bepinex.dev/) (IL2CPP) plugin for Limbus Company that makes the
official server's and the private Lethe server's cached asset bundles coexist in the same cache root
— so switching servers no longer triggers a full re-download.

| | |
|---|---|
| **Version** | 0.3.0 |
| **Plugin GUID** | `com.limbusmods.keepcachedversions` |
| **Requires** | BepInEx 6.0 pre (IL2CPP) + UnityDoorstop installed in the game |
| **Dependencies** | none (no NuGet packages; references only the game's own interop assemblies) |
| **Scope** | deletion blocking only — **no UI, no config, no active cache cleanup** |

---

## English

### What it does

Limbus Company stores downloaded asset bundles in one shared Unity `Caching` directory. After each
download the game deletes "the other versions" of that bundle — which, in a dual-server setup, means
the *other server's* files. Switch server → it re-downloads what you had before → and wipes the
server you just left. The loop repeats on every switch (~34 divergent bundles per switch in our
measurements: unit / enemy / shader / ui / story / skin).

This plugin patches those deletion calls into **no-ops**. Both servers' content-hash versions are
then kept side by side, and switching servers becomes instant.

The plugin is purely passive: it injects no UI, adds no buttons, and never deletes anything itself.
It only prevents the game from deleting.

### Install

Drop `KeepCachedVersions.dll` into `<Game>\BepInEx\plugins\` and start the game. There is no
`BepInEx\config\` entry — the plugin has no options.

Verify in `BepInEx\LogOutput.log`:

```
[Info   :KeepCachedVersions] [KeepCachedVersions] no-delete patch active: ClearCachedVersions is a no-op
[Info   :KeepCachedVersions] [KeepCachedVersions] old-cache patch active: AddressableManager.ClearOldCache is a no-op
[Info   :KeepCachedVersions] [KeepCachedVersions] fallback patch active: ClearCachedVersionInternal is a no-op
```

> If a `BepInEx\config\com.limbusmods.keepcachedversions.cfg` is left over from v0.2.x, it is simply
> ignored — v0.3.0 binds no config entries, so the file is never re-created. Delete it if you want a
> tidy install.

### Build from source

```powershell
.\build.ps1              # compile only
.\build.ps1 -Deploy      # compile + copy the DLL into the game's BepInEx\plugins
.\build.ps1 -RefDir "C:\...\Limbus Company\BepInEx"   # build against another interop set
```

The project has no NuGet dependencies and builds with `--no-restore` against the game's own
interop assemblies (`core\` for BepInEx/Harmony, `interop\` for `UnityEngine.CoreModule` and
`Il2Cppmscorlib`). `-Deploy` refuses to run while the game is up, because the DLL would be locked.

### How it works (technical)

Three runtime HarmonyX no-op prefixes over the deletion entry points of the Unity caching /
Addressables download pipeline. Full mechanism, root cause and fix:

1. **Shared cache root.** The official server and the private (Lethe) server share the same Unity
   `Caching` directory — `%USERPROFILE%\AppData\LocalLow\Unity\ProjectMoon_LimbusCompany\`. There is
   no per-server cache separation (the Lethe `CachePath` redirect is dead code).

2. **On-disk layout.** Each cached bundle lives at `<cacheRoot>/<outer>/<inner>/`, where:
   - `inner` = the bundle's **content hash** (a 32-hex string, globally unique — the entry's fingerprint);
   - `outer` = the cache key derived from the asset-bundle name (stable across versions);
   - each leaf holds `__data` (raw bundle bytes) and `__info` (`-1\n<unix_ts>\n1\n__data\n`).

   Because the two servers use the same `outer` keys, **the other server's version sits in a
   different `inner` under the same `outer`**.

3. **Root cause of the re-download loop.** Cache-hit checks (`Caching.IsVersionCached` /
   `Cache::IsCached`) only test path existence + parse `__info` — they never delete. The real
   deletion happens **after a download completes**: in the IL2CPP managed callback
   `AssetBundleResource.WebRequestOperationCompleted`, when
   `AssetBundleRequestOptions.m_ClearOtherCachedVersionsWhenLoaded` is true, the game calls
   `UnityEngine.Caching.ClearCachedVersions(name, justDownloadedHash, keepInputVersion: true)`.

   Semantics: keep the version it just downloaded, **delete every other version under that bundle
   name** — i.e. the *other server's* `inner` folder. This call goes through the `Caching` API
   (eventually via vtable to native `Cache::RemoveCacheEntry` / `RemoveDirectoryW`), not a plain
   `Directory.Delete`.

   Result: switch server → current server downloads the missing bundle → wipes the other server's
   `inner` → switch back → missing again → re-download.

4. **The fix.** Every "delete cached versions" entry point in the download pipeline becomes a no-op
   via a Harmony **Prefix** that returns `false`:

   | Target | Why |
   |---|---|
   | `UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)` | The post-download "clear other versions" call. Located by **reflection on name + 3-parameter count**, avoiding the IL2CPP interop `Hash128` ref/value mismatch that would break signature matching. `__result = false` signals "operation not performed". |
   | `Addressable.AddressableManager.ClearOldCache()` ★ | **The real culprit.** It walks `Caching.GetCachedVersions()` and deletes every version **not in the current catalog**; in a dual-server setup the other server's bundles are never in the active catalog, so they always get wiped. Called from `DownloadProcess` / `PrevDownloadProcess` via the 2-arg `ClearCachedVersionInternal`, which fully bypasses the patch above. Found at runtime by assembly scan, so the plugin compiles without any game-type reference. |
   | `UnityEngine.Caching.ClearCachedVersionInternal(string, Hash128)` | Fallback, covering the `WebRequestOperationCompleted` retry branch and any other call sites. |

   The v0.1.0 release shipped only the first patch and was still losing bundles — the other two are
   what made dual-server coexistence actually work.

5. **Why it's safe / side-effect notes.**
   - **Global, not Lethe-only:** both servers, after downloading, stop clearing the other's `inner`.
     On the official server everything is already a cache hit, so the call is never even triggered →
     no gameplay side effects.
   - **Manual "Clear All Cache" still works:** the in-game button goes through `Caching.ClearCache()`
     (full wipe, a *different* method) plus `System.IO.Directory.Delete` for Project Moon's own data
     dirs — it does **not** call `ClearCachedVersions`, so it remains fully functional.
   - **Trade-off:** explicit cleanups that also route through `ClearCachedVersions`
     (e.g. `ClearDependencyCacheForKey`, `CleanBundleCacheOperation.RemoveCacheEntries`) become
     no-ops too. For a dual-server coexistence setup this is an acceptable compromise; you can still
     reclaim space via the full "Clear All Cache" button.
   - **Disk growth:** because neither version is deleted, the cache folder grows to hold both
     servers' assets. Clear it manually when you want the space back.

*Verified against the 2026-08-20 build (Unity 6000.3.12f1, metadata v39) via IL2CPP `dump.cs` + IDA
call-chain analysis.*

---

## 中文

### 功能

Limbus Company 的下载资源存放在**同一个** Unity `Caching` 目录里。每次下载完成后，游戏会删除该
bundle 的"其他版本"——在双服场景下就是**另一台服务器**的文件。于是：切换服务器 → 重新下载刚才还
有的东西 → 顺手清掉上一台服务器的缓存 → 切回去又缺。每次切换实测约 34 个差异包
（unit / enemy / shader / ui / story / skin）。

本插件把这些"删除缓存版本"的调用全部打成 no-op，两服的内容哈希版本在同一缓存根下并存，切换服务器
不再需要重新下载。

插件完全被动：不注入任何 UI、不加按钮、自己也从不删除任何东西 —— **只阻止游戏删除**。

### 安装

把 `KeepCachedVersions.dll` 放进 `<游戏目录>\BepInEx\plugins\`，启动游戏即可。本插件
**没有任何配置项**，`BepInEx\config\` 下不会出现对应 cfg。

在 `BepInEx\LogOutput.log` 中应看到三行：

```
[Info   :KeepCachedVersions] [KeepCachedVersions] no-delete patch active: ClearCachedVersions is a no-op
[Info   :KeepCachedVersions] [KeepCachedVersions] old-cache patch active: AddressableManager.ClearOldCache is a no-op
[Info   :KeepCachedVersions] [KeepCachedVersions] fallback patch active: ClearCachedVersionInternal is a no-op
```

> 若安装目录残留 v0.2.x 的 `BepInEx\config\com.limbusmods.keepcachedversions.cfg`，会被直接忽略 ——
> v0.3.0 不绑定任何配置项，也不会再生成该文件。想保持干净可手动删除。

### 从源码构建

```powershell
.\build.ps1              # 仅编译
.\build.ps1 -Deploy      # 编译 + 部署到游戏 BepInEx\plugins
.\build.ps1 -RefDir "C:\...\Limbus Company\BepInEx"   # 换用另一套 interop 引用
```

工程无 NuGet 依赖，用 `--no-restore` 直接针对游戏自带 interop 程序集编译
（`core\` 提供 BepInEx/Harmony，`interop\` 提供 `UnityEngine.CoreModule` 与 `Il2Cppmscorlib`）。
游戏运行时 `-Deploy` 会主动拒绝执行，因为 DLL 被锁定。

### 原理（技术细节）

三条运行时 HarmonyX no-op 前缀，覆盖 Unity caching / Addressables 下载管线中的全部删除入口：

1. **共享缓存根目录。** 官服与私服（Lethe）共用同一个 Unity `Caching` 目录 ——
   `%USERPROFILE%\AppData\LocalLow\Unity\ProjectMoon_LimbusCompany\`。并不存在按服务器隔离的缓存
   （Lethe 的 `CachePath` 重定向是死代码，未启用）。

2. **磁盘上的布局。** 每个缓存的资源包位于 `<cacheRoot>/<outer>/<inner>/`，其中：
   - `inner` = 资源包的**内容哈希**（32 位十六进制字符串，全局唯一，作为条目的指纹）；
   - `outer` = 由资源包名算出的缓存键（跨版本稳定）；
   - 每个叶子目录包含 `__data`（bundle 原始字节）和 `__info`（`-1\n<unix时间戳>\n1\n__data\n`）。

   由于两服使用相同的 `outer` 键，**另一台服务器的版本就存放在同一个 `outer` 下、不同的 `inner` 里**。

3. **重复下载循环的根因。** 缓存命中判定（`Caching.IsVersionCached` / `Cache::IsCached`）只检查路径
   是否存在、解析 `__info`，**从不删除**。真正的删除发生在**下载完成之后**：在 IL2CPP 受管回调
   `AssetBundleResource.WebRequestOperationCompleted` 中，当
   `AssetBundleRequestOptions.m_ClearOtherCachedVersionsWhenLoaded` 为真时，游戏会调用
   `UnityEngine.Caching.ClearCachedVersions(name, 刚下载的hash, keepInputVersion: true)`。

   语义是：保留刚下载的版本，**删除该 bundle 名下所有其他版本** —— 也就是*另一台服务器*的 `inner`
   目录。这个调用走 `Caching` API（最终经虚表落到原生 `Cache::RemoveCacheEntry` /
   `RemoveDirectoryW`），并非普通的 `Directory.Delete`。

   结果：切换服务器 → 当前服下载缺失包 → 清掉对方服的 `inner` → 切回对方又缺 → 再次下载。

4. **修复方式。** 把下载管线中所有"删除缓存版本"的入口经 Harmony **Prefix**（返回 `false`）打成空
   操作（no-op）：

   | 目标 | 说明 |
   |---|---|
   | `UnityEngine.Caching.ClearCachedVersions(string, Hash128, bool)` | 下载完成后的"清除其他版本"调用。通过**反射按"方法名 + 参数个数(3)"**定位 `MethodInfo`，避开 IL2CPP interop 中 `Hash128` 的 ref/值 形态差异导致的签名匹配失败。`__result = false` 向调用方表示"操作未执行"。 |
   | `Addressable.AddressableManager.ClearOldCache()` ★ | **真正的元凶。** 它遍历 `Caching.GetCachedVersions()`，删除所有**不在当前 catalog 里**的版本；双服场景下另一服的 bundle 天然不在当前 catalog，必然被清。它在 `DownloadProcess` / `PrevDownloadProcess` 内被调用，走 2 参 `ClearCachedVersionInternal`，完全绕过上一条补丁。运行时按程序集扫描定位，插件编译期不依赖任何游戏类型。 |
   | `UnityEngine.Caching.ClearCachedVersionInternal(string, Hash128)` | 兜底，覆盖 `WebRequestOperationCompleted` 重试分支及其它调用点。 |

   v0.1.0 只打了第一条，仍然会掉包 —— 后两条才是双服共存真正生效的关键。

5. **为何安全 / 副作用说明。**
   - **全局生效，不限于 Lethe：** 两服各自下载后都不再清除对方的 `inner`。官服正常游玩时一切本就是
     命中，该调用根本不会被触发 → 无游戏逻辑副作用。
   - **"清除全部缓存"按钮仍有效：** 游戏内按钮走 `Caching.ClearCache()`（全量清除，*另一个*方法）+
     `System.IO.Directory.Delete` 删除 Project Moon 自己的数据目录 —— 它**不**调用
     `ClearCachedVersions`，因此功能完全保留。
   - **取舍：** 同样经由 `ClearCachedVersions` 的显式清理（如 `ClearDependencyCacheForKey`、
     `CleanBundleCacheOperation.RemoveCacheEntries`）也会变成 no-op。对于双服共存场景这是可接受的
     折中；仍可用"清除全部缓存"按钮回收空间。
   - **磁盘增长：** 由于两个版本都不会被删，缓存文件夹会增长以容纳两台服务器的资源。若想释放空间，
     可手动清理缓存。

*以上结论已对照 2026-08-20 构建（Unity 6000.3.12f1、metadata v39），经 IL2CPP `dump.cs` 与 IDA
调用链分析验证。*

---

## Version history

| | |
|---|---|
| **v0.1.0** | Single no-op patch on `Caching.ClearCachedVersions`. |
| **v0.2.0** | Added the `AddressableManager.ClearOldCache` and `ClearCachedVersionInternal` patches (the real root-cause path). Also added a start-page "Clear cache" button with a uGUI modal and a dual-catalog "clear unused cache" feature. |
| **v0.3.0** | Removed all UI code — login-scene button patch, modal, cache cleaner, localization strings, and the four cleanup config entries. The plugin is now purely the three deletion-blocking no-op patches described above, and compiles without any game-type reference. Output shrank from 45 KB to 10 KB. The v0.2.0 UI is preserved in git history (`d6a871d`) if anyone wants to revive it. |

### Notes on the v0.2.0 UI removal

The modal window is gone by design, but it also hit a platform-specific wall worth recording: this
title's IL2CPP build has the **IMGUI native implementation stripped out**
(`GUIStyle.padding/font`, `GUILayout.FlexibleSpace`, `GUI.DrawTexture`, …). The interop signatures
still exist so the code compiles, but every call throws
`System.NotSupportedException: Method unstripping failed` at runtime. The v0.2.0 `OnGUI` version hit
exactly that: because the modal's `_open` flag stayed `true`, the exception path re-ran **every
frame** — one baseline log had 1257 `unstripping` errors, 2520 of its 3818 lines coming from this
plugin. Rewriting the window in uGUI + TextMeshPro fixed that (the game's own UI stack is fully
intact), and dropping the UI entirely removed the problem. If you add UI back to this project,
**use uGUI, never IMGUI** — see `docs/KeepCachedVersions-IMGUI-STRIPPED-FIX-PLAN.md` in the parent
analysis repo for the full diagnosis and the `script.json` verification method.
