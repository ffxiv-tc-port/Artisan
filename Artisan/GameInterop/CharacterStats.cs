using Artisan.Autocraft;
using Artisan.RawInformation.Character;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Runtime.CompilerServices;
using Lumina.Excel.Sheets;
using Artisan.RawInformation;
using Lumina.Excel;
using System.Linq;
using Dalamud.Game.ClientState.Statuses;

namespace Artisan.GameInterop;

public unsafe static class CharacterStatsUtils
{
    public enum Stat { Craftsmanship, Control, CP, Count }

    public static uint[] ParamIds = [70, 71, 11]; // rows in BaseParam table
    public static int[,] StatCapModifiers = GetStatCapModifiers(); // [equipslot, stat]

    private static int[,] GetStatCapModifiers()
    {
        var res = new int[23, (int)Stat.Count];
        var sheet = Svc.Data.GetExcelSheet<RawRow>(name: "BaseParam")!;
        for (var stat = Stat.Craftsmanship; stat < Stat.Count; ++stat)
        {
            var row = sheet.GetRow(ParamIds[(int)stat])!;
            for (int i = 1; i < 23; ++i)
            {
                res[i, (int)stat] = row.ReadInt16Column(i + 3);
            }
        }
        return res;
    }
}

public unsafe struct ItemStats
{
    public struct StatValue
    {
        public int Max; // depends on item level and slot
        public int Base; // nq/hq stats
        public int Melded;

        public int Effective => Math.Min(Max, Base + Melded);
    }

    public bool HQ;
    public Item? Data;
    public StatValue[] Stats = new StatValue[(int)CharacterStatsUtils.Stat.Count];

    public ItemStats(uint ItemId, bool hq, Span<ushort> materia, Span<byte> materiaGrades)
    {
        HQ = hq;
        if (ItemId == 0 || ItemId == 8575) //Eternity ring is weird?
            return;

        Data = Svc.Data.GetExcelSheet<Item>()?.GetRow(ItemId);
        if (Data == null)
            return;
         
        foreach (var p in Data.Value.BaseParams())
        {
            if (Array.IndexOf(CharacterStatsUtils.ParamIds, p.BaseParam.RowId) is var stat && stat >= 0)
            {
                Stats[stat].Base += p.BaseParamValue;
            }
        }
        if (hq)
        {
            foreach (var p in Data.Value.BaseParamSpecials())
            {
                if (Array.IndexOf(CharacterStatsUtils.ParamIds, p.BaseParamSpecial.RowId) is var stat && stat >= 0)
                {
                    Stats[stat].Base += p.BaseParamValueSpecial;
                }
            }
        }

        var ilvl = Data.Value.LevelItem.Value;
        Stats[0].Max = Math.Max(Stats[0].Base, (int)(0.5 + 0.001 * CharacterStatsUtils.StatCapModifiers[Data.Value.EquipSlotCategory.RowId, 0] * ilvl.Craftsmanship));
        Stats[1].Max = Math.Max(Stats[1].Base, (int)(0.5 + 0.001 * CharacterStatsUtils.StatCapModifiers[Data.Value.EquipSlotCategory.RowId, 1] * ilvl.Control));
        Stats[2].Max = Math.Max(Stats[2].Base, (int)(0.5 + 0.001 * CharacterStatsUtils.StatCapModifiers[Data.Value.EquipSlotCategory.RowId, 2] * ilvl.CP));

        var sheetMat = Svc.Data.GetExcelSheet<Materia>();
        for (int i = 0; i < 5; ++i)
        {
            if (materia[i] == 0)
                continue;

            var materiaRow = sheetMat?.GetRow(materia[i]);
            if (materiaRow == null)
                continue;

            var baseParamRow = materiaRow.Value.BaseParam.ValueNullable;
            if (baseParamRow is null || baseParamRow.Value.RowId == 0)
                continue;

            var stat = Array.IndexOf(CharacterStatsUtils.ParamIds, materiaRow.Value.BaseParam.RowId);
            if (stat >= 0)
                Stats[stat].Melded += materiaRow.Value.Value[materiaGrades[i]];
        }
    }
    public ItemStats(InventoryItem* item) : this(item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), item->Materia, item->MateriaGrades) { }
    public ItemStats(RaptureGearsetModule.GearsetItem* item) : this(item->ItemId % 1000000, item->ItemId >= 1000000, item->Materia, item->MateriaGrades) { }
}

