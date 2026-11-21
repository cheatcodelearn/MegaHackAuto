using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MegaHackAuto
{
    // One-click Mega Hack: resolves the latest standalone bundle from the
    // vendor manifest, downloads it, patches the license checks by content,
    // installs into the Geometry Dash folder, and launches the game.
    // Re-running it after an update re-downloads and re-patches automatically.
    internal static class Program
    {
        private const string ManifestUrl = "https://absolllute.com/api/mega_hack/v9/install.json";
        private const string FilesBase = "https://absolllute.com/api/mega_hack/v9/files";

        private static readonly byte[] Stub = { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };
        private static readonly byte[] IdPrologue = { 0x56, 0x57, 0x48, 0x83, 0xEC, 0x48, 0x48, 0x83, 0x79, 0x10, 0x40 };

        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string gameDir = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : FindGameDir();
            bool installOnly = Array.IndexOf(args, "-install-only") >= 0;

            if (gameDir == null || !File.Exists(Path.Combine(gameDir, "GeometryDash.exe")))
            {
                Console.WriteLine("Geometry Dash not found. Pass the folder as an argument.");
                return 1;
            }

            if (Process.GetProcessesByName("GeometryDash").Length > 0)
            {
                Console.WriteLine("Geometry Dash is running. Close it and run this again.");
                return 1;
            }

            try
            {
                Update(gameDir).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Update failed: " + ex.ToString());
                return 1;
            }

            if (installOnly) return 0;

            var start = Process.Start(new ProcessStartInfo(Path.Combine(gameDir, "GeometryDash.exe"))
            {
                WorkingDirectory = gameDir,
                UseShellExecute = true,
            });
            System.Threading.Thread.Sleep(2500);
            bool alive = false;
            try { alive = start != null && !start.HasExited; } catch { }
            if (!alive)
            {
                // Steam builds need the Steam launcher context; fall back to the URL.
                try { Process.Start("steam://rungameid/322170"); } catch { }
            }
            return 0;
        }

        private static string FindGameDir()
        {
            var candidates = new List<string>();
            foreach (string key in new[]
            {
                @"HKCU\Software\Valve\Steam",
                @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKLM\SOFTWARE\Valve\Steam",
            })
            {
                try
                {
                    string s = Registry.GetValue(key, "SteamPath", null) as string;
                    if (!string.IsNullOrEmpty(s))
                        candidates.Add(Path.Combine(s, @"steamapps\common\Geometry Dash"));
                }
                catch { }
            }
            candidates.AddRange(new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Geometry Dash",
                @"C:\Program Files\Steam\steamapps\common\Geometry Dash",
                @"D:\Steam\steamapps\common\Geometry Dash",
                @"D:\SteamLibrary\steamapps\common\Geometry Dash",
                @"E:\SteamLibrary\steamapps\common\Geometry Dash",
            });
            foreach (string c in candidates)
                if (File.Exists(Path.Combine(c, "GeometryDash.exe")))
                    return c;
            return null;
        }

        private static async Task Update(string gameDir)
        {
            string json;
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaHackAuto/1.0");
                json = await http.GetStringAsync(ManifestUrl);
            }

            var serializer = new JavaScriptSerializer();
            var manifest = serializer.Deserialize<Dictionary<string, object>>(json);
            var bundles = new List<Dictionary<string, object>>();
            foreach (var pkg in AsList(manifest["packages"]))
            {
                var p = (Dictionary<string, object>)pkg;
                if ((string)p["name"] != "Mega Hack v9") continue;
                foreach (var b in AsList(p["bundles"]))
                    bundles.Add((Dictionary<string, object>)b);
            }

            Dictionary<string, object> best = null;
            foreach (var b in bundles)
            {
                bool win = false;
                foreach (var plat in AsList(b["platform"]))
                    if ((string)plat == "win") win = true;
                if (!win || (bool)b["geode"]) continue;
                if (best == null || VersionKey((string)b["version"]) > VersionKey((string)best["version"]))
                    best = b;
            }
            if (best == null) throw new Exception("no standalone Windows bundle in manifest");

            string version = (string)best["version"];
            string file = (string)best["file"];
            string expectedHash = (string)best["hash"];
            string marker = Path.Combine(gameDir, "hackpro.dll.mhbuild");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == expectedHash)
            {
                Console.WriteLine("Mega Hack is up to date (" + version + ").");
                return;
            }

            string url = FilesBase + "/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(file);
            string temp = Path.Combine(Path.GetTempPath(), "mhauto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string zipPath = Path.Combine(temp, "bundle.zip");

            Console.WriteLine("Downloading Mega Hack " + version + "...");
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaHackAuto/1.0");
                using (var stream = await http.GetStreamAsync(url))
                using (var fs = File.Create(zipPath))
                    await stream.CopyToAsync(fs);
            }

            string actual = Sha256(zipPath);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new Exception("bundle hash mismatch: " + actual);

            string extractDir = Path.Combine(temp, "x");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            string dll = Path.Combine(extractDir, "hackpro.dll");
            if (!File.Exists(dll)) throw new Exception("hackpro.dll missing in bundle");
            PatchDll(dll);

            string backup = Path.Combine(gameDir, "hackpro.dll.orig");
            string installed = Path.Combine(gameDir, "hackpro.dll");
            if (File.Exists(installed) && !File.Exists(backup))
                File.Copy(installed, backup);

            CopyTree(extractDir, gameDir);
            EnsureLicense(gameDir);
            File.WriteAllText(marker, expectedHash);
            Directory.Delete(temp, true);
            Console.WriteLine("Installed Mega Hack " + version + ".");
        }

        private static IEnumerable<object> AsList(object value)
        {
            var list = value as System.Collections.IEnumerable;
            if (list == null) yield break;
            foreach (var item in list) yield return item;
        }

        private static int VersionKey(string version)
        {
            var numbers = Regex.Matches(version, @"\d+");
            int key = 0;
            for (int i = 0; i < 4 && i < numbers.Count; i++)
                key = key * 100 + int.Parse(numbers[i].Value);
            return key;
        }

        private static void CopyTree(string source, string target)
        {
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, dir.Substring(source.Length + 1)));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length + 1);
                File.Copy(file, Path.Combine(target, rel), true);
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        private static void EnsureLicense(string gameDir)
        {
            string dir = Path.Combine(gameDir, "absolllute.megahack");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "license");
            if (File.Exists(path)) return;

            string id = Hex(32);
            string inner = "{\"id\":\"" + id + "\",\"guid2\":\"" + Guid.NewGuid() + "\"}";
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));
            string sig = Convert.ToBase64String(new byte[64]);
            string token = Hex(16);
            File.WriteAllText(path, "{\"data\":\"" + b64 + "\",\"sig\":\"" + sig + "\",\"token\":\"" + token + "\"}");
        }

        private static string Hex(int bytes)
        {
            byte[] b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder(bytes * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ---- patching ----

        private sealed class Pe
        {
            public int SectionTable, NumberOfSections, OptionalHeader, SizeOfOptionalHeader;
            public ushort Magic;
            public uint ExceptionDirRva, ExceptionDirSize;
        }

        private static Pe ReadPe(byte[] data)
        {
            int e = BitConverter.ToInt32(data, 0x3C);
            var pe = new Pe();
            pe.NumberOfSections = BitConverter.ToUInt16(data, e + 6);
            pe.SizeOfOptionalHeader = BitConverter.ToUInt16(data, e + 20);
            pe.OptionalHeader = e + 24;
            pe.Magic = BitConverter.ToUInt16(data, pe.OptionalHeader);
            pe.SectionTable = pe.OptionalHeader + pe.SizeOfOptionalHeader;
            if (pe.Magic == 0x20B)
            {
                int dd = pe.OptionalHeader + 112 + 3 * 8;
                pe.ExceptionDirRva = BitConverter.ToUInt32(data, dd);
                pe.ExceptionDirSize = BitConverter.ToUInt32(data, dd + 4);
            }
            return pe;
        }

        private static bool RvaToOffset(Pe pe, byte[] data, uint rva, out int offset)
        {
            for (int i = 0; i < pe.NumberOfSections; i++)
            {
                int sh = pe.SectionTable + i * 40;
                uint virtSize = BitConverter.ToUInt32(data, sh + 8);
                uint virtAddr = BitConverter.ToUInt32(data, sh + 12);
                uint rawSize = BitConverter.ToUInt32(data, sh + 16);
                uint rawPtr = BitConverter.ToUInt32(data, sh + 20);
                uint span = Math.Max(virtSize, rawSize);
                if (rva >= virtAddr && rva < virtAddr + span)
                {
                    offset = (int)(rawPtr + (rva - virtAddr));
                    return offset >= 0 && offset < data.Length;
                }
            }
            offset = -1;
            return false;
        }

        private static uint OffsetToRva(Pe pe, byte[] data, int offset)
        {
            for (int i = 0; i < pe.NumberOfSections; i++)
            {
                int sh = pe.SectionTable + i * 40;
                uint rawSize = BitConverter.ToUInt32(data, sh + 16);
                uint rawPtr = BitConverter.ToUInt32(data, sh + 20);
                uint virtAddr = BitConverter.ToUInt32(data, sh + 12);
                if (offset >= rawPtr && offset < rawPtr + rawSize)
                    return virtAddr + (uint)(offset - rawPtr);
            }
            return 0;
        }

        private static int FindBytes(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;
            for (int i = Math.Max(0, start); i <= last; i++)
            {
                if (haystack[i] != needle[0]) continue;
                bool ok = true;
                for (int j = 1; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private static uint FunctionStart(Pe pe, byte[] data, uint rva)
        {
            if (pe.ExceptionDirRva == 0) return 0;
            int off;
            if (!RvaToOffset(pe, data, pe.ExceptionDirRva, out off)) return 0;
            int count = (int)(pe.ExceptionDirSize / 12);
            for (int i = 0; i < count; i++)
            {
                int e = off + i * 12;
                if (e + 12 > data.Length) break;
                uint begin = BitConverter.ToUInt32(data, e);
                uint end = BitConverter.ToUInt32(data, e + 4);
                if (rva >= begin && rva < end) return begin;
            }
            return 0;
        }

        private static uint FunctionByString(Pe pe, byte[] data, string anchor)
        {
            int msgOff = FindBytes(data, Encoding.ASCII.GetBytes(anchor), 0);
            if (msgOff < 0) return 0;
            uint msgRva = OffsetToRva(pe, data, msgOff);
            if (msgRva == 0) return 0;
            for (int i = 0; i < data.Length - 7; i++)
            {
                if ((data[i] != 0x48 && data[i] != 0x4C) || data[i + 1] != 0x8D) continue;
                if ((data[i + 2] & 0xC7) != 0x05) continue;
                int disp = BitConverter.ToInt32(data, i + 3);
                uint insnRva = OffsetToRva(pe, data, i);
                if (insnRva == 0) continue;
                if (insnRva + 7 + disp == msgRva) return FunctionStart(pe, data, insnRva);
            }
            return 0;
        }

        private static void Nop(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++) data[offset + i] = 0x90;
        }

        private static void PatchDll(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var pe = ReadPe(data);
            bool patched = false;

            // 1. signature-failure branch inside the validator
            uint validator = FunctionByString(pe, data, "signature invalid");
            if (validator != 0)
            {
                int off;
                if (RvaToOffset(pe, data, validator, out off))
                {
                    int msgOff = FindBytes(data, Encoding.ASCII.GetBytes("signature invalid"), 0);
                    uint msgRva = OffsetToRva(pe, data, msgOff);
                    int scan = off;
                    while (true)
                    {
                        int br = FindBytes(data, new byte[] { 0x84, 0xC0, 0x0F, 0x84 }, scan);
                        if (br < 0 || br >= off + 0x1200) break;
                        uint brRva = OffsetToRva(pe, data, br + 2);
                        if (brRva != 0)
                        {
                            int rel = BitConverter.ToInt32(data, br + 4);
                            uint target = brRva + 6 + (uint)rel;
                            if (BranchReachesString(pe, data, target, msgRva))
                            {
                                Nop(data, br, 8);
                                patched = true;
                                break;
                            }
                        }
                        scan = br + 1;
                    }
                }
            }

            // 2. id/HWID compare
            int idOff = FindBytes(data, IdPrologue, 0);
            if (idOff >= 0)
            {
                Array.Copy(Stub, 0, data, idOff, Stub.Length);
                patched = true;
            }

            // 3. login gate after license loader
            uint loader = FunctionByString(pe, data, "data empty");
            if (loader != 0)
            {
                for (int i = 0; i < data.Length - 5; i++)
                {
                    if (data[i] != 0xE8) continue;
                    uint insnRva = OffsetToRva(pe, data, i);
                    if (insnRva == 0) continue;
                    int rel = BitConverter.ToInt32(data, i + 1);
                    if (insnRva + 5 + rel != loader) continue;
                    int scan = FindBytes(data, new byte[] { 0x84, 0xC0, 0x0F, 0x84 }, i + 5);
                    if (scan >= 0 && scan < i + 5 + 24)
                    {
                        Nop(data, scan, 8);
                        patched = true;
                    }
                }
            }

            if (!patched) throw new Exception("license patterns not found in hackpro.dll");
            File.WriteAllBytes(path, data);
        }

        private static bool BranchReachesString(Pe pe, byte[] data, uint targetRva, uint msgRva)
        {
            int start;
            if (!RvaToOffset(pe, data, targetRva, out start)) return false;
            int limit = Math.Min(data.Length - 7, start + 0x80);
            for (int i = start; i < limit; i++)
            {
                if ((data[i] != 0x48 && data[i] != 0x4C) || data[i + 1] != 0x8D) continue;
                if ((data[i + 2] & 0xC7) != 0x05) continue;
                int disp = BitConverter.ToInt32(data, i + 3);
                uint insnRva = OffsetToRva(pe, data, i);
                if (insnRva != 0 && insnRva + 7 + disp == msgRva) return true;
            }
            return false;
        }
    }
}
