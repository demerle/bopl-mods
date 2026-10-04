using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using UnityEngine.InputSystem;

namespace BoplWithFriends;

/// <summary>
/// Reimplementation of CharacterSelectHandler_online.ForceStartGame with extension slots.
/// Vanilla iterates only slots p1..p4; clients/players sitting in slot >=5 would never be
/// built into the Player list. We keep the vanilla logic and extend it.
/// </summary>
public static class StartPatches
{
    [HarmonyPatch(typeof(CharacterSelectHandler_online), nameof(CharacterSelectHandler_online.ForceStartGame))]
    static class ForceStartGame_Patch
    {
        static bool Prefix(PlayerColors pcs = null)
        {
            if (SteamManager.startParameters.nrOfPlayers <= 4)
                return true; // vanilla path is fine

            var selfRef = (CharacterSelectHandler_online)Bwf.Get(typeof(CharacterSelectHandler_online), "selfRef");
            if (selfRef == null)
                return true;

            if (pcs == null)
                pcs = selfRef.playerColors;

            StartRequestPacket sp = SteamManager.startParameters;
            Updater.ReInit();
            var list = new List<Player>();
            Updater.InitSeed(sp.seed);

            // vanilla slots 1..4, then extension slots 5..N.
            // Gating mirrors vanilla: slot is used if its first-ability slot != 0 and the
            // declared nrOfPlayers hasn't been reached yet.
            int built = 0;
            InvokeInit(list, 1, sp.p1_color, sp.p1_team, sp.p1_ability1, sp.p1_ability2, sp.p1_ability3, sp.nrOfAbilites, pcs, sp, ref built);
            InvokeInit(list, 2, sp.p2_color, sp.p2_team, sp.p2_ability1, sp.p2_ability2, sp.p2_ability3, sp.nrOfAbilites, pcs, sp, ref built);
            InvokeInit(list, 3, sp.p3_color, sp.p3_team, sp.p3_ability1, sp.p3_ability2, sp.p3_ability3, sp.nrOfAbilites, pcs, sp, ref built);
            InvokeInit(list, 4, sp.p4_color, sp.p4_team, sp.p4_ability1, sp.p4_ability2, sp.p4_ability3, sp.nrOfAbilites, pcs, sp, ref built);
            foreach (Bwf.ExtraPlayer e in Bwf.Extras.Values.OrderBy(x => x.Slot))
            {
                InvokeInit(list, e.Slot, e.Color, e.Team, (byte)e.Ab1, (byte)e.Ab2, (byte)e.Ab3, sp.nrOfAbilites, pcs, sp, ref built);
            }

            Player player = null;
            ulong mySteamId = SteamClient.SteamId.Value;
            if (GameLobby.isPlayingAReplay)
            {
                player = list[0];
            }
            else
            {
                foreach (Player p in list)
                {
                    if (IdForSlot(sp, p.Id) == mySteamId)
                    {
                        player = p;
                        break;
                    }
                }
            }

            for (int m = 0; m < list.Count; m++)
                list[m].steamId = IdForSlot(sp, list[m].Id);

            player.IsLocalPlayer = true;
            var localPlayerInit = (PlayerInit)Bwf.Get(typeof(CharacterSelectHandler_online), "localPlayerInit");
            player.inputDevice = localPlayerInit.inputDevice;
            player.UsesKeyboardAndMouse = localPlayerInit.usesKeyboardMouse;
            player.CustomKeyBinding = localPlayerInit.keybindOverride;

            Bwf.Set(typeof(CharacterSelectHandler_online), "startButtonAvailable", false);
            PlayerHandler.Get().SetPlayerList(list);
            SteamManager.instance.StartHostedGame();
            AudioManager.Get()?.Play("startGame");
            GameSession.Init();
            if (GameLobby.isPlayingAReplay)
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(sp.currentLevel + 6);
            }
            else
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene("Level1");
            }
            if (!WinnerTriangleCanvas.HasBeenSpawned)
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene("winnerTriangle", UnityEngine.SceneManagement.LoadSceneMode.Additive);
            }
            Bwf.Info($"game started with {list.Count} players ({sp.nrOfPlayers} declared)");
            return false;
        }

        /// <summary>Mirrors vanilla's `if (pN_ability1 != 0 && nrOfPlayers > num)` gating.</summary>
        static void InvokeInit(List<Player> list, int slot, int color, int team, byte ab1, byte ab2, byte ab3, byte nrOfAbilites, PlayerColors pcs, StartRequestPacket sp, ref int built)
        {
            if (ab1 == 0 || sp.nrOfPlayers <= built || slot > sp.nrOfPlayers)
                return;
            var args = new object[] { slot, (byte)color, (byte)team, ab1, ab2, ab3, nrOfAbilites, pcs };
            list.Add((Player)Bwf.M(typeof(CharacterSelectHandler_online), "InitPlayer").Invoke(null, args));
            built++;
        }

        static ulong IdForSlot(StartRequestPacket sp, int slot)
        {
            return slot switch
            {
                1 => sp.p1_id,
                2 => sp.p2_id,
                3 => sp.p3_id,
                4 => sp.p4_id,
                _ => Bwf.Extras.TryGetValue(slot, out var e) ? e.SteamId : 0uL,
            };
        }
    }
}
