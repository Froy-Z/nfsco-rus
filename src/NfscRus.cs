// NFS Carbon / NFSCO Russian localization builder.
// Compiled at install time by install.ps1 (Add-Type, C# 5 / .NET Framework 4.x) - no extra software needed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NfscRus
{
    public class Chunk
    {
        public uint Id;
        public byte[] Data; // whole chunk including the 8-byte header

        public byte[] Body()
        {
            byte[] b = new byte[Data.Length - 8];
            Buffer.BlockCopy(Data, 8, b, 0, b.Length);
            return b;
        }
    }

    public static class Bin
    {
        public static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }
        public static void Put32(byte[] d, int p, uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, p, 4); }
        public static void Put16(byte[] d, int p, ushort v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, p, 2); }

        public static List<Chunk> Top(byte[] d)
        {
            var r = new List<Chunk>();
            int p = 0;
            while (p + 8 <= d.Length)
            {
                uint id = U32(d, p);
                int size = (int)U32(d, p + 4);
                if (p + 8 + size > d.Length) throw new InvalidDataException("Повреждённый блок в файле");
                byte[] c = new byte[8 + size];
                Buffer.BlockCopy(d, p, c, 0, c.Length);
                r.Add(new Chunk { Id = id, Data = c });
                p += 8 + size;
            }
            if (p != d.Length) throw new InvalidDataException("Неожиданный конец файла");
            return r;
        }

        public static Chunk Find(List<Chunk> l, uint id)
        {
            foreach (var c in l) if (c.Id == id) return c;
            throw new InvalidDataException(string.Format("Блок 0x{0:X8} не найден", id));
        }

        public static byte[] Make(uint id, byte[] body)
        {
            byte[] r = new byte[8 + body.Length];
            Put32(r, 0, id);
            Put32(r, 4, (uint)body.Length);
            Buffer.BlockCopy(body, 0, r, 8, body.Length);
            return r;
        }

        // zero chunk (id 0) so that the next chunk starts aligned; minimum 8 bytes
        public static byte[] Pad(int curLen, int align)
        {
            int need = ((-(curLen + 8)) % align + align) % align;
            return Make(0, new byte[need]);
        }

        public static byte[] Concat(List<byte[]> parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        public static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }

    // Language string chunk (0x39000): header, (hash, offset) table, zero-terminated strings
    public class StringTable
    {
        public byte[] Name;
        public List<uint> Order = new List<uint>();
        public Dictionary<uint, byte[]> Map = new Dictionary<uint, byte[]>();

        public static StringTable FromFile(string path)
        {
            return Parse(Bin.Find(Bin.Top(File.ReadAllBytes(path)), 0x39000).Data);
        }

        public static StringTable Parse(byte[] c)
        {
            var t = new StringTable();
            int n = (int)Bin.U32(c, 8), ht = (int)Bin.U32(c, 12), st = (int)Bin.U32(c, 16);
            t.Name = new byte[8 + ht - 20];
            Buffer.BlockCopy(c, 20, t.Name, 0, t.Name.Length);
            for (int i = 0; i < n; i++)
            {
                uint h = Bin.U32(c, 8 + ht + i * 8);
                int p = 8 + st + (int)Bin.U32(c, 8 + ht + i * 8 + 4);
                int e = Array.IndexOf(c, (byte)0, p);
                byte[] s = new byte[e - p];
                Buffer.BlockCopy(c, p, s, 0, s.Length);
                t.Order.Add(h);
                t.Map[h] = s;
            }
            return t;
        }

        public static byte[] Build(byte[] name, Dictionary<uint, byte[]> ent)
        {
            var hs = new List<uint>(ent.Keys);
            hs.Sort(); // the game binary-searches by hash
            int ht = 12 + name.Length, st = ht + 8 * hs.Count;
            var table = new MemoryStream();
            var strings = new MemoryStream();
            foreach (uint h in hs)
            {
                table.Write(BitConverter.GetBytes(h), 0, 4);
                table.Write(BitConverter.GetBytes((uint)strings.Length), 0, 4);
                strings.Write(ent[h], 0, ent[h].Length);
                strings.WriteByte(0);
            }
            var body = new MemoryStream();
            body.Write(BitConverter.GetBytes((uint)hs.Count), 0, 4);
            body.Write(BitConverter.GetBytes((uint)ht), 0, 4);
            body.Write(BitConverter.GetBytes((uint)st), 0, 4);
            body.Write(name, 0, name.Length);
            table.WriteTo(body);
            strings.WriteTo(body);
            while (body.Length % 4 != 0) body.WriteByte(0);
            return Bin.Make(0x39000, body.ToArray());
        }
    }

    // Byte -> Unicode table (chunk 0x39001). String byte X >= 0x80 renders as unicode table[X + 2].
    public class Charset
    {
        Dictionary<int, char> dec = new Dictionary<int, char>();
        Dictionary<char, byte> enc = new Dictionary<char, byte>();
        public int Marker; // first table entry: 256 in original English files, 297 in Russian ones

        public Charset(string langFile)
        {
            byte[] b = Bin.Find(Bin.Top(File.ReadAllBytes(langFile)), 0x39001).Body();
            Marker = BitConverter.ToUInt16(b, 0);
            for (int x = 0x80; x < 0x100; x++)
            {
                ushort u = BitConverter.ToUInt16(b, (x + 2) * 2);
                if (u != 0) { dec[x] = (char)u; enc[(char)u] = (byte)x; }
            }
        }

        public string Decode(byte[] s)
        {
            var sb = new StringBuilder();
            foreach (byte c in s)
            {
                char ch;
                sb.Append(c >= 0x80 ? (dec.TryGetValue(c, out ch) ? ch : '�') : (char)c);
            }
            return sb.ToString();
        }

        public byte[] Encode(string text)
        {
            text = text.Replace('ё', 'е').Replace('Ё', 'Е').Replace('Ъ', 'ъ')
                       .Replace('«', '"').Replace('»', '"').Replace('—', '-').Replace('–', '-');
            var r = new List<byte>();
            foreach (char ch in text)
            {
                byte b;
                if (ch < 0x80) r.Add((byte)ch);
                else if (enc.TryGetValue(ch, out b)) r.Add(b);
                else throw new ArgumentException("Символ не поддерживается шрифтом: " + ch);
            }
            return r.ToArray();
        }
    }

    public static class LangBuilder
    {
        static readonly string[][] PartNames = {
            new[] { "FRONTEND", "Frontend" }, new[] { "INGAME", "InGame" }, new[] { "GLOBAL", "Global" } };

        static readonly Dictionary<string, string> Colors = new Dictionary<string, string> {
            { "ORANGE", "ОРАНЖЕВЫЙ" }, { "PURPLE", "ФИОЛЕТОВЫЙ" }, { "SILVER", "СЕРЕБРИСТЫЙ" }, { "YELLOW", "ЖЕЛТЫЙ" },
            { "DARK BLUE", "ТЕМНО-СИНИЙ" }, { "LIGHT BLUE", "ГОЛУБОЙ" }, { "BLACK", "ЧЕРНЫЙ" }, { "GREEN", "ЗЕЛЕНЫЙ" },
            { "CYAN", "БИРЮЗОВЫЙ" }, { "LIME", "САЛАТОВЫЙ" }, { "PINK", "РОЗОВЫЙ" }, { "RED", "КРАСНЫЙ" }, { "WHITE", "БЕЛЫЙ" } };
        static readonly Dictionary<string, string> PartsRu = new Dictionary<string, string> {
            { "ENGINE", "ДВИГАТЕЛЬ" }, { "SUSPENSION", "ПОДВЕСКА" }, { "TURBO & SUPERCHARGER", "ТУРБО И НАГНЕТАТЕЛЬ" },
            { "TIRES & BRAKES", "ШИНЫ И ТОРМОЗА" }, { "NITROUS", "ЗАКИСЬ АЗОТА" }, { "TRANSMISSION", "ТРАНСМИССИЯ" },
            { "RIMS", "ДИСКИ" }, { "WINDOW TINTS", "ТОНИРОВКА" }, { "EXHAUSTS", "ВЫХЛОП" }, { "ROOF SCOOPS", "ВОЗДУХОЗАБОРНИКИ" },
            { "WINDOW DECALS", "НАКЛЕЙКИ НА СТЕКЛА" }, { "BODY KITS", "ОБВЕСЫ" }, { "HOODS", "КАПОТЫ" }, { "SPOILERS", "СПОЙЛЕРЫ" } };

        class Pattern
        {
            public Regex Re;
            public MatchEvaluator Rep;
            public Pattern(string re, MatchEvaluator rep) { Re = new Regex("^(?:" + re + ")$"); Rep = rep; }
        }

        static List<Pattern> Patterns()
        {
            var colorRe = Alt(Colors.Keys);
            var partsRe = Alt(PartsRu.Keys);
            var l = new List<Pattern>();
            string[][] simple = {
                new[] { @"STRIPE BODY (\d+)", "ПОЛОСА (КУЗОВ) {0}" }, new[] { @"STRIPE (\d+)", "ПОЛОСА {0}" },
                new[] { @"PINSTRIPE (\d+)", "ШТРИХ {0}" },
                new[] { @"TRIBAL BODY (\d+)", "ТОТЕМ (КУЗОВ) {0}" }, new[] { @"TRIBAL (\d+)", "ТОТЕМ {0}" },
                new[] { @"FLAME BODY (\d+)", "ПЛАМЯ (КУЗОВ) {0}" }, new[] { @"FLAME (\d+)", "ПЛАМЯ {0}" },
                new[] { @"BASIC BODY (\d+)", "ОСНОВНОЙ (КУЗОВ) {0}" }, new[] { @"PRIMITIVE (\d+)", "ФИГУРЫ {0}" },
                new[] { @"STROKE (\d+)", "КОНТУР {0}" }, new[] { @"DROP SHADOW (\d+)", "ТЕНЬ {0}" }, new[] { @"FILL (\d+)", "ЗАЛИВКА {0}" },
                new[] { @"CAMOUFLAGE (\d+)", "КАМУФЛЯЖ {0}" }, new[] { @"RACE FLAG (\d+)", "ГОНОЧНЫЙ ФЛАГ {0}" },
                new[] { @"PET (\d+)", "ЗВЕРЬ {0}" }, new[] { @"CUSTOM (\d+)", "СВОЙ {0}" }, new[] { @"KIT (\d+)", "КОМПЛЕКТ {0}" },
                new[] { @"PEARLESCENT (\d+)", "ПЕРЛАМУТР {0}" } };
            foreach (var s in simple)
            {
                string fmt = s[1];
                l.Add(new Pattern(s[0], m => string.Format(fmt, m.Groups[1].Value)));
            }
            l.Add(new Pattern("PEARLESCENT (" + colorRe + ")", m => "ПЕРЛАМУТР " + Colors[m.Groups[1].Value]));
            l.Add(new Pattern(@"STAGE (\d) (" + colorRe + ")", m => "КЛАСС " + m.Groups[1].Value + " " + Colors[m.Groups[2].Value]));
            l.Add(new Pattern("(" + partsRe + @") - TIER (\d) STAGE (\d)",
                m => PartsRu[m.Groups[1].Value] + " - УРОВЕНЬ " + m.Groups[2].Value + " КЛАСС " + m.Groups[3].Value));
            l.Add(new Pattern("(" + partsRe + @") - PACKAGE (\d)", m => PartsRu[m.Groups[1].Value] + " - ПАКЕТ " + m.Groups[2].Value));
            return l;
        }

        static string Alt(IEnumerable<string> keys)
        {
            var k = new List<string>(keys);
            k.Sort((a, b) => b.Length.CompareTo(a.Length));
            return string.Join("|", k.ConvertAll(Regex.Escape).ToArray());
        }

        // ignore case / punctuation / whitespace-only edits made by NFSCO
        static string Norm(byte[] s)
        {
            var sb = new StringBuilder();
            foreach (byte b in s)
            {
                char c = (char)(b >= 'A' && b <= 'Z' ? b + 32 : b);
                if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) == UnicodeCategory.OtherNumber) sb.Append(c);
            }
            return sb.ToString();
        }

        static bool IsUpper(string s)
        {
            bool cased = false;
            foreach (char c in s)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) cased = true;
            }
            return cased;
        }

        static Dictionary<uint, string> LoadManual(string path)
        {
            var r = new Dictionary<uint, string>();
            if (!File.Exists(path)) return r;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var d = js.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
            foreach (var kv in d) r[uint.Parse(kv.Key, NumberStyles.HexNumber)] = kv.Value;
            return r;
        }

        static string P(string dir, string file) { return Path.Combine(dir, file); }

        public static string Build(string nfscoDir, string vanillaDir, string rusDir, string trDir, string outDir)
        {
            var log = new StringBuilder();
            var rusCs = new Charset(P(rusDir, "Russian_Global.bin"));
            var enCs = new Charset(P(nfscoDir, "ENGLISH_GLOBAL.BIN"));
            if (enCs.Marker != 256)
                throw new InvalidDataException("Исходные языковые файлы NFSCO уже изменены (не оригинал)");

            // dictionary: vanilla English text -> Russian text
            var dic = new Dictionary<string, string>();
            foreach (var pn in PartNames)
            {
                var v = StringTable.FromFile(P(vanillaDir, "ENGLISH_" + pn[0] + ".BIN"));
                var r = StringTable.FromFile(P(rusDir, "Russian_" + pn[1] + ".bin"));
                foreach (uint h in v.Order)
                    if (r.Map.ContainsKey(h))
                    {
                        string k = enCs.Decode(v.Map[h]).Trim().ToLowerInvariant();
                        if (!dic.ContainsKey(k)) dic[k] = rusCs.Decode(r.Map[h]).Trim();
                    }
            }
            // manual translations also feed the dictionary, so e.g. UPPERCASE duplicates get translated
            var manuals = new Dictionary<string, Dictionary<uint, string>>();
            foreach (var pn in PartNames)
            {
                manuals[pn[0]] = LoadManual(P(trDir, "tr_" + pn[0].ToLowerInvariant() + ".json"));
                var e = StringTable.FromFile(P(nfscoDir, "ENGLISH_" + pn[0] + ".BIN"));
                foreach (var kv in manuals[pn[0]])
                    if (e.Map.ContainsKey(kv.Key))
                    {
                        string k = enCs.Decode(e.Map[kv.Key]).Trim().ToLowerInvariant();
                        if (!dic.ContainsKey(k)) dic[k] = kv.Value;
                    }
            }

            var patterns = Patterns();
            var hasLatin = new Regex("[A-Za-z]");
            Directory.CreateDirectory(outDir);
            foreach (var pn in PartNames)
            {
                var manual = manuals[pn[0]];
                var e = StringTable.FromFile(P(nfscoDir, "ENGLISH_" + pn[0] + ".BIN"));
                var v = StringTable.FromFile(P(vanillaDir, "ENGLISH_" + pn[0] + ".BIN"));
                string rusFile = P(rusDir, "Russian_" + pn[1] + ".bin");
                var r = StringTable.FromFile(rusFile);
                var outMap = new Dictionary<uint, byte[]>();
                int nEnpy = 0, nManual = 0, nDict = 0, nPattern = 0, nEnglish = 0;
                foreach (uint h in e.Order)
                {
                    byte[] s = e.Map[h];
                    string text = enCs.Decode(s);
                    if (manual.ContainsKey(h)) // manual wins: also covers strings the base localization left in English
                    { outMap[h] = rusCs.Encode(manual[h]); nManual++; continue; }
                    if (v.Map.ContainsKey(h) && Norm(v.Map[h]) == Norm(s) && r.Map.ContainsKey(h))
                    { outMap[h] = r.Map[h]; nEnpy++; continue; }
                    string key = text.Trim().ToLowerInvariant();
                    if (dic.ContainsKey(key) && hasLatin.IsMatch(text))
                    {
                        string ru = dic[key];
                        outMap[h] = rusCs.Encode(IsUpper(text.Trim()) ? ru.ToUpper(CultureInfo.GetCultureInfo("ru-RU")) : ru);
                        nDict++; continue;
                    }
                    bool done = false;
                    foreach (var pt in patterns)
                        if (pt.Re.IsMatch(text)) { outMap[h] = rusCs.Encode(pt.Re.Replace(text, pt.Rep)); nPattern++; done = true; break; }
                    if (done) continue;
                    try { outMap[h] = rusCs.Encode(text); }
                    catch (ArgumentException) { outMap[h] = Encoding.ASCII.GetBytes(text); }
                    nEnglish++;
                }
                var parts = new List<byte[]> { StringTable.Build(e.Name, outMap) };
                if (pn[0] == "GLOBAL")
                {
                    // Russian charset table (0x39001) and Cyrillic font tables (0x30201)
                    int len = parts[0].Length;
                    foreach (var c in Bin.Top(File.ReadAllBytes(rusFile)))
                        if (c.Id == 0x39001 || c.Id == 0x30201)
                        {
                            byte[] pad = Bin.Pad(len, 16);
                            parts.Add(pad); parts.Add(c.Data);
                            len += pad.Length + c.Data.Length;
                        }
                }
                byte[] buf = Bin.Concat(parts);
                File.WriteAllBytes(P(outDir, "ENGLISH_" + pn[0] + ".BIN"), buf);
                File.WriteAllBytes(P(outDir, "GERMAN_" + pn[0] + ".BIN"), buf);
                log.AppendFormat("{0}: перевод {1}, ручной {2}, словарь {3}, шаблоны {4}, без перевода {5}\n",
                    pn[0], nEnpy, nManual, nDict, nPattern, nEnglish);
            }
            return log.ToString();
        }
    }

    // GLOBALA.BUN: NFSCO HD texture pack with the Russian HD fonts + Russian font tables
    public static class GlobalaBuilder
    {
        const uint TitleHash = 0x2bd9f6bd, BodyHash = 0x94af4029;

        public static bool IsPatched(string path)
        {
            byte[] recs = Recs(File.ReadAllBytes(path));
            int rp = Bin.IndexOf(recs, BitConverter.GetBytes(BodyHash));
            return rp >= 0 && recs[rp + 0x22] == 0x26 && BitConverter.ToUInt16(recs, rp + 0x1c) == 2048;
        }

        static byte[] Recs(byte[] d)
        {
            var tpk = Bin.Top(d)[0];
            var info = Bin.Find(Bin.Top(tpk.Body()), 0xb3310000);
            return Bin.Find(Bin.Top(info.Body()), 0x33310004).Data;
        }

        public static string Build(string nfscoPath, string rusPath, string ddsDir, string outPath)
        {
            var log = new StringBuilder();
            var fonts = new Dictionary<uint, string> { { TitleHash, "FONT_NFS_TITLE.dds" }, { BodyHash, "FONT_NFS_BODY.dds" } };
            var tpk = Bin.Top(File.ReadAllBytes(nfscoPath))[0];
            if (tpk.Id != 0xb3300000) throw new InvalidDataException("GLOBALA.BUN NFSCO: неожиданный формат");
            var subs = Bin.Top(tpk.Body());
            var isub = Bin.Top(Bin.Find(subs, 0xb3310000).Body());
            var dsub = Bin.Top(Bin.Find(subs, 0xb3320000).Body());
            byte[] recs = Bin.Find(isub, 0x33310004).Data;
            byte[] comp = Bin.Find(isub, 0x33310005).Data;
            byte[] hashes = Bin.Find(isub, 0x33310002).Data;
            byte[] raw = Bin.Find(dsub, 0x33320002).Body();
            const int TEX0 = 0x78; // textures start after 0x78 bytes of 0x11 padding inside the data chunk

            var order = new List<uint>();
            for (int i = 0; i < (hashes.Length - 8) / 8; i++) order.Add(Bin.U32(hashes, 8 + 8 * i));
            foreach (uint fh in fonts.Keys)
                if (!order.Contains(fh)) throw new InvalidDataException("В GLOBALA.BUN NFSCO не найдены текстуры шрифтов");

            var rps = new Dictionary<uint, int>();
            var imgs = new Dictionary<uint, byte[]>();
            for (int i = 0; i < order.Count; i++)
            {
                uint h = order[i];
                int rp = Bin.IndexOf(recs, BitConverter.GetBytes(h));
                int place = (int)Bin.U32(recs, rp + 8), size = (int)Bin.U32(recs, rp + 0x10);
                byte[] img;
                if (fonts.ContainsKey(h))
                {
                    byte[] d = File.ReadAllBytes(Path.Combine(ddsDir, fonts[h]));
                    int height = (int)Bin.U32(d, 12), width = (int)Bin.U32(d, 16), mips = (int)Bin.U32(d, 28);
                    if (Encoding.ASCII.GetString(d, 84, 4) != "DXT5") throw new InvalidDataException(fonts[h] + ": ожидался формат DXT5");
                    img = new byte[d.Length - 128];
                    Buffer.BlockCopy(d, 128, img, 0, img.Length);
                    Bin.Put32(recs, rp + 0x10, (uint)img.Length);
                    Bin.Put32(recs, rp + 0x18, (uint)(width * height)); // DXT5: 1 byte per pixel
                    Bin.Put16(recs, rp + 0x1c, (ushort)width);
                    Bin.Put16(recs, rp + 0x1e, (ushort)height);
                    recs[rp + 0x20] = (byte)Log2(width);
                    recs[rp + 0x21] = (byte)Log2(height);
                    recs[rp + 0x22] = 0x26;
                    recs[rp + 0x26] = (byte)mips;
                    Buffer.BlockCopy(Encoding.ASCII.GetBytes("DXT5"), 0, comp, 8 + 24 * i + 12, 4);
                    log.AppendFormat("{0}: {1}x{2} DXT5\n", fonts[h], width, height);
                }
                else
                {
                    img = new byte[size];
                    Buffer.BlockCopy(raw, TEX0 + place, img, 0, size);
                }
                rps[h] = rp;
                imgs[h] = img;
            }

            var payload = new MemoryStream();
            for (int i = 0; i < TEX0; i++) payload.WriteByte(0x11);
            foreach (uint h in order)
            {
                while ((payload.Length - TEX0) % 0x80 != 0) payload.WriteByte(0); // pad between textures only
                Bin.Put32(recs, rps[h] + 8, (uint)(payload.Length - TEX0));
                payload.Write(imgs[h], 0, imgs[h].Length);
            }

            var newIsub = new List<byte[]>();
            foreach (var c in isub) newIsub.Add(c.Data); // recs / comp were edited in place
            var newDsub = new List<byte[]>();
            foreach (var c in dsub) newDsub.Add(c.Id == 0x33320002 ? Bin.Make(c.Id, payload.ToArray()) : c.Data);
            var newSubs = new List<byte[]>();
            foreach (var c in subs)
                newSubs.Add(c.Id == 0xb3310000 ? Bin.Make(c.Id, Bin.Concat(newIsub)) :
                            c.Id == 0xb3320000 ? Bin.Make(c.Id, Bin.Concat(newDsub)) : c.Data);
            byte[] newTpk = Bin.Make(0xb3300000, Bin.Concat(newSubs));

            var rest = Bin.Top(File.ReadAllBytes(rusPath));
            if (rest[0].Id != 0xb3300000) throw new InvalidDataException("GLOBALA.BUN русификатора: неожиданный формат");
            if (newTpk.Length % 0x80 != rest[0].Data.Length % 0x80) throw new InvalidDataException("Ошибка выравнивания GLOBALA.BUN");
            var all = new List<byte[]> { newTpk };
            for (int i = 1; i < rest.Count; i++) all.Add(rest[i].Data);
            File.WriteAllBytes(outPath, Bin.Concat(all));
            return log.ToString();
        }

        static int Log2(int v) { int r = 0; while ((1 << (r + 1)) <= v) r++; return r; }
    }
}
