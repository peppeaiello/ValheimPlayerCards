using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ValheimPlayerCards
{
    internal class ItemRecord
    {
        public string slot = "";
        public string prefab = "";
        public string name = "";
        public string token = "";
        public string icon = "";
        public int quality;
        public int variant;
    }

    internal class SkillRecord
    {
        public string id = "";
        public string name = "";
        public float level;
    }

    internal class StatRecord
    {
        public string id = "";
        public float value;
    }

    internal class SessionRecord
    {
        public long start;
        public long end;
    }

    /// <summary>What the server keeps about one player, saved as players/&lt;id&gt;.json.</summary>
    internal class PlayerRecord
    {
        public string id = "";
        public string name = "";
        public long firstSeen;
        public long lastSeen;
        public double playSeconds;
        public int sessions;
        public long openSince;          // start of the session in progress; closed at lastSeen after a crash
        public List<SessionRecord> recent = new List<SessionRecord>();
        public long cardUpdated;
        public int portraitWidth;
        public int portraitHeight;
        public int model;
        public string hair = "";
        public string beard = "";
        public string skinColor = "";
        public string hairColor = "";
        public List<ItemRecord> items = new List<ItemRecord>();
        public List<SkillRecord> skills = new List<SkillRecord>();
        public List<StatRecord> stats = new List<StatRecord>();

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = name,
                ["firstSeen"] = firstSeen, ["lastSeen"] = lastSeen, ["playSeconds"] = playSeconds, ["sessions"] = sessions,
                ["openSince"] = openSince,
                ["recent"] = recent.Select(r => new Dictionary<string, object> { ["start"] = r.start, ["end"] = r.end }).ToList(),
                ["cardUpdated"] = cardUpdated, ["portraitWidth"] = portraitWidth, ["portraitHeight"] = portraitHeight,
                ["model"] = model, ["hair"] = hair, ["beard"] = beard, ["skinColor"] = skinColor, ["hairColor"] = hairColor,
                ["items"] = items.Select(i => new Dictionary<string, object>
                {
                    ["slot"] = i.slot, ["prefab"] = i.prefab, ["name"] = i.name, ["token"] = i.token,
                    ["icon"] = i.icon, ["quality"] = i.quality, ["variant"] = i.variant
                }).ToList(),
                ["skills"] = skills.Select(k => new Dictionary<string, object> { ["id"] = k.id, ["name"] = k.name, ["level"] = k.level }).ToList(),
                ["stats"] = stats.Select(k => new Dictionary<string, object> { ["id"] = k.id, ["value"] = k.value }).ToList()
            };
        }

        public static PlayerRecord FromJson(IDictionary<string, object> o)
        {
            return new PlayerRecord
            {
                id = Json.Str(o, "id"), name = Json.Str(o, "name"),
                firstSeen = Json.Long(o, "firstSeen"), lastSeen = Json.Long(o, "lastSeen"),
                playSeconds = Json.Num(o, "playSeconds"), sessions = Json.Int(o, "sessions"), openSince = Json.Long(o, "openSince"),
                recent = Json.Objects(o, "recent").Select(r => new SessionRecord { start = Json.Long(r, "start"), end = Json.Long(r, "end") }).ToList(),
                cardUpdated = Json.Long(o, "cardUpdated"), portraitWidth = Json.Int(o, "portraitWidth"), portraitHeight = Json.Int(o, "portraitHeight"),
                model = Json.Int(o, "model"), hair = Json.Str(o, "hair"), beard = Json.Str(o, "beard"),
                skinColor = Json.Str(o, "skinColor"), hairColor = Json.Str(o, "hairColor"),
                items = Json.Objects(o, "items").Select(i => new ItemRecord
                {
                    slot = Json.Str(i, "slot"), prefab = Json.Str(i, "prefab"), name = Json.Str(i, "name"), token = Json.Str(i, "token"),
                    icon = Json.Str(i, "icon"), quality = Json.Int(i, "quality"), variant = Json.Int(i, "variant")
                }).ToList(),
                skills = Json.Objects(o, "skills").Select(k => new SkillRecord { id = Json.Str(k, "id"), name = Json.Str(k, "name"), level = (float)Json.Num(k, "level") }).ToList(),
                stats = Json.Objects(o, "stats").Select(k => new StatRecord { id = Json.Str(k, "id"), value = (float)Json.Num(k, "value") }).ToList()
            };
        }
    }

    /// <summary>Immutable view for the web server thread.</summary>
    internal sealed class Snapshot
    {
        public string Directory = "";
        public Dictionary<string, byte[]> Json = new Dictionary<string, byte[]>();
        public HashSet<string> Portraits = new HashSet<string>();
        public HashSet<string> Icons = new HashSet<string>();
    }

    /// <summary>
    /// Player records, sessions and stored images. Main thread only; the web server reads the
    /// latest <see cref="Current"/> snapshot.
    /// </summary>
    internal static class PlayerStore
    {
        private const int RecentSessions = 30;
        private const float RefreshSeconds = 30f;
        private const float HeartbeatSeconds = 60f;

        private sealed class Online
        {
            public string Id;
            public long Start;
        }

        private static readonly Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
        private static readonly Dictionary<long, Online> OnlineByPeer = new Dictionary<long, Online>();
        private static readonly HashSet<string> IconKeys = new HashSet<string>();
        private static string _dir;
        private static bool _dirty;
        private static float _nextRefresh;
        private static float _nextHeartbeat;

        public static volatile Snapshot Current;

        public static bool Active => _dir != null;

        public static IEnumerable<string> KnownIcons => IconKeys;

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---- Lifecycle -----------------------------------------------------

        public static void Start()
        {
            _dir = ResolveDirectory();
            Directory.CreateDirectory(Path.Combine(_dir, "players"));
            Directory.CreateDirectory(Path.Combine(_dir, "portraits"));
            Directory.CreateDirectory(Path.Combine(_dir, "icons"));
            Players.Clear();
            OnlineByPeer.Clear();
            IconKeys.Clear();

            foreach (string file in Directory.GetFiles(Path.Combine(_dir, "players"), "*.json"))
            {
                try
                {
                    var rec = PlayerRecord.FromJson(Json.Parse(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>);
                    if (rec == null || string.IsNullOrEmpty(rec.id) || rec.id != Path.GetFileNameWithoutExtension(file)) continue;
                    if (rec.openSince > 0)
                    {
                        // The server stopped without closing this session (crash, kill): end it
                        // at the last moment the player was seen.
                        CloseSession(rec, Math.Max(rec.openSince, rec.lastSeen));
                        Save(rec);
                    }
                    Players[rec.id] = rec;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Cannot read {Path.GetFileName(file)}: {e.Message}");
                }
            }
            foreach (string file in Directory.GetFiles(Path.Combine(_dir, "icons"), "*.png"))
                IconKeys.Add(Path.GetFileNameWithoutExtension(file));

            _dirty = true;
            Rebuild();
            Plugin.Log.LogInfo($"Player cards in {_dir}: {Players.Count} players, {IconKeys.Count} item icons.");
        }

        public static void Stop()
        {
            if (_dir == null) return;
            long now = Now;
            foreach (var online in OnlineByPeer.Values)
                if (Players.TryGetValue(online.Id, out var rec))
                {
                    CloseSession(rec, now);
                    Save(rec);
                }
            OnlineByPeer.Clear();
            _dir = null;
            Current = null;
        }

        public static void Tick()
        {
            if (_dir == null) return;
            float t = Time.realtimeSinceStartup;
            if (OnlineByPeer.Count > 0 && t >= _nextHeartbeat)
            {
                _nextHeartbeat = t + HeartbeatSeconds;
                long now = Now;
                foreach (var online in OnlineByPeer.Values)
                    if (Players.TryGetValue(online.Id, out var rec))
                    {
                        rec.lastSeen = now;
                        Save(rec);
                    }
            }
            if (_dirty || t >= _nextRefresh) Rebuild();
        }

        private static string ResolveDirectory()
        {
            string dir = Plugin.DataDirectory.Value;
            if (string.IsNullOrWhiteSpace(dir))
            {
                string world = ZNet.World != null ? ZNet.World.m_name : "world";
                return Path.Combine(Paths.ConfigPath, "ValheimPlayerCards", SafeName(world));
            }
            return Path.IsPathRooted(dir) ? dir : Path.Combine(Paths.GameRootPath, dir);
        }

        // ---- Sessions ------------------------------------------------------

        public static void OnPlayerJoined(ZNetPeer peer)
        {
            if (_dir == null || peer == null || string.IsNullOrEmpty(peer.m_playerName)) return;
            if (OnlineByPeer.ContainsKey(peer.m_uid)) return;   // respawn after death
            long now = Now;
            var rec = GetOrCreate(peer.m_playerName);
            if (rec.firstSeen == 0) rec.firstSeen = now;
            if (rec.openSince > 0) CloseSession(rec, Math.Max(rec.openSince, rec.lastSeen));
            rec.openSince = now;
            rec.lastSeen = now;
            rec.sessions++;
            OnlineByPeer[peer.m_uid] = new Online { Id = rec.id, Start = now };
            Save(rec);
            _dirty = true;
        }

        public static void OnPeerLeft(ZNetPeer peer)
        {
            if (_dir == null || peer == null || !OnlineByPeer.TryGetValue(peer.m_uid, out var online)) return;
            OnlineByPeer.Remove(peer.m_uid);
            if (Players.TryGetValue(online.Id, out var rec))
            {
                CloseSession(rec, Now);
                Save(rec);
            }
            _dirty = true;
        }

        private static void CloseSession(PlayerRecord rec, long end)
        {
            if (rec.openSince <= 0) return;
            long start = rec.openSince;
            if (end > start)
            {
                rec.playSeconds += end - start;
                rec.recent.Add(new SessionRecord { start = start, end = end });
                if (rec.recent.Count > RecentSessions) rec.recent.RemoveRange(0, rec.recent.Count - RecentSessions);
            }
            rec.lastSeen = Math.Max(rec.lastSeen, end);
            rec.openSince = 0;
        }

        // ---- Cards ---------------------------------------------------------

        /// <summary>Stores what a player sent. The name comes from the connection, not from the payload.</summary>
        public static void SaveCard(string playerName, CardPayload p, int width, int height)
        {
            if (_dir == null) return;
            var rec = GetOrCreate(playerName);
            long now = Now;
            if (rec.firstSeen == 0) rec.firstSeen = now;
            rec.lastSeen = Math.Max(rec.lastSeen, now);

            WriteAtomic(Path.Combine(_dir, "portraits", rec.id + ".png"), p.Png);
            int iconsSaved = 0;
            // Only icons of items on this card, so a client cannot fill the folder with other images.
            var wanted = new HashSet<string>(p.Items.Select(i => IconKey(i.Prefab, i.Variant)));
            foreach (var icon in p.Icons)
            {
                string key = SafeName(icon.Key);
                if (IconKeys.Contains(key) || !wanted.Contains(key)) continue;
                if (!Protocol.TryReadPngSize(icon.Png, out int iw, out int ih) || iw > Protocol.MaxIconSide || ih > Protocol.MaxIconSide) continue;
                WriteAtomic(Path.Combine(_dir, "icons", key + ".png"), icon.Png);
                IconKeys.Add(key);
                iconsSaved++;
            }

            rec.cardUpdated = now;
            rec.portraitWidth = width;
            rec.portraitHeight = height;
            rec.model = p.Model;
            rec.hair = p.Hair;
            rec.beard = p.Beard;
            rec.skinColor = Hex(p.SkinColor);
            rec.hairColor = Hex(p.HairColor);
            rec.items = p.Items.Select(i => new ItemRecord
            {
                slot = i.Slot, prefab = i.Prefab, name = i.Name, token = i.Token,
                icon = IconKey(i.Prefab, i.Variant), quality = i.Quality, variant = i.Variant
            }).ToList();
            rec.skills = p.Skills.Select(s => new SkillRecord { id = s.Id, name = s.Name, level = s.Level }).ToList();
            rec.stats = p.Stats.Select(s => new StatRecord { id = s.Id, value = s.Value }).ToList();
            Save(rec);
            _dirty = true;
            Plugin.Log.LogInfo($"Saved card of {playerName}: portrait {p.Png.Length / 1024} KB {width}x{height}, {p.Items.Count} items, {p.Skills.Count} skills, {p.Stats.Count} stats, {iconsSaved} new icons.");
        }

        public static string IconKey(string prefab, int variant)
        {
            return SafeName(variant != 0 ? $"{prefab}-{variant}" : prefab);
        }

        private static PlayerRecord GetOrCreate(string name)
        {
            string id = SafeName(name);
            if (!Players.TryGetValue(id, out var rec))
            {
                rec = new PlayerRecord { id = id };
                Players[id] = rec;
            }
            rec.name = name;
            return rec;
        }

        private static void Save(PlayerRecord rec)
        {
            try
            {
                WriteAtomic(Path.Combine(_dir, "players", rec.id + ".json"), Encoding.UTF8.GetBytes(Json.Write(rec.ToJson(), true)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cannot save {rec.id}.json: {e.Message}");
            }
        }

        // ---- Snapshot for the web server -----------------------------------

        private static void Rebuild()
        {
            _dirty = false;
            _nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
            try
            {
                var snap = new Snapshot { Directory = _dir };
                var hidden = new HashSet<string>(
                    Plugin.HiddenPlayers.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                long now = Now;
                var features = new Features
                {
                    PlayTime = Plugin.ShowPlayTime.Value,
                    Equipment = Plugin.ShowEquipment.Value,
                    ItemIcons = Plugin.ShowEquipment.Value && Plugin.ShowItemIcons.Value,
                    Skills = Plugin.ShowSkills.Value,
                    Stats = Plugin.ShowStats.Value
                };
                var statList = Plugin.StatList.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

                var onlineSince = new Dictionary<string, long>();
                foreach (var o in OnlineByPeer.Values) onlineSince[o.Id] = o.Start;

                var players = new List<object>();
                int online = 0;
                foreach (var rec in Players.Values.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase))
                {
                    if (hidden.Contains(rec.name)) continue;
                    bool hasPortrait = rec.cardUpdated > 0 && File.Exists(Path.Combine(_dir, "portraits", rec.id + ".png"));
                    if (hasPortrait) snap.Portraits.Add(rec.id);
                    bool isOnline = features.PlayTime && onlineSince.ContainsKey(rec.id);
                    if (isOnline) online++;
                    var api = ToApi(rec, features, statList, onlineSince, now, hasPortrait);
                    players.Add(api);
                    snap.Json["api/players/" + rec.id + ".json"] = Encoding.UTF8.GetBytes(Json.Write(api));
                }

                var server = new Dictionary<string, object>
                {
                    ["title"] = string.IsNullOrWhiteSpace(Plugin.PageTitle.Value) ? ServerName() : Plugin.PageTitle.Value.Trim(),
                    ["world"] = ZNet.World != null ? ZNet.World.m_name : "",
                    ["version"] = Plugin.Version,
                    ["language"] = Plugin.PageLanguage.Value,
                    ["now"] = now,
                    ["online"] = online,
                    ["features"] = new Dictionary<string, object>
                    {
                        ["playTime"] = features.PlayTime, ["equipment"] = features.Equipment, ["itemIcons"] = features.ItemIcons,
                        ["skills"] = features.Skills, ["stats"] = features.Stats
                    },
                    ["statList"] = statList
                };
                snap.Json["api/players.json"] = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                {
                    ["server"] = server,
                    ["players"] = players
                }));
                if (features.ItemIcons)
                    foreach (string k in IconKeys) snap.Icons.Add(k);
                Current = snap;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Cannot build the API data: {e}");
            }
        }

        private sealed class Features
        {
            public bool PlayTime, Equipment, ItemIcons, Skills, Stats;
        }

        /// <summary>One player as the API shows it. Parts turned off in the config are empty.</summary>
        private static Dictionary<string, object> ToApi(PlayerRecord rec, Features f, List<string> statList, Dictionary<string, long> onlineSince, long now, bool hasPortrait)
        {
            bool online = f.PlayTime && onlineSince.TryGetValue(rec.id, out long since) && since > 0;
            long start = online ? onlineSince[rec.id] : 0;

            var items = new List<object>();
            if (f.Equipment)
                foreach (var i in rec.items)
                    items.Add(new Dictionary<string, object>
                    {
                        ["slot"] = i.slot, ["prefab"] = i.prefab, ["name"] = i.name,
                        ["icon"] = f.ItemIcons && IconKeys.Contains(i.icon) ? $"icons/{i.icon}.png" : "",
                        ["quality"] = i.quality, ["variant"] = i.variant
                    });

            var skills = new List<object>();
            if (f.Skills)
                foreach (var k in rec.skills.OrderByDescending(k => k.level))
                    skills.Add(new Dictionary<string, object> { ["id"] = k.id, ["name"] = k.name, ["level"] = k.level });

            var stats = new List<object>();
            if (f.Stats)
            {
                var byId = new Dictionary<string, float>();
                foreach (var k in rec.stats) byId[k.id] = k.value;
                foreach (string id in statList)
                    if (byId.TryGetValue(id, out float value))
                        stats.Add(new Dictionary<string, object> { ["id"] = id, ["value"] = value });
            }

            var recent = new List<object>();
            if (f.PlayTime)
                foreach (var r in rec.recent)
                    recent.Add(new Dictionary<string, object> { ["start"] = r.start, ["end"] = r.end });

            return new Dictionary<string, object>
            {
                ["id"] = rec.id,
                ["name"] = rec.name,
                ["portrait"] = hasPortrait ? $"portraits/{rec.id}.png?v={rec.cardUpdated}" : "",
                ["portraitWidth"] = hasPortrait ? rec.portraitWidth : 0,
                ["portraitHeight"] = hasPortrait ? rec.portraitHeight : 0,
                ["cardUpdated"] = rec.cardUpdated,
                ["online"] = online,
                ["onlineSince"] = start,
                ["firstSeen"] = f.PlayTime ? rec.firstSeen : 0,
                ["lastSeen"] = f.PlayTime ? (online ? now : rec.lastSeen) : 0,
                ["playSeconds"] = f.PlayTime ? rec.playSeconds + (online ? Math.Max(0, now - start) : 0) : 0,
                ["sessions"] = f.PlayTime ? rec.sessions : 0,
                ["recent"] = recent,
                ["model"] = rec.model,
                ["skinColor"] = rec.skinColor,
                ["hairColor"] = rec.hairColor,
                ["items"] = items,
                ["skills"] = skills,
                ["stats"] = stats
            };
        }

        private static string ServerName()
        {
            try
            {
                var field = HarmonyLib.AccessTools.Field(typeof(ZNet), "m_ServerName");
                if (field?.GetValue(null) is string name && name.Length > 0) return name;
            }
            catch (Exception)
            {
                // Fall back to the world name.
            }
            return ZNet.World != null ? ZNet.World.m_name : "Valheim";
        }

        // ---- Helpers -------------------------------------------------------

        /// <summary>Lowercase letters, digits, '-' and '_'. Accents are kept as their base letter.</summary>
        internal static string SafeName(string name)
        {
            string decomposed = (name ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                char l = char.ToLowerInvariant(c);
                if ((l >= 'a' && l <= 'z') || (l >= '0' && l <= '9') || l == '-' || l == '_') sb.Append(l);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= 64) break;
            }
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "player" : s;
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static string Hex(float[] c)
        {
            int B(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
            return $"#{B(c[0]):x2}{B(c[1]):x2}{B(c[2]):x2}";
        }
    }
}
