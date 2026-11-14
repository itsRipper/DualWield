using System.Collections.Generic;
using System.Globalization;

namespace DualWieldLab;

// Identity of an assembled pistol for the pose caches: template plus attachment tree, so a changed
// grip or sight is sampled afresh. The magazine is excluded; it changes per reload and moves nothing.
internal static class GeometryKey
{
    internal const string MagazineSlot = "mod_magazine";

    // attachments: "slot=template" for every installed mod, in the weapon's own slot order.
    internal static string Compose(string templateId, IReadOnlyList<string> attachments)
    {
        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < attachments.Count; i++)
            {
                string entry = attachments[i];
                for (int c = 0; c < entry.Length; c++) hash = (hash ^ entry[c]) * 16777619;
                hash = (hash ^ '|') * 16777619;
            }
            return templateId + "#" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    internal static bool AffectsGeometry(string slotId) => slotId != MagazineSlot;
}
