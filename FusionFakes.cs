// FusionFakes - "server side" bots.
//
// Goal: you are the HOST and the only one with these mods. Everyone else has stock LabFusion and should still
// see your Doppel bots, with a normal player nametag and Fusion's own "joined" / "left" notices.
//
// How: to LabFusion, a player is just a PlayerID that the host announces, followed by a stream of pose
// messages tagged with that player's small id. So for every bot we
//   1. make a fake PlayerID (fake platform id, a free small id, the bot's name as Username),
//   2. tell each remote player "this player joined" (the same ConnectionResponse message the host sends
//      for real joins), and later "this player left" (the same Disconnect message),
//   3. about 20 times a second read the bot's rig into a Fusion RigPose (exactly what Fusion does for a real
//      player) and send it to each remote player with the fake id as the sender.
//
// Why everything is sent to each remote player one by one instead of broadcast:
//   a broadcast would also be handled on the host itself, which would spawn a second Fusion body on top of
//   the real bot (and register the fake id in the host's player list). Sending directly keeps the host out
//   of it: to the host this is still just a Doppel bot, plus the nametag from this add-on.
//
// What does NOT work (yet): other players hitting a bot, grabbing it, voice chat, the bot showing in the
// host's own Fusion player list. It is written against LabFusion 1.14.x and was never run by its author, so
// treat it as experimental. It can be switched off with ServerSideBots = false in MelonPreferences.cfg.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LabFusion.Data;
using LabFusion.Entities;
using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Representation;
using MelonLoader;
using UnityEngine;

namespace DoppelNames
{
    internal static class FusionFakes
    {
        // Platform ids for fake players. Real ones (Steam etc.) are nowhere near this range.
        const ulong FakePlatformBase = 0x0D0BBE1000000000UL;
        static ulong counter;

        // A player has to have been around this long before we introduce bots to them (seconds),
        // so their own connection has finished. Poses start a little after the introduction.
        const float IntroDelay = 1.5f;
        const float PoseDelay = 0.6f;

        sealed class Fake
        {
            public ulong PlatformID;
            public byte SmallID;
            public PlayerID Id;
            public string Name;
            public string Barcode;
            public SerializedAvatarStats Stats;
            public RigSkeleton Skeleton;
            public readonly RigPose Pose = new RigPose();
            public float Created;
            public int Failures;
            public int PosesSent;
            public float NextReport;

            // remote player platform id -> when we introduced this bot to them
            public readonly Dictionary<ulong, float> Introduced = new Dictionary<ulong, float>();
        }

        static readonly List<Fake> all = new List<Fake>();
        static readonly Dictionary<ulong, float> firstSeen = new Dictionary<ulong, float>();
        static bool warnedNoIds;

        /// <summary>Why the last Add() returned null (for the log).</summary>
        public static string LastReason = "";

        // ------------------------------------------------------------------ public surface (used by Mod)

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsHosting()
        {
            try { return NetworkInfo.HasServer && NetworkInfo.IsHost; }
            catch { return false; }
        }

