using Artisan.Autocraft;
using Artisan.CraftingLogic;
using Artisan.GameInterop;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Artisan.CraftingLists
{
    internal class CraftingListUI
    {
        internal static string Search = string.Empty;
        public static unsafe InventoryManager* invManager = InventoryManager.Instance();
        public static ConcurrentDictionary<Recipe, bool> CraftableItems = new();
        internal static ConcurrentDictionary<int, int> SelectedRecipeRawIngredients = new();
        internal static bool keyboardFocus = true;
        internal static string newListName = string.Empty;
        internal static NewCraftingList selectedList = new();
        internal static List<uint> jobs = new();
        internal static List<int> rawIngredientsList = new();
        internal static ConcurrentDictionary<int, int> subtableList = new();
        internal static List<int> listMaterials = new();
        internal static ConcurrentDictionary<int, int> listMaterialsNew = new();
        public static bool Processing;
        public static uint CurrentProcessedItem;
        public static int CurrentProcessedItemIndex;
        public static int CurrentProcessedItemCount;
        public static int CurrentProcessedItemListCount;
        private static readonly ListFolders ListsUI = new();

        private static bool GatherBuddy => DalamudReflector.TryGetDalamudPlugin("GatherbuddyReborn", out var gb, false, true);
        private static bool ItemVendor => DalamudReflector.TryGetDalamudPlugin("ItemVendorLocation", out var ivl, false, true);

        private static bool MonsterLookup => DalamudReflector.TryGetDalamudPlugin("Monster Loot Hunter", out var mlh, false, true);

        internal static void Draw()
        {
            ImGui.TextWrapped(("Crafting lists are a fantastic way to queue up different crafts and have them craft one-by-one. Create a list by importing from Teamcraft using the button at the bottom, or click the '+' icon and give your list a name." +
                              " You can also right click an item from the game's recipe menu to either add it to a new list if one is not selected, or to create a new list with it as the first item if a list is not selected.").Loc());

            ImGui.Dummy(new Vector2(0, 14f));
            ImGui.TextWrapped("Left click a list to open the editor. Right click a list to select it without opening the editor.".Loc());

            ImGui.Separator();

            DrawListOptions();
            ImGui.Spacing();
        }

        private static void DrawListOptions()
        {
            ImGui.BeginChild("ListsSelector", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 200f));
            ListsUI.Draw(ImGui.GetContentRegionAvail().X);
            ImGui.EndChild();

            ImGui.BeginChild("ListButtons", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 95f));
            if (selectedList.ID != 0)
            {
                if (Endurance.Enable || Processing)
                    ImGui.BeginDisabled();

                if (ImGui.Button("Start Crafting List".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                {
                    StartList();
                }

                if (RetainerInfo.ATools)
                {
                    if (RetainerInfo.TM.IsBusy)
                    {
                        if (ImGui.Button("Abort Collecting From Retainer".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                        {
                            RetainerInfo.TM.Abort();
                        }
                    }
                    else
                    {
                        bool disable = !Player.Available ? false : RetainerInfo.GetReachableRetainerBell() == null;
                        using (ImRaii.Disabled(disable))
                        {
                            if (ImGui.Button("Restock Inventory From Retainers".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                            {
                                Task.Run(() => RetainerInfo.RestockFromRetainers(selectedList));
                            }
                        }
                    }
                }
                else
                {
                    if (!RetainerInfo.AToolsInstalled)
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, "Please install Allagan Tools for retainer features.".Loc());

                    if (RetainerInfo.AToolsInstalled && !RetainerInfo.AToolsEnabled)
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, "Please enable Allagan Tools for retainer features.".Loc());
                }


                if (Endurance.Enable || Processing)
                    ImGui.EndDisabled();
            }

            if (ImGui.Button("Import List From Clipboard (Artisan Export)".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30)))
            {
                try
                {
                    var clipboard = ImGui.GetClipboardText();
                    if (clipboard != string.Empty)
                    {
                        if (clipboard.TryParseJson<NewCraftingList>(out var import))
                        {
                            import.SetID();
                            import.Save(true);
                        }
                        else
                        {
                            Notify.Error("Invalid import string.".Loc());
                        }
                    }
                    else
                    {
                        Notify.Error("Clipboard is empty.".Loc());
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }

            ImGui.EndChild();

            ImGui.BeginChild("TeamCraftSection", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5f), false);
            Teamcraft.DrawTeamCraftListButtons();
            ImGui.EndChild();
        }

        public static void StartList()
        {
            CraftingListFunctions.Materials = null;
            CraftingListFunctions.CurrentIndex = 0;
            selectedList.ExpandedList.Clear();
            foreach (var r in selectedList.Recipes)
            {
                if (r.ListItemOptions == null)
                {
                    r.ListItemOptions = new();
                    P.Config.Save();
                }
                if (r.ListItemOptions.Skipping) continue;
                selectedList.ExpandedList.AddRange(Enumerable.Repeat(r.ID, r.Quantity));
            }

            if (P.ws.Windows.FindFirst(x => x.WindowName.Contains(selectedList.ID.ToString(), StringComparison.CurrentCultureIgnoreCase), out var window))
                window.IsOpen = false;


            CraftingListFunctions.ListEndTime = GetListTimer(selectedList);
            Crafting.CraftFinished += UpdateListTimer;
            Processing = true;
            Endurance.ToggleEndurance(false);
        }

        /// <remarks>
        /// 🔴 <c>Crafting.CraftFinished</c> 是從 <c>Crafting.Update()</c> 發出的,而那支掛在
        /// <c>Artisan.OnFrameworkUpdate</c> 上 ⇒ <b>進到這裡時已經在遊戲主執行緒上</b>。
        /// 所以能力值快照在 <c>Task.Run</c> <b>之前</b>就地讀好,背景那側只拿純量。
        /// </remarks>
        public static void UpdateListTimer(Recipe recipe, CraftState craft, StepState finalStep, bool cancelled)
        {
            var statsSnapshot = CrafterStatsSnapshot.Read();
            Task.Run(() =>
            {
                TimeSpan output = new();
                for (int i = CraftingListFunctions.CurrentIndex; i < selectedList.ExpandedList.Count; i++)
                {
                    var item = selectedList.ExpandedList[i];
                    var options = selectedList.Recipes.First(x => x.ID == item).ListItemOptions;
                    output = output.Add(GetCraftDuration(item, (options?.NQOnly ?? false), statsSnapshot)).Add(TimeSpan.FromSeconds(1));
                }

                CraftingListFunctions.ListEndTime = output;
            });
        }

        /// <param name="statsSnapshot">
        /// 見 <see cref="GetCraftDuration"/>:從背景執行緒呼叫時必須給,
        /// 否則底下會在錯的執行緒上解原生記憶體。
        /// </param>
        public static TimeSpan GetListTimer(NewCraftingList selectedList, CrafterStatsSnapshot? statsSnapshot = null)
        {
            TimeSpan output = new();
            try
            {
                if (selectedList is null) return output;
                foreach (var item in selectedList.Recipes.Distinct())
                {
                    if (item.ListItemOptions is null)
                    {
                        item.ListItemOptions = new();
                        P.Config.Save();
                    }
                    if (item.ListItemOptions.Skipping) continue;
                    var count = item.Quantity;
                    var options = item.ListItemOptions;
                    if (GetCraftDuration(item.ID, (options?.NQOnly ?? false), statsSnapshot) == TimeSpan.Zero)
                        output = TimeSpan.Zero;
                    else
                        output = output.Add(GetCraftDuration(item.ID, (options?.NQOnly ?? false), statsSnapshot) * count).Add(TimeSpan.FromSeconds(1 * count));
                }

                return output;
            }
            catch (Exception ex)
            {
                return output;
            }
        }

        /// <summary>
        /// 估算單一配方跑完要多久。
        /// </summary>
        /// <param name="statsSnapshot">
        /// 已經在遊戲主執行緒上讀好的能力值快照。<b>從背景執行緒呼叫時必須給</b> ——
        /// 給了之後這支完全不碰原生記憶體(其餘部分只讀 Lumina 資料表、<c>P.Config</c>
        /// 與純計算的模擬器／求解器)。傳 <c>default</c> 等於「就地讀」,那只能在主執行緒上。
        /// </param>
        /// <remarks>
        /// 🔴 不給快照時這支會解三處原生記憶體:
        /// <c>GetBaseStatsForClassHeuristic</c> → <c>RaptureGearsetModule.Instance()-&gt;Entries</c>
        /// ／<c>InventoryManager.Instance()-&gt;GetInventoryContainer</c>;
        /// <c>CharacterInfo.FCCraftsmanshipbuff.Param</c> → <c>Status.Struct-&gt;Param</c>;
        /// <c>BuildCraftStateForRecipe</c> → <c>PlayerState.Instance()-&gt;ClassJobLevels</c>。
        /// </remarks>
        public static TimeSpan GetCraftDuration(uint recipeId, bool qs, CrafterStatsSnapshot? statsSnapshot = null)
        {
            if (qs)
                return TimeSpan.FromSeconds(3);

            var recipe = LuminaSheets.RecipeSheet[recipeId];
            var config = P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId) ?? new();
            var craftType = recipe.CraftType.RowId;
            var job = (Job)((uint)Job.CRP + craftType);
            CharacterStats stats;
            if (statsSnapshot is null)
            {
                stats = CharacterStats.GetBaseStatsForClassHeuristic(job);
                stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ), new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
            }
            else
            {
                stats = statsSnapshot.StatsForCraftType(craftType);
                stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ), new(config.RequiredPotion, config.RequiredPotionHQ), statsSnapshot.FcCraftsmanshipParam);
            }
            var craft = Crafting.BuildCraftStateForRecipe(stats, job, recipe);
            var solver = CraftingProcessor.GetSolverForRecipe(config, craft).CreateSolver(craft);
            if (solver != null)
            {
                var time = SolverUtils.EstimateCraftTime(solver, craft, 0);

                return time;
            }
            return TimeSpan.Zero;
        }

        private static void DrawNewListPopup()
        {
            if (ImGui.BeginPopup("NewCraftingList"))
            {
                if (keyboardFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    keyboardFocus = false;
                }

                if (ImGui.InputText("List Name".Loc() + "###listName", ref newListName, 100, ImGuiInputTextFlags.EnterReturnsTrue) && newListName.Any())
                {
                    NewCraftingList newList = new();
                    newList.Name = newListName;
                    newList.SetID();
                    newList.Save(true);

                    newListName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        public static void AddAllSubcrafts(Recipe selectedRecipe, NewCraftingList selectedList, int amounts = 1, int loops = 1)
        {
            foreach (var subItem in selectedRecipe.Ingredients().Where(x => x.Amount > 0))
            {
                var subRecipe = CraftingListHelpers.GetIngredientRecipe(subItem.Item.RowId);
                if (subRecipe != null)
                {
                    AddAllSubcrafts(subRecipe.Value, selectedList, subItem.Amount * amounts, loops);

                    var quant = Math.Ceiling(subItem.Amount / (double)subRecipe.Value.AmountResult * loops * amounts);
                    if (selectedList.Recipes.Any(x => x.ID == subRecipe.Value.RowId))
                    {
                        selectedList.Recipes.First(x => x.ID == subRecipe.Value.RowId).Quantity += (int)quant;
                    }
                    else
                    {
                        Svc.Log.Debug($"Adding as new {subRecipe.Value.RowId.NameOfRecipe()}");
                        selectedList.Recipes.Add(new() { ID = subRecipe.Value.RowId, Quantity = (int)quant });
                    }
                }
            }
        }


        private static void AddRecipeIngredientsToList(Recipe? recipe, ref List<int> ingredientList, bool addSubList = true, NewCraftingList? selectedList = null)
        {
            try
            {
                if (recipe == null) return;

                foreach (var ing in recipe.Value.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0))
                {
                    var name = LuminaSheets.ItemSheet[ing.Item.RowId].Name.ToString();
                    CraftingListHelpers.SelectedRecipesCraftable[ing.Item.RowId] = LuminaSheets.RecipeSheet!.Any(x => x.Value.ItemResult.Value.Name.ToDalamudString().ToString() == name);

                    for (int i = 1; i <= ing.Amount; i++)
                    {
                        ingredientList.Add((int)ing.Item.RowId);
                        if (CraftingListHelpers.GetIngredientRecipe(ing.Item.RowId).Value.RowId != 0 && addSubList)
                        {
                            AddRecipeIngredientsToList(CraftingListHelpers.GetIngredientRecipe(ing.Item.RowId), ref ingredientList);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "ERROR");
            }
        }


        /// <summary>
        /// 這個配方的材料夠不夠(「只顯示做得出來的」那個篩選用的判斷)。
        /// </summary>
        /// <param name="inventorySnapshot">
        /// 道具 id → 背包持有量(NQ+HQ)的純量快照,由呼叫端在遊戲主執行緒上
        /// <see cref="SnapshotCraftableCheckCounts"/> 讀好。
        /// <b>從背景執行緒呼叫時必須給</b>;給了之後這支完全不碰原生記憶體。
        /// 傳 <c>null</c> 時逐個材料走閘門讀取(已經在主執行緒上就是就地讀,零額外成本)。
        /// </param>
        /// <param name="retainerRoster">
        /// 僱員名冊快照,見 <see cref="RetainerInfo.ReadRetainerRoster"/>。
        /// 只有 <paramref name="checkRetainer"/> 為真時才用得到;給了之後
        /// <c>GetRetainerItemCount</c> 就不必自己再往返主執行緒一次。
        /// </param>
        /// <remarks>
        /// 🔴 <c>invManager-&gt;GetInventoryItemCount</c> 是原生解參考,只能在遊戲主執行緒上讀。
        /// ⚠️ 這裡的 <c>GetInventoryItemCount(id)</c> 走的是<b>預設參數</b>
        /// (<c>checkEquipped: true, checkArmory: true</c>),與 <see cref="NumberOfIngredient"/>
        /// 的 <c>(id, false, false, false)</c> <b>不是同一個數</b> ——
        /// 所以兩者各有自己的快照函式,不可以互相代用。
        /// </remarks>
        public static bool CheckForIngredients(Recipe recipe, bool fetchFromCache = true, bool checkRetainer = false,
            IReadOnlyDictionary<uint, int>? inventorySnapshot = null, RetainerInfo.RetainerRoster? retainerRoster = null)
        {
            if (fetchFromCache)
                if (CraftableItems.TryGetValue(recipe, out bool canCraft)) return canCraft;

            foreach (var value in recipe.Ingredients().Where(x => x.Item.RowId != 0 && x.Amount > 0))
            {
                try
                {
                    int owned = inventorySnapshot is not null
                        ? inventorySnapshot.GetValueOrDefault(value.Item.RowId)
                        : CraftableCheckCount(value.Item.RowId);

                    if (!checkRetainer)
                    {
                        if (value.Amount > owned)
                        {
                            CraftableItems[recipe] = false;
                            return false;
                        }
                    }
                    else
                    {
                        int retainerCount = RetainerInfo.GetRetainerItemCount(value.Item.RowId, roster: retainerRoster);
                        if (value.Amount > owned + retainerCount)
                        {

                            CraftableItems[recipe] = false;
                            return false;
                        }
                    }
                }
                catch
                {

                }

            }

            CraftableItems[recipe] = true;
            return true;
        }

        /// <summary>
        /// <see cref="CheckForIngredients"/> 用的那一種持有量:<c>GetInventoryItemCount</c>
        /// 的<b>預設參數</b>(含裝備中與裝備箱),NQ ＋ HQ。
        /// </summary>
        /// <remarks>
        /// 🔴 與 <see cref="NumberOfIngredient"/> 的 <c>(id, false, false, false)</c>
        /// <b>不是同一個數</b>,兩者不可互相代用(CLAUDE.md:抄先例要逐參數比對,
        /// 尤其是有預設值可省略的參數)。
        /// </remarks>
        private static unsafe int CraftableCheckCountCore(uint itemId)
            => invManager->GetInventoryItemCount(itemId) + invManager->GetInventoryItemCount(itemId, true);

        /// <summary>單一道具:走主執行緒閘門的 <see cref="CraftableCheckCountCore"/>。</summary>
        internal static int CraftableCheckCount(uint itemId)
            => IpcFrameworkGate.Get(CraftableCheckEndpoint, () => CraftableCheckCountCore(itemId), 0);

        private const string CraftableCheckEndpoint = "CraftingListUI.CheckForIngredients(背包持有量)";

        /// <summary>
        /// 一次把很多道具的持有量讀成純量快照,<b>整批只付一次主執行緒往返</b>。
        /// </summary>
        /// <remarks>
        /// 🔑 為什麼要批次而不是逐個走閘門:<c>RunOnFrameworkThread</c> 從背景執行緒呼叫時
        /// 要等到下一次 <c>Framework.Update</c> ⇒ 一次往返大約一幀。
        /// ⚠️ 取不到(卸載期／等逾時)時回<b>已經讀到的部分</b>,缺的鍵查出來是 0 ——
        /// 與本來的 <c>catch { }</c> 同方向:少報只會讓配方被判成「材料不足」而不顯示,
        /// 不會讓流程拿著不存在的材料去開工。
        /// </remarks>
        internal static Dictionary<uint, int> SnapshotCraftableCheckCounts(IEnumerable<uint> itemIds, int batchSize = 512)
            => SnapshotCounts(itemIds, CraftableCheckEndpoint, CraftableCheckCountCore, batchSize);

        /// <summary>
        /// 一次把很多道具的 <see cref="NumberOfIngredient"/> 讀成純量快照,
        /// <b>整批只付一次主執行緒往返</b>。語意與 <see cref="NumberOfIngredient"/> 逐字相同。
        /// </summary>
        internal static Dictionary<uint, int> SnapshotNumberOfIngredient(IEnumerable<uint> itemIds, int batchSize = 512)
            => SnapshotCounts(itemIds, NumberOfIngredientEndpoint, NumberOfIngredientCore, batchSize);

        /// <summary>
        /// 上面兩支共用的批次讀取。<paramref name="readOne"/> 只會在遊戲主執行緒上被呼叫。
        /// </summary>
        /// <remarks>
        /// 🔴🔴 閘門裡的委派<b>絕不可以就地寫外面那個 <c>result</c> 字典</b>。
        /// 那種寫法下呼叫端會在主執行緒還在寫同一個 <c>Dictionary</c> 時繼續往前跑,
        /// 而裸 <c>Dictionary</c> 並行改動的失敗形式<b>不是「拿到舊值」而是字典本身壞掉</b>。
        /// 🔑 所以每一批在閘門內只寫<b>自己的區域陣列</b>再整份回傳,由呼叫端在
        /// <c>Task.WaitAny</c> 回來之後才合併;逾時時回 <c>null</c>,那一批被丟掉,
        /// 被遺棄的委派只會寫它自己那個沒人看的陣列。
        /// </remarks>
        private static Dictionary<uint, int> SnapshotCounts(IEnumerable<uint> itemIds, string endpoint, Func<uint, int> readOne, int batchSize)
        {
            if (batchSize < 1) batchSize = 1;
            var result = new Dictionary<uint, int>();
            var seen = new HashSet<uint>();
            var batch = new List<uint>(batchSize);

            void FlushBatch()
            {
                if (batch.Count == 0) return;
                var pending = batch.ToArray();
                batch.Clear();

                var values = IpcFrameworkGate.Get<int[]?>(endpoint, () =>
                {
                    var read = new int[pending.Length];
                    for (var i = 0; i < pending.Length; i++)
                        read[i] = readOne(pending[i]);
                    return read;
                }, null);

                if (values is null) return;
                for (var i = 0; i < pending.Length; i++)
                    result[pending[i]] = values[i];
            }

            foreach (var id in itemIds)
            {
                if (id == 0 || !seen.Add(id)) continue;
                batch.Add(id);
                if (batch.Count >= batchSize) FlushBatch();
            }
            FlushBatch();
            return result;
        }

        private const string NumberOfIngredientEndpoint = "CraftingListUI.NumberOfIngredient";

        /// <summary>
        /// 背包(不含裝備中與裝備箱)持有量;收藏品另外逐格數。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>invManager</c> 是遊戲的原生指標,<c>GetInventoryItemCount</c>／
        /// <c>GetInventoryContainer</c>／<c>GetInventorySlot</c> 只能在遊戲主執行緒上讀。
        /// 熱迴圈要避免「每個道具一次主執行緒往返」時改用
        /// <see cref="SnapshotNumberOfIngredient"/>。
        /// ⚠️ 取不到時回 <c>0</c> —— 與本來的 <c>catch { return 0; }</c> 同值,而呼叫端一律是
        /// 「持有量 &gt;= 需求量?」的閘門 ⇒ 少報只會讓流程判定素材不足而不開工。
        /// </remarks>
        public static int NumberOfIngredient(uint ingredient)
            => IpcFrameworkGate.Get(NumberOfIngredientEndpoint, () => NumberOfIngredientCore(ingredient), 0);

        /// <summary><b>只能在遊戲主執行緒上呼叫</b>,由 <see cref="NumberOfIngredient"/> 那一層的閘門保證。</summary>
        private static unsafe int NumberOfIngredientCore(uint ingredient)
        {
            try
            {
                var invNumberNQ = invManager->GetInventoryItemCount(ingredient, false, false, false);
                var invNumberHQ = invManager->GetInventoryItemCount(ingredient, true, false, false);

                if (LuminaSheets.ItemSheet[ingredient].AlwaysCollectable)
                {
                    var inventories = new List<InventoryType>
                                          {
                                              InventoryType.Inventory1,
                                              InventoryType.Inventory2,
                                              InventoryType.Inventory3,
                                              InventoryType.Inventory4,
                                          };

                    foreach (var inv in inventories)
                    {
                        var container = invManager->GetInventoryContainer(inv);
                        // 讀不到就跳過這一頁。這個迴圈只會「加上去」，少加＝少報，
                        // 呼叫端一律是「持有量 >= 需求量?」的閘門，少報會讓流程判定素材不足而不開工，
                        // 多報則會讓它拿著不存在的素材去製作。
                        // ⚠️ 外層的 try/catch 雖然接得住 NRE，但它是整個函式回 0，
                        //    連前面 GetInventoryItemCount 已經算好的 NQ/HQ 也一起丟掉。
                        if (container == null || container->Items == null)
                            continue;
                        for (int i = 0; i < container->Size; i++)
                        {
                            var item = container->GetInventorySlot(i);

                            if (item != null && item->ItemId == ingredient)
                                invNumberNQ++;
                        }
                    }
                }

                return invNumberHQ + invNumberNQ;
            }
            catch
            {
                return 0;
            }
        }




        public static Recipe? GetIngredientRecipe(string ingredient)
        {
            return LuminaSheets.RecipeSheet.Values.Any(x => x.ItemResult.Value.Name.ToDalamudString().ToString() == ingredient) ? LuminaSheets.RecipeSheet.Values.First(x => x.ItemResult.Value.Name.ToDalamudString().ToString() == ingredient) : null;
        }
    }
}
