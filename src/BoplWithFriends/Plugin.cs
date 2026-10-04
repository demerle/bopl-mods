using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BoplWithFriends;

[BepInPlugin(ModInfo.PLUGIN_GUID, ModInfo.PLUGIN_NAME, ModInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static ManualLogSource _log;
    private static Harmony _harmony;

    private void Awake()
    {
        _log = Logger;
        Bwf.Log = Logger;

        Bwf.MaxPlayersConfig = Config.Bind(
            "General",
            "MaxPlayers",
            8,
            new ConfigDescription("Maximum number of players in an online lobby (2-16). " +
                                  "All players need this mod; the host's setting is used for the match.",
                new AcceptableValueRange<int>(2, ModInfo.HARD_MAX_PLAYERS)));

        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, ModInfo.PLUGIN_GUID);

        RaiseCaps();
        SceneManager.sceneLoaded += OnSceneLoaded;

        Bwf.Info($"{ModInfo.PLUGIN_GUID} {ModInfo.PLUGIN_VERSION} loaded (MaxPlayers={Bwf.MaxPlayers})");
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    /// <summary>Swap the hardcoded 4-player constants as early as possible.</summary>
    private static void RaiseCaps()
    {
        typeof(Constants).GetField("MAX_PLAYERS")!.SetValue(null, Bwf.MaxPlayers);
        // AchievementHandler indexes killsThisFrame[playerId - 1]
        object killFrame = typeof(AchievementHandler).GetField("killsThisFrame", AccessTools.all)!.GetValue(null);
        if (killFrame is int[] a && a.Length < Bwf.MaxPlayers)
            typeof(AchievementHandler).GetField("killsThisFrame", AccessTools.all)!.SetValue(null, new int[Bwf.MaxPlayers]);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (Bwf.MaxPlayers <= 4)
            return;

        // Inspect + expand color/team palettes once things are loaded.
        EnsureTeamColors();
        LogPalettes();
    }

    private static bool palettesLogged;

    private static void LogPalettes()
    {
        if (palettesLogged)
            return;
        palettesLogged = true;

        foreach (TeamColors tc in Resources.FindObjectsOfTypeAll(typeof(TeamColors)).Cast<TeamColors>())
            Bwf.Info($"TeamColors '{tc.name}': {tc.teamColors?.Length ?? 0} entries");

        foreach (PlayerColors pc in Resources.FindObjectsOfTypeAll(typeof(PlayerColors)).Cast<PlayerColors>())
            Bwf.Info($"PlayerColors '{pc.name}': {pc.playerColors?.Length ?? 0} entries");

        foreach (ColorPool cp in Resources.FindObjectsOfTypeAll(typeof(ColorPool)).Cast<ColorPool>())
            Bwf.Info($"ColorPool '{cp.name}': {cp.colors?.Count ?? 0} colors");
    }

    /// <summary>
    /// Team ids are chosen by cycling `teams.teamColors.Length` in TeamSelector, and everything
    /// else indexes teamColors[team] directly. If a palette is too short for our player count,
    /// clone + hue-shift entries so every player can be visually distinct.
    /// </summary>
    private static void EnsureTeamColors()
    {
        foreach (TeamColors tc in Resources.FindObjectsOfTypeAll(typeof(TeamColors)).Cast<TeamColors>())
        {
            if (tc.teamColors == null || tc.teamColors.Length >= Bwf.MaxPlayers)
                continue;

            int old = tc.teamColors.Length;
            TeamColor[] expanded = new TeamColor[Bwf.MaxPlayers];
            for (int i = 0; i < Bwf.MaxPlayers; i++)
            {
                expanded[i] = tc.teamColors[i % old];
                if (i >= old)
                {
                    TeamColor c = expanded[i];
                    c.fill = ShiftHue(c.fill, i - old);
                    c.border = ShiftHue(c.border, i - old);
                    c.saturated = ShiftHue(c.saturated, i - old);
                    c.team = i;
                    expanded[i] = c;
                }
            }
            tc.teamColors = expanded;
            Bwf.Info($"TeamColors '{tc.name}' expanded {old} -> {expanded.Length}");
        }
    }

    private static Color ShiftHue(Color c, int step)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        h = (h + 0.618f * step) % 1f;
        return s <= 0.01f ? c : Color.HSVToRGB(h, Mathf.Clamp01(s), v);
    }
}
