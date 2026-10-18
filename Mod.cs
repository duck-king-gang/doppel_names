// DoppelNames - an add-on for the Doppel bot mod (by RUDYHILL).
//
// What it does
//   * Gives every bot YOU spawn a random name ("Timmy") and floats it above the bot's head, using
//     LabFusion's own player nametag, so it looks exactly like a player's nametag.
//   * Shows "Timmy has joined the server" when you spawn a bot and
//     "Timmy has left the server" when it is removed.
//   * With LabFusion, sends the same name and messages to the other players in the lobby, so they see
//     the same nametag on their copy of the bot and the same pop-ups. Everyone who wants to see them
//     needs this add-on installed (players without it just see nothing extra; nothing breaks).
//
// How it works (and why it never edits Doppel.dll)
//   Doppel keeps every bot in a static dictionary, BotManager.bots. This mod looks at that dictionary a
//   few times a second (through reflection, so it does not need Doppel to compile) and notices bots
//   appearing and disappearing. Names are also written into Bot.nameIs so Doppel's own log lines use them.
//   Other players' bots show up on your screen as "puppets"; they are matched to a name by
//   (owner small-id, bot id), which is exactly how Doppel itself matches them.
//   The nametag is LabFusion's RigNameTag + RigHeadUI: the same prefab, font, size, height and
//   facing-the-camera behaviour that Fusion uses for real players. Each frame we hand it the bot's rig.
//
// Built against: MelonLoader 0.7.x, BoneLib 3.2.x, LabFusion 1.14.x, Doppel 1.4.0.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BoneLib.Notifications;
using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.SDK.Modules;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DoppelNames.Mod), "DoppelNames", "1.0.0", "you")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace DoppelNames
{
    public class Mod : MelonMod
    {
        // ------------------------------------------------------------------ settings
        MelonPreferences_Entry<bool> prefEnabled;
        MelonPreferences_Entry<bool> prefTags;
        MelonPreferences_Entry<bool> prefPopups;
        MelonPreferences_Entry<bool> prefServer;

        // ------------------------------------------------------------------ Doppel, found by reflection
        FieldInfo botsF, nameIsF, netOwnerF, netIdF, rigF;
        bool doppelReady;
        float nextResolve;

        // ------------------------------------------------------------------ state
        sealed class Entry
        {
            public object Bot;
            public string Name;
            public bool Puppet;
            public bool NetAnnounced;
            public int Id;
            public float Born;
            public object TagHandle;      // a FusionTags.Handle (kept as object so this class never needs Fusion)
            public object Fake;           // a FusionFakes.Fake: the bot as a fake Fusion player (host-only mode)
            public float FakeNextTry;
            public bool FakeWarned;
        }

        readonly Dictionary<IntPtr, Entry> tracked = new Dictionary<IntPtr, Entry>();
        readonly Dictionary<int, string> remoteNames = new Dictionary<int, string>(); // (owner << 8 | id) -> name
        readonly System.Random rng = new System.Random();

        float nextPoll, nextSnapshot, nextFakeTick, quietUntil;
        bool fusionPresent;   // LabFusion is installed (needed for the nametag prefab)
        bool fusionReady;     // our network message is registered (needed to tell other players)
        bool loggedError;

        internal struct Inbound { public int Owner; public byte[] Bytes; }
        internal static readonly ConcurrentQueue<Inbound> Inbox = new ConcurrentQueue<Inbound>();

        const byte KindJoin = 1, KindLeave = 2, KindSnapshot = 3;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // ------------------------------------------------------------------ MelonLoader hooks
        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("DoppelNames");
            prefEnabled = cat.CreateEntry("Enabled", true, "Turn the whole add-on on or off");
            prefTags = cat.CreateEntry("NameTags", true, "Show name tags above bots (needs LabFusion)");
            prefPopups = cat.CreateEntry("JoinLeavePopups", true, "Show 'has joined/left the server' pop-ups");
            prefServer = cat.CreateEntry("ServerSideBots", true,
                "When you are the host: make bots show up for players who do NOT have this mod (experimental)");
            LoggerInstance.Msg("DoppelNames loaded. Waiting for Doppel.");
        }

        public override void OnLateInitializeMelon()
        {
            // All mods are loaded by now, so LabFusion (if installed) is available.
            fusionPresent = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "LabFusion");
            if (!fusionPresent)
            {
                LoggerInstance.Warning("LabFusion is not installed: pop-ups will work, but name tags need LabFusion.");
                return;
            }

            try
            {
                fusionReady = FusionBridge.Start();
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not register the network message; names stay on your screen only. " + e.Message);
            }
        }

        public override void OnDeinitializeMelon()
        {
            foreach (var e in tracked.Values) { DestroyTag(e); DropFake(e, false); }
            tracked.Clear();
            if (fusionReady) { try { FusionBridge.Stop(); } catch { } }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            // Bots are cleared when a level unloads. Stay quiet for a moment so you don't get a wall
            // of "has left the server" pop-ups every time you change maps.
            quietUntil = Time.unscaledTime + 4f;
        }

        public override void OnUpdate()
        {
            if (!prefEnabled.Value)
            {
                if (tracked.Count > 0) { foreach (var e in tracked.Values) { DestroyTag(e); DropFake(e, false); } tracked.Clear(); }
                return;
            }

            float now = Time.unscaledTime;

            if (!doppelReady)
            {
                if (now < nextResolve) return;
                nextResolve = now + 2f;
                doppelReady = ResolveDoppel();
                return;
            }

            try
            {
                if (fusionReady) DrainInbox();

                if (now >= nextPoll)
                {
                    nextPoll = now + 0.2f;
                    Poll(now);
                }

                if (fusionReady && now >= nextSnapshot)
                {
                    nextSnapshot = now + 4f;
                    if (!ServerSideActive()) SendSnapshot();
                }

                if (now >= nextFakeTick)
                {
                    nextFakeTick = now + 0.05f;     // 20 times a second, like Fusion's own pose sending
                    TickFakes();
                }

            }
            catch (Exception e)
            {
                if (!loggedError) { loggedError = true; LoggerInstance.Error("DoppelNames hit an error (logged once): " + e); }
            }
        }

        public override void OnLateUpdate()
        {
            // Same job Fusion's NetworkPlayer does for real players: every frame, put the tag above the head.
            if (!doppelReady || !fusionPresent || rigF == null || !prefEnabled.Value || !prefTags.Value) return;

            foreach (var e in tracked.Values)
            {
                if (e.TagHandle == null) continue;
                try { FusionTags.Follow(e.TagHandle, rigF.GetValue(e.Bot)); } catch { }
            }
        }

        // ------------------------------------------------------------------ finding Doppel
        static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(full, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        bool ResolveDoppel()
        {
            var botManager = FindType("ThirdEyeBonelabMod.Bots.BotManager");
            var bot = FindType("ThirdEyeBonelabMod.Bots.Bot");
            if (botManager == null || bot == null) return false; // Doppel not loaded (yet)

            botsF = botManager.GetField("bots", Any);
            nameIsF = bot.GetField("nameIs", Any);
            netOwnerF = bot.GetField("netOwner", Any);
            netIdF = bot.GetField("netId", Any);
            rigF = bot.GetField("rig", Any);

            if (botsF == null || netOwnerF == null || netIdF == null)
            {
                LoggerInstance.Error("Found Doppel, but it looks like a different version than 1.4.0 " +
                                     "(a field this add-on needs is missing). Names are disabled.");
                nextResolve = float.MaxValue;
                return false;
            }

            LoggerInstance.Msg("Hooked into Doppel.");
            return true;
        }

        // ------------------------------------------------------------------ watching the bots
        void Poll(float now)
        {
            var dict = botsF.GetValue(null) as IDictionary;
            if (dict == null) return;

            var live = new List<KeyValuePair<IntPtr, object>>(dict.Count);
            foreach (DictionaryEntry de in dict) live.Add(new KeyValuePair<IntPtr, object>((IntPtr)de.Key, de.Value));
            var liveKeys = new HashSet<IntPtr>(live.Select(k => k.Key));
            bool quiet = now < quietUntil;

            // Bots that went away.
            foreach (var key in tracked.Keys.ToList())
            {
                if (liveKeys.Contains(key)) continue;
                var gone = tracked[key];
                tracked.Remove(key);
                DestroyTag(gone);
                DropFake(gone, false);     // tells remote players the "player" left (even during a level change)
                if (gone.Puppet || quiet || string.IsNullOrEmpty(gone.Name)) continue;

                Popup(gone.Name + " has left the server");
                if (fusionReady && gone.NetAnnounced && gone.Id > 0 && !ServerSideActive()) FusionSend(Pack(KindLeave, gone.Id, gone.Name));
            }

            // New bots, and keeping known ones up to date.
            foreach (var kv in live)
            {
                if (!tracked.TryGetValue(kv.Key, out var e))
                {
                    e = new Entry { Bot = kv.Value, Born = now, Puppet = IsPuppet(kv.Value) };
                    tracked[kv.Key] = e;

                    if (!e.Puppet)
                    {
                        e.Name = PickName();
                        nameIsF?.SetValue(e.Bot, e.Name);          // Doppel's own messages use the name too
                        if (!quiet) Popup(e.Name + " has joined the server");
                    }
                }

                if (e.Puppet) ResolvePuppetName(e);
                else
                {
                    EnsureFake(e, now);
                    // Our own join/leave messages are only for other DoppelNames users. When the bot is a
                    // fake Fusion player, Fusion shows its native notices to everyone instead.
                    if (!e.NetAnnounced && !ServerSideActive()) TryAnnounceOverNetwork(e, now, quiet);
                }

                EnsureTag(e);
            }
        }

        // ------------------------------------------------------------------ server-side bots (host only)
        // True when we are hosting and should present bots to other players as fake Fusion players.
        bool ServerSideActive()
        {
            if (!fusionPresent || !prefServer.Value) return false;
            try { return FusionFakes.IsHosting(); } catch { return false; }
        }

        void EnsureFake(Entry e, float now)
        {
            if (e.Fake != null || e.Puppet || string.IsNullOrEmpty(e.Name) || now < e.FakeNextTry) return;
            if (!ServerSideActive()) return;      // not hosting (or the setting is off): nothing to do

            e.FakeNextTry = now + 0.5f;     // the bot may not have its avatar yet; just try again shortly
            try
            {
                e.Fake = FusionFakes.Add(e.Name, rigF?.GetValue(e.Bot));
                if (e.Fake == null && !e.FakeWarned && now - e.Born > 5f)
                {
                    e.FakeWarned = true;
                    LoggerInstance.Warning("Server-side bot '" + e.Name + "' still not created: " + FusionFakes.LastReason);
                }
            }
            catch (Exception ex)
            {
                e.FakeNextTry = now + 5f;
                LoggerInstance.Warning("Could not make a server-side bot: " + ex.Message);
            }
        }

        // quiet = true: just free the slot. Otherwise remote players are told the "player" left (if we still host).
        void DropFake(Entry e, bool quiet)
        {
            if (e.Fake == null) return;
            var f = e.Fake;
            e.Fake = null;
            try { if (quiet || !FusionFakes.IsHosting()) FusionFakes.Forget(f); else FusionFakes.Remove(f); }
            catch (Exception ex) { LoggerInstance.Warning("Could not remove a server-side bot: " + ex.Message); }
        }

        void TickFakes()
        {
            if (!fusionPresent) return;

            bool active = ServerSideActive();
            foreach (var e in tracked.Values)
            {
                if (e.Fake == null) continue;

                if (!active)    // turned off in settings, or the server closed
                {
                    DropFake(e, false);
                    continue;
                }

                try { FusionFakes.Tick(e.Fake, rigF?.GetValue(e.Bot)); } catch { }
            }
        }

        bool IsPuppet(object bot)
        {
            // Doppel: a bot is somebody else's copy ("puppet") when netOwner >= 0; your own bots are -1.
            return Convert.ToInt32(netOwnerF.GetValue(bot)) >= 0;
        }

        void ResolvePuppetName(Entry e)
        {
            if (!string.IsNullOrEmpty(e.Name)) return;
            int owner = Convert.ToInt32(netOwnerF.GetValue(e.Bot));
            int id = Convert.ToInt32(netIdF.GetValue(e.Bot));
            if (remoteNames.TryGetValue((owner << 8) | (id & 0xFF), out var name))
            {
                e.Name = name;
                nameIsF?.SetValue(e.Bot, name);
            }
        }

        void TryAnnounceOverNetwork(Entry e, float now, bool quiet)
        {
            if (!fusionReady) { e.NetAnnounced = true; return; }
            if (!FusionBridge.IsOnline()) return;                  // alone in the lobby: try again later

            // Doppel gives each of your bots a small id (1-255) once it is shared with the lobby.
            int id = Convert.ToInt32(netIdF.GetValue(e.Bot));
            if (id <= 0 || id > 255)
            {
                if (now - e.Born > 8f) e.NetAnnounced = true;      // never got an id; give up quietly
                return;
            }

            e.Id = id;
            e.NetAnnounced = true;
            if (!quiet) FusionSend(Pack(KindJoin, id, e.Name));
        }

        // ------------------------------------------------------------------ network
        void SendSnapshot()
        {
            if (!FusionBridge.IsOnline()) return;
            var items = new List<KeyValuePair<int, string>>();
            foreach (var e in tracked.Values)
            {
                if (e.Puppet || string.IsNullOrEmpty(e.Name)) continue;
                int id = Convert.ToInt32(netIdF.GetValue(e.Bot));
                if (id > 0 && id <= 255) { e.Id = id; items.Add(new KeyValuePair<int, string>(id, e.Name)); }
            }
            if (items.Count == 0) return;
            FusionSend(PackSnapshot(items));
        }

        static void FusionSend(byte[] payload)
        {
            try { FusionBridge.Send(payload); } catch { }
        }

        void DrainInbox()
        {
            while (Inbox.TryDequeue(out var msg))
            {
                try { Handle(msg.Owner, msg.Bytes); } catch (Exception e) { LoggerInstance.Warning("Bad message: " + e.Message); }
            }
        }

        void Handle(int owner, byte[] bytes)
        {
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                byte kind = r.ReadByte();
                int count = r.ReadByte();

                if (kind == KindSnapshot)
                {
                    var keep = new HashSet<int>();
                    for (int i = 0; i < count; i++)
                    {
                        int id = r.ReadByte();
                        string name = Clean(r.ReadString());
                        if (name.Length == 0) continue;
                        int key = (owner << 8) | id;
                        keep.Add(key);
                        remoteNames[key] = name;
                    }
                    foreach (var stale in remoteNames.Keys.Where(k => (k >> 8) == owner && !keep.Contains(k)).ToList())
                        remoteNames.Remove(stale);
                    return;
                }

                if (count < 1) return;
                int bid = r.ReadByte();
                string bname = Clean(r.ReadString());
                if (bname.Length == 0) return;

                if (kind == KindJoin)
                {
                    remoteNames[(owner << 8) | bid] = bname;
                    if (Time.unscaledTime >= quietUntil) Popup(bname + " has joined the server");
                }
                else if (kind == KindLeave)
                {
                    remoteNames.Remove((owner << 8) | bid);
                    if (Time.unscaledTime >= quietUntil) Popup(bname + " has left the server");
                }
            }
        }

        static byte[] Pack(byte kind, int id, string name)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(kind);
                w.Write((byte)1);
                w.Write((byte)id);
                w.Write(name);
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] PackSnapshot(List<KeyValuePair<int, string>> items)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(KindSnapshot);
                w.Write((byte)Math.Min(items.Count, 255));
                foreach (var it in items.Take(255)) { w.Write((byte)it.Key); w.Write(it.Value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        // Names come over the network, so never trust them: plain letters/digits only, short.
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.') sb.Append(c);
                if (sb.Length >= 20) break;
            }
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------ pop-ups
        void Popup(string message)
        {
            LoggerInstance.Msg(message);
            if (!prefPopups.Value) return;
            try
            {
                Notifier.Send(new Notification
                {
                    Title = "Server",
                    Message = message,
                    Type = (NotificationType)0,
                    PopupLength = 2.5f,
                    ShowTitleOnPopup = true
                });
            }
            catch { }
        }

        // ------------------------------------------------------------------ name tags
        void EnsureTag(Entry e)
        {
            if (!fusionPresent) return;

            bool want = prefTags.Value && !string.IsNullOrEmpty(e.Name);
            if (!want)
            {
                if (e.TagHandle != null) DestroyTag(e);
                return;
            }

            try
            {
                if (e.TagHandle == null) e.TagHandle = FusionTags.Create(e.Name);
                else FusionTags.SetName(e.TagHandle, e.Name);
            }
            catch (Exception ex)
            {
                if (!loggedError) { loggedError = true; LoggerInstance.Error("Could not make a name tag (logged once): " + ex); }
            }
        }

        void DestroyTag(Entry e)
        {
            if (e.TagHandle == null) return;
            try { FusionTags.Destroy(e.TagHandle); } catch { }
            e.TagHandle = null;
        }

        // ------------------------------------------------------------------ random names
        static readonly string[] Names =
        {
            "Timmy","Jake","Mason","Liam","Noah","Ethan","Logan","Lucas","Owen","Caleb","Dylan","Tyler","Ryan","Aiden",
            "Jayden","Carter","Wyatt","Hunter","Eli","Landon","Connor","Colton","Brody","Gavin","Austin","Chase","Cole",
            "Nolan","Jace","Parker","Blake","Declan","Finn","Grant","Henry","Isaac","Jasper","Kai","Leo","Miles","Nate",
            "Oscar","Pete","Quinn","Rex","Sam","Theo","Vince","Zane","Max","Emma","Olivia","Ava","Mia","Chloe","Ella",
            "Lily","Zoe","Nora","Hazel","Piper","Ruby","Stella","Violet","Willow","Ivy","Maya","Layla","Aria","Brooke",
            "Cara","Dana","Erin","Faith","Grace","Hannah","Iris","Jade","Kylie","Lena","Molly","Nina","Paige","Riley",
            "Sadie","Tessa","Uma","Vera","Wendy","Alex","Casey","Drew","Jamie","Jordan","Morgan","Sky","Taylor","Avery",
            "Bobby","Danny","Eddie","Freddie","Georgie","Jimmy","Kenny","Larry","Mikey","Ricky","Tommy","Willy","Zach"
        };

        string PickName()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in tracked.Values) if (!string.IsNullOrEmpty(e.Name)) used.Add(e.Name);
            foreach (var n in remoteNames.Values) used.Add(n);

            for (int tries = 0; tries < 40; tries++)
            {
                var n = Names[rng.Next(Names.Length)];
                if (!used.Contains(n)) return n;
            }
            // Every name is taken (lots of bots): add a number so tags stay unique.
            return Names[rng.Next(Names.Length)] + rng.Next(2, 99);
        }
    }

    // ====================================================================================================
    // Everything that touches LabFusion lives down here, in its own classes. The game only loads these
    // when LabFusion is installed (Mod.OnLateInitializeMelon checks first), so the add-on still works
    // single-player without Fusion.
    // ====================================================================================================

    public class NamesData : INetSerializable
    {
        public byte[] bytes;
        public int? GetSize() => (bytes == null ? 0 : bytes.Length) + 8;
        public void Serialize(INetSerializer serializer) { serializer.SerializeValue(ref bytes); }
    }

    public class NamesMessage : ModuleMessageHandler
    {
        protected override void OnHandleMessage(ReceivedMessage received)
        {
            try
            {
                var sender = received.Sender;
                if (!sender.HasValue) return;

                object senderValue = sender.Value;
                int owner;
                if (senderValue is IConvertible c) owner = c.ToInt32(null);
                else
                {
                    var f = senderValue.GetType().GetProperty("SmallID")?.GetValue(senderValue);
                    if (f == null) return;
                    owner = Convert.ToInt32(f);
                }

                var data = received.ReadData<NamesData>();
                if (data?.bytes == null || data.bytes.Length < 2 || data.bytes.Length > 8192) return;

                Mod.Inbox.Enqueue(new Mod.Inbound { Owner = owner, Bytes = data.bytes });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("DoppelNames: could not read a message: " + e.Message);
            }
        }
    }

    internal static class FusionBridge
    {
        // Our own message number. It must be different from Doppel's (5929072799880532017).
        const long Tag = 7391645520138472951L;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Start()
        {
            // This mirrors how Doppel registers its own handler on this Fusion version.
            if (ModuleMessageManager.TagToHandlerLookup.TryGetValue(Tag, out var old))
            {
                ModuleMessageManager.TagToHandlerLookup.Remove(Tag);
                ModuleMessageManager.Handlers.Remove(old);
                ModuleMessageManager.TypeToHandlerLookup.Remove(old.GetType());
            }

            var handler = new NamesMessage();

            var tagField = typeof(ModuleMessageHandler).GetField("_tag", BindingFlags.Instance | BindingFlags.NonPublic);
            if (tagField == null) throw new Exception("this Fusion keeps its handlers' numbers some other way");
            tagField.SetValue(handler, Tag);

            var attrs = typeof(MessageHandler).GetProperty("NetAttributes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (attrs != null)
                attrs.SetValue(handler, Array.CreateInstance(attrs.PropertyType.GetElementType(), 0));

            ModuleMessageManager.Handlers.Add(handler);
            ModuleMessageManager.TypeToHandlerLookup[typeof(NamesMessage)] = handler;
            ModuleMessageManager.TagToHandlerLookup[Tag] = handler;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Stop()
        {
            if (ModuleMessageManager.TagToHandlerLookup.TryGetValue(Tag, out var h))
            {
                ModuleMessageManager.TagToHandlerLookup.Remove(Tag);
                ModuleMessageManager.Handlers.Remove(h);
                ModuleMessageManager.TypeToHandlerLookup.Remove(h.GetType());
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsOnline()
        {
            try { return NetworkInfo.HasServer && PlayerIDManager.HasOtherPlayers; }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Send(byte[] payload)
        {
            var data = new NamesData { bytes = payload };
            MessageRelay.RelayModule<NamesMessage, NamesData>(data, CommonMessageRoutes.ReliableToOtherClients);
        }
    }
    // The real LabFusion player nametag (v1.14.x): RigNameTag is the text prefab, RigHeadUI is the layout that
    // floats it above a head, scales it with avatar height and turns it to face you. Fusion drives both
    // from NetworkPlayer; we drive them for each bot.
    internal static class FusionTags
    {
        sealed class Handle
        {
            public LabFusion.Entities.RigHeadUI HeadUI;
            public LabFusion.Entities.RigNameTag Tag;
            public int Failures;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object Create(string name)
        {
            var tag = new LabFusion.Entities.RigNameTag { Username = name };
            var headUI = new LabFusion.Entities.RigHeadUI();
            headUI.RegisterElement(tag);   // register first, then Spawn() creates the layout and the tag in it
            headUI.Spawn();
            return new Handle { HeadUI = headUI, Tag = tag };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SetName(object handle, string name)
        {
            var h = (Handle)handle;
            if (h.Tag.Username != name) h.Tag.Username = name;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Follow(object handle, object rig)
        {
            var h = (Handle)handle;
            if (h.Failures > 30) return;                       // gave up on this one; don't spam errors

            var rigManager = rig as Il2CppSLZ.Marrow.RigManager;
            if (rigManager == null) return;

            try
            {
                h.HeadUI.UpdateScale(rigManager);
                h.HeadUI.UpdateTransform(rigManager);
            }
            catch (Exception e)
            {
                if (++h.Failures == 31) MelonLogger.Warning("DoppelNames: a name tag stopped following its bot: " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Destroy(object handle)
        {
            var h = (Handle)handle;
            h.HeadUI.Despawn();     // despawns the layout and the tag inside it
        }
    }
}
