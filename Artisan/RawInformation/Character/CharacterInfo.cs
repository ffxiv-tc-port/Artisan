using Dalamud.Game.ClientState.Statuses;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.RawInformation.Character
{
    public static class CharacterInfo
    {
        public static unsafe void UpdateCharaStats()
        {
            if (Svc.Objects.LocalPlayer is null) return;

            JobID = (Job)(Svc.Objects.LocalPlayer?.ClassJob.Value.RowId ?? 0);
            CharacterLevel = Svc.Objects.LocalPlayer?.Level;
            CurrentCP = Svc.Objects.LocalPlayer.CurrentCp;
            MaxCP = Svc.Objects.LocalPlayer.MaxCp;
            Craftsmanship = PlayerState.Instance()->Attributes[70];
            Control = PlayerState.Instance()->Attributes[71];
            FCCraftsmanshipbuff = Svc.Objects.LocalPlayer?.StatusList.FirstOrDefault(x => x.StatusId == 356);
        }

        public static byte? CharacterLevel;

        public static Job JobID;

        public static uint CurrentCP;

        public static uint MaxCP;

        public static unsafe int Craftsmanship;

        public static unsafe int Control;

        public static unsafe Dalamud.Game.ClientState.Statuses.Status? FCCraftsmanshipbuff;

        /// <summary>
        /// 取得指定職業的等級。查無此列、或該職業沒有經驗值欄位時回 0(未知)。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>ExpArrayIndex</c> 是 <c>sbyte</c>,而<b>第 0 列(冒險者/ADV)是 -1</b>
        /// —— 那是台服 7.20 ClassJob 表 46 列裡唯一的負值(其餘 0..31,2026-09-07 離線查表確認)。
        /// <c>ClassJobLevels</c> 是 <c>FixedSizeArray35</c>,索引 -1 會擲
        /// <see cref="IndexOutOfRangeException"/>。
        /// <br/><br/>
        /// 🔴 舊寫法的 <c>?? 0</c> 綁在<b>整張表為 null</b> 上,不是綁在 <c>ExpArrayIndex</c> 上
        /// —— 表存在而該列的 <c>ExpArrayIndex</c> 是 -1 會原樣穿過去。
        /// <br/><br/>
        /// 🔴 退路回 0 而不是索引 0:第 0 格是格鬥士/武僧(PGL/MNK)的等級,
        /// 拿它當未知職業的等級是<b>安靜的錯答案</b>。
        /// <br/><br/>
        /// 📌 <c>GetRow</c> 改成 <c>GetRowOrDefault</c>:前者對表上沒有的 id 會擲
        /// <see cref="ArgumentOutOfRangeException"/>,那同樣會炸掉呼叫端。
        /// </remarks>
        public static unsafe int JobLevel(Job job)
        {
            int expArrayIndex = Svc.Data.GetExcelSheet<ClassJob>()?.GetRowOrDefault((uint)job)?.ExpArrayIndex ?? -1;

            var levels = PlayerState.Instance()->ClassJobLevels;
            if (expArrayIndex < 0 || expArrayIndex >= levels.Length)
            {
                LogBadExpArrayIndexOnce(job, expArrayIndex, levels.Length);
                return 0;
            }

            return levels[expArrayIndex];
        }

        private static readonly HashSet<Job> LoggedBadExpArrayIndex = new();

        /// <summary>
        /// 同一個 <see cref="Job"/> 只寫一行 <c>Information</c>,之後靜默。
        /// </summary>
        /// <remarks>
        /// 🔴 用鎖而不是裸 <see cref="HashSet{T}"/>:<see cref="JobLevel"/> 的呼叫點同時有
        /// framework 路徑(<c>Crafting</c> 狀態機)與 <c>RepairManager</c>,並行插入的失敗形式
        /// 是集合本身壞掉,不是「拿到舊值」。只有失敗路徑會進到這裡,鎖是無競爭的。
        /// 🔴 <c>Svc.Log</c> 的呼叫刻意放在鎖外(鎖內不做 I/O);
        /// 用 <c>Information</c> 而不是 <c>DuoLog</c>:後者每個等級都會無條件洗使用者的聊天視窗。
        /// </remarks>
        private static void LogBadExpArrayIndexOnce(Job job, int expArrayIndex, int arrayLength)
        {
            lock (LoggedBadExpArrayIndex)
            {
                if (!LoggedBadExpArrayIndex.Add(job))
                    return;
            }

            Svc.Log.Information($"[CharacterInfo] ClassJob {(uint)job} ({job}) 的 ExpArrayIndex 是 {expArrayIndex}," +
                                $"不在 0..{arrayLength - 1} 內(冒險者/ADV 是 -1);等級以 0 回報。");
        }

        internal static bool IsManipulationUnlocked(Job job) =>  job switch
        {
            Job.CRP => QuestUnlocked(67979),
            Job.BSM => QuestUnlocked(68153),
            Job.ARM => QuestUnlocked(68132),
            Job.GSM => QuestUnlocked(68137),
            Job.LTW => QuestUnlocked(68147),
            Job.WVR => QuestUnlocked(67969),
            Job.ALC => QuestUnlocked(67974),
            Job.CUL => QuestUnlocked(68142),
            _ => false,
        };

        private unsafe static bool QuestUnlocked(int v)
        {
            return QuestManager.IsQuestComplete((uint)v);
        }

        public static bool MateriaExtractionUnlocked() => QuestUnlocked(66174);

        internal static uint CraftLevel() => CharacterLevel switch
        {
            <= 50 => (uint)CharacterLevel,
            51 => 120,
            52 => 125,
            53 => 130,
            54 => 133,
            55 => 136,
            56 => 139,
            57 => 142,
            58 => 145,
            59 => 148,
            60 => 150,
            61 => 260,
            62 => 265,
            63 => 270,
            64 => 273,
            65 => 276,
            66 => 279,
            67 => 282,
            68 => 285,
            69 => 288,
            70 => 290,
            71 => 390,
            72 => 395,
            73 => 400,
            74 => 403,
            75 => 406,
            76 => 409,
            77 => 412,
            78 => 415,
            79 => 418,
            80 => 420,
            81 => 517,
            82 => 520,
            83 => 525,
            84 => 530,
            85 => 535,
            86 => 540,
            87 => 545,
            88 => 550,
            89 => 555,
            90 => 560,
            _ => 0,
        };
    }

    internal unsafe class RecipeInformation : IDisposable
    {
        delegate byte HasItemBeenCraftedDelegate(uint recipe);
        [Signature("40 53 48 83 EC 20 8B D9 81 F9")]
        HasItemBeenCraftedDelegate GetIsGatheringItemGathered = null!;

        private List<uint> Uncompletables = new List<uint>()
        {
            30971, 30987, 31023, 31052,31094, 31157, 31192, 31217, 30001, 30002, 30003, 30004, 30005, 30006, 
            30007, 30008, 30009, 30010, 30011, 30012, 30013, 30014, 30015, 30016, 30017, 30018, 30019, 30020,
            30021, 30022, 30023, 30024, 30025, 30026, 30027, 30028, 30029, 30030, 30031, 30032, 30033, 30034, 
            30035, 30036, 30037, 30038, 30039, 30040, 30041, 30042, 30043, 30044, 30045, 30046, 30047, 30048, 
            30049, 30050, 30051, 30052, 30053, 30054, 30055, 30056, 30057, 30058, 30059, 30060, 30061, 30062, 
            30063, 30064, 30065, 30066, 30067, 30068, 30069, 30070, 30071, 30072, 30073, 30074, 30075, 30076, 
            30077, 30078, 30079, 30080, 30081, 30082, 30083, 30084, 30085, 30086, 30087, 30088, 30089, 30090, 
            30091, 30092, 30093, 30094, 30095, 30096, 30097, 30098, 30099, 30100, 30101, 30102, 30103, 30104, 
            30105, 30106, 30107, 30108, 30109, 30110, 30111, 30112, 30113, 30114, 30115, 30116, 30117, 30118, 
            30119, 30120, 30121, 30122, 30123, 30124, 30125, 30126, 30127, 30128, 30129, 30130, 30131, 30132, 
            30133, 30134, 30135, 30136, 30137, 30138, 30139, 30140, 30141, 30142, 30143, 30144, 30145, 30146, 
            30147, 30148, 30149, 30150, 30151, 30152, 30354, 30355, 30356, 30357, 30358, 30359, 30360, 30361, 
            30362, 30363, 30364, 30365, 30366, 30367, 30368, 30369, 30370, 30371, 30372, 30373, 30374, 30375, 
            30376, 30377, 30378, 30379, 30380, 30381, 30382, 30383, 30384, 30385, 30386, 30387, 30388, 30389, 
            30390, 30391, 30392, 30393, 30394, 30395, 30396, 30397, 30398, 30399, 30400, 30401
        };

        public bool HasRecipeCrafted(uint recipe)
        {
            if (Uncompletables.Any(x => x == recipe)) return true;
            if (!LuminaSheets.RecipeSheet.ContainsKey(recipe)) return false;
            if (LuminaSheets.RecipeSheet[recipe].SecretRecipeBook.RowId > 0) return true;

            return GetIsGatheringItemGathered(recipe) != 0;
        }

        public void Dispose()
        {
            
        }

        internal RecipeInformation()
        {
            Svc.Hook.InitializeFromAttributes(this);

        }
    }
}
