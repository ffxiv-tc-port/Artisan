using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using static ECommons.GenericHelpers;
using RetainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager;

namespace Artisan.IPC
{
    public static class RetainerInfo
    {
        private static ICallGateSubscriber<ulong?, bool>? _OnRetainerChanged;
        private static ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>? _OnItemAdded;
        private static ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>? _OnItemRemoved;
        private static ICallGateSubscriber<uint, ulong, int, uint>? _ItemCount;
        private static ICallGateSubscriber<uint, ulong, int, uint>? _ItemCountHQ;
        private static ICallGateSubscriber<bool, bool>? _Initialized;
        private static ICallGateSubscriber<bool>? _IsInitialized;
        private static bool _InventoryChanged;

        public static TaskManager TM = new TaskManager();
        internal static bool GenericThrottle => EzThrottler.Throttle("RetainerInfoThrottler", 100);
        internal static void RethrottleGeneric(int num) => EzThrottler.Throttle("RetainerInfoThrottler", num, true);
        internal static void RethrottleGeneric() => EzThrottler.Throttle("RetainerInfoThrottler", 100, true);
        internal static Tasks.RetainerManager retainerManager = new(Svc.SigScanner);

        public static bool AToolsInstalled
        {
            get
            {
                return Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName is "Allagan Tools" or "InventoryTools");
            }
        }

        public static bool AToolsEnabled
        {
            get
            {
                return AToolsInstalled && (DalamudReflector.TryGetDalamudPlugin("Allagan Tools", out var at, false, true) || DalamudReflector.TryGetDalamudPlugin("InventoryTools", out var it, false, true)) && _IsInitialized != null && _IsInitialized.InvokeFunc();
            }
        }

