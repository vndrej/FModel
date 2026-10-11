using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views.Resources.Controls;
using ModelContextProtocol;
using Newtonsoft.Json;
using Serilog;
using SkiaSharp;
using Svg.Skia;

namespace FModel.Mcp;

/// <summary>
/// Everything the MCP tools need from FModel, in one place. Tools stay thin; this class
/// knows how to talk to the provider, the worker queue and the WPF dispatcher safely from
/// the ASP.NET Core request threads the MCP server runs on.
/// </summary>
internal static class McpBridge
{
    private static ApplicationViewModel App => ApplicationService.ApplicationView;
    private static CUE4ParseViewModel Cue => App.CUE4Parse;
    private static AbstractVfsFileProvider Provider => Cue.Provider;
    private static ThreadWorkerViewModel Worker => ApplicationService.ThreadWorkerView;
    private static Dispatcher Ui => Application.Current.Dispatcher;

    /// <summary>Serializes every operation that goes through FModel's single worker queue.</summary>
    private static readonly SemaphoreSlim WorkerLock = new(1, 1);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    public static string ToJson(object value) => JsonConvert.SerializeObject(value, JsonSettings);

    /// <summary>Writes a line to FModel's log panel and log file so the user can follow what the AI is doing.</summary>
    public static void Trace(string tool, string detail = null)
    {
        Log.Information("MCP {Tool} {Detail}", tool, detail ?? string.Empty);
        FLogger.Append(ELog.None, () =>
        {
            FLogger.Text("[MCP] ", Constants.BLUE);
            FLogger.Text(tool, Constants.WHITE);
            if (!string.IsNullOrEmpty(detail)) FLogger.Text($" {detail}", Constants.GRAY);
            FLogger.Text(string.Empty, Constants.WHITE, true);
        });
    }

    #region Guards

    public static void EnsureProvider()
    {
        if (App?.CUE4Parse?.Provider is null)
            throw new McpException("FModel has not finished initializing its file provider yet. Retry in a few seconds.");
    }

    public static void EnsureFilesLoaded()
    {
        EnsureProvider();
        if (Provider.Files.Count == 0)
        {
            if (Provider.RequiredKeys.Count > 0 && Provider.Keys.Count == 0)
                throw new McpException("The archives are encrypted and no AES key has been accepted. Use set_aes_key, or configure the AES endpoint in FModel's settings.");
            throw new McpException("No files are mounted. FModel may still be mounting archives (check status), or the game directory contains no supported archives.");
        }
    }

    public static GameFile ResolveFile(string path)
    {
        EnsureFilesLoaded();
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException("'path' is required.");

        var p = path.Trim().Replace('\\', '/').TrimStart('/');
        if (Provider.TryGetGameFile(p, out var file))
            return file;

        // Not found at that path: look for files with the same name anywhere (plugins move content around a lot).
        var leaf = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
        var stem = leaf.Contains('.') ? leaf[..leaf.LastIndexOf('.')] : leaf;
        var candidates = new List<GameFile>();
        if (stem.Length > 0)
        {
            foreach (var f in Provider.Files.Values)
            {
                // Compare on the path span: GameFile.Name/NameWithoutExtension allocate and intern, far too slow for millions of files.
                var span = f.Path.AsSpan();
                var leafSpan = span[(span.LastIndexOf('/') + 1)..];
                var dot = leafSpan.LastIndexOf('.');
                var stemSpan = dot < 0 ? leafSpan : leafSpan[..dot];
                if (!stemSpan.Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.IsUePackagePayload || !IsCanonical(f)) continue;
                candidates.Add(f);
                if (candidates.Count >= 6) break;
            }
        }

        if (candidates.Count == 1)
            return candidates[0];

        var suggestion = candidates.Count > 0
            ? $" Files with that name: {string.Join(", ", candidates.Select(c => c.Path))}."
            : string.Empty;
        throw new McpException($"No file found for '{path}'.{suggestion} Paths look like 'ProjectName/Content/Folder/Asset.uasset' (also accepted: '/Game/Folder/Asset'). Use search_files or list_directory to find the exact path.");
    }

    /// <summary>
    /// The provider enumerates one entry per archive, so a patched file shows up several times.
    /// The indexer returns the highest-priority copy; that is the one we surface.
    /// </summary>
    private static bool IsCanonical(GameFile file)
        => file is not VfsEntry || !Provider.Files.TryGetValue(file.Path, out var canonical) || ReferenceEquals(canonical, file);

    #endregion

    #region Worker queue

