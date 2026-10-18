# DoppelNames

An add-on for **Doppel** (by RUDYHILL) that gives your bots random names, with a join/leave message
everyone can see.

- Spawn a bot: it gets a random name like **Timmy**, floating above its head, and a pop-up says
  **"Timmy has joined the server"**. The name tag is LabFusion's own player nametag (same look, font,
  height and size scaling as a real player's), so it needs **LabFusion** installed.
- Remove the bot: **"Timmy has left the server"**.
- With LabFusion, the other players in the lobby see the same name tag and the same pop-ups.

It is a separate mod, so Doppel.dll is not touched and Doppel can update normally.

## Host-only mode (server-side bots) - experimental

If **you are the host**, you are the only one who needs the mods. With `ServerSideBots = true` (the default)
every bot you spawn joins the lobby as a fake LabFusion player: other players, who have nothing extra
installed, see the bot's body moving, a normal player nametag, and Fusion's own "joined" / "left" notices.

Limits of this mode:
- It is untested by its author. It may glitch, or print Fusion errors on other players' screens.
- Other players cannot hit or grab the bot, and the bot is not in your own Fusion player list.
- Bots only appear for players who are in the lobby; someone who joins later gets them after ~1.5 seconds.
- If anything goes wrong, set `ServerSideBots = false` in `UserData/MelonPreferences.cfg` and restart.

## When you are not the host (or the mode is off)

Fusion only sends the names to people who have this add-on. Everyone in the lobby who should see the
tags and pop-ups needs **DoppelNames.dll** in their `Mods` folder (and Doppel, same as before). Players
without it just don't see the extra stuff; nothing breaks for them.

## Build it

I could not compile this for you (the game's DLLs are not on my side), so you build it once:

1. Install the **.NET SDK** (6 or newer): https://dotnet.microsoft.com/download
2. Run BONELAB once with MelonLoader, Doppel, BoneLib and LabFusion installed, so MelonLoader creates
   its `Il2CppAssemblies` folder.
3. In this folder, run (change the path to your BONELAB folder):

   ```
   dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\BONELAB"
   ```

4. The build copies `DoppelNames.dll` into `BONELAB\Mods` for you. Copy it to your friends too.

If the build prints errors, send me the error text and I'll fix the code.

## Settings

After the first run, `UserData/MelonPreferences.cfg` has a `[DoppelNames]` section:

| Setting | What it does |
| --- | --- |
| `Enabled` | Turns the add-on off completely. |
| `NameTags` | Show or hide the floating name tags. |
| `JoinLeavePopups` | Show or hide the "has joined/left the server" pop-ups. |
| `ServerSideBots` | Host only: show your bots to players who don't have the mod (experimental). |

To change the wording, search `Mod.cs` for `has joined the server` and `has left the server`.
To change the name list, edit the `Names` array.

## How it works

- Doppel keeps all bots in `BotManager.bots`. The add-on watches that a few times a second and notices
  bots appearing and disappearing.
- The name is also written to Doppel's own `Bot.nameIs`, so Doppel's log lines use it.
- The name tag is created with LabFusion's `RigNameTag` and `RigHeadUI` (the classes Fusion uses for
  players) and moved above the bot's head every frame, so it is the real player nametag, not a copy.
- Over the network it uses its own LabFusion message (registered the same way Doppel registers its own).
  The owner sends `joined` / `left` events plus a short name list every few seconds, so someone who joins
  the lobby later still gets the tags.
- Other players' bots appear on your screen as "puppets". They are matched to the right name by
  (owner id, bot id), which is how Doppel matches them too.

## How host-only mode works

For each bot the add-on makes a fake Fusion player id, tells each remote player directly "this player
joined" (the same message the host sends for real joins), then about 20 times a second sends the bot's
pose as that player, and finally a "player left" message. Everything goes to each remote player one by one,
never as a broadcast, so your own game doesn't spawn a second copy of the bot.

## Known limits

- Without LabFusion the pop-ups still work, but there are no name tags (they come from Fusion).
- Written against Doppel **1.4.0**, LabFusion **1.14.x**, BoneLib **3.2.x**, MelonLoader **0.7.x**. A
  different Doppel version may rename the fields it reads; the log will say so ("looks like a different
  version").
- Changing levels clears the bots, so pop-ups are muted for about 4 seconds after a level change.
- If you spawn a bot while alone in the lobby, the tag shows for you right away. Players who join later
  get the name within a few seconds, but no "joined" pop-up for it.