        /// <summary>
        /// Creates the fake player for a bot. Returns null if the bot is not ready yet (no avatar, say);
        /// the caller just tries again a moment later. Nothing is sent yet: Tick() introduces it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object Add(string name, object rig)
        {
            var rm = rig as Il2CppSLZ.Marrow.RigManager;
            if (rm == null) { LastReason = "the bot has no rig yet"; return null; }
            if (rm.avatar == null) { LastReason = "the bot has no avatar yet"; return null; }
            if (rm.physicsRig == null) { LastReason = "the bot has no physics rig yet"; return null; }
            if (rm.AvatarCrate == null) { LastReason = "the bot's avatar has no crate"; return null; }

            string barcode = rm.AvatarCrate.Barcode.ID;
            if (string.IsNullOrEmpty(barcode)) { LastReason = "the bot's avatar has no barcode"; return null; }

            var local = PlayerIDManager.LocalID;
            if (local == null) { LastReason = "your own player id is missing"; return null; }

            byte? small = PlayerIDManager.GetUniquePlayerID();
            if (!small.HasValue)
            {
                LastReason = "no free player slot";
                if (!warnedNoIds) { warnedNoIds = true; MelonLogger.Warning("DoppelNames: no free player slot for a server-side bot."); }
                return null;
            }

            // Start from the host's own metadata (level, loading state...), then make it the bot's.
            var meta = new Dictionary<string, string>(local.Metadata.Metadata.LocalDictionary);
            meta[nameof(PlayerMetadata.Username)] = name;
            meta[nameof(PlayerMetadata.Nickname)] = "";
            meta[nameof(PlayerMetadata.Description)] = "";
            // Fusion stores this flag as JSON, so it must be lowercase "false" ("False" makes the receiver throw and never build the body).
            meta[nameof(PlayerMetadata.Loading)] = "false";
            meta[nameof(PlayerMetadata.PermissionLevel)] = PermissionLevel.DEFAULT.ToString();

            var f = new Fake
            {
                PlatformID = FakePlatformBase + (++counter),
                SmallID = small.Value,
                Name = name,
                Barcode = barcode,
                Stats = new SerializedAvatarStats(rm.avatar),
                Created = Time.unscaledTime,
            };
            f.Id = new PlayerID(f.PlatformID, f.SmallID, meta, new List<string>());

            // Keep a real player from being given this small id. (We do NOT put the fake id in the host's
            // player list: Fusion's join code walks that list and expects every entry to be a real player.)
            PlayerIDManager.ReserveSmallID(f.SmallID);

            all.Add(f);
            MelonLogger.Msg($"DoppelNames: server-side bot '{name}' created (player slot {f.SmallID}).");
            return f;
        }

        /// <summary>Call about 20 times a second for each bot: introduces it to new players, then streams its pose.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Tick(object handle, object rig)
        {
            var f = (Fake)handle;
            if (f.Failures > 40) return;

            try
            {
                float now = Time.unscaledTime;
                var recipients = Recipients(now);
                if (recipients.Count == 0)
                {
                    if (now >= f.NextReport) { f.NextReport = now + 10f; MelonLogger.Msg($"DoppelNames: '{f.Name}': waiting, no other players in the lobby."); }
                    return;
                }

                // Introduce the bot to players who have not met it yet.
                foreach (var r in recipients)
                {
                    if (f.Introduced.ContainsKey(r)) continue;
                    if (now - firstSeen[r] < IntroDelay) continue;

                    // Players present when the bot was made get the normal "joined" notice; someone who
                    // joins the lobby later just gets the bot quietly.
                    bool notify = now - f.Created < IntroDelay + 3f;
                    SendJoin(f, r, notify);
                    f.Introduced[r] = now;
                    MelonLogger.Msg($"DoppelNames: introduced '{f.Name}' (slot {f.SmallID}) to player {r}" + (notify ? " with a join notice." : "."));
                }

                var rm = rig as Il2CppSLZ.Marrow.RigManager;
                if (rm == null) return;

                // Anyone ready for poses?
                var ready = new List<ulong>();
                foreach (var kv in f.Introduced)
                {
                    if (now - kv.Value >= PoseDelay && recipients.Contains(kv.Key)) ready.Add(kv.Key);
                }
                if (ready.Count == 0)
                {
                    if (now >= f.NextReport) { f.NextReport = now + 10f; MelonLogger.Msg($"DoppelNames: '{f.Name}': {recipients.Count} player(s) here, none ready for poses yet."); }
                    return;
                }

                if (f.Skeleton == null) f.Skeleton = new RigSkeleton(rm);
                f.Pose.ReadSkeleton(f.Skeleton);

                var data = PlayerPoseUpdateData.Create(f.Pose);
                using (var writer = NetWriter.Create(data.GetSize()))
                {
                    data.Serialize(writer);

                    // The fake id goes in as the sender, which is how the receiver knows whose pose this is.
                    using (var message = NetMessage.Create(NativeMessageTag.PlayerPoseUpdate, writer, CommonMessageRoutes.UnreliableToOtherClients, f.SmallID))
                    {
                        foreach (var r in ready) MessageSender.SendFromServer(r, NetworkChannel.Unreliable, message);
                    }
                }

                f.PosesSent++;
                if (now >= f.NextReport)
                {
                    f.NextReport = now + 10f;
                    MelonLogger.Msg($"DoppelNames: '{f.Name}': {f.PosesSent} pose updates sent so far to {ready.Count} player(s).");
                }
            }
            catch (Exception e)
            {
                f.Skeleton = null;   // rebuild it next time (the rig may have been replaced)
                if (++f.Failures == 41) MelonLogger.Warning("DoppelNames: server-side bot '" + f.Name + "' stopped updating: " + e.Message);
            }
        }

