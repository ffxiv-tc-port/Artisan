using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace Artisan.CraftingLists
{
    /// <summary>以 RowId 比較 <see cref="Recipe"/>。凡是拿 Recipe 當鍵或做 Distinct 都要用它。</summary>
    /// <remarks>
    /// Recipe 含參考欄位：預設 GetHashCode 只看 ExcelPage（整張表同一個值），預設 Equals 走 ValueType 反射，
    /// 所以預設比較下字典每次查找都要對整張表逐列反射比較。
    /// 前提：所有 Recipe 都來自同一張表，RowId 相同就是同一列。
    /// </remarks>
    internal sealed class RecipeRowComparer : IEqualityComparer<Recipe>
    {
        public static readonly RecipeRowComparer Instance = new();

        public bool Equals(Recipe x, Recipe y) => x.RowId == y.RowId;

        public int GetHashCode(Recipe obj) => (int)obj.RowId;
    }
}