public unsafe struct ConsumableStats
{
    public struct StatValue
    {
        public int Percent;
        public int Max;
        public int Param;

        public int Effective(int baseValue) => Math.Min(Max, baseValue * Percent / 100);
    }

    public bool HQ;
    public Item? Data;
    public StatValue[] Stats = new StatValue[(int)CharacterStatsUtils.Stat.Count];

    public int EffectiveValue(CharacterStatsUtils.Stat stat, int baseValue) => (int)stat < Stats.Length ? Stats[(int)stat].Effective(baseValue) : 0;

    public ConsumableStats(uint ItemId, bool hq)
    {
        HQ = hq;
        if (ItemId == 0)
            return;

        Data = Svc.Data.GetExcelSheet<Item>()?.GetRow(ItemId);
        if (Data == null)
            return;

        var food = ConsumableChecker.GetItemConsumableProperties(Data.Value, hq);
        if (food == null)
            return;

        int i = 0;
        foreach (var p in food.Value.Params)
        {
            var stat = Array.IndexOf(CharacterStatsUtils.ParamIds, p.BaseParam.RowId);
            if (stat >= 0)
            {
                var val = hq ? p.ValueHQ : p.Value;
                var max = hq ? p.MaxHQ : p.Max;
                Stats[stat].Percent = p.IsRelative ? val : 0;
                Stats[stat].Max = p.IsRelative ? max : val;
                Stats[stat].Param = (int)p.BaseParam.RowId;
            }
            i++;
        }
    }
}

public unsafe struct CharacterStats
{
    public int Craftsmanship;
    public int Control;
    public int CP;
    public int Level;
    public bool SplendorCosmic;
    public bool Specialist;
    public bool Manipulation;

    public override string ToString()
    {
        return $"Craft: {Craftsmanship}; Control: {Control}; CP: {CP}; Level: {Level}; Splendorous/Cosmic: {SplendorCosmic}; Specialist: {Specialist}; Manipulation: {Manipulation};";
    }

    // current in-game stats
    public static CharacterStats GetCurrentStats()
    {
        var stats = new CharacterStats();

        stats.Craftsmanship = CharacterInfo.Craftsmanship;
        stats.Control = CharacterInfo.Control;
        stats.CP = (int)CharacterInfo.MaxCP;
        // InventoryManager.GetInventorySlot(type, index) 有 null 回傳路徑（換區／剛登入時容器還沒載入），
        // 原本兩行都直接 ->ItemId 解參考。讀不到時兩個旗標都留 false：
        // Specialist=false 讓求解器不去規劃專家限定技能（假定有而實際不能用，整條製作序列會斷）；
        // SplendorCosmic=false 同樣是「少假設一個加成」的方向。
        var soulCrystal = InventoryManager.Instance()->GetInventorySlot(InventoryType.EquippedItems, 13);
        stats.Specialist = soulCrystal != null && soulCrystal->ItemId != 0; // specialist == job crystal equipped
        var mainHand = InventoryManager.Instance()->GetInventorySlot(InventoryType.EquippedItems, 0);
        stats.SplendorCosmic = mainHand != null && Svc.Data.GetExcelSheet<Item>()?.GetRow(mainHand->ItemId) is { LevelEquip: 90 or 100, Rarity: >= 4 };
        stats.Manipulation = CharacterInfo.IsManipulationUnlocked(CharacterInfo.JobID);

        return stats;
    }

    // base naked stats
    public static CharacterStats GetBaseStatsNaked() => new() { CP = 180 };

    // base stats (i.e. without consumables) with currently equipped gear
    public static CharacterStats GetBaseStatsEquipped()
    {
        var res = GetBaseStatsNaked();
        var inventory = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        // 既有的 == null 只擋了一半：容器存在但 Items（偏移 0x08）尚未配置時 Size 可能已非 0，
        // 下面的 inventory->Items + i 會變成「null + i * 0x48」的小偏移假指標，
        // ItemStats 會照著它讀出垃圾裝備數值 —— 那比讀不到更糟（求解器會拿假的匠心/加工去排序列）。
        if (inventory == null || inventory->Items == null)
            return res;

        for (int i = 0; i < inventory->Size; ++i)
        {
            var details = new ItemStats(inventory->Items + i);
            if (details.Data != null)
                res.AddItem(i, ref details);
        }
        res.Manipulation = CharacterInfo.IsManipulationUnlocked(CharacterInfo.JobID);
        return res;
    }

