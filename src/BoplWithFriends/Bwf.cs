using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BoplWithFriends;

/// <summary>
/// Static state + helpers shared by all patches.
/// </summary>
public static class Bwf
{
    public static BepInEx.Configuration.ConfigEntry<int> MaxPlayersConfig;
    public static BepInEx.Logging.ManualLogSource Log;

    /// <summary>Effective max players (host's config value wins at start-packet build time).</summary>
    public static int MaxPlayers
    {
        get
        {
            int v = MaxPlayersConfig?.Value ?? 8;
            return Math.Clamp(v, 2, ModInfo.HARD_MAX_PLAYERS);
        }
    }

    /// <summary>Extension slots decoded from the byte chunk appended to a StartRequestPacket. Slot 5..N → player info.</summary>
    public sealed class ExtraPlayer
    {
        public int Slot;      // player id, 5..
        public ulong SteamId;
        public int Color;
        public int Team;
        public int Ab1, Ab2, Ab3;
    }

    /// <summary>Id(sub)-extras of the most recently received/built start packet.</summary>
    public static readonly Dictionary<int, ExtraPlayer> Extras = new();
    /// <summary>Extras built by the host at encode time (cleared per encode).</summary>
    public static readonly List<ExtraPlayer> PendingExtras = new();

    /// <summary>True while the local client owns the lobby (acting server).</summary>
    public static bool IsHost;

    public static void ResetExtras()
    {
        Extras.Clear();
    }

    // ---- packet codec -----------------------------------------------------

    public const byte Marker = 0xAA;
    public const int VanillaPacketSize = 105;
    /// <summary>Round-tripped bytes per extra player: 8 steam + color + team + 3 abilities.</summary>
    public const int BytesPerExtra = 13;

    public static byte[] EncodeExtrasPacket(List<ExtraPlayer> extras)
    {
        int n = extras?.Count ?? 0;
        if (n == 0)
            return null;
        byte[] data = new byte[2 + n * BytesPerExtra];
        data[0] = Marker;
        data[1] = (byte)n;
        int i = 2;
        foreach (ExtraPlayer e in extras.OrderBy(x => x.Slot))
        {
            byte[] id = BitConverter.GetBytes(e.SteamId);
            Array.Copy(id, 0, data, i, 8); i += 8;
            data[i++] = (byte)e.Color;
            data[i++] = (byte)e.Team;
            data[i++] = (byte)e.Ab1;
            data[i++] = (byte)e.Ab2;
            data[i++] = (byte)e.Ab3;
        }
        return data;
    }

    public static List<ExtraPlayer> DecodeExtras(byte[] data)
    {
        var result = new List<ExtraPlayer>();
        if (data == null || data.Length < 2 || data[0] != Marker)
            return result;
        int count = data[1];
        if (data.Length < 2 + count * BytesPerExtra)
        {
            Log?.LogWarning($"corrupt start-request extension: claimed {count} extras, buffer {data.Length} bytes");
            return result;
        }
        int i = 2;
        for (int k = 0; k < count; k++)
        {
            var e = new ExtraPlayer { Slot = 5 + result.Count };
            e.SteamId = BitConverter.ToUInt64(data, i); i += 8;
            e.Color = data[i++];
            e.Team = data[i++];
            e.Ab1 = data[i++];
            e.Ab2 = data[i++];
            e.Ab3 = data[i++];
            result.Add(e);
        }
        return result;
    }

    // ---- reflection helpers ----------------------------------------------

    static readonly Dictionary<(Type, string), FieldInfo> fieldCache = new();
    static readonly Dictionary<(Type, string), MethodInfo> methodCache = new();

    public static FieldInfo F(Type t, string name)
    {
        var key = (t, name);
        if (!fieldCache.TryGetValue(key, out var fi))
        {
            fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 ?? throw new MissingFieldException(t.Name, name);
            fieldCache[key] = fi;
        }
        return fi;
    }

    public static object Get(object o, Type t, string name) => F(t, name).GetValue(o);
    public static object Get(Type t, string name) => F(t, name).GetValue(null);
    public static void Set(object o, Type t, string name, object v) => F(t, name).SetValue(o, v);
    public static void Set(Type t, string name, object v) => F(t, name).SetValue(null, v);

    public static MethodInfo M(Type t, string name)
    {
        var key = (t, name);
        if (!methodCache.TryGetValue(key, out var mi))
        {
            mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 ?? throw new MissingMethodException(t.Name, name);
            methodCache[key] = mi;
        }
        return mi;
    }

    // ---- misc -------------------------------------------------------------

    public static void ApplyExtraInput(int slot, InputPacket input)
    {
        Player player = PlayerHandler.Get().GetPlayer(slot);
        player?.OverrideInputWithNetworkInput(input);
    }

    public static void ResetExtraInputQueues()
    {
        InputPatches.ResetExtraInputQueues();
    }

    public static void Info(object o) => Log?.LogInfo(o ?? "null");
    public static void Warn(object o) => Log?.LogWarning(o ?? "null");
    public static void Error(object o) => Log?.LogError(o ?? "null");
}
