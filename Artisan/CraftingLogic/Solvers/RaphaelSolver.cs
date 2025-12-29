using Artisan.Autocraft;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
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
                return new MacroSolver(output!, craft);
            }
            return craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
        }

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            //if (RaphaelCache.HasSolution(craft, out var solution))
            yield return new(this, 3, 0, $"Raphael 配方求解器");
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
                P.Config.RaphaelSolverCacheV4.TryRemove(key, out _);

                Svc.Log.Information("启动 Raphael 进程");

                var manipulation = craft.UnlockedManipulation ? "--manipulation" : "";
                var itemText = $"--recipe-id {craft.RecipeId}";
                var extraArgsBuilder = new StringBuilder();

                extraArgsBuilder.Append($"--initial {craft.InitialQuality} "); // must always have a space after

                if (config.EnsureReliability)
                {
                    Svc.Log.Error("已启用确保可靠性，这可能需要较长时间。启用后不提供任何支持。");
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
                        Arguments = $"solve {itemText} {manipulation} --level {craft.StatLevel} --stats {craft.StatCraftsmanship} {craft.StatControl} {craft.StatCP} {extraArgsBuilder} --output-variables action_ids", // Command to execute
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                Svc.Log.Information(process.StartInfo.Arguments);

                var cts = new CancellationTokenSource();
                cts.Token.Register(() =>
                {
                    try
                    {
                        process?.Kill();
                    }
                    catch (Exception ex)
                    {
                        ex.Log("无法移除进程，可能已结束。");
                    }
                    Tasks.TryRemove(key, out var _);
                }
                );
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
                    cts.Token.ThrowIfCancellationRequested();

                    Svc.Log.Information("Raphael 进程完成，输出生成");
                    var rng = new Random();
                    var ID = rng.Next(50001, 10000000);
                    while (P.Config.RaphaelSolverCacheV4.Any(kv => kv.Value.ID == ID))
                        ID = rng.Next(50001, 10000000);

                    var cleansedOutput = output.Replace("[", "").Replace("]", "").Replace("\"", "").Split(", ").Select(x => int.TryParse(x, out int n) ? n : 0);
                    P.Config.RaphaelSolverCacheV4[key] = new MacroSolverSettings.Macro()
                    {
                        ID = ID,
                        Name = key,
                        Steps = MacroUI.ParseMacro(cleansedOutput),
                        Options = new()
                        {
                            SkipQualityIfMet = false,
                            UpgradeProgressActions = false,
                            UpgradeQualityActions = false,
                            MinCP = craft.StatCP,
                            MinControl = craft.StatControl,
                            MinCraftsmanship = craft.StatCraftsmanship,
                        }
                    };

                    Svc.Log.Information("Raphael 宏生成并存储在缓存中。");
                    if (P.Config.RaphaelSolverCacheV4[key] == null || P.Config.RaphaelSolverCacheV4[key].Steps.Count == 0)
                    {
                        Svc.Log.Error($"Raphael 无法生成有效的宏。这可能是以下原因之一：" +
                            $"\n- 如果您不在运行 Windows，Raphael 可能与您的操作系统不兼容。" +
                            $"\n- 您取消了生成过程。" +
                            $"\n- Raphael 在找不到结果后放弃了。{(P.Config.RaphaelSolverConfig.AutoGenerate ? "\n自动生成将被禁用。" : "")}");
                        P.Config.RaphaelSolverConfig.AutoGenerate = false;
                        cts.Cancel();
                        return;
                    }

                    static bool autoSwitchOk(uint recipeId)
                    {
                        if (P.Config.RaphaelSolverConfig.AutoSwitchOverManual)
                            return true;

                        if (P.Config.RecipeConfigs.TryGetValue(recipeId, out var cfg))
                            // flavours: 0 = standard, expert; 3 = raphael; otherwise = macro/script
                            return cfg.SolverFlavour is 0 or 3;

                        return true;
                    }

                    if (P.Config.RaphaelSolverConfig.AutoSwitch)
                    {
                        Svc.Log.Information("已启用自动切换，如果适用，则切换求解器。");
                        if (!P.Config.RaphaelSolverConfig.AutoSwitchOnAll)
                        {
                            Svc.Log.Debug("切换到 Raphael 求解器 - 单个配方");
                            var nopt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael 配方求解器");
                            if (nopt is { } opt)
                            {
                                if (autoSwitchOk(craft.Recipe.RowId))
                                {
                                    Svc.Log.Information("自动切换检查通过，正在设置配置");
                                    var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                    config.SolverType = opt.Def.GetType().FullName!;
                                    config.SolverFlavour = opt.Flavour;
                                    P.Config.RecipeConfigs[craft.Recipe.RowId] = config;
                                }
                                else
                                    Svc.Log.Information("配方已有宏分配，跳过切换。");
                            }
                        }
                        else
                        {
                            var crafts = AllValidCrafts(key).ToList();
                            Svc.Log.Information($"将求解器应用到 {crafts.Count} 个配方。");
                            var nopt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael 配方求解器");
                            if (nopt is { } opt)
                            {
                                var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                config.SolverType = opt.Def.GetType().FullName!;
                                config.SolverFlavour = opt.Flavour;
                                foreach (var c in crafts)
                                {
                                    if (autoSwitchOk(c.Recipe.RowId))
                                    {
                                        Svc.Log.Information($"将 {c.Recipe.RowId} ({c.Recipe.ItemResult.Value.Name}) 切换到 Raphael 求解器");
                                        P.Config.RecipeConfigs[c.Recipe.RowId] = config;
                                    }
                                    else
                                        Svc.Log.Information($"跳过 {c.Recipe.RowId} ({c.Recipe.ItemResult.Value.Name})，该配方已分配了宏");
                                }
                            }
                        }
                    }
                    Svc.Log.Information("保存 Raphael 生成后的配置更改。");
                    P.Config.Save();

                    Svc.Log.Information("清理任务。");
                    Tasks.Remove(key, out var _);
                }, cts.Token);

                Tasks.TryAdd(key, new(cts, task));
            }
        }

        public static string GetKey(CraftState craft)
        {
            return $"{craft.CraftLevel}/{craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}-{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}-{(craft.CraftExpert ? "Ex" : "St")}/{craft.InitialQuality}/{(craft.Specialist ? "Sp" : "Re")}";
        }

        public static RaphaelSolutionConfig GetConfigFromTempOrDefault(CraftState craft)
        {
            var key = GetKey(craft);
            var config = new RaphaelSolutionConfig();

            var hasTempConfig = TempConfigs.TryGetValue(key, out var tempconfig);
            var hasDelins = Crafting.DelineationCount() > 0;
            //config.EnsureReliability = hasTempConfig ? tempconfig.EnsureReliability : P.Config.RaphaelSolverConfig.AllowEnsureReliability;
            config.BackloadProgress = hasTempConfig ? tempconfig.BackloadProgress : P.Config.RaphaelSolverConfig.AllowBackloadProgress;
            config.HeartAndSoul = hasTempConfig ? tempconfig.HeartAndSoul : P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist && hasDelins;
            config.QuickInno = hasTempConfig ? tempconfig.QuickInno : P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist && hasDelins;

            return config;
        }

        public static IEnumerable<CraftState> AllValidCrafts(string key)
        {
            var stats = KeyParts(key);
            var recipes = LuminaSheets.RecipeSheet.Values.Where(x => x.RecipeLevelTable.Value.ClassJobLevel == stats.Level);
            foreach (var recipe in recipes)
            {
                var state = Crafting.BuildCraftStateForRecipe(default, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
                if (stats.Prog == state.CraftProgress &&
                    stats.Qual == state.CraftQualityMax &&
                    stats.Dur == state.CraftDurability)
                    yield return state;
            }
        }

        public static (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool SP) KeyParts(string key)
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
            var sp = parts[7] == "Sp";

            return (lvl, prog, qual, dur, initial, crafts, ctrl, cp, sp);
        }

        public static bool HasSolution(CraftState craft, out MacroSolverSettings.Macro? raphaelSolutionConfig)
        {
            var thisKey = GetKey(craft);
            raphaelSolutionConfig = null;
            var sol = P.Config.RaphaelSolverCacheV4.FirstOrNull(x => x.Key == thisKey);
            if (sol != null)
            {
                raphaelSolutionConfig = sol.Value.Value;
                return true;
            }
            else
                return false;

            //foreach (var solution in P.Config.RaphaelSolverCacheV4.OrderByDescending(x => KeyParts(x.Key).Control))
            //{
            //    if (solution.Value.Steps.Count == 0) continue;

            //    var solKey = KeyParts(solution.Key);

            //    if (solKey.Level == craft.CraftLevel &&
            //        solKey.Prog == craft.CraftProgress &&
            //        solKey.Qual == craft.CraftQualityMax &&
            //        solKey.Crafts == craft.StatCraftsmanship &&
            //        solKey.Control <= craft.StatControl &&
            //        solKey.Initial == craft.InitialQuality &&
            //        solKey.CP <= craft.StatCP &&
            //        solKey.SP == craft.Specialist)
            //    {
            //        raphaelSolutionConfig = solution.Value;
            //        return true;
            //    }
            //}
            //return false;
        }

        public static bool InProgress(CraftState craft) => Tasks.TryGetValue(GetKey(craft), out var _);

        public static bool InProgressAny() => Tasks.Any();

        internal static bool CLIExists()
        {
            return File.Exists(Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"));
        }

        public static void DrawRaphaelDropdown(CraftState craft, bool liveStats = true)
        {
            var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.RecipeId) ?? new();
            if (CLIExists())
            {
                var hasSolution = HasSolution(craft, out var solution);
                var key = GetKey(craft);

                if (!TempConfigs.ContainsKey(key))
                {
                    TempConfigs.Add(key, new());
                    //TempConfigs[key].EnsureReliability = P.Config.RaphaelSolverConfig.AllowEnsureReliability;
                    TempConfigs[key].BackloadProgress = P.Config.RaphaelSolverConfig.AllowBackloadProgress;
                    TempConfigs[key].HeartAndSoul = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                    TempConfigs[key].QuickInno = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                }

                var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael Recipe Solver");
                var solverIsRaph = config.CurrentSolverType == opt?.Def.GetType().FullName!;
                if (hasSolution)
                {
                    var curStats = CharacterStats.GetCurrentStats();

                    if (!solverIsRaph)
                    {
                        if (liveStats)
                        {
                            ImGuiEx.TextCentered($"已生成 Raphael 解决方案。(点击切换)");
                            if (ImGui.IsItemClicked())
                            {
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                            }
                        }
                        else
                        {
                            ImGuiEx.TextCentered($"已生成 Raphael 解决方案。");
                        }
                    }
                    else
                    {
                        ImGuiEx.TextCentered($"解决方案密钥: {key}");
                        var playerIsJob = Player.ClassJob.RowId == craft.Recipe.CraftType.RowId + 8;
                        var parts = KeyParts(key);
                        if (!playerIsJob)
                            ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"当前不是对应职业。");
                        else
                        {
                            if (curStats.Craftsmanship == craft.StatCraftsmanship)
                                ImGuiEx.TextCentered(ImGuiColors.HealerGreen, $"作业精度满足解决方案需求: {parts.Crafts}。");
                            else
                            {
                                ImGuiEx.TextCentered(ImGuiColors.DPSRed, $"解决方案({craft.StatCraftsmanship})与当前作业精度({curStats.Craftsmanship})存在差异。\n在解决此问题前不会使用Raphael。");
                                var foodIsCrafts = ConsumableChecker.GetItemConsumableProperties(LuminaSheets.ItemSheet[config.RequiredFood], false)?.Params.Any(x => x.BaseParam.RowId is 70);
                                if (foodIsCrafts == true)
                                    ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"(设置的食物是作业精度食物，此问题在增益生效后可能会解决)");

                                var potIsCrafts = ConsumableChecker.GetItemConsumableProperties(LuminaSheets.ItemSheet[config.RequiredPotion], false)?.Params.Any(x => x.BaseParam.RowId is 70);
                                if (potIsCrafts == true)
                                    ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"(设置的药水是作业精度药水，此问题在增益生效后可能会解决)");

                                if ((foodIsCrafts == null || foodIsCrafts == false) && (potIsCrafts == null || potIsCrafts == false))
                                    ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"(您当前有提供作业精度的进食/药剂增益\n但未设置为食物/药水，移除这些增益可能解决此问题)");

                                var diffPos = Math.Abs(craft.StatCraftsmanship - curStats.Craftsmanship);
                                var diffAct = (craft.StatCraftsmanship - curStats.Craftsmanship);
                                if (diffPos % 5 == 0)
                                    if (diffAct > 0)
                                        ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"(此解决方案可能是在有部队作业精度增益时生成的，而您现在没有)");
                                    else
                                        ImGuiEx.TextCentered(ImGuiColors.DalamudOrange, $"(您可能有部队作业精度增益，但在生成解决方案时该增益未激活)");
                            }
                        }

                    }
                }
                else
                {
                    ImGuiEx.TextCentered(ImGuiColors.DalamudRed, "未生成 Raphael 解决方案。");
                    if (P.Config.RaphaelSolverConfig.AutoGenerate && CraftingProcessor.GetAvailableSolversForRecipe(craft, true).Any() && (!craft.CraftExpert || (craft.CraftExpert && P.Config.RaphaelSolverConfig.GenerateOnExperts)))
                    {
                        Build(craft, TempConfigs[key]);
                    }
                }

                ImGui.Separator();

                var inProgress = InProgress(craft);

                if (inProgress)
                    ImGui.BeginDisabled();

                //if (P.Config.RaphaelSolverConfig.AllowEnsureReliability)
                //    raphChanges |= ImGui.Checkbox($"确保可靠性##{key}Reliability", ref TempConfigs[key].EnsureReliability);
                if (P.Config.RaphaelSolverConfig.AllowBackloadProgress)
                    ImGui.Checkbox($"后置进度##{key}Progress", ref TempConfigs[key].BackloadProgress);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    ImGui.Checkbox($"允许使用专心致志##{key}HS", ref TempConfigs[key].HeartAndSoul);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    ImGui.Checkbox($"允许使用快速改革##{key}QI", ref TempConfigs[key].QuickInno);

                if (inProgress)
                    ImGui.EndDisabled();

                if (!inProgress)
                {
                    if (ImGui.Button("构建 Raphael 解决方案", new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Build(craft, TempConfigs[key]);
                    }
                }
                else
                {
                    if (ImGui.Button("取消 Raphael 生成", new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Tasks.TryRemove(key, out var task);
                        task.Item1.Cancel();
                    }
                }

                if (TempConfigs[key].EnsureReliability && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("已启用确保质量，由于可能造成的问题，启用后不提供任何支持。");
                    ImGui.EndTooltip();
                }

                if (TempConfigs[key].HeartAndSoul || TempConfigs[key].QuickInno)
                {
                    ImGui.Text("已启用专家技能，这会显著降低求解器速度。");
                }

                if (inProgress)
                {
                    ImGuiEx.TextCentered("生成中...");
                }
            }
        }
    }

    public class RaphaelSolverSettings
    {
        public bool AllowBackloadProgress = true;
        public bool ShowSpecialistSettings = false;
        public bool ExactCraftsmanship = false;
        public bool AutoGenerate = false;
        public bool AutoSwitch = false;
        public bool AutoSwitchOnAll = false;
        public bool AutoSwitchOverManual = true;
        public int MaximumThreads = 0;
        public bool GenerateOnExperts = false;
        public int TimeOutMins = 1;

        public bool Draw()
        {
            bool changed = false;
            try
            {

                ImGui.Indent();
                ImGui.TextWrapped($"Raphael 设置可以改变性能和系统内存消耗。如果您内存较少，请尽量不要更改设置，建议至少保留 2GB 可用内存");

                if (ImGui.SliderInt("最大线程数", ref MaximumThreads, 0, Environment.ProcessorCount))
                {
                    P.Config.Save();
                }
                ImGuiEx.TextWrapped("默认使用所有可用资源，但在低端机器上您可能需要使用更少的 CPU 以牺牲速度为代价。(0 = 全部)");

                //changed |= ImGui.Checkbox("在宏生成中确保 100% 可靠性", ref AllowEnsureReliability);
                //ImGui.PushTextWrapPos(0);
                //ImGui.TextColored(new System.Numerics.Vector4(255, 0, 0, 1), "确保可靠性可能并不总是有效，且非常消耗 CPU 和内存，建议至少保留 16GB+ 可用内存。启用此选项后将不提供任何支持");
                //ImGui.PopTextWrapPos();
                changed |= ImGui.Checkbox("在宏生成中允许后置进度", ref AllowBackloadProgress);
                changed |= ImGui.Checkbox("在可用时显示专家选项", ref ShowSpecialistSettings);
                changed |= ImGui.Checkbox($"如果尚未创建有效解决方案，则自动生成解决方案。", ref AutoGenerate);

                if (AutoGenerate)
                {
                    ImGui.Indent();
                    changed |= ImGui.Checkbox($"在专家配方上生成", ref GenerateOnExperts);
                    ImGui.Unindent();
                }

                changed |= ImGui.Checkbox($"一旦创建解决方案，自动切换到 Raphael 求解器。", ref AutoSwitch);

                if (AutoSwitch)
                {
                    ImGui.Indent();
                    changed |= ImGui.Checkbox($"应用到所有有效制作", ref AutoSwitchOnAll);
                    changed |= ImGui.Checkbox("应用到已有宏分配的配方上", ref AutoSwitchOverManual);
                    ImGui.Unindent();
                }

                changed |= ImGui.SliderInt("解决方案生成超时", ref TimeOutMins, 1, 15);

                ImGuiComponents.HelpMarker($"如果解决方案生成时间超过此分钟数，将取消生成任务。");

                if (ImGui.Button($"清除 raphael 宏缓存 (当前存储 {P.Config.RaphaelSolverCacheV4.Count} 个)"))
                {
                    P.Config.RaphaelSolverCacheV4.Clear();
                    changed |= true;
                }

                ImGui.Unindent();
                return changed;
            }
            catch { }
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
