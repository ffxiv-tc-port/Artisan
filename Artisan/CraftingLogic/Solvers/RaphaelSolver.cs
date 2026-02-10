using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
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
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic.Solvers
{
    public class RaphaelSolverDefintion : ISolverDefinition
    {
        public Solver Create(CraftState craft, int flavour)
        {
            if (craft.StatLevel <= 7)
                return new StandardSolver();

            var key = RaphaelCache.GetKey(craft);
            if (RaphaelCache.HasSolution(craft, out var output))
            {
                return new MacroSolver(output!, craft);
            }
            return craft.CraftExpert ? new ExpertSolver() : new StandardSolver();
        }

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            yield return new(this, 3, 0, $"Raphael 配方求解器", craft.StatLevel <= 7 ? $"无法在未解锁技能 {Skills.MastersMend.NameOfAction()} 时工作。 请使用标准求解器。" : "");
        }
    }

    internal static class RaphaelCache
    {
        internal static readonly ConcurrentDictionary<string, RaphaelTaskInfo> Tasks = [];
        [NonSerialized]
        public static Dictionary<string, RaphaelSolutionConfig> TempConfigs = new();

        internal sealed class RaphaelTaskInfo
        {
            public CancellationTokenSource Cancellation { get; set; }
            public Task Task { get; set; }
            public volatile bool FromStartCraft;
            public volatile bool Succeeded;

            public RaphaelTaskInfo(CancellationTokenSource cts, Task task, bool fromStartCraft)
            {
                Cancellation = cts;
                Task = task;
                FromStartCraft = fromStartCraft;
            }
        }


        public static void Build(CraftState craft, RaphaelSolutionConfig config, bool fromStartCraft = false)
        {
            if (craft.StatLevel <= 7) return;

            var key = GetKey(craft);
            if (!CLIExists() || Tasks.ContainsKey(key)) return;

            P.Config.RaphaelSolverCacheV5.TryRemove(key, out _);

            var manipulation = craft.UnlockedManipulation ? "--manipulation" : "";
            var itemText = $"--custom-recipe {craft.LevelTable.RowId} {craft.CraftProgress} {(craft.CraftCollectible ? craft.CraftQualityMin3 : craft.CraftQualityMax)} {craft.CraftDurability} {(craft.CraftExpert ? "1" : "0")} --stellar-steady-hand {Math.Min(craft.CurrentSteadyHandCharges, P.Config.RaphaelSolverConfig.MaxStellarHand)}";

            var argsList = new List<string>
            {
                $"--initial {craft.InitialQuality}"
            };

            if (config.EnsureReliability) argsList.Add("--adversarial");
            if (config.BackloadProgress) argsList.Add("--backload-progress");
            if (config.HeartAndSoul) argsList.Add("--heart-and-soul");
            if (config.QuickInno) argsList.Add("--quick-innovation");
            if (P.Config.RaphaelSolverConfig.MaximumThreads > 0)
                argsList.Add($"--threads {P.Config.RaphaelSolverConfig.MaximumThreads}");

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(P.Config.RaphaelSolverConfig.TimeOutMins));
            var info = new RaphaelTaskInfo(cts, null!, fromStartCraft);

            info.Task = Task.Run(async () =>
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.bin"),
                            Arguments = $"solve {itemText} {manipulation} --level {craft.StatLevel} --stats {craft.StatCraftsmanship} {craft.StatControl} {craft.StatCP} {string.Join(' ', argsList)} --output-variables action_ids",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        EnableRaisingEvents = true
                    };

                    process.Start();

                    using (cts.Token.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); }
                        catch (Exception ex) { ex.Log(); }
                        finally
                        {
                            if (Tasks.TryRemove(key, out var t) && t.FromStartCraft && Crafting.CurState is Crafting.State.WaitStart)
                            {
                                DuoLog.Error("Raphael has timed out or cancelled before a solution could be generated. Crafting will not start, please restart this craft.");
                                Crafting.CurState = Crafting.State.InvalidState;
                            }
                        }
                    }))
                    {
                        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

                        await Task.WhenAll(
                            stdOutTask,
                            stdErrTask,
                            process.WaitForExitAsync(cts.Token)
                        ).ConfigureAwait(false);

                        var output = stdOutTask.Result;
                        var error = stdErrTask.Result.Trim();

                        if (process.ExitCode != 0)
                        {
                            if (!string.IsNullOrWhiteSpace(error))
                                DuoLog.Error(error.Split('\r', '\n')[0]);

                            info.Succeeded = false;
                            cts.Cancel();
                            return;
                        }

                        var cleansedOutput = output.Replace("[", "").Replace("]", "").Replace("\"", "")
                                                   .Split(", ")
                                                   .Select(x => int.TryParse(x, out int n) ? n : 0);

                        P.Config.RaphaelSolverCacheV5[key] = new MacroSolverSettings.Macro
                        {
                            ID = new Random().Next(50001, 10000000),
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

                        info.Succeeded = P.Config.RaphaelSolverCacheV5[key]?.Steps.Count > 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    info.Succeeded = false;
                }
                catch (Exception ex)
                {
                    ex.Log("Something went wrong with Raphael task.");
                    info.Succeeded = false;
                }
                finally
                {
                    Tasks.TryRemove(key, out _);
                }
            }, cts.Token);

            Tasks.TryAdd(key, info);
        }


        public static string GetKey(CraftState craft)
        {
            return $"{craft.CraftLevel}/{craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}-{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}-{(craft.CraftExpert ? "Ex" : "St")}/{craft.InitialQuality}/{(craft.Specialist ? "Sp" : "Re")}/Steady{Math.Min(craft.CurrentSteadyHandCharges, P.Config.RaphaelSolverConfig.MaxStellarHand)}";
        }

        public static RaphaelSolutionConfig GetConfigFromTempOrDefault(CraftState craft)
        {
            var key = GetKey(craft);
            var config = new RaphaelSolutionConfig();

            var hasTempConfig = TempConfigs.TryGetValue(key, out var tempconfig);
            var hasDelins = Crafting.DelineationCount() > 0;
            config.EnsureReliability = hasTempConfig ? tempconfig.EnsureReliability : P.Config.RaphaelSolverConfig.AllowEnsureReliability;
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
                if (state.StatLevel <= 7) continue;

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
            var sol = P.Config.RaphaelSolverCacheV5.FirstOrNull(x => x.Key == thisKey);
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
            return File.Exists(Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.bin"));
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
                    TempConfigs[key].EnsureReliability = P.Config.RaphaelSolverConfig.AllowEnsureReliability;
                    TempConfigs[key].BackloadProgress = P.Config.RaphaelSolverConfig.AllowBackloadProgress;
                    TempConfigs[key].HeartAndSoul = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                    TempConfigs[key].QuickInno = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                }

                var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael 配方求解器");
                var solverIsRaph = config.CurrentSolverType == opt?.Def.GetType().FullName!;
                if (!hasSolution)
                {
                    if (solverIsRaph)
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

                if (P.Config.RaphaelSolverConfig.AllowEnsureReliability)
                    ImGui.Checkbox($"确保可靠性##{key}Reliability", ref TempConfigs[key].EnsureReliability);
                if (P.Config.RaphaelSolverConfig.AllowBackloadProgress)
                    ImGui.Checkbox($"后置进度##{key}Progress", ref TempConfigs[key].BackloadProgress);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    ImGui.Checkbox($"允许使用专心致志##{key}HS", ref TempConfigs[key].HeartAndSoul);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    ImGui.Checkbox($"允许使用快速改革##{key}QI", ref TempConfigs[key].QuickInno);

                if (inProgress)
                    ImGui.EndDisabled();

                if (craft.StatLevel > 7)
                {
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
                            task.Cancellation.Cancel();
                        }
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
        public bool AllowEnsureReliability = false;
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
        public int MaxStellarHand = 2;
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

                changed |= ImGui.Checkbox("在宏生成中确保 100% 可靠性", ref AllowEnsureReliability);
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(new System.Numerics.Vector4(255, 0, 0, 1), "确保可靠性可能并不总是有效，且非常消耗 CPU 和内存，建议至少保留 16GB+ 可用内存。启用此选项后将不提供任何支持");
                ImGui.PopTextWrapPos();
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

                changed |= ImGui.SliderInt("宇宙稳手使用上限", ref MaxStellarHand, 0, 2);

                ImGuiComponents.HelpMarker("仅对包含宇宙稳手的任务有效，将限制每个宏中可用的宇宙稳手次数（Raphael 在实际方案中可能会用更少）。");

                changed |= ImGui.SliderInt("解决方案生成超时", ref TimeOutMins, 1, 15);

                ImGuiComponents.HelpMarker($"如果解决方案生成时间超过此分钟数，将取消生成任务。");

                if (ImGui.Button($"清除 raphael 宏缓存 (当前存储 {P.Config.RaphaelSolverCacheV5.Count} 个)"))
                {
                    P.Config.RaphaelSolverCacheV5.Clear();
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
