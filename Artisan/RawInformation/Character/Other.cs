using FFXIVClientStructs.FFXIV.Client.Game;

namespace Artisan.RawInformation.Character
{
    static unsafe class CharacterOther
    {
        /// <summary>背包空格數。任何一個背包頁讀不到就整個回 0，不回部分計數。
        /// 讀不到的兩種形狀都擋：容器本身是 null，或容器在但 <c>Items</c>（偏移 0x08）尚未配置
        /// —— 後者的 <c>Size</c> 可能已非 0，<c>Items[i]</c> 會從小偏移假位址讀垃圾 ItemId，
        /// 而垃圾值剛好是 0 就會被算成一個空格。
        /// ⚠️ 回 0 是收斂方向：唯一的呼叫端是 <c>Spiritbond.ExtractMateriaTask</c> 的
        ///    <c>== 0 → return true</c>（視為這一輪不用抽魔晶石而跳過），所以「讀不到」會讓流程
        ///    少做事。反過來回一個偏大的部分計數，會讓它以為有空間而去抽魔晶石，抽出來沒地方放。</summary>
        internal static int GetInventoryFreeSlotCount()
        {
            InventoryType[] types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
            var c = InventoryManager.Instance();
            var slots = 0;
            foreach (var x in types)
            {
                var inv = c->GetInventoryContainer(x);
                if (inv == null || inv->Items == null) return 0;
                for (var i = 0; i < inv->Size; i++)
                {
                    if (inv->Items[i].ItemId == 0)
                    {
                        slots++;
                    }
                }
            }
            return slots;
        }
    }
}
