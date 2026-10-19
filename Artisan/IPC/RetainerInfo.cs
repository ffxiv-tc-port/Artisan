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

        // 逾時訊息一定要留在 ECommons 自己的 Warning：本檔多數任務沒有取名字，而那一行是
        //    唯一叫得出它們名字的（GetTaskName 對匿名任務退回 lambda 的 <外層方法>b__N）。
        // 🔴 刻意不設 TimeoutSilently = true —— 那會把它推進使用者的 log 等級收不到的 Verbose。
        // 任一步逾時就清掉整條鏈（舊版 TaskManager 預設只丟那一步、後面照跑，會對著錯的視窗按）；
        //    收尾（還鎖、還原 AutoRetainer、告訴使用者）交給 Tick 看門狗。「找不到就跳過」的步驟個別傳 false。
        public static TaskManager TM = new TaskManager() { AbortOnTimeout = true };
        internal static bool GenericThrottle => EzThrottler.Throttle("RetainerInfoThrottler", 100);
        internal static void RethrottleGeneric(int num) => EzThrottler.Throttle("RetainerInfoThrottler", num, true);
        internal static void RethrottleGeneric() => EzThrottler.Throttle("RetainerInfoThrottler", 100, true);
        internal static Tasks.RetainerManager retainerManager = new(Svc.SigScanner);

        public static bool AToolsInstalled => CurrentAToolsState().Installed;

        public static bool AToolsEnabled => CurrentAToolsState().Enabled;

        private static bool QueryAToolsInstalled()
            => Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName is "Allagan Tools" or "InventoryTools");

        private static bool QueryAToolsEnabled(bool installed)
            => installed && (DalamudReflector.TryGetDalamudPlugin("Allagan Tools", out var at, false, true) || DalamudReflector.TryGetDalamudPlugin("InventoryTools", out var it, false, true)) && _IsInitialized != null && _IsInitialized.InvokeFunc();

        // 一次查詢＝走訪插件清單＋最多兩次反射找插件＋一次 IPC，而繪製路徑每幀、每列都在讀 ⇒ 快取，最多每秒重查一次。
        // 讀取端橫跨框架、繪製與執行緒池（LoadCache、從僱員補貨、材料表背景建構）：快照不可變、整份換掉。
        // 🔴 只有框架執行緒會查：查詢走 DalamudReflector.TryGetDalamudPlugin（寫 ECommons 的裸 Dictionary）與 AllaganTools IPC。
        // 其他執行緒一律回手上的快照（還沒有就當作沒啟用），過期時留下請求、由框架執行緒下一幀代查，不等待。
        // 「停用 Allagan Tools 整合」設定刻意不進快取：ATools 每次照讀，勾選當下就生效。
        private sealed class AToolsSnapshot(bool installed, bool enabled)
        {
            public readonly bool Installed = installed;
            public readonly bool Enabled = enabled;
        }

        private static AToolsSnapshot? aToolsSnapshot;
        private static long aToolsNextCheck;
        private static int aToolsRefreshRequested;
        private static readonly AToolsSnapshot NotYetQueried = new(false, false);

        private static AToolsSnapshot CurrentAToolsState()
        {
            var now = Environment.TickCount64;
            var next = Interlocked.Read(ref aToolsNextCheck);
            var snapshot = Volatile.Read(ref aToolsSnapshot);
            if (snapshot != null && now < next)
                return snapshot;
            if (Svc.Framework is not { IsInFrameworkUpdateThread: true })
            {
                Volatile.Write(ref aToolsRefreshRequested, 1);
                return snapshot ?? NotYetQueried;
            }
            if (Interlocked.CompareExchange(ref aToolsNextCheck, now + 1000, next) != next && snapshot != null)
                return snapshot;

            var installed = QueryAToolsInstalled();
            snapshot = new AToolsSnapshot(installed, QueryAToolsEnabled(installed));
            Volatile.Write(ref aToolsSnapshot, snapshot);
            return snapshot;
        }

        /// <summary>由 <c>Artisan.OnFrameworkUpdate</c> 每幀呼叫：其他執行緒讀到過期快照時留下的請求，在這裡代查一次。</summary>
        internal static void RefreshAToolsIfRequested()
        {
            if (Volatile.Read(ref aToolsRefreshRequested) != 0 && Interlocked.Exchange(ref aToolsRefreshRequested, 0) != 0)
                CurrentAToolsState();
        }

        /// <summary>下一次在框架執行緒上讀取就重查。可從任何執行緒呼叫（AllaganTools 的 Initialized 廣播跑在對方的執行緒上）。</summary>
        private static void InvalidateAToolsState() => Interlocked.Exchange(ref aToolsNextCheck, 0);

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
            // 換角＝登出再登入、插件不重載：「只顯示能做的」是上一個角色的背包算的。
            CraftingListUI.CraftableItems.Clear();
            CraftingListUI.CraftableItemsStale = true;
        }

        private static void SetupIPC(bool obj)
        {

            _OnRetainerChanged = Svc.PluginInterface.GetIpcSubscriber<ulong?, bool>("AllaganTools.RetainerChanged");
            _OnItemAdded = Svc.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemAdded");
            _OnItemRemoved = Svc.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemRemoved");

            // 🔴 第 3 個參數（inventoryType）在 AllaganTools 端宣告的是 int，不是 uint
            //    （InventoryTools/IPC/IPCService.cs 的 ItemCount(uint, ulong, int)）。
            //    宣告成 uint 不會炸：Dalamud 在型別不同時會用 Newtonsoft 把引數 JSON 來回轉一次，
            //    但那是**每次呼叫每個引數**都要付的代價，而這兩支是 GetRetainerInventoryItem 的熱路徑；
            //    ⚠️ 而且轉換只對 <= int.MaxValue 的值成立，超過會擲 IpcTypeMismatchError。
            _ItemCount = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
            _ItemCountHQ = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCountHQ");
            _OnItemAdded.Subscribe(OnItemAdded);
            _OnItemRemoved.Subscribe(OnItemRemoved);

            // Init 剛換上 _IsInitialized，或 AllaganTools 剛啟動完成：快取裡的「沒啟用」可能已經過期。
            InvalidateAToolsState();
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
            CraftingListUI.CraftableItemsStale = false;

            if (P.Config.ShowOnlyCraftable || onLoad)
            {
                // 🔑 把「讀數字」與「比大小」切開:
                //    在主執行緒上分批讀成純量快照(每批一次往返),之後整個比較迴圈留在背景、
                //    一次原生記憶體都不碰。
                var prefetchRetainers = ATools && P.Config.ShowOnlyCraftableRetainers || onLoad;

                // 先確保跳到背景執行緒:下面列材料 id 是純資料表走訪,
                // 而快照本身要在「不是主執行緒」時才會分批往返 —— 在主執行緒上呼叫的話
                // 閘門會就地執行,原生讀取就全擠進同一格畫面。
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

        /// <summary>
        /// 給 TaskManager 的 <c>Enqueue(Action)</c> 用：同步部分照舊在呼叫端跑到第一個 await 才返回，
        /// 例外記一行 Warning、留在這個 Task 裡；不可寫成 async lambda（那會綁成 async void，例外直接結束遊戲）。
        /// </summary>
        public static async Task LoadCacheLogged()
        {
            try
            {
                await LoadCache();
            }
            catch (Exception e)
            {
                Svc.Log.Warning($"[Artisan][僱員快取] 重建「只顯示有材料的配方」快取失敗：{e}");
            }
        }

        // 除錯記錄節流：僱員背包的每一個 add/remove 事件都會清一次快取，逐筆印會在
        // 僱員視窗開著時把整份 log 洗掉。
        // 這裡只計數，等到某次清除「真的把快取裡的東西清掉了」才印，且每秒最多一行；
        // 被壓下來的事件數會帶在那一行裡，所以資訊不會遺失。
        private static int _cacheClearAdds;
        private static int _cacheClearRemoves;
        private static int _cacheClearEffective;
        private static long _cacheClearNextLogTick;

        // 🔴 AllaganTools 的 ItemAdded／ItemRemoved 回呼跑在「對方外掛的執行緒」上，而 Dalamud 的
        //    CallGateChannel.SendMessage 對訂閱者是裸 DynamicInvoke、完全不攔例外 ⇒ 從這裡擲出去的例外
        //    會傳回 InventoryTools 自己的事件迴圈，把它後面的訂閱者一起打斷。
        //    🔑 所以回呼只做兩件事：Interlocked 計數 + try/catch 兜底；原生讀取、RetainerData 走訪與 ClearCache
        //    都在 DrainInventoryEvents()，由 Artisan.OnFrameworkUpdate 在框架執行緒排乾。
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
        /// ⚠️ <c>OccupiedSummoningBell</c> 是在排乾的那一刻取樣，
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
            // 補貨鏈做到一半被卸載：TextAdvance 的暫停名單沒有持有者存活檢查，不在這裡還就一直停著。
            Svc.Framework.Update -= Tick;
            ECommons.GenericHelpers.Safe(ReleaseClickLocks);
            ECommons.GenericHelpers.Safe(AutoRetainerIPC.Unsuppress);
        }

        /// <summary>
        /// 僱員背包快取。
        /// <para/>
        /// 🔴 為什麼是 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是 <c>Dictionary</c>：
        /// 這份資料同時被三條執行緒碰 ——
        /// ①框架／繪製執行緒（材料表、<c>Framework.Update</c> 上的排乾與 TaskManager）；
        /// ②執行緒池：<c>CraftingListUI.cs</c> 與 <c>ListEditor.cs</c> 兩個「從僱員取回」按鈕都是
        /// <c>Task.Run(() =&gt; RestockFromRetainers(...))</c>，而它會呼叫 <c>GetRetainerItemCount</c>（寫）
        /// 並走訪整份快取（讀）。
        /// <para/>
        /// 裸 <c>Dictionary</c> 在這種形狀下的失敗形式<b>不是「拿到舊值」而是字典本身壞掉</b>，
        /// 而走訪中的並行改動會擲 <c>InvalidOperationException</c> —— 那個例外會被
        /// <c>GetRetainerItemCount</c> 自己的 <c>catch</c> 吞成「回 0」，使用者看到的是僱員持有量
        /// 莫名其妙變成 0，log 上一片安靜。
        /// <para/>
        /// ⚠️ <c>ConcurrentDictionary</c> 的 LINQ 走訪是<b>弱一致</b>的：不會擲例外，但
        /// 讀到的可能是走訪期間的混合快照；對這裡的用途（估算持有量、決定要拜訪哪些僱員）夠用。
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

            // Resolved once instead of rebuilding the same filtered array inside all ten iterations below.
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
        /// 順序是契約:「沒登入就回 0」必須發生在快取快捷路徑之前。
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

        /// <summary><b>只能在遊戲主執行緒上呼叫</b>，由 <see cref="GetRetainerItemCount"/> 那一層的閘門保證。</summary>
        private static CountPrologue ReadCountPrologue(uint ItemId, bool tryCache, bool hqOnly)
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial])
                return CountPrologue.NotAvailable;

            if (tryCache && TrySumCachedRetainerItem(ItemId, hqOnly, out var cached))
                return CountPrologue.FromCache(cached);

            var resolved = ReadRetainerRosterCore();
            return resolved is null ? CountPrologue.NotAvailable : CountPrologue.FromRoster(resolved);
        }

        // ConcurrentDictionary 沒有結構列舉器，foreach 一次配置一個，而快取命中路徑在清單視窗開著時每幀每列都走。
        // 主執行緒上重用同一個：只有那一條執行緒碰這兩個欄位，用完一律 Reset（下次從頭走、也不留住舊的桶陣列）。
        // 其他執行緒照舊每次新建。
        private static IEnumerator<KeyValuePair<ulong, ConcurrentDictionary<uint, ItemInfo>>>? frameworkThreadWalker;
        private static ConcurrentDictionary<ulong, ConcurrentDictionary<uint, ItemInfo>>? frameworkThreadWalkerSource;

        /// <summary>
        /// 快取裡所有僱員身上這個道具的合計（<paramref name="hqOnly"/> 時只算 HQ）；沒有任何僱員快取過它時回 <c>false</c>。
        /// 內層用 <c>TryGetValue</c>：鍵一律等於 <see cref="ItemInfo.ItemId"/>（本檔每個寫入點都用同一個 id 寫鍵與值）。
        /// </summary>
        private static bool TrySumCachedRetainerItem(uint ItemId, bool hqOnly, out int total)
        {
            var data = RetainerData;
            var walker = Svc.Framework.IsInFrameworkUpdateThread ? FrameworkThreadWalker(data) : data.GetEnumerator();

            var found = false;
            long sum = 0;
            try
            {
                while (walker.MoveNext())
                {
                    if (walker.Current.Value.TryGetValue(ItemId, out var item))
                    {
                        found = true;
                        sum += hqOnly ? item.HQQuantity : item.Quantity;
                    }
                }
            }
            finally
            {
                walker.Reset();
            }

            total = (int)sum;
            return found;
        }

        private static IEnumerator<KeyValuePair<ulong, ConcurrentDictionary<uint, ItemInfo>>> FrameworkThreadWalker(
            ConcurrentDictionary<ulong, ConcurrentDictionary<uint, ItemInfo>> data)
        {
            if (frameworkThreadWalker is null || !ReferenceEquals(frameworkThreadWalkerSource, data))
            {
                frameworkThreadWalker = data.GetEnumerator();
                frameworkThreadWalkerSource = data;
            }
            return frameworkThreadWalker;
        }

        private static int SumCachedRetainerItem(uint ItemId, bool hqOnly)
        {
            TrySumCachedRetainerItem(ItemId, hqOnly, out var total);
            return total;
        }

        /// <summary>
        /// 這個道具在所有僱員身上的數量。
        /// </summary>
        /// <param name="roster">
        /// 呼叫端已經在主執行緒上解析好的僱員名冊(<see cref="ReadRetainerRoster"/>)。
        /// 不給的話這支自己解一次 —— 正確,但從背景執行緒呼叫時<b>每次要等一幀</b>。
        /// </param>
        /// <remarks>
        /// 🔴 前置判斷(可用性、快取、名冊)交回主執行緒做:
        /// <c>Svc.ClientState.IsLoggedIn</c>(→ <c>AgentLobby.Instance()-&gt;IsLoggedIn</c>)、
        /// <c>Svc.Condition[...]</c>、<c>Svc.PlayerState.ContentId</c>
        /// (→ <c>PlayerState.Instance()-&gt;ContentId</c>)與
        /// <c>RetainerManager.Instance()-&gt;GetRetainerBySortedIndex</c>／<c>retainer-&gt;Available</c>
        /// 全部是原生解參考,只能在遊戲主執行緒上讀。
        /// <para/>
        /// 🔴 <b>慢速的 AllaganTools IPC 迴圈刻意留在呼叫端的執行緒上</b> ——
        /// 每個僱員 8~15 次 IPC、整份清單好幾秒,搬到主執行緒上會把遊戲卡住。
        /// 它碰的 <c>RetainerData</c> 是 <c>ConcurrentDictionary</c>,本來就設計成跨執行緒共用。
        /// <para/>
        /// ⚠️ 呼叫端傳 <paramref name="roster"/> 時,可用性
        /// (登入中／非免費體驗)是「該輪解析名冊的那一刻」判斷的,不再逐個道具重判。
        /// 若掃描途中登出,
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
                        // 狀態走參數、lambda 不擷取任何東西 ⇒ 在主執行緒上就地執行時整段零配置（快取命中是每幀每列的常態）。
                        var prologue = IpcFrameworkGate.Get(PrologueEndpoint, (ItemId: ItemId, TryCache: tryCache, HqOnly: hqOnly),
                            static a => ReadCountPrologue(a.ItemId, a.TryCache, a.HqOnly), CountPrologue.NotAvailable);

                        if (prologue.Unavailable) return 0;
                        if (prologue.Cached is int cached) return cached;
                        roster = prologue.Roster!;
                    }
                    else if (tryCache && TrySumCachedRetainerItem(ItemId, hqOnly, out var cachedTotal))
                    {
                        return cachedTotal;
                    }

                    foreach (var retainerId in roster.RetainerIds)
                    {
                        // 用 GetOrAdd 而不是「先 TryAdd 再用索引子取回」：中間若有人 Clear() 就是 KeyNotFoundException。
                        var ret = RetainerData.GetOrAdd(retainerId, static _ => new ConcurrentDictionary<uint, ItemInfo>());
                        // 走到這裡＝快取沒命中或呼叫端不要快取：兩個數量一起重讀，不留下新的 Quantity 配舊的 HQQuantity。
                        ret[ItemId] = new ItemInfo(ItemId,
                            GetRetainerInventoryItem(ItemId, retainerId),
                            GetRetainerInventoryItem(ItemId, retainerId, true));
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
        /// </remarks>
        public static void RestockFromRetainers(uint ItemId, int howManyToGet)
            => IpcFrameworkGate.Run("RetainerInfo.RestockFromRetainers(單品)", () => RestockSingleCore(ItemId, howManyToGet));

        /// <summary><b>只能在框架執行緒上呼叫</b>，由上面那層閘門保證。</summary>
        private static void RestockSingleCore(uint ItemId, int howManyToGet)
        {
            if (RetainerData.SelectMany(x => x.Value).Any(x => x.Value.ItemId == ItemId && x.Value.Quantity > 0))
            {
                Svc.Log.Information($"[Artisan][Restock] Single-item restock starting: item {ItemId} x{howManyToGet}.");
                var plannedTerritory = Svc.ClientState.TerritoryType;
                TM.Enqueue(() => BeginRestockChain(plannedTerritory));
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
                            TM.EnqueueImmediate(CloseRetainerListAtEnd);
                            TM.EnqueueImmediate(() => ReleaseClickLocks());
                            TM.EnqueueImmediate(() => AutoRetainerIPC.Unsuppress());
                            TM.EnqueueImmediate(() => Svc.Framework.Update -= Tick);
                            TM.EnqueueImmediate(() => TM.Abort());
                        }
                    });
                }

                TM.DelayNext("CloseRetainerList", 200);
                TM.Enqueue(CloseRetainerListAtEnd);
                TM.Enqueue(() => ReleaseClickLocks());
                TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
                TM.Enqueue(() => Svc.Framework.Update -= Tick);
            }
        }

        /// <summary>
        /// 依快取判斷「這個僱員身上有沒有這個物品的 HQ 版本」。
        /// <para/>
        /// 🔴 用 <c>TryGetValue</c> 不用 <c>RetainerData[retainerId]</c> 索引子 —— 快取在
        /// 判斷的那一刻剛好被清空（登出、<c>LoadCache</c>、AllaganTools 事件排乾）就是
        /// <c>KeyNotFoundException</c>，而呼叫點全都在取回流程的中途。
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
                // 這個僱員身上找不到（快取過期）＝跳過這個道具，不中止整條鏈。
                TM.EnqueueImmediate(() => RetainerHandlers.OpenItemContextMenu(ItemId, lookingForHQ, out firstFoundQuantity), 300, false);
                TM.DelayNextImmediate("WaitOnNumericPopup", 200);
                // 數量視窗沒出現時這個任務每幀重跑到逾時，說明只寫第一次。
                var logged = false;
                TM.EnqueueImmediate(() =>
                {
                    if (Math.Min(howManyToGet, (int)firstFoundQuantity) == 0) return true;

                    var freeSlots = GetFreeInventorySlots();
                    var value = WithdrawalQuantity(howManyToGet, (int)firstFoundQuantity, freeSlots);
                    if (!logged)
                    {
                        logged = true;
                        Svc.Log.Information($"[Artisan][Restock] item {ItemId}: withdrawing {value} of the {firstFoundQuantity} in this stack " +
                                            $"(still needed {howManyToGet}, free bag slots {(freeSlots < 0 ? "unknown" : freeSlots.ToString())}).");
                    }
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
                        // Clamp for the same reason as ExtractItemViaWindow: a whole-stack withdrawal can overshoot,
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
            // retainers over AllaganTools IPC before any retainer window is even opened, so a long list
            // spends seconds here with nothing visible happening.
            // Timed at Information level: a diagnostic users are asked to report.
            var planStartedAt = Environment.TickCount64;

            Svc.Log.Debug($"Making material list");

            materialList = list.ListMaterials();

            Svc.Log.Debug($"Creating Fetch List");

            // 🔑 背包數量「一次讀完」而不是「每個材料往返一次」:
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

            Autocraft.DarkMatterRestock.AddToRestock(list, requiredItems, roster);

            // 🔴 這一行是「背景」與「主執行緒」的分界。
            //    上面那段（列材料、比對背包、GetRetainerItemCount）每個材料都要走十個僱員、每個僱員 8~15 次
            //    AllaganTools IPC，長清單會跑上好幾秒，所以留在背景（兩顆按鈕用 Task.Run 包起來的理由）。
            //    下面那段只是把任務推進 TaskManager 的裸 List，不阻塞，
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
                var plannedTerritory = Svc.ClientState.TerritoryType;
                TM.Enqueue(() => BeginRestockChain(plannedTerritory));
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
                TM.Enqueue(CloseRetainerListAtEnd);
                TM.Enqueue(() => ReleaseClickLocks());
                TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
                TM.Enqueue(() => Svc.Framework.Update -= Tick);
            }
        }

        private static unsafe void Tick(IFramework framework)
        {
            // Watchdog. The restock chain ends with tasks that unlock YesAlready, un-suppress AutoRetainer and
            // detach this handler - but TaskManager.Abort() (fired explicitly on early completion, and by any
            // task enqueued with abortOnTimeout: true) clears the whole queue, so those trailing tasks can
            // simply never run, leaving AutoRetainer permanently suppressed with no message anywhere. Once the
            // queue is genuinely empty there is nothing left to wait for, so close everything out here instead.
            if (!TM.IsBusy)
            {
                Svc.Framework.Update -= Tick;
                if (AutoRetainerIPC.ReEnable || RestockStartedAt != 0)
                {
                    Svc.Log.Information($"[Artisan][Restock] Chain finished or was aborted after " +
                                        $"{(RestockStartedAt == 0 ? 0 : Environment.TickCount64 - RestockStartedAt)}ms; releasing YesAlready/AutoRetainer.");
                }
                // 沒走到收尾、也沒人說為什麼停 ⇒ 是某一步逾時清掉了整條鏈（步驟名在 ECommons 那行 took too long）。
                if (RestockStartedAt != 0 && !restockWatchdog.IsEnding && restockStopReason == null)
                {
                    Svc.Log.Information($"[Artisan][Restock] 中止從僱員補貨：有一個步驟在時限內沒有完成（補貨開始後 {Environment.TickCount64 - RestockStartedAt}ms）。剩下的步驟不再執行。");
                    Svc.Chat.PrintError("已中止從僱員補貨：有一個步驟在時限內沒有完成，僱員視窗可能還開著。", "Artisan");
                }
                restockStopReason = null;
                RestockStartedAt = 0;
                ReleaseClickLocks();
                AutoRetainerIPC.Unsuppress();
                return;
            }

            YesAlready.Reassert();
            TextAdvanceLock.Reassert();

            var stop = restockWatchdog.Check(Environment.TickCount64,
                Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                Svc.ClientState.TerritoryType, Svc.Condition[ConditionFlag.OccupiedSummoningBell]);
            if (stop != RestockWatchdog.Stop.None)
            {
                var reason = RestockWatchdog.Describe(stop);
                Svc.Log.Information($"[Artisan][Restock] 中止從僱員補貨：{reason}（補貨開始後 {Environment.TickCount64 - RestockStartedAt}ms）。剩下的步驟不再執行。");
                Svc.Chat.PrintError($"已中止從僱員補貨：{reason}。", "Artisan");
                restockStopReason = reason;
                TM.Abort();
                return;
            }

            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                // 🔴 這是每幀跑、零節流的按下點。Talk 按一次翻一頁、窗不消失,最後一頁那一下才是關閉,
                // 而關閉中的幾幀 IsVisible 仍為真 ⇒ 沒有守衛時同一扇關閉中的 Talk 每幀吃三個 ReceiveEvent。
                // 守衛記位址、15 幀逃生口(艦隊 Talk 政策):翻頁節奏每頁 +0.25s,關閉中的危險窗口 <10 幀不落在裡面。
                if (TextAdvanceLock.MayClickTalk
                    && TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible
                    && AddonPressGuard.TryBeginPress("Talk", &addon->AtkUnitBase, escapeFrames: AddonPressGuard.RoutineRePressEscapeFrames))
                {
                    new AddonMaster.Talk((IntPtr)addon).Click();
                }
            }
        }

        /// <summary>Tick count at which the current restock chain started, 0 when idle. Diagnostics only.</summary>
        private static long RestockStartedAt = 0;

        private static readonly RestockWatchdog restockWatchdog = new();

        /// <summary>有人明確叫停時的理由；null＝沒人叫停（鏈若沒走到收尾就是逾時）。</summary>
        private static string? restockStopReason;

        /// <summary>
        /// 從外面中止從僱員補貨的入口（按鈕、停止製作）；只在框架執行緒呼叫。
        /// 只清佇列，還鎖與還原 AutoRetainer 由 <see cref="Tick"/> 在佇列空的那一幀做。
        /// </summary>
        internal static void AbortRestock(string reason)
        {
            if (RestockStartedAt != 0)
            {
                restockStopReason ??= reason;
                Svc.Log.Information($"[Artisan][Restock] 停止從僱員補貨：{reason}。");
            }
            TM.Abort();
        }

        /// <summary>
        /// Attaches <see cref="Tick"/> exactly once: the <c>-=</c> before the <c>+=</c> means an aborted chain
        /// (whose trailing detach never ran) cannot leave a second subscription behind.
        /// </summary>
        private static void BeginRestockChain(ushort plannedTerritory)
        {
            Svc.Framework.Update -= Tick;
            Svc.Framework.Update += Tick;
            RestockStartedAt = Environment.TickCount64;
            restockStopReason = null;
            restockWatchdog.Begin(RestockStartedAt, plannedTerritory);
            // Re-arms the direct retrieval path, so a stand-down caused by one run does not carry into the
            // next one - the retainer window not being open is a per-run condition, not a permanent one.
            RetainerDirectFetch.BeginRound();
        }

        /// <summary>解開補貨鏈開頭對 YesAlready／TextAdvance 上的鎖；沒上鎖時什麼都不做。只在框架執行緒呼叫。</summary>
        private static void ReleaseClickLocks()
        {
            YesAlready.Unlock();
            TextAdvanceLock.Unlock();
        }

        private static bool? CloseRetainerListAtEnd()
        {
            restockWatchdog.MarkEnding();
            return RetainerListHandlers.CloseRetainerList();
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
        /// unknown (-1) or tight it falls back to the exact quantity.
        /// <para/>
        /// The "room to spare" threshold is <see cref="Configuration.RestockFullStackFreeSlots"/> (default 2).
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
                // RefreshRetainerItem, not GetRetainerItemCount(): the latter walks all ten retainers and fires 8-15
                // AllaganTools IPC calls per retainer on every single recursion, even though the only
                // value read afterwards is RetainerData[key] for the retainer whose window is open right now.
                TM.EnqueueImmediate(() => RefreshRetainerItem((uint)item.Key, key));
                bool lookingForHQ = WantsHQFromRetainer(key, (uint)item.Key);
                Svc.Log.Debug($"HQ?: {lookingForHQ}");
                TM.DelayNextImmediate("WaitOnRetainerInventory", 500);
                // 這個僱員身上找不到（快取過期）＝跳過這個道具，不中止整條鏈。
                TM.EnqueueImmediate(() => RetainerHandlers.OpenItemContextMenu((uint)item.Key, lookingForHQ, out firstFoundQuantity), 300, false);
                TM.DelayNextImmediate("WaitOnNumericPopup", 200);
                // 數量視窗沒出現時這個任務每幀重跑到逾時，說明只寫第一次。
                var logged = false;
                TM.EnqueueImmediate(() =>
                {
                    var stillNeeded = requiredItems[item.Key];
                    if (Math.Min(stillNeeded, (int)firstFoundQuantity) == 0) return true;

                    var freeSlots = GetFreeInventorySlots();
                    var value = WithdrawalQuantity(stillNeeded, (int)firstFoundQuantity, freeSlots);
                    if (!logged)
                    {
                        logged = true;
                        Svc.Log.Information($"[Artisan][Restock] item {item.Key}: withdrawing {value} of the {firstFoundQuantity} in this stack " +
                                            $"(still needed {stillNeeded}, free bag slots {(freeSlots < 0 ? "unknown" : freeSlots.ToString())}).");
                    }

                    if (firstFoundQuantity == 1) { requiredItems[item.Key] = Math.Max(0, stillNeeded - (int)firstFoundQuantity); return true; }
                    if (RetainerHandlers.InputNumericValue(value))
                    {
                        // Clamp: taking the whole stack can exceed what was still needed, and a negative
                        // remainder would never compare equal to 0 and would keep the recursion going forever.
                        requiredItems[item.Key] = Math.Max(0, stillNeeded - value);
                        // 等不到只代表快取沒刷新；下一輪開右鍵選單讀的是實際容器，所以不中止。
                        TM.EnqueueImmediate(() => _InventoryChanged, false);
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
