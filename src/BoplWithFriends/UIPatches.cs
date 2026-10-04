using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BoplWithFriends;

/// <summary>
/// Makes the UI show more than 4 players:
/// 1. CSBox_online.Init team auto-assign is hardcoded to teams 1..3 — generalize to palette length.
/// 2. CharacterSelectHandler_online's remote-player boxes (networkPlayerBoxes) and loading
///    circles are scene arrays of 4 — clone enough to hold MaxPlayers-1 remotes, rewire the
///    box's private refs (Instantiate drops non-serialized references), and lay them out evenly.
/// 3. HandleAbilitySelectUI lays one ability circle per player with a fixed Separation —
///    shrink spacing/scale so >=4 circles fit the screen.
/// </summary>
public static class UIPatches
{
    // -------------------------------------------------------------- helpers

    /// <summary>
    /// GameObject.Instantiate drops non-serialized private references (components in the
    /// original hierarchy). Deep-clone the hierarchy and rewire every reference field that
    /// points into the original's own subtree to the cloned equivalent.
    /// </summary>
    static GameObject CloneWithRefs(GameObject orig, Transform parent)
    {
        GameObject clone = UnityEngine.Object.Instantiate(orig, parent, worldPositionStays: false);
        clone.name = orig.name;

        // component pairs indexed by hierarchy path
        var map = new Dictionary<Component, Component>();
        BuildComponentMap(orig.transform, clone.transform, map);

        foreach (KeyValuePair<Component, Component> kv in map)
        {
            FieldInfo[] fields = kv.Key.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                if (f.Name.StartsWith("m_") || f.IsInitOnly || f.IsLiteral)
                    continue;
                try
                {
                    object value = f.GetValue(kv.Key);
                    if (value is not Component origRef)
                        continue;
                    if (!map.TryGetValue(origRef, out Component cloneRef))
                        continue;
                    f.SetValue(kv.Value, cloneRef);
                }
                catch
                {
                    // skip inaccessible members
                }
            }
        }
        return clone;
    }

    static void BuildComponentMap(Transform orig, Transform clone, Dictionary<Component, Component> map)
    {
        Component[] origComps = orig.GetComponents<Component>();
        Component[] cloneComps = clone.GetComponents<Component>();
        for (int i = 0; i < origComps.Length && i < cloneComps.Length; i++)
        {
            if (origComps[i] != null && cloneComps[i] != null && origComps[i].GetType() == cloneComps[i].GetType())
                map[origComps[i]] = cloneComps[i];
        }
        // recurse by shared children order
        int n = Math.Min(orig.childCount, clone.childCount);
        for (int i = 0; i < n; i++)
            BuildComponentMap(orig.GetChild(i), clone.GetChild(i), map);
    }

    static RectTransform Rect(GameObject go) => go.GetComponent<RectTransform>();

    // -------------------------------------------------------- 1. remote box team auto-assign

    [HarmonyPatch(typeof(CSBox_online), nameof(CSBox_online.Init))]
    static class CSBox_Init_TeamAssign_Patch
    {
        static bool Prefix(CSBox_online self, SteamConnection targetPlayer)
        {
            TeamColors teams = (TeamColors)Bwf.Get(self, typeof(CSBox_online), "teamColors");
            if (teams == null)
                return true;

            self.connectedPlayer = targetPlayer;
            try { Bwf.Set(self, typeof(CSBox_online), "choosingTextIsVisible", true); } catch { }

            if (SteamManager.instance.currentLobby.IsOwnedBy(targetPlayer.id))
            {
                targetPlayer.lobby_team = 0;
            }
            else
            {
                int maxTeam = teams.teamColors.Length - 1;
                for (byte b = 1; b <= maxTeam; b++)
                {
                    bool taken = false;
                    foreach (SteamConnection conn in SteamManager.instance.connectedPlayers)
                        taken |= conn.lobby_team == b;
                    if (!taken)
                    {
                        targetPlayer.lobby_team = b;
                        break;
                    }
                }
            }

            if (Settings.Get().Hide == 0)
                self.playerNameText.text = targetPlayer.steamName;
            else
                self.playerNameText.text = string.Empty;

            Bwf.Set(self, typeof(CSBox_online), "isVisible", true);
            var choosingText = (AnimateInOutUI)Bwf.Get(self, typeof(CSBox_online), "choosingText");
            var animateInOut = (AnimateInOutUI)Bwf.Get(self, typeof(CSBox_online), "animateInOut");
            choosingText?.AnimateIn();
            animateInOut?.AnimateIn();
            return false;
        }
    }

    // -------------------------------------------------------- 2. clone remote boxes

    [HarmonyPatch(typeof(CharacterSelectHandler_online), "Awake")]
    static class CharSelect_Awake_CloneBoxes_Patch
    {
        static void Postfix(CharacterSelectHandler_online self)
        {
            int targetCount = Bwf.MaxPlayers - 1; // remotes
            CSBox_online[] boxes = self.networkPlayerBoxes;
            if (boxes == null || boxes.Length == 0 || self.loadingCircles == null || boxes.Length >= targetCount)
                return;

            CSBox_online orig = boxes[0];
            Transform parent = orig.transform.parent;
            if (parent == null)
                return;

            // telemetry: exact geometry so layout constants can be tuned from logs
            foreach (CSBox_online b in boxes)
            {
                RectTransform rt = Rect(b.gameObject);
                if (rt != null)
                    Bwf.Info($"box '{b.name}' size={rt.rect.width}x{rt.rect.height} pos={rt.anchoredPosition} anchorMin={rt.anchorMin} anchorMax={rt.anchorMax} scale={rt.localScale}");
            }

            float scale = Mathf.Clamp01(4f / targetCount);
            var newBoxes = new List<CSBox_online>(boxes);
            var newCircles = new List<Image>(self.loadingCircles);

            for (int i = boxes.Length; i < targetCount; i++)
            {
                GameObject clone = CloneWithRefs(orig.gameObject, parent);
                clone.transform.localScale = Vector3.one * scale;
                var box = clone.GetComponent<CSBox_online>();
                Bwf.Set(box, typeof(CSBox_online), "isVisible", false);
                newBoxes.Add(box);
                Bwf.Info($"cloned remote box #{i + 1} from '{orig.name}'");

                // keep loadingCircles parallel to networkPlayerBoxes
                if (i < self.loadingCircles.Length)
                {
                    newCircles.Add(self.loadingCircles[i % self.loadingCircles.Length]);
                }
                else
                {
                    Image circle = self.loadingCircles[0];
                    GameObject circleClone = UnityEngine.Object.Instantiate(circle.gameObject, circle.transform.parent, worldPositionStays: false);
                    circleClone.GetComponent<Image>().enabled = false;
                    newCircles.Add(circleClone.GetComponent<Image>());
                }
            }

            self.networkPlayerBoxes = newBoxes.ToArray();
            self.loadingCircles = newCircles.ToArray();
            // original boxes match the clone scale for a uniform row
            foreach (CSBox_online b in self.networkPlayerBoxes)
                b.transform.localScale = Vector3.one * scale;

            LayoutBoxes(self);
        }

        static void LayoutBoxes(CharacterSelectHandler_online self)
        {
            CSBox_online[] boxes = self.networkPlayerBoxes;
            int n = boxes.Length;
            if (n < 2)
                return;

            RectTransform first = Rect(boxes[0].gameObject);
            RectTransform second = Rect(boxes[1].gameObject);
            if (first == null || second == null)
                return;

            float spacing = Mathf.Abs(second.anchoredPosition.x - first.anchoredPosition.x);
            if (spacing <= 0.01f)
                spacing = first.rect.width * 1.1f;

            // configured spacing for 4 boxes; squeeze so N boxes span the same width
            float targetSpacing = spacing * 4f / n;
            float centerX = first.anchoredPosition.x - spacing * 1.5f; // vanilla centers around 3 boxes from box0
            for (int i = 0; i < n; i++)
            {
                RectTransform rt = Rect(boxes[i].gameObject);
                if (rt == null)
                    continue;
                Vector2 p = rt.anchoredPosition;
                rt.anchoredPosition = new Vector2(centerX + i * targetSpacing, p.y);
            }
            Bwf.Info($"layout: {n} boxes, spacing {targetSpacing:F1} (was {spacing:F1})");
        }
    }

    // -------------------------------------------------------- 3. ability select row

    static float baseSeparation = -1f;

    [HarmonyPatch(typeof(HandleAbilitySelectUI), "Awake")]
    static class HandleAbilitySelect_Awake_SeparationCache_Patch
    {
        static void Postfix(HandleAbilitySelectUI self)
        {
            baseSeparation = (float)Bwf.Get(self, typeof(HandleAbilitySelectUI), "Separation");
        }
    }

    [HarmonyPatch(typeof(HandleAbilitySelectUI), "Init")]
    static class HandleAbilitySelect_Init_FitRow_Patch
    {
        static void Prefix(HandleAbilitySelectUI self)
        {
            int n = PlayerHandler.Get().NumberOfPlayers();
            if (n <= 1)
                return;
            if (baseSeparation <= 0)
                baseSeparation = (float)Bwf.Get(self, typeof(HandleAbilitySelectUI), "Separation");
            if (n <= 4)
                return; // vanilla behavior

            float shrink = 3f / (n - 1);              // keep total row width == vanilla 4-player width
            Bwf.Set(self, typeof(HandleAbilitySelectUI), "Separation", baseSeparation * shrink);
            Bwf.Info($"ability select: {n} players, separation {baseSeparation * shrink:F2} (base {baseSeparation:F2})");
        }
    }

    [HarmonyPatch(typeof(HandleAbilitySelectUI), "Init")]
    static class HandleAbilitySelect_Init_ScaleCircles_Patch
    {
        static void Postfix(HandleAbilitySelectUI self)
        {
            int n = PlayerHandler.Get().NumberOfPlayers();
            if (n <= 4)
                return;
            // scale each circle proportionally so they don't overlap once spacing shrinks
            float scale = Mathf.Sqrt(3f / (n - 1));
            var selectors = (AbilitySelectCircle[])Bwf.Get(self, typeof(HandleAbilitySelectUI), "Selectors");
            if (selectors == null)
                return;
            foreach (AbilitySelectCircle sel in selectors)
            {
                if (sel != null)
                    sel.trans.localScale = Vector3.one * scale;
            }
            Bwf.Info($"ability select: scaled circles x{scale:F2} for {n} players");
        }
    }
}
