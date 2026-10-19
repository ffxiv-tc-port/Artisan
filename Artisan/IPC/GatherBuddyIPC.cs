using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Reflection;
using System;
using System.Collections.Generic;

namespace Artisan.IPC
{
    /// <summary>
    /// GatherBuddy Reborn 的自動採集清單與開關。加清單<b>不會</b>叫對方開始採集；
    /// 開始只由使用者按鈕觸發（<see cref="StartAutoGather"/>）。
    /// </summary>
    /// <remarks>
    /// 端點名的大小寫兩邊不一樣，兩個都是對的，不要「統一」：
    /// 外掛的 InternalName 是小寫 b 的 <c>GatherbuddyReborn</c>（feed 產生的 manifest），
    /// 而 IPC 前綴是對方碼裡的常數、大寫 B 的 <c>GatherBuddyReborn</c>。
    /// </remarks>
    internal static class GatherBuddyIPC
    {
        private const string PluginInternalName = "GatherbuddyReborn";
        private const string IpcPrefix          = "GatherBuddyReborn";

        /// <summary>AddToAutoGatherList／GetActiveAutoGatherItemCount 從對方的 IPC 版本 3 起才有。</summary>
        private const int RequiredIpcVersion = 3;

        /// <summary>SetAutoGatherListQuantity／GetAutoGatherListQuantity 從對方的 IPC 版本 4 起才有。</summary>
        private const int SetQuantityIpcVersion = 4;

        /// <summary>問不到清單件數或目標數量時回這個，畫面上要顯示成「?」而不是 0。</summary>
        internal const int UnknownCount = -1;

        internal static bool Installed
            => DalamudReflector.TryGetDalamudPlugin(PluginInternalName, out _, false, true);

        private static DateTime _versionCheck = DateTime.MinValue;
        private static int      _version;

        /// <summary>
        /// 對方的 IPC 版本；沒裝＝0。每幀都會被按鈕的 disable 狀態問到，所以快取一秒，
        /// 不要每幀真的打一次 IPC。
        /// </summary>
        private static int CachedVersion
        {
            get
            {
                if (DateTime.Now < _versionCheck)
                    return _version;

                _versionCheck = DateTime.Now.AddSeconds(1);
                _version      = Installed ? GetVersion() : 0;
                return _version;
            }
        }

        /// <summary>對方在不在、而且版本夠新。</summary>
        internal static bool Supported => CachedVersion >= RequiredIpcVersion;

        /// <summary>對方有「設定目標數量」的端點；沒有就只能走累加的舊路徑。</summary>
        internal static bool SupportsSetQuantity => CachedVersion >= SetQuantityIpcVersion;

        private static int GetVersion()
        {
            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<int>($"{IpcPrefix}.Version").InvokeFunc();
            }
            catch (IpcError)
            {
                return 0;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 問不到 {IpcPrefix}.Version：{e.Message}");
                return 0;
            }
        }

        internal enum TargetOutcome
        {
            Raised,
            AlreadyCovered,
            Failed,
        }

        /// <summary>
        /// 把對方「來自其他插件」清單上這個道具的目標拉到 <paramref name="required"/>（所需數量），原本就不低於它就不動。
        /// 設定語意：重按、Artisan 重新載入都不會疊加。只給 <see cref="SupportsSetQuantity"/> 的對方用。
        /// </summary>
        /// <param name="targetBefore">對方原本的目標；<see cref="UnknownCount"/> ＝ 問不到。</param>
        internal static TargetOutcome RaiseAutoGatherTarget(uint itemId, int required, out int targetBefore)
        {
            targetBefore = GetAutoGatherListQuantity(itemId);
            var target = PlanTarget(required, targetBefore);
            if (target == 0)
                return TargetOutcome.AlreadyCovered;
            if (target < 0 || !SetAutoGatherListQuantity(itemId, target))
                return TargetOutcome.Failed;

            _shownUntil = 0;
            return TargetOutcome.Raised;
        }

