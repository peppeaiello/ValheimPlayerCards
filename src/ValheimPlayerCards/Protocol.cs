using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValheimPlayerCards
{
    internal sealed class ItemInfo
    {
        public string Slot = "";
        public string Prefab = "";
        public string Name = "";
        public string Token = "";
        public int Quality;
        public int Variant;
    }

    internal sealed class SkillInfo
    {
        public string Id = "";
        public string Name = "";
        public float Level;
    }

    internal sealed class StatInfo
    {
        public string Id = "";
        public float Value;
    }

    internal sealed class IconInfo
    {
        public string Key = "";
        public byte[] Png = Array.Empty<byte>();
    }

    internal sealed class CardPayload
    {
        public readonly List<ItemInfo> Items = new List<ItemInfo>();
        public readonly List<SkillInfo> Skills = new List<SkillInfo>();
        public readonly List<StatInfo> Stats = new List<StatInfo>();
        public readonly List<IconInfo> Icons = new List<IconInfo>();
        public int Model;
        public string Hair = "";
        public string Beard = "";
        public float[] SkinColor = { 1f, 1f, 1f };
        public float[] HairColor = { 1f, 1f, 1f };
        public byte[] Png = Array.Empty<byte>();
    }

    /// <summary>
    /// Wire format. Client -> server: Hello(version). Server -> client: Ready(version, max bytes,
    /// min interval, max side, icon keys the server already has). Client -> server: Upload chunks
    /// of one serialized CardPayload. Everything the server reads is bounded: a broken or hostile
    /// client cannot make it allocate more than MaxBytes per connection or store anything but
    /// validated PNGs and short strings.
    /// </summary>
    internal static class Protocol
    {
        public const int Version = 1;
        public const string RpcHello = "ValheimPlayerCards_Hello";
        public const string RpcReady = "ValheimPlayerCards_Ready";
        public const string RpcUpload = "ValheimPlayerCards_Upload";

        public const int ChunkSize = 60_000;
        public const int HardMaxBytes = 2 * 1024 * 1024;
        public const int MaxItems = 24;
        public const int MaxSkills = 96;
        public const int MaxStats = 512;
        public const int MaxIcons = 24;
        public const int MaxIconBytes = 48 * 1024;
        public const int MaxIconSide = 128;
        public const int MaxKnownIcons = 2000;
        public const int MaxStringLength = 128;
        public const int MaxImageSide = 2048;

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static int ChunkCount(int total) => (total + ChunkSize - 1) / ChunkSize;

        public static byte[] Serialize(CardPayload p)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(Version);

                    int items = Math.Min(p.Items.Count, MaxItems);
                    w.Write(items);
                    for (int i = 0; i < items; i++)
                    {
                        var it = p.Items[i];
                        w.Write(Clip(it.Slot));
                        w.Write(Clip(it.Prefab));
                        w.Write(Clip(it.Name));
                        w.Write(Clip(it.Token));
                        w.Write(it.Quality);
                        w.Write(it.Variant);
                    }

                    w.Write(p.Model);
                    w.Write(Clip(p.Hair));
                    w.Write(Clip(p.Beard));
                    for (int i = 0; i < 3; i++) w.Write(p.SkinColor[i]);
                    for (int i = 0; i < 3; i++) w.Write(p.HairColor[i]);

                    int skills = Math.Min(p.Skills.Count, MaxSkills);
                    w.Write(skills);
                    for (int i = 0; i < skills; i++)
                    {
                        w.Write(Clip(p.Skills[i].Id));
                        w.Write(Clip(p.Skills[i].Name));
                        w.Write(p.Skills[i].Level);
                    }

                    int stats = Math.Min(p.Stats.Count, MaxStats);
                    w.Write(stats);
                    for (int i = 0; i < stats; i++)
                    {
                        w.Write(Clip(p.Stats[i].Id));
                        w.Write(p.Stats[i].Value);
                    }

                    int icons = Math.Min(p.Icons.Count, MaxIcons);
                    w.Write(icons);
                    for (int i = 0; i < icons; i++)
                    {
                        w.Write(Clip(p.Icons[i].Key));
                        w.Write(p.Icons[i].Png.Length);
                        w.Write(p.Icons[i].Png);
                    }

                    w.Write(p.Png.Length);
                    w.Write(p.Png);
                }
                return ms.ToArray();
            }
        }

        public static CardPayload Deserialize(byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8))
            {
                if (r.ReadInt32() != Version) throw new InvalidDataException("unknown payload version");
                var p = new CardPayload();

                int items = ReadCount(r, MaxItems, "items");
                for (int i = 0; i < items; i++)
                {
                    p.Items.Add(new ItemInfo
                    {
                        Slot = ReadString(r),
                        Prefab = ReadString(r),
                        Name = ReadString(r),
                        Token = ReadString(r),
                        Quality = r.ReadInt32(),
                        Variant = r.ReadInt32()
                    });
                }

                p.Model = r.ReadInt32();
                p.Hair = ReadString(r);
                p.Beard = ReadString(r);
                for (int i = 0; i < 3; i++) p.SkinColor[i] = r.ReadSingle();
                for (int i = 0; i < 3; i++) p.HairColor[i] = r.ReadSingle();

                int skills = ReadCount(r, MaxSkills, "skills");
                for (int i = 0; i < skills; i++)
                    p.Skills.Add(new SkillInfo { Id = ReadString(r), Name = ReadString(r), Level = Finite(r.ReadSingle()) });

                int stats = ReadCount(r, MaxStats, "stats");
                for (int i = 0; i < stats; i++)
                    p.Stats.Add(new StatInfo { Id = ReadString(r), Value = Finite(r.ReadSingle()) });

                int icons = ReadCount(r, MaxIcons, "icons");
                for (int i = 0; i < icons; i++)
                {
                    string key = ReadString(r);
                    p.Icons.Add(new IconInfo { Key = key, Png = ReadBytes(r, MaxIconBytes) });
                }

                p.Png = ReadBytes(r, data.Length);
                return p;
            }
        }

        /// <summary>Reads width and height from the IHDR chunk. False if this is not a PNG.</summary>
        public static bool TryReadPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 33) return false;
            for (int i = 0; i < PngSignature.Length; i++)
                if (png[i] != PngSignature[i]) return false;
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return width > 0 && height > 0;
        }

        private static int ReadCount(BinaryReader r, int max, string what)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException("too many " + what);
            return n;
        }

        private static byte[] ReadBytes(BinaryReader r, int max)
        {
            int length = r.ReadInt32();
            if (length <= 0 || length > max) throw new InvalidDataException("bad image length");
            byte[] bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("truncated image");
            return bytes;
        }

        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        private static string ReadString(BinaryReader r)
        {
            string s = r.ReadString();
            if (s.Length > MaxStringLength) throw new InvalidDataException("string too long");
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsControl(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string Clip(string s)
        {
            s = s ?? "";
            return s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) : s;
        }
    }
}
