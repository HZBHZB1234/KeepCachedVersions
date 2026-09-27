using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace KeepCachedVersions
{
    public enum CleanupPhase
    {
        Idle = 0,
        Fetching = 1,
        Deleting = 2,
        Done = 3,
        Failed = 4,
    }

    /// <summary>后台任务与主线程 UI(<see cref="CacheCleanupUI"/>)共享的进度/结果状态(volatile 字段, 主线程只读)。</summary>
    public sealed class CleanupState
    {
        public volatile int Phase;              // CleanupPhase
        public volatile int TotalEntries;       // 待扫描条目总数
        public volatile int Scanned;            // 已扫描
        public volatile int DeletedEntries;     // 已删除(或 dry-run 将删除)
        public long DeletedBytes;               // 释放字节数(64 位运行时读写原子, 无需 volatile)
        public volatile string Message = "";    // 技术性详情(英文)
        public volatile bool NeedOtherIndex;    // 因只拿到一份索引而中止
        public volatile bool DryRun;            // 只报告不删除
    }

    /// <summary>
    /// "清除无用缓存": 请求官方与私服两份 catalog_S1.bin, 取所有 inner content-hash 的并集,
    /// 删除本地 Unity Caching 中"两服索引都不包含"的条目。
    /// 官方索引经宿主 .NET HttpClient 直连官方 CDN —— 完全绕过 lethe.dll 对
    /// UnityWebRequest 的 URL 重定向(download.limbuscompanycdn.org -> assets.lethelc.site)。
    /// 解析逻辑与 CacheWarmer/CatalogParser.cs、tools/prepare_update.py 一致。
    /// </summary>
    public static class CacheCleaner
    {
        private const string UA = "UnityPlayer/6000.3.12f1 (UnityWebRequest/1.0, libcurl/8.5.0-DEV)";
        private static readonly string[] Hosts =
        {
            "download.limbuscompanycdn.org", // 官方 CDN
            "assets.lethelc.site",           // 私服(Lethe) CDN
        };
        // settings.json 中出现的 s-token: s<日期8位>_<随机>
        private static readonly Regex TokenRe = new Regex(
            @"(?:download\.limbuscompanycdn\.org|assets\.lethelc\.site)/(s[0-9]{8}_[A-Za-z0-9_\-]+)/",
            RegexOptions.IgnoreCase);
        // catalog 二进制里的 ".bundle" 名字串(ASCII)
        private static readonly Regex NamePat = new Regex(@"[A-Za-z0-9_.\-]+\.bundle");
        // bundle 名尾段 32-hex(content hash)
        private static readonly Regex TailHex = new Regex(@"[0-9a-f]{32}$", RegexOptions.IgnoreCase);

        public static void Run(CleanupState st)
        {
            try
            {
                string cacheRoot = ResolveCacheRoot();
                if (string.IsNullOrEmpty(cacheRoot) || !Directory.Exists(cacheRoot))
                {
                    Fail(st, "Unity cache root not found: " + cacheRoot);
                    return;
                }

                var settings = CollectSettingsPaths();
                var tokens = CollectTokens(settings);
                if (tokens.Count == 0)
                {
                    Fail(st, "no s-token found in settings.json files:\n" + string.Join("\n", settings));
                    return;
                }
                Plugin.LogInfo($"[KeepCachedVersions] useless-clear: cacheRoot={cacheRoot} tokens={string.Join(",", tokens)}");

                // 1) 拉取所有候选索引(每个 token × 两个 CDN), 按内容去重
                st.Phase = (int)CleanupPhase.Fetching;
                var catalogs = new List<byte[]>();
                foreach (var tok in tokens)
                {
                    foreach (var host in Hosts)
                    {
                        try
                        {
                            var b = HttpGet($"https://{host}/{tok}/catalog_S1.bin");
                            if (b != null && b.Length > 0) catalogs.Add(b);
                        }
                        catch (Exception e)
                        {
                            Plugin.LogWarning($"[KeepCachedVersions] catalog fetch failed {host}/{tok}: {e.Message}");
                        }
                    }
                }
                var distinct = DistinctBySha256(catalogs);
                if (distinct.Count == 0)
                {
                    Fail(st, "failed to download any catalog_S1.bin (network / X-Requested-With?)");
                    return;
                }

                // 2) 安全闸: 必须确认两服索引都在(否则会把另一服独有 bundle 当无用删掉)
                if (distinct.Count < 2 && !Plugin.AllowSingleIndex.Value)
                {
                    st.NeedOtherIndex = true;
                    st.Phase = (int)CleanupPhase.Failed;
                    st.Message = $"only {distinct.Count} distinct catalog(s) fetched";
                    return;
                }
                Plugin.LogInfo($"[KeepCachedVersions] distinct catalogs: {distinct.Count}");

                // 3) 并集: inner(content hash) + outer(缓存键, 兜底特殊 bundle)
                var keepInners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var keepOuters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in distinct)
                    ParseCatalog(cat, keepInners, keepOuters);
                Plugin.LogInfo($"[KeepCachedVersions] keep: inners={keepInners.Count} outers={keepOuters.Count}");

                // 4) 扫描本地缓存并删除"两服都不引用"的条目
                st.Phase = (int)CleanupPhase.Deleting;
                DeleteUseless(st, cacheRoot, keepInners, keepOuters);
                st.Phase = (int)CleanupPhase.Done;
                Plugin.LogInfo($"[KeepCachedVersions] useless-clear finished: deleted={st.DeletedEntries} bytes={st.DeletedBytes} dryRun={st.DryRun}");
            }
            catch (Exception e)
            {
                Fail(st, e.ToString());
            }
        }

        private static void Fail(CleanupState st, string msg)
        {
            st.Message = msg;
            st.Phase = (int)CleanupPhase.Failed;
            Plugin.LogError($"[KeepCachedVersions] useless-clear failed: {msg}");
        }

        // ---- settings.json 发现 ----

        private static List<string> CollectSettingsPaths()
        {
            var list = new List<string>();
            try
            {
                string active = Path.Combine(Paths.GameRootPath,
                    "LimbusCompany_Data", "StreamingAssets", "aa", "settings.json");
                if (File.Exists(active)) list.Add(active);
            }
            catch { }
            // Steam 标准安装路径(运行 lethe 分发包时用于发现官服 token)
            string steam = @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\aa\settings.json";
            if (File.Exists(steam) && !list.Contains(steam, StringComparer.OrdinalIgnoreCase)) list.Add(steam);
            try
            {
                string cfg = Plugin.OtherServerSettingsPath.Value;
                if (!string.IsNullOrWhiteSpace(cfg))
                {
                    string norm = NormalizeSettingsPath(cfg.Trim());
                    if (norm != null && !list.Contains(norm, StringComparer.OrdinalIgnoreCase)) list.Add(norm);
                }
            }
            catch { }
            return list;
        }

        // cfg 可填: settings.json 文件 | .../aa 目录 | 游戏根目录
        private static string NormalizeSettingsPath(string p)
        {
            if (File.Exists(p)) return p;
            foreach (var cand in new[]
            {
                Path.Combine(p, "settings.json"),
                Path.Combine(p, "LimbusCompany_Data", "StreamingAssets", "aa", "settings.json"),
            })
                if (File.Exists(cand)) return cand;
            return null;
        }

        private static List<string> CollectTokens(List<string> settingsPaths)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in settingsPaths)
            {
                try
                {
                    string text = File.ReadAllText(p, Encoding.UTF8);
                    foreach (Match m in TokenRe.Matches(text))
                        tokens.Add(m.Groups[1].Value);
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"[KeepCachedVersions] read settings failed {p}: {e.Message}");
                }
            }
            return tokens.ToList();
        }

        // ---- HTTP(宿主 .NET, 不经 UnityWebRequest, 不受 lethe 重定向影响) ----

        private static byte[] HttpGet(string url)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Add("User-Agent", UA);
            client.DefaultRequestHeaders.Add("X-Requested-With", Plugin.CdnHeaderValue.Value);
            return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }

        private static List<byte[]> DistinctBySha256(List<byte[]> catalogs)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<byte[]>();
            foreach (var b in catalogs)
            {
                if (b == null || b.Length == 0) continue;
                string h = Convert.ToHexString(SHA256.HashData(b));
                if (seen.Add(h)) result.Add(b);
            }
            return result;
        }

        // ---- catalog 解析(与 CacheWarmer/CatalogParser.cs 对齐) ----

        private static void ParseCatalog(byte[] bin, HashSet<string> keepInners, HashSet<string> keepOuters)
        {
            string text = Encoding.ASCII.GetString(bin);
            foreach (Match m in NamePat.Matches(text))
            {
                string name = m.Value;
                if (name.Length >= 200) continue;
                // inner = 名字尾段 32-hex(content hash)
                if (name.Length >= 39)
                {
                    string tail = name.Substring(name.Length - 39, 32);
                    if (TailHex.IsMatch(tail)) keepInners.Add(tail.ToLowerInvariant());
                }
                // outer = 名字后首个 32-hex(缓存键); 特殊 bundle 无 options 记录 -> 名字前缀
                int after = m.Index + name.Length;
                int len = Math.Min(200, text.Length - after);
                if (len <= 0) continue;
                string seg = text.Substring(after, len);
                Match hm = Regex.Match(seg, @"(?<![0-9a-f])([0-9a-f]{32})(?![0-9a-f])", RegexOptions.IgnoreCase);
                if (hm.Success)
                {
                    keepOuters.Add(hm.Groups[1].Value.ToLowerInvariant());
                }
                else if (name.Contains("_monoscripts_") || name.StartsWith("vfx__unitybuiltinassets_"))
                {
                    string outer = name.Substring(0, Math.Max(0, name.Length - 7)).TrimEnd('_');
                    keepOuters.Add(outer);
                }
            }
        }

        // ---- 删除 ----

        private static void DeleteUseless(CleanupState st, string cacheRoot,
            HashSet<string> keepInners, HashSet<string> keepOuters)
        {
            var outers = Directory.GetDirectories(cacheRoot);
            int total = 0;
            foreach (var od in outers)
                total += Directory.GetDirectories(od).Length;
            st.TotalEntries = total;

            int scanned = 0, deleted = 0;
            long freed = 0;
            foreach (var od in outers)
            {
                string outerName = Path.GetFileName(od);
                foreach (var id in Directory.GetDirectories(od))
                {
                    string innerName = Path.GetFileName(id);
                    scanned++;
                    // 仅处理标准 32-hex 条目; 两服任一索引引用(inner 或 outer)则保留
                    if (innerName.Length == 32
                        && (keepInners.Contains(innerName) || keepOuters.Contains(outerName)))
                        continue;
                    if (innerName.Length != 32) continue; // 非标准目录, 保守跳过
                    string data = Path.Combine(id, "__data");
                    if (!File.Exists(data)) continue;     // 不完整条目, 跳过
                    long sz = 0;
                    try { sz = new FileInfo(data).Length; } catch { }
                    deleted++;
                    freed += sz;
                    if (!st.DryRun)
                    {
                        try { Directory.Delete(id, true); }
                        catch (Exception e) { Plugin.LogWarning($"[KeepCachedVersions] delete fail {id}: {e.Message}"); }
                    }
                    st.Scanned = scanned;
                    st.DeletedEntries = deleted;
                    st.DeletedBytes = freed;
                }
                if (!st.DryRun)
                {
                    try
                    {
                        if (!Directory.GetFileSystemEntries(od).Any())
                            Directory.Delete(od); // 清空后移除空 outer
                    }
                    catch { }
                }
            }
            st.Scanned = scanned;
            st.DeletedEntries = deleted;
            st.DeletedBytes = freed;
        }

        private static string ResolveCacheRoot()
        {
            try
            {
                string p = UnityEngine.Caching.currentCacheForWriting.path;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[KeepCachedVersions] Caching.currentCacheForWriting failed: {e.Message}");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        }
    }
}