    /// <summary>
    /// Runs <paramref name="action"/> through FModel's worker queue (same path as a double-click in the UI),
    /// waiting for any user-started operation to finish first. Exceptions thrown by the action are surfaced to the MCP client.
    /// </summary>
    public static async Task RunOnWorkerAsync(Action<CancellationToken> action, CancellationToken ct)
    {
        await WorkerLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await WaitUntilReadyAsync(ct);

                Exception captured = null;
                var ran = false;
                var task = await Ui.InvokeAsync(() => Worker.Begin(token =>
                {
                    ran = true;
                    try
                    {
                        action(token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        captured = e;
                        throw;
                    }
                }));
                await task;

                if (!ran) continue; // the UI grabbed the worker between our readiness check and Begin, try again

                if (captured != null)
                    throw new McpException($"{captured.GetType().Name}: {captured.Message}");
                if (App.Status.Kind == EStatusKind.Stopped)
                    throw new McpException("The operation was cancelled in FModel.");
                return;
            }

            throw new McpException("FModel is continuously busy with user operations; try again later.");
        }
        finally
        {
            WorkerLock.Release();
        }
    }

    public static async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (!App.Status.IsReady)
        {
            if (DateTime.UtcNow - started > ReadyTimeout)
                throw new McpException($"FModel is busy ({App.Status.Label}). Wait for it to finish or call cancel.");
            await Task.Delay(100, ct);
        }
    }

    public static Task CancelAsync() => Ui.InvokeAsync(() =>
    {
        if (Worker.CanBeCanceled)
        {
            App.Status.SetStatus(EStatusKind.Stopping);
            Worker.Cancel();
        }

        ExportSessionViewModel.Instance.CancelExport();
    }).Task;

    #endregion

    #region Status / archives / settings

    public static object GetStatus()
    {
        EnsureProvider();
        var session = ExportSessionViewModel.Instance;
        var mappings = Provider.MappingsContainer switch
        {
            FileUsmapTypeMappingsProvider usmap => usmap.FileName,
            null => null,
            var other => other.GetType().Name
        };

        return new
        {
            game = Provider.GameDisplayName ?? Provider.ProjectName,
            projectName = Provider.ProjectName,
            ueVersion = Provider.Versions.Game.ToString(),
            texturePlatform = Provider.Versions.Platform.ToString(),
            gameDirectory = UserSettings.Default.GameDirectory,
            status = App.Status.Kind.ToString(),
            statusLabel = App.Status.Label,
            isReady = App.Status.IsReady,
            archives = new { mounted = Provider.MountedVfs.Count, unloaded = Provider.UnloadedVfs.Count },
            aesKeys = new { provided = Provider.Keys.Count, required = Provider.RequiredKeys.Count },
            mountedFiles = Provider.Files.Count,
            packagesLoadedInExplorer = Cue.SearchVm.SearchResults.Count,
            localizedResources = Cue.LocalizedResourcesCount,
            assetLanguage = UserSettings.Default.AssetLanguage.ToString(),
            mappings,
            exportSession = new { queued = session.Session.TotalQueued, isRunning = session.IsRunning },
            outputDirectories = GetOutputDirectories(),
            mcpEndpoint = McpServerHost.Endpoint,
            hints = Provider.Files.Count > 0 && Cue.SearchVm.SearchResults.Count == 0
                ? "Files are mounted but not loaded into the explorer yet; call load_packages once so localized text and virtual paths resolve."
                : null
        };
    }

    public static object ListArchives()
    {
        EnsureProvider();
        static ArchiveInfo Describe(IAesVfsReader reader, bool mounted) => new(
            reader.Name,
            mounted,
            reader.IsEncrypted,
            reader.EncryptionKeyGuid.ToString(),
            reader.AesKey != null,
            mounted ? reader.FileCount : null,
            reader.Length,
            mounted ? reader.MountPoint : null,
            reader.Ver.ToString());

        return new
        {
            mounted = Provider.MountedVfs.Select(x => Describe(x, true)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            unloaded = Provider.UnloadedVfs.Select(x => Describe(x, false)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public static object GetOutputDirectories() => new
    {
        output = UserSettings.Default.OutputDirectory,
        rawData = UserSettings.Default.RawDataDirectory,
        properties = UserSettings.Default.PropertiesDirectory,
        textures = UserSettings.Default.TextureDirectory,
        audio = UserSettings.Default.AudioDirectory,
        code = UserSettings.Default.CodeDirectory,
        models = UserSettings.Default.ModelDirectory
    };

    public static object GetSettings() => new
    {
        directories = GetOutputDirectories(),
        keepDirectoryStructure = UserSettings.Default.KeepDirectoryStructure,
        meshFormat = UserSettings.Default.MeshExportFormat.ToString(),
        naniteMeshFormat = UserSettings.Default.NaniteMeshExportFormat.ToString(),
        meshQuality = UserSettings.Default.MeshQuality.ToString(),
        socketFormat = UserSettings.Default.SocketExportFormat.ToString(),
        compressionFormat = UserSettings.Default.CompressionFormat.ToString(),
        materialFormat = UserSettings.Default.MaterialExportFormat.ToString(),
        saveEmbeddedMaterials = UserSettings.Default.SaveEmbeddedMaterials,
        saveMorphTargets = UserSettings.Default.SaveMorphTargets,
        saveSkeletonAsMesh = UserSettings.Default.SaveSkeletonAsMesh,
        textureFormat = UserSettings.Default.TextureExportFormat.ToString(),
        textureQuality = UserSettings.Default.TextureQuality,
        saveHdrTexturesAsHdr = UserSettings.Default.SaveHdrTexturesAsHdr,
        exportAllTextureMips = UserSettings.Default.ExportAllTextureMips,
        convertAudioToWav = UserSettings.Default.ConvertAudioOnBulkExport,
        assetLanguage = UserSettings.Default.AssetLanguage.ToString(),
        allowedValues = new
        {
            meshFormat = Enum.GetNames<CUE4Parse_Conversion.Options.EMeshFormat>(),
            naniteMeshFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ENaniteMeshFormat>(),
            meshQuality = Enum.GetNames<CUE4Parse_Conversion.Options.EMeshQuality>(),
            socketFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ESocketFormat>(),
            compressionFormat = Enum.GetNames<CUE4Parse_Conversion.Writers.UEFormat.Enums.EFileCompressionFormat>(),
            materialFormat = Enum.GetNames<CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth>(),
            textureFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ETextureFormat>()
        }
    };

    public static object UpdateSettings(McpSettingsUpdate update)
    {
        var s = UserSettings.Default;
        var changed = new List<string>();

        void SetDir(string value, Action<string> apply, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var full = Path.GetFullPath(value);
            Directory.CreateDirectory(full);
            apply(full);
            changed.Add(name);
        }

        void SetEnum<T>(string value, Action<T> apply, string name) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Enum.TryParse<T>(value, true, out var parsed))
                throw new McpException($"Invalid {name} '{value}'. Allowed: {string.Join(", ", Enum.GetNames<T>())}");
            apply(parsed);
            changed.Add(name);
        }

        void SetBool(bool? value, Action<bool> apply, string name)
        {
            if (value is null) return;
            apply(value.Value);
            changed.Add(name);
        }

        Ui.Invoke(() =>
        {
            SetDir(update.RawDataDirectory, v => s.RawDataDirectory = v, "rawDataDirectory");
            SetDir(update.PropertiesDirectory, v => s.PropertiesDirectory = v, "propertiesDirectory");
            SetDir(update.TextureDirectory, v => s.TextureDirectory = v, "textureDirectory");
            SetDir(update.AudioDirectory, v => s.AudioDirectory = v, "audioDirectory");
            SetDir(update.CodeDirectory, v => s.CodeDirectory = v, "codeDirectory");
            SetDir(update.ModelDirectory, v => s.ModelDirectory = v, "modelDirectory");
            if (!string.IsNullOrWhiteSpace(update.AllExportsDirectory))
            {
                var full = Path.GetFullPath(update.AllExportsDirectory);
                Directory.CreateDirectory(full);
                s.RawDataDirectory = s.PropertiesDirectory = s.TextureDirectory = s.AudioDirectory = s.CodeDirectory = s.ModelDirectory = full;
                changed.Add("allExportsDirectory");
            }

            SetBool(update.KeepDirectoryStructure, v => s.KeepDirectoryStructure = v, "keepDirectoryStructure");
            SetEnum<CUE4Parse_Conversion.Options.EMeshFormat>(update.MeshFormat, v => s.MeshExportFormat = v, "meshFormat");
            SetEnum<CUE4Parse_Conversion.Options.ENaniteMeshFormat>(update.NaniteMeshFormat, v => s.NaniteMeshExportFormat = v, "naniteMeshFormat");
            SetEnum<CUE4Parse_Conversion.Options.EMeshQuality>(update.MeshQuality, v => s.MeshQuality = v, "meshQuality");
            SetEnum<CUE4Parse_Conversion.Options.ESocketFormat>(update.SocketFormat, v => s.SocketExportFormat = v, "socketFormat");
            SetEnum<CUE4Parse_Conversion.Writers.UEFormat.Enums.EFileCompressionFormat>(update.CompressionFormat, v => s.CompressionFormat = v, "compressionFormat");
            SetEnum<CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth>(update.MaterialFormat, v => s.MaterialExportFormat = v, "materialFormat");
            SetBool(update.SaveEmbeddedMaterials, v => s.SaveEmbeddedMaterials = v, "saveEmbeddedMaterials");
            SetBool(update.SaveMorphTargets, v => s.SaveMorphTargets = v, "saveMorphTargets");
            SetBool(update.SaveSkeletonAsMesh, v => s.SaveSkeletonAsMesh = v, "saveSkeletonAsMesh");
            SetEnum<CUE4Parse_Conversion.Options.ETextureFormat>(update.TextureFormat, v => s.TextureExportFormat = v, "textureFormat");
            if (update.TextureQuality is { } q)
            {
                s.TextureQuality = Math.Clamp(q, 1, 100);
                changed.Add("textureQuality");
            }
            SetBool(update.SaveHdrTexturesAsHdr, v => s.SaveHdrTexturesAsHdr = v, "saveHdrTexturesAsHdr");
            SetBool(update.ExportAllTextureMips, v => s.ExportAllTextureMips = v, "exportAllTextureMips");
            SetBool(update.ConvertAudioToWav, v => s.ConvertAudioOnBulkExport = v, "convertAudioToWav");

            if (changed.Count > 0) UserSettings.Save();
        });

        return new { changed, settings = GetSettings() };
    }

    public static async Task<object> SetAesKeyAsync(string key, string archiveNameOrGuid, CancellationToken ct)
    {
        EnsureProvider();
        var fixedKey = Helper.FixKey(key);
        if (fixedKey.Length != 66)
            throw new McpException("The key must be a 32-byte AES key written as 64 hex characters (optionally prefixed with 0x).");

        var keys = App.AesManager.AesKeys;
        if (keys is null)
            throw new McpException("The AES manager is not initialized yet; retry in a few seconds.");

        string target = null;
        await Ui.InvokeAsync(() =>
        {
            FileItem item;
            if (string.IsNullOrWhiteSpace(archiveNameOrGuid))
            {
                item = keys[0]; // main static key
            }
            else
            {
                item = keys.FirstOrDefault(x =>
                           x.Name.Equals(archiveNameOrGuid, StringComparison.OrdinalIgnoreCase) ||
                           x.Guid.ToString().Equals(archiveNameOrGuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ||
                           x.Guid.ToString().Equals(archiveNameOrGuid, StringComparison.OrdinalIgnoreCase))
                       ?? throw new McpException($"No archive matches '{archiveNameOrGuid}'. Use list_archives to see names and encryption key GUIDs.");
            }

            item.Key = fixedKey;
            target = item.Name;
        });

        await WorkerLock.WaitAsync(ct);
        try
        {
            await WaitUntilReadyAsync(ct);
            var task = await Ui.InvokeAsync(() => App.UpdateProvider(false));
            await task;
        }
        finally
        {
            WorkerLock.Release();
        }

        return new
        {
            appliedTo = target,
            archives = new { mounted = Provider.MountedVfs.Count, unloaded = Provider.UnloadedVfs.Count },
            aesKeys = new { provided = Provider.Keys.Count, required = Provider.RequiredKeys.Count },
            mountedFiles = Provider.Files.Count
        };
    }

    #endregion

    #region Loading

    public static async Task<object> LoadPackagesAsync(CancellationToken ct)
    {
        EnsureFilesLoaded();

        await WorkerLock.WaitAsync(ct);
        try
        {
            await WaitUntilReadyAsync(ct);
            await Ui.InvokeAsync(() =>
            {
                UserSettings.Default.LoadingMode = ELoadingMode.All;
                App.LoadingModes.LoadCommand.Execute(null);
            });

            // LoadCommand is fire-and-forget; it flips the status to Loading synchronously, so poll until it is done.
            await Task.Delay(250, ct);
            await WaitUntilReadyAsync(ct);
            await Task.Delay(250, ct);
        }
        finally
        {
            WorkerLock.Release();
        }

        return new
        {
            packagesLoadedInExplorer = Cue.SearchVm.SearchResults.Count,
            localizedResources = Cue.LocalizedResourcesCount,
            status = App.Status.Kind.ToString()
        };
    }

    #endregion

    #region Browsing

    private sealed class DirNode
    {
        public readonly SortedSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<GameFile> Files = [];
    }

    private static Dictionary<string, DirNode> _dirIndex;
    private static int _dirIndexFileCount = -1;
    private static readonly object DirIndexLock = new();

    private static Dictionary<string, DirNode> GetDirIndex()
    {
        lock (DirIndexLock)
        {
            if (_dirIndex != null && _dirIndexFileCount == Provider.Files.Count)
                return _dirIndex;

            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
            DirNode Node(string dir)
            {
                if (!nodes.TryGetValue(dir, out var node)) nodes[dir] = node = new DirNode();
                return node;
            }

            foreach (var file in Provider.Files.Values)
            {
                if (file.IsUePackagePayload || !IsCanonical(file)) continue;
                var dir = file.Path.Contains('/') ? file.Directory : string.Empty;
                Node(dir).Files.Add(file);

                var current = dir;
                while (current.Length > 0)
                {
                    var slash = current.LastIndexOf('/');
                    var parent = slash < 0 ? string.Empty : current[..slash];
                    var name = slash < 0 ? current : current[(slash + 1)..];
                    if (!Node(parent).Folders.Add(name)) break; // ancestors already registered
                    current = parent;
                }
            }

            _dirIndex = nodes;
            _dirIndexFileCount = Provider.Files.Count;
            return nodes;
        }
    }

    public static object ListDirectory(string path, int limit)
    {
        EnsureFilesLoaded();
        var dir = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (dir.StartsWith("Game/", StringComparison.OrdinalIgnoreCase) || dir.Equals("Game", StringComparison.OrdinalIgnoreCase))
            dir = Provider.ProjectName + "/Content" + dir[4..];

        var index = GetDirIndex();
        if (!index.TryGetValue(dir, out var node))
        {
            var parentDir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : string.Empty;
            var siblings = index.TryGetValue(parentDir, out var parent) ? parent.Folders.Take(30) : [];
            throw new McpException($"Directory '{dir}' does not exist. Folders under '{parentDir}': {string.Join(", ", siblings)}");
        }

        limit = Math.Clamp(limit <= 0 ? 500 : limit, 1, 5000);
        var files = node.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new
        {
            path = dir,
            folderCount = node.Folders.Count,
            fileCount = files.Count,
            folders = node.Folders.Take(limit).ToList(),
            files = files.Take(limit).Select(f => new { name = f.Name, size = f.Size, archive = (f as VfsEntry)?.Vfs.Name }).ToList(),
            truncated = node.Folders.Count > limit || files.Count > limit
        };
    }

    public static object SearchFiles(string query, bool regex, bool caseSensitive, string extension, string directory, int limit, int offset)
    {
        EnsureFilesLoaded();
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(extension) && string.IsNullOrWhiteSpace(directory))
            throw new McpException("Provide at least one of query, extension or directory.");

        limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 2000);
        offset = Math.Max(0, offset);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var dirPrefix = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim().Replace('\\', '/').Trim('/') + "/";
        if (dirPrefix != null && dirPrefix.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            dirPrefix = Provider.ProjectName + "/Content/" + dirPrefix[5..];
        var ext = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim().TrimStart('.');

        Func<string, bool> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            matches = _ => true;
        }
        else if (regex)
        {
            Regex re;
            try
            {
                re = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException e)
            {
                throw new McpException($"Invalid regex: {e.Message}");
            }

            matches = p => re.IsMatch(p);
        }
        else
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            matches = p => terms.All(t => p.Contains(t, comparison));
        }

        var total = 0;
        var results = new List<object>(limit);
        foreach (var file in Provider.Files.Values)
        {
            if (file.IsUePackagePayload) continue;
            if (ext != null && !file.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (dirPrefix != null && !file.Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!matches(file.Path)) continue;
            if (!IsCanonical(file)) continue; // cheap lookup, only paid for matches

            if (total >= offset && results.Count < limit)
                results.Add(new { path = file.Path, size = file.Size, archive = (file as VfsEntry)?.Vfs.Name });
            total++;
        }

        return new { total, offset, returned = results.Count, results };
    }

    public static object GetFileInfo(GameFile file, int maxExports)
    {
        maxExports = Math.Clamp(maxExports <= 0 ? 200 : maxExports, 1, 5000);
        List<ExportInfo> exports = null;
        string mainExportClass = null;
        int? exportCount = null;
        int? importCount = null;

        if (file.IsUePackage)
        {
            var pkg = Provider.LoadPackage(file);
            exportCount = pkg.ExportMapLength;
            importCount = pkg.ImportMapLength;
            exports = new List<ExportInfo>(Math.Min(pkg.ExportMapLength, maxExports));
            for (var i = 0; i < pkg.ExportMapLength && i < maxExports; i++)
            {
                var resolved = new FPackageIndex(pkg, i + 1).ResolvedObject;
                var name = resolved?.Name.Text;
                var cls = resolved?.Class?.Name.Text;
                exports.Add(new ExportInfo(i, name, cls));
                if (mainExportClass is null && name != null && name.Equals(file.NameWithoutExtension, StringComparison.OrdinalIgnoreCase))
                    mainExportClass = cls;
            }

            mainExportClass ??= exports.FirstOrDefault()?.Class;
        }

        return new
        {
            path = file.Path,
            name = file.Name,
            extension = file.Extension,
            size = file.Size,
            isUePackage = file.IsUePackage,
            isEncrypted = file.IsEncrypted,
            archive = (file as VfsEntry)?.Vfs.Name,
            mainExportClass,
            exportCount,
            importCount,
            exports,
            exportsTruncated = exportCount > maxExports
        };
    }

    #endregion

    #region Reading

    public static (string json, string note) PackageToJson(GameFile file, string exportName, int? exportIndex)
    {
        if (!file.IsUePackage)
            throw new McpException($"'{file.Path}' is not a UE package (.uasset/.umap). Use read_text for text-like files or get_texture for images.");

        var pkg = Provider.LoadPackage(file);
        object data;
        string note;

        if (!string.IsNullOrWhiteSpace(exportName))
        {
            var idx = pkg.GetExportIndex(exportName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new McpException($"Export '{exportName}' not found in '{file.Name}'. Call get_file_info to list the exports.");
            data = pkg.GetExport(idx);
            note = $"export {idx} '{exportName}' of {pkg.ExportMapLength}";
        }
        else if (exportIndex is { } i)
        {
            if (i < 0 || i >= pkg.ExportMapLength)
                throw new McpException($"exportIndex {i} is out of range; the package has {pkg.ExportMapLength} exports.");
            data = pkg.GetExport(i);
            note = $"export {i} of {pkg.ExportMapLength}";
        }
        else if (pkg.ExportMapLength >= 5000)
        {
            var main = pkg.GetExportIndex(file.NameWithoutExtension, StringComparison.OrdinalIgnoreCase);
            if (main < 0) main = 0;
            data = pkg.GetExport(main);
            note = $"the package has {pkg.ExportMapLength} exports, only export {main} is returned; pass exportIndex or exportName to read others";
        }
        else
        {
            data = pkg.GetExports();
            note = $"all {pkg.ExportMapLength} exports";
        }

        return (JsonConvert.SerializeObject(data, Formatting.Indented), note);
    }

    public static string SaveJsonToPropertiesDirectory(GameFile file, string json)
    {
        var fileName = Path.ChangeExtension(file.Name, ".json");
        var path = Path.Combine(UserSettings.Default.PropertiesDirectory,
            UserSettings.Default.KeepDirectoryStructure ? file.Directory : string.Empty, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    public static string ReadText(GameFile file)
    {
        var ext = file.Extension.ToLowerInvariant();
        switch (ext)
        {
            case "uasset":
            case "umap":
                throw new McpException("This is a UE package; use read_package_json instead.");
            case "locres":
                return JsonConvert.SerializeObject(new FTextLocalizationResource(file.CreateReader()), Formatting.Indented);
            case "locmeta":
                return JsonConvert.SerializeObject(new FTextLocalizationMetaDataResource(file.CreateReader()), Formatting.Indented);
            case "ini" when file.Name.Contains("BinaryConfig", StringComparison.OrdinalIgnoreCase):
                return JsonConvert.SerializeObject(new FConfigCacheIni(file.CreateReader()), Formatting.Indented);
            case "bin" when file.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
                return JsonConvert.SerializeObject(new FAssetRegistryState(file.CreateReader()), Formatting.Indented);
            case "json":
            {
                var raw = Encoding.UTF8.GetString(file.Read());
                try
                {
                    return JsonConvert.SerializeObject(JsonConvert.DeserializeObject(raw), Formatting.Indented);
                }
                catch
                {
                    return raw;
                }
            }
            default:
            {
                var data = file.Read();
                var probe = data.AsSpan(0, Math.Min(data.Length, 8192));
                if (probe.IndexOf((byte) 0) >= 0)
                    throw new McpException($"'{file.Name}' looks binary. Use export_assets with kind 'raw' to save it to disk, get_texture if it is an image, or open_in_ui to let FModel parse it.");
                return Encoding.UTF8.GetString(data);
            }
        }
    }

    public static string Truncate(string text, int maxChars, out bool truncated)
    {
        maxChars = Math.Clamp(maxChars <= 0 ? 200_000 : maxChars, 1_000, 5_000_000);
        truncated = text.Length > maxChars;
        return truncated ? text[..maxChars] : text;
    }

    public static (byte[] data, string mimeType, object info) GetTexture(GameFile file, string exportName, int maxSize)
    {
        maxSize = Math.Clamp(maxSize <= 0 ? 1024 : maxSize, 16, 4096);
        var ext = file.Extension.ToLowerInvariant();
        switch (ext)
        {
            case "png":
            case "jpg":
            case "jpeg":
            case "bmp":
            {
                var bytes = file.Read();
                using var bitmap = SKBitmap.Decode(bytes) ?? throw new McpException("Could not decode image.");
                var mime = ext is "jpg" or "jpeg" ? "image/jpeg" : "image/png";
                if (ext == "bmp" || bitmap.Width > maxSize || bitmap.Height > maxSize)
                {
                    using var resized = Downscale(bitmap, maxSize);
                    using var encoded = resized.Encode(SKEncodedImageFormat.Png, 100);
                    return (encoded.ToArray(), "image/png", new { file.Name, width = bitmap.Width, height = bitmap.Height, returnedWidth = resized.Width, returnedHeight = resized.Height });
                }

                return (bytes, mime, new { file.Name, width = bitmap.Width, height = bitmap.Height });
            }
            case "svg":
            {
                using var stream = new MemoryStream(file.Read());
                var svg = new SKSvg();
                svg.Load(stream);
                if (svg.Picture == null) throw new McpException("Could not parse SVG.");

                var size = Math.Min(maxSize, 1024);
                using var bitmap = new SKBitmap(size, size);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    var bounds = svg.Picture.CullRect;
                    var scale = Math.Min(size / bounds.Width, size / bounds.Height);
                    canvas.Scale(scale);
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    canvas.DrawPicture(svg.Picture);
                }

                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                return (png.ToArray(), "image/png", new { file.Name, rasterizedAt = size });
            }
        }

        if (!file.IsUePackage)
            throw new McpException($"'{file.Name}' is neither an image file nor a UE package.");

        var pkg = Provider.LoadPackage(file);
        UTexture texture = null;
        if (!string.IsNullOrWhiteSpace(exportName))
        {
            texture = pkg.GetExportOrNull(exportName, StringComparison.OrdinalIgnoreCase) as UTexture
                      ?? throw new McpException($"Export '{exportName}' is not a texture (or does not exist).");
        }
        else
        {
            foreach (var lazy in pkg.ExportsLazy)
            {
                if (lazy.Value is UTexture t)
                {
                    texture = t;
                    break;
                }
            }

            if (texture is null)
                throw new McpException($"No UTexture export found in '{file.Name}'. Use get_file_info to inspect its exports; textures referenced by this asset must be fetched by their own path.");
        }

        var platform = UserSettings.Default.CurrentDir.TexturePlatform;
        var decoded = texture is UTextureCube
            ? texture.Decode(platform)?.ToPanorama()
            : texture.Decode(maxSize, platform);
        if (decoded is null)
            throw new McpException($"Texture '{texture.Name}' ({texture.Format}) could not be decoded.");

        using var skBitmap = decoded.ToSkBitmap();
        SKBitmap toEncode = skBitmap;
        SKBitmap scaled = null;
        if (skBitmap.Width > maxSize || skBitmap.Height > maxSize)
            toEncode = scaled = Downscale(skBitmap, maxSize);

        try
        {
            using var png = toEncode.Encode(SKEncodedImageFormat.Png, 100);
            var mip = texture.GetFirstMip();
            return (png.ToArray(), "image/png", new
            {
                export = texture.Name,
                @class = texture.ExportType,
                pixelFormat = texture.Format.ToString(),
                fullWidth = mip?.SizeX,
                fullHeight = mip?.SizeY,
                returnedWidth = toEncode.Width,
                returnedHeight = toEncode.Height,
                isCubemapPanorama = texture is UTextureCube
            });
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static SKBitmap Downscale(SKBitmap source, int maxSize)
    {
        var scale = Math.Min((float) maxSize / source.Width, (float) maxSize / source.Height);
        if (scale >= 1f) return source.Copy();
        var w = Math.Max(1, (int) (source.Width * scale));
        var h = Math.Max(1, (int) (source.Height * scale));
        return source.Resize(new SKImageInfo(w, h, source.ColorType, source.AlphaType), SKFilterQuality.Medium) ?? source.Copy();
    }

    private sealed record ArchiveInfo(string Name, bool Mounted, bool Encrypted, string EncryptionKeyGuid, bool HasKey, int? FileCount, long SizeBytes, string MountPoint, string Version);
    private sealed record ExportInfo(int Index, string Name, string Class);

    public static async Task<string> DecompileAsync(GameFile file, CancellationToken ct)
    {
        if (!file.IsUePackage)
            throw new McpException("Only UE packages containing a UClass/Blueprint can be decompiled.");

        string text = null;
        await RunOnWorkerAsync(_ =>
        {
            // The decompiler needs the script bytecode, which FModel only deserializes when the
            // "Serialize Script Bytecode" setting is on. Enable it for this read if the user has it off.
            var previous = Provider.ReadScriptData;
            Provider.ReadScriptData = true;
            try
            {
                text = Cue.DecompileToText(file);
            }
            finally
            {
                Provider.ReadScriptData = previous;
            }
        }, ct);
        return text ?? throw new McpException($"'{file.Name}' contains no blueprint class to decompile.");
    }

    public static async Task<object> FindReferencesAsync(GameFile file, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 500 : limit, 1, 5000);
        List<GameFile> refs = null;
        await RunOnWorkerAsync(_ => refs = Provider.ScanForPackageRefs(file), ct);
        refs ??= [];
        return new
        {
            target = file.Path,
            total = refs.Count,
            references = refs.Take(limit).Select(r => r.Path).ToList(),
            truncated = refs.Count > limit
        };
    }

    public static Task OpenInUiAsync(IReadOnlyList<GameFile> files, CancellationToken ct)
        => RunOnWorkerAsync(token => Cue.ExtractSelected(token, files), ct);

    #endregion

    #region Exporting

    public static EBulkType ParseExportKind(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "raw" or "data" => EBulkType.Raw,
        "properties" or "json" => EBulkType.Properties,
        "textures" or "texture" => EBulkType.Textures,
        "models" or "meshes" or "mesh" or "model" => EBulkType.Meshes,
        "animations" or "animation" or "anims" => EBulkType.Animations,
        "worlds" or "world" or "maps" or "map" => EBulkType.Worlds,
        "audio" or "sounds" or "sound" => EBulkType.Audio,
        "code" or "decompiled" => EBulkType.Code,
        _ => throw new McpException($"Unknown export kind '{kind}'. Use one of: raw, properties, textures, models, animations, worlds, audio, code.")
    };

    private static string OutputDirectoryFor(EBulkType bulk) => bulk switch
    {
        EBulkType.Raw => UserSettings.Default.RawDataDirectory,
        EBulkType.Properties => UserSettings.Default.PropertiesDirectory,
        EBulkType.Textures => UserSettings.Default.TextureDirectory,
        EBulkType.Audio => UserSettings.Default.AudioDirectory,
        EBulkType.Code => UserSettings.Default.CodeDirectory,
        _ => UserSettings.Default.ModelDirectory
    };

    public static IReadOnlyList<GameFile> FilesUnderFolder(string folderPath, bool recursive, string extension)
    {
        EnsureFilesLoaded();
        var dir = (folderPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (dir.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            dir = Provider.ProjectName + "/Content" + dir[4..];
        if (dir.Length == 0)
            throw new McpException("Refusing to export the whole archive root; pass a folder such as 'ProjectName/Content/Characters'.");

        var prefix = dir + "/";
        var ext = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim().TrimStart('.');
        var files = Provider.Files.Values
            .Where(f => !f.IsUePackagePayload && f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(f => recursive || f.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase))
            .Where(f => ext is null || f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
            .Where(IsCanonical)
            .ToList();

        if (files.Count == 0)
            throw new McpException($"No files found under '{dir}'" + (ext != null ? $" with extension '{ext}'." : "."));
        return files;
    }

    public static async Task<object> ExportAsync(IReadOnlyList<GameFile> files, EBulkType bulk, CancellationToken ct)
    {
        EnsureFilesLoaded();
        var session = ExportSessionViewModel.Instance;
        var started = DateTime.UtcNow.AddSeconds(-2);

        Interlocked.Exchange(ref Cue.ExportedCount, 0);
        Interlocked.Exchange(ref Cue.FailedExportCount, 0);

        // Let a user-started export finish before we touch the shared session queue.
        while (session.IsRunning)
            await Task.Delay(200, ct);

        var queuedBefore = session.Session.TotalQueued;
        await RunOnWorkerAsync(token =>
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (bulk == EBulkType.Raw) Cue.ExportData(file);
                    else Cue.Extract(token, file, Cue.TabControl.HasNoTabs || Cue.TabControl.SelectedTab is null, bulk | EBulkType.Auto);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref Cue.FailedExportCount);
                    Log.Warning(e, "MCP export of {Path} failed", file.Path);
                }
            }
        }, ct);

        IReadOnlyList<ExportResult> results = null;
        var queued = session.Session.TotalQueued - queuedBefore;
        if (session.Session.TotalQueued > 0)
        {
            var task = await Ui.InvokeAsync(() => session.ExportAsync());
            results = await task;
        }

        var written = new List<string>();
        var failures = new List<object>();
        if (results != null)
        {
            foreach (var r in results)
            {
                if (r.Success) written.AddRange(r.DiskFilePaths ?? []);
                else failures.Add(new { r.ObjectPath, error = r.Error?.GetBaseException().Message });
            }
        }

        // Properties, audio, code and creator images are written directly rather than via the export session.
        if (bulk is EBulkType.Properties or EBulkType.Audio or EBulkType.Code or EBulkType.Textures)
            written.AddRange(ScanNewFiles(OutputDirectoryFor(bulk), files, started).Where(p => !written.Contains(p, StringComparer.OrdinalIgnoreCase)));

        var directWrites = Cue.ExportedCount;
        var directFailures = Cue.FailedExportCount;
        written = written
            .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }) // session and FModel mix '/' and '\'
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        const int maxListed = 1000;
        return new
        {
            kind = bulk.ToString(),
            requestedFiles = files.Count,
            outputDirectory = OutputDirectoryFor(bulk),
            queuedForConversion = Math.Max(0, queued),
            converted = results?.Count(r => r.Success) ?? 0,
            directlySaved = directWrites,
            failed = failures.Count + directFailures,
            writtenFiles = written.Take(maxListed).ToList(),
            writtenFilesTruncated = written.Count > maxListed,
            writtenFileCount = written.Count,
            failures = failures.Take(50).ToList(),
            note = written.Count == 0 && failures.Count == 0 && directFailures == 0
                ? "Nothing was written: the selected files contain no asset of that kind (e.g. no mesh in a texture package). Check get_file_info for export classes."
                : null
        };
    }

    private static IEnumerable<string> ScanNewFiles(string baseDirectory, IReadOnlyList<GameFile> files, DateTime since)
    {
        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
            yield break;

        IEnumerable<string> roots;
        SearchOption option;
        if (UserSettings.Default.KeepDirectoryStructure)
        {
            roots = files.Select(f => Path.Combine(baseDirectory, f.Directory)).Distinct(StringComparer.OrdinalIgnoreCase);
            option = SearchOption.AllDirectories;
        }
        else
        {
            roots = [baseDirectory];
            option = SearchOption.TopDirectoryOnly;
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(root, "*", option);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                DateTime stamp;
                try
                {
                    stamp = File.GetLastWriteTimeUtc(candidate);
                }
                catch
                {
                    continue;
                }

                if (stamp >= since) yield return candidate;
            }
        }
    }

    #endregion
}

/// <summary>Optional settings a client may change through the update_settings tool. Null means "leave unchanged".</summary>
public sealed class McpSettingsUpdate
{
    public string AllExportsDirectory { get; set; }
    public string RawDataDirectory { get; set; }
    public string PropertiesDirectory { get; set; }
    public string TextureDirectory { get; set; }
    public string AudioDirectory { get; set; }
    public string CodeDirectory { get; set; }
    public string ModelDirectory { get; set; }
    public bool? KeepDirectoryStructure { get; set; }
    public string MeshFormat { get; set; }
    public string NaniteMeshFormat { get; set; }
    public string MeshQuality { get; set; }
    public string SocketFormat { get; set; }
    public string CompressionFormat { get; set; }
    public string MaterialFormat { get; set; }
    public bool? SaveEmbeddedMaterials { get; set; }
    public bool? SaveMorphTargets { get; set; }
    public bool? SaveSkeletonAsMesh { get; set; }
    public string TextureFormat { get; set; }
    public int? TextureQuality { get; set; }
    public bool? SaveHdrTexturesAsHdr { get; set; }
    public bool? ExportAllTextureMips { get; set; }
    public bool? ConvertAudioToWav { get; set; }
}