        public static bool ATools
        {
            get
            {
                try
                {
                    return !P.Config.DisableAllaganTools && AToolsEnabled;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static int firstFoundQuantity = 0;

        public static bool CacheBuilt = ATools ? false : true;
        public static CancellationTokenSource CTSource = new();
        public static readonly object _lockObj = new();

        internal static void Init()
        {
            _Initialized = Svc.PluginInterface.GetIpcSubscriber<bool, bool>("AllaganTools.Initialized");
            _IsInitialized = Svc.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
            _Initialized.Subscribe(SetupIPC);
            Svc.ClientState.Logout += LogoutCacheClear;
            SetupIPC(true);
        }

        private static void LogoutCacheClear(int t, int c)
        {
            RetainerData.Clear();
        }

        private static void SetupIPC(bool obj)
        {

            _OnRetainerChanged = Svc.PluginInterface.GetIpcSubscriber<ulong?, bool>("AllaganTools.RetainerChanged");
            _OnItemAdded = Svc.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemAdded");
            _OnItemRemoved = Svc.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemRemoved");

            // 🔴 第 3 個參數（inventoryType）在 AllaganTools 端宣告的是 int，不是 uint
            //    （InventoryTools/IPC/IPCService.cs 的 ItemCount(uint, ulong, int)）。
            //    宣告成 uint 不會炸：Dalamud 的 CallGateChannel.CheckAndConvertArgs 在型別不同時
            //    會用 Newtonsoft 把引數 JSON 來回轉一次 —— 但那是**每次呼叫每個引數**都要付的代價，
            //    而這兩支是 GetRetainerInventoryItem 的熱路徑（每個材料每個僱員 8~15 次）。
            //    ⚠️ 而且轉換只對 <= int.MaxValue 的值成立，超過會擲 IpcTypeMismatchError。
            _ItemCount = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
            _ItemCountHQ = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCountHQ");
            _OnItemAdded.Subscribe(OnItemAdded);
            _OnItemRemoved.Subscribe(OnItemRemoved);
            // 🔴 逾時訊息刻意留在 Verbose（使用者的 LogLevel 收不到 Verbose），
            //    但補回來的不是原本那一行 —— 見 ReportTaskTimeouts()：
            //    由我方在 Tick 觀測任務交接，用 Information 印出「是哪一個任務」。
            //    把這裡改回 false 只會讓同一件事印兩遍，所以不要改。
            TM.TimeoutSilently = true;
        }

        public async static Task<bool?> LoadCache(bool onLoad = false)
        {
            if (onLoad)
            {
                CraftingListUI.CraftableItems.Clear();
                RetainerData.Clear();
            }

            CacheBuilt = false;
            CraftingListUI.CraftableItems.Clear();

            if (P.Config.ShowOnlyCraftable || onLoad)
            {
                // 🔴 改動前這個迴圈是「每個配方一個 Task.Run」,而 CheckForIngredients 裡是
                //    invManager->GetInventoryItemCount ——原生記憶體只能在遊戲主執行緒上讀。
                //    台服 7.20 離線查表:Recipe 表 14,409 列、其中 12,802 列有材料、
                //    合計 63,397 個(配方,材料)對 ⇒ 每次重建快取都是數萬次錯執行緒的原生讀取,
                //    而 AccessViolationException 在 .NET Core 是 corrupted-state exception,
                //    CheckForIngredients 裡那個 catch(以及任何 try/catch)完全攔不到 ——
                //    失敗形式是整個遊戲崩掉,不是回錯的數字。
                // 🔑 修法是把「讀數字」與「比大小」切開:相異材料只有 3,241 個,
                //    在主執行緒上分批讀成純量快照(每批一次往返),之後整個比較迴圈留在背景、
                //    一次原生記憶體都不碰。順帶把原生讀取次數從六萬多降到六千多。
                // 📌 時序沒變:同樣是每個配方一個 Task.Run、同樣寫進 CraftableItems、
                //    同樣在迴圈結束後才 CacheBuilt = true。
                var prefetchRetainers = ATools && P.Config.ShowOnlyCraftableRetainers || onLoad;

                // 先確保跳到背景執行緒:下面列材料 id 是 14,409 列的純資料表走訪,
                // 而快照本身要在「不是主執行緒」時才會分批往返 —— 在主執行緒上呼叫的話
                // 閘門會就地執行,六千多次原生讀取就全擠進同一格畫面。
                var ingredientIds = await Task.Run(() =>
                {
                    var ids = new List<uint>();
                    foreach (var r in LuminaSheets.RecipeSheet.Values)
                    {
                        foreach (var ing in r.Ingredients())
                        {
                            if (ing.Item.RowId != 0 && ing.Amount > 0)
                                ids.Add(ing.Item.RowId);
                        }
                    }
                    return ids;
                });

                var inventorySnapshot = await Task.Run(() => CraftingListUI.SnapshotCraftableCheckCounts(ingredientIds));
                var roster = prefetchRetainers ? await Task.Run(ReadRetainerRoster) : null;

                Svc.Log.Information($"[Artisan][僱員快取] 材料持有量快照完成:{inventorySnapshot.Count} 個相異材料" +
                                    $"(來自 {ingredientIds.Count} 個配方材料項);僱員名冊 " +
                                    $"{(prefetchRetainers ? roster is null ? "不可用" : $"{roster.RetainerIds.Length} 位" : "本輪不需要")}。" +
                                    $"原生讀取全部在遊戲主執行緒上完成,比對迴圈在背景。");

                foreach (var recipe in LuminaSheets.RecipeSheet.Values)
                {
                    if (ATools && P.Config.ShowOnlyCraftableRetainers || onLoad)
                        await Task.Run(() => Safe(() => CraftingListUI.CheckForIngredients(recipe, false, true, inventorySnapshot, roster)));
                    else
                        await Task.Run(() => Safe(() => CraftingListUI.CheckForIngredients(recipe, false, false, inventorySnapshot)));
                }
            }

            // 🔴 上面那個迴圈的 await Task.Run 之後，這裡已經在執行緒池上：Dalamud 外掛沒有
            //    SynchronizationContext，await 的接續不會回到框架執行緒。ClearCache 會走訪整份
            //    RetainerData，CacheBuilt 又是繪製執行緒每幀在讀的旗標 —— 兩者一起交回框架執行緒。
            // 📌 沒進到那個迴圈時（ShowOnlyCraftable 關著且非 onLoad）這裡仍在框架執行緒上：
            //    RunOnFrameworkThread 會就地執行，await 一個已完成的工作不換執行緒，零額外延遲。
            await Svc.Framework.RunOnFrameworkThread(() =>
            {
                ClearCache(null);
                CacheBuilt = true;
            });
            return true;
        }

        // 除錯記錄節流：僱員背包的每一個 add/remove 事件都會清一次快取，逐筆印會在
        // 僱員視窗開著時把整份 log 洗掉（實測 11.5 萬行、峰值 354 行/分，且成對重複）。
        // 這裡只計數，等到某次清除「真的把快取裡的東西清掉了」才印，且每秒最多一行；
        // 被壓下來的事件數會帶在那一行裡，所以資訊不會遺失。清快取的行為完全沒變。
        private static int _cacheClearAdds;
        private static int _cacheClearRemoves;
        private static int _cacheClearEffective;
        private static long _cacheClearNextLogTick;

        // 🔴 AllaganTools 的 ItemAdded／ItemRemoved 回呼跑在「對方外掛的執行緒」上
        //    （InventoryTools 的 InventoryMonitor／CharacterMonitor），而 Dalamud 的
        //    CallGateChannel.SendMessage 對訂閱者是裸 DynamicInvoke、完全不攔例外
        //    （Dalamud/Plugin/Ipc/Internal/CallGateChannel.cs:98-107）⇒ 從這裡擲出去的例外
        //    會傳回 InventoryTools 自己的事件迴圈，把它後面的訂閱者一起打斷。
        //
        //    🔑 所以回呼只做兩件事：Interlocked 計數 + try/catch 兜底。原本在這裡做的
        //    Svc.Condition 原生讀取、RetainerData 走訪（HasCachedRetainerData）與 ClearCache
        //    全部移到 DrainInventoryEvents()，由 Artisan.OnFrameworkUpdate 在框架執行緒排乾。
        //    RetainerData 是裸 Dictionary：從對方的執行緒清它，正在走訪它的框架／繪製執行緒
        //    會擲 InvalidOperationException，而那個例外會被 GetRetainerItemCount 的 catch
        //    吞成「回 0」——失敗形式是數量靜默變成 0，不是報錯。
        private static int _pendingItemAdded;
        private static int _pendingItemRemoved;

        private static bool HasCachedRetainerData()
        {
            foreach (var retainer in RetainerData)
            {
                if (retainer.Value.Count > 0)
                    return true;
            }
            return false;
        }

        private static void NoteCacheCleared(int added, int removed, bool hadCachedData)
        {
            _cacheClearAdds += added;
            _cacheClearRemoves += removed;

            // 快取本來就是空的，這次清除等於沒做事，不值得佔一行 log。
            if (!hadCachedData)
                return;

            _cacheClearEffective++;

            var now = Environment.TickCount64;
            if (now < _cacheClearNextLogTick)
                return;
            _cacheClearNextLogTick = now + 1000;

            Svc.Log.Debug($"Retainer cache cleared ({_cacheClearEffective}x effective) after {_cacheClearAdds} item added / {_cacheClearRemoves} item removed event(s)");
            _cacheClearAdds = 0;
            _cacheClearRemoves = 0;
            _cacheClearEffective = 0;
        }

        private static void OnItemAdded((uint, InventoryItem.ItemFlags, ulong, uint) tuple)
        {
            try
            {
                Interlocked.Increment(ref _pendingItemAdded);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Artisan] AllaganTools ItemAdded 事件處理失敗：{ex.Message}");
            }
        }

        private static void OnItemRemoved((uint, InventoryItem.ItemFlags, ulong, uint) tuple)
        {
            try
            {
                Interlocked.Increment(ref _pendingItemRemoved);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Artisan] AllaganTools ItemRemoved 事件處理失敗：{ex.Message}");
            }
        }

        /// <summary>
        /// 把 AllaganTools 事件回呼記下的待處理筆數在框架執行緒上排乾：判斷條件旗標、清僱員快取。
        /// <para/>
        /// 由 <c>Artisan.OnFrameworkUpdate</c> 每幀無條件呼叫（含未登入時），所以計數不會累積跨越登入。
        /// ⚠️ 與舊版的唯一行為差異：<c>OccupiedSummoningBell</c> 改成在排乾的那一刻取樣，
        /// 不是事件抵達的那一刻 —— 兩者最多差一幀。同一幀進來的多筆事件合併成一次清除
        /// （ClearCache 本來就是等冪的），但 add／remove 的筆數仍逐筆計入診斷行。
        /// </summary>
        internal static void DrainInventoryEvents()
        {
            var added = Interlocked.Exchange(ref _pendingItemAdded, 0);
            var removed = Interlocked.Exchange(ref _pendingItemRemoved, 0);
            if (added == 0 && removed == 0)
                return;

            if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                return;

            NoteCacheCleared(added, removed, HasCachedRetainerData());
            ClearCache(null);
            _InventoryChanged = true;
        }

        internal static void Dispose()
        {
            _Initialized?.Unsubscribe(SetupIPC);
            _OnItemAdded?.Unsubscribe(OnItemAdded);
            _OnItemRemoved?.Unsubscribe(OnItemRemoved);
            Svc.ClientState.Logout -= LogoutCacheClear;
            _Initialized = null;
            _IsInitialized = null;
            _OnRetainerChanged = null;
            _OnItemAdded = null;
            _OnItemRemoved = null;
            _ItemCount = null;
        }

        /// <summary>
        /// 僱員背包快取。
        /// <para/>
        /// 🔴 為什麼是 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是 <c>Dictionary</c>：
        /// 這份資料同時被三條執行緒碰 ——
        /// ①框架／繪製執行緒（材料表、<c>Framework.Update</c> 上的排乾與 TaskManager）；
        /// ②執行緒池：<c>CraftingListUI.cs</c> 與 <c>ListEditor.cs</c> 兩個「從僱員取回」按鈕都是
        /// <c>Task.Run(() =&gt; RestockFromRetainers(...))</c>，而它會呼叫 <c>GetRetainerItemCount</c>（寫）
        /// 並走訪整份快取（讀）；
        /// ③同樣是執行緒池：<c>LoadCache</c> 迴圈裡的 <c>await Task.Run(...)</c> 走 <c>CheckForIngredients</c>
        /// → <c>GetRetainerItemCount</c>（寫）。
        /// <para/>
        /// 裸 <c>Dictionary</c> 在這種形狀下的失敗形式<b>不是「拿到舊值」而是字典本身壞掉</b>，
        /// 而走訪中的並行改動會擲 <c>InvalidOperationException</c> —— 那個例外會被
        /// <c>GetRetainerItemCount</c> 自己的 <c>catch</c> 吞成「回 0」，使用者看到的是僱員持有量
        /// 莫名其妙變成 0，log 上一片安靜。
        /// <para/>
        /// ⚠️ 換成 <c>ConcurrentDictionary</c> 之後 LINQ 走訪是<b>弱一致</b>的：不會擲例外，但
        /// 讀到的可能是走訪期間的混合快照。對這裡的用途（估算持有量、決定要拜訪哪些僱員）
        /// 與改動前「剛好沒撞到」時拿到的結果同級，而改動前撞到的那一次是例外不是舊值。
        /// </summary>
        public static ConcurrentDictionary<ulong, ConcurrentDictionary<uint, ItemInfo>> RetainerData = new();

        /// <summary>
        /// 單一僱員身上單一物品的數量快照。
        /// <para/>
        /// 🔑 三個屬性刻意<b>沒有 setter</b>：更新一律整份換掉（<c>ret[id] = new ItemInfo(...)</c>）。
        /// 就地改欄位的話，讀取端可能看到「新的 Quantity ＋ 舊的 HQQuantity」這種撕裂組合
        /// —— 參考指派本身是不可分割的，三個獨立的 uint 寫入不是。
        /// </summary>
        public class ItemInfo
        {
            public uint ItemId { get; }

            public uint Quantity { get; }

            public uint HQQuantity { get; }

            public ItemInfo(uint itemId, uint quantity, uint hqQuantity)
            {
                ItemId = itemId;
                Quantity = quantity;
                HQQuantity = hqQuantity;
            }
        }

        public static void ClearCache(ulong? RetainerId)
        {
            RetainerData.Each(x => x.Value.Clear());
        }

        public static unsafe uint GetRetainerInventoryItem(uint ItemId, ulong retainerId, bool hqonly = false)
        {
            if (ATools)
            {
                if (!hqonly)
                {
                    return _ItemCount.InvokeFunc(ItemId, retainerId, 10000) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10001) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10002) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10003) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10004) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10005) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, 10006) +
                            _ItemCount.InvokeFunc(ItemId, retainerId, (int)InventoryType.RetainerCrystals);
                }
                else
                {
                    return _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10000) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10001) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10002) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10003) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10004) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10005) +
                            _ItemCountHQ.InvokeFunc(ItemId, retainerId, 10006);
                }
            }
            return 0;
        }
        /// <summary>
        /// Refreshes the cached quantities of a single item on a single retainer. This is what the extraction
        /// loop actually needs: <see cref="GetRetainerItemCount"/> re-walks all ten retainers and issues 8-15
        /// AllaganTools IPC calls for each of them, and the extraction path then only ever reads back the entry
        /// for the retainer whose window is currently open.
        /// </summary>
        private static void RefreshRetainerItem(uint ItemId, ulong retainerId)
        {
            if (!ATools || retainerId == 0) return;
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial]) return;

            try
            {
                // GetOrAdd 取代「TryGetValue 不中就自己建一個再指派」：後者在兩條執行緒同時
                // 走到時會各自建一份，其中一份連同已經寫進去的項目一起被覆蓋掉。
                var ret = RetainerData.GetOrAdd(retainerId, static _ => new ConcurrentDictionary<uint, ItemInfo>());

                var quantity = GetRetainerInventoryItem(ItemId, retainerId);
                var hq = GetRetainerInventoryItem(ItemId, retainerId, true);
                // 兩條分支（有舊值／沒有舊值）本來就寫出同一組值，整份替換之後合而為一。
                ret[ItemId] = new ItemInfo(ItemId, quantity, hq);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Artisan][Restock] Could not refresh item {ItemId} on retainer {retainerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// 十個僱員欄位解析完之後,「這一輪要去問哪些僱員」的純量快照。
        /// </summary>
        /// <remarks>
        /// 🔑 這是把原生讀取與慢速 IPC 切開的那條線:解析名冊要碰
        /// <c>RetainerManager.Instance()-&gt;GetRetainerBySortedIndex</c>、<c>retainer-&gt;Available</c>、
        /// <c>Svc.PlayerState.ContentId</c>、<c>Svc.Condition</c> —— 全部只能在遊戲主執行緒上;
        /// 而拿到 id 之後的 <c>AllaganTools</c> IPC 是<b>慢速的受管理呼叫</b>
        /// (每個僱員 8~15 次、整份清單會跑上好幾秒),**不能**搬到主執行緒上做,
        /// 那會把遊戲整個卡住。
        /// </remarks>
        public sealed class RetainerRoster
        {
            /// <summary>要查詢的僱員 id,順序與遊戲顯示順序一致;已扣掉「不可用」的那些。</summary>
            public ulong[] RetainerIds { get; }

            internal RetainerRoster(ulong[] retainerIds) => RetainerIds = retainerIds;
        }

        private const string RosterEndpoint = "RetainerInfo.GetRetainerItemCount(僱員名冊)";

        /// <summary>
        /// 在遊戲主執行緒上解析一次僱員名冊。<b>取不到時回 <c>null</c></b>
        /// (沒登入／免費體驗／Dalamud 卸載期／等主執行緒逾時)。
        /// </summary>
        /// <remarks>
        /// 📌 給「同一輪要問成千上萬個道具」的呼叫端用(<see cref="LoadCache"/>、
        /// <see cref="RestockFromRetainers(NewCraftingList)"/>):解析一次之後把結果傳進
        /// <see cref="GetRetainerItemCount"/>,整輪就只付一次主執行緒往返。
        /// 🔴 不傳的話 <see cref="GetRetainerItemCount"/> 會自己解一次 —— 那是正確的,
        /// 但從背景執行緒呼叫時<b>每個道具要等一幀</b>,三千多個道具就是一分鐘級的延遲。
        /// </remarks>
        public static RetainerRoster? ReadRetainerRoster()
            => IpcFrameworkGate.Get<RetainerRoster?>(RosterEndpoint, ReadRetainerRosterCore, null);

        /// <summary><b>只能在遊戲主執行緒上呼叫</b>,由 <see cref="ReadRetainerRoster"/> 那一層的閘門保證。</summary>
        private static unsafe RetainerRoster? ReadRetainerRosterCore()
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial]) return null;

            // Resolved once instead of rebuilding the same filtered array inside all ten iterations
            // below - this method is called once per material when a list is restocked, so the old
            // Where().Select().ToArray()[i] allocated ten arrays per material for no reason.
            var configuredRetainerIds = P.Config.RetainerIDs
                .Where(x => x.Value == Svc.PlayerState.ContentId)
                .Select(x => x.Key)
                .ToArray();

            // GetRetainerBySortedIndex walks the display-order table at +0x2D0 and returns null
            // whenever that table holds a value >= 10 - which it does before the retainer list has
            // finished loading, and after a character switch leaves stale entries behind. The
            // surrounding catch cannot save us here: dereferencing null is an AccessViolation, a
            // corrupted-state exception that try/catch does not intercept in .NET Core.
            var retainerManager = RetainerManager.Instance();
            var wanted = new List<ulong>(10);

            for (int i = 0; i < 10; i++)
            {
                ulong retainerId = 0;
                var retainer = retainerManager is null ? null : retainerManager->GetRetainerBySortedIndex((uint)i);

                if (configuredRetainerIds.Length > i)
                {
                    retainerId = configuredRetainerIds[i];
                }
                else if (retainer is not null && retainer->Available)
                {
                    retainerId = retainer->RetainerId;
                }

                if (retainer is not null)
                {
                    if (retainer->RetainerId > 0 && !P.Config.RetainerIDs.Any(x => x.Key == retainer->RetainerId && x.Value == Svc.PlayerState.ContentId))
                    {
                        if (retainer->Available)
                        {
                            P.Config.RetainerIDs.Add(retainer->RetainerId, Svc.PlayerState.ContentId);
                            P.Config.Save();
                        }
                    }

                    if (!retainer->Available)
                    {
                        if (retainer->RetainerId > 0 && !P.Config.UnavailableRetainerIDs.Contains(retainer->RetainerId))
                        {
                            P.Config.UnavailableRetainerIDs.Add(retainer->RetainerId);
                            P.Config.Save();
                        }
                    }
                    else
                    {
                        if (P.Config.UnavailableRetainerIDs.Contains(retainer->RetainerId))
                        {
                            P.Config.UnavailableRetainerIDs.RemoveWhere(x => x == retainer->RetainerId);
                            P.Config.Save();
                        }
                    }
                }

                if (retainerId > 0 && !P.Config.UnavailableRetainerIDs.Any(x => x == retainerId))
                    wanted.Add(retainerId);
            }

            return new RetainerRoster(wanted.ToArray());
        }

        /// <summary>
        /// 主執行緒上要一次做完的前置:可用性判斷 → 快取快捷路徑 → 名冊解析。
        /// 三件事的<b>順序與改動前逐字相同</b>,所以「沒登入就回 0」仍然發生在快取快捷路徑之前。
        /// </summary>
        private readonly struct CountPrologue
        {
            public readonly bool Unavailable;
            public readonly int? Cached;
            public readonly RetainerRoster? Roster;

            private CountPrologue(bool unavailable, int? cached, RetainerRoster? roster)
            {
                Unavailable = unavailable;
                Cached = cached;
                Roster = roster;
            }

            public static CountPrologue NotAvailable => new(true, null, null);
            public static CountPrologue FromCache(int value) => new(false, value, null);
            public static CountPrologue FromRoster(RetainerRoster roster) => new(false, null, roster);
        }

        private const string PrologueEndpoint = "RetainerInfo.GetRetainerItemCount(前置)";

        private static int SumCachedRetainerItem(uint ItemId, bool hqOnly)
        {
            if (hqOnly)
            {
                return (int)RetainerData.Values.SelectMany(x => x.Values).Where(x => x.ItemId == ItemId).Sum(x => x.HQQuantity);
            }

            return (int)RetainerData.SelectMany(x => x.Value).Where(x => x.Key == ItemId).Sum(x => x.Value.Quantity);
        }

        /// <summary>
        /// 這個道具在所有僱員身上的數量。
        /// </summary>
        /// <param name="roster">
        /// 呼叫端已經在主執行緒上解析好的僱員名冊(<see cref="ReadRetainerRoster"/>)。
        /// 不給的話這支自己解一次 —— 正確,但從背景執行緒呼叫時<b>每次要等一幀</b>。
        /// </param>
        /// <remarks>
        /// 🔴 為什麼要拆:改動前整支都在呼叫端的執行緒上跑,而
        /// <c>Svc.ClientState.IsLoggedIn</c>(→ <c>AgentLobby.Instance()-&gt;IsLoggedIn</c>)、
        /// <c>Svc.Condition[...]</c>、<c>Svc.PlayerState.ContentId</c>
        /// (→ <c>PlayerState.Instance()-&gt;ContentId</c>)與
        /// <c>RetainerManager.Instance()-&gt;GetRetainerBySortedIndex</c>／<c>retainer-&gt;Available</c>
        /// 全部是原生解參考,只能在遊戲主執行緒上讀。
        /// 2026-09-12 實測有三條背景路徑會打到這裡:<c>LoadCache</c> 的
        /// <c>Task.Run(CheckForIngredients)</c>、兩顆「從僱員取回」按鈕的
        /// <c>Task.Run(RestockFromRetainers)</c>、以及 <c>ListEditor</c> 表格重建的
        /// <c>Task.Run(GenerateTableAsync)</c> → <c>GetEffectiveCraftQuantity</c>。
        /// <c>AccessViolationException</c> 在 .NET Core 是 corrupted-state exception,
        /// 底下那個 <c>catch</c> 攔不到,使用者看到的是整個遊戲崩掉。
        /// <para/>
        /// 🔴 <b>慢速的 AllaganTools IPC 迴圈刻意留在呼叫端的執行緒上</b> ——
        /// 每個僱員 8~15 次 IPC、整份清單好幾秒,搬到主執行緒上會把遊戲卡住。
        /// 它碰的 <c>RetainerData</c> 是 <c>ConcurrentDictionary</c>,本來就設計成跨執行緒共用。
        /// <para/>
        /// ⚠️ <b>唯一的語意差</b>:呼叫端傳 <paramref name="roster"/> 時,可用性
        /// (登入中／非免費體驗)是「該輪解析名冊的那一刻」判斷的,不再逐個道具重判。
        /// 那是把三千多次原生讀取收斂成一次的必然代價;若掃描途中登出,
        /// <c>Svc.ClientState.Logout</c> 會清掉 <c>RetainerData</c>,而 AllaganTools
        /// 對過期的僱員 id 本來就回 0 ⇒ 結果方向仍然是「少報」。
        /// </remarks>
        public static int GetRetainerItemCount(uint ItemId, bool tryCache = true, bool hqOnly = false, RetainerRoster? roster = null)
        {

            if (ATools)
            {
                try
                {
                    if (roster is null)
                    {
                        var prologue = IpcFrameworkGate.Get(PrologueEndpoint, () =>
                        {
                            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial])
                                return CountPrologue.NotAvailable;

                            if (tryCache && RetainerData.SelectMany(x => x.Value).Any(x => x.Key == ItemId))
                                return CountPrologue.FromCache(SumCachedRetainerItem(ItemId, hqOnly));

                            var resolved = ReadRetainerRosterCore();
                            return resolved is null ? CountPrologue.NotAvailable : CountPrologue.FromRoster(resolved);
                        }, CountPrologue.NotAvailable);

                        if (prologue.Unavailable) return 0;
                        if (prologue.Cached is int cached) return cached;
                        roster = prologue.Roster!;
                    }
                    else if (tryCache && RetainerData.SelectMany(x => x.Value).Any(x => x.Key == ItemId))
                    {
                        return SumCachedRetainerItem(ItemId, hqOnly);
                    }

                    foreach (var retainerId in roster.RetainerIds)
                    {
                        // 改動前這裡是「有這個僱員」與「沒有這個僱員」兩段一模一樣的複製貼上，
                        // 而 else 那段先 TryAdd 再用索引子取回 —— 中間若有人 Clear() 就是
                        // KeyNotFoundException。GetOrAdd 一次搞定，兩段合一，結果逐字相同。
                        var ret = RetainerData.GetOrAdd(retainerId, static _ => new ConcurrentDictionary<uint, ItemInfo>());
                        if (ret.TryGetValue(ItemId, out var item))
                        {
                            // ⚠️ 只更新 Quantity、沿用舊的 HQQuantity 是改動前就有的行為
                            //（原本是 item.Quantity = ... 而完全沒碰 item.HQQuantity），逐字保留：
                            // 改成一併更新會多送一輪 AllaganTools IPC，也不是這次要改的東西。
                            ret[ItemId] = new ItemInfo(ItemId, GetRetainerInventoryItem(ItemId, retainerId), item.HQQuantity);
                        }
                        else
                        {
                            ret.TryAdd(ItemId, new ItemInfo(ItemId, GetRetainerInventoryItem(ItemId, retainerId), GetRetainerInventoryItem(ItemId, retainerId, true)));
                        }
                    }

                    return SumCachedRetainerItem(ItemId, hqOnly);
                }
                catch (Exception ex)
                {
                    //Svc.Log.Error(ex, "RetainerInfoItemCount");
                    return 0;
                }
            }

            return 0;
        }

        /// <summary>
        /// 單一物品的「從僱員取回」。整支交回遊戲主執行緒之後才開始排任務。
        /// </summary>
        /// <remarks>
        /// 🔴 方法體十餘次 <c>TM.Enqueue</c>／<c>TM.DelayNext</c>／<c>TM.EnqueueImmediate</c>
        /// 動的是 <c>LegacyTaskManager</c> 的 <c>Tasks</c> 與 <c>ImmediateTasks</c> ——
        /// 兩個都是<b>裸 <c>List</c></b>（ECommons 自己的註解明寫
        /// "only ever do that from Framework.Update event"），而框架執行緒每一幀都在走訪它們。
        /// 並行插入的失敗形式<b>不是「拿到舊值」而是集合本身壞掉</b>，走訪中的並行改動則擲
        /// <c>InvalidOperationException</c>。
        /// <para/>
        /// 📌 目前兩個呼叫點（<c>IngredientTable</c> 的右鍵項、<c>CraftingListContextMenu</c> 的
        /// 「從僱員取出」）本來就在遊戲主執行緒上 ⇒ 閘門就地執行，行為逐字不變、不多花一幀。
        /// 這一層擋的是「哪天有人跟著清單版那兩個按鈕也包一層 <c>Task.Run</c>」。
        /// </remarks>
        public static void RestockFromRetainers(uint ItemId, int howManyToGet)
            => IpcFrameworkGate.Run("RetainerInfo.RestockFromRetainers(單品)", () => RestockSingleCore(ItemId, howManyToGet));

        /// <summary><b>只能在框架執行緒上呼叫</b>，由上面那層閘門保證。</summary>
        private static void RestockSingleCore(uint ItemId, int howManyToGet)
        {
            if (RetainerData.SelectMany(x => x.Value).Any(x => x.Value.ItemId == ItemId && x.Value.Quantity > 0))
            {
                Svc.Log.Information($"[Artisan][Restock] Single-item restock starting: item {ItemId} x{howManyToGet}.");
                TM.Enqueue(() => BeginRestockChain());
                TM.Enqueue(() => AutoRetainerIPC.Suppress());
                TM.EnqueueBell();
                TM.DelayNext("BellInteracted", 200);

                var retainerListSorted = RetainerData.Where(x => x.Value.Values.Any(y => y.ItemId == ItemId && y.HQQuantity > 0)).ToDictionary(x => x.Key, x => x.Value);
                RetainerData.Where(x => x.Value.Values.Any(y => y.ItemId == ItemId && y.Quantity > 0)).ToList().ForEach(x => retainerListSorted.TryAdd(x.Key, x.Value));

                foreach (var retainer in retainerListSorted)
                {
                    TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(retainer.Key), 5000, true, "SelectRetainer");
                    TM.DelayNext("WaitToSelectEntrust", 200);
                    TM.Enqueue(() => RetainerHandlers.SelectEntrustItems());
                    TM.DelayNext("EntrustSelected", 200);
                    TM.Enqueue(() =>
                    {
                        ExtractSingular(ItemId, howManyToGet, retainer.Key);
                    }, "ExtractSingularEntry");

                    TM.DelayNext("CloseRetainer", 200);
                    TM.Enqueue(() => RetainerHandlers.CloseAgentRetainer());
                    TM.DelayNext("ClickQuit", 200);
                    TM.Enqueue(() => RetainerHandlers.SelectQuit());
                    TM.Enqueue(() =>
                    {
                        if (CraftingListUI.NumberOfIngredient(ItemId) >= howManyToGet)
                        {
                            TM.DelayNextImmediate("CloseRetainerList", 200);
                            TM.EnqueueImmediate(() => RetainerListHandlers.CloseRetainerList());
                            TM.EnqueueImmediate(() => YesAlready.Unlock());
                            TM.EnqueueImmediate(() => AutoRetainerIPC.Unsuppress());
                            TM.EnqueueImmediate(() => Svc.Framework.Update -= Tick);
                            TM.EnqueueImmediate(() => TM.Abort());
                        }
                    });
                }

                TM.DelayNext("CloseRetainerList", 200);
                TM.Enqueue(() => RetainerListHandlers.CloseRetainerList());
                TM.Enqueue(() => YesAlready.Unlock());
                TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
                TM.Enqueue(() => Svc.Framework.Update -= Tick);
            }
        }

        /// <summary>
        /// 依快取判斷「這個僱員身上有沒有這個物品的 HQ 版本」。
        /// <para/>
        /// 🔴 改動前四個呼叫點都是 <c>RetainerData[retainerId]</c> 索引子 —— 快取在
        /// 判斷的那一刻剛好被清空（登出、<c>LoadCache</c>、AllaganTools 事件排乾）就是
        /// <c>KeyNotFoundException</c>，而這四個點全都在取回流程的中途。
        /// <para/>
        /// ⚠️ 快取裡沒有這個僱員時回 <c>false</c>，與「有這個僱員但沒有 HQ」同義：兩者都讓
        /// 取回路徑去找任意品質，那正是快取還沒建起來時本來就有的行為。
        /// </summary>
        private static bool WantsHQFromRetainer(ulong retainerId, uint itemId)
            => RetainerData.TryGetValue(retainerId, out var retainerCache)
               && retainerCache.Values.Any(x => x.ItemId == itemId && x.HQQuantity > 0);

        public static bool ExtractSingular(uint ItemId, int howManyToGet, ulong retainerKey)
        {
            if (howManyToGet != 0 && RetainerDirectFetch.Available)
            {
                bool wantHQ = WantsHQFromRetainer(retainerKey, ItemId);
                EnqueueDirectExtract(ItemId, wantHQ, howManyToGet, (gained, fallBack) =>
                {
                    var remaining = Math.Max(0, howManyToGet - gained);
                    if (fallBack && remaining > 0)
                        TM.EnqueueImmediate(() => { ExtractSingularViaWindow(ItemId, remaining, retainerKey); }, "FallbackExtractSingular");
                });
                return true;
            }

            return ExtractSingularViaWindow(ItemId, howManyToGet, retainerKey);
        }

        /// <summary>The original retainer-window path for a single item - see
        /// <see cref="ExtractItemViaWindow"/>.</summary>
        private static bool ExtractSingularViaWindow(uint ItemId, int howManyToGet, ulong retainerKey)
        {
            Svc.Log.Debug($"{howManyToGet}");
            if (howManyToGet != 0)
            {
                bool lookingForHQ = WantsHQFromRetainer(retainerKey, ItemId);
                TM.DelayNextImmediate("WaitOnRetainerInventory", 500);
                TM.EnqueueImmediate(() => RetainerHandlers.OpenItemContextMenu(ItemId, lookingForHQ, out firstFoundQuantity), 300);
                TM.DelayNextImmediate("WaitOnNumericPopup", 200);
                TM.EnqueueImmediate(() =>
                {
                    if (Math.Min(howManyToGet, (int)firstFoundQuantity) == 0) return true;

                    var freeSlots = GetFreeInventorySlots();
                    var value = WithdrawalQuantity(howManyToGet, (int)firstFoundQuantity, freeSlots);
                    Svc.Log.Information($"[Artisan][Restock] item {ItemId}: withdrawing {value} of the {firstFoundQuantity} in this stack " +
                                        $"(still needed {howManyToGet}, free bag slots {(freeSlots < 0 ? "unknown" : freeSlots.ToString())}).");
                    if (firstFoundQuantity == 1)
                    {
                        howManyToGet = Math.Max(0, howManyToGet - (int)firstFoundQuantity);
                        TM.EnqueueImmediate(() =>
                        {
                            // Stays on the window path - see the matching note in ExtractItemViaWindow.
                            ExtractSingularViaWindow(ItemId, howManyToGet, retainerKey);
                        });
                        return true;
                    }
                    if (RetainerHandlers.InputNumericValue(value))
                    {
                        // Clamp for the same reason as ExtractItem: a whole-stack withdrawal can overshoot,
                        // and howManyToGet is compared against 0 to end the recursion.
                        howManyToGet = Math.Max(0, howManyToGet - value);

                        TM.EnqueueImmediate(() =>
                        {
                            ExtractSingularViaWindow(ItemId, howManyToGet, retainerKey);
                        });
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }, 1000);
            }

            return true;
        }

        public static void RestockFromRetainers(NewCraftingList list)
        {
            Dictionary<int, int> requiredItems = new();
            Dictionary<uint, int> materialList = new();

            // The loops below call GetRetainerItemCount once per material, and each of those walks all ten
            // retainers over AllaganTools IPC. That happens synchronously on the framework thread before any
            // retainer window is even opened, so a long list stalls here with nothing visible happening.
            // Timed at Information level because that is the log level users actually run.
            var planStartedAt = Environment.TickCount64;

            Svc.Log.Debug($"Making material list");

            materialList = list.ListMaterials();

            Svc.Log.Debug($"Creating Fetch List");

            // 🔴 這支的兩個呼叫點都是 Task.Run(CraftingListUI.cs 與 ListEditor.cs 的
            //    「從僱員取回」按鈕)⇒ 整段跑在執行緒池上,而 NumberOfIngredient 與
            //    GetRetainerItemCount 底下都是原生解參考(invManager->GetInventoryItemCount／
            //    GetInventoryContainer／GetInventorySlot、RetainerManager.Instance()->
            //    GetRetainerBySortedIndex、PlayerState->ContentId、Svc.Condition)。
            //    原生記憶體只能在遊戲主執行緒上讀,而 AccessViolationException 在 .NET Core
            //    是 corrupted-state exception,try/catch 攔不到 —— 失敗形式是整個遊戲崩掉。
            // 🔑 這裡的修法是「一次讀完」而不是「每個材料往返一次」:
            //    往返一次大約要等一幀,長清單的材料數以百計,逐個往返就是好幾秒的額外延遲。
            //    僱員名冊同理,解析一次之後傳給每一次 GetRetainerItemCount。
            //    ⚠️ 慢速的 AllaganTools IPC 迴圈刻意留在背景(見 GetRetainerItemCount 的 remarks)。
            var snapshotIds = new List<uint>(materialList.Keys);
            if (P.Config.RestockFinishedProductsFromRetainers)
            {
                foreach (var entry in list.Recipes)
                    snapshotIds.Add(LuminaSheets.RecipeSheet[entry.ID].ItemResult.RowId);
            }

            var invCounts = CraftingListUI.SnapshotNumberOfIngredient(snapshotIds);
            var roster = ReadRetainerRoster();
            Svc.Log.Information($"[Artisan][Restock] 背包持有量快照完成:{invCounts.Count} 個道具" +
                                $"(原生讀取在遊戲主執行緒上);僱員名冊 " +
                                $"{(roster is null ? "不可用" : $"{roster.RetainerIds.Length} 位")}。");

            foreach (var material in materialList.OrderByDescending(x => x.Key))
            {
                Svc.Log.Debug($"{material}");
                var invCount = invCounts.GetValueOrDefault(material.Key);
                if (invCount < material.Value)
                {
                    var diffcheck = material.Value - invCount;
                    Svc.Log.Debug($"{material.Key} {diffcheck}");
                    requiredItems.Add((int)material.Key, diffcheck);
                }

                //Refresh retainer cache if empty
                GetRetainerItemCount(material.Key, roster: roster);
            }

            if (P.Config.RestockFinishedProductsFromRetainers)
            {
                foreach (var entry in list.Recipes)
                {
                    var recipe = LuminaSheets.RecipeSheet[entry.ID];
                    var target = entry.Quantity * recipe.AmountResult;
                    var invCount = invCounts.GetValueOrDefault(recipe.ItemResult.RowId);
                    if (invCount < target)
                    {
                        var diffcheck = target - invCount;
                        Svc.Log.Debug($"{recipe.ItemResult.RowId} {diffcheck}");
                        if (requiredItems.ContainsKey((int)recipe.ItemResult.RowId))
                            requiredItems[(int)recipe.ItemResult.RowId] += diffcheck;
                        else
                            requiredItems.Add((int)recipe.ItemResult.RowId, diffcheck);
                    }

                    //Refresh retainer cache if empty
                    GetRetainerItemCount(recipe.ItemResult.RowId, roster: roster);
                }
            }

            // 🔴 這一行是「背景」與「主執行緒」的分界。
            //    上面那段（列材料、比對背包、GetRetainerItemCount）每個材料都要走十個僱員、
            //    每個僱員 8~15 次 AllaganTools IPC，長清單會跑上好幾秒 —— 那才是
            //    CraftingListUI 與 ListEditor 兩顆按鈕用 Task.Run 包起來的真正理由，所以留在背景。
            //    下面那段只是把任務推進 TaskManager 的裸 List，本來就不阻塞，
            //    但必須在框架執行緒上做（理由見 RestockFromRetainers(uint,int) 的 remarks）。
            IpcFrameworkGate.Run("RetainerInfo.RestockFromRetainers(清單)", () => EnqueueListRestock(requiredItems, planStartedAt));
        }

        /// <summary>
        /// 把「清單取回」的整條任務鏈推進 <see cref="TM"/>。
        /// <b>只能在框架執行緒上呼叫</b>，由 <see cref="RestockFromRetainers(NewCraftingList)"/>
        /// 那一層的 <see cref="IpcFrameworkGate"/> 保證。
        /// </summary>
        private static void EnqueueListRestock(Dictionary<int, int> requiredItems, long planStartedAt)
        {
            if (RetainerData.SelectMany(x => x.Value).Any(x => requiredItems.Any(y => y.Key == x.Value.ItemId)))
            {
                Svc.Log.Debug($"Processing Retainer Data");
                Svc.Log.Information($"[Artisan][Restock] List restock starting: {requiredItems.Count(x => x.Value > 0)} item(s) short, " +
                                    $"planning visits to {RetainerData.Count(r => r.Value.Values.Any(x => requiredItems.Any(y => y.Value > 0 && y.Key == x.ItemId && x.Quantity > 0)))} retainer(s). " +
                                    $"Cache preparation took {Environment.TickCount64 - planStartedAt}ms.");
                TM.Enqueue(() => BeginRestockChain());
                TM.Enqueue(() => AutoRetainerIPC.Suppress());
                TM.EnqueueBell();
                TM.DelayNext("BellInteracted", 200);

                foreach (var retainer in RetainerData)
                {
                    if (retainer.Value.Values.Any(x => requiredItems.Any(y => y.Value > 0 && y.Key == x.ItemId && x.Quantity > 0)))
                    {
                        TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(retainer.Key));
                        TM.DelayNext("WaitToSelectEntrust", 200);
                        TM.Enqueue(() => RetainerHandlers.SelectEntrustItems());
                        TM.DelayNext("EntrustSelected", 200);
                        foreach (var item in requiredItems)
                        {
                            if (retainer.Value.Values.Any(x => x.ItemId == item.Key && x.Quantity > 0))
                            {
                                TM.DelayNext("SwitchItems", 200);
                                TM.Enqueue(() =>
                                {
                                    ExtractItem(requiredItems, item, retainer.Key);
                                });
                            }
                        }
                        TM.DelayNext("CloseRetainer", 200);
                        TM.Enqueue(() => RetainerHandlers.CloseAgentRetainer());
                        TM.DelayNext("ClickQuit", 200);
                        TM.Enqueue(() => RetainerHandlers.SelectQuit());
                    }
                }
                TM.DelayNext("CloseRetainerList", 200);
                TM.Enqueue(() => RetainerListHandlers.CloseRetainerList());
                TM.Enqueue(() => YesAlready.Unlock());
                TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
                TM.Enqueue(() => Svc.Framework.Update -= Tick);
            }
        }

        private static unsafe void Tick(IFramework framework)
        {
            ReportTaskTimeouts();

            // Watchdog. The restock chain ends with tasks that unlock YesAlready, un-suppress AutoRetainer and
            // detach this handler - but TaskManager.Abort() (fired explicitly on early completion, and by any
            // task enqueued with abortOnTimeout: true) clears the whole queue, so those trailing tasks can
            // simply never run. Before the suppress fix that was invisible because Suppress() was a no-op;
            // now it would leave AutoRetainer permanently suppressed with no message anywhere. Once the queue
            // is genuinely empty there is nothing left to wait for, so close everything out here instead.
            if (!TM.IsBusy)
            {
                Svc.Framework.Update -= Tick;
                if (AutoRetainerIPC.ReEnable || RestockStartedAt != 0)
                {
                    Svc.Log.Information($"[Artisan][Restock] Chain finished or was aborted after " +
                                        $"{(RestockStartedAt == 0 ? 0 : Environment.TickCount64 - RestockStartedAt)}ms; releasing YesAlready/AutoRetainer.");
                }
                RestockStartedAt = 0;
                YesAlready.Unlock();
                AutoRetainerIPC.Unsuppress();
                return;
            }

            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                // 🔴 這是每幀跑、零節流的按下點。Talk 按一次翻一頁、窗不消失,最後一頁那一下才是關閉,
                // 而關閉中的幾幀 IsVisible 仍為真 ⇒ 沒有守衛時同一扇關閉中的 Talk 每幀吃三個 ReceiveEvent。
                // 守衛記位址、15 幀逃生口(艦隊 Talk 政策):翻頁節奏每頁 +0.25s,關閉中的危險窗口 <10 幀不落在裡面。
                if (TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible
                    && AddonPressGuard.TryBeginPress("Talk", &addon->AtkUnitBase, escapeFrames: AddonPressGuard.RoutineRePressEscapeFrames))
                {
                    new AddonMaster.Talk((IntPtr)addon).Click();
                }
            }
        }

        /// <summary>上一幀在 <see cref="TM"/> 上看到的具名任務，<c>null</c> 代表當時沒有具名任務在跑。</summary>
        private static string? LastSeenTaskName = null;

        /// <summary>上一幀看到的那個具名任務的時限截止點（<see cref="Environment.TickCount64"/> 刻度），0 代表沒有。</summary>
        private static long LastSeenTaskAbortAt = 0;

        /// <summary>
        /// 把 ECommons 導進 Verbose 的逾時訊息，用 Information 在這裡補一則帶任務名的。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 由來：<c>TM.TimeoutSilently = true</c> 讓 <c>LegacyTaskManager</c> 的逾時訊息走 <c>PluginLog.Verbose</c>，
        /// 而 Verbose 是使用者的 log 等級唯一收不到的一級 ⇒ 僱員鏈的某一步逾時被跳過時，log 上一個字都沒有。
        /// 🔴 刻意<b>不</b>把 <c>TimeoutSilently</c> 改回 <c>false</c>：那會讓同一件事印兩遍。
        /// 🔴 也刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那份。
        /// </para>
        /// <para>
        /// 做法：本方法在 <see cref="Tick"/> 開頭執行，而 <see cref="TM"/> 自己的 <c>Framework.Update</c> 處理器
        /// 是在它的建構子裡掛上的（靜態欄位初始式，遠早於本檔把 <see cref="Tick"/> 掛上去）⇒ 每一幀都是
        /// TaskManager 先跑、本方法後跑，所以這裡看到的是「這一幀處理完之後」的狀態。
        /// 具名任務從「是它」變成「不是它」的那一幀，若當下已經越過它自己的時限，就是逾時被跳過。
        /// </para>
        /// <para>
        /// ⚠️ 兩個已知的界限，都是「少報」而不是「亂報」：
        /// ①<c>CurrentTaskName</c> 對<b>沒有取名字</b>的任務回 <c>null</c>（與「沒有任務」分不出來）⇒ 匿名任務不在覆蓋範圍內；
        /// ②連續兩個<b>同名</b>任務之間的交接看不出來。
        /// ⚠️ 反過來唯一的誤報形狀：一個任務剛好在越過時限的那一幀才回報成功（<c>result == true</c> 不看時限）。
        /// 窗口只有一幀，所以訊息寫成「拖過時限才結束」而不是斷言「逾時」。
        /// </para>
        /// </remarks>
        private static void ReportTaskTimeouts()
        {
            var name = TM.CurrentTaskName;
            if (name == LastSeenTaskName)
            {
                if (name != null) LastSeenTaskAbortAt = TM.AbortAt;
                return;
            }

            if (LastSeenTaskName != null && LastSeenTaskAbortAt != 0 && Environment.TickCount64 > LastSeenTaskAbortAt)
            {
                Svc.Log.Information($"[Artisan][Restock] 任務「{LastSeenTaskName}」拖過自己的時限才結束（超出 " +
                                    $"{Environment.TickCount64 - LastSeenTaskAbortAt}ms），最可能的原因是逾時被跳過，" +
                                    $"接下來的步驟會在錯的畫面上執行。ECommons 把逾時訊息導到 Verbose，所以在這裡補一則。");
            }

            LastSeenTaskName = name;
            LastSeenTaskAbortAt = name == null ? 0 : TM.AbortAt;
        }
        /// <summary>Tick count at which the current restock chain started, 0 when idle. Diagnostics only.</summary>
        private static long RestockStartedAt = 0;

        /// <summary>
        /// Attaches <see cref="Tick"/> exactly once. <c>Svc.Framework.Update += Tick</c> was previously enqueued
        /// at the head of every restock chain while the matching <c>-=</c> lived at the tail, so an aborted chain
        /// left a subscription behind and the next restock added a second one.
        /// </summary>
        private static void BeginRestockChain()
        {
            Svc.Framework.Update -= Tick;
            Svc.Framework.Update += Tick;
            RestockStartedAt = Environment.TickCount64;
            // Re-arms the direct retrieval path, so a stand-down caused by one run does not carry into the
            // next one - the retainer window not being open is a per-run condition, not a permanent one.
            RetainerDirectFetch.BeginRound();
        }

        /// <summary>
        /// Free slots across the four player bags, or -1 when the inventory cannot be read right now.
        /// <para/>
        /// ⚠️ Deliberately distinguishes "unreadable" from "zero": the containers are genuinely unreadable while
        /// zoning, and a plain 0 there would read as "bag is full" and silently switch the withdrawal back to
        /// exact quantities forever. Callers must treat -1 as "don't know" rather than as a small number.
        /// </summary>
        private static unsafe int GetFreeInventorySlots()
        {
            if (!Svc.ClientState.IsLoggedIn) return -1;
            if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return -1;

            var mgr = InventoryManager.Instance();
            if (mgr == null) return -1;

            InventoryType[] bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
            var slots = 0;
            foreach (var bag in bags)
            {
                var inv = mgr->GetInventoryContainer(bag);
                if (inv == null || inv->Items == null || inv->Size <= 0) return -1;
                for (var i = 0; i < inv->Size; i++)
                    if (inv->GetInventorySlot(i)->ItemId == 0)
                        slots++;
            }
            return slots;
        }

        /// <summary>
        /// How many items to pull out of a retainer stack holding <paramref name="stackQuantity"/>
        /// when <paramref name="stillNeeded"/> are still wanted.
        /// <para/>
        /// Takes the whole stack rather than the exact amount, so a restock does not leave a 3-item remainder
        /// on the retainer that the next list has to come back for. Retrieving a full stack can need one more
        /// bag slot than a partial one would (a 999 stack landing on top of an existing partial stack splits),
        /// so this only does it while the bag is known to have room to spare; when the free-slot count is
        /// unknown (-1) or tight it falls back to the exact quantity, which is the old behaviour.
        /// <para/>
        /// The "room to spare" threshold is <see cref="Configuration.RestockFullStackFreeSlots"/>, which
        /// defaults to the 2 this used to hardcode.
        /// </summary>
        private static int WithdrawalQuantity(int stillNeeded, int stackQuantity, int freeSlots)
        {
            var exact = Math.Min(stillNeeded, stackQuantity);
            if (stackQuantity <= exact)
                return exact; // already the whole stack

            // Clamped to >= 1 on purpose: GetFreeInventorySlots() reports "don't know" as -1, and a config
            // file hand-edited to 0 or lower would make that unknown compare as "plenty of room".
            var needed = Math.Max(1, P.Config.RestockFullStackFreeSlots);
            return freeSlots >= needed ? stackQuantity : exact;
        }

        /// <summary>
        /// Pulls <paramref name="stillNeeded"/> of an item off the open retainer with AutoRetainer's retrieve
        /// command, and reports how many actually landed in the player's bags.
        /// <para/>
        /// Runs as a single polling task rather than the enqueue-a-step-per-item recursion the window-driving
        /// path uses: there is no context menu to open and no quantity dialog to answer, so the only thing to
        /// wait for is the item arriving. The task's own time limit is only a backstop - the step function
        /// enforces its own deadlines so that it can hand back to the UI path instead of dying on a
        /// TimeoutException.
        /// </summary>
        /// <param name="onFinished">Given the number that arrived, and whether the UI path still has to run
        /// for the remainder.</param>
        private static void EnqueueDirectExtract(uint itemId, bool hqOnly, int stillNeeded, Action<int, bool> onFinished)
        {
            var progress = new RetainerDirectFetch.Progress(itemId, hqOnly, stillNeeded);
            // 🔴 Typed explicitly rather than written inline. EnqueueImmediate is overloaded on both Action
            // and Func<bool?>, and a lambda whose body is just a bool-returning call fits either; binding to
            // the Action overload would throw away the "not finished yet" result and run the polling step
            // exactly once, which looks like the retrieve simply not working.
            Func<bool?> poll = () => progress.Step();
            TM.EnqueueImmediate(() => { RetainerDirectFetch.ResetTracking(); return true; }, "DirectExtractReset");
            TM.EnqueueImmediate(poll, 90000, "DirectExtract");
            TM.EnqueueImmediate(() =>
            {
                onFinished(progress.Gained, progress.FallBackToUi);
                return true;
            }, "DirectExtractFinish");
        }

        private static bool ExtractItem(Dictionary<int, int> requiredItems, KeyValuePair<int, int> item, ulong key)
        {
            if (requiredItems[item.Key] != 0)
            {
                _InventoryChanged = false;
                if (RetainerDirectFetch.Available)
                {
                    // The retainer cache is what decides HQ-vs-any here, exactly as below; it is refreshed
                    // first because the direct path is fast enough that a stale cache would be the slowest
                    // part of it.
                    TM.EnqueueImmediate(() => RefreshRetainerItem((uint)item.Key, key));
                    TM.EnqueueImmediate(() =>
                    {
                        var wanted = requiredItems[item.Key];
                        if (wanted <= 0) return true;
                        var wantHQ = WantsHQFromRetainer(key, (uint)item.Key);
                        EnqueueDirectExtract((uint)item.Key, wantHQ, wanted, (gained, fallBack) =>
                        {
                            requiredItems[item.Key] = Math.Max(0, wanted - gained);
                            // Only the window-driving path is re-entered on fallback, and only when something
                            // is still missing - re-entering the direct path would just repeat the failure.
                            if (fallBack && requiredItems[item.Key] > 0)
                                TM.EnqueueImmediate(() => { ExtractItemViaWindow(requiredItems, item, key); }, "FallbackExtract");
                        });
                        return true;
                    }, "DirectExtractEntry");
                    return true;
                }

                return ExtractItemViaWindow(requiredItems, item, key);
            }

            return true;
        }

        /// <summary>The original retainer-window path: open the stack's context menu, pick "Retrieve from
        /// Retainer", type a quantity, repeat. Kept whole as the fallback for everything the command path
        /// cannot or should not do.</summary>
        private static bool ExtractItemViaWindow(Dictionary<int, int> requiredItems, KeyValuePair<int, int> item, ulong key)
        {
            if (requiredItems[item.Key] != 0)
            {
                _InventoryChanged = false;
                // Was GetRetainerItemCount(), which walks all ten retainers and fires 8-15 AllaganTools IPC
                // calls per retainer - up to ~150 round trips - on every single recursion, even though the only
                // value read afterwards is RetainerData[key] for the retainer whose window is open right now.
                TM.EnqueueImmediate(() => RefreshRetainerItem((uint)item.Key, key));
                bool lookingForHQ = WantsHQFromRetainer(key, (uint)item.Key);
                Svc.Log.Debug($"HQ?: {lookingForHQ}");
                TM.DelayNextImmediate("WaitOnRetainerInventory", 500);
                TM.EnqueueImmediate(() => RetainerHandlers.OpenItemContextMenu((uint)item.Key, lookingForHQ, out firstFoundQuantity), 300);
                TM.DelayNextImmediate("WaitOnNumericPopup", 200);
                TM.EnqueueImmediate(() =>
                {
                    var stillNeeded = requiredItems[item.Key];
                    if (Math.Min(stillNeeded, (int)firstFoundQuantity) == 0) return true;

                    var freeSlots = GetFreeInventorySlots();
                    var value = WithdrawalQuantity(stillNeeded, (int)firstFoundQuantity, freeSlots);
                    Svc.Log.Information($"[Artisan][Restock] item {item.Key}: withdrawing {value} of the {firstFoundQuantity} in this stack " +
                                        $"(still needed {stillNeeded}, free bag slots {(freeSlots < 0 ? "unknown" : freeSlots.ToString())}).");

                    if (firstFoundQuantity == 1) { requiredItems[item.Key] = Math.Max(0, stillNeeded - (int)firstFoundQuantity); return true; }
                    if (RetainerHandlers.InputNumericValue(value))
                    {
                        // Clamp: taking the whole stack can exceed what was still needed, and a negative
                        // remainder would never compare equal to 0 and would keep the recursion going forever.
                        requiredItems[item.Key] = Math.Max(0, stillNeeded - value);
                        TM.EnqueueImmediate(() => _InventoryChanged);
                        TM.EnqueueImmediate(() =>
                        {
                            // Stays on the window path deliberately: this recursion is only reached from the
                            // fallback, and bouncing back into the direct path would repeat whatever made it
                            // give up in the first place.
                            ExtractItemViaWindow(requiredItems, item, key);
                        }, "RecursiveExtract");
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }, 1000);
            }

            return true;
        }

        internal static IGameObject? GetReachableRetainerBell()
        {
            foreach (var x in Svc.Objects)
            {
                if ((x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(BellName, "リテイナーベル"))
                {
                    if (Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < GetValidInteractionDistance(x) && x.IsTargetable())
                    {
                        return x;
                    }
                }
            }
            return null;
        }

        internal static float GetValidInteractionDistance(IGameObject bell)
        {
            if (bell.ObjectKind == ObjectKind.Housing)
            {
                return 6.5f;
            }
            else if (Inns.List.Contains(Svc.ClientState.TerritoryType))
            {
                return 4.75f;
            }
            else
            {
                return 4.6f;
            }
        }

        internal static string BellName
        {
            get => Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ToString();
        }

        public unsafe static bool IsTargetable(this IGameObject o)
        {
            return o.Struct()->GetIsTargetable();
        }

        public unsafe static FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* Struct(this IGameObject o)
        {
            return (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address;
        }
    }
}
