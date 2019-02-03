using BattleTech;
using BattleTech.Data;
using Harmony;
using HBS.Util;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ModTek.Caches;
using ModTek.UI;
using ModTek.Util;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Local

namespace ModTek
{
    using static Logger;

    public static class ModTek
    {
        private static readonly string[] IGNORE_LIST = { ".DS_STORE", "~", ".nomedia" };
        private static readonly string[] MODTEK_TYPES = { "Video", "AdvancedJSONMerge" };
        private static readonly string[] VANILLA_TYPES = Enum.GetNames(typeof(BattleTechResourceType));

        public static bool HasLoaded { get; private set; }

        // game paths/directories
        public static string GameDirectory { get; private set; }
        public static string ModsDirectory { get; private set; }
        public static string StreamingAssetsDirectory { get; private set; }

        // file/directory names
        private const string MODS_DIRECTORY_NAME = "Mods";
        private const string MOD_JSON_NAME = "mod.json";
        private const string MODTEK_DIRECTORY_NAME = "ModTek";
        private const string TEMP_MODTEK_DIRECTORY_NAME = ".modtek";
        private const string CACHE_DIRECTORY_NAME = "Cache";
        private const string MERGE_CACHE_FILE_NAME = "merge_cache.json";
        private const string TYPE_CACHE_FILE_NAME = "type_cache.json";
        private const string LOG_NAME = "ModTek.log";
        private const string LOAD_ORDER_FILE_NAME = "load_order.json";
        private const string DATABASE_DIRECTORY_NAME = "Database";
        private const string MDD_FILE_NAME = "MetadataDatabase.db";
        private const string DB_CACHE_FILE_NAME = "database_cache.json";
        private const string HARMONY_SUMMARY_FILE_NAME = "harmony_summary.log";
        private const string CONFIG_FILE_NAME = "config.json";

        // ModTek paths/directories
        internal static string ModTekDirectory { get; private set; }
        internal static string TempModTekDirectory { get; private set; }
        internal static string CacheDirectory { get; private set; }
        internal static string DatabaseDirectory { get; private set; }
        internal static string MergeCachePath { get; private set; }
        internal static string TypeCachePath { get; private set; }
        internal static string MDDBPath { get; private set; }
        internal static string ModMDDBPath { get; private set; }
        internal static string DBCachePath { get; private set; }
        internal static string LoadOrderPath { get; private set; }
        internal static string HarmonySummaryPath { get; private set; }
        internal static string ConfigPath { get; private set; }

        // internal temp structures
        private static Dictionary<string, JObject> cachedJObjects = new Dictionary<string, JObject>();
        private static Dictionary<string, List<ModEntry>> entriesByMod = new Dictionary<string, List<ModEntry>>();
        private static Stopwatch stopwatch = new Stopwatch();

        // internal structures
        private static List<string> loadOrder;
        internal static Configuration Config;
        internal static HashSet<string> FailedToLoadMods { get; } = new HashSet<string>();

        // the end result of loading mods, these are used to push into game data through patches
        internal static VersionManifest CachedVersionManifest;
        internal static List<ModEntry> BTRLEntries = new List<ModEntry>();
        internal static Dictionary<string, Dictionary<string, VersionManifestEntry>> CustomResources = new Dictionary<string, Dictionary<string, VersionManifestEntry>>();
        internal static Dictionary<string, string> ModAssetBundlePaths { get; } = new Dictionary<string, string>();
        internal static Dictionary<string, string> ModVideos { get; } = new Dictionary<string, string>();
        internal static Dictionary<string, Assembly> TryResolveAssemblies = new Dictionary<string, Assembly>();
        internal static Dictionary<string, Assembly> ModAssemblies = new Dictionary<string, Assembly>();


        // INITIALIZATION (called by injected code)
        [UsedImplicitly]
        public static void Init()
        {
            stopwatch.Start();

            // if the manifest directory is null, there is something seriously wrong
            var manifestDirectory = Path.GetDirectoryName(VersionManifestUtilities.MANIFEST_FILEPATH);
            if (manifestDirectory == null)
                return;

            // setup directories
            ModsDirectory = Path.GetFullPath(
                Path.Combine(manifestDirectory,
                    Path.Combine(Path.Combine(Path.Combine(
                        "..", ".."), ".."), MODS_DIRECTORY_NAME)));

            StreamingAssetsDirectory = Path.GetFullPath(Path.Combine(manifestDirectory, ".."));
            GameDirectory = Path.GetFullPath(Path.Combine(Path.Combine(StreamingAssetsDirectory, ".."), ".."));
            MDDBPath = Path.Combine(Path.Combine(StreamingAssetsDirectory, "MDD"), MDD_FILE_NAME);

            ModTekDirectory = Path.Combine(ModsDirectory, MODTEK_DIRECTORY_NAME);
            TempModTekDirectory = Path.Combine(ModsDirectory, TEMP_MODTEK_DIRECTORY_NAME);
            CacheDirectory = Path.Combine(TempModTekDirectory, CACHE_DIRECTORY_NAME);
            DatabaseDirectory = Path.Combine(TempModTekDirectory, DATABASE_DIRECTORY_NAME);

            LogPath = Path.Combine(TempModTekDirectory, LOG_NAME);
            HarmonySummaryPath = Path.Combine(TempModTekDirectory, HARMONY_SUMMARY_FILE_NAME);
            LoadOrderPath = Path.Combine(TempModTekDirectory, LOAD_ORDER_FILE_NAME);
            MergeCachePath = Path.Combine(CacheDirectory, MERGE_CACHE_FILE_NAME);
            TypeCachePath = Path.Combine(CacheDirectory, TYPE_CACHE_FILE_NAME);
            ModMDDBPath = Path.Combine(DatabaseDirectory, MDD_FILE_NAME);
            DBCachePath = Path.Combine(DatabaseDirectory, DB_CACHE_FILE_NAME);
            ConfigPath = Path.Combine(TempModTekDirectory, CONFIG_FILE_NAME);

            // creates the directories above it as well
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(DatabaseDirectory);

            var versionString = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            // create log file, overwriting if it's already there
            using (var logWriter = File.CreateText(LogPath))
            {
                logWriter.WriteLine($"ModTek v{versionString} -- {DateTime.Now}");
            }

            // load progress bar
            if (!ProgressPanel.Initialize(ModTekDirectory, $"ModTek v{versionString}"))
            {
                Log("Failed to load progress bar.  Skipping mod loading completely.");
                Finish();
            }

            // read config
            Config = Configuration.FromFile(ConfigPath);

            SetupAssemblyResolveHandler();
            TryResolveAssemblies.Add("0Harmony", Assembly.GetAssembly(typeof(HarmonyInstance)));

            try
            {
                HarmonyInstance.Create("io.github.mpstark.ModTek").PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                LogException("PATCHING FAILED!", e);
                CloseLogStream();
                return;
            }

            LoadMods();
            BuildModManifestEntries();
        }

        public static void Finish()
        {
            HasLoaded = true;

            stopwatch.Stop();
            Log("");
            LogWithDate($"Done. Elapsed running time: {stopwatch.Elapsed.TotalSeconds} seconds\n");

            CloseLogStream();

            cachedJObjects = null;
            entriesByMod = null;
            stopwatch = null;
        }


        // UTIL
        private static void PrintHarmonySummary(string path)
        {
            var harmony = HarmonyInstance.Create("io.github.mpstark.ModTek");

            var patchedMethods = harmony.GetPatchedMethods().ToArray();
            if (patchedMethods.Length == 0)
                return;

            using (var writer = File.CreateText(path))
            {
                writer.WriteLine($"Harmony Patched Methods (after ModTek startup) -- {DateTime.Now}\n");

                foreach (var method in patchedMethods)
                {
                    var info = harmony.GetPatchInfo(method);

                    if (info == null || method.ReflectedType == null)
                        continue;

                    writer.WriteLine($"{method.ReflectedType.FullName}.{method.Name}:");

                    // prefixes
                    if (info.Prefixes.Count != 0)
                        writer.WriteLine("\tPrefixes:");
                    foreach (var patch in info.Prefixes)
                        writer.WriteLine($"\t\t{patch.owner}");

                    // transpilers
                    if (info.Transpilers.Count != 0)
                        writer.WriteLine("\tTranspilers:");
                    foreach (var patch in info.Transpilers)
                        writer.WriteLine($"\t\t{patch.owner}");

                    // postfixes
                    if (info.Postfixes.Count != 0)
                        writer.WriteLine("\tPostfixes:");
                    foreach (var patch in info.Postfixes)
                        writer.WriteLine($"\t\t{patch.owner}");

                    writer.WriteLine("");
                }
            }
        }

        private static bool FileIsOnDenyList(string filePath)
        {
            return IGNORE_LIST.Any(x => filePath.EndsWith(x, StringComparison.InvariantCultureIgnoreCase));
        }

        internal static string ResolvePath(string path, string rootPathToUse)
        {
            if (!Path.IsPathRooted(path))
                path = Path.Combine(rootPathToUse, path);

            return Path.GetFullPath(path);
        }

