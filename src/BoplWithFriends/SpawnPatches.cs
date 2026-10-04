using System;
using System.Collections.Generic;
using System.Linq;
using BoplFixedMath;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BoplWithFriends;

/// <summary>
/// Reimplementation of GameSessionHandler.SpawnPlayers.
/// Vanilla sizes spawn book-keeping arrays to exactly 4 teams/4 spawn points; with more
/// players than spawn points the vanilla code indexes out of bounds. Extra players spawn
/// in offset "waves" around their spawn cross.
/// </summary>
public static class SpawnPatches
{
    [HarmonyPatch(typeof(GameSessionHandler), "SpawnPlayers")]
    static class SpawnPlayers_Patch
    {
        static bool Prefix(GameSessionHandler gsh)
        {
            List<Player> list = PlayerHandler.Get().PlayerList();

            SlimeController PlayerPrefab = (SlimeController)Bwf.Get(gsh, typeof(GameSessionHandler), "PlayerPrefab");
            List<GameObject> AbilityReadyIndicatorPrefabs = (List<GameObject>)Bwf.Get(gsh, typeof(GameSessionHandler), "AbilityReadyIndicators");
            InputUpdater InputUpdaterPrefab = (InputUpdater)Bwf.Get(gsh, typeof(GameSessionHandler), "InputUpdaterPrefab");
            GameObject playerSpawnsRoot = (GameObject)Bwf.Get(gsh, typeof(GameSessionHandler), "playerSpawnsRoot");
            TeamColors teamColors = (TeamColors)Bwf.Get(gsh, typeof(GameSessionHandler), "teamColors");
            Vec2[] teamSpawns = (Vec2[])Bwf.Get(gsh, typeof(GameSessionHandler), "teamSpawns");
            Fix teammateSpawnSpacing = (Fix)Bwf.Get(gsh, typeof(GameSessionHandler), "teammateSpawnSpacing");

            var slimeControllers = new SlimeController[list.Count];
            Bwf.Set(gsh, typeof(GameSessionHandler), "slimeControllers", slimeControllers);

            Bwf.Set(gsh, typeof(GameSessionHandler), "gameInProgress", true);

            int[] teamOrder; // teams in first-appearance order (same as vanilla DifferentTeams)
            try
            {
                teamOrder = (int[])Bwf.M(typeof(GameSessionHandler), "DifferentTeams")
                    .Invoke(gsh, null);
            }
            catch (Exception e)
            {
                Bwf.Error($"DifferentTeams failed: {e.Message}");
                teamOrder = list.Select(p => p.Team).Distinct().OrderBy(x => x).ToArray();
            }

            int[] spawnCounter = new int[Math.Max(teamSpawns.Length, 1)]; // players already spawned per spawn point
            List<InputUpdater> inputUpdaters = new();

            for (int i = 0; i < list.Count; i++)
            {
                int teamIndex = Array.IndexOf(teamOrder, list[i].Team);
                if (teamIndex < 0)
                    teamIndex = i; // shouldn't happen; keep deterministic
                int spawnIx = teamIndex % teamSpawns.Length;
                int wave = spawnCounter[spawnIx]; // 0 = at the cross, 1.. = offset around it
                list[i].playersAndClonesStillAlive = 1;

                // vanilla pair offsets, extended to any number of players per spawn cross
                Vec2 pos;
                if (wave == 0)
                {
                    pos = teamSpawns[spawnIx];
                }
                else
                {
                    int layer = (wave + 1) / 2;   // wave 1,2 -> layer 1; wave 3,4 -> layer 2 ...
                    Fix sign = (wave % 2 == 1) ? Fix.One : (Fix)(-1L);
                    Vec2 diag = new(teammateSpawnSpacing, teammateSpawnSpacing * (Fix)0.5f);
                    pos = teamSpawns[spawnIx] + diag * (Fix)layer * sign;
                }
                spawnCounter[spawnIx]++;

                slimeControllers[i] = FixTransform.InstantiateFixed(PlayerPrefab, pos);
                slimeControllers[i].playerNumber = list[i].Id;
                slimeControllers[i].transform.SetParent(playerSpawnsRoot.transform);
                slimeControllers[i].GetPlayerSprite().sprite = null;
                slimeControllers[i].GetPlayerSprite().material = list[i].Color;

                List<AbilityMonoBehaviour> abilityBehaviours = new();
                list[i].CurrentAbilities = new List<GameObject>();
                for (int k = 0; k < list[i].Abilities.Count; k++)
                {
                    RandomAbility component = list[i].Abilities[k].GetComponent<RandomAbility>();
                    if (component != null)
                    {
                        NamedSprite randomAbilityPrefab = RandomAbility.GetRandomAbilityPrefab(component.abilityIcons, component.abilityIcons_demo);
                        list[i].AbilityIcons[k] = randomAbilityPrefab.sprite;
                        GameObject go = FixTransform.InstantiateFixed(randomAbilityPrefab.associatedGameObject, Vec2.zero);
                        go.SetActive(value: false);
                        list[i].CurrentAbilities.Add(go);
                        abilityBehaviours.Add(go.GetComponent<AbilityMonoBehaviour>());
                    }
                    else
                    {
                        GameObject go2 = FixTransform.InstantiateFixed(list[i].Abilities[k], Vec2.zero);
                        go2.SetActive(value: false);
                        list[i].CurrentAbilities.Add(go2);
                        abilityBehaviours.Add(go2.GetComponent<AbilityMonoBehaviour>());
                    }
                }
                slimeControllers[i].abilities = abilityBehaviours;

                AbilityReadyIndicator[] indicators = new AbilityReadyIndicator[3];
                for (int l = 0; l < 3; l++)
                {
                    indicators[l] = UnityEngine.Object.Instantiate(AbilityReadyIndicatorPrefabs[l], playerSpawnsRoot.transform).GetComponent<AbilityReadyIndicator>();
                    indicators[l].Init();
                    int teamSprite = Math.Min(list[i].Team, teamColors.teamColors.Length - 1);
                    indicators[l].SetColor(teamColors.teamColors[teamSprite].fill);
                    indicators[l].GetComponent<FollowTransform>().Leader = slimeControllers[i].transform;
                    indicators[l].gameObject.SetActive(value: false);
                    if (l < list[i].AbilityIcons.Count)
                        indicators[l].SetSprite(list[i].AbilityIcons[l]);
                }
                slimeControllers[i].AbilityReadyIndicators = indicators;

                Bwf.Set(gsh, typeof(GameSessionHandler), "gameOver", false);

                if (list[i].IsLocalPlayer)
                {
                    InputUpdater inputUpdater = UnityEngine.Object.Instantiate(InputUpdaterPrefab);
                    PlayerInput playerInput = inputUpdater.GetComponent<PlayerInput>();
                    inputUpdater.Claim(list[i].Id);
                    inputUpdater.Init(list[i].Id);
                    playerInput.neverAutoSwitchControlSchemes = true;
                    playerInput.ActivateInput();
                    inputUpdaters.Add(inputUpdater);
                }
            }
            return false;
        }
    }
}
