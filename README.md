# Ancient Dungeon VR Big Lobby Mod

A BepInEx 5 plugin that raises the multiplayer lobby cap from 4 to 8.

## What it does

**Lobby**
- In the multiplayer menu, the **4** button becomes a 4–8 size button and new lobbies default to 8
  (configurable). While it's selected, each press steps 4 → 5 → 6 → 7 → 8 → 4 and the label shows the
  current size. While 2 or 3 is selected, the label shows the maximum and one press jumps straight to it.
- The public room browser lists rooms up to 8 players and shows real counts (e.g. 6/8).
- The connected-players list gets rows 5–8, with working kick buttons for the host. The list shrinks
  to fit its panel.

**Run start**
- The game's ready list has 4 fixed networked slots. The host tracks players 5–8 itself and waits for
  them to be ready before allowing Start.
- Weapon-pick name badges show players 5–8. The host sends their picks to the other players as a
  mod-only network message, which unmodded players ignore. Each weapon button holds up to 8 names and
  shrinks the name column when more than 4 are showing.

**Gameplay fixes for players 5–8**
- Start floor: players 5–8 share the 4 entrance spawn spots, offset 0.6 m to the side so nobody spawns
  inside another player. Vanilla throws here and aborts dungeon generation for those players.
- Inventory/stat screen: per-player pickup panels and name/mute/teleport rows are added for players
  5–8. Vanilla throws while wiring up the inventory, which breaks weapons and relics. The other-player
  stats panel no longer throws, which used to freeze the inventory half-open.
- Scrying-glass drops (weapon parts, augment crystals) with more than 4 owners now spawn. Vanilla left an
  empty pedestal. The glass's speech bubble still only shows for 4 of the owners (4 networked slots).
- Special enemies that agree on a position across players (King Slime, phase-jumping and teleporting
  enemies) now weigh every player's view, not just the first 4.
