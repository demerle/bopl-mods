using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;

namespace BoplWithFriends;

/// <summary>
/// Lobby capacity + wire-format extensions (start request packet with >4 players).
/// The vanilla StartRequestPacket has fixed slots p1..p4. We keep the vanilla 105 bytes
/// as-is and append an extension chunk (marker + extra players) after it — all peers
/// running this mod decode it; vanilla dispatch (SteamSocket switch on size) never sees it.
/// </summary>
public static class NetworkingPatches
{
    static int ExtendedPacketSizeLimit => 105 + 2 + (ModInfo.HARD_MAX_PLAYERS - 4) * Bwf.BytesPerExtra;

    // ------------------------------------------------------------------ lobby size

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobbyAsync))]
    static class CreateLobbyAsync_RaiseCap_Patch
    {
        // vanilla calls CreateLobbyAsync(4) to create the game lobby.
        static void Prefix(ref int __0)
        {
            if (__0 == 4)
            {
                Bwf.Info($"lobby capacity 4 -> {Bwf.MaxPlayers}");
                __0 = Bwf.MaxPlayers;
            }
        }
    }

    // ------------------------------------------------------------ start request wire format

    /// <summary>
    /// SteamSocket.OnMessage dispatches on message size; sizes >105 fall into Host.ReadPacket
    /// (wrong parser). Our prefix intercepts start-request packets that carry our extension chunk.
    /// </summary>
    [HarmonyPatch(typeof(SteamSocket), "OnMessage")]
    static class OnMessage_StartRequestExtension_Patch
    {
        static readonly List<byte[]> helperBuffers = new();
        // ref-able helpers for NetworkTools.ReadStartRequest
        static readonly byte[] buf = new byte[2048];
        static byte[] ushortHelper = new byte[2];
        static byte[] uintHelper = new byte[4];
        static byte[] ulongHelper = new byte[8];

        static bool Prefix(IntPtr data, int size, NetIdentity identity)
        {
            if (size <= Bwf.VanillaPacketSize || size > ExtendedPacketSizeLimit)
                return true;

            Marshal.Copy(data, buf, 0, size);
            if (buf[Bwf.VanillaPacketSize] != Bwf.Marker)
                return true;

            // Vanilla case-105 body, plus extension decode.
            StartRequestPacket packet = NetworkTools.ReadStartRequest(
                buf, ref uintHelper, ref ulongHelper, ref ushortHelper);
            SteamManager.startParameters = packet;

            Bwf.ResetExtras();
            byte[] chunk = new byte[size - Bwf.VanillaPacketSize];
            Array.Copy(buf, Bwf.VanillaPacketSize, chunk, 0, chunk.Length);
            foreach (Bwf.ExtraPlayer e in Bwf.DecodeExtras(chunk))
                Bwf.Extras[e.Slot] = e;
            Bwf.Info($"received start request: {packet.nrOfPlayers} players ({Bwf.Extras.Count} extra slots)");

            SteamManager sm = SteamManager.instance;
            if (sm.currentLobby.IsOwnedBy(identity.SteamId))
            {
                byte[] encoded = new byte[Bwf.VanillaPacketSize];
                object[] replayArgs = { encoded, packet };
                Bwf.M(typeof(SteamManager), "EncodeCurrentStartParameters_forReplay").Invoke(sm, replayArgs);

                if (GameSession.inMenus)
                    CharacterSelectHandler_online.ForceStartGame();
                else
                    SteamManager.ForceLoadNextLevel();
            }
            return false; // skip vanilla
        }
    }

    // ------------------------------------------------------------ host start packets

    static List<Bwf.ExtraPlayer> ExtrasFromConnectedPlayers()
    {
        var list = new List<Bwf.ExtraPlayer>();
        List<SteamConnection> connected = SteamManager.instance.connectedPlayers;
        // p1 = host, p2..p4 = connectedPlayers[0..2], extras = connectedPlayers[3..]
        for (int i = 3; i < connected.Count; i++)
        {
            list.Add(new Bwf.ExtraPlayer
            {
                Slot = 5 + list.Count,
                SteamId = connected[i].id.Value,
                Color = connected[i].lobby_color,
                Team = connected[i].lobby_team,
                Ab1 = connected[i].lobby_ability1,
                Ab2 = connected[i].lobby_ability2,
                Ab3 = connected[i].lobby_ability3,
            });
        }
        return list;
    }

    /// <summary>Vanilla-encode 105 bytes + append extension chunk; stores the full buffer on SteamManager.</summary>
    static byte[] BuildStartPacket(SteamManager sm)
    {
        byte[] ext = Bwf.EncodeExtrasPacket(ExtrasFromConnectedPlayers());

        byte[] buffer = new byte[Bwf.VanillaPacketSize + (ext?.Length ?? 0)];
        NetworkTools.EncodeStartRequest(ref buffer, SteamManager.startParameters);
        ext?.CopyTo(buffer, Bwf.VanillaPacketSize);
        Bwf.F(typeof(SteamManager), "startRequestBuffer").SetValue(sm, buffer);
        return buffer;
    }

    [HarmonyPatch(typeof(SteamManager), nameof(SteamManager.HostGame))]
    static class HostGame_Patch
    {
        static bool Prefix(SteamManager sm, PlayerInit hostPlayer)
        {
            sm.currentLobby.SetData("LFM", "0");
            sm.currentLobby.SetFriendsOnly();
            sm.currentLobby.SetJoinable(b: false);

            var sp = default(StartRequestPacket);
            ushort seq = (ushort)Bwf.F(typeof(SteamManager), "nextStartGameSeq").GetValue(sm);
            sp.seqNum = seq;
            Bwf.F(typeof(SteamManager), "nextStartGameSeq").SetValue(sm, (ushort)(seq + 1));
            sp.nrOfAbilites = (byte)Settings.Get().NumberOfAbilities;
            sp.currentLevel = GameSession.CurrentLevel();
            sp.seed = (uint)Environment.TickCount;
            sp.nrOfPlayers = (byte)(sm.connectedPlayers.Count + 1);
            sp.p1_id = SteamClient.SteamId.Value;
            sp.p1_team = (byte)hostPlayer.team;
            sp.p1_color = (byte)hostPlayer.color;
            sp.p1_ability1 = (byte)hostPlayer.ability0;
            sp.p1_ability2 = (byte)hostPlayer.ability1;
            sp.p1_ability3 = (byte)hostPlayer.ability2;
            for (int i = 0; i < sm.connectedPlayers.Count; i++)
            {
                SetConnectionSlots(ref sp, i, sm.connectedPlayers[i]);
            }

            SteamManager.startParameters = sp;

            object dlc = Bwf.Get(sm, typeof(SteamManager), "dlc");
            bool hasDlc = (bool)Bwf.M(dlc.GetType(), "HasDLC").Invoke(dlc, null);
            byte b = (byte)(hasDlc ? 1 : 0);
            for (int j = 0; j < sm.connectedPlayers.Count; j++)
            {
                if (sm.connectedPlayers[j].ownsFullGame)
                    b = (byte)(b | (1 << j + 1));
            }
            sp.isDemoMask = b;
            SteamManager.startParameters = sp;

            byte[] data = BuildStartPacket(sm);

            byte[] encodedForReplay = new byte[Bwf.VanillaPacketSize];
            object[] replayArgs = { encodedForReplay, SteamManager.startParameters };
            Bwf.M(typeof(SteamManager), "EncodeCurrentStartParameters_forReplay").Invoke(sm, replayArgs);

            foreach (SteamConnection conn in sm.connectedPlayers)
                conn.Connection.SendMessage(data);

            Bwf.IsHost = true;
            Bwf.Info($"HostGame: serving {sp.nrOfPlayers} players ({ExtrasFromConnectedPlayers().Count} extra slots)");
            return false; // skip vanilla
        }
    }

    static void SetConnectionSlots(ref StartRequestPacket sp, int index, SteamConnection conn)
    {
        switch (index + 2)
        {
        case 2:
            sp.p2_id = conn.id.Value;
            sp.p2_team = conn.lobby_team;
            sp.p2_color = (byte)conn.lobby_color;
            sp.p2_ability1 = conn.lobby_ability1;
            sp.p2_ability2 = conn.lobby_ability2;
            sp.p2_ability3 = conn.lobby_ability3;
            break;
        case 3:
            sp.p3_id = conn.id.Value;
            sp.p3_team = conn.lobby_team;
            sp.p3_color = (byte)conn.lobby_color;
            sp.p3_ability1 = conn.lobby_ability1;
            sp.p3_ability2 = conn.lobby_ability2;
            sp.p3_ability3 = conn.lobby_ability3;
            break;
        case 4:
            sp.p4_id = conn.id.Value;
            sp.p4_team = conn.lobby_team;
            sp.p4_color = (byte)conn.lobby_color;
            sp.p4_ability1 = conn.lobby_ability1;
            sp.p4_ability2 = conn.lobby_ability2;
            sp.p4_ability3 = conn.lobby_ability3;
            break;
        }
    }

    [HarmonyPatch(typeof(SteamManager), nameof(SteamManager.HostNextLevel))]
    static class HostNextLevel_Patch
    {
        static bool Prefix(SteamManager sm, Player hostPlayer, NamedSpriteList abilityIcons)
        {
            StartRequestPacket sp = SteamManager.startParameters;
            sp.frameBufferSize = (byte)Host.CurrentDelayBufferSize;
            sp.seed = (uint)Environment.TickCount;
            sp.nrOfPlayers = (byte)(sm.connectedPlayers.Count + 1);
            sp.currentLevel = GameSession.CurrentLevel();

            List<Player> list = PlayerHandler.Get().PlayerList();
            for (int i = 0; i < list.Count; i++)
            {
                if (hostPlayer.Id == list[i].Id)
                {
                    SetLocalAbilities(ref sp, list[i].Id, hostPlayer, abilityIcons);
                }
                else
                {
                    for (int j = 0; j < sm.connectedPlayers.Count; j++)
                    {
                        if ((ulong)list[i].steamId == (ulong)sm.connectedPlayers[j].id)
                        {
                            SetRemoteAbilities(ref sp, list[i].Id, sm.connectedPlayers[j]);
                            break;
                        }
                    }
                }
            }
            SteamManager.startParameters = sp;

            byte[] data = BuildStartPacket(sm);

            byte[] encodedForReplay = new byte[Bwf.VanillaPacketSize];
            object[] replayArgs = { encodedForReplay, SteamManager.startParameters };
            Bwf.M(typeof(SteamManager), "EncodeCurrentStartParameters_forReplay").Invoke(sm, replayArgs);

            for (int k = 0; k < sm.connectedPlayers.Count; k++)
                sm.connectedPlayers[k].Connection.SendMessage(data);

            Bwf.Info($"HostNextLevel: serving {sp.nrOfPlayers} players");
            return false; // skip vanilla
        }

        static void SetLocalAbilities(ref StartRequestPacket sp, int playerId, Player p, NamedSpriteList icons)
        {
            byte a1 = (byte)icons.IndexOf(p.Abilities[0].name);
            byte a2 = 0, a3 = 0;
            if (Settings.Get().NumberOfAbilities > 1)
                a2 = (byte)icons.IndexOf(p.Abilities[1].name);
            if (Settings.Get().NumberOfAbilities > 2)
                a3 = (byte)icons.IndexOf(p.Abilities[2].name);
            switch (playerId)
            {
            case 1:
                sp.p1_ability1 = a1; sp.p1_ability2 = a2; sp.p1_ability3 = a3; break;
            case 2:
                sp.p2_ability1 = a1; sp.p2_ability2 = a2; sp.p2_ability3 = a3; break;
            case 3:
                sp.p3_ability1 = a1; sp.p3_ability2 = a2; sp.p3_ability3 = a3; break;
            case 4:
                sp.p4_ability1 = a1; sp.p4_ability2 = a2; sp.p4_ability3 = a3; break;
            }
        }

        static void SetRemoteAbilities(ref StartRequestPacket sp, int playerId, SteamConnection conn)
        {
            switch (playerId)
            {
            case 2:
                sp.p2_ability1 = conn.lobby_ability1; sp.p2_ability2 = conn.lobby_ability2; sp.p2_ability3 = conn.lobby_ability3; break;
            case 3:
                sp.p3_ability1 = conn.lobby_ability1; sp.p3_ability2 = conn.lobby_ability2; sp.p3_ability3 = conn.lobby_ability3; break;
            case 4:
                sp.p4_ability1 = conn.lobby_ability1; sp.p4_ability2 = conn.lobby_ability2; sp.p4_ability3 = conn.lobby_ability3; break;
            }
        }
    }

    // ---------------------------------------------------------------- network client init

    [HarmonyPatch(typeof(SteamManager), "InitNetworkClient")]
    static class InitNetworkClient_Patch
    {
        static bool Prefix(SteamManager sm)
        {
            StartRequestPacket sp = SteamManager.startParameters;
            int localPlayerId = 0;
            if (sp.p1_id == SteamClient.SteamId.Value) localPlayerId = 1;
            else if (sp.p2_id == SteamClient.SteamId.Value) localPlayerId = 2;
            else if (sp.p3_id == SteamClient.SteamId.Value) localPlayerId = 3;
            else if (sp.p4_id == SteamClient.SteamId.Value) localPlayerId = 4;
            else
            {
                foreach (Bwf.ExtraPlayer e in Bwf.Extras.Values)
                {
                    if (e.SteamId == SteamClient.SteamId.Value)
                        localPlayerId = e.Slot;
                }
            }

            var list = new List<Client>();
            for (int i = 0; i < sm.connectedPlayers.Count; i++)
            {
                if (sm.connectedPlayers[i].id.Value == sp.p1_id && sp.nrOfPlayers >= 1)
                    list.Add(new Client(1, sm.connectedPlayers[i]));
                else if (sm.connectedPlayers[i].id.Value == sp.p2_id && sp.nrOfPlayers >= 2)
                    list.Add(new Client(2, sm.connectedPlayers[i]));
                else if (sm.connectedPlayers[i].id.Value == sp.p3_id && sp.nrOfPlayers >= 3)
                    list.Add(new Client(3, sm.connectedPlayers[i]));
                else if (sm.connectedPlayers[i].id.Value == sp.p4_id && sp.nrOfPlayers >= 4)
                    list.Add(new Client(4, sm.connectedPlayers[i]));
                else
                {
                    Bwf.ExtraPlayer extra = Bwf.Extras.Values
                        .FirstOrDefault(x => sp.nrOfPlayers >= x.Slot && x.SteamId == sm.connectedPlayers[i].id.Value);
                    if (extra != null)
                        list.Add(new Client(extra.Slot, sm.connectedPlayers[i]));
                }
            }

            sm.networkClient.Init(list, localPlayerId, sp.currentLevel, sm.startFrameBuffer, sm.checkForDesyncs);
            SteamManager.networkClientHandle = sm.networkClient;
            if (localPlayerId > 4)
                Bwf.Info($"this client sits in extension slot {localPlayerId}");
            return false; // skip vanilla
        }
    }
}
