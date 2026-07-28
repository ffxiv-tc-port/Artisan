using Artisan.RawInformation;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using PunishLib.ImGuiMethods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Artisan.CraftingLists
{
    internal static class Teamcraft
    {
        internal static string importListName = "";
        internal static string importListLink = "";
        internal static string importListPreCraft = "";
        internal static string importListItems = "";
        internal static bool openImportWindow = false;
        private static bool precraftQS = false;
        private static bool finalitemQS = false;
        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static bool fetchingList = false;

        internal static void DrawTeamCraftListButtons()
        {
            string labelText = "Teamcraft Lists".Loc();
            var labelLength = ImGui.CalcTextSize(labelText);
            ImGui.SetCursorPosX((ImGui.GetContentRegionMax().X - labelLength.X) * 0.5f);
            ImGui.TextColored(ImGuiColors.ParsedGreen, labelText);
            if (IconButtons.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Download, "Import".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30)))
            {
                openImportWindow = true;
            }
            OpenTeamcraftImportWindow();
            if (CraftingListUI.selectedList.ID != 0)
            {
                if (IconButtons.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Upload, "Export".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30), true))
                {
                    ExportSelectedListToTC();
                }

                if (IconButtons.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Paste, "Merge List From Clipboard (Teamcraft Export/Link)".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 30), true))
                {
                    MergeClipboardIntoSelectedList();
                }
            }
        }

        internal static void MergeClipboardIntoSelectedList()
        {
            if (CraftingListUI.selectedList.ID == 0)
            {
                Notify.Error("Please select a list first.".Loc());
                return;
            }

            var clipboard = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(clipboard))
            {
                Notify.Error("Clipboard is empty.".Loc());
                return;
            }

            if (TryGetListShareUid(clipboard, out var uid))
            {
                var targetId = CraftingListUI.selectedList.ID;
                FetchListShareItemsAsync(uid, entries =>
                {
                    var target = P.Config.NewCraftingLists.FirstOrDefault(x => x.ID == targetId);
                    if (target == null) return;
                    var before = target.Recipes.Sum(x => x.Quantity);
                    MergeShareEntriesIntoList(entries, target, P.Config.DefaultListQuickSynth, P.Config.DefaultListQuickSynth);
                    if (target.Recipes.Sum(x => x.Quantity) == before)
                    {
                        Notify.Error("The Teamcraft list contains no craftable items.".Loc());
                        return;
                    }
                    P.Config.Save();
                    Notify.Success("Merged clipboard items into the current list.".Loc());
                });
                return;
            }

            var before = CraftingListUI.selectedList.Recipes.Sum(x => x.Quantity);
            if (clipboard.Contains("/import/", StringComparison.OrdinalIgnoreCase))
                MergeTeamcraftLinkIntoList(clipboard, CraftingListUI.selectedList, P.Config.DefaultListQuickSynth);
            else
                MergeLinesIntoList(clipboard, CraftingListUI.selectedList, P.Config.DefaultListQuickSynth);
            var after = CraftingListUI.selectedList.Recipes.Sum(x => x.Quantity);

            if (after == before)
            {
                Notify.Error("The clipboard contains no recognisable items. Please check the format.".Loc());
                return;
            }

            P.Config.Save();
            Notify.Success("Merged clipboard items into the current list.".Loc());
        }

        private static void ExportSelectedListToTC()
        {
            string baseUrl = "https://ffxivteamcraft.com/import/";
            string exportItems = "";

            var sublist = CraftingListUI.selectedList.Recipes.Distinct().Reverse().ToList();
            for (int i = 0; i < sublist.Count; i++)
            {
                if (i >= sublist.Count) break;

                int number = CraftingListUI.selectedList.Recipes[i].Quantity;
                var recipe = LuminaSheets.RecipeSheet[sublist[i].ID];
                var ItemId = recipe.ItemResult.Value.RowId;

                Svc.Log.Debug($"{recipe.ItemResult.Value.Name.ToDalamudString().ToString()} {sublist.Count}");
                ExtractRecipes(sublist, recipe);
            }

            foreach (var item in sublist)
            {
                int number = item.Quantity;
                var recipe = LuminaSheets.RecipeSheet[item.ID];
                var ItemId = recipe.ItemResult.Value.RowId;

                exportItems += $"{ItemId},null,{number};";
            }

            exportItems = exportItems.TrimEnd(';');

            var plainTextBytes = Encoding.UTF8.GetBytes(exportItems);
            string base64 = Convert.ToBase64String(plainTextBytes);

            Svc.Log.Debug($"{baseUrl}{base64}");
            ImGui.SetClipboardText($"{baseUrl}{base64}");
            Notify.Success("Link copied to clipboard".Loc());
        }

        private static void ExtractRecipes(List<ListItem> sublist, Recipe recipe)
        {
            foreach (var ing in recipe.Ingredients().Where(x => x.Amount > 0))
            {
                var subRec = CraftingListHelpers.GetIngredientRecipe(ing.Item.RowId);
                if (subRec != null)
                {
                    if (sublist.Any(x => x.ID == subRec.Value.RowId))
                    {
                        foreach (var subIng in subRec.Value.Ingredients().Where(x => x.Amount > 0))
                        {
                            var subSubRec = CraftingListHelpers.GetIngredientRecipe(subIng.Item.RowId);
                            if (subSubRec != null)
                            {
                                if (sublist.Any(x => x.ID == subSubRec.Value.RowId))
                                {
                                    sublist.RemoveAll(x => x.ID == subSubRec.Value.RowId);
                                }
                            }
                        }

                        sublist.RemoveAll(x => x.ID == subRec.Value.RowId);
                    }
                }
            }
        }

        private static void OpenTeamcraftImportWindow()
        {
            if (!openImportWindow) return;


            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.2f, 0.1f, 0.2f, 1f));
            ImGui.SetNextWindowSize(new Vector2(1, 1), ImGuiCond.Appearing);
            if (ImGui.Begin("Teamcraft Import".Loc() + "###TCImport", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("List Name".Loc());
                ImGui.SameLine();
                ImGuiComponents.HelpMarker(("Guide to importing lists.\n\n" +
                    "Option A: Paste your Teamcraft list's URL (https://ffxivteamcraft.com/list/...) into the Teamcraft Link box. The list is fetched from Teamcraft directly, including pre-crafts, so this works no matter which language Teamcraft is displaying.\n\n" +
                    "Option B: Use Teamcraft's \"Copy as Text\" button on the pre crafts and final items sections, and paste them into the boxes below. Item names in English, Simplified Chinese or the game client's language are all recognised.\n\n" +
                    "Give your list a name and click import.").Loc());
                ImGui.InputText("###ImportListName", ref importListName, 50);
                ImGui.Text("Teamcraft Link".Loc());
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("Accepts a Teamcraft list URL (ffxivteamcraft.com/list/...), fetched online with pre-crafts included, or an import link (ffxivteamcraft.com/import/...), decoded offline. Items are matched by ID, so any Teamcraft display language works.".Loc());
                ImGui.InputText("###ImportListLink", ref importListLink, 5000);
                ImGui.Text("Pre-craft Items".Loc());
                ImGui.InputTextMultiline("###PrecraftItems", ref importListPreCraft, 5000000, new Vector2(ImGui.GetContentRegionAvail().X, 100));

                if (!P.Config.DefaultListQuickSynth)
                    ImGui.Checkbox("Import as Quick Synth".Loc() + "###ImportQSPre", ref precraftQS);
                else
                    ImGui.TextWrapped("These items will try to be added as quick synth due to the default setting being enabled.".Loc());
                ImGui.Text("Final Items".Loc());
                ImGui.InputTextMultiline("###FinalItems", ref importListItems, 5000000, new Vector2(ImGui.GetContentRegionAvail().X, 100));
                if (!P.Config.DefaultListQuickSynth)
                    ImGui.Checkbox("Import as Quick Synth".Loc() + "###ImportQSFinal", ref finalitemQS);
                else
                    ImGui.TextWrapped("These items will try to be added as quick synth due to the default setting being enabled.".Loc());

                try
                {
                    if (ImGui.Button("Import".Loc()))
                    {
                        if (TryGetListShareUid(importListLink, out var uid))
                        {
                            var name = importListName;
                            var precraftText = importListPreCraft;
                            var finalText = importListItems;
                            var preQS = precraftQS;
                            var finalQS = finalitemQS;
                            FetchListShareItemsAsync(uid, entries =>
                            {
                                var list = new NewCraftingList { Name = name };
                                MergeShareEntriesIntoList(entries, list, preQS, finalQS);
                                MergeLinesIntoList(precraftText, list, preQS);
                                MergeLinesIntoList(finalText, list, finalQS);
                                if (list.Recipes.Count == 0)
                                {
                                    Notify.Error("The imported list has no items. Please check your import and try again.".Loc());
                                    return;
                                }
                                if (GenericHelpers.IsNullOrEmpty(list.Name))
                                    list.Name = list.Recipes.FirstOrDefault().ID.NameOfRecipe();
                                list.SetID();
                                list.Save();
                                Notify.Success("List imported from Teamcraft.".Loc());
                            });
                            openImportWindow = false;
                            importListName = "";
                            importListLink = "";
                            importListPreCraft = "";
                            importListItems = "";
                        }
                        else
                        {
                            NewCraftingList? importedList = ParseImport(precraftQS, finalitemQS);
                            if (importedList is not null)
                            {
                                if (GenericHelpers.IsNullOrEmpty(importedList.Name))
                                    importedList.Name = importedList.Recipes.FirstOrDefault().ID.NameOfRecipe();
                                importedList.SetID();
                                importedList.Save();
                                openImportWindow = false;
                                importListName = "";
                                importListLink = "";
                                importListPreCraft = "";
                                importListItems = "";

                            }
                            else
                            {
                                Notify.Error("The imported list has no items. Please check your import and try again.".Loc());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel".Loc()))
                {
                    openImportWindow = false;
                    importListName = "";
                    importListLink = "";
                    importListPreCraft = "";
                    importListItems = "";
                }
                ImGui.End();
            }
            ImGui.PopStyleColor();
        }

        private static NewCraftingList? ParseImport(bool precraftQS, bool finalitemQS)
        {
            if (string.IsNullOrEmpty(importListName) && string.IsNullOrEmpty(importListLink) && string.IsNullOrEmpty(importListItems) && string.IsNullOrEmpty(importListPreCraft)) return null;
            NewCraftingList output = new NewCraftingList();
            output.Name = importListName;
            MergeTeamcraftLinkIntoList(importListLink, output, finalitemQS);
            MergeLinesIntoList(importListPreCraft, output, precraftQS);
            MergeLinesIntoList(importListItems, output, finalitemQS);

            if (output.Recipes.Count == 0) return null;

            return output;
        }

        // Parses Teamcraft's "copy as text" format ("NxItem Name" per line) and merges
        // the parsed quantities into an existing list, rather than always building a new one -
        // shared by the full-list import window and the "merge from clipboard" button.
        private static void MergeLinesIntoList(string text, NewCraftingList target, bool quickSynth)
        {
            using System.IO.StringReader reader = new(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                if (parts[0][^1] != 'x')
                    continue;

                int numberOfItem = int.Parse(parts[0].Substring(0, parts[0].Length - 1));
                var builder = new StringBuilder();
                for (int i = 1; i < parts.Length; i++)
                {
                    builder.Append(parts[i]);
                    builder.Append(" ");
                }
                var item = builder.ToString().Trim();
                if (DebugTab.Debug) Svc.Log.Debug($"{numberOfItem} x {item}");

                var recipe = GenericHelpers.FindRow<Recipe>(x => x.ItemResult.ValueNullable?.RowId > 0 && x.ItemResult.ValueNullable?.Name.ToDalamudString().ToString() == item);
                if (recipe is null && ForeignItemNames.TryGetItemId(item, out var foreignId))
                    recipe = GenericHelpers.FindRow<Recipe>(x => x.ItemResult.ValueNullable?.RowId == foreignId);
                if (recipe?.RowId > 0)
                    AddRecipeToList(target, recipe.Value, numberOfItem, quickSynth);
            }
        }

        // Resolves a Teamcraft list share URL (ffxivteamcraft.com/list/<uid>) by reading the
        // public Firestore document behind it - the URL itself carries no item data, unlike
        // /import/ links, so this path needs one online request.
        private static bool TryGetListShareUid(string text, out string uid)
        {
            uid = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Regex.Match(text, @"ffxivteamcraft\.com/list/([A-Za-z0-9_-]+)");
            if (!m.Success) return false;
            uid = m.Groups[1].Value;
            return true;
        }

        private static void FetchListShareItemsAsync(string uid, Action<List<(uint ItemId, int Amount, bool Precraft)>> onSuccess)
        {
            if (fetchingList)
            {
                Notify.Error("Already fetching a list from Teamcraft, please wait.".Loc());
                return;
            }
            fetchingList = true;
            Notify.Info("Fetching list from Teamcraft...".Loc());
            Task.Run(async () =>
            {
                try
                {
                    var json = await httpClient.GetStringAsync($"https://firestore.googleapis.com/v1/projects/ffxivteamcraft/databases/(default)/documents/lists/{uid}");
                    var fields = JObject.Parse(json)["fields"];
                    var entries = new List<(uint ItemId, int Amount, bool Precraft)>();
                    AddShareArrayEntries(fields?["items"], true, entries);
                    AddShareArrayEntries(fields?["finalItems"], false, entries);
                    await Svc.Framework.RunOnFrameworkThread(() => onSuccess(entries));
                }
                catch (Exception ex)
                {
                    ex.Log();
                    await Svc.Framework.RunOnFrameworkThread(() => Notify.Error("Failed to fetch the list from Teamcraft. Check the link is correct and the list is public.".Loc()));
                }
                finally
                {
                    fetchingList = false;
                }
            });
        }

        private static void AddShareArrayEntries(JToken? array, bool precraft, List<(uint ItemId, int Amount, bool Precraft)> entries)
        {
            if (array?["arrayValue"]?["values"] is not JArray values) return;
            foreach (var value in values)
            {
                var fields = value["mapValue"]?["fields"];
                if (fields == null) continue;
                if (!uint.TryParse((string?)fields["id"]?["integerValue"], out var itemId) || itemId == 0) continue;
                if (!int.TryParse((string?)fields["amount"]?["integerValue"], out var amount) || amount <= 0) continue;
                entries.Add((itemId, amount, precraft));
            }
        }

        // Non-craftable entries (crystals, gathered materials) simply find no recipe and are
        // skipped; craftable entries from the "items" array become pre-crafts ahead of finals.
        private static void MergeShareEntriesIntoList(List<(uint ItemId, int Amount, bool Precraft)> entries, NewCraftingList target, bool precraftQS, bool finalQS)
        {
            foreach (var (itemId, amount, precraft) in entries)
            {
                var recipe = GenericHelpers.FindRow<Recipe>(x => x.ItemResult.ValueNullable?.RowId == itemId);
                if (recipe?.RowId > 0)
                    AddRecipeToList(target, recipe.Value, amount, precraft ? precraftQS : finalQS);
            }
        }

        // Parses Teamcraft's import-link format (https://ffxivteamcraft.com/import/<base64>,
        // decoding to "itemId,recipeId|null,quantity;...") and merges it into an existing list.
        // Matching is by item ID rather than name, so it works regardless of the game client's
        // data language - names exported from an English Teamcraft cannot match TC-client sheets.
        internal static void MergeTeamcraftLinkIntoList(string text, NewCraftingList target, bool quickSynth)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!TryExtractLinkPayload(text, out var payload)) return;

            foreach (var entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = entry.Split(',');
                if (fields.Length < 2) continue;

                if (!long.TryParse(fields[0].Trim(), out var rawItemId)) continue;
                var itemId = (uint)Math.Abs(rawItemId);
                if (!int.TryParse(fields[^1].Trim(), out var numberOfItem) || numberOfItem <= 0) continue;
                if (DebugTab.Debug) Svc.Log.Debug($"{numberOfItem} x item#{itemId}");

                var recipe = GenericHelpers.FindRow<Recipe>(x => x.ItemResult.ValueNullable?.RowId == itemId && itemId > 0);
                if (recipe?.RowId > 0)
                    AddRecipeToList(target, recipe.Value, numberOfItem, quickSynth);
            }
        }

        private static bool TryExtractLinkPayload(string text, out string payload)
        {
            payload = "";
            var t = text.Trim();
            var idx = t.IndexOf("/import/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                t = t.Substring(idx + "/import/".Length);

            foreach (var cut in new[] { '?', '#' })
            {
                var c = t.IndexOf(cut);
                if (c >= 0) t = t.Substring(0, c);
            }

            // Tolerate URL-encoded and URL-safe base64 variants with stripped padding.
            t = Uri.UnescapeDataString(t).Trim().Replace('-', '+').Replace('_', '/');
            if (t.Length == 0) return false;
            if (t.Length % 4 != 0) t = t.PadRight(t.Length + (4 - t.Length % 4), '=');

            try
            {
                payload = Encoding.UTF8.GetString(Convert.FromBase64String(t));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AddRecipeToList(NewCraftingList target, Recipe recipe, int numberOfItem, bool quickSynth)
        {
            int quantity = (int)Math.Ceiling(numberOfItem / (double)recipe.AmountResult);
            if (target.Recipes.Any(x => x.ID == recipe.RowId))
                target.Recipes.First(x => x.ID == recipe.RowId).Quantity += quantity;
            else
                target.Recipes.Add(new ListItem() { ID = recipe.RowId, Quantity = quantity, ListItemOptions = new() });

            if (quickSynth && recipe.CanQuickSynth)
                target.Recipes.First(x => x.ID == recipe.RowId).ListItemOptions.NQOnly = true;
        }
    }
}