        /// <summary>要把對方的目標設成多少。純函式。</summary>
        /// <returns>要設的值；0 ＝ 不必動（所需小於 1，或對方原本就不低於所需）；-1 ＝ 不知道對方現在是多少，不動。</returns>
        /// <remarks>
        /// 取最大值不取相加：相加得記住哪些清單還要做，那份帳做完一份清單就過期，重新載入後又回到疊加的老問題。
        /// 代價是兩份清單要一起做時，目標只會是較大的那份。
        /// </remarks>
        internal static int PlanTarget(int required, int currentTarget)
        {
            if (required < 1)
                return 0;
            if (currentTarget < 0)
                return -1;
            return currentTarget >= required ? 0 : required;
        }

        private static uint _shownItem;
        private static int  _shownTarget = UnknownCount;
        private static long _shownUntil;

        /// <summary>給繪製路徑顯示用：同一個道具一秒內只問一次對方。只在繪製執行緒呼叫。</summary>
        internal static int GetAutoGatherListQuantityForDisplay(uint itemId)
        {
            var now = Environment.TickCount64;
            if (itemId != _shownItem || now >= _shownUntil)
            {
                _shownItem   = itemId;
                _shownTarget = GetAutoGatherListQuantity(itemId);
                _shownUntil  = now + 1000;
            }

            return _shownTarget;
        }

