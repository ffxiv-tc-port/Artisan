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
            set => autoMode = value;
        }

        /// <summary>使用者在 UI／指令切換自動模式的唯一入口：勾上時立刻送出目前這一則建議。</summary>
        /// <remarks><see cref="AutoMode"/> 本身只做賦值——設定屬性不得帶遊戲動作副作用，
        /// 否則反序列化使用者設定檔就會呼叫一次 UseSkill。</remarks>
        public void SetAutoMode(bool enabled)
        {
            autoMode = enabled;
            if (enabled)
                ActionManagerEx.UseSkill(CraftingProcessor.NextRec.Action);
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
        public bool ReplicateMacroDelayWhenPlayersNearby = false;
        public int AutoDelay = 0;
        public bool DelayRecommendation = false;
        public int RecommendationDelay = 0;
        public bool AbortIfNoFoodPot = false;

        /// <summary>設了食物／藥水／筆記但背包沒有時提示一次，<b>不中止</b>製作。</summary>
        /// <remarks>🔴 只提示、不購買 —— 自動上市場採買是紅線。中止是 <see cref="AbortIfNoFoodPot"/> 的事。</remarks>
        public bool WarnIfNoFoodPot = true;
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

        /// <summary>製作被迫停下、需要人處理時播放提示音（遊戲錯誤、缺消耗品、無法修理、解算器不能用等；正常做完與使用者自己停的不算）。</summary>
        /// <remarks>各停止點經由 <c>Endurance.NotifyForcedStop</c> 觸發，「三十秒內最多一次」的節流在 <c>Endurance.PlayErrorSound</c>。</remarks>
        public bool PlaySoundError = false;

        /// <summary>
        /// 製作清單整份跑完時，透過 IPC 請 TataruPraise 念一句誇獎。
        /// </summary>
        /// <remarks>
        /// 📌 預設 <c>true</c>：TataruPraise 沒安裝時整條路是靜默 no-op（IPC 擲 <c>IpcNotReadyError</c>
        /// 被吃掉），而 TataruPraise 自己的總開關（預設關）與冷卻也還在，所以預設開不會讓任何人多聽到聲音。
        /// </remarks>
        public bool TataruPraiseFinishList = true;

        /// <summary>
        /// 耐力模式<b>自己跑到結束</b>時（份數做完、或素材用完），請 TataruPraise 說一句。
        /// </summary>
        /// <remarks>
        /// 🔴 一輪最多說一句（去重在 <c>Artisan.IPC.TataruPraiseIPC</c> 的 <c>stopNoticeArmed</c>）。
        /// ⚠️ 使用者自己把耐力模式關掉、或取消一次製作，都<b>不算</b>「跑完了」，不會說。
        /// </remarks>
        public bool TataruPraiseFinishEndurance = true;

        /// <summary>
        /// 製作<b>被迫停下</b>時，請 TataruPraise 說一句「需要幫忙」。
        /// </summary>
        /// <remarks>
        /// 📌 只在「不是你要的停止」時說，停止點與 <see cref="PlaySoundError"/> 是同一組（都經由 <c>Endurance.NotifyForcedStop</c>）。
        /// 🔴 一輪最多說一句，而且清單模式與耐力模式共用同一個閘門；暫停中的清單不算在跑，不會重喊。
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

        /// <summary>製作清單碰到素材不夠的項目時，先把自己做得出來的缺料做到足夠再繼續；關掉＝照舊跳過。</summary>
        /// <remarks>Load 走 JObject.ToObject：舊設定檔沒有這個鍵時保留這裡的初始值（開）。</remarks>
        public bool PrecraftMissingIngredients = true;

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
        /// An unknown free-slot count (-1, e.g. while zoning) always falls back to the exact amount no matter what
        /// this is set to, which is why the effective value is clamped to at least 1 at the point of use.
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
        /// ⚠️ 真的被截斷時 <c>MarketboardData.ListingsTruncated</c> 會標起來，畫面改顯示
        /// 下界而不是假裝那是總數。
        /// </remarks>
        public int UniversalisListingsPerItem = 300;

        /// <summary>
        /// 同一個範圍＋同一件道具的查價結果快取多久（分鐘，0＝不快取）。
        /// </summary>
        /// <remarks>
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
                // 備援只在上面失敗時才讀：成功時它整份反序列化完就丟掉。
                return Svc.PluginInterface.GetPluginConfig() as Configuration ?? new();
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
