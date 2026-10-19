using Dalamud.Plugin;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System.Collections.Concurrent;
using System.Threading;

namespace Artisan.IPC
{
    public static class ItemVendorLocation
    {
        private const string PluginName = "ItemVendorLocation";

        public static void OpenContextMenu(uint ItemId)
        {
            if (DalamudReflector.TryGetDalamudPlugin("ItemVendorLocation", out var pl, false, true))
            {
                var itemLookup = pl.GetFoP("ItemLookup");
                if (itemLookup == null) return;
                var itemInfo = itemLookup.Call("GetItemInfo", [ItemId]);
                if (itemInfo != null)
                {
                    itemInfo.Call("ApplyFilters", []);
                    pl.Call("ShowMultipleVendors", [itemInfo]);
                }
            }

        }

        // 答案只取決於 ItemVendorLocation 在建構子裡建好、之後不再改的道具表 ⇒ 依插件實例快取。
        // 呼叫端有繪製執行緒也有執行緒池（材料表背景建構）：快取物件整份換掉，答案表用 ConcurrentDictionary。
        // 「插件在不在」每次照讀 IsLoaded（不配置）：卸載時 ActivePluginsChanged 約 1 秒後才送，只靠事件會回舊答案。
        private sealed class VendorCache(IExposedPlugin? plugin, int epoch)
        {
            public readonly IExposedPlugin? Plugin = plugin;
            public readonly int Epoch = epoch;
            public IDalamudPlugin? Instance;
            public readonly ConcurrentDictionary<uint, bool> HasVendor = new();
        }

        private static VendorCache? vendorCache;
        private static int vendorEpoch;

        internal static void Init() => Svc.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;

        internal static void Dispose() => Svc.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;

        // Dalamud 在插件清單的鎖內、載入／卸載的那條執行緒上呼叫：只作廢不查。
        private static void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
        {
            Interlocked.Increment(ref vendorEpoch);
            Volatile.Write(ref vendorCache, null);
        }

        // 世代號擋的是「事件前開始建、事件後才寫回」的那一份：它的 Epoch 對不上，下一次照樣重建。
        private static VendorCache CurrentCache()
        {
            var epoch = Volatile.Read(ref vendorEpoch);
            var cache = Volatile.Read(ref vendorCache);
            if (cache != null && cache.Epoch == epoch)
                return cache;

            IExposedPlugin? found = null;
            foreach (var p in Svc.PluginInterface.InstalledPlugins)
            {
                if (p.InternalName != PluginName)
                    continue;
                found ??= p;
                if (p.IsLoaded)
                {
                    found = p;
                    break;
                }
            }
            var fresh = new VendorCache(found, epoch);
            Interlocked.CompareExchange(ref vendorCache, fresh, cache);
            return fresh;
        }

        /// <remarks>材料表在執行緒池上建構時也會呼叫（<c>Ingredient</c> 建構子）⇒ 查插件走 <see cref="PluginLookup"/>。</remarks>
        public static bool ItemHasVendor(uint ItemId)
        {
            var cache = CurrentCache();
            if (cache.Plugin is not { IsLoaded: true })
                return false;
            if (cache.HasVendor.TryGetValue(ItemId, out var has))
                return has;

            if (!PluginLookup.TryGetLoaded(PluginName, out var pl))
                return false;
            var itemLookup = pl.GetFoP("ItemLookup");
            if (itemLookup == null) return false;
            has = itemLookup.Call("GetItemInfo", [ItemId]) != null;

            // 插件重載過而事件還沒到：手上這份屬於舊實例，丟掉讓下一次重建；這次的答案照回不存。
            var owner = Interlocked.CompareExchange(ref cache.Instance, pl, null) ?? pl;
            if (ReferenceEquals(owner, pl))
                cache.HasVendor[ItemId] = has;
            else
                Interlocked.CompareExchange(ref vendorCache, null, cache);
            return has;
        }
    }

}
