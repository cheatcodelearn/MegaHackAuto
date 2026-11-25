// MegaHackAutoGeode.cs — Mega Hack v9 installer, Geode build.
//
// Build (Windows, .NET Framework 4.x):
//   csc /optimize+ /target:exe /out:MegaHackAutoGeode.exe ^
//     /r:System.Web.Extensions.dll /r:System.IO.Compression.dll ^
//     /r:System.IO.Compression.FileSystem.dll /r:System.Net.Http.dll ^
//     MegaHackAutoGeode.cs
//
// Usage:
//   MegaHackAutoGeode.exe            install the Geode mod (default)
//   -standalone                      force the standalone (hackpro.dll) build
//   -dump                            extract + patch a copy to .\mh-geode-dump, no install
//   -strict                          abort instead of installing when no pattern matches
//   -check                           dry run: print chosen bundles, hashes and URL status
//   -where                           list every GD install, geode, mods, node-ids state
//   -install-only                    install, don't launch
//   -dir=PATH                        game folder override
//   -license=DIR                     license stub folder override
//
// Patch anchors (string based, survive updates that keep the strings):
//   "signature invalid"  -> test+jcc pair NOPed (8 bytes)
//   "data empty"         -> test+jcc pair near the loader call NOPed (8 bytes)
//   56 57 48 83 EC 48 48 83 79 10 40 -> B8 01 00 00 00 C3 (mov eax,1; ret)
//   geode build: cached login-state accessor -> B8 01 00 00 00 C3 (unique match only)

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
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MegaHackAuto
{
    internal sealed class Options
    {
        public bool Geode;
        public bool Standalone;
        public bool InstallOnly;
        public bool Strict;
        public bool Dump;
        public bool Check;
        public bool Where;
        public string GameDir;
        public string LicenseDir;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (var a in args)
            {
                if (a.Equals("-geode", StringComparison.OrdinalIgnoreCase)) o.Geode = true;
                else if (a.Equals("-standalone", StringComparison.OrdinalIgnoreCase)) o.Standalone = true;
                else if (a.Equals("-install-only", StringComparison.OrdinalIgnoreCase)) o.InstallOnly = true;
                else if (a.Equals("-strict", StringComparison.OrdinalIgnoreCase)) o.Strict = true;
                else if (a.Equals("-dump", StringComparison.OrdinalIgnoreCase)) o.Dump = true;
                else if (a.Equals("-check", StringComparison.OrdinalIgnoreCase)) o.Check = true;
                else if (a.Equals("-where", StringComparison.OrdinalIgnoreCase)) o.Where = true;
                else if (a.StartsWith("-license=", StringComparison.OrdinalIgnoreCase)) o.LicenseDir = a.Substring("-license=".Length);
                else if (a.StartsWith("-dir=", StringComparison.OrdinalIgnoreCase)) o.GameDir = a.Substring("-dir=".Length);
                else if (!a.StartsWith("-")) o.GameDir = a;
            }
            return o;
        }
    }

    internal struct PatchResult
    {
        public int Signature;
        public int Id;
        public int Login;
        public int LoginState;

        public int Total { get { return Signature + Id + Login + LoginState; } }
    }

    internal static class Program
    {
        private const string ManifestUrl = "https://absolllute.com/api/mega_hack/v9/install.json";
        private const string FilesBase = "https://absolllute.com/api/mega_hack/v9/files";
        private const string PackageName = "Mega Hack v9";
        // Bumped whenever the patch set changes, so an install made by an older
        // build of this tool is re-patched instead of reported as up to date.
        private const string ToolRevision = "2";
        private const string NodeIdsApi = "https://api.geode-sdk.org/v1/mods/geode.node-ids";
        private const string GeodeModFile = "absolllute.megahack.geode";
        private const string GeodeMarker = "absolllute.megahack.mhbuild";
        private const string StandaloneMarker = "hackpro.dll.mhbuild";

        private static readonly byte[] IdPrologue = { 0x56, 0x57, 0x48, 0x83, 0xEC, 0x48, 0x48, 0x83, 0x79, 0x10, 0x40 };
        private static readonly byte[] RetTrue = { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };
        // Geode build: the cached "is logged in" accessor. Its prologue plus the
        // cached-byte fast path ("mov al,1; cmp byte [rcx],0; jne return") is unique.
        private static readonly byte[] LoginStatePrologue =
        {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x78, 0x48, 0x8D, 0x6C, 0x24,
            0x70, 0x48, 0xC7, 0x45, 0x00, 0xFE, 0xFF, 0xFF, 0xFF, 0xB0, 0x01, 0x80, 0x39, 0x00, 0x0F, 0x85
        };

        // Both branch anchors are matched inside this span of the anchor string.
        private const int AnchorWindow = 0x1200;
        // The patched jcc has to land within this distance of the anchor string.
        private const int TargetSlack = 0x2000;

        private static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            var opt = Options.Parse(args);

            // This build targets the Geode mod by default; -standalone forces the
            // standalone (hackpro.dll) install path.
            if (!opt.Standalone) opt.Geode = true;

            if (opt.Check)
            {
                using (var httpCheck = new HttpClient())
                {
                    httpCheck.DefaultRequestHeaders.UserAgent.ParseAdd("MegaHackAuto/1.1");
                    try { return Check(httpCheck); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Check failed: " + ex);
                        return 1;
                    }
                }
            }

            if (opt.Where) return Where();

            var installs = FindGameDirs();
            string gameDir = ChooseGameDir(opt, installs);
            if (string.IsNullOrEmpty(gameDir))
            {
                Console.Error.WriteLine("Geometry Dash folder not found. Pass it as the last argument or -dir=PATH.");
                return 1;
            }

            gameDir = Path.GetFullPath(gameDir);
            if (!File.Exists(Path.Combine(gameDir, "GeometryDash.exe")))
            {
                Console.Error.WriteLine("GeometryDash.exe not found in " + gameDir);
                return 1;
            }
            Console.WriteLine("Game folder: " + gameDir);
            var running = Process.GetProcessesByName("GeometryDash");
            foreach (var process in running) process.Dispose();
            if (running.Length > 0)
            {
                Console.Error.WriteLine("Close Geometry Dash first.");
                return 1;
            }

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaHackAuto/1.1");
                try
                {
                    if (opt.Geode) InstallGeode(http, gameDir, opt);
                    else InstallStandalone(http, gameDir, opt);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Update failed: " + ex);
                    return 1;
                }
            }

            if (opt.Dump) return 0;
            if (!opt.InstallOnly) Launch(gameDir);
            return 0;
        }

        // ---------- manifest ----------

        private static Dictionary<string, object> PickBundle(string json, bool geode)
        {
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object packagesObj;
            if (!root.TryGetValue("packages", out packagesObj))
                throw new InvalidDataException("manifest has no packages array");

            foreach (var p in AsList(packagesObj))
            {
                var pkg = (Dictionary<string, object>)p;
                if (Convert.ToString(pkg["name"]) != PackageName) continue;

                Dictionary<string, object> best = null;
                long bestKey = -1;

                foreach (var b in AsList(pkg["bundles"]))
                {
                    var bundle = (Dictionary<string, object>)b;
                    bool isGeode = bundle.ContainsKey("geode") && Convert.ToBoolean(bundle["geode"]);
                    if (isGeode != geode) continue;
                    if (!SupportsWindows(bundle)) continue;

                    long key = VersionKey(Convert.ToString(bundle["version"]));
                    if (key > bestKey)
                    {
                        bestKey = key;
                        best = bundle;
                    }
                }

                if (best != null) return best;
            }

            throw new InvalidDataException("no " + (geode ? "Geode" : "standalone") + " Windows bundle in the manifest");
        }

        // JavaScriptSerializer hands nested JSON arrays back as ArrayList, not object[].
        private static List<object> AsList(object value)
        {
            var arrayList = value as System.Collections.ArrayList;
            if (arrayList != null)
            {
                var items = new List<object>(arrayList.Count);
                foreach (var item in arrayList) items.Add(item);
                return items;
            }

            var array = value as object[];
            if (array != null) return new List<object>(array);

            throw new InvalidDataException("manifest field is not a JSON array");
        }

        private static bool SupportsWindows(Dictionary<string, object> bundle)
        {
            object platforms;
            if (!bundle.TryGetValue("platform", out platforms)) return false;
            foreach (var p in AsList(platforms))
                if (Convert.ToString(p) == "win") return true;
            return false;
        }

        private static long VersionKey(string version)
        {
            long key = 0;
            var matches = Regex.Matches(version ?? string.Empty, @"\d+");
            for (int i = 0; i < matches.Count && i < 4; i++)
                key = key * 100 + long.Parse(matches[i].Value);
            return key;
        }

        private static string DownloadBundle(HttpClient http, Dictionary<string, object> bundle)
        {
            string version = Convert.ToString(bundle["version"]);
            string file = Convert.ToString(bundle["file"]);
            string expected = Convert.ToString(bundle["hash"]);
            string url = FilesBase + "/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(file);

            string dir = Path.Combine(Path.GetTempPath(), "mhauto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);

            Console.WriteLine("Downloading " + version + " -> " + file);
            using (var stream = http.GetStreamAsync(url).Result)
            using (var fs = File.Create(path))
                stream.CopyTo(fs);

            string actual = Sha256(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(dir);
                throw new InvalidDataException("bundle hash mismatch: " + actual);
            }

            return path;
        }

        // ---------- manifest dry run ----------

        private static int Check(HttpClient http)
        {
            string json = http.GetStringAsync(ManifestUrl).Result;

            foreach (bool geode in new[] { false, true })
            {
                var bundle = PickBundle(json, geode);
                string version = Convert.ToString(bundle["version"]);
                string file = Convert.ToString(bundle["file"]);
                string hash = Convert.ToString(bundle["hash"]);
                string url = FilesBase + "/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(file);

                if (!Regex.IsMatch(hash ?? string.Empty, "^[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException("manifest hash not 64 hex chars for " + version);

                Console.WriteLine((geode ? "geode" : "standalone") + ": " +
                                  Convert.ToString(bundle["name"]) + " | " + version + " | " + file);
                Console.WriteLine("  hash " + hash);
                Console.WriteLine("  url  " + url);

                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var response = http.SendAsync(request).Result)
                {
                    string length = response.Content.Headers.ContentLength.HasValue
                        ? " (" + response.Content.Headers.ContentLength.Value + " bytes)"
                        : string.Empty;
                    Console.WriteLine("  http " + (int)response.StatusCode + " " + response.StatusCode + length);
                }
            }

            return 0;
        }

        // ---------- standalone ----------

        private static void InstallStandalone(HttpClient http, string gameDir, Options opt)
        {
            var bundle = PickBundle(http.GetStringAsync(ManifestUrl).Result, false);
            string hash = Convert.ToString(bundle["hash"]);
            string expectedMarker = hash + "|" + ToolRevision;
            string marker = Path.Combine(gameDir, StandaloneMarker);
            string installed = Path.Combine(gameDir, "hackpro.dll");

            if (File.Exists(marker) && File.Exists(installed) &&
                string.Equals(File.ReadAllText(marker).Trim(), expectedMarker, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Mega Hack is up to date.");
                return;
            }

            string bundlePath = DownloadBundle(http, bundle);
            string workDir = Path.GetDirectoryName(bundlePath);
            string extractDir = Path.Combine(workDir, "x");
            ZipFile.ExtractToDirectory(bundlePath, extractDir);

            string dll = Path.Combine(extractDir, "hackpro.dll");
            if (!File.Exists(dll)) throw new InvalidDataException("hackpro.dll missing from bundle root");

            PatchResult patches = PatchFile(dll, false);
            Console.WriteLine("Patched " + patches.Total + " pattern(s) in hackpro.dll (sig " + patches.Signature +
                              ", id " + patches.Id + ", login " + patches.Login + ")");
            if (patches.Total == 0 && opt.Strict)
                throw new InvalidDataException("license patterns not found in hackpro.dll");

            string backup = installed + ".orig";
            if (File.Exists(installed) && !File.Exists(backup)) File.Copy(installed, backup);

            CopyTree(extractDir, gameDir);
            EnsureLicense(opt);
            File.WriteAllText(marker, expectedMarker);
            TryDelete(workDir);
            Console.WriteLine("Installed Mega Hack " + Convert.ToString(bundle["version"]));
        }

        // ---------- geode ----------

        private static void InstallGeode(HttpClient http, string gameDir, Options opt)
        {
            var bundle = PickBundle(http.GetStringAsync(ManifestUrl).Result, true);
            string version = Convert.ToString(bundle["version"]);
            string gdVersion = bundle.ContainsKey("gjver") ? Convert.ToString(bundle["gjver"]) : null;
            string hash = Convert.ToString(bundle["hash"]);
            string expectedMarker = hash + "|" + ToolRevision;
            string modsDir = GetModsDir(gameDir);
            string target = Path.Combine(modsDir, GeodeModFile);
            string marker = Path.Combine(modsDir, GeodeMarker);

            if (!opt.Dump && File.Exists(marker) && File.Exists(target) &&
                string.Equals(File.ReadAllText(marker).Trim(), expectedMarker, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Mega Hack (Geode) is up to date.");
                return;
            }

            string bundlePath = DownloadBundle(http, bundle);
            string workDir = Path.GetDirectoryName(bundlePath);
            string extractDir = Path.Combine(workDir, "x");
            ZipFile.ExtractToDirectory(bundlePath, extractDir);

            // A .geode is a zip. The core DLL inside is patched exactly like the
            // standalone hackpro.dll; every DLL in the bundle gets scanned so the
            // layout can change between vendor builds.
            var dlls = Directory.GetFiles(extractDir, "*.dll", SearchOption.AllDirectories);
            if (dlls.Length == 0) throw new InvalidDataException("no DLLs inside the .geode bundle");

            int totalPatches = 0;
            foreach (var dll in dlls)
            {
                PatchResult r = PatchFile(dll, true);
                totalPatches += r.Total;
                Console.WriteLine("  " + Relative(extractDir, dll) + " — sig " + r.Signature +
                                  ", id " + r.Id + ", login " + r.Login + ", state " + r.LoginState);
            }

            if (opt.Dump)
            {
                string dumpDir = Path.Combine(Directory.GetCurrentDirectory(), "mh-geode-dump");
                TryDelete(dumpDir);
                CopyTree(extractDir, dumpDir);
                TryDelete(workDir);
                Console.WriteLine("Dumped patched bundle (unzipped) to " + dumpDir + " — inspect mod.json and the DLLs there.");
                return;
            }

            if (!GeodePresent(gameDir))
                Console.WriteLine("[warn] Geode loader not detected in the game folder — the mod will sit in mods\\ until Geode is installed.");

            if (totalPatches == 0)
            {
                string msg = "no license patterns matched any DLL in the .geode bundle";
                if (opt.Strict) throw new InvalidDataException(msg);
                Console.WriteLine("[warn] " + msg + " — installing unpatched; if it asks for a key, run with -dump and send the DLL list.");
            }

            Directory.CreateDirectory(modsDir);
            string backup = target + ".orig";
            if (File.Exists(target))
            {
                if (!File.Exists(backup)) File.Copy(target, backup);
                File.Delete(target);
            }

            // Repack preserves the bundle's internal layout (mod.json at root,
            // per-platform subfolders if the vendor ships a universal build).
            RepackZip(extractDir, target);
            EnsureNodeIds(http, gameDir, gdVersion);
            EnsureLicense(opt);
            File.WriteAllText(marker, expectedMarker);

            // Drop a copy left in the legacy root mods folder by older builds;
            // Geode v6 never reads that folder and a duplicate only confuses things.
            string legacyDir = Path.Combine(gameDir, "mods");
            if (!string.Equals(Path.GetFullPath(modsDir), Path.GetFullPath(legacyDir), StringComparison.OrdinalIgnoreCase))
            {
                string legacyMod = Path.Combine(legacyDir, GeodeModFile);
                if (File.Exists(legacyMod))
                {
                    try
                    {
                        File.Delete(legacyMod);
                        try { File.Delete(Path.Combine(legacyDir, GeodeMarker)); } catch { }
                        Console.WriteLine("Removed stale legacy copy: mods\\" + GeodeModFile);
                    }
                    catch { }
                }
            }

            TryDelete(workDir);
            string shownDir = modsDir.Substring(gameDir.Length).TrimStart(Path.DirectorySeparatorChar);
            Console.WriteLine("Installed Mega Hack (Geode) " + version + " -> " + Path.Combine(shownDir, GeodeModFile));
            Console.WriteLine("Fully restart Geometry Dash so Geode re-scans the mods folder.");
        }

        // Mega Hack v9 declares geode.node-ids as a hard dependency; Geode silently
        // skips the mod when it is missing. Pull the newest accepted Windows build
        // from the Geode index and verify its hash before dropping it in mods\.
        private static void EnsureNodeIds(HttpClient http, string gameDir, string gdVersion)
        {
            try
            {
                if (ModsHaveId(gameDir, "geode.node-ids"))
                {
                    Console.WriteLine("node-ids: present");
                    return;
                }

                Console.WriteLine("node-ids: missing, installing from the Geode index");
                string json = http.GetStringAsync(NodeIdsApi).Result;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var payload = (Dictionary<string, object>)root["payload"];

                Dictionary<string, object> exact = null;
                Dictionary<string, object> any = null;
                foreach (var item in AsList(payload["versions"]))
                {
                    var version = (Dictionary<string, object>)item;
                    if (Convert.ToString(version["status"]) != "accepted") continue;

                    var gd = version["gd"] as Dictionary<string, object>;
                    string winVersion = gd != null && gd.ContainsKey("win") ? Convert.ToString(gd["win"]) : null;
                    if (string.IsNullOrEmpty(winVersion)) continue;

                    if (any == null) any = version;
                    if (!string.IsNullOrEmpty(gdVersion) && winVersion == gdVersion)
                    {
                        exact = version;
                        break;
                    }
                }

                var chosen = exact ?? any;
                if (chosen == null)
                {
                    Console.WriteLine("[warn] node-ids: no compatible Windows build on the index");
                    return;
                }

                string versionName = Convert.ToString(chosen["version"]);
                string hash = Convert.ToString(chosen["hash"]);
                string link = Convert.ToString(chosen["download_link"]);

                string temp = Path.Combine(Path.GetTempPath(), "mhauto-nodeids-" + Guid.NewGuid().ToString("N") + ".geode");
                using (var stream = http.GetStreamAsync(link).Result)
                using (var file = File.Create(temp))
                    stream.CopyTo(file);

                string actual = Sha256(temp);
                if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(temp); } catch { }
                    throw new InvalidDataException("node-ids hash mismatch: " + actual);
                }

                string mods = GetModsDir(gameDir);
                Directory.CreateDirectory(mods);
                File.Copy(temp, Path.Combine(mods, "geode.node-ids.geode"), true);
                try { File.Delete(temp); } catch { }

                Console.WriteLine("node-ids: installed " + versionName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[warn] node-ids install failed: " + ex.Message);
            }
        }

        // .NET Framework's ZipFile.CreateFromDirectory writes entry names with
        // backslashes on Windows; Geode's reader only resolves forward slashes,
        // so directories and resources would silently fail to load. Repack
        // manually with '/' separators.
        private static void RepackZip(string sourceDir, string targetPath)
        {
            var root = new DirectoryInfo(sourceDir);
            using (var zip = new ZipArchive(File.Create(targetPath), ZipArchiveMode.Create))
            {
                foreach (var file in root.GetFiles("*", SearchOption.AllDirectories))
                {
                    string rel = file.FullName.Substring(root.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
                    string entryName = rel.Replace('\\', '/');
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var output = entry.Open())
                    using (var input = File.OpenRead(file.FullName))
                        input.CopyTo(output);
                }
            }
        }

        private static bool GeodePresent(string gameDir)
        {
            foreach (var f in Directory.GetFiles(gameDir, "*.dll"))
                if (string.Equals(Path.GetFileName(f), "Geode.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            return Directory.Exists(Path.Combine(gameDir, "geode"));
        }

        // ---------- patching ----------

        private static PatchResult PatchFile(string path, bool loginState)
        {
            byte[] data = File.ReadAllBytes(path);
            var result = new PatchResult();

            result.Id = PatchExactPrologue(data);
            result.Signature = PatchBranchNearString(data, "signature invalid");
            result.Login = PatchBranchNearString(data, "data empty");
            if (loginState) result.LoginState = PatchLoginState(data);

            if (result.Total > 0) File.WriteAllBytes(path, data);
            return result;
        }

        // Geode build: force the cached login-state accessor to report "logged in".
        // Returns 0 unless the signature appears exactly once, so an update can
        // never turn this into a blind patch of the wrong function.
        private static int PatchLoginState(byte[] data)
        {
            int found = -1;
            int count = 0;
            for (int i = 0; i <= data.Length - LoginStatePrologue.Length; i++)
            {
                if (data[i] != LoginStatePrologue[0]) continue;
                if (!MatchesAt(data, i, LoginStatePrologue)) continue;
                found = i;
                count++;
            }

            if (count != 1) return 0;
            Array.Copy(RetTrue, 0, data, found, RetTrue.Length);
            return 1;
        }

        // The 11-byte ID/HWID prologue must occur exactly once; a second match in
        // a future build would mean guessing, and a wrong stub breaks the mod.
        private static int PatchExactPrologue(byte[] data)
        {
            int found = -1;
            int count = 0;
            for (int i = 0; i <= data.Length - IdPrologue.Length; i++)
            {
                if (data[i] != IdPrologue[0]) continue;
                if (!MatchesAt(data, i, IdPrologue)) continue;
                found = i;
                count++;
            }

            if (count != 1) return 0;
            Array.Copy(RetTrue, 0, data, found, RetTrue.Length);
            return 1;
        }

        // NOPs the "test al, al; jz error" pair (8 bytes) that guards a license check.
        // The branch must land near the anchor string's error path, otherwise it is
        // some unrelated test/jcc and is left alone.
        private static int PatchBranchNearString(byte[] data, string anchor)
        {
            int patched = 0;
            byte[] needle = Encoding.ASCII.GetBytes(anchor);

            foreach (int stringOffset in FindAll(data, needle))
            {
                int best = -1;
                int bestDistance = int.MaxValue;

                int from = Math.Max(0, stringOffset - AnchorWindow);
                int to = Math.Min(data.Length - 8, stringOffset + AnchorWindow);

                for (int i = from; i <= to; i++)
                {
                    if (data[i] != 0x84 || data[i + 1] != 0xC0 || data[i + 2] != 0x0F || data[i + 3] != 0x84) continue;

                    int relative = BitConverter.ToInt32(data, i + 4);
                    int target = i + 8 + relative;
                    if (target < 0 || target >= data.Length) continue;
                    if (Math.Abs(target - stringOffset) > TargetSlack) continue;

                    int distance = Math.Abs(i - stringOffset);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                if (best < 0) continue;
                for (int k = 0; k < 8; k++) data[best + k] = 0x90;
                patched++;
            }

            return patched;
        }

        private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
        {
            if (offset < 0 || offset + pattern.Length > data.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
                if (data[offset + i] != pattern[i]) return false;
            return true;
        }

        private static List<int> FindAll(byte[] data, byte[] needle)
        {
            var hits = new List<int>();
            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                if (data[i] != needle[0]) continue;
                bool match = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) hits.Add(i);
            }
            return hits;
        }

        // ---------- license stub ----------

        private static void EnsureLicense(Options opt)
        {
            string dir = opt.LicenseDir;
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "absolllute.megahack");

            string path = Path.Combine(dir, "license");
            if (File.Exists(path)) return;

            Directory.CreateDirectory(dir);

            using (var rng = new RNGCryptoServiceProvider())
            {
                string id = Hex(rng, 32);
                string guid = Guid.NewGuid().ToString();

                var inner = new Dictionary<string, string>();
                inner["id"] = id;
                inner["guid2"] = guid;
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(inner)));

                string signature = Convert.ToBase64String(new byte[64]);
                string token = Hex(rng, 16);

                var outer = new Dictionary<string, string>();
                outer["data"] = payload;
                outer["sig"] = signature;
                outer["token"] = token;

                File.WriteAllText(path, new JavaScriptSerializer().Serialize(outer));
            }

            Console.WriteLine("Wrote license stub: " + path);
        }

        private static string Hex(RandomNumberGenerator rng, int bytes)
        {
            var buffer = new byte[bytes];
            rng.GetBytes(buffer);
            var sb = new StringBuilder(bytes * 2);
            foreach (var b in buffer) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ---------- filesystem / process ----------

        private static List<string> FindGameDirs()
        {
            string[] keys =
            {
                @"HKEY_CURRENT_USER\Software\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
            };

            var roots = new List<string>();
            foreach (var key in keys)
            {
                try
                {
                    var value = Registry.GetValue(key, "SteamPath", null) as string;
                    if (!string.IsNullOrEmpty(value) && Directory.Exists(value)) roots.Add(value);
                }
                catch { }
            }

            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
            roots.Add(@"D:\Steam");
            roots.Add(@"D:\SteamLibrary");
            roots.Add(@"E:\SteamLibrary");

            var dirs = new List<string>();
            foreach (var root in roots)
            {
                AddGameDir(dirs, Path.Combine(root, @"steamapps\common\Geometry Dash"));

                // Secondary Steam libraries live in libraryfolders.vdf.
                string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string library = m.Groups[1].Value.Replace("\\\\", "\\");
                        AddGameDir(dirs, Path.Combine(library, @"steamapps\common\Geometry Dash"));
                    }
                }
                catch { }
            }

            // A running copy is the strongest hint about which install is the real one.
            try
            {
                foreach (var process in Process.GetProcessesByName("GeometryDash"))
                {
                    try { AddGameDir(dirs, Path.GetDirectoryName(process.MainModule.FileName)); }
                    catch { }
                    process.Dispose();
                }
            }
            catch { }

            return dirs;
        }

        private static void AddGameDir(List<string> dirs, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            try { candidate = Path.GetFullPath(candidate); }
            catch { return; }
            if (!File.Exists(Path.Combine(candidate, "GeometryDash.exe"))) return;
            foreach (var existing in dirs)
                if (string.Equals(existing.TrimEnd('\\'), candidate.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return;
            dirs.Add(candidate);
        }

        private static string ChooseGameDir(Options opt, List<string> dirs)
        {
            if (!string.IsNullOrEmpty(opt.GameDir)) return opt.GameDir;
            if (dirs.Count == 0) return null;
            if (dirs.Count == 1) return dirs[0];

            var withGeode = new List<string>();
            foreach (var dir in dirs)
                if (GeodePresent(dir)) withGeode.Add(dir);

            var pool = withGeode.Count > 0 ? withGeode : dirs;
            string best = pool[0];
            if (pool.Count > 1)
            {
                DateTime bestTime = DateTime.MinValue;
                foreach (var dir in pool)
                {
                    DateTime stamp = LastActivity(dir);
                    if (stamp > bestTime)
                    {
                        bestTime = stamp;
                        best = dir;
                    }
                }

                Console.WriteLine("[warn] several Geometry Dash installs found:");
                foreach (var dir in dirs)
                    Console.WriteLine("   " + dir +
                                      (string.Equals(dir, best, StringComparison.OrdinalIgnoreCase) ? "   <-- using" : string.Empty));
                Console.WriteLine("   pin one with -dir=PATH if that is wrong.");
            }
            return best;
        }

        // Geode v6 moved the mods folder to <GD>\geode\mods. Older versions read
        // <GD>\mods. Pick by the version the loader logs, then by folder shape.
        private static string GetModsDir(string gameDir)
        {
            string nested = Path.Combine(gameDir, "geode", "mods");
            if (GeodeMajor(gameDir) >= 6) return nested;
            if (Directory.Exists(nested)) return nested;
            return Path.Combine(gameDir, "mods");
        }

        private static int GeodeMajor(string gameDir)
        {
            try
            {
                string logs = Path.Combine(gameDir, "geode", "logs");
                if (!Directory.Exists(logs)) return 0;

                DateTime newest = DateTime.MinValue;
                string newestPath = null;
                foreach (var file in Directory.GetFiles(logs, "*.log"))
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (stamp > newest)
                    {
                        newest = stamp;
                        newestPath = file;
                    }
                }
                if (newestPath == null) return 0;

                string text;
                using (var stream = new FileStream(newestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                var match = Regex.Match(text, @"Geode v(\d+)\.");
                if (match.Success) return int.Parse(match.Groups[1].Value);
            }
            catch { }
            return 0;
        }

        private static DateTime LastActivity(string gameDir)
        {            DateTime newest = DateTime.MinValue;
            string logs = Path.Combine(gameDir, "geode", "logs");
            if (Directory.Exists(logs))
                foreach (var file in Directory.GetFiles(logs, "*.log"))
                    if (File.GetLastWriteTimeUtc(file) > newest) newest = File.GetLastWriteTimeUtc(file);
            string mods = Path.Combine(gameDir, "mods");
            if (Directory.Exists(mods))
                foreach (var file in Directory.GetFiles(mods))
                    if (File.GetLastWriteTimeUtc(file) > newest) newest = File.GetLastWriteTimeUtc(file);
            return newest;
        }

        private static int Where()
        {
            var dirs = FindGameDirs();
            if (dirs.Count == 0)
            {
                Console.WriteLine("no Geometry Dash installs found");
                return 1;
            }

            foreach (var dir in dirs)
            {
                string mods = GetModsDir(dir);
                int count = Directory.Exists(mods) ? Directory.GetFiles(mods, "*.geode*").Length : 0;
                Console.WriteLine(dir);
                Console.WriteLine("  gd version : " + GameVersion(dir));
                Console.WriteLine("  geode      : " + (GeodePresent(dir) ? "yes (v" + GeodeMajor(dir) + ")" : "no"));
                Console.WriteLine("  mods dir   : " + mods);
                Console.WriteLine("  mods       : " + count + " file(s)");
                Console.WriteLine("  megahack   : " + (File.Exists(Path.Combine(mods, GeodeModFile)) ? "installed" : "missing"));
                Console.WriteLine("  node-ids   : " + (ModsHaveId(dir, "geode.node-ids") ? "installed" : "missing"));
            }
            return 0;
        }

        private static string GameVersion(string gameDir)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(Path.Combine(gameDir, "GeometryDash.exe"));
                return (info.ProductVersion ?? info.FileVersion ?? "?").Trim();
            }
            catch { return "?"; }
        }

        private static bool ModsHaveId(string gameDir, string id)
        {
            string mods = GetModsDir(gameDir);
            if (!Directory.Exists(mods)) return false;
            foreach (var file in Directory.GetFiles(mods, "*.geode*"))
            {
                // A disabled copy is not loaded by Geode, so it does not count.
                if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using (var zip = ZipFile.OpenRead(file))
                    {
                        var entry = zip.GetEntry("mod.json");
                        if (entry == null) continue;
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                            object metaId;
                            if (meta.TryGetValue("id", out metaId) && Convert.ToString(metaId) == id) return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        private static void CopyTree(string source, string target)
        {
            var root = new DirectoryInfo(source);
            foreach (var dir in root.GetDirectories("*", SearchOption.AllDirectories))
            {
                string rel = dir.FullName.Substring(root.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(target, rel));
            }
            foreach (var file in root.GetFiles("*", SearchOption.AllDirectories))
            {
                string rel = file.FullName.Substring(root.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
                string dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file.FullName, dest, true);
            }
        }

        private static string Relative(string root, string path)
        {
            return path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        private static void Launch(string gameDir)
        {
            string exe = Path.Combine(gameDir, "GeometryDash.exe");
            try
            {
                // cmd /c start detaches the game from this console. Starting it
                // directly attaches it to the terminal, so closing the window
                // that ran the installer would also kill Geometry Dash.
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"\" \"" + exe + "\"",
                    WorkingDirectory = gameDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                for (int i = 0; i < 20; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    var started = Process.GetProcessesByName("GeometryDash");
                    foreach (var process in started) process.Dispose();
                    if (started.Length > 0) return;
                }

                Process.Start("steam://rungameid/322170");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Launch failed: " + ex.Message);
            }
        }
    }
}