- "One for every player" pedestals offer one option per player, up to 8. Vanilla stopped at 4.
  - Options 5–8 come from the same drop table, continuing the game's seeding.
  - The host tells the other players which items are options 5–8. After you take one option, all the
    others grey out for you, as in vanilla (each player's lockout is their own).
  - With 5+ items, options are laid out evenly on a ring.
- Boss stash food (insight): the game already spawns one potato per injured player. Food 5–8 used to
  pile up on the centre spot; it's now spread on a ring.

**Per-player icons**
- Insight board: every upgrade shows a lock (open/closed) for each of up to 8 players. The lock row
  shrinks to fit the button. Each player's purchases were already synced; only the icons were missing.
- Minimap: markers and name labels for players 5–8.

**Difficulty scaling for 5–8 players**

The game scales co-op difficulty on the host. Most of it is linear in player count and already keeps
growing past 4. The parts that stopped at 4, or broke, are extended along the game's own curves:

| What | Vanilla (1–4 players) | 5–8 players with the mod |
|---|---|---|
| Extra enemies per room | + players × room enemies × floor multiplier | unchanged (already linear) |
| Special-room spawns | floor(3.08 × players) | unchanged (already linear) |
| Boss HP (most bosses) | per-boss table for 1–4 | table extended linearly (vanilla threw → unscaled HP, no NG+) |
| Final boss | arms and stage 2 HP × players, stage 3 × (1 + 0.55 per extra player) | unchanged, but HP now saturates at 32767 instead of overflowing to negative |
| Crystal boss spikes / adds | table for 1–4 | table extended linearly (vanilla soft-locked) |
| Deranged adventurer, challenge chests | +HP / +spawns per player | unchanged (already linear) |
| Book boss | +0.6 chance per extra player for extra wave enemies, capped at +2 each | unchanged (saturates around 4–5 players) |
| Arena: extra enemies per wave | 0.25 × players × wave size | continues: 2× wave size at 8 |
| Arena: extra concurrent enemies | 0, 1, 1, 2 | +1 per 2 more players (4 at 8) |
| Arena: spawn interval | ×1, ×0.725, ×0.6, ×0.5 | ×2/players (×0.25 at 8) |
| Arena: relic loot | 2/1, 3/2, 4/2, 4/2 (count/picks) | stays 4/2 (one pedestal per item) |
| Arcane rift: max alive enemies | players + 1 | players + 1, up to 9 (vanilla dropped to 2) |
| Loot table mix | blends toward the multiplayer weights, fully at 4 | unchanged (stays at the 4-player mix) |
| Dungeon size | solo vs co-op layout only, not by count | unchanged (see below) |

**Dungeon size doesn't scale with player count**, in vanilla or with this mod. Generation only switches
between solo and co-op room variants. Every client builds the dungeon locally from the shared seed, so
scaling the size would have to be identical on every client. That's possible as a later addition, since
everyone needs the mod anyway.

## Who needs it

**Everyone in a 5–8 player lobby should install it.**

Only the host's copy decides the room size. Fusion clients size themselves from the room, so unmodded
players can join an 8-player room by code. However, several 4-player assumptions run in each player's
own client. Without the mod, a 5+ player session breaks their inventory (weapons/relics), their spawn
on the first floor, and their stat screen. Unmodded clients also hide 5–8 player rooms in the public
list.

| Fix | Must run on | Players without the mod |
|---|---|---|
| Room size, ready gate, boss/arena/rift scaling, scrying glass, pedestal options 5–8, food layout, kick rows | host | get the result (it's all host-spawned or host-decided) |
| Weapon badges for players 5–8 | host + viewer | see the vanilla 4 badges |
| Pedestal "take one" lockout for options 5–8 | host + each player | only options 1–4 grey out for them, so they could take an extra |
| Inventory, stat screen, start-floor spawn, public list, player list rows, insight locks, minimap | each player | vanilla behaviour; with 5+ players their inventory and first-floor spawn can break |

## Known limitations

- **Host migration** isn't supported for 5+ player sessions. If the host leaves mid-session, the new
  host only tracks 4 players for the ready gate.
- **Other players' stat panels** in the inventory only show as many players as the scene has panels.
- **"Bought by all players" insight upgrades** (e.g. the boss stash) only take effect once every player
  has bought them, as in vanilla. That's rarer with 8 players.
- **Arena relic waves** stay at 4 items / 2 picks, the 4-player amount.
- **Small VR targets:** with 8 players, the lobby list rows and insight locks are scaled down to fit their
  panels.
- **Game updates:** each patch is applied separately and checks that the game code still looks as
  expected. If one of the fixes that keep 5+ player sessions working can't be applied, the mod logs an
  error and keeps lobbies at 4 players instead of creating rooms that would break.
- **What's been tested** (off-screen, non-VR mode, one machine):
  - every patch applies;
  - an 8-slot private room was created on Photon;
  - (1.2) every patch still applies, each at its expected number of sites;
  - the lobby picker, the 8-row player list, 8 insight locks and 8 weapon badges work;
  - the pedestal/food ring layout and scaling formulas give the expected values.

  A real 5–8 player session hasn't been played yet, so pedestal options 5–8 haven't been seen in a
  dungeon, and the mod-to-mod messages added in 1.2 (weapon badges and pedestal options for players
  5–8) haven't been tested between two machines. Please report issues.

## Install

1. Install [BepInEx 5.4.23.x (win_x64)](https://github.com/BepInEx/BepInEx/releases) into the game folder,
   next to `Ancient_Dungeon.exe`.
2. **Linux / Steam Deck (Proton):** set the game's Steam launch options to
   `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
3. Launch the game once so BepInEx creates its folders, then quit.
4. Copy `ADVR.MorePlayers.dll` to `BepInEx/plugins/`.
5. Optional: edit `BepInEx/config/advr.moreplayers.cfg` after the first launch.
   - `MaxPlayers`: 4–8, default 8.
   - `DefaultLobbySize`: 2–8, default 8.

To uninstall, delete `BepInEx/plugins/ADVR.MorePlayers.dll`. To remove BepInEx entirely, also delete
`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

## Build

```bash
dotnet build -c Release -p:GameDir="/path/to/Ancient Dungeon VR"
```

Copy `BepInEx.dll` and `0Harmony.dll` from a BepInEx 5.4.23.x release (`BepInEx/core/`) into `lib/`
first. The output is `bin/Release/net472/ADVR.MorePlayers.dll`. It references the game's own assemblies from
`GameDir`; no game files are included in this repository.

## Notes

- The game has its own `allowMods` lobby flag for its built-in (Lua) mod loader. This plugin doesn't touch it.
- Built with AI assistance (Claude), using decompiled game code for reference only. No game code is
  redistributed.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