    // base stats (i.e. without consumables) with specified gearset
    public static CharacterStats GetBaseStatsGearset(ref RaptureGearsetModule.GearsetEntry gs)
    {
        var res = GetBaseStatsNaked();
        if (!gs.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
            return res;

        for (int i = 0; i < gs.Items.Length; ++i)
        {
            var details = new ItemStats((RaptureGearsetModule.GearsetItem*)Unsafe.AsPointer(ref gs.Items[i]));
            if (details.Data != null)
            {
                res.AddItem(i, ref details);
            }
        }
        res.Manipulation = CharacterInfo.IsManipulationUnlocked((Job)gs.ClassJob);
        return res;
    }

    // base stats (i.e. without consumables) with rear for specified class (either currently equipped or first found matching gearset)
    public static CharacterStats GetBaseStatsForClassHeuristic(Job job)
    {
        if (CharacterInfo.JobID == job)
            return GetBaseStatsEquipped();

        // Instance() 沒登入時合法回 null(它是 UIModule 的轉手,FFXIVClientStructs 裡就寫成
        // 「uiModule == null ? null : ...」)。直接 ->Entries 是解參考 null =
        // AccessViolationException,而它是 corrupted-state exception:下面那個 try/catch
        // 完全攔不到。取不到裝備組清單就走本來就有的 fallback。
        var gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return GetBaseStatsEquipped();

        foreach (ref var gs in gearsetModule->Entries)
        {
            try
            {
                if ((Job)gs.ClassJob == job)
                    return GetBaseStatsGearset(ref gs);
            }
            catch (Exception ex) 
            {
                ex.Log();
            }
        }
        return GetBaseStatsEquipped(); // fallback
    }

    public void AddItem(int slot, ref ItemStats item)
    {
        Craftsmanship += item.Stats[(int)CharacterStatsUtils.Stat.Craftsmanship].Effective;
        Control += item.Stats[(int)CharacterStatsUtils.Stat.Control].Effective;
        CP += item.Stats[(int)CharacterStatsUtils.Stat.CP].Effective;
        SplendorCosmic |= slot == 0 && item.Data.Value.LevelEquip is 90 or 100 && item.Data.Value.Rarity >= 4;
        Specialist |= slot == 13; // specialist == job crystal equipped
    }

    public void AddConsumables(ConsumableStats food, ConsumableStats pot, Dalamud.Game.ClientState.Statuses.Status fcCraftBuff)
        => AddConsumables(food, pot, fcCraftBuff != null ? fcCraftBuff.Param : 0);

    /// <summary>
    /// 與上面那個多載完全相同,只是部隊加成以<b>純量</b>傳入。
    /// </summary>
    /// <remarks>
    /// 🔴 背景執行緒一律用這一個。另一個多載收的是 Dalamud 的 <c>Status</c> 包裝,
    /// 而 <c>Status.Param</c> 的實作是 <c>this.Struct-&gt;Param</c> —— 那是原生解參考,
    /// 只能在遊戲主執行緒上讀;而且 <c>CharacterInfo.FCCraftsmanshipbuff</c> 是
    /// <b>跨幀保存</b>的包裝(每次 <c>UpdateCharaStats</c> 才換一次)。
    /// </remarks>
    public void AddConsumables(ConsumableStats food, ConsumableStats pot, int fcCraftBuffParam)
    {
        Craftsmanship += food.EffectiveValue(CharacterStatsUtils.Stat.Craftsmanship, Craftsmanship) + pot.EffectiveValue(CharacterStatsUtils.Stat.Craftsmanship, Craftsmanship) + fcCraftBuffParam;
        Control += food.EffectiveValue(CharacterStatsUtils.Stat.Control, Control) + pot.EffectiveValue(CharacterStatsUtils.Stat.Control, Control);
        CP += food.EffectiveValue(CharacterStatsUtils.Stat.CP, CP) + pot.EffectiveValue(CharacterStatsUtils.Stat.CP, CP);
    }
}

/// <summary>
/// 八個製作職業的基礎能力值 ＋ 部隊工匠加成,<b>在遊戲主執行緒上一次讀完</b>的純量快照。
/// </summary>
/// <remarks>
/// 🔴 刻意做成<b>參考型別</b>而不是 struct:<see cref="Current"/> 的快取欄位會被
/// 主執行緒寫、被別的執行緒讀,而 struct 指派(一個參考 ＋ 一個 int)<b>不是不可分割的</b>
/// ⇒ 撕裂讀會拿到「新陣列 ＋ 舊加成」。換成參考型別之後發布一個參考就是原子操作。
/// ⚠️ 只涵蓋 <c>CraftType</c> 的八列(台服 7.20 離線查表:恰好 8 列、id 0..7 連續,
/// 對應 CRP..CUL)。索引以外回 <c>default(CharacterStats)</c>。
/// </remarks>
public sealed class CrafterStatsSnapshot
{
    /// <summary><c>CraftType</c> 的列數(台服 7.20 離線查表為 8,id 0..7)。</summary>
    public const int CraftTypeCount = 8;

    /// <summary><see cref="Current"/> 的重讀間隔。</summary>
    private const long CacheMs = 500;

    private readonly CharacterStats[] statsByCraftType;

    /// <summary>部隊「工匠精神」(狀態 356)的 <c>Param</c>;沒有這個加成時為 0。</summary>
    public int FcCraftsmanshipParam { get; }

    private CrafterStatsSnapshot(CharacterStats[] stats, int fcParam)
    {
        statsByCraftType = stats;
        FcCraftsmanshipParam = fcParam;
    }

    /// <summary>
    /// 現在就重讀一份。<b>只能在遊戲主執行緒上呼叫</b>
    /// (呼叫端都已經在上面,所以刻意不加閘門 —— 加了反而會在主執行緒上多繞一圈)。
    /// </summary>
    public static CrafterStatsSnapshot Read()
    {
        var stats = new CharacterStats[CraftTypeCount];
        for (var craftType = 0u; craftType < CraftTypeCount; ++craftType)
        {
            var job = (Job)((uint)Job.CRP + craftType);
            var s = CharacterStats.GetBaseStatsForClassHeuristic(job);
            // GetBaseStats* 從來不填 Level(GetBaseStatsNaked 只設 CP=180),而
            // Crafting.BuildCraftStateForRecipe 只在 Level 是 default 時才去讀
            // PlayerState.Instance()->ClassJobLevels。在這裡就把它填好,背景那側
            // 因此連那一次原生讀取都不會發生,而值與改動前逐字相同。
            if (s.Level == default)
                s.Level = CharacterInfo.JobLevel(job);
            stats[craftType] = s;
        }
        return new CrafterStatsSnapshot(stats, CharacterInfo.FCCraftsmanshipbuff?.Param ?? 0);
    }

    private static CrafterStatsSnapshot? current;
    private static long currentExpiresAt;

    /// <summary>
    /// <b>每幀路徑專用</b>:在遊戲主執行緒上最多每 <see cref="CacheMs"/> 毫秒重讀一次,
    /// 其餘時間回上一份。不是主執行緒時<b>絕不重讀</b>,只回已經發布的那一份(可能是 <c>null</c>)。
    /// </summary>
    /// <remarks>
    /// 🔑 為什麼要這一層:<c>ListEditor.DrawRecipeData</c> 每一幀都會走到,而
    /// <see cref="Read"/> 要走訪 <c>gearsetModule-&gt;Entries</c>(100 格)並為八個職業各建
    /// 十幾份 <c>ItemStats</c>。每幀做一次會把成本從執行緒池搬到<b>主執行緒的幀時間</b>上
    /// —— 那是把一個崩潰問題換成一個掉幀問題,不可接受。
    /// ⚠️ 代價是「約略清單時間」最多慢 500 毫秒反映換裝 —— 那個欄位的標題本身就寫著「約略」。
    /// </remarks>
    public static CrafterStatsSnapshot? Current()
    {
        if (!Svc.Framework.IsInFrameworkUpdateThread)
            return System.Threading.Volatile.Read(ref current);

        var now = Environment.TickCount64;
        var cached = System.Threading.Volatile.Read(ref current);
        if (cached is null || now >= currentExpiresAt)
        {
            cached = Read();
            System.Threading.Volatile.Write(ref current, cached);
            currentExpiresAt = now + CacheMs;
        }
        return cached;
    }

    /// <summary>
    /// 取某個 <c>CraftType</c> 的基礎能力值。<see cref="CharacterStats"/> 是 struct,
    /// 所以回的是<b>複本</b> —— 呼叫端可以安全地在上面 <c>AddConsumables</c> 而不污染快照。
    /// </summary>
    public CharacterStats StatsForCraftType(uint craftType)
        => craftType < statsByCraftType.Length ? statsByCraftType[craftType] : default;
}
