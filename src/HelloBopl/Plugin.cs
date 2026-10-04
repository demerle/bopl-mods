using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HelloBopl
{
    [BepInPlugin(ModInfo.PLUGIN_GUID, ModInfo.PLUGIN_NAME, ModInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static ManualLogSource _log;
        private static ConfigEntry<string> _greeting;

        private void Awake()
        {
            _log = Logger;
            _greeting = Config.Bind(
                "General",
                "Greeting",
                "Hello from a Bopl mod!",
                "Message logged whenever any ability spawns.");

            Harmony.CreateAndPatchAll(typeof(Plugin), ModInfo.PLUGIN_GUID);
            _log.LogInfo($"Plugin {ModInfo.PLUGIN_GUID} is loaded!");
        }

        // Proof-of-life Harmony postfix: fires every time any ability instance spawns in-game.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Ability), nameof(Ability.Awake))]
        private static void AbilityAwakePostfix(Ability __instance)
        {
            _log.LogInfo($"{_greeting.Value} (patched ability: {__instance.GetType().Name})");
        }
    }
}
