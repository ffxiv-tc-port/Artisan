namespace Artisan.CraftingLists;

/// <summary>「開工前」卡片的屬性判定（純計算：不碰遊戲、Dalamud、設定；離線測試直接編這個檔）。</summary>
internal static class ListPrepRules
{
    internal enum StatVerdict
    {
        /// <summary>讀不到這個職業的裝備數值；不可當成足夠。</summary>
        Unknown = 0,
        Ok = 1,
        /// <summary>只靠裝備不夠，加上設定的食物／藥水（與公會加成）才夠。</summary>
        NeedsConsumables = 2,
        /// <summary>加上設定的食物／藥水也不夠。</summary>
        Short = 3,
    }

    /// <param name="gearCraft">只算裝備（含魔晶石）的作業精度。</param>
    /// <param name="boostedCraft">裝備＋設定的食物／藥水＋公會加成。</param>
    internal static StatVerdict Classify(bool known, int reqCraft, int reqControl,
        int gearCraft, int gearControl, int boostedCraft, int boostedControl)
    {
        if (!known)
            return StatVerdict.Unknown;
        if (boostedCraft < reqCraft || boostedControl < reqControl)
            return StatVerdict.Short;
        if (gearCraft < reqCraft || gearControl < reqControl)
            return StatVerdict.NeedsConsumables;
        return StatVerdict.Ok;
    }
}