        internal static string GetRelativePath(string path, string rootPath)
        {
            if (!Path.IsPathRooted(path))
                return path;

            rootPath = Path.GetFullPath(rootPath);
            if (rootPath.Last() != Path.DirectorySeparatorChar)
                rootPath += Path.DirectorySeparatorChar;

            var pathUri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            var rootUri = new Uri(rootPath, UriKind.Absolute);

            if (pathUri.Scheme != rootUri.Scheme)
                return path;

            var relativeUri = rootUri.MakeRelativeUri(pathUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (pathUri.Scheme.Equals("file", StringComparison.InvariantCultureIgnoreCase))
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return relativePath;
        }

        internal static JObject ParseGameJSONFile(string path)
        {
            if (cachedJObjects.ContainsKey(path))
                return cachedJObjects[path];

            // because StripHBSCommentsFromJSON is private, use Harmony to call the method
            var commentsStripped = Traverse.Create(typeof(JSONSerializationUtility)).Method("StripHBSCommentsFromJSON", File.ReadAllText(path)).GetValue<string>();

            if (commentsStripped == null)
                throw new Exception("StripHBSCommentsFromJSON returned null.");

            // add missing commas, this only fixes if there is a newline
            var rgx = new Regex(@"(\]|\}|""|[A-Za-z0-9])\s*\n\s*(\[|\{|"")", RegexOptions.Singleline);
            var commasAdded = rgx.Replace(commentsStripped, "$1,\n$2");

            cachedJObjects[path] = JObject.Parse(commasAdded);
            return cachedJObjects[path];
        }

        private static string InferIDFromJObject(JObject jObj)
        {
            if (jObj == null)
                return null;

            // go through the different kinds of id storage in JSONs
            string[] jPaths = { "Description.Id", "id", "Id", "ID", "identifier", "Identifier" };
            return jPaths.Select(jPath => (string) jObj.SelectToken(jPath)).FirstOrDefault(id => id != null);
        }

        private static string InferIDFromFile(string path)
        {
            // if not json, return the file name without the extension, as this is what HBS uses
            var ext = Path.GetExtension(path);
            if (ext == null || ext.ToLowerInvariant() != ".json" || !File.Exists(path))
                return Path.GetFileNameWithoutExtension(path);

            // read the json and get ID out of it if able to
            return InferIDFromJObject(ParseGameJSONFile(path)) ?? Path.GetFileNameWithoutExtension(path);
        }

        private static VersionManifestEntry GetEntryByID(string id)
        {
            var containingCustomType = CustomResources.Where(pair => pair.Value.ContainsKey(id)).ToArray();
            if (containingCustomType.Any())
                return containingCustomType.Last().Value[id];

            return BTRLEntries.FindLast(x => x.Id == id)?.GetVersionManifestEntry()
                ?? CachedVersionManifest.Find(x => x.Id == id);
        }


        // READING mod.json AND INIT MODS
        private static bool LoadMod(ModDef modDef)
        {
            var potentialAdditions = new List<ModEntry>();

            Log($"{modDef.Name} {modDef.Version}");

            // load out of the manifest
            if (modDef.LoadImplicitManifest && modDef.Manifest.All(x => Path.GetFullPath(Path.Combine(modDef.Directory, x.Path)) != Path.GetFullPath(Path.Combine(modDef.Directory, "StreamingAssets"))))
                modDef.Manifest.Add(new ModEntry("StreamingAssets", true));

            // read in custom resource types
            foreach (var customResourceType in modDef.CustomResourceTypes)
            {
                if (VANILLA_TYPES.Contains(customResourceType) || MODTEK_TYPES.Contains(customResourceType))
                {
                    Log($"\t{modDef.Name} has a custom resource type that has the same name as a vanilla/modtek resource type. Ignoring this type.");
                    continue;
                }

                if (!CustomResources.ContainsKey(customResourceType))
                    CustomResources.Add(customResourceType, new Dictionary<string, VersionManifestEntry>());
            }

            // note: if a JSON has errors, this mod will not load, since InferIDFromFile will throw from parsing the JSON
            foreach (var modEntry in modDef.Manifest)
            {
                // handle prefabs; they have potential internal path to assetbundle
                if (modEntry.Type == "Prefab" && !string.IsNullOrEmpty(modEntry.AssetBundleName))
                {
                    if (!potentialAdditions.Any(x => x.Type == "AssetBundle" && x.Id == modEntry.AssetBundleName))
                    {
                        Log($"\t{modDef.Name} has a Prefab that's referencing an AssetBundle that hasn't been loaded. Put the assetbundle first in the manifest!");
                        return false ;
                    }

                    modEntry.Id = Path.GetFileNameWithoutExtension(modEntry.Path);

                    if (!FileIsOnDenyList(modEntry.Path))
                        potentialAdditions.Add(modEntry);

                    continue;
                }

                if (string.IsNullOrEmpty(modEntry.Path) && string.IsNullOrEmpty(modEntry.Type) && modEntry.Path != "StreamingAssets")
                {
                    Log($"\t{modDef.Name} has a manifest entry that is missing its path or type! Aborting load.");
                    return false;
                }

                if (!string.IsNullOrEmpty(modEntry.Type)
                    && !VANILLA_TYPES.Contains(modEntry.Type)
                    && !MODTEK_TYPES.Contains(modEntry.Type)
                    && !CustomResources.ContainsKey(modEntry.Type))
                {
                    Log($"\t{modDef.Name} has a manifest entry that has a type '{modEntry.Type}' that doesn't match an existing type and isn't declared in CustomResourceTypes");
                    return false;
                }

                var entryPath = Path.GetFullPath(Path.Combine(modDef.Directory, modEntry.Path));
                if (Directory.Exists(entryPath))
                {
                    // path is a directory, add all the files there
                    var files = Directory.GetFiles(entryPath, "*", SearchOption.AllDirectories).Where(filePath => !FileIsOnDenyList(filePath));
                    foreach (var filePath in files)
                    {
                        var path = Path.GetFullPath(filePath);
                        try
                        {
                            var childModEntry = new ModEntry(modEntry, path, InferIDFromFile(filePath));
                            potentialAdditions.Add(childModEntry);
                        }
                        catch(Exception e)
                        {
                            LogException($"\tCanceling {modDef.Name} load!\n\tCaught exception reading file at {GetRelativePath(path, GameDirectory)}", e);
                            return false;
                        }
                    }
                }
                else if (File.Exists(entryPath) && !FileIsOnDenyList(entryPath))
                {
                    // path is a file, add the single entry
                    try
                    {
                        modEntry.Id = modEntry.Id ?? InferIDFromFile(entryPath);
                        modEntry.Path = entryPath;
                        potentialAdditions.Add(modEntry);
                    }
                    catch (Exception e)
                    {
                        LogException($"\tCanceling {modDef.Name} load!\n\tCaught exception reading file at {GetRelativePath(entryPath, GameDirectory)}", e);
                        return false;
                    }
                }
                else if (modEntry.Path != "StreamingAssets")
                {
                    // path is not StreamingAssets and it's missing
                    Log($"\tMissing Entry: Manifest specifies file/directory of {modEntry.Type} at path {modEntry.Path}, but it's not there. Continuing to load.");
                }
            }

            // load mod dll
            if (modDef.DLL != null)
            {
                var dllPath = Path.Combine(modDef.Directory, modDef.DLL);
                string typeName = null;
                var methodName = "Init";

                if (!File.Exists(dllPath))
                {
                    Log($"\t{modDef.Name} has a DLL specified ({dllPath}), but it's missing! Aborting load.");
                    return false;
                }

                if (modDef.DLLEntryPoint != null)
                {
                    var pos = modDef.DLLEntryPoint.LastIndexOf('.');
                    if (pos == -1)
                    {
                        methodName = modDef.DLLEntryPoint;
                    }
                    else
                    {
                        typeName = modDef.DLLEntryPoint.Substring(0, pos);
                        methodName = modDef.DLLEntryPoint.Substring(pos + 1);
                    }
                }

                var assembly = AssemblyUtil.LoadDLL(dllPath, methodName, typeName,
                    new object[] { modDef.Directory, modDef.Settings.ToString(Formatting.None) });

                if (assembly == null)
                {
                    Log($"\t{modDef.Name}: Failed to load mod assembly at path {dllPath}. Check BTML log!");
                    return false;
                }

                ModAssemblies.Add(modDef.Name, assembly);

                if (!modDef.EnableAssemblyVersionCheck)
                    TryResolveAssemblies.Add(assembly.GetName().Name, assembly);
            }

            if (potentialAdditions.Count <= 0)
                return true;

            Log($"\t{potentialAdditions.Count} entries");

            // actually add the additions, since we successfully got through loading the other stuff
            entriesByMod[modDef.Name] = potentialAdditions;
            return true;
        }

        internal static void SetupAssemblyResolveHandler()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var resolvingName = new AssemblyName(args.Name);
                return !TryResolveAssemblies.TryGetValue(resolvingName.Name, out var assembly) ? null : assembly;
            };
        }

