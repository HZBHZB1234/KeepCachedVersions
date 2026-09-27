using UnityEngine;

namespace KeepCachedVersions
{
    /// <summary>游戏语言(与 LOCALIZE_LANGUAGE 对齐, 额外支持 zh 兜底)。</summary>
    public enum GameLang
    {
        KR,
        EN,
        JP,
        ZH,
    }

    /// <summary>多语言文字: 按钮文案与模态窗口文案, 按当前游戏语言取词。</summary>
    public static class Strings
    {
        /// <summary>检测当前语言: 优先 UtilityUI.FontManager.CurrentLanguage, 回退 Application.systemLanguage。</summary>
        public static GameLang DetectLanguage()
        {
            try
            {
                switch (UtilityUI.FontManager.Instance.CurrentLanguage)
                {
                    case LOCALIZE_LANGUAGE.KR: return GameLang.KR;
                    case LOCALIZE_LANGUAGE.JP: return GameLang.JP;
                    case LOCALIZE_LANGUAGE.EN: return GameLang.EN;
                }
            }
            catch { /* FontManager 未就绪时走系统语言 */ }
            try
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.Korean: return GameLang.KR;
                    case SystemLanguage.Japanese: return GameLang.JP;
                    case SystemLanguage.ChineseSimplified:
                    case SystemLanguage.ChineseTraditional: return GameLang.ZH;
                }
            }
            catch { }
            return GameLang.EN;
        }

        private static string T(GameLang l, string en, string jp, string kr, string zh)
        {
            switch (l)
            {
                case GameLang.JP: return jp;
                case GameLang.KR: return kr;
                case GameLang.ZH: return zh;
                default: return en;
            }
        }

        // ---- 按钮 ----
        public static string ClearCacheButton(GameLang l) => T(l,
            "Clear cache", "キャッシュを削除", "캐시 삭제", "清除缓存");

        // ---- 模态窗口 ----
        public static string ModalTitle(GameLang l) => T(l,
            "Cache Cleanup", "キャッシュの整理", "캐시 정리", "缓存管理");

        public static string BtnClearAll(GameLang l) => T(l,
            "Clear all caches", "全キャッシュを削除", "모든 캐시 삭제", "清除全部缓存");

        public static string BtnClearUseless(GameLang l) => T(l,
            "Clear unused caches", "不要なキャッシュを削除", "불필요한 캐시 삭제", "清除无用缓存");

        public static string BtnCancel(GameLang l) => T(l,
            "Cancel", "キャンセル", "취소", "取消");

        public static string BtnClose(GameLang l) => T(l,
            "Close", "閉じる", "닫기", "关闭");

        // ---- 进度 / 结果 ----
        public static string PhaseFetching(GameLang l) => T(l,
            "Fetching cache indexes...", "キャッシュ索引を取得中...", "캐시 인덱스를 가져오는 중...", "正在获取缓存索引...");

        public static string PhaseDeleting(GameLang l) => T(l,
            "Deleting unused cache entries...", "不要なキャッシュを削除中...", "불필요한 캐시를 삭제하는 중...", "正在删除无用缓存...");

        public static string ResultDone(GameLang l, int n, long mb) => T(l,
            $"Done. Deleted {n} entries ({mb} MB).",
            $"完了。{n} 件 ({mb} MB) 削除しました。",
            $"완료. {n}개 ({mb} MB) 삭제했습니다.",
            $"完成。已删除 {n} 个条目 ({mb} MB)。");

        public static string ResultDry(GameLang l, int n, long mb) => T(l,
            $"Dry-run: would delete {n} entries ({mb} MB). (nothing deleted)",
            $"ドライラン: {n} 件 ({mb} MB) を削除予定。(未削除)",
            $"건식 실행: {n}개 ({mb} MB) 삭제 예정. (삭제 안 함)",
            $"预演：将删除 {n} 个条目 ({mb} MB)。（未实际删除）");

        public static string ResultFailed(GameLang l) => T(l,
            "Failed", "失敗", "실패", "失败");

        // ---- 提示 ----
        public static string ResultIdleHint(GameLang l) => T(l,
            "Both servers' cache indexes are requested over HTTPS.\nWith only one index available, deletion is aborted.",
            "両サーバーのキャッシュ索引を HTTPS で取得します。\n片方しか取得できない場合は削除を中止します。",
            "두 서버의 캐시 인덱스를 HTTPS 로 가져옵니다.\n한쪽만 얻으면 삭제를 중단합니다.",
            "将同时通过 HTTPS 请求两服的缓存索引。\n只拿到一份索引时会中止删除。");

        public static string DryRunHint(GameLang l) => T(l,
            "[Dry run] Config DryRun=true — nothing will actually be deleted.",
            "[ドライラン] 設定 DryRun=true — 実際には削除しません。",
            "[건식 실행] 설정 DryRun=true — 실제로 삭제하지 않습니다.",
            "[预演模式] 配置 DryRun=true —— 不会实际删除任何文件。");

        public static string NotifyNeedOtherIndex(GameLang l) => T(l,
            "Only one server's cache index was obtained.\nRefusing to delete: with a single index, the other server's unique bundles would be removed.\nSet the other server's path in the mod config (OtherServerSettingsPath), or run this from the other server, or set AllowSingleIndex=true to force.",
            "片方のサーバーのみのキャッシュ索引を取得しました。\n削除を中止: 単一の索引では、もう片方のサーバー固有の bundle まで削除されてしまいます。\n設定(OtherServerSettingsPath)に他サーバーのパスを設定するか、他サーバー側で実行するか、AllowSingleIndex=true で強制してください。",
            "한쪽 서버의 캐시 인덱스만 가져왔습니다.\n삭제를 중단합니다: 단일 인덱스로는 다른 서버 고유 번들을 삭제하게 됩니다.\n설정(OtherServerSettingsPath)에 다른 서버 경로를 지정하거나, 다른 서버에서 실행하거나, AllowSingleIndex=true 로 강제하세요.",
            "只获取到了一台服务器的缓存索引。\n已中止删除：仅凭单一索引会把另一服独有 bundle 一并删掉。\n请在配置(OtherServerSettingsPath)中设置另一服路径、或到另一服侧执行、或设 AllowSingleIndex=true 强制执行。");
    }
}