        /// <summary>對方「來自其他插件」清單上這個道具目前的目標數量。</summary>
        /// <returns>數量；0 ＝ 不在清單上；<see cref="UnknownCount"/> ＝ 問不到，<b>不是</b> 0。</returns>
        private static int GetAutoGatherListQuantity(uint itemId)
        {
            if (itemId == 0 || !SupportsSetQuantity)
                return UnknownCount;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<uint, int>($"{IpcPrefix}.GetAutoGatherListQuantity").InvokeFunc(itemId);
            }
            catch (IpcError)
            {
                return UnknownCount;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 問不到 {IpcPrefix}.GetAutoGatherListQuantity({itemId})：{e.Message}");
                return UnknownCount;
            }
        }

        /// <summary>把對方「來自其他插件」清單上這個道具的目標數量設為 <paramref name="quantity"/>（不是加上去）。</summary>
        /// <returns>對方確實設好了才是 <see langword="true"/>。</returns>
        private static bool SetAutoGatherListQuantity(uint itemId, int quantity)
        {
            if (itemId == 0 || !SupportsSetQuantity)
                return false;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<uint, int, bool>($"{IpcPrefix}.SetAutoGatherListQuantity")
                    .InvokeFunc(itemId, quantity);
            }
            catch (IpcError)
            {
                return false;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 呼叫 {IpcPrefix}.SetAutoGatherListQuantity({itemId}, {quantity}) 失敗：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 本次載入期間，每份製作清單已經替每個道具送進對方清單多少。只在繪製執行緒讀寫。
        /// </summary>
        /// <remarks>
        /// 只給舊版對方（沒有設定端點）用：同道具是累加、又沒有查數量的端點，重複送會把目標疊高，只能靠這份帳擋。
        /// 按清單分開記：兩份清單都要同一個道具時，對方的目標本來就該是兩份相加。
        /// 刻意不存檔：對方清單使用者隨時能改，帳留越久越不準。
        /// </remarks>
        private static readonly Dictionary<(int ListId, uint ItemId), int> SentThisSession = new();

        /// <summary>這份清單本次載入已經替這個道具送了多少。</summary>
        internal static int SentFromList(int listId, uint itemId)
            => SentThisSession.GetValueOrDefault((listId, itemId));

        /// <summary>
        /// 舊版對方（沒有設定端點）用的累加路徑：讓這份清單在對方清單上的量補到 <paramref name="required"/>（所需數量）。
        /// 平常只送本次還沒送過的差額；<paramref name="again"/> 是使用者明說對方清單上已經沒有了，照所需數量重送。
        /// </summary>
        /// <remarks>給所需數量不給缺量：對方的數量是「持有到 N 個為止」，會自己扣掉已持有的。</remarks>
        /// <returns>這次送出的量；0 ＝ 不必送；<see langword="null"/> ＝ 對方收不下。</returns>
        internal static int? AddRequiredToAutoGatherList(int listId, uint itemId, int required, bool again = false)
        {
            var key    = (listId, itemId);
            var toSend = PlanSend(required, SentThisSession.GetValueOrDefault(key), again, out var sentAfter);
            if (toSend == 0)
                return 0;
            if (!AddToAutoGatherList(itemId, toSend))
                return null;

            SentThisSession[key] = sentAfter;
            return toSend;
        }

        /// <summary>這一次該送多少，以及送成功後帳上該記多少。純函式。</summary>
        /// <remarks>對方把 1 以下當 1 而且照樣加上去，所以「不送」一律回 0，呼叫端不可以把 0 送出去。</remarks>
        internal static int PlanSend(int required, int alreadySent, bool again, out int sentAfter)
        {
            if (required < 1)
            {
                sentAfter = alreadySent;
                return 0;
            }

            var toSend = again ? required : Math.Max(0, required - alreadySent);
            sentAfter = again ? required : alreadySent + toSend;
            return toSend;
        }

        /// <summary>把道具加進對方的自動採集清單。同道具已在清單上是累加；<paramref name="quantity"/> 小於 1 由對方當 1。</summary>
        /// <returns>對方確實加進去了才是 <see langword="true"/>。</returns>
        private static bool AddToAutoGatherList(uint itemId, int quantity)
        {
            if (itemId == 0 || !Supported)
                return false;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<uint, int, bool>($"{IpcPrefix}.AddToAutoGatherList")
                    .InvokeFunc(itemId, quantity);
            }
            catch (IpcError)
            {
                return false;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 呼叫 {IpcPrefix}.AddToAutoGatherList({itemId}, {quantity}) 失敗：{e.Message}");
                return false;
            }
        }

        /// <summary>對方清單上目前有幾種道具要採。</summary>
        /// <returns>件數；<see cref="UnknownCount"/> ＝ 問不到，<b>不是</b> 0。</returns>
        internal static int GetActiveItemCount()
        {
            if (!Supported)
                return UnknownCount;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<int>($"{IpcPrefix}.GetActiveAutoGatherItemCount").InvokeFunc();
            }
            catch (IpcError)
            {
                return UnknownCount;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 問不到 {IpcPrefix}.GetActiveAutoGatherItemCount：{e.Message}");
                return UnknownCount;
            }
        }

        /// <summary>對方的自動採集開著沒。對方讀的是每幀更新的快照，不會卡住呼叫端。</summary>
        /// <returns><see langword="null"/> ＝ 問不到。</returns>
        internal static bool? IsAutoGatherEnabled()
        {
            if (!Supported)
                return null;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<bool>($"{IpcPrefix}.IsAutoGatherEnabled").InvokeFunc();
            }
            catch (IpcError)
            {
                return null;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 問不到 {IpcPrefix}.IsAutoGatherEnabled：{e.Message}");
                return null;
            }
        }

        /// <summary>對方自動採集目前在做什麼，是對方自己在地化過的文字。</summary>
        /// <returns><see langword="null"/> ＝ 問不到。</returns>
        internal static string? GetAutoGatherStatusText()
        {
            if (!Supported)
                return null;

            try
            {
                return Svc.PluginInterface.GetIpcSubscriber<string>($"{IpcPrefix}.GetAutoGatherStatusText").InvokeFunc();
            }
            catch (IpcError)
            {
                return null;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 問不到 {IpcPrefix}.GetAutoGatherStatusText：{e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 叫對方開始自動採集，等同按它自己的開關。<b>只給使用者按鈕呼叫</b>，不可接成丟料後自動開始。
        /// </summary>
        /// <returns>
        /// 送出後對方回報的開關。<see langword="false"/> ＝ 對方沒接下（等不到主執行緒或正在卸載）；
        /// <see langword="null"/> ＝ 問不到。
        /// </returns>
        internal static bool? StartAutoGather()
        {
            if (!Supported)
                return null;

            try
            {
                Svc.PluginInterface.GetIpcSubscriber<bool, object>($"{IpcPrefix}.SetAutoGatherEnabled").InvokeAction(true);
            }
            catch (IpcError)
            {
                return null;
            }
            catch (Exception e)
            {
                PluginLog.Warning($"[Artisan] 呼叫 {IpcPrefix}.SetAutoGatherEnabled(true) 失敗：{e}");
                return null;
            }

            return IsAutoGatherEnabled();
        }
    }
}
