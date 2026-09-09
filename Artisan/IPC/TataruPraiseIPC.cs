using ECommons.DalamudServices;
using ECommons.Logging;
using System;

namespace Artisan.IPC;

/// <summary>
/// 呼叫 TataruPraise（塔塔露誇獎）的 IPC，讓製作清單整份跑完時念一句誇獎。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不加任何組件相依</b>：契約名以字串常數逐字寫在這裡，只走 Dalamud 原生的
/// <c>GetIpcSubscriber</c>。TataruPraise 沒安裝／沒載入時 <c>InvokeFunc</c> 會擲
/// <c>IpcNotReadyError</c>，這裡整個吞掉，Artisan 這邊完全無感。
/// <para>
/// 📌 契約名的權威來源是 <c>TataruPraise/IpcContract.cs</c>。CallGate 是純字串比對，
/// 對不上不會有任何錯誤訊息，只會永遠拿到「沒有人註冊」——<b>失敗形式是靜默的</b>，
/// 所以這幾個字串不要「順手整理」。
/// </para>
/// <para>
/// ⚠️ 每次呼叫都重新取 subscriber，不快取。TataruPraise 可以在 Artisan 載入之後才被裝上／重載，
/// 快取住的 subscriber 在那之後的行為沒有保證；重取的成本只是一次字典查詢。
/// </para>
/// </remarks>
internal static class TataruPraiseIPC
{
    /// <summary><c>Func&lt;string, bool&gt;</c>：<b>這一個情境</b>現在出不出得了聲（總開關＋這個情境的開關＋這個情境有已合成的語音）。</summary>
    /// <remarks>📌 刻意<b>不</b>看冷卻：冷卻是「這一次剛好不出聲」，不是「不能出聲」。</remarks>
    private const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句來念。</summary>
    private const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 情境字串。⚠️ 這是 <c>pool.json</c> 的鍵，TataruPraise 那邊查不到這個鍵時
    /// <c>Praise</c> 只會回 <c>false</c>（不出聲、不報錯），使用者要自己在池裡加「製作」這一類。
    /// </summary>
    internal const string CategoryCrafting = "製作";

    /// <summary>
    /// 「自動化卡住了，需要人來看一下」的情境字串（TataruPraise 的既有內建情境）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只給<b>不是使用者要的停止</b>用：連續錯誤、遊戲說這個製作不可能成功、缺食物藥水、
    /// 以及「失敗就停／非 HQ 就停」真的觸發時。做完了走 <see cref="CategoryCrafting"/>
    /// ——「做完了」跟「做不下去了」念同一句話等於沒講。
    /// </remarks>
    internal const string CategoryNeedHelp = "需要幫忙";

    /// <summary>
    /// 請塔塔露念一句「<paramref name="category"/>」情境的誇獎。
    /// 對方沒裝／沒載入／不想出聲都只是回 <c>false</c>，不擲例外。
    /// </summary>
    /// <remarks>🔴 呼叫端必須在主執行緒上（IPC 的實作是在呼叫端的執行緒上跑的）。</remarks>
    internal static bool Praise(string category)
    {
        try
        {
            // 先問 IsAvailableFor(category)：問的是「這一個情境」出不出得了聲——總開關關著、
            // 使用者把這個情境關掉、或這個情境一句已合成的都沒有，都在這裡擋掉。
            // 🔴 不要退回去問 IsAvailable：那個問的是「整池」，於是「別的情境有句子、
            //    我這個情境一句都沒有」時它照樣回 true，這道閘門等於白做。
            // 這一步同時兼作「對方在不在」的探測——沒註冊就會在這裡擲例外。
            if (!Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(category))
                return false;

            return Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(category);
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError)
        {
            // 對方沒安裝／還沒載入。這是完全正常的情況，靜默。
            return false;
        }
        catch (Exception e)
        {
            // 其他狀況（對方在自己的回呼裡爆掉之類）記一筆就好，絕不要讓它往上冒
            // 打斷「清單完成」的收尾流程。Information 級：回報用的使用者跑 LogLevel 1。
            PluginLog.Information($"[Artisan] 呼叫 TataruPraise 失敗（不影響製作）：{e.Message}");
            return false;
        }
    }