        internal static void LoadMods()
        {
            ProgressPanel.SubmitWork(LoadModsLoop);
        }

        internal static IEnumerator<ProgressReport> LoadModsLoop()
        {
            yield return new ProgressReport(1, "Initializing Mods", "");

            // find all sub-directories that have a mod.json file
            var modDirectories = Directory.GetDirectories(ModsDirectory)
                .Where(x => File.Exists(Path.Combine(x, MOD_JSON_NAME))).ToArray();

            if (modDirectories.Length == 0)
            {
                Log("No ModTek-compatible mods found.");
                yield break;
            }

            // create ModDef objects for each mod.json file
            var modDefs = new Dictionary<string, ModDef>();
            foreach (var modDirectory in modDirectories)
            {
                ModDef modDef;
                var modDefPath = Path.Combine(modDirectory, MOD_JSON_NAME);

                try
                {
                    modDef = ModDef.CreateFromPath(modDefPath);
                }
                catch (Exception e)
                {
                    FailedToLoadMods.Add(GetRelativePath(modDirectory, ModsDirectory));
                    LogException($"Caught exception while parsing {MOD_JSON_NAME} at path {modDefPath}", e);
                    continue;
                }

                if (!modDef.Enabled)
                {
                    Log($"Will not load {modDef.Name} because it's disabled.");
                    continue;
                }

                if (modDefs.ContainsKey(modDef.Name))
                {
                    Log($"Already loaded a mod named {modDef.Name}. Skipping load from {modDef.Directory}.");
                    continue;
                }

                // check game version vs. specific version or against min/max
                if (!string.IsNullOrEmpty(modDef.BattleTechVersion) && !VersionInfo.ProductVersion.StartsWith(modDef.BattleTechVersion))
                {
                    if (!modDef.IgnoreLoadFailure)
                    {
                        Log($"Will not load {modDef.Name} because it specifies a game version and this isn't it ({modDef.BattleTechVersion} vs. game {VersionInfo.ProductVersion})");
                        FailedToLoadMods.Add(modDef.Name);
                    }

                    continue;
                }

                var btgVersion = new Version(VersionInfo.ProductVersion);

                if (!string.IsNullOrEmpty(modDef.BattleTechVersionMin))
                {
                    var minVersion = new Version(modDef.BattleTechVersionMin);

                    if (btgVersion < minVersion)
                    {
                        if (!modDef.IgnoreLoadFailure)
                        {
                            Log($"Will not load {modDef.Name} because it doesn't match the min version set in the mod.json ({modDef.BattleTechVersionMin} vs. game {VersionInfo.ProductVersion})");
                            FailedToLoadMods.Add(modDef.Name);
                        }

                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(modDef.BattleTechVersionMax))
                {
                    var maxVersion = new Version(modDef.BattleTechVersionMax);

                    if (btgVersion > maxVersion)
                    {
                        if (!modDef.IgnoreLoadFailure)
                        {
                            Log($"Will not load {modDef.Name} because it doesn't match the max version set in the mod.json ({modDef.BattleTechVersionMax} vs. game {VersionInfo.ProductVersion})");
                            FailedToLoadMods.Add(modDef.Name);
                        }

                        continue;
                    }
                }

                modDefs.Add(modDef.Name, modDef);
            }

            loadOrder = LoadOrder.CreateLoadOrder(modDefs, out var willNotLoad, LoadOrder.FromFile(LoadOrderPath));
            foreach (var modName in willNotLoad)
            {
                if (modDefs[modName].IgnoreLoadFailure)
                    continue;

                Log($"Will not load {modName} because it's lacking a dependency or has a conflict.");
                FailedToLoadMods.Add(modName);
            }
            Log("");

            // lists guarantee order
            var modLoaded = 0;

            foreach (var modName in loadOrder)
            {
                var modDef = modDefs[modName];

                if (modDef.DependsOn.Intersect(FailedToLoadMods).Any())
                {
                    if (!modDef.IgnoreLoadFailure)
                    {
                        Log($"Skipping load of {modName} because one of its dependencies failed to load.");
                        FailedToLoadMods.Add(modName);
                    }

                    continue;
                }

                yield return new ProgressReport(modLoaded++ / ((float)loadOrder.Count), "Initializing Mods", $"{modDef.Name} {modDef.Version}", true);

                try
                {
                    if (!LoadMod(modDef) && !modDef.IgnoreLoadFailure)
                        FailedToLoadMods.Add(modName);
                }
                catch (Exception e)
                {
                    if (modDef.IgnoreLoadFailure)
                        continue;

                    LogException($"Tried to load mod: {modDef.Name}, but something went wrong. Make sure all of your JSON is correct!", e);
                    FailedToLoadMods.Add(modName);
                }
            }

            PrintHarmonySummary(HarmonySummaryPath);
            LoadOrder.ToFile(loadOrder, LoadOrderPath);
        }


        // ADDING MOD CONTENT TO THE GAME
        private static void AddModEntry(ModEntry modEntry)
        {
            if (modEntry.Path == null)
                return;

            // custom type
            if (CustomResources.ContainsKey(modEntry.Type))
            {
                Log($"\tAdd/Replace (CustomResource): \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");
                CustomResources[modEntry.Type][modEntry.Id] = modEntry.GetVersionManifestEntry();
                return;
            }

            VersionManifestAddendum addendum = null;
            if (!string.IsNullOrEmpty(modEntry.AddToAddendum))
            {
                addendum = CachedVersionManifest.GetAddendumByName(modEntry.AddToAddendum);

                if (addendum == null)
                {
                    Log($"\tCannot add {modEntry.Id} to {modEntry.AddToAddendum} because addendum doesn't exist in the manifest.");
                    return;
                }
            }

            // special handling for particular types
            switch (modEntry.Type)
            {
                case "AssetBundle":
                    ModAssetBundlePaths[modEntry.Id] = modEntry.Path;
                    break;
            }

            // add to addendum instead of adding to manifest
            if (addendum != null)
                Log($"\tAdd/Replace: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type}) [{addendum.Name}]");
            else
                Log($"\tAdd/Replace: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");

            // entries in BTRLEntries will be added to game through patch in Patches\BattleTechResourceLocator
            BTRLEntries.Add(modEntry);
        }

        private static bool AddModEntryToDB(MetadataDatabase db, DBCache dbCache, string absolutePath, string typeStr)
        {
            if (Path.GetExtension(absolutePath)?.ToLower() != ".json")
                return false;

            var type = (BattleTechResourceType)Enum.Parse(typeof(BattleTechResourceType), typeStr);
            var relativePath = GetRelativePath(absolutePath, GameDirectory);

            switch (type) // switch is to avoid poisoning the output_log.txt with known types that don't use MDD
            {
                case BattleTechResourceType.TurretDef:
                case BattleTechResourceType.UpgradeDef:
                case BattleTechResourceType.VehicleDef:
                case BattleTechResourceType.ContractOverride:
                case BattleTechResourceType.SimGameEventDef:
                case BattleTechResourceType.LanceDef:
                case BattleTechResourceType.MechDef:
                case BattleTechResourceType.PilotDef:
                case BattleTechResourceType.WeaponDef:
                    var writeTime = File.GetLastWriteTimeUtc(absolutePath);
                    if (!dbCache.Entries.ContainsKey(relativePath) || dbCache.Entries[relativePath] != writeTime)
                    {
                        try
                        {
                            VersionManifestHotReload.InstantiateResourceAndUpdateMDDB(type, absolutePath, db);

                            // don't write game files to the dbCache, since they're assumed to be default in the db
                            if (!absolutePath.Contains(StreamingAssetsDirectory))
                                dbCache.Entries[relativePath] = writeTime;

                            return true;
                        }
                        catch (Exception e)
                        {
                            LogException($"\tAdd to DB failed for {Path.GetFileName(absolutePath)}, exception caught:", e);
                            return false;
                        }
                    }
                    break;
            }

            return false;
        }

        internal static void BuildModManifestEntries()
        {
            CachedVersionManifest = VersionManifestUtilities.LoadDefaultManifest();
            ProgressPanel.SubmitWork(BuildModManifestEntriesLoop);
        }

        internal static IEnumerator<ProgressReport> BuildModManifestEntriesLoop()
        {
            // there are no mods loaded, just return
            if (loadOrder == null || loadOrder.Count == 0)
            {
                Finish();
                yield break;
            }

            Log("");

            // read/create/upgrade all of the caches
            yield return new ProgressReport(1, "Reading Caches", "", true);
            var dbCache = new DBCache(DBCachePath, MDDBPath, ModMDDBPath);
            dbCache.UpdateToRelativePaths();
            var mergeCache = MergeCache.FromFile(MergeCachePath);
            mergeCache.UpdateToRelativePaths();
            var typeCache = new TypeCache(TypeCachePath);
            typeCache.UpdateToIDBased();

            Log("");

            var jsonMerges = new Dictionary<string, List<string>>();
            var manifestMods = loadOrder.Where(name => entriesByMod.ContainsKey(name)).ToList();

            var entryCount = 0;
            var numEntries = 0;
            entriesByMod.Do(entries => numEntries += entries.Value.Count);

            foreach (var modName in manifestMods)
            {
                Log($"{modName}:");
                yield return new ProgressReport(entryCount / ((float)numEntries), $"Loading {modName}", "", true);

                foreach (var modEntry in entriesByMod[modName])
                {
                    yield return new ProgressReport(entryCount++ / ((float)numEntries), $"Loading {modName}", modEntry.Id);

                    // type being null means we have to figure out the type from the path (StreamingAssets)
                    if (modEntry.Type == null)
                    {
                        // TODO: + 16 is a little bizarre looking, it's the length of the substring + 1 because we want to get rid of it and the \
                        var relPath = modEntry.Path.Substring(modEntry.Path.LastIndexOf("StreamingAssets", StringComparison.Ordinal) + 16);
                        var fakeStreamingAssetsPath = Path.GetFullPath(Path.Combine(StreamingAssetsDirectory, relPath));
                        if (!File.Exists(fakeStreamingAssetsPath))
                        {
                            Log($"\tCould not find a file at {fakeStreamingAssetsPath} for {modName} {modEntry.Id}. NOT LOADING THIS FILE");
                            continue;
                        }

                        var types = typeCache.GetTypes(modEntry.Id, CachedVersionManifest);
                        if (types == null)
                        {
                            Log($"\tCould not find an existing VersionManifest entry for {modEntry.Id}. Is this supposed to be a new entry? Don't put new entries in StreamingAssets!");
                            continue;
                        }

                        // this is getting merged later and then added to the BTRL entries then
                        if (Path.GetExtension(modEntry.Path).ToLower() == ".json" && modEntry.ShouldMergeJSON)
                        {
                            if (!jsonMerges.ContainsKey(modEntry.Id))
                                jsonMerges[modEntry.Id] = new List<string>();

                            if (jsonMerges[modEntry.Id].Contains(modEntry.Path)) // TODO: is this necessary?
                                continue;

                            // this assumes that .json can only have a single type
                            // typeCache will always contain this path
                            modEntry.Type = typeCache.GetTypes(modEntry.Id)[0];

                            Log($"\tMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");

                            jsonMerges[modEntry.Id].Add(modEntry.Path);
                            continue;
                        }

                        foreach (var type in types)
                        {
                            var subModEntry = new ModEntry(modEntry, modEntry.Path, modEntry.Id);
                            subModEntry.Type = type;
                            AddModEntry(subModEntry);

                            // clear json merges for this entry, mod is overwriting the original file, previous mods merges are tossed
                            if (jsonMerges.ContainsKey(modEntry.Id))
                            {
                                jsonMerges.Remove(modEntry.Id);
                                Log($"\t\tHad merges for {modEntry.Id} but had to toss, since original file is being replaced");
                            }
                        }

                        continue;
                    }

                    // get "fake" entries that don't actually go into the game's VersionManifest
                    // add videos to be loaded from an external path
                    switch (modEntry.Type)
                    {
                        case "Video":
                            var fileName = Path.GetFileName(modEntry.Path);
                            if (fileName != null && File.Exists(modEntry.Path))
                            {
                                Log($"\tVideo: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\"");
                                ModVideos.Add(fileName, modEntry.Path);
                            }
                            continue;
                        case "AdvancedJSONMerge":
                            var id = JSONMerger.GetTargetID(modEntry.Path);

                            // need to add the types of the file to the typeCache, so that they can be used later
                            // if merging onto a file added by another mod, the type is already in the cache
                            var types = typeCache.GetTypes(id, CachedVersionManifest);

                            if (types == null || types.Count == 0)
                            {
                                Log($"\tERROR: AdvancedJSONMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" has ID that doesn't match anything! Skipping this merge");
                                continue;
                            }

                            if (!jsonMerges.ContainsKey(id))
                                jsonMerges[id] = new List<string>();

                            if (jsonMerges[id].Contains(modEntry.Path)) // TODO: is this necessary?
                                continue;

                            Log($"\tAdvancedJSONMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({types[0]})");
                            jsonMerges[id].Add(modEntry.Path);
                            continue;
                    }

                    // non-StreamingAssets json merges
                    if (Path.GetExtension(modEntry.Path)?.ToLower() == ".json" && modEntry.ShouldMergeJSON)
                    {
                        // have to find the original path for the manifest entry that we're merging onto
                        var matchingEntry = GetEntryByID(modEntry.Id);

                        if (matchingEntry == null)
                        {
                            Log($"\tCould not find an existing VersionManifest entry for {modEntry.Id}!");
                            continue;
                        }

                        if (!jsonMerges.ContainsKey(modEntry.Id))
                            jsonMerges[modEntry.Id] = new List<string>();

                        if (jsonMerges[modEntry.Id].Contains(modEntry.Path)) // TODO: is this necessary?
                            continue;

                        // this assumes that .json can only have a single type
                        modEntry.Type = matchingEntry.Type;
                        typeCache.TryAddType(modEntry.Id, modEntry.Type);

                        Log($"\tMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");
                        jsonMerges[modEntry.Id].Add(modEntry.Path);
                        continue;
                    }

                    AddModEntry(modEntry);
                    typeCache.TryAddType(modEntry.Id, modEntry.Type);

                    // clear json merges for this entry, mod is overwriting the original file, previous mods merges are tossed
                    if (jsonMerges.ContainsKey(modEntry.Id))
                    {
                        jsonMerges.Remove(modEntry.Id);
                        Log($"\t\tHad merges for {modEntry.Id} but had to toss, since original file is being replaced");
                    }
                }
            }

            // perform merges into cache
            Log("");
            LogWithDate("Doing merges...");
            yield return new ProgressReport(1, "Merging", "", true);

            var mergeCount = 0;
            foreach (var id in jsonMerges.Keys)
            {
                var existingEntry = GetEntryByID(id);
                if (existingEntry == null)
                {
                    Log($"\tHave merges for {id} but cannot find an original file! Skipping.");
                    continue;
                }

                var originalPath = Path.GetFullPath(existingEntry.FilePath);
                var mergePaths = jsonMerges[id];

                if (!mergeCache.HasCachedEntry(originalPath, mergePaths))
                    yield return new ProgressReport(mergeCount++ / ((float)jsonMerges.Count), "Merging", id);

                var cachePath = mergeCache.GetOrCreateCachedEntry(originalPath, mergePaths);

                // something went wrong (the parent json prob had errors)
                if (cachePath == null)
                    continue;

                var cacheEntry = new ModEntry(cachePath)
                {
                    ShouldMergeJSON = false,
                    Type = typeCache.GetTypes(id)[0], // this assumes only one type for each json file
                    Id = id
                };

                AddModEntry(cacheEntry);
            }

            Log("");
            Log("Syncing Database");
            yield return new ProgressReport(1, "Syncing Database", "", true);

            // since DB instance is read at type init, before we patch the file location
            // need re-init the mddb to read from the proper modded location
            var mddbTraverse = Traverse.Create(typeof(MetadataDatabase));
            mddbTraverse.Field("instance").SetValue(null);
            mddbTraverse.Method("InitInstance").GetValue();

            // check if files removed from DB cache
            var shouldWriteDB = false;
            var shouldRebuildDB = false;
            var replacementEntries = new List<VersionManifestEntry>();
            var removeEntries = new List<string>();
            foreach (var path in dbCache.Entries.Keys)
            {
                var absolutePath = ResolvePath(path, GameDirectory);

                // check if the file in the db cache is still used
                if (BTRLEntries.Exists(x => x.Path == absolutePath))
                    continue;

                Log($"\tNeed to remove DB entry from file in path: {path}");

                // file is missing, check if another entry exists with same filename in manifest or in BTRL entries
                var fileName = Path.GetFileName(path);
                var existingEntry = BTRLEntries.FindLast(x => Path.GetFileName(x.Path) == fileName)?.GetVersionManifestEntry()
                    ?? CachedVersionManifest.Find(x => Path.GetFileName(x.FilePath) == fileName);

                if (existingEntry == null)
                {
                    Log("\t\tHave to rebuild DB, no existing entry in VersionManifest matches removed entry");
                    shouldRebuildDB = true;
                    break;
                }

                replacementEntries.Add(existingEntry);
                removeEntries.Add(path);
            }

            // add removed entries replacements to db
            if (!shouldRebuildDB)
            {
                // remove old entries
                foreach (var removeEntry in removeEntries)
                    dbCache.Entries.Remove(removeEntry);

                foreach (var replacementEntry in replacementEntries)
                {
                    if (AddModEntryToDB(MetadataDatabase.Instance, dbCache, Path.GetFullPath(replacementEntry.FilePath), replacementEntry.Type))
                    {
                        Log($"\t\tReplaced DB entry with an existing entry in path: {GetRelativePath(replacementEntry.FilePath, GameDirectory)}");
                        shouldWriteDB = true;
                    }
                }
            }

            // if an entry has been removed and we cannot find a replacement, have to rebuild the mod db
            if (shouldRebuildDB)
                dbCache = new DBCache(null, MDDBPath, ModMDDBPath);

            // add needed files to db
            var addCount = 0;
            foreach (var modEntry in BTRLEntries)
            {
                if (modEntry.AddToDB && AddModEntryToDB(MetadataDatabase.Instance, dbCache, modEntry.Path, modEntry.Type))
                {
                    yield return new ProgressReport(addCount / ((float)BTRLEntries.Count), "Populating Database", modEntry.Id);
                    Log($"\tAdded/Updated {modEntry.Id} ({modEntry.Type})");
                    shouldWriteDB = true;
                }
                addCount++;
            }

            mergeCache.ToFile(MergeCachePath);
            typeCache.ToFile(TypeCachePath);
            dbCache.ToFile(DBCachePath);
            Config.ToFile(ConfigPath);

            if (shouldWriteDB)
            {
                yield return new ProgressReport(1, "Writing Database", "", true);
                MetadataDatabase.Instance.WriteInMemoryDBToDisk();
            }

            Finish();
        }
    }
}
