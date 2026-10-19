using System.Collections.Generic;
using System.Linq;

namespace Artisan.Universalis
{
    /// <summary>
    /// 一份 <see cref="MarketboardData"/> 是從哪一條路徑來的。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="Unknown"/> 刻意給明確的 0：沒有零值的列舉會讓 <c>default</c> 落在一個
    /// 無效值上，而那種壞法是靜默的。
    /// </remarks>
    public enum MarketboardSource
    {
        /// <summary>還沒設定過（正常路徑上不該出現；出現了就是漏了賦值，不要當成正常資料）。</summary>
        Unknown = 0,

        /// <summary>正常路徑：<c>/api/v2/{scope}/{ids}</c> 的完整掛售清單。</summary>
        Listings = 1,

        /// <summary>
        /// 降級路徑：正常端點問不到（逾時／504），改用 <c>/api/v2/aggregated/…</c> 的極簡回應。
        /// 只有「最低單價」與「在哪個世界」是真的，掛售筆數與在售總量<b>不可知</b>。
        /// </summary>
        Aggregated = 2,
    }

    public class MarketboardData
    {
        public long LastCheckTime { get; set; }

        public long LastUploadTime { get; set; }

        public double? AveragePriceNQ { get; set; }

        public double? AveragePriceHQ { get; set; }

        public double? CurrentAveragePriceNQ { get; set; }

        public double? CurrentAveragePriceHQ { get; set; }

        public double? MinimumPriceNQ { get; set; }

        public double? MinimumPriceHQ { get; set; }

        public double? MaximumPriceNQ { get; set; }

        public double? MaximumPriceHQ { get; set; }

        public double? CurrentMinimumPrice { get; set; } = -1;

        public double? ListingQuantity { get; set; }
        public string? LowestWorld { get; set; }

        public double? TotalNumberOfListings { get;  set; }

        public double? TotalQuantityOfUnits { get; set; }

        public List<Listing> AllListings { get; set; } = new();

        /// <summary>這份資料是完整查詢還是降級查詢來的。</summary>
        public MarketboardSource Source { get; set; } = MarketboardSource.Unknown;

        /// <summary>這份資料可不可以拿去和「自己做」「NPC 商店」比價。</summary>
        /// <remarks>
        /// 🔴 降級路徑只知道「一件的最低價」（合成的掛售數量恆為 1）⇒ 拿它算「買 N 件」
        /// 會得到一件的價，市場選項幾乎必然虛假勝出。比價一律問這裡，不要各自判 Source。
        /// </remarks>
        public bool IsUsableForComparison => Source == MarketboardSource.Listings && AllListings.Count > 0;

        /// <summary>
        /// 掛售清單被 <c>listings=N</c> 參數截斷了。
        /// </summary>
        /// <remarks>
        /// 🔴 為什麼要記這一位：Universalis 的 <c>listingsCount</c> 與 <c>unitsForSale</c>
        /// 算的是<b>這次回傳的那幾筆</b>，不是市場上的真實總數。
        /// 這一位讓畫面改成「≥N」，把「只知道下界」講出來。
        /// </remarks>
        public bool ListingsTruncated { get; set; }

        /// <summary>
        /// 供結果快取用的淺複製。
        /// </summary>
        /// <remarks>
        /// 🔴 必須複製，不可以把同一個實例交給兩個呼叫端：
        /// <c>IngredientTable.CheapestServerColumn.ToName</c> 會<b>就地改寫</b>
        /// <see cref="LowestWorld"/>（寫入「這一列所需數量下最便宜的世界」）。
        /// </remarks>
        public MarketboardData Clone() => new()
        {
            LastCheckTime = LastCheckTime,
            LastUploadTime = LastUploadTime,
            AveragePriceNQ = AveragePriceNQ,
            AveragePriceHQ = AveragePriceHQ,
            CurrentAveragePriceNQ = CurrentAveragePriceNQ,
            CurrentAveragePriceHQ = CurrentAveragePriceHQ,
            MinimumPriceNQ = MinimumPriceNQ,
            MinimumPriceHQ = MinimumPriceHQ,
            MaximumPriceNQ = MaximumPriceNQ,
            MaximumPriceHQ = MaximumPriceHQ,
            CurrentMinimumPrice = CurrentMinimumPrice,
            ListingQuantity = ListingQuantity,
            LowestWorld = LowestWorld,
            TotalNumberOfListings = TotalNumberOfListings,
            TotalQuantityOfUnits = TotalQuantityOfUnits,
            Source = Source,
            ListingsTruncated = ListingsTruncated,
            AllListings = AllListings
                .Select(x => new Listing { World = x.World, Quantity = x.Quantity, TotalPrice = x.TotalPrice, UnitPrice = x.UnitPrice })
                .ToList(),
        };
    }

    public class Listing
    {
        public string World { get; set; } = "";

        public double Quantity { get; set; }

        public double TotalPrice { get; set; }

        public double UnitPrice { get; set; }
    }
}