        /// <summary>The bot went away: tell everyone who met it that the player left.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Remove(object handle)
        {
            var f = (Fake)handle;
            all.Remove(f);

            try
            {
                var recipients = Recipients(Time.unscaledTime);
                foreach (var r in recipients)
                {
                    if (f.Introduced.ContainsKey(r)) SendLeave(f, r);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("DoppelNames: could not announce a bot leaving: " + e.Message);
            }

            PlayerIDManager.UnreserveSmallID(f.SmallID);
        }

        /// <summary>We are no longer hosting (server closed): nothing to tell anyone, just release the slot.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Forget(object handle)
        {
            var f = (Fake)handle;
            all.Remove(f);
            PlayerIDManager.UnreserveSmallID(f.SmallID);
        }

        // ------------------------------------------------------------------ messages

        // Same message the host sends for a real join (ConnectionSender.SendPlayerJoin), but to one player only.
        static void SendJoin(Fake f, ulong recipient, bool notify)
        {
            var response = ConnectionResponseData.Create(f.Id, f.Barcode, f.Stats, notify);

            using (var writer = NetWriter.Create())
            {
                writer.SerializeValue(ref response);

                using (var message = NetMessage.Create(NativeMessageTag.ConnectionResponse, writer, CommonMessageRoutes.None))
                {
                    MessageSender.SendFromServer(recipient, NetworkChannel.Reliable, message);
                }
            }
        }

        // Same message the host sends when a player disconnects (ConnectionSender.SendDisconnect), minus the
        // part that closes a network connection, since a fake player has none.
        static void SendLeave(Fake f, ulong recipient)
        {
            var disconnect = DisconnectMessageData.Create(f.PlatformID, "");

            using (var writer = NetWriter.Create())
            {
                writer.SerializeValue(ref disconnect);

                using (var message = NetMessage.Create(NativeMessageTag.Disconnect, writer, CommonMessageRoutes.None))
                {
                    MessageSender.SendFromServer(recipient, NetworkChannel.Reliable, message);
                }
            }
        }

        // ------------------------------------------------------------------ who is in the lobby

        // Every remote (non-host) real player right now. Also tracks when each was first seen, and forgets
        // players who left.
        static List<ulong> Recipients(float now)
        {
            var result = new List<ulong>();
            var present = new HashSet<ulong>();

            foreach (var id in PlayerIDManager.PlayerIDs)
            {
                if (id.IsMe) continue;

                ulong p = id.PlatformID;
                present.Add(p);
                result.Add(p);
                if (!firstSeen.ContainsKey(p)) firstSeen[p] = now;
            }

            List<ulong> gone = null;
            foreach (var k in firstSeen.Keys)
            {
                if (!present.Contains(k)) (gone ?? (gone = new List<ulong>())).Add(k);
            }

            if (gone != null)
            {
                foreach (var k in gone)
                {
                    firstSeen.Remove(k);
                    foreach (var f in all) f.Introduced.Remove(k);
                }
            }

            return result;
        }
    }
}
