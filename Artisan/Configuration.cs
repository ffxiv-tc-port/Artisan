using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.UI.Tables;
using Dalamud.Configuration;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Artisan
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 2;
        public bool AutoMode
        {
            get => autoMode;
            set
            {
                if (value)
                {
                    ActionManagerEx.UseSkill(CraftingProcessor.NextRec.Action);
                }
                autoMode = value;
            }
        }
        public bool DisableFailurePrediction = false;
        public int MaxPercentage = 100;
        public bool UseTricksGood = false;
        public int MaxIQPrepTouch = 10;
        public bool UseMaterialMiracle = false;
        public bool MaterialMiracleMulti;
        public bool LowStatsMode = false;
        public bool UseTricksExcellent = false;
        public bool UseSpecialist = false;
        public bool ShowEHQ = true;
        public int CurrentSimulated = 0;
        public bool UseSimulatedStartingQuality = false;
        public bool DisableHighlightedAction = false;

        public ExpertSolverSettings ExpertSolverConfig = new();
        public MacroSolverSettings MacroSolverConfig = new();
        public ScriptSolverSettings ScriptSolverConfig = new();

        public Dictionary<uint, RecipeConfig> RecipeConfigs = new();

        public List<CraftingList> CraftingLists { get; set; } = new();
        public List<NewCraftingList> NewCraftingLists { get; set; } = new();

        public bool ReplicateMacroDelay = false;
        public int AutoDelay = 0;
        public bool DelayRecommendation = false;
        public int RecommendationDelay = 0;
        public bool AbortIfNoFoodPot = false;
        public bool Repair = false;
        public bool PrioritizeRepairNPC = false;
        public bool DisableEnduranceNoRepair = false;
        public bool DisableListsNoRepair = false;
        public bool QuickSynthMode = false;
        public bool DisableToasts = false;
        public bool ShowOnlyCraftable = false;
        public bool ShowOnlyCraftableRetainers = false;
        public bool Materia = false;
        public bool LockMiniMenuR = true;

        public bool EnduranceStopFail = false;
        public bool EnduranceStopNQ = false;

        public int RepairPercent = 50;

        public Dictionary<ulong, ulong> RetainerIDs = new Dictionary<ulong, ulong>();
        public HashSet<ulong> UnavailableRetainerIDs = new HashSet<ulong>();

        [NonSerialized]
        public bool CraftingX = false;

        public bool MaxQuantityMode = false;
        public bool HideQuestHelper = false;
        public bool DisableTheme = true;
        public bool RequestToStopDuty = false;
        public bool RequestToResumeDuty = false;
        public int RequestToResumeDelay = 5;

        public bool UseConsumablesTrial = false;
        public bool UseConsumablesQuickSynth = false;
        public RecipeConfig DefaultConsumables = new(){
            requiredFood = RecipeConfig.Disabled,
            requiredPotion = RecipeConfig.Disabled,
            requiredManual = RecipeConfig.Disabled,
            requiredSquadronManual = RecipeConfig.Disabled,
            requiredFoodHQ = false,
            requiredPotionHQ = false
        };


        public bool PlaySoundFinishEndurance = false;
        public bool PlaySoundFinishList = false;

        /// <summary>
        /// 製作清單整份跑完時，透過 IPC 請 TataruPraise 念一句誇獎。
        /// </summary>
        /// <remarks>
        /// 📌 預設 <c>true</c>：TataruPraise 沒安裝時整條路是靜默 no-op（IPC 擲 <c>IpcNotReadyError</c>
        /// 被吃掉），而 TataruPraise 自己的總開關（預設關）與冷卻也還在，所以預設開不會讓任何人多聽到聲音。
        /// ⚠️ 這是新加的欄位，既有使用者的設定檔裡沒有這個鍵 ⇒ 反序列化時保留欄位初始值，
        /// 也就是既有使用者<b>拿得到</b>這個預設（與 ECommons EzConfig 的行為相反；Artisan 走的是
        /// Dalamud 自己的 <c>SavePluginConfig</c>）。
        /// </remarks>
        public bool TataruPraiseFinishList = true;

        /// <summary>
        /// 耐力模式<b>自己跑到結束</b>時（份數做完、或素材用完），請 TataruPraise 說一句。
        /// </summary>
        /// <remarks>
        /// 📌 走的是跟 <see cref="TataruPraiseFinishList"/> <b>同一個</b>「製作」情境，
        /// 所以既有使用者本來就有語音，開了馬上就會響。
        /// 🔴 一輪最多說一句（去重在 <c>Artisan.IPC.TataruPraiseIPC</c> 的 <c>stopNoticeArmed</c>）。
        /// ⚠️ 使用者自己把耐力模式關掉、或取消一次製作，都<b>不算</b>「跑完了」，不會說。
        /// </remarks>
        public bool TataruPraiseFinishEndurance = true;

        /// <summary>
        /// 製作<b>被迫停下</b>時，請 TataruPraise 說一句「需要幫忙」。
        /// </summary>
        /// <remarks>
        /// 📌 只在「不是你要的停止」時響：十秒內連續五次錯誤、遊戲回報這個製作不可能成功、
        /// 缺少要求的食物或藥水、連續五次開不了製作，以及你自己設定的「失敗就停／非 HQ 就停」真的觸發。
        /// 正常做完走 <see cref="TataruPraiseFinishEndurance"/>——兩件事說同一句話等於沒講。
        /// 🔴 一輪最多說一句，而且清單模式與耐力模式共用同一個閘門。
        /// </remarks>
        public bool TataruPraiseCraftNeedHelp = true;

        public float SoundVolume = 0.25f;

        public bool DefaultListMateria = false;
        public bool DefaultListSkip = false;
        public bool DefaultListSkipLiteral = false;
        public bool DefaultListRepair = false;
        public int DefaultListRepairPercent = 50;
        public bool DefaultListQuickSynth = false;
        public bool ResetTimesToAdd = false;
        public bool SkipMacroStepIfUnable = false;
        public bool DisableAllaganTools = false;
        public bool DisableMacroArtisanRecommendation = false;
        public bool UseQualityStarter = false;
        public bool ShowMacroAssignResults = false;
        public bool HideContextMenus = false;
        public int ContextMenuLoops = 1;
        public float ListCraftThrottle2 = 1f;

        public bool SubtractOwnedFinishedProductFromIngredientTable = false;
        public bool RestockFinishedProductsFromRetainers = false;

        /// <summary>
        /// Whether retainer restocking may ask AutoRetainer to fire the game's retrieve command directly at a
        /// slot instead of driving the retainer window. Defaults on because that is the whole point of the
        /// integration; turning it off restores the window-driving path exactly as it was, and the path is
        /// skipped automatically anyway whenever AutoRetainer is missing or too old.
        /// </summary>
        public bool UseDirectRetainerRetrieval = true;

        /// <summary>
        /// How many free player bag slots the retainer-window restock path must be able to see before it takes
        /// a whole stack instead of only the amount still needed. Defaults to 2, which is the value the path
        /// shipped with as a constant: a full stack landing on top of an existing partial stack of the same
        /// item can split across two slots, so one spare slot is not always enough.
        /// <para/>
        /// Lower is more eager - fewer return trips to the retainer, but a nearly full bag can leave the
        /// withdrawal short. Higher is more conservative. An unknown free-slot count (-1, e.g. while zoning)
        /// always falls back to the exact amount no matter what this is set to, which is why the effective
        /// value is clamped to at least 1 at the point of use.
        /// </summary>
        public int RestockFullStackFreeSlots = 2;

        public bool DefaultHideInventoryColumn = false;
        public bool DefaultHideRetainerColumn = false;
        public bool DefaultHideRemainingColumn = false;
        public bool DefaultHideCraftableColumn = false;
        public bool DefaultHideCraftableCountColumn = false;
        public bool DefaultHideCraftItemsColumn = false;
        public bool DefaultHideCategoryColumn = false;
        public bool DefaultHideGatherLocationColumn = false;
        public bool DefaultHideIdColumn = false;

        public bool DefaultColourValidation = false;
        public bool DefaultHQCrafts = false;

        public int ListOpacity = 100;

        public bool UseUniversalis = false;
        public bool LimitUnversalisToDC = false;
        public bool UniversalisOnDemand = false;

        /// <summary>
        /// 向 Universalis 查價時，每件道具最多取回幾筆掛售（0＝不限）。
        /// </summary>
        /// <remarks>
        /// 📌 這個上限<b>不是</b>為了修 HTTP 504（那是 <c>entries=0</c> 修好的，見
        /// <c>UniversalisClient</c> 檔頭的實測），而是為了讓回應大小有上界：一個 18 件的區域批次
        /// 不限筆數時是 801 KB，取 100 筆是 482 KB。
        /// 🔴 為什麼預設是 300 而不是更小：Universalis 回的 <c>listingsCount</c> 與
        /// <c>unitsForSale</c> 算的是<b>這次回傳的那幾筆</b>，而「買 N 件最便宜的世界」也是從掛售
        /// 明細算的 ⇒ 上限太低會讓畫面上的數字變小且變錯。2026-09-13 拿使用者實機 log 裡
        /// 出現過的全部 90 件道具實測掛售深度：中位數 90 筆、p90 是 207 筆、最深 656 筆；
        /// 上限 50 會截斷 73% 的道具、100 會截斷 44%、200 是 11%、<b>300 是 3.3%</b>。
        /// 另外用同一份真實資料逐件重跑「最便宜世界」的計算：上限 100 有 11/90 個情境算出
        /// 不同答案、上限 50 有 42/90 ⇒ 那兩個值都會改到使用者看得見的數字。
        /// ⚠️ 真的被截斷時 <c>MarketboardData.ListingsTruncated</c> 會標起來，畫面改顯示
        /// 下界而不是假裝那是總數。
        /// ⚠️ 這是新加的欄位，既有使用者的設定檔裡沒有這個鍵 ⇒ 反序列化時保留欄位初始值，
        /// 也就是既有使用者也拿得到這個預設（Artisan 走 Dalamud 自己的 <c>SavePluginConfig</c>）。
        /// </remarks>
        public int UniversalisListingsPerItem = 300;

        /// <summary>
        /// 同一個範圍＋同一件道具的查價結果快取多久（分鐘，0＝不快取）。
        /// </summary>
        /// <remarks>
        /// 🔴 為什麼需要：重建一次製作清單就把整份材料重問一遍。實機上使用者一個遊戲期間
        /// 重建了 32 次清單，那是 419 次區域請求的主要來源，而材料價格十分鐘內不會有
        /// 有意義的變化。
        /// ⚠️ 只快取「問到了」的結果；失敗與「沒有市場資料」不入快取，
        /// 否則使用者再按一次「取得價格」會什麼都不做。
        /// </remarks>
        public int UniversalisCacheMinutes = 10;

        public int SolverCollectibleMode = 3;
        public ItemFilter ShowItemsV1 { get; set; } = ItemFilter.All;

        public bool PinMiniMenu = false;

        public bool DontEquipItems = false;

        [NonSerialized]
        public int CraftX = 0;

        [NonSerialized]
        private bool autoMode = false;

        public bool ViewedEnduranceMessage = false;

        public float SimulatorActionSize = 40f;
        public bool SimulatorHoverMode = true;
        public bool HideRecipeWindowSimulator = false;
        public bool DisableSimulatorActionTooltips = false;

        public bool ReplaceSearch = true;
        public bool UsingDiscordHooks;
        public string? DiscordWebhookUrl;
        public RaphaelSolverSettings RaphaelSolverConfig = new();
        public ConcurrentDictionary<string, MacroSolverSettings.Macro> RaphaelSolverCacheV2 = [];
        public ConcurrentDictionary<string, MacroSolverSettings.Macro> RaphaelSolverCacheV3 = [];

        public void Save()
        {
            Svc.PluginInterface.SavePluginConfig(this);
        }

        public static Configuration Load()
        {
            var fallback = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new();
            try
            {
                var contents = File.ReadAllText(Svc.PluginInterface.ConfigFile.FullName);
                var json = JObject.Parse(contents);
                var version = (int?)json["Version"] ?? 0;
                ConvertConfig(json, version);
                return json.ToObject<Configuration>() ?? new();
            }
            catch (Exception e)
            {
                Svc.Log.Error($"Failed to load config from {Svc.PluginInterface.ConfigFile.FullName}: {e}");
                return fallback;
            }
        }

        private static void ConvertConfig(JObject json, int version)
        {
            if (version <= 0)
            {
                var userMacros = json["UserMacros"] as JArray;
                if (userMacros != null)
                {
                    foreach (var m in userMacros)
                    {
                        m["Options"] = m["MacroOptions"];
                        var actions = m["MacroActions"] as JArray;
                        if (actions != null)
                        {
                            var stepopts = m["MacroStepOptions"] as JArray;
                            var steps = new JArray();
                            for (int i = 0; i < actions.Count; ++i)
                            {
                                var step = stepopts != null && i < stepopts.Count ? stepopts[i] : new JObject();
                                step["Action"] = actions[i];
                                steps.Add(step);
                            }
                            m["Steps"] = steps;
                        }
                    }
                    json["MacroSolverConfig"] = new JObject() { { "Macros", userMacros } };
                }

                var irm = json["IRM"] as JObject;
                if (irm != null)
                {
                    var cvt = new JObject();
                    foreach (var (k, v) in irm)
                    {
                        if (k == "$type")
                            continue;
                        var c = new JObject();
                        c["SolverType"] = typeof(MacroSolverDefinition).FullName;
                        c["SolverFlavour"] = v;
                        cvt[k] = c;
                    }
                    json["RecipeConfigs"] = cvt;
                }
            }
        }
    }
}
