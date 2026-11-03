using System;
using System.Diagnostics;
using System.IO;

namespace DualWieldLab;

// Both plugins hook EFT internals that change between EFT builds, so they only run on the EFT
// build SPT 4.1 uses, whichever SPT 4.1 release installed it. Patch setup still fails closed if
// a hooked member is missing.
internal static class SupportedGame
{
    internal const string Build = "0.16.9.40743";
    // Assembly-CSharp of SPT 4.1.6, accepted even if the file version cannot be read.
    private static readonly Guid Inspected = new Guid("cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7");

    internal static string Running()
    {
        // The numeric fields: under the game's Mono runtime the FileVersion string loses its last digit.
        try
        {
            var v = FileVersionInfo.GetVersionInfo(Path.Combine(BepInEx.Paths.GameRootPath, "EscapeFromTarkov.exe"));
            return v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart + "." + v.FilePrivatePart;
        }
        catch { return "unknown"; }
    }

    internal static bool IsSupported(Type eftType)
        => Running() == Build || eftType.Assembly.ManifestModule.ModuleVersionId == Inspected;
}
