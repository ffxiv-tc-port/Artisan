using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic.Solvers
{
    public class RaphaelSolverDefintion : ISolverDefinition
    {
        public Solver Create(CraftState craft, int flavour)
        {
            var key = RaphaelCache.GetKey(craft);
            if (RaphaelCache.HasSolution(craft, out var output))
            {
                // a macro plays back a fixed action list and cannot react to good/excellent/poor showing up;
                // wrap it so we can deviate opportunistically, but only when the simulator says the deviation
                // still finishes the craft with strictly better quality than the untouched plan
                //
                // 再包一層奇蹟之材:Raphael 的狀態模型只有 通常/高品質/最高品質/低品質,
                // 連「大進展」這類專家狀態都表達不出來,所以它永遠不會提議 41269。
                // 而奇蹟之材是免費動作(不佔回合、不耗 CP/耐久、不讓 buff 掉一格),
                // 插在計畫前面不會動到計畫本身 —— 詳見 MaterialMiracleSolver 的類別註解。
                // ⚠️ 順序是刻意的:奇蹟之材在最外層,底下才是機會性偏離,
                //    這樣閘門的 rollout 驅動的是「真正會執行的那份計畫」。
                return new MaterialMiracleSolver(new OpportunisticSolver(new MacroSolver(output!, craft)));
            }
            return craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
        }

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            if (RaphaelCache.HasSolution(craft, out var solution))
            {
                yield return new(this, 3, 0, "Raphael Recipe Solver".Loc());
            }
            else if (!P.Config.RaphaelSolverConfig.AllowFallbackToStandard)
            {
                // Yielding an *unsupported* flavour rather than nothing at all: GetAvailableSolversForRecipe
                // still filters this out of every picker (it passes returnUnsupported: false), but FindSolver
                // now returns it for a recipe explicitly assigned to Raphael, so CraftingProcessor raises
                // SolverFailed with this reason instead of silently starting on the standard solver.
                yield return new(this, 3, 0, "Raphael Recipe Solver".Loc(),
                    RaphaelCache.DescribeIgnoredSolution(craft) is { Length: > 0 } why
                        ? "No Raphael solution matches your current stats: ??".Loc(why)
                        : "No Raphael solution has been generated for this recipe.".Loc());
            }
        }
    }

    internal static class RaphaelCache
    {
        internal static readonly ConcurrentDictionary<string, Tuple<CancellationTokenSource, Task>> Tasks = [];
        [NonSerialized]
        public static Dictionary<string, RaphaelSolutionConfig> TempConfigs = new();

        public static void Build(CraftState craft, RaphaelSolutionConfig config)
        {
            var key = GetKey(craft);

            if (CLIExists() && !Tasks.ContainsKey(key))
            {
                P.Config.RaphaelSolverCacheV3.TryRemove(key, out _);

                Svc.Log.Information("Spawning Raphael process");

                var manipulation = craft.UnlockedManipulation ? "--manipulation" : "";
                var itemText = $"--recipe-id {craft.RecipeId}";
                var extraArgsBuilder = new StringBuilder();

                extraArgsBuilder.Append($"--initial {craft.InitialQuality} "); // must always have a space after

                if (config.EnsureReliability)
                {
                    Svc.Log.Error("Ensuring reliability is enabled, this may take a while. NO SUPPORT GIVEN IF ENABLED.");
                    extraArgsBuilder.Append($"--adversarial "); // must always have a space after
                }

                if (config.BackloadProgress)
                {
                    extraArgsBuilder.Append($"--backload-progress "); // must always have a space after
                }

                if (config.HeartAndSoul)
                {
                    extraArgsBuilder.Append($"--heart-and-soul "); // must always have a space after
                }

                if (config.QuickInno)
                {
                    extraArgsBuilder.Append($"--quick-innovation "); // must always have a space after
                }

                if (P.Config.RaphaelSolverConfig.MaximumThreads > 0)
                {
                    extraArgsBuilder.Append($"--threads {P.Config.RaphaelSolverConfig.MaximumThreads} "); // must always have a space after
                }

                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"),
                        Arguments = $"solve {itemText} {manipulation} --level {craft.StatLevel} --stats {craft.StatCraftsmanship} {craft.StatControl} {craft.StatCP} {extraArgsBuilder} --output-variables ids", // Command to execute
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                Svc.Log.Information(process.StartInfo.Arguments);

                var cts = new CancellationTokenSource();
                cts.Token.Register(() => { process.Kill(); Tasks.Remove(key, out var _); });
                cts.CancelAfter(TimeSpan.FromMinutes(P.Config.RaphaelSolverConfig.TimeOutMins));

                var task = Task.Run(() =>
                {
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd().Trim();
                    if (process.ExitCode != 0)
                    {
                        DuoLog.Error(error.Split('\r', '\n')[1]);
                        cts.Cancel();
                        return;
                    }
                    var rng = new Random();
                    var ID = rng.Next(50001, 10000000);
                    while (P.Config.RaphaelSolverCacheV3.Any(kv => kv.Value.ID == ID))
                        ID = rng.Next(50001, 10000000);

                    var cleansedOutput = output.Replace("[", "").Replace("]", "").Replace("\"", "").Split(", ").Select(x => int.TryParse(x, out int n) ? n : 0);
                    P.Config.RaphaelSolverCacheV3[key] = new MacroSolverSettings.Macro()
                    {
                        ID = ID,
                        Name = key,
                        Steps = MacroUI.ParseMacro(cleansedOutput),
                        Options = new()
                        {
                            SkipQualityIfMet = false,
                            // 🔴 這兩個維持 false 是刻意的,2026-08-06 用離線量測台實測過(每格 3000 次製作,
                            //    台服 rlvl740 一般配方 #36073 / #36062,三種能力值檔位):
                            //      打開 Upgrade → 做出來率 100% 掉到 58~85%,期望品質 96.6 掉到 56.1
                            //    原因是 Raphael 的解把 CP 與耐久算得剛剛好,把某一步換成
                            //    集中加工/集中製作會改變消耗,整份計畫的預算就崩了。
                            //    要在好/高品質狀態撿便宜,正確做法是 OpportunisticSolver ——
                            //    它會先模擬「偏離之後剩下整段還跑不跑得完」再決定,實測 +0.5~2.8 期望品質、
                            //    做出來率完全不變(100% → 100%)。
                            UpgradeProgressActions = false,
                            UpgradeQualityActions = false,
                            MinCP = craft.StatCP,
                            MinControl = craft.StatControl,
                            MinCraftsmanship = craft.StatCraftsmanship,
                        }
                    };

                    cts.Token.ThrowIfCancellationRequested();
                    if (P.Config.RaphaelSolverCacheV3[key] == null || P.Config.RaphaelSolverCacheV3[key].Steps.Count == 0)
                    {
                        Svc.Log.Error($"Raphael failed to generate a valid macro. This could be one of the following reasons:" +
                            $"\n- If you are not running Windows, Raphael may not be compatible with your OS." +
                            $"\n- You cancelled the generation." +
                            $"\n- Raphael just gave up after not finding a result.{(P.Config.RaphaelSolverConfig.AutoGenerate ? "\nAutomatic generation will be disabled as a result." : "")}");
                        P.Config.RaphaelSolverConfig.AutoGenerate = false;
                        cts.Cancel();
                        return;
                    }


                    if (P.Config.RaphaelSolverConfig.AutoSwitch)
                    {
                        if (!P.Config.RaphaelSolverConfig.AutoSwitchOnAll)
                        {
                            Svc.Log.Debug("Switching to Raphael solver");
                            var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == "Raphael Recipe Solver".Loc());
                            if (opt is not null)
                            {
                                var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                                P.Config.RecipeConfigs[craft.Recipe.RowId] = config;
                            }
                        }
                        else
                        {
                            var crafts = AllValidCrafts(key, craft.Recipe.CraftType.RowId).ToList();
                            Svc.Log.Debug($"Applying solver to {crafts.Count()} recipes.");
                            var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == "Raphael Recipe Solver".Loc());
                            if (opt is not null)
                            {
                                var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                                foreach (var c in crafts)
                                {
                                    Svc.Log.Debug($"Switching {c.Recipe.RowId} ({c.Recipe.ItemResult.Value.Name}) to Raphael solver");
                                    P.Config.RecipeConfigs[c.Recipe.RowId] = config;
                                }
                            }
                        }
                    }
                    P.Config.Save();
                    Tasks.Remove(key, out var _);
                }, cts.Token);

                Tasks.TryAdd(key, new(cts, task));
            }
        }

        public static string GetKey(CraftState craft)
        {
            return $"{craft.CraftLevel}/{craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}-{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}-{(craft.CraftExpert ? "Expert" : "Standard")}/{craft.InitialQuality}";
        }

        public static IEnumerable<CraftState> AllValidCrafts(string key, uint craftType)
        {
            var stats = KeyParts(key);
            var recipes = LuminaSheets.RecipeSheet.Values.Where(x => x.CraftType.RowId == craftType && x.RecipeLevelTable.Value.ClassJobLevel == stats.Level);
            foreach (var recipe in recipes)
            {
                var state = Crafting.BuildCraftStateForRecipe(default, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
                if (stats.Prog == state.CraftProgress &&
                    stats.Qual == state.CraftQualityMax &&
                    stats.Dur == state.CraftDurability)
                    yield return state;
            }
        }

        public static (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool Expert) KeyParts(string key)
        {
            var parts = key.Split('/');

            int.TryParse(parts[0], out var lvl);
            int.TryParse(parts[1], out var prog);
            int.TryParse(parts[2], out var qual);
            int.TryParse(parts[3].Split('-')[0], out var dur);
            int.TryParse(parts[3].Split('-')[1], out var crafts);
            int.TryParse(parts[4], out var ctrl);
            int.TryParse(parts[5].Split('-')[0], out var cp);
            int.TryParse(parts[6], out var initial);
            // GetKey 產生的第 5 段是 "{CP}-{Expert|Standard}"。以前只取了 CP 就把後半丟掉,
            // 於是「專家配方的解」跟「一般配方的解」在比對時完全無法區分。
            var expert = parts[5].Split('-').Length > 1 && parts[5].Split('-')[1] == "Expert";

            return (lvl, prog, qual, dur, initial, crafts, ctrl, cp, expert);
        }

        public static bool HasSolution(CraftState craft, out MacroSolverSettings.Macro? raphaelSolutionConfig)
        {
            foreach (var solution in P.Config.RaphaelSolverCacheV3.OrderByDescending(x => KeyParts(x.Key).Control))
            {
                if (solution.Value.Steps.Count == 0) continue;

                var solKey = KeyParts(solution.Key);

                // 耐久與 Expert 旗標以前**完全沒有比對** —— KeyParts 有解析出來,但這裡沒用到。
                // 兩個配方的等級/進度/品質可以完全一樣而耐久不同(DurabilityFactor 不同就會這樣),
                // 那種情況下拿另一個耐久的解來跑,巨集會在中途把耐久用光,而且是靜默的。
                // Expert 同理:專家配方的狀態機完全不同,不能共用一般配方的解。
                if (solKey.Level == craft.CraftLevel &&
                    solKey.Prog == craft.CraftProgress &&
                    solKey.Qual == craft.CraftQualityMax &&
                    solKey.Dur == craft.CraftDurability &&
                    solKey.Expert == craft.CraftExpert &&
                    solKey.Crafts == craft.StatCraftsmanship &&
                    solKey.Control <= craft.StatControl &&
                    solKey.Initial == craft.InitialQuality &&
                    solKey.CP <= craft.StatCP)
                {
                    raphaelSolutionConfig = solution.Value;
                    return true;
                }
            }
            raphaelSolutionConfig = null;
            return false;
        }

        /// <summary>
        /// HasSolution throws a cached solution away the moment a single stat dimension misses - craftsmanship
        /// has to be EXACTLY equal, while control/CP only have to be greater-or-equal. Swapping a piece of gear
        /// or letting food/a potion expire is therefore enough to make a perfectly good solution stop counting,
        /// and because Flavours() then yields nothing, the Raphael option disappears from the UI entirely and
        /// the craft quietly falls back to the standard solver.
        /// <para/>
        /// This finds the closest cache entry that was generated for this same recipe (level, progress, quality
        /// and durability all match) but was rejected on stats, and describes what disqualified it. Returns an
        /// empty string when there is genuinely nothing cached for this recipe, so callers can tell
        /// "no solution exists" apart from "a solution exists and is being ignored".
        /// </summary>
        public static string DescribeIgnoredSolution(CraftState craft)
        {
            var best = "";
            var bestDistance = long.MaxValue;

            foreach (var solution in P.Config.RaphaelSolverCacheV3)
            {
                if (solution.Value.Steps.Count == 0) continue;

                var k = KeyParts(solution.Key);
                if (k.Level != craft.CraftLevel || k.Prog != craft.CraftProgress ||
                    k.Qual != craft.CraftQualityMax || k.Dur != craft.CraftDurability)
                    continue;

                var reasons = new List<string>();
                long distance = 0;

                if (k.Crafts != craft.StatCraftsmanship)
                {
                    reasons.Add("Craftsmanship must match exactly: solution ??, you have ??".Loc(k.Crafts, craft.StatCraftsmanship));
                    distance += Math.Abs(k.Crafts - craft.StatCraftsmanship);
                }
                if (k.Control > craft.StatControl)
                {
                    reasons.Add("Control is ?? short: solution needs ??, you have ??".Loc(k.Control - craft.StatControl, k.Control, craft.StatControl));
                    distance += k.Control - craft.StatControl;
                }
                if (k.CP > craft.StatCP)
                {
                    reasons.Add("CP is ?? short: solution needs ??, you have ??".Loc(k.CP - craft.StatCP, k.CP, craft.StatCP));
                    distance += k.CP - craft.StatCP;
                }
                if (k.Initial != craft.InitialQuality)
                {
                    reasons.Add("Starting quality must match exactly: solution ??, this craft ??".Loc(k.Initial, craft.InitialQuality));
                    distance += Math.Abs(k.Initial - craft.InitialQuality);
                }
                if (k.Expert != craft.CraftExpert)
                {
                    // 沒有這條的話,「只有專家旗標不同」會走到下面的 reasons.Count == 0 而被當成
                    // 「其實沒有被忽略」,使用者就會看到「有解但沒說為什麼」的空白提示。
                    reasons.Add(k.Expert
                        ? "The cached solution was generated for an expert recipe, but this one is not.".Loc()
                        : "The cached solution was generated for a standard recipe, but this one is expert.".Loc());
                    distance += 1;
                }

                // No reasons means HasSolution would have accepted it, so it is not being ignored at all.
                if (reasons.Count == 0) continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = string.Join("\n", reasons);
                }
            }

            return best;
        }

        public static bool InProgress(CraftState craft) => Tasks.TryGetValue(GetKey(craft), out var _);

        public static bool InProgressAny() => Tasks.Any();

        internal static bool CLIExists()
        {
            return File.Exists(Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"));
        }

        public static bool DrawRaphaelDropdown(CraftState craft, bool liveStats = true)
        {
            bool changed = false;
            var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.RecipeId) ?? new();
            if (CLIExists())
            {
                var hasSolution = HasSolution(craft, out var solution);
                var key = GetKey(craft);

                if (!TempConfigs.ContainsKey(key))
                {
                    TempConfigs.Add(key, new());
                    TempConfigs[key].EnsureReliability = P.Config.RaphaelSolverConfig.AllowEnsureReliability;
                    TempConfigs[key].BackloadProgress = P.Config.RaphaelSolverConfig.AllowBackloadProgress;
                    TempConfigs[key].HeartAndSoul = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                    TempConfigs[key].QuickInno = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                }

                if (hasSolution)
                {
                    var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == "Raphael Recipe Solver".Loc());
                    var solverIsRaph = config.SolverType == opt?.Def.GetType().FullName!;
                    var curStats = CharacterStats.GetCurrentStats();
                    //Svc.Log.Debug($"{curStats.Craftsmanship}/{craft.StatCraftsmanship} - {curStats.Control}/{craft.StatControl} - {curStats.CP}/{craft.StatCP}");
                    if (liveStats && craft.StatCraftsmanship != curStats.Craftsmanship && solverIsRaph)
                    {
                        var craftsmanshipError = curStats.Craftsmanship - craft.StatCraftsmanship > 0 ? "(Excess of ??) ".Loc(curStats.Craftsmanship - craft.StatCraftsmanship) : "";
                        ImGuiEx.Text(ImGuiColors.DalamudRed, "Your current Craftsmanship ??does not match the generated result.\nThis solver won't be used until they match due to possible early finishes.\n(You may just need to have the correct buffs applied)".Loc(craftsmanshipError));
                    }

                    if (!solverIsRaph)
                    {
                        if (liveStats)
                        {
                            ImGuiEx.TextCentered("Raphael Solution Has Been Generated. (Click to Switch)".Loc());
                            if (ImGui.IsItemClicked())
                            {
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                                changed = true;
                            }
                        }
                        else
                        {
                            ImGuiEx.TextCentered("Raphael Solution Has Been Generated.".Loc());
                        }
                    }
                }
                else
                {
                    // "A solution exists but is being ignored" has to be visible on the row itself - drawing
                    // nothing here is what made this look like "Raphael never solved this recipe". The row says
                    // THAT it is being ignored; the tooltip carries which stat is off and by how much.
                    var ignored = DescribeIgnoredSolution(craft);
                    if (ignored.Length > 0)
                    {
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, "A Raphael solution exists but does not match your current stats - not used.".Loc());
                        ImGuiEx.Tooltip(ignored + "\n\n" + "Rebuild the solution with your current gear, or restore the stats it was generated for.".Loc());
                    }

                    if (liveStats && P.Config.RaphaelSolverConfig.AutoGenerate && CraftingProcessor.GetAvailableSolversForRecipe(craft, true).Any())
                    {
                        if (!craft.CraftExpert || (craft.CraftExpert && P.Config.RaphaelSolverConfig.GenerateOnExperts))
                            Build(craft, TempConfigs[key]);
                    }
                }

                ImGui.Separator();
                var inProgress = InProgress(craft);
                var raphChanges = false;

                if (inProgress)
                    ImGui.BeginDisabled();

                if (P.Config.RaphaelSolverConfig.AllowEnsureReliability)
                    raphChanges |= ImGui.Checkbox("Ensure reliability".Loc() + $"##{key}Reliability", ref TempConfigs[key].EnsureReliability);
                if (P.Config.RaphaelSolverConfig.AllowBackloadProgress)
                    raphChanges |= ImGui.Checkbox("Backload progress".Loc() + $"##{key}Progress", ref TempConfigs[key].BackloadProgress);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox("Allow heart and soul usage".Loc() + $"##{key}HS", ref TempConfigs[key].HeartAndSoul);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox("Allow quick innovation usage".Loc() + $"##{key}QI", ref TempConfigs[key].QuickInno);

                changed |= raphChanges;

                if (inProgress)
                    ImGui.EndDisabled();

                if (!inProgress)
                {
                    if (ImGui.Button("Build Raphael Solution".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Build(craft, TempConfigs[key]);
                    }
                }
                else
                {
                    if (ImGui.Button("Cancel Raphael Generation".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Tasks.TryRemove(key, out var task);
                        task.Item1.Cancel();
                    }
                }

                if (TempConfigs[key].EnsureReliability && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Ensuring quality is enabled, no support shall be provided when its enabled\nDue to problems that can be caused.".Loc());
                    ImGui.EndTooltip();
                }

                if (TempConfigs[key].HeartAndSoul || TempConfigs[key].QuickInno)
                {
                    ImGui.Text("Specialist actions are enabled, this can slow down the solver a lot.".Loc());
                }

                if (inProgress)
                {
                    ImGuiEx.TextCentered("Generating...".Loc());
                }
            }

            return changed;
        }
    }

    public class RaphaelSolverSettings
    {
        public bool AllowEnsureReliability = false;
        public bool AllowBackloadProgress = false;
        public bool ShowSpecialistSettings = false;
        public bool ExactCraftsmanship = false;
        public bool AutoGenerate = false;
        public bool AutoSwitch = false;
        public bool AutoSwitchOnAll = false;
        public int MaximumThreads = 0;
        public bool GenerateOnExperts = false;
        public int TimeOutMins = 1;
        public bool OpportunisticDeviation = true;
        // Default true == the behaviour Artisan has always had: when the solver a recipe is assigned to
        // cannot produce a usable flavour (Raphael with no matching cached solution, a deleted macro, ...)
        // the craft silently runs on whatever solver has the highest priority, i.e. the standard solver.
        // Turning this off makes that case raise SolverFailed instead, so the craft stops and says why.
        public bool AllowFallbackToStandard = true;

        public bool Draw()
        {
            bool changed = false;

            ImGui.Indent();
            ImGui.TextWrapped("Raphael settings can change the performance and system memory consumption. If you have low amounts of RAM try not to change settings, recommended minimum amount of RAM free is 2GB".Loc());

            ImGui.SliderInt("Maximum Threads".Loc(), ref MaximumThreads, 0, Environment.ProcessorCount);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                P.Config.Save();
            }
            ImGuiEx.TextWrapped("By default uses all it can, but on lower end machines you might need to use less cpu at the cost of speed. (0 = everything)".Loc());

            changed |= ImGui.Checkbox("Ensure 100% reliability in macro generation".Loc(), ref AllowEnsureReliability);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(new System.Numerics.Vector4(255, 0, 0, 1), "Ensuring reliability may not always work and is very CPU and RAM intensive, suggested RAM at least 16GB+ spare. NO SUPPORT SHALL BE GIVEN IF YOU HAVE THIS ON".Loc());
            ImGui.PopTextWrapPos();
            changed |= ImGui.Checkbox("Allow backloading of progress in macro generation".Loc(), ref AllowBackloadProgress);
            changed |= ImGui.Checkbox("Show specialist options when available".Loc(), ref ShowSpecialistSettings);
            changed |= ImGui.Checkbox("Automatically generate a solution if a valid one hasn't been created.".Loc(), ref AutoGenerate);

            if (AutoGenerate)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("Generate on Expert Recipes".Loc(), ref GenerateOnExperts);
                ImGui.Unindent();
            }

            changed |= ImGui.Checkbox("Automatically switch to the Raphael Solver once a solution has been created.".Loc(), ref AutoSwitch);

            if (AutoSwitch)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("Apply to all valid crafts".Loc(), ref AutoSwitchOnAll);
                ImGui.Unindent();
            }

            changed |= ImGui.Checkbox("依製作狀態機會性偏離解算結果", ref OpportunisticDeviation);
            ImGuiComponents.HelpMarker("Raphael 產生的是固定步驟,本身看不到「高品質／最高品質／低品質」。開啟後,遇到這些狀態時會先用模擬器把候選動作連同剩下的巨集整段跑完,只有在「仍然完成製作」且「最終品質確實更高」時才偏離,否則照原計畫走。不會動用能工巧匠圖紙。");

            changed |= ImGui.Checkbox("Allow automatic fallback to the standard solver".Loc(), ref AllowFallbackToStandard);
            ImGuiComponents.HelpMarker("When the solver a recipe is assigned to cannot be used right now - Raphael with no cached solution matching your current stats, an assigned macro that was deleted - Artisan quietly starts the craft on the standard solver instead. Turn this off to make it stop and tell you which solver was unavailable rather than silently crafting with a different one.".Loc());

            changed |= ImGui.SliderInt("Timeout solution generation".Loc(), ref TimeOutMins, 1, 15);

            ImGuiComponents.HelpMarker("If a solution takes longer than this many minutes to generate, it will cancel the generation task.".Loc());

            if (ImGui.Button("Clear raphael macro cache (Currently ?? stored)".Loc(P.Config.RaphaelSolverCacheV3.Count)))
            {
                P.Config.RaphaelSolverCacheV3.Clear();
                changed |= true;
            }

            ImGui.Unindent();
            return changed;
        }
    }

    public class RaphaelSolutionConfig
    {
        public bool EnsureReliability = false;
        public bool BackloadProgress = false;
        public bool HeartAndSoul = false;
        public bool QuickInno = false;
        public string Macro = string.Empty;

        public int MinCP = 0;
        public int MinControl = 0;
        public int ExactCraftsmanship = 0;

        [NonSerialized]
        public bool HasChanges = false;
    }
}
