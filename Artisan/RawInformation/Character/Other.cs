using FFXIVClientStructs.FFXIV.Client.Game;

namespace Artisan.RawInformation.Character
{
    static unsafe class CharacterOther
    {
        private static readonly InventoryType[] BagTypes =
            [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

        /// <summary>背包空格數。任何一個背包頁讀不到就整個回 0，不回部分計數。
        /// 讀不到的兩種形狀都擋：容器本身是 null，或容器在但 <c>Items</c>（偏移 0x08）尚未配置
        /// —— 後者的 <c>Size</c> 可能已非 0，<c>Items[i]</c> 會從小偏移假位址讀垃圾 ItemId，
        /// 而垃圾值剛好是 0 就會被算成一個空格。
        /// ⚠️ 回 0 是收斂方向：<c>Spiritbond.ExtractMateriaTask</c> 的
        ///    <c>== 0 → return true</c>（視為這一輪不用抽魔晶石而跳過），所以「讀不到」會讓流程
        ///    少做事。反過來回一個偏大的部分計數，會讓它以為有空間而去抽魔晶石，抽出來沒地方放。
        ///    要分辨「滿了」和「讀不到」的呼叫端改用 <see cref="TryGetInventoryFreeSlotCount"/>。</summary>
        internal static int GetInventoryFreeSlotCount()
            => TryGetInventoryFreeSlotCount(out var free) ? free : 0;

        /// <summary>背包空格數；任何一個背包頁讀不到回 <see langword="false"/>。只在框架執行緒呼叫。</summary>
        internal static bool TryGetInventoryFreeSlotCount(out int free)
        {
            free = 0;
            var c = InventoryManager.Instance();
            if (c == null)
                return false;
            foreach (var x in BagTypes)
            {
                var inv = c->GetInventoryContainer(x);
                if (inv == null || inv->Items == null) return false;
                for (var i = 0; i < inv->Size; i++)
                {
                    if (inv->Items[i].ItemId == 0)
                        free++;
                }
            }
            return true;
        }

        /// <summary>
        /// 背包裡 <paramref name="itemId"/>（<paramref name="hq"/> 那一種品質）既有各疊還放得下幾個：
        /// <paramref name="totalRoom"/>＝各疊合計，<paramref name="maxRoom"/>＝單一疊最多。收藏品不算（不會再疊）。
        /// 讀不到回 <see langword="false"/>。只在框架執行緒呼叫，不保留任何指標。
        /// </summary>
        internal static bool TryGetStackRoom(uint itemId, bool hq, int stackSize, out int totalRoom, out int maxRoom)
        {
            totalRoom = 0;
            maxRoom = 0;
            var c = InventoryManager.Instance();
            if (c == null)
                return false;
            foreach (var x in BagTypes)
            {
                var inv = c->GetInventoryContainer(x);
                if (inv == null || inv->Items == null) return false;
                for (var i = 0; i < inv->Size; i++)
                {
                    ref var slot = ref inv->Items[i];
                    if (slot.ItemId != itemId)
                        continue;
                    if ((slot.Flags & InventoryItem.ItemFlags.Collectable) != 0)
                        continue;
                    if (((slot.Flags & InventoryItem.ItemFlags.HighQuality) != 0) != hq)
                        continue;
                    var room = stackSize - (int)slot.Quantity;
                    if (room <= 0)
                        continue;
                    totalRoom += room;
                    if (room > maxRoom)
                        maxRoom = room;
                }
            }
            return true;
        }
    }
}
