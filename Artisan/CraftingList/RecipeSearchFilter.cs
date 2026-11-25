using System;
using System.Collections.Generic;
using System.Globalization;

namespace Artisan.CraftingLists;

// 本檔假設與契約：
// - 純資料、只用 System：不碰 Dalamud／Lumina／原生記憶體，任何執行緒都可呼叫（離線探針直接連結本檔）。
// - 「做過沒」「有沒有材料」由呼叫端以委派提供；回 null＝不知道，篩選時照樣列出、不當成「沒有」。

/// <summary>「搜尋配方」分頁的一列：配方表與成品道具表攤平成純量。</summary>
public readonly record struct RecipeSearchRow(
    uint RecipeId,
    uint ItemId,
    string Name,
    uint CraftType,
    int Level,
    int Stars,
    uint UiCategory,
    bool Collectable,
    bool Expert,
    bool SecretBook,
    int AmountResult);

public enum RecipeSearchSort
{
    JobThenLevel = 0,
    Level = 1,
    Name = 2,
    Id = 3,
}

/// <summary>三態篩選：不限／只要／排除。</summary>
public enum RecipeSearchTri
{
    Any = 0,
    Yes = 1,
    No = 2,
}

/// <summary>「搜尋配方」分頁的篩選條件，整份存進設定檔。</summary>
/// <remarks>集合欄位的初始值必須是空的：設定檔走 Newtonsoft 合併，非空初始值會和存檔內容疊加。</remarks>
public sealed class RecipeSearchSettings
{
    public string Text = string.Empty;
    /// <summary>空＝不限職業；元素是 <c>Recipe.CraftType</c>（0＝木工師～7＝烹調師）。</summary>
    public List<uint> CraftTypes = new();
    public int MinLevel = 1;
    public int MaxLevel = 100;
    /// <summary>-1＝不限，其餘＝星數恰好相等。</summary>
    public int Stars = -1;
    /// <summary>0＝不限，其餘＝成品的 ItemUICategory。</summary>
    public uint UiCategory;
    public RecipeSearchTri Collectable = RecipeSearchTri.Any;
    public RecipeSearchTri Expert = RecipeSearchTri.Any;
    public RecipeSearchTri SecretBook = RecipeSearchTri.Any;
    public RecipeSearchTri Crafted = RecipeSearchTri.Any;
    public RecipeSearchSort Sort = RecipeSearchSort.JobThenLevel;
    public bool SortDescending;
    public bool TidyAfterAdd;
    /// <summary>加入目標清單的 ID；0 或找不到＝沒有選清單。</summary>
    public int TargetListId;
}

public static class RecipeSearchFilter
{
    /// <summary>可以加進製作清單的配方：有成品、有配方號（Number 0 是宇宙探索等不能進清單的配方）。</summary>
    public static bool IsListable(uint itemResult, int number) => itemResult != 0 && number != 0;

    /// <summary>名稱比對：字面子字串、不分大小寫、不吃 regex；整段是數字時另外比配方 ID 與成品道具 ID。</summary>
    public static bool MatchesText(in RecipeSearchRow row, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var t = text.Trim();
        if (row.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            return true;
        return uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
               && (row.RecipeId == id || row.ItemId == id);
    }

    /// <summary>不含「做過沒」「有沒有材料」的靜態條件。</summary>
    public static bool MatchesStatic(in RecipeSearchRow row, RecipeSearchSettings s)
    {
        if (s.CraftTypes.Count > 0 && !s.CraftTypes.Contains(row.CraftType)) return false;
        if (row.Level < s.MinLevel || row.Level > s.MaxLevel) return false;
        if (s.Stars >= 0 && row.Stars != s.Stars) return false;
        if (s.UiCategory != 0 && row.UiCategory != s.UiCategory) return false;
        if (!Tri(s.Collectable, row.Collectable)) return false;
        if (!Tri(s.Expert, row.Expert)) return false;
        if (!Tri(s.SecretBook, row.SecretBook)) return false;
        return MatchesText(row, s.Text);
    }

    /// <summary>三態對已知值；<paramref name="value"/> 為 null（不知道）時一律放行。</summary>
    public static bool Tri(RecipeSearchTri want, bool? value)
        => want == RecipeSearchTri.Any || value is not { } v || v == (want == RecipeSearchTri.Yes);

    /// <param name="crafted">做過沒；null＝不知道。只在「做過」篩選不是不限時呼叫。</param>
    /// <param name="hasMaterials">有沒有材料；傳 null＝不篩。委派回 false 或 null 都不列（材料快取沒算到的列看不到）。</param>
    public static List<RecipeSearchRow> Apply(
        IReadOnlyList<RecipeSearchRow> rows,
        RecipeSearchSettings s,
        Func<uint, bool?>? crafted = null,
        Func<uint, bool?>? hasMaterials = null)
    {
        var result = new List<RecipeSearchRow>();
        foreach (var row in rows)
        {
            if (!MatchesStatic(row, s)) continue;
            if (hasMaterials != null && hasMaterials(row.RecipeId) != true) continue;
            if (s.Crafted != RecipeSearchTri.Any && crafted != null && !Tri(s.Crafted, crafted(row.RecipeId))) continue;
            result.Add(row);
        }
        result.Sort(Comparer(s.Sort, s.SortDescending));
        return result;
    }

    public static Comparison<RecipeSearchRow> Comparer(RecipeSearchSort sort, bool descending)
    {
        Comparison<RecipeSearchRow> primary = sort switch
        {
            RecipeSearchSort.Level => (a, b) => Then(a.Level.CompareTo(b.Level), a.CraftType.CompareTo(b.CraftType)),
            RecipeSearchSort.Name => (a, b) => string.CompareOrdinal(a.Name, b.Name),
            RecipeSearchSort.Id => (a, b) => 0,
            _ => (a, b) => Then(Then(a.CraftType.CompareTo(b.CraftType), a.Level.CompareTo(b.Level)), a.Stars.CompareTo(b.Stars)),
        };
        // 配方 ID 收尾：同序的列每次重算都排在同一個位置，捲動中重算不會跳動。
        return descending
            ? (a, b) => -Then(primary(a, b), a.RecipeId.CompareTo(b.RecipeId))
            : (a, b) => Then(primary(a, b), a.RecipeId.CompareTo(b.RecipeId));
    }

    private static int Then(int first, int second) => first != 0 ? first : second;

    /// <summary>要做幾次才夠 <paramref name="items"/> 個成品：ceil(數量／每次產出)，與 Teamcraft 匯入同一條公式。</summary>
    public static int CraftsFor(int items, int amountResult)
    {
        if (items <= 0) return 0;
        var per = Math.Max(1, amountResult);
        return (int)Math.Min(int.MaxValue, ((long)items + per - 1) / per);
    }
}
