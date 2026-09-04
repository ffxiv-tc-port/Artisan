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
}
