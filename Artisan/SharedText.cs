namespace Artisan;

// 跨檔逐字重複的使用者可見字串收斂處。
//
// 🔴 為什麼要收斂:.Loc() 是**用英文原文當 key** 去查 LanguageChineseTraditional.ini,
//    而 ini 是字典、同一句只存一條。所以同一句被複製到兩個檔時,改動其中一份的英文
//    會讓**那一份**查不到翻譯而靜默退回英文,另一份照樣是中文 —— 看起來像「漏翻一句」
//    而不是「兩個複製品走散了」。集中成常數之後,改一次兩邊一起改,key 也永遠只有一個。
//    ECommons 的 Loc() 查不到就 return s,不擲例外也不寫 log。
//
// ⚠️ 這裡只放**真的出現在兩個以上位置、且每一處都被 .Loc() 包住**的字串;
//    只用一次的字串留在使用處比較好讀。
// ⚠️ 字串裡的 ?? 是 ECommons Loc(params object[]) 的位置參數佔位符,不是缺字。
public static class SharedText {
    public const string AutoRepairHelp = "If enabled, Artisan will automatically repair your gear when any piece reaches the configured repair threshold.\n\nCurrent min gear condition is ??% and cost to repair at a vendor is ?? gil.\n\nIf unable to repair with Dark Matter, will try for a nearby repair NPC.";
    public const string MateriaExtractionNotUnlocked = "This character has not unlocked materia extraction. This setting will be ignored.";
    public const string AutoMateriaExtractionHelp = "Will automatically extract materia from any equipped gear once it's spiritbond is 100%";
    public const string EnduranceNoRecipeSet = "No recipe has been set for Endurance mode. Disabling Endurance mode.";
    public const string CraftingModeChangedTooManyErrors = "Current crafting mode has been ?? due to too many errors in succession.";
    public const string JobAbbreviations = "Job abbreviations: CRP - Carpenter; ARM - Armorer; LTW - Leatherworker; ALC - Alchemist; BSM - Blacksmith; GSM - Goldsmith; WVR - Weaver; CUL - Culinarian.";
    public const string ListBeingCreated = "Your list is being created. Please wait.";
    public const string ClipboardMergedIntoList = "Merged clipboard items into the current list.";
    public const string ImportAsQuickSynthNotice = "These items will try to be added as quick synth due to the default setting being enabled.";
    public const string ImportedListEmpty = "The imported list has no items. Please check your import and try again.";
    public const string RunInSimulatorForResults = "Please run this recipe in the simulator for results.";
    public const string NotOnFirstStep = "You are not on the first step of the craft";
    public const string AllowOnlyIfStepsRemain = "Allow ?? only if more than this amount of steps remain on ??";
    public const string NewItemsAsQuickSynth = "Set new items added to list as quick synth";
    public const string MacroDefaultSolverHelp = "Uses a recommendation from the appropriate default solver, i.e Standard Recipe Solver for regular recipes, Expert Recipe Solver for expert recipes.";
    public const string MacroTouchComboHelp = "This will use the appropriate step of the 3-step touch combo, depending on the last action actually used. Useful if upgrading quality actions or skipping on conditions.";
    public const string MacroAlternateTouchComboHelp = "Similar to the other touch combo, this will alternate between Basic Touch & Refined Touch depending on the previous action used.";
    public const string CraftingInProgressSettingsLocked = "Crafting in progress. Macro settings will be unavailable until you stop crafting.";
    public const string AutoFocusRecipeSearchWarning = "Warning: You have the \"Auto Focus Recipe Search\" SimpleTweak enabled. This is highly incompatible with Artisan and is recommended to disable it.";
    public const string CreateListWithSubcraftsStarOnly = "Create Crafting List (with subcrafts) (Star only)";
    public const string GearsetRequiredForFeature = "Please have a gearset selected from above to use this feature.";
}
