using Artisan.CraftingLists;
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
        private static ICallGateSubscriber<uint, ulong, uint, uint>? _ItemCount;
        private static ICallGateSubscriber<uint, ulong, uint, uint>? _ItemCountHQ;
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

            _ItemCount = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, uint, uint>("AllaganTools.ItemCount");
            _ItemCountHQ = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, uint, uint>("AllaganTools.ItemCountHQ");
            _OnItemAdded.Subscribe(OnItemAdded);
            _OnItemRemoved.Subscribe(OnItemRemoved);
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
                foreach (var recipe in LuminaSheets.RecipeSheet.Values)
                {
                    if (ATools && P.Config.ShowOnlyCraftableRetainers || onLoad)
                        await Task.Run(() => Safe(() => CraftingListUI.CheckForIngredients(recipe, false, true)));
                    else
                        await Task.Run(() => Safe(() => CraftingListUI.CheckForIngredients(recipe, false, false)));
                }
            }

            ClearCache(null);
            CacheBuilt = true;
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

        private static bool HasCachedRetainerData()
        {
            foreach (var retainer in RetainerData)
            {
                if (retainer.Value.Count > 0)
                    return true;
            }
            return false;
        }

        private static void NoteCacheCleared(bool added, bool hadCachedData)
        {
            if (added)
                _cacheClearAdds++;
            else
                _cacheClearRemoves++;

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
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                NoteCacheCleared(true, HasCachedRetainerData());
                ClearCache(null);
                _InventoryChanged = true;
            }
        }

        private static void OnItemRemoved((uint, InventoryItem.ItemFlags, ulong, uint) tuple)
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                NoteCacheCleared(false, HasCachedRetainerData());
                ClearCache(null);
                _InventoryChanged = true;
            }
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

        public static Dictionary<ulong, Dictionary<uint, ItemInfo>> RetainerData = new Dictionary<ulong, Dictionary<uint, ItemInfo>>();
        public class ItemInfo
        {
            public uint ItemId { get; set; }

            public uint Quantity { get; set; }

            public uint HQQuantity { get; set; }

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
                            _ItemCount.InvokeFunc(ItemId, retainerId, (uint)InventoryType.RetainerCrystals);
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
                if (!RetainerData.TryGetValue(retainerId, out var ret))
                {
                    ret = new Dictionary<uint, ItemInfo>();
                    RetainerData[retainerId] = ret;
                }

                var quantity = GetRetainerInventoryItem(ItemId, retainerId);
                var hq = GetRetainerInventoryItem(ItemId, retainerId, true);
                if (ret.TryGetValue(ItemId, out var info))
                {
                    info.ItemId = ItemId;
                    info.Quantity = quantity;
                    info.HQQuantity = hq;
                }
                else
                {
                    ret[ItemId] = new ItemInfo(ItemId, quantity, hq);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Artisan][Restock] Could not refresh item {ItemId} on retainer {retainerId}: {ex.Message}");
            }
        }

        public static unsafe int GetRetainerItemCount(uint ItemId, bool tryCache = true, bool hqOnly = false)
        {

            if (ATools)
            {
                if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial]) return 0;

                try
                {
                    if (tryCache)
                    {
                        if (RetainerData.SelectMany(x => x.Value).Any(x => x.Key == ItemId))
                        {
                            if (hqOnly)
                            {
                                return (int)RetainerData.Values.SelectMany(x => x.Values).Where(x => x.ItemId == ItemId).Sum(x => x.HQQuantity);
                            }

                            return (int)RetainerData.Values.SelectMany(x => x.Values).Where(x => x.ItemId == ItemId).Sum(x => x.Quantity);
                        }
                    }

                    // Resolved once instead of rebuilding the same filtered array inside all ten iterations
                    // below - this method is called once per material when a list is restocked, so the old
                    // Where().Select().ToArray()[i] allocated ten arrays per material for no reason.
                    var configuredRetainerIds = P.Config.RetainerIDs
                        .Where(x => x.Value == SvcEx.PlayerState.ContentId)
                        .Select(x => x.Key)
                        .ToArray();

                    // GetRetainerBySortedIndex walks the display-order table at +0x2D0 and returns null
                    // whenever that table holds a value >= 10 - which it does before the retainer list has
                    // finished loading, and after a character switch leaves stale entries behind. The
                    // surrounding catch cannot save us here: dereferencing null is an AccessViolation, a
                    // corrupted-state exception that try/catch does not intercept in .NET Core.
                    var retainerManager = RetainerManager.Instance();

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
                            if (retainer->RetainerId > 0 && !P.Config.RetainerIDs.Any(x => x.Key == retainer->RetainerId && x.Value == SvcEx.PlayerState.ContentId))
                            {
                                if (retainer->Available)
                                {
                                    P.Config.RetainerIDs.Add(retainer->RetainerId, SvcEx.PlayerState.ContentId);
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
                        {
                            if (RetainerData.ContainsKey(retainerId))
                            {
                                var ret = RetainerData[retainerId];
                                if (ret.ContainsKey(ItemId))
                                {
                                    var item = ret[ItemId];
                                    item.ItemId = ItemId;
                                    item.Quantity = GetRetainerInventoryItem(ItemId, retainerId);

                                }
                                else
                                {
                                    ret.TryAdd(ItemId, new ItemInfo(ItemId, GetRetainerInventoryItem(ItemId, retainerId), GetRetainerInventoryItem(ItemId, retainerId, true)));
                                }
                            }
                            else
                            {
                                RetainerData.TryAdd(retainerId, new Dictionary<uint, ItemInfo>());
                                var ret = RetainerData[retainerId];
                                if (ret.ContainsKey(ItemId))
                                {
                                    var item = ret[ItemId];
                                    item.ItemId = ItemId;
                                    item.Quantity = GetRetainerInventoryItem(ItemId, retainerId);

                                }
                                else
                                {
                                    ret.TryAdd(ItemId, new ItemInfo(ItemId, GetRetainerInventoryItem(ItemId, retainerId), GetRetainerInventoryItem(ItemId, retainerId, true)));
                                }
                            }
                        }
                    }

                    if (hqOnly)
                    {
                        return (int)RetainerData.Values.SelectMany(x => x.Values).Where(x => x.ItemId == ItemId).Sum(x => x.HQQuantity);
                    }

                    return (int)RetainerData.SelectMany(x => x.Value).Where(x => x.Key == ItemId).Sum(x => x.Value.Quantity);
                }
                catch (Exception ex)
                {
                    //Svc.Log.Error(ex, "RetainerInfoItemCount");
                    return 0;
                }
            }

            return 0;
        }

        public static void RestockFromRetainers(uint ItemId, int howManyToGet)
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

        public static bool ExtractSingular(uint ItemId, int howManyToGet, ulong retainerKey)
        {
            if (howManyToGet != 0 && RetainerDirectFetch.Available)
            {
                bool wantHQ = RetainerData[retainerKey].Values.Any(x => x.ItemId == ItemId && x.HQQuantity > 0);
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
                bool lookingForHQ = RetainerData[retainerKey].Values.Any(x => x.ItemId == ItemId && x.HQQuantity > 0);
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

            foreach (var material in materialList.OrderByDescending(x => x.Key))
            {
                Svc.Log.Debug($"{material}");
                var invCount = CraftingListUI.NumberOfIngredient(material.Key);
                if (invCount < material.Value)
                {
                    var diffcheck = material.Value - invCount;
                    Svc.Log.Debug($"{material.Key} {diffcheck}");
                    requiredItems.Add((int)material.Key, diffcheck);
                }

                //Refresh retainer cache if empty
                GetRetainerItemCount(material.Key);
            }

            if (P.Config.RestockFinishedProductsFromRetainers)
            {
                foreach (var entry in list.Recipes)
                {
                    var recipe = LuminaSheets.RecipeSheet[entry.ID];
                    var target = entry.Quantity * recipe.AmountResult;
                    var invCount = CraftingListUI.NumberOfIngredient(recipe.ItemResult.RowId);
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
                    GetRetainerItemCount(recipe.ItemResult.RowId);
                }
            }

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
                if (TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible)
                {
                    new AddonMaster.Talk((IntPtr)addon).Click();
                }
            }
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
                        var wantHQ = RetainerData[key].Values.Any(x => x.ItemId == item.Key && x.HQQuantity > 0);
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
                bool lookingForHQ = RetainerData[key].Values.Any(x => x.ItemId == item.Key && x.HQQuantity > 0);
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