    /// <summary>這一輪掛著的製作<b>還沒</b>說過「停下來了」。</summary>
    /// <remarks>
    /// 🔴 <b>這是防「連續說好幾次」唯一的機制，不要拿掉。</b>耐力模式的收尾與中止路徑有十條，
    /// 而且好幾條會在同一次停止裡先後成立（例如「素材用完」的錯誤提示與 <c>Endurance.Update</c>
    /// 裡的「可做份數為 0」）。這個旗標由 <see cref="ArmStopNotice"/> 在<b>製作還在跑的每一幀</b>
    /// 重新舉起，送出一次就放下 ⇒ <b>一輪最多說一句</b>。
    /// <para>
    /// ⚠️ 刻意<b>不</b>用 <c>EzThrottler</c>：那是整個外掛共用的靜態字典而且零同步，
    /// key 還全域持久、首次必放行——四個性質都跟這裡要的相反。
    /// 這個 bool 只被 framework 執行緒碰（框架更新、toast 與聊天回呼都是），所以不需要鎖。
    /// </para>
    /// <para>
    /// 📌 <b>「整份製作清單跑完」不走這道閘門</b>（<c>CraftingListFunctions.ProcessList</c> 的收尾點
    /// 直接呼叫 <see cref="Praise"/>）：那條路徑本來就只會走到一次，維持原行為。
    /// </para>
    /// </remarks>
    private static bool stopNoticeArmed;

    /// <summary>
    /// 掛著的製作還在跑（耐力模式開著、或清單處理中）時<b>每一幀</b>呼叫一次，
    /// 把「停下來要說一句」重新舉起。
    /// </summary>
    /// <remarks>🔴 刻意只做一個 bool 指派、不加任何條件——這一支每幀都會跑。</remarks>
    internal static void ArmStopNotice() => stopNoticeArmed = true;

    /// <summary>耐力模式<b>自己跑到結束</b>了（份數做完、或素材用完），請塔塔露說一句。</summary>
    /// <param name="reason">寫進記錄檔的原因，不會唸出來。</param>
    /// <remarks>🔴 呼叫端必須在 framework 執行緒上（IPC 的實作是在呼叫端的執行緒上跑的）。</remarks>
    internal static void NotifyEnduranceFinished(string reason)
        => SendStopNotice(CategoryCrafting, P.Config.TataruPraiseFinishEndurance, reason);

    /// <summary>製作<b>被迫停下</b>了（不是使用者要的停止），請塔塔露說一句「需要幫忙」。</summary>
    /// <param name="reason">寫進記錄檔的原因，不會唸出來。</param>
    /// <remarks>🔴 呼叫端必須在 framework 執行緒上（IPC 的實作是在呼叫端的執行緒上跑的）。</remarks>
    internal static void NotifyCraftNeedsHelp(string reason)
        => SendStopNotice(CategoryNeedHelp, P.Config.TataruPraiseCraftNeedHelp, reason);

    private static void SendStopNotice(string category, bool enabled, string reason)
    {
        if (!enabled)
            return;

        if (!stopNoticeArmed)
            return;

        // 🔴 先放下旗標再送：下面任何一步出事都不該讓後面每一幀重試。
        stopNoticeArmed = false;

        // 📌 使用者跑 LogLevel 1（盲區只有 Verbose）⇒ 這是「到底有沒有送出去」唯一的線索。
        //    刻意用 PluginLog 不用 DuoLog：DuoLog 每一個等級都會直接印進使用者的聊天視窗。
        PluginLog.Information($"[Artisan] 掛著的製作停下來了（{reason}），請 TataruPraise 念「{category}」。");

        // Praise 自己會先問 IsAvailableFor，並且把 IpcNotReadyError 與提供端擲的例外全部吃掉。
        Praise(category);
    }
}
