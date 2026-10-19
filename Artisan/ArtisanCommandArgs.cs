namespace Artisan;

/// <summary>/artisan 參數的純判斷（不碰遊戲，可離線測）。</summary>
internal static class ArtisanCommandArgs
{
    /// <summary>
    /// 不帶參數（空字串或只有空白）且主視窗開著＝關閉；主視窗關著時照舊走「開啟總覽」那條路。
    /// Dalamud 只剝掉指令後的第一個空白，「/artisan」後面多打的空白會原樣傳進來。
    /// </summary>
    internal static bool ClosesMainWindow(string args, bool mainWindowOpen)
        => mainWindowOpen && string.IsNullOrWhiteSpace(args);
}
