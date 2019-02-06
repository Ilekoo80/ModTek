using System.IO;
using Harmony;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global

namespace ModTek.Patches
{
    [HarmonyPatch(typeof(LoadedAudioBank), "LoadBank")]
    public static class LoadedAudioBank_LoadBank_Patch
    {
        public static void Prefix(string ___name)
        {
            if (!ModTek.ModSoundBanks.ContainsKey(___name))
                return;

            var directory = Path.GetDirectoryName(ModTek.ModSoundBanks[___name]);
            AkSoundEngine.SetBasePath(directory);
        }

        public static void Postfix(string ___name)
        {
            if (!ModTek.ModSoundBanks.ContainsKey(___name))
                return;

            var basePath = AkBasePathGetter.GetValidBasePath();
            AkSoundEngine.SetBasePath(basePath);
        }
    }
}
