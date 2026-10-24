# Valheim Player Cards

A web page with a card for every player on your Valheim server: the player's **real character, as
it looks in game**, with the equipment they wear, their skills, character statistics and the time
they spent on the server. A JSON API is included, so the cards can also be shown on your own
website or status page.

A dedicated server has no graphics card and cannot draw characters. So each player's game draws its
own character (off screen, in a neutral 3/4 pose, on a transparent background) whenever the visible
equipment changes, and sends the picture to the server together with the rest of the card. The
server keeps the cards and serves the page.

![A player's card: portrait, equipment, skills and statistics](https://github.com/peppeaiello/ValheimPlayerCards/raw/main/docs/screenshots/card.jpg)

The gallery, with an online player, and the same card on a phone:

![Gallery of players](https://github.com/peppeaiello/ValheimPlayerCards/raw/main/docs/screenshots/gallery.jpg)

![Card on a phone](https://github.com/peppeaiello/ValheimPlayerCards/raw/main/docs/screenshots/phone.jpg)

## Features

- **Real portraits**: helmet, armor, cape, belt, weapons, hair, beard and colors, exactly as the
  game renders them, mods included.
- **Equipment** with the game's own item icons and upgrade level.
- **Skills** with their level.
- **Character statistics** from the game: deaths, enemies and bosses killed, pieces built, items
  crafted, distance traveled and more. You choose which ones.
- **Time on the server**: online now, time played, sessions, first and last seen, recent sessions.
  Tracked by the server.
- **Built-in page** for desktop and phone, in English, German, French, Spanish, Portuguese,
  Russian, Polish and Italian: the page follows the visitor's browser language and has a language
  selector. `#/<player>` opens one card, `?lang=de` forces a language, `?embed=1` hides the header
  for use in an iframe.
- **JSON API** with CORS, for status pages and bots.
- Every part of the card can be turned off in the server config, and players can choose not to share
  their skills or statistics.

## How it works

- **Client**: when the visible equipment changes (and when you enter the world), the game renders
  your character with Jötunn's RenderManager and sends the card to the server. At most one card
  every 30 seconds; drawing or sheathing a weapon does not count as a change. The console command
  `playercard` sends a new one right away.
- **Server**: saves the cards in `BepInEx/config/ValheimPlayerCards/<world>/` and serves the page and
  the API on port **3010**. The player name always comes from the connection, never from the upload,
  so nobody can replace someone else's card. Uploads are limited in size and frequency, and only
  valid PNG images are stored.
- The same DLL runs on clients and on the dedicated server. Players without the mod can still join
  and play; they just have no portrait. Servers without the mod receive nothing.

## Installation

Install **ValheimPlayerCards** with a mod manager (r2modman, Gale, Thunderstore app) or by hand, on
**the server and on every player's game**. It needs
[BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and
[Jötunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/).

By hand: copy `ValheimPlayerCards.dll` to `BepInEx/plugins/ValheimPlayerCards/`.

Then open `http://<server address>:3010/`.

- The port must be reachable: open it in the firewall, and if the server runs in Docker publish it
  (for example `-p 3010:3010`).
- The page has no login. To put it on your own domain or behind HTTPS, use a reverse proxy; the page
  uses relative links, so it also works under a sub-path such as `https://example.com/players/`.
  With a proxy on the same machine you can set `Host = 127.0.0.1` so the port is not exposed.

## API

All paths are relative to the page.

| Path | Content |
|---|---|
| `api/players.json` | Server info and every player's card |
| `api/players/<id>.json` | One player's card |
| `portraits/<id>.png` | Portrait (transparent PNG, 512×768 by default) |
| `icons/<item>.png` | Item icon (64×64) |

`<id>` is the player name in lowercase, with accents removed and other characters replaced by `_`.

```json
{
  "server": {
    "title": "My Valheim Server", "world": "Midgard", "version": "0.2.1", "language": "auto",
    "now": 1792880000, "online": 1,
    "features": { "playTime": true, "equipment": true, "itemIcons": true, "skills": true, "stats": true },
    "statList": ["Deaths", "EnemyKills", "BossKills"]
  },
  "players": [
    {
      "id": "bjorn", "name": "Bjorn",
      "portrait": "portraits/bjorn.png?v=1792879117", "portraitWidth": 512, "portraitHeight": 768,
      "cardUpdated": 1792879117,
      "online": true, "onlineSince": 1792877600, "firstSeen": 1792100000, "lastSeen": 1792880000,
      "playSeconds": 415629, "sessions": 48,
      "recent": [{ "start": 1792790000, "end": 1792794000 }],
      "model": 0, "skinColor": "#a6a6a6", "hairColor": "#8c6445",
      "items": [
        { "slot": "helmet", "prefab": "HelmetDrake", "name": "Drake helmet", "icon": "icons/helmetdrake.png", "quality": 2, "variant": 0 }
      ],
      "skills": [{ "id": "Swords", "name": "Swords", "level": 42.5 }],
      "stats": [{ "id": "Deaths", "value": 12 }]
    }
  ]
}
```

- Times are Unix seconds. `playSeconds` includes the session in progress.
- `slot` is one of `helmet`, `chest`, `legs`, `cape`, `utility`, `trinket`, `right`, `left`,
  `backRight`, `backLeft`. Item and skill names are in the language of the player who sent them;
  `prefab` and `id` are the same in every language.
- Statistic ids are Valheim's `PlayerStatType` names. They count every world the character has
  played, not only this server.
- Parts turned off in the config are empty, and `features` says which ones.

## Configuration

`BepInEx/config/Manke.ValheimPlayerCards.cfg`

| Section | Setting | Default | |
|---|---|---|---|
| Server | Enabled | true | Accept cards |
| Server | DataDirectory | *(empty)* | Empty = `BepInEx/config/ValheimPlayerCards/<world>` |
| Server | MaxUploadKB | 700 | Largest card accepted |
| Server | MinIntervalSeconds | 20 | Per player |
| Server | HiddenPlayers | *(empty)* | Names that never appear on the page or in the API |
| Web | Enabled | true | Serve the page and the API |
| Web | Host | * | `127.0.0.1` = only reachable from the same machine |
| Web | Port | 3010 | |
| Web | AllowOrigin | * | `Access-Control-Allow-Origin`; empty = no CORS |
| Web | Language | auto | `auto` (visitor's browser) or a language code such as `en`, `de`, `fr` |
| Web | Title | *(empty)* | Empty = the server name |
| Web | CustomPageDirectory | *(empty)* | Your own `index.html`, `app.js`, `lang.js` or `style.css` |
| Card | ShowPlayTime | true | Online, time played, sessions |
| Card | ShowEquipment | true | |
| Card | ShowItemIcons | true | |
| Card | ShowSkills | true | |
| Card | ShowStats | true | |
| Card | StatList | Deaths, EnemyKills, BossKills, … | Which statistics, in this order |
| Client | Enabled | true | Send your card |
| Client | ShareSkills | true | |
| Client | ShareStats | true | |
| Client | MinIntervalSeconds | 30 | |
| Client | Width / Height | 512 / 768 | Portrait size |
| Client | Supersampling | 2 | Smoother edges |
| Client | TargetKB | 300 | The portrait is scaled down until it fits |
| Client | SaveLocalCopy | true | Also save `BepInEx/ValheimPlayerCards/last-portrait.png` |
| Look | Yaw, Pitch | -5, 0 | Pose angle |
| Look | Ambient, KeyLight, RimLight | 0.5, 0.6, 0.5 | Lighting |

## Languages

Page texts, skill names and statistic names are in `lang.js`. To fix a translation or add a
language, copy `lang.js` from the [repository](https://github.com/peppeaiello/ValheimPlayerCards/tree/main/src/ValheimPlayerCards/web)
to the folder set in `CustomPageDirectory` and edit it; pull requests with new languages are welcome.
Item names, and the names of skills added by other mods, are shown as the player's game sent them,
in that player's language.

## What is sent, and what is not

The client sends: the portrait, the equipped items (name, prefab, upgrade level), their icons the
first time the server needs them, hair, beard and colors, and, if the player allows it, skill levels
and character statistics. Nothing else: no position, inventory, chat, IP address or account
information. The server adds the times it saw the player online.

## Limitations

- Rendering takes a moment on the player's PC (a small hitch, at most once every 30 seconds).
- Portraits use a fixed light, not the light of the place where the player stands.
- Statistics are kept by the game per character, across all worlds.
- The page has no authentication; anyone who can reach the port can read the cards.

## Building from source

Needs the .NET SDK and a Valheim install (game or dedicated server) with BepInEx and Jötunn.

```bash
VALHEIM_DIR=/path/to/Valheim ./build.sh
```

The package for mod managers is written to `dist/`.

## License

MIT
