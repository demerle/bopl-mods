using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BoplWithFriends;

/// <summary>
/// Host input-pipeline patch. Vanilla aggregates remote client inputs into a 4-slot
/// InputPacketQuad (slots p1..p4 only); inputs belonging to slots 5..N are dequeued and
/// DROPPED. We reimplement the aggregation loop so extra slots feed per-slot queues, and
/// apply their contents right before each simulation tick.
/// </summary>
public static class InputPatches
{
    // slot (5..) -> pending inputs for the next sim tick (FIFO)
    internal static readonly Dictionary<int, Queue<InputPacket>> ExtraInputQueues = new();

    public static void ResetExtraInputQueues()
    {
        ExtraInputQueues.Clear();
    }

    [HarmonyPatch(typeof(Host), "ProcessNetworkPackets")]
    static class Host_ProcessNetworkPackets_Patch
    {
        static bool Prefix(Host host)
        {
            List<Client> clients = (List<Client>)Bwf.Get(host, typeof(Host), "clients");
            TimedInputPacket[] inputHistory = (TimedInputPacket[])Bwf.Get(host, typeof(Host), "inputHistory");
            bool adaptive = host.AdaptiveInputDelayBuffer;
            uint localPacketsSent = host.inputPacketsSent;
            int localPlayerId = (int)Bwf.Get(host, typeof(Host), "localPlayerId");
            var InputBuffer = (Queue<InputPacketQuad>)Bwf.Get(host, typeof(Host), "InputBuffer");
            var sentPacketHistory = (Queue<InputPacket>)Bwf.Get(host, typeof(Host), "sentPacketHistory");
            int UpdatesPlacedInDelayBuffer = (int)Bwf.Get(host, typeof(Host), "UpdatesPlacedInDelayBuffer");
            int delayBufferSize = (int)Bwf.Get(typeof(Host), "delayBufferSize");
            bool allClientsSynced = (bool)Bwf.Get(host, typeof(Host), "allClientsSynced");

            if (clients.Count == 0)
            {
                UnityEngine.MonoBehaviour.print("There are no clients");
            }
            for (int i = 0; i < clients.Count; i++)
            {
                if (!clients[i].steamConnection.Connected && !clients[i].steamConnection.Connecting)
                {
                    UnityEngine.MonoBehaviour.print(" a client is not connected nor connecting");
                }
            }
            bool flag = true;
            for (int j = 0; j < clients.Count; j++)
            {
                clients[j].ProcessNetworkPackets(UnityEngine.Time.unscaledTime, inputHistory, adaptive, (byte)localPlayerId);
                flag &= clients[j].HasReceivedSyncAck;
                flag &= clients[j].HasSentSyncAck;
            }
            if (flag && !allClientsSynced)
            {
                allClientsSynced = true;
                for (int k = 0; k < delayBufferSize; k++)
                {
                    InputBuffer.Enqueue(default(InputPacketQuad));
                }
            }
            if (!allClientsSynced)
            {
                PersistState(host, allClientsSynced);
                return false;
            }

            uint num = uint.MaxValue;
            for (int l = 0; l < clients.Count; l++)
            {
                if (clients[l].InputPacketsReceived < num)
                    num = clients[l].InputPacketsReceived;
            }
            if (localPacketsSent < num)
                num = localPacketsSent;
            int num2 = (int)num - UpdatesPlacedInDelayBuffer;
            for (int m = 0; m < num2; m++)
            {
                var item = default(InputPacketQuad);
                for (int n = 0; n < clients.Count; n++)
                {
                    InputPacket inputPacket = clients[n].inputHistory.Dequeue();
                    if (clients[n].PlayerId <= 4)
                    {
                        switch (clients[n].PlayerId)
                        {
                        case 1: item.p1 = inputPacket; break;
                        case 2: item.p2 = inputPacket; break;
                        case 3: item.p3 = inputPacket; break;
                        case 4: item.p4 = inputPacket; break;
                        }
                    }
                    else
                    {
                        // extra slot: keep for the upcoming sim ticks
                        if (!ExtraInputQueues.TryGetValue(clients[n].PlayerId, out var q))
                        {
                            q = new Queue<InputPacket>();
                            ExtraInputQueues[clients[n].PlayerId] = q;
                        }
                        q.Enqueue(inputPacket);
                        ExpireExtraQueue(clients[n].PlayerId, q);
                    }
                }
                InputPacket inputPacket2 = sentPacketHistory.Dequeue();
                switch (localPlayerId)
                {
                case 1: item.p1 = inputPacket2; break;
                case 2: item.p2 = inputPacket2; break;
                case 3: item.p3 = inputPacket2; break;
                case 4: item.p4 = inputPacket2; break;
                }
                InputBuffer.Enqueue(item);
                Bwf.Set(host, typeof(Host), "UpdatesPlacedInDelayBuffer", UpdatesPlacedInDelayBuffer + 1);
            }
            try
            {
                if (localPlayerId == 1)
                {
                    for (int num3 = 0; num3 < clients.Count; num3++)
                    {
                        clients[num3].CheckForDesyncs();
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("Desync occured, ending game " + ex);
                GameSessionHandler.EndGameImmediately();
            }

            PersistState(host, allClientsSynced);
            return false;
        }

        /// <summary>Extra-slot queues must never grow without bound if a peer stalls.</summary>
        static void ExpireExtraQueue(int slot, Queue<InputPacket> q)
        {
            while (q.Count > 64)
                q.Dequeue();
        }

        static void PersistState(Host host, bool allClientsSynced)
        {
            Bwf.Set(host, typeof(Host), "allClientsSynced", allClientsSynced);
        }
    }

    /// <summary>Applies extra-slot inputs right before every simulation tick.</summary>
    [HarmonyPatch(typeof(Updater), nameof(Updater.TickSimulation))]
    static class Updater_TickSimulation_Extras_Patch
    {
        static void Prefix()
        {
            foreach (KeyValuePair<int, Queue<InputPacket>> kv in ExtraInputQueues)
            {
                if (kv.Value.Count > 0)
                {
                    Bwf.ApplyExtraInput(kv.Key, kv.Value.Dequeue());
                }
                else
                {
                    // shouldn't happen (peer sync accounting includes extra slots),
                    // but keep the sim deterministic: default input for this tick
                    Bwf.ApplyExtraInput(kv.Key, default(InputPacket));
                }
            }
        }
    }
}
