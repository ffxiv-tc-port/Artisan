using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.Universalis;
using Dalamud.Interface.Textures.TextureWraps;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Artisan.RawInformation
{
    public class Ingredient
    {
        private DateTime RetainerCheck;
        private DateTime RemainingCheck;

        public int AmountUsedForSubcrafts;
        public bool CanBeCrafted = false;
        public uint Category;
        public Recipe CraftedRecipe;
        public Item Data;
        public TerritoryType GatherZone;
        public int MaterialIndex;
        public NewCraftingList OriginList;
        public Dictionary<uint, int> OriginListMaterials;
        public int Required;
        public List<int> Sources = new();
        public bool TimedNode;
        public List<uint> UsedInCrafts = new();
        private int remaining = -1;
        private Dictionary<uint, Recipe?> subRecipes = new();
        public Dictionary<uint, int> UsedInMaterialsList;
        public Dictionary<uint, int> UsedInMaterialsListCount = new();
        public Dictionary<uint, List<Tuple<uint, int, int>>> SubSubMaterials = new();
        public MarketboardData? MarketboardData;
        public bool MarketboardFetchFailed;
        public bool RecipeOnList;
        public IngredientHelpers IngredientHelper;

        public Ingredient(uint ItemId, int required, NewCraftingList originList, Dictionary<uint, int> materials, IngredientHelpers ingredientHelpers)
        {
            Data = LuminaSheets.ItemSheet.Values.First(x => x.RowId == ItemId);
            Required = required;
            if (LuminaSheets.RecipeSheet.Values.FindFirst(x => x.ItemResult.RowId == ItemId, out CraftedRecipe)) { Sources.Add(1); CanBeCrafted = true; }
            if (LuminaSheets.GatheringItemSheet.Values.Any(x => x.Item.RowId == ItemId)) Sources.Add(2);
            if (Svc.Data.GetExcelSheet<FishingSpot>()!.Any(x => x.Item.Any(y => y.Value.RowId == ItemId))) Sources.Add(3);
            if (ItemVendorLocation.ItemHasVendor(ItemId)) Sources.Add(4);
            if (Svc.Data.GetExcelSheet<RetainerTaskNormal>()!.Any(x => x.Item.RowId == ItemId && x.GatheringLog.RowId == 0 && x.FishingLog.RowId == 0) || DropSources.Sources!.Any(x => x.ItemId == ItemId)) Sources.Add(5);

            if (Sources.Count == 0) Sources.Add(-1);
            GatherZone = Svc.Data.Excel.GetSheet<TerritoryType>()!.First(x => x.RowId == 1);
            TimedNode = false;
            if (LuminaSheets.GatheringItemSheet!.FindFirst(x => x.Value.Item.RowId == Data.RowId, out var gather))
            {
                if (HiddenItems.Items.FindFirst(x => x.ItemId == Data.RowId, out var hiddenItems))
                {
                    if (Svc.Data.Excel.GetSheet<GatheringPoint>()!.FindFirst(y => y.GatheringPointBase.RowId == hiddenItems.NodeId && y.TerritoryType.Value.PlaceName.RowId > 0, out var gatherpoint))
                    {
                        var transient = Svc.Data.Excel.GetSheet<GatheringPointTransient>().GetRow(gatherpoint.RowId);
                        if (transient.GatheringRarePopTimeTable.RowId > 0)
                            TimedNode = true;

                        if (gatherpoint.TerritoryType.IsValid)
                            GatherZone = gatherpoint.TerritoryType.Value!;
                    }
                }
                else
                {
                    foreach (var pointBase in LuminaSheets.GatheringPointBaseSheet.Values.Where(x => x.Item.Any(y => y.RowId == gather.Key)))
                    {
                        if (GatherZone.RowId != 1) break;
                        if (Svc.Data.Excel.GetSheet<GatheringPoint>()!.FindFirst(y => y.GatheringPointBase.RowId == pointBase.RowId && y.TerritoryType.Value.PlaceName.RowId > 0, out var gatherpoint))
                        {
                            var transient = Svc.Data.Excel.GetSheet<GatheringPointTransient>().GetRow(gatherpoint.RowId);
                            if (transient.GatheringRarePopTimeTable.RowId > 0)
                                TimedNode = true;

                            if (gatherpoint.TerritoryType.IsValid)
                                GatherZone = gatherpoint.TerritoryType.Value!;
                        }
                    }
                }
            }

            Category = Data.ItemSearchCategory.RowId;
            OriginList = originList;
            OriginListMaterials = materials;
            foreach (var recipe in OriginList.Recipes)
            {
                if (LuminaSheets.RecipeSheet[recipe.ID].Ingredients().Any(x => x.Item.RowId == ItemId) && !UsedInCrafts.Contains(recipe.ID))
                    UsedInCrafts.Add(recipe.ID);
            }
            UsedInMaterialsList = materials.Where(x => LuminaSheets.RecipeSheet.Values.Any(y => y.ItemResult.RowId == x.Key && y.Ingredients().Any(z => z.Item.RowId == Data.RowId))).ToDictionary(x => x.Key, x => x.Value);
            RecipeOnList = originList.Recipes.Any(x => LuminaSheets.RecipeSheet[x.ID].ItemResult.RowId == ItemId);
            if (P.Config.UseUniversalis && !P.Config.UniversalisOnDemand)
            {
                // Building a list creates one Ingredient per material; the Universalis client coalesces and
                // paces the requests, so kicking one off per ingredient is safe.
                var limitToDC = P.Config.LimitUnversalisToDC;
                var itemIdForPrice = ItemId;
                _ = Task.Run(async () => MarketboardData = limitToDC
                    ? await P.UniversalsisClient.GetDCDataAsync(itemIdForPrice).ConfigureAwait(false)
                    : await P.UniversalsisClient.GetRegionDataAsync(itemIdForPrice).ConfigureAwait(false));
            }
            IngredientHelper = ingredientHelpers;
        }

        // 下面三個都是遊戲資料（Data／GatherZone／Category 只在建構子裡指定），清單編輯器每幀每列都要畫 ⇒ 只轉一次字串。
        private string? itemName, gatherZoneName, categoryName;
        public string ItemName => itemName ??= Data.Name.ToString();
        public string GatherZoneName => gatherZoneName ??= GatherZone.PlaceName.Value.Name.ToString();
        public string CategoryName => categoryName ??= Svc.Data.Excel.GetSheet<ItemSearchCategory>().GetRow(Category).Name.ToString();

        public virtual event EventHandler<bool>? OnRemainingChange;

        public int Inventory => CraftingListUI.NumberOfIngredient(Data.RowId);
        public unsafe int InventoryHQ => InventoryManager.Instance()->GetInventoryItemCount(Data.RowId, true, false, false);

        public int Remaining
        {
            get
            {
                if (DateTime.Now > RemainingCheck)
                {
                    // "Remaining" is the physical shortfall shown to the user and used by exports/filters/
                    // market-board lookups. Do not subtract TotalCraftable or AmountUsedForSubcrafts here:
                    // NumberCraftable evaluates each recipe on the list independently, so a material shared by
                    // multiple recipes would be deducted once per recipe from the same finite pool. Downstream
                    // products already crafted are also not inventory of this raw ingredient.
                    var current = Math.Max(0, Required - Inventory - RetainerCount);
                    if (remaining != current)
                    {
                        remaining = current;
                        OnRemainingChange?.Invoke(this, true);
                    }
                    RemainingCheck = DateTime.Now.AddSeconds(0.5);
                }
                return remaining;
            }
        }

        public int RetainerCount
        {
            get
            {
                if (DateTime.Now > RetainerCheck)
                {
                    // Called synchronously in place (matching the ReainterCountHQ getter below). Do not wrap it in
                    // Task.Run(...).Result: GetRetainerItemCount marshals its native reads back to the framework
                    // thread, which may be the very thread blocked waiting on the result.
                    retainerCount = RetainerInfo.GetRetainerItemCount(Data.RowId);
                    RetainerCheck = DateTime.Now.AddSeconds(0.5);
                }
                return retainerCount;
            }
        }

        public int ReainterCountHQ
        {
            get
            {
                if (DateTime.Now > RetainerCheck)
                {
                    retainerCountHQ = RetainerInfo.GetRetainerItemCount(Data.RowId, true, true);
                    RetainerCheck = DateTime.Now.AddSeconds(0.5);
                }
                return retainerCountHQ;
            }
        }

        public int TotalCraftable => NumberCraftable(Data.RowId);

        private int retainerCount;
        private int retainerCountHQ;

        // GetSubCraftCount 在清單編輯器每幀每列都跑（道具名欄的底色），所以下面幾支查找寫成迴圈、不經 LINQ／Ingredients()。
        // 找不到時擲 InvalidOperationException（與 LINQ 的 First 同一種失敗形式）。
        private static int AmountUsedIn(Recipe recipe, uint itemId)
        {
            for (var i = 0; i < recipe.Ingredient.Count; i++)
            {
                if (recipe.Ingredient[i].RowId == itemId && recipe.Ingredient[i].IsValid)
                    return recipe.AmountIngredient[i];
            }
            throw new InvalidOperationException("Sequence contains no matching element");
        }

        private int QuantityOnList(uint recipeId)
        {
            foreach (var r in OriginList.Recipes)
            {
                if (r.ID == recipeId)
                    return r.Quantity;
            }
            throw new InvalidOperationException("Sequence contains no matching element");
        }

        private Ingredient? FindHelper(uint itemId)
        {
            foreach (var x in IngredientHelper.HelperList)
            {
                if (x.Data.RowId == itemId)
                    return x;
            }
            return null;
        }

        private bool SubSubMaterialsContain(uint recipeId)
        {
            foreach (var list in SubSubMaterials.Values)
            {
                foreach (var y in list)
                {
                    if (y.Item1 == recipeId)
                        return true;
                }
            }
            return false;
        }

        public int GetSubCraftCount()
        {
            int output = 0;
            SubSubMaterials.Clear();
            UsedInMaterialsListCount.Clear();

            foreach (var craft in UsedInCrafts)
            {
                var recipe = LuminaSheets.RecipeSheet[craft];
                var owned = RetainerInfo.GetRetainerItemCount(recipe.ItemResult.RowId) + CraftingListUI.NumberOfIngredient(recipe.ItemResult.RowId);
                var numberUsedInRecipe = AmountUsedIn(recipe, Data.RowId);
                var numberOnList = QuantityOnList(craft); // * recipe.AmountResult; Did I want to multiply that? Answers on a postcard
                var subing = FindHelper(recipe.ItemResult.RowId);
                if (subing != null)
                {
                    if (subing.UsedInCrafts.Count > 0)
                    {
                        //output += subing.GetSubCraftCount() * numberUsedInRecipe;
                        foreach (var i in subing.UsedInMaterialsListCount)
                        {
                            if (i.Value == 0) continue;

                            if (SubSubMaterials.ContainsKey(recipe.RowId))
                            {
                                if (SubSubMaterialsContain(i.Key))
                                    continue;
                                SubSubMaterials[recipe.RowId].Add(new Tuple<uint, int, int>(i.Key, i.Value * numberUsedInRecipe, i.Value));
                            }
                            else
                            {
                                SubSubMaterials[recipe.RowId] = new() { new Tuple<uint, int, int>(i.Key, i.Value * numberUsedInRecipe, i.Value) };
                            }
                        }
                    }
                }
                else
                {
                    continue;
                }

                UsedInMaterialsListCount[recipe.RowId] = Math.Min(numberUsedInRecipe * numberOnList, (int)Math.Floor((double)(owned / recipe.AmountResult) * numberUsedInRecipe));

                output += UsedInMaterialsListCount[recipe.RowId];
            }

            return output;

            //foreach (var material in UsedInMaterialsList)
            //{
            //    var owned = RetainerInfo.GetRetainerItemCount(material.Key) + CraftingListUI.NumberOfIngredient(material.Key);
            //    var recipe = LuminaSheets.RecipeSheet.Values.First(x => x.ItemResult.RowId == material.Key && x.UnkData5.Any(y => y.ItemIngredient == Data.RowId));
            //    var numberUsedInRecipe = recipe.UnkData5.First(x => x.ItemIngredient == Data.RowId).AmountIngredient;
            //    var listMaterialRequired = OriginListMaterials.Any(x => x.Key == material.Key) ? OriginListMaterials.FirstOrDefault(x => x.Key == material.Key).Value : 0;
            //    var stillToMake = Math.Max(listMaterialRequired - owned, 0);
            //    var technicallyAlreadyCrafted = Math.Ceiling((double)owned / recipe.AmountResult);

            //    if (listMaterialRequired % recipe.AmountResult == 0)
            //    {
            //        UsedInMaterialsListCount[recipe.RowId] = Math.Min((int)Math.Floor((double)(owned / recipe.AmountResult)) * numberUsedInRecipe, material.Value * numberUsedInRecipe);
            //    }
            //    else
            //    {
            //        UsedInMaterialsListCount[recipe.RowId] = (int)technicallyAlreadyCrafted * numberUsedInRecipe;
            //    }

            //    output += UsedInMaterialsListCount[recipe.RowId];
            //}
            //return output;
        }

        // 材料集合與順序同 Ingredients().Where(Amount > 0)（有效的列、數量大於 0）；取最小值不收進 List。
        private int NumberCraftable(uint ItemId)
        {
            if (LuminaSheets.TryGetFirstRecipeByResult(ItemId, out var recipe))
            {
                var minUses = int.MaxValue;
                var anyIngredient = false;
                for (var slot = 0; slot < recipe.Ingredient.Count; slot++)
                {
                    int amount = recipe.AmountIngredient[slot];
                    if (amount <= 0 || !recipe.Ingredient[slot].IsValid)
                        continue;
                    var ingredientId = recipe.Ingredient[slot].RowId;

                    int invCount = CraftingListUI.NumberOfIngredient(ingredientId);
                    int retainerCount = RetainerInfo.GetRetainerItemCount(ingredientId);

                    int craftableCount = 0;
                    Recipe? subRecipe;
                    if (subRecipes.ContainsKey(ingredientId))
                    {
                        subRecipe = subRecipes[ingredientId];
                    }
                    else
                    {
                        subRecipe = CraftingListHelpers.GetIngredientRecipe(ingredientId);
                        subRecipes.Add(ingredientId, subRecipe);
                    }

                    if (subRecipe is not null)
                    {
                        craftableCount = NumberCraftable(ingredientId);
                    }

                    int total = invCount + retainerCount + craftableCount;

                    int uses = total / amount;

                    if (uses < minUses)
                        minUses = uses;
                    anyIngredient = true;
                }

                // 與對空集合呼叫 Min() 相同的失敗形式。
                if (!anyIngredient)
                    throw new InvalidOperationException("Sequence contains no elements");
                return minUses * recipe.AmountResult;
            }
            else
                return -1;
        }
    }
}