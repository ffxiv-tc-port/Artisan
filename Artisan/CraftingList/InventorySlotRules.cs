using System;

namespace Artisan.CraftingLists;

// 製作清單背包格位的純判斷（預估要幾格、剩幾格就暫停）；不碰 Dalamud，離線探針直接編譯這個檔。
internal static class InventorySlotRules
{
    public enum Quality
    {
        Nq,
        Hq,
        /// <summary>一般製作、道具與配方都能 HQ：做出來是哪一種要看解算器與運氣，事前不知道。</summary>
        Either,
    }

    /// <summary>簡易製作一律 NQ；道具或配方不能 HQ 也是 NQ；其餘兩種都可能。</summary>
    public static Quality ExpectedQuality(bool quickSynth, bool itemCanBeHq, bool recipeCanHq)
        => quickSynth || !itemCanBeHq || !recipeCanHq ? Quality.Nq : Quality.Either;

    /// <summary>做出來放進水晶欄或貨幣、不佔背包格的道具（1＝Gil，2～19＝碎晶／水晶／晶簇）。</summary>
    public static bool SkipsBag(uint itemId) => itemId is > 0 and < 20;

    /// <summary>收藏品每件各佔一格，不和別的疊在一起。</summary>
    public static bool IsStackable(int stackSize, bool alwaysCollectable) => stackSize > 1 && !alwaysCollectable;

    /// <summary>
    /// 再做出 <paramref name="amount"/> 個要新佔幾格。<paramref name="room"/>＝背包裡同一種既有各疊還放得下的總數。
    /// 不扣中間素材用完空出來的格子（上限估法）。
    /// </summary>
    public static int NewSlotsNeeded(int amount, bool stackable, int stackSize, int room)
    {
        if (amount <= 0)
            return 0;
        if (!stackable || stackSize <= 1)
            return amount;
        var overflow = amount - Math.Max(0, room);
        return overflow <= 0 ? 0 : (overflow + stackSize - 1) / stackSize;
    }

    /// <summary>
    /// 下一件做出來能不能直接疊進背包裡現有的某一疊（只看單一疊放不放得下）。
    /// 品質不確定時兩種品質都要放得下，才不會吃掉保留的格子。
    /// </summary>
    public static bool FitsExistingStack(Quality quality, bool stackable, int amount, int maxRoomNq, int maxRoomHq)
        => stackable && quality switch
        {
            Quality.Nq => maxRoomNq >= amount,
            Quality.Hq => maxRoomHq >= amount,
            _          => maxRoomNq >= amount && maxRoomHq >= amount,
        };

    /// <summary>「背包剩 N 格就暫停清單」：<paramref name="reserve"/> 小於等於 0＝關（照舊做到遊戲拒絕為止）。</summary>
    public static bool ShouldPause(int reserve, int freeSlots, bool fitsExistingStack)
        => reserve > 0 && !fitsExistingStack && freeSlots <= reserve;
}
