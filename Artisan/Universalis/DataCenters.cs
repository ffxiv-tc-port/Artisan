using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Linq;

namespace Artisan.Universalis
{
    public static class DataCenters
    {
        public static string? GetWorldName(uint world)
        {
            var name = Svc.Data.GetExcelSheet<World>()?.FirstOrDefault(x => x.RowId == world).Name;

            if (name != null)
                return name.Value.ExtractText();

            return null;
        }
    }
}
