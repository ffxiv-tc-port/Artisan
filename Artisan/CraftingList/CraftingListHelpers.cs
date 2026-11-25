using Artisan;
using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

internal static class CraftingListHelpers
{
    // 會被執行緒池（清單表格重建、僱員補貨）與框架執行緒同時寫；讀一律 IsSelectedRecipeCraftable，缺鍵＝false。
    internal static readonly ConcurrentDictionary<uint, bool> SelectedRecipesCraftable = new();

    internal static bool IsSelectedRecipeCraftable(uint itemId)
        => SelectedRecipesCraftable.TryGetValue(itemId, out var craftable) && craftable;

    public static void AddRecipeIngredientsToList(Recipe? recipe, ref Dictionary<uint, int> ingredientList, bool addSublist = true, NewCraftingList? selectedList = null)
    {
        try
        {
            if (recipe == null) return;

            if (selectedList != null)
            {
                var effectiveQuantity = GetEffectiveCraftQuantity(recipe.Value, selectedList);
                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    if (ingredientList.ContainsKey(ing.Item.RowId))
                    {
                        ingredientList[ing.Item.RowId] += ing.Amount * effectiveQuantity;
                    }
                    else
                    {
                        ingredientList.TryAdd(ing.Item.RowId, ing.Amount * effectiveQuantity);
                    }

                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    SelectedRecipesCraftable[ing.Item.RowId] = SheetExtensions.AnyRecipeResultNamed(name);

                    if (GetIngredientRecipe(ing.Item.RowId) != null && addSublist)
                    {
                        AddRecipeIngredientsToList(GetIngredientRecipe(ing.Item.RowId), ref ingredientList);
                    }

                }
            }
            else
            {
                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    if (ingredientList.ContainsKey(ing.Item.RowId))
                    {
                        ingredientList[ing.Item.RowId] += ing.Amount;
                    }
                    else
                    {
                        ingredientList.TryAdd(ing.Item.RowId, ing.Amount);
                    }

                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    SelectedRecipesCraftable[ing.Item.RowId] = SheetExtensions.AnyRecipeResultNamed(name);

                    if (GetIngredientRecipe(ing.Item.RowId) != null && addSublist)
                    {
                        AddRecipeIngredientsToList(GetIngredientRecipe(ing.Item.RowId), ref ingredientList);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "ERROR");
        }
    }

    public static int GetEffectiveCraftQuantity(Recipe recipe, NewCraftingList selectedList)
    {
        var quantity = selectedList.Recipes.First(x => x.ID == recipe.RowId).Quantity;

        if (P.Config.SubtractOwnedFinishedProductFromIngredientTable)
        {
            var owned = CraftingListUI.NumberOfIngredient(recipe.ItemResult.RowId) + RetainerInfo.GetRetainerItemCount(recipe.ItemResult.RowId);
            var ownedCrafts = (int)Math.Floor((double)owned / recipe.AmountResult);
            quantity = Math.Max(0, quantity - ownedCrafts);
        }

        return quantity;
    }

    public static Recipe? GetIngredientRecipe(uint ingredient)
    {
        if (LuminaSheets.TryGetFirstRecipeByResult(ingredient, out var result))
            return result;

        return null;
    }

    public static void TidyUpList(NewCraftingList list)
    {
        var tempMaterialList = new Dictionary<uint, int>();
        foreach (var recipe in list.Recipes)
        {
            Recipe r = LuminaSheets.RecipeSheet[recipe.ID];
            AddRecipeIngredientsToList(r, ref tempMaterialList, false, list);
        }

        foreach (var requiredItem in tempMaterialList)
        {
            if (IsSelectedRecipeCraftable(requiredItem.Key))
            {
                var recipe = GetIngredientRecipe(requiredItem.Key);
                if (list.Recipes.Any(x => x.ID == recipe.Value.RowId))
                {
                    var crafting = list.Recipes.First(x => x.ID == recipe.Value.RowId).Quantity * recipe.Value.AmountResult;
                    
                    if (crafting > requiredItem.Value)
                    {
                        double quant = Math.Ceiling((double)requiredItem.Value / recipe.Value.AmountResult);
                        list.Recipes.First(x => x.ID == recipe.Value.RowId).Quantity = (int)quant;
                    }
                }

            }
        }
    }

    /// <summary>
    /// 把配方加進清單的共用路徑：清單編輯器兩顆加入鈕、道具右鍵選單、搜尋配方分頁都走這裡。
    /// 不存檔、不刷新清單編輯器：呼叫端整批加完存一次、刷新一次。只在框架執行緒呼叫（會改設定物件）。
    /// </summary>
    /// <param name="items">配方與製作次數；Number 0（不能進清單）或次數 &lt;1 的跳過。</param>
    /// <param name="nqOnExisting">已在清單裡的配方也改成清單的「新加入時設為簡易製作」（清單編輯器）；否則只設新列（右鍵選單）。</param>
    /// <param name="source">寫進 log 的來源（量各入口的使用次數）。</param>
    /// <returns>實際加入的配方數。</returns>
    internal static int AddRecipesToList(NewCraftingList list, IEnumerable<(Recipe Recipe, int Crafts)> items,
        bool withSubcrafts, bool nqOnExisting, bool tidy, string source)
    {
        var added = 0;
        foreach (var (recipe, crafts) in items)
        {
            if (recipe.Number == 0 || crafts < 1)
                continue;
            if (withSubcrafts)
                CraftingListUI.AddAllSubcrafts(recipe, list, 1, crafts);

            var existing = list.Recipes.FirstOrDefault(x => x.ID == recipe.RowId);
            if (existing != null)
            {
                existing.Quantity = (int)Math.Min(int.MaxValue, (long)existing.Quantity + crafts);
                if (nqOnExisting)
                    (existing.ListItemOptions ??= new ListItemOptions()).NQOnly = list.AddAsQuickSynth;
            }
            else
            {
                list.Recipes.Add(new ListItem { ID = recipe.RowId, Quantity = crafts, ListItemOptions = new ListItemOptions { NQOnly = list.AddAsQuickSynth } });
            }
            added++;
        }

        if (added == 0)
            return 0;
        if (tidy)
            TidyUpList(list);
        Svc.Log.Information($"[Artisan][加入清單] 來源：{source}；清單「{list.Name}」加入 {added} 筆配方（{(withSubcrafts ? "含子配方" : "不含子配方")}）。");
        return added;
    }
}