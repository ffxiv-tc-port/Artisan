using ECommons;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Artisan.RawInformation
{
    // Maps craftable-item names in languages the game client does not ship
    // (English global names, Simplified Chinese CN-client names) to item IDs, so
    // Teamcraft "Copy as Text" exports made under those display languages can
    // still be imported on a Traditional Chinese client. Regenerate the embedded
    // TSV with scripts/generate_teamcraft_item_names.py.
    internal static class ForeignItemNames
    {
        private static Dictionary<string, uint>? map;

        internal static bool TryGetItemId(string name, out uint itemId)
        {
            map ??= Load();
            return map.TryGetValue(name, out itemId);
        }

        private static Dictionary<string, uint> Load()
        {
            var dict = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var resource = asm.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("TeamcraftItemNames.tsv"));
                if (resource == null) return dict;
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) return dict;
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = line.Split('\t');
                    if (fields.Length < 2 || !uint.TryParse(fields[0], out var id)) continue;
                    for (int i = 1; i < fields.Length; i++)
                    {
                        if (fields[i].Length > 0)
                            dict.TryAdd(fields[i], id);
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
            return dict;
        }
    }
}
