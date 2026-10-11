using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp;

/// <summary>
/// The tool surface exposed to AI clients. Every tool returns JSON text (or an image), and every
/// failure is reported as a tool error with a message the model can act on.
/// </summary>
[McpServerToolType]
public sealed class FModelMcpTools
{
    private FModelMcpTools() { }

    public const string Instructions =
        """
        You are connected to a running FModel instance (Unreal Engine archive explorer, powered by CUE4Parse).
        Workflow:
        1. Call `status` first. It tells you which game is open, whether archives are mounted, and whether packages were loaded.
        2. If `packagesLoadedInExplorer` is 0, call `load_packages` once so localized text and virtual paths resolve.
        3. Find assets with `search_files` (multi-word queries, optional regex/extension/directory) or browse with `list_directory`.
        4. Inspect with `get_file_info` (export classes), `read_package_json` (full property dump), `get_texture` (see an image), `read_text`.
        5. Export with `export_assets` / `export_folder` using a kind: raw, properties, textures, models, animations, worlds, audio, code.
           Output goes to the directories shown by `get_settings`; change formats or folders with `update_settings`.
        6. `open_in_ui` shows an asset in FModel's own window (3D viewer for meshes, audio player for sounds).
        Paths look like `ProjectName/Content/Folder/Asset.uasset`; `/Game/Folder/Asset` is also accepted.
        Large JSON responses are truncated at `maxChars`; pass `saveToFile=true` to write the full JSON to disk and read it with your own file tools.
        Everything you do is mirrored in FModel's log panel so the user can follow along.
        """;

    [McpServerTool(Name = "status", Title = "FModel status", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Current FModel state: game, UE version, mounted archives, AES keys, loaded packages, output directories, busy/ready status.")]
    public static string Status() => Run("status", null, () => McpBridge.ToJson(McpBridge.GetStatus()));

    [McpServerTool(Name = "list_archives", Title = "List archives", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("List every .pak/.utoc archive FModel found, whether it is mounted, encrypted, and its encryption key GUID.")]
    public static string ListArchives() => Run("list_archives", null, () => McpBridge.ToJson(McpBridge.ListArchives()));

    [McpServerTool(Name = "load_packages", Title = "Load packages", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Equivalent of pressing Load in FModel: populates the asset explorer, loads localized text and virtual paths. Call once per session before reading assets that contain FText.")]
    public static Task<string> LoadPackages(CancellationToken ct)
        => RunAsync("load_packages", null, async () => McpBridge.ToJson(await McpBridge.LoadPackagesAsync(ct)));

    [McpServerTool(Name = "list_directory", Title = "List directory", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("List sub-folders and files of a virtual directory inside the mounted archives. Use an empty path for the root.")]
    public static string ListDirectory(
        [Description("Virtual directory, e.g. 'FortniteGame/Content/Athena' or '' for the root. '/Game/...' is accepted.")] string path = "",
        [Description("Maximum folders and files to return (default 500).")] int limit = 500)
        => Run("list_directory", path, () => McpBridge.ToJson(McpBridge.ListDirectory(path, limit)));

    [McpServerTool(Name = "search_files", Title = "Search files", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Search file paths across all mounted archives. Space-separated terms must all match (case-insensitive) unless regex=true. Filter by extension (e.g. 'uasset', 'umap', 'wem') and/or directory prefix. Paginate with offset.")]
    public static string SearchFiles(
        [Description("Search terms, or a .NET regular expression when regex=true. May be empty if extension or directory is given.")] string query = "",
        [Description("Treat query as a regular expression.")] bool regex = false,
        [Description("Case-sensitive matching.")] bool caseSensitive = false,
        [Description("Only files with this extension, without the dot.")] string extension = null,
        [Description("Only files under this virtual directory.")] string directory = null,
        [Description("Maximum results (default 200, max 2000).")] int limit = 200,
        [Description("Number of matches to skip, for paging.")] int offset = 0)
        => Run("search_files", query, () => McpBridge.ToJson(McpBridge.SearchFiles(query, regex, caseSensitive, extension, directory, limit, offset)));

    [McpServerTool(Name = "get_file_info", Title = "File info", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Metadata for one file: size, archive, and for UE packages the list of exports with their classes (StaticMesh, Texture2D, SoundWave, ...).")]
    public static string GetFileInfo(
        [Description("Virtual path of the file.")] string path,
        [Description("Maximum exports to list (default 200).")] int maxExports = 200)
        => Run("get_file_info", path, () => McpBridge.ToJson(McpBridge.GetFileInfo(McpBridge.ResolveFile(path), maxExports)));

    [McpServerTool(Name = "read_package_json", Title = "Read package JSON", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Serialize a UE package (.uasset/.umap) to JSON exactly like FModel's property viewer. Optionally restrict to one export by name or index. Long output is truncated; set saveToFile=true to write the complete JSON into the properties export directory and get its path.")]
    public static string ReadPackageJson(
        [Description("Virtual path of the package.")] string path,
        [Description("Only serialize the export with this object name.")] string exportName = null,
        [Description("Only serialize the export at this index (see get_file_info).")] int? exportIndex = null,
        [Description("Maximum characters to return inline (default 200000).")] int maxChars = 200_000,
        [Description("Also write the full JSON to disk and return the file path.")] bool saveToFile = false)
        => Run("read_package_json", path, () =>
        {
            var file = McpBridge.ResolveFile(path);
            var (json, note) = McpBridge.PackageToJson(file, exportName, exportIndex);
            string savedTo = null;
            if (saveToFile) savedTo = McpBridge.SaveJsonToPropertiesDirectory(file, json);

            var body = McpBridge.Truncate(json, maxChars, out var truncated);
            var header = McpBridge.ToJson(new
            {
                file = file.Path,
                note,
                totalChars = json.Length,
                truncated,
                savedTo,
                hint = truncated && savedTo is null ? "Output truncated. Re-run with saveToFile=true, a larger maxChars, or exportName/exportIndex." : null
            });
            return header + "\n" + body;
        });

    [McpServerTool(Name = "read_text", Title = "Read text file", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Read a non-package file as text: ini, json, locres/locmeta (as JSON), AssetRegistry.bin (as JSON), uplugin, verse, lua, csv, txt, ...")]
    public static string ReadText(
        [Description("Virtual path of the file.")] string path,
        [Description("Maximum characters to return (default 200000).")] int maxChars = 200_000)
        => Run("read_text", path, () =>
        {
            var file = McpBridge.ResolveFile(path);
            var text = McpBridge.ReadText(file);
            var body = McpBridge.Truncate(text, maxChars, out var truncated);
            return truncated ? $"[truncated: showing {body.Length} of {text.Length} characters]\n{body}" : body;
        });

    [McpServerTool(Name = "get_texture", Title = "View texture", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Decode a texture (UTexture2D, cubemap, ...) or an image file (png/jpg/svg) and return it as a PNG image so you can look at it, plus its dimensions and pixel format.")]
    public static CallToolResult GetTexture(
        [Description("Virtual path of the texture package or image file.")] string path,
        [Description("Export name inside the package, when it contains several textures.")] string exportName = null,
        [Description("Longest side of the returned image in pixels (default 1024, max 4096).")] int maxSize = 1024)
        => Run("get_texture", path, () =>
        {
            var file = McpBridge.ResolveFile(path);
            var (data, mime, info) = McpBridge.GetTexture(file, exportName, maxSize);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = McpBridge.ToJson(new { file = file.Path, info }) },
                    new ImageContentBlock { Data = data, MimeType = mime }
                ]
            };
        });

    [McpServerTool(Name = "export_assets", Title = "Export assets", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Export one or more files to disk using FModel's export pipeline. kind: raw (original bytes), properties (JSON), textures (png/tga/...), models (meshes in the configured format), animations, worlds (umap), audio (wav/wem/...), code (decompiled blueprint). Returns the written file paths.")]
    public static Task<string> ExportAssets(
        [Description("Virtual paths of the files to export.")] string[] paths,
        [Description("What to export: raw, properties, textures, models, animations, worlds, audio, code.")] string kind,
        CancellationToken ct)
        => RunAsync("export_assets", $"{kind} x{paths?.Length ?? 0}", async () =>
        {
            if (paths is null || paths.Length == 0) throw new McpException("'paths' must contain at least one path.");
            var bulk = McpBridge.ParseExportKind(kind);
            var files = paths.Select(McpBridge.ResolveFile).Distinct().ToList();
            return McpBridge.ToJson(await McpBridge.ExportAsync(files, bulk, ct));
        });

    [McpServerTool(Name = "export_folder", Title = "Export folder", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Export every file under a virtual folder (recursively by default) with the given kind. Optionally filter by extension. Same output as export_assets.")]
    public static Task<string> ExportFolder(
        [Description("Virtual folder, e.g. 'FortniteGame/Content/Athena/Items/Cosmetics/Characters'.")] string folder,
        [Description("What to export: raw, properties, textures, models, animations, worlds, audio, code.")] string kind,
        [Description("Include sub-folders (default true).")] bool recursive = true,
        [Description("Only files with this extension, without the dot.")] string extension = null,
        CancellationToken ct = default)
        => RunAsync("export_folder", $"{kind} {folder}", async () =>
        {
            var bulk = McpBridge.ParseExportKind(kind);
            var files = McpBridge.FilesUnderFolder(folder, recursive, extension);
            return McpBridge.ToJson(await McpBridge.ExportAsync(files, bulk, ct));
        });

    [McpServerTool(Name = "open_in_ui", Title = "Open in FModel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Open files in the FModel window exactly like a double-click: JSON tab, texture preview, 3D viewer for meshes/maps, audio player for sounds. Use this when the user wants to look at something themselves.")]
    public static Task<string> OpenInUi(
        [Description("Virtual paths to open.")] string[] paths,
        CancellationToken ct)
        => RunAsync("open_in_ui", string.Join(", ", paths ?? []), async () =>
        {
            if (paths is null || paths.Length == 0) throw new McpException("'paths' must contain at least one path.");
            var files = paths.Select(McpBridge.ResolveFile).Distinct().ToList();
            await McpBridge.OpenInUiAsync(files, ct);
            return McpBridge.ToJson(new { opened = files.Select(f => f.Path).ToList() });
        });

    [McpServerTool(Name = "find_references", Title = "Find references", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Scan all packages for ones that reference the given package (slow on large games; can be cancelled with the cancel tool or Escape in FModel).")]
    public static Task<string> FindReferences(
        [Description("Virtual path of the package.")] string path,
        [Description("Maximum references to return (default 500).")] int limit = 500,
        CancellationToken ct = default)
        => RunAsync("find_references", path, async () => McpBridge.ToJson(await McpBridge.FindReferencesAsync(McpBridge.ResolveFile(path), limit, ct)));

    [McpServerTool(Name = "decompile_blueprint", Title = "Decompile blueprint", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Decompile a Blueprint/UClass package into readable pseudo C++ (same as FModel's 'Decompile' context menu).")]
    public static Task<string> DecompileBlueprint(
        [Description("Virtual path of the blueprint package.")] string path,
        [Description("Maximum characters to return (default 200000).")] int maxChars = 200_000,
        CancellationToken ct = default)
        => RunAsync("decompile_blueprint", path, async () =>
        {
            var text = await McpBridge.DecompileAsync(McpBridge.ResolveFile(path), ct);
            var body = McpBridge.Truncate(text, maxChars, out var truncated);
            return truncated ? $"[truncated: showing {body.Length} of {text.Length} characters]\n{body}" : body;
        });

    [McpServerTool(Name = "get_settings", Title = "Get export settings", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Current export settings: output directories, mesh/texture/material formats, and the allowed values for each.")]
    public static string GetSettings() => Run("get_settings", null, () => McpBridge.ToJson(McpBridge.GetSettings()));

    [McpServerTool(Name = "update_settings", Title = "Update export settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Change export settings (persisted like the Settings window). Only the fields you pass are changed. Use get_settings for allowed values. allExportsDirectory sets every export directory at once.")]
    public static string UpdateSettings(
        [Description("Set all export directories (raw, properties, textures, audio, code, models) to this folder.")] string allExportsDirectory = null,
        string rawDataDirectory = null,
        string propertiesDirectory = null,
        string textureDirectory = null,
        string audioDirectory = null,
        string codeDirectory = null,
        string modelDirectory = null,
        [Description("Recreate the virtual folder structure under the output directory.")] bool? keepDirectoryStructure = null,
        [Description("Mesh format: ActorX, Gltf2, OBJ, UEFormat, USD ... (see get_settings).")] string meshFormat = null,
        string naniteMeshFormat = null,
        string meshQuality = null,
        string socketFormat = null,
        string compressionFormat = null,
        [Description("Material depth: TopLayerOnly, AllLayers, ...")] string materialFormat = null,
        bool? saveEmbeddedMaterials = null,
        bool? saveMorphTargets = null,
        bool? saveSkeletonAsMesh = null,
        [Description("Texture format: Png, Jpeg, Tga, Dds, ...")] string textureFormat = null,
        [Description("JPEG quality 1-100.")] int? textureQuality = null,
        bool? saveHdrTexturesAsHdr = null,
        bool? exportAllTextureMips = null,
        [Description("Convert exported audio to .wav with vgmstream.")] bool? convertAudioToWav = null)
        => Run("update_settings", null, () => McpBridge.ToJson(McpBridge.UpdateSettings(new McpSettingsUpdate
        {
            AllExportsDirectory = allExportsDirectory,
            RawDataDirectory = rawDataDirectory,
            PropertiesDirectory = propertiesDirectory,
            TextureDirectory = textureDirectory,
            AudioDirectory = audioDirectory,
            CodeDirectory = codeDirectory,
            ModelDirectory = modelDirectory,
            KeepDirectoryStructure = keepDirectoryStructure,
            MeshFormat = meshFormat,
            NaniteMeshFormat = naniteMeshFormat,
            MeshQuality = meshQuality,
            SocketFormat = socketFormat,
            CompressionFormat = compressionFormat,
            MaterialFormat = materialFormat,
            SaveEmbeddedMaterials = saveEmbeddedMaterials,
            SaveMorphTargets = saveMorphTargets,
            SaveSkeletonAsMesh = saveSkeletonAsMesh,
            TextureFormat = textureFormat,
            TextureQuality = textureQuality,
            SaveHdrTexturesAsHdr = saveHdrTexturesAsHdr,
            ExportAllTextureMips = exportAllTextureMips,
            ConvertAudioToWav = convertAudioToWav
        })));

    [McpServerTool(Name = "set_aes_key", Title = "Set AES key", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Provide an AES decryption key (64 hex chars) for the main static key or for one archive (by name or encryption key GUID), then remount the archives.")]
    public static Task<string> SetAesKey(
        [Description("AES key as 64 hex characters, with or without 0x.")] string key,
        [Description("Archive name or encryption key GUID to apply the key to; omit for the main static key.")] string archiveNameOrGuid = null,
        CancellationToken ct = default)
        => RunAsync("set_aes_key", archiveNameOrGuid ?? "main", async () => McpBridge.ToJson(await McpBridge.SetAesKeyAsync(key, archiveNameOrGuid, ct)));

    [McpServerTool(Name = "cancel", Title = "Cancel current operation", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Cancel the operation FModel is currently running (same as pressing Escape) and any running export session.")]
    public static Task<string> Cancel()
        => RunAsync("cancel", null, async () =>
        {
            await McpBridge.CancelAsync();
            return McpBridge.ToJson(new { cancelled = true });
        });

    #region Plumbing

    private static T Run<T>(string tool, string detail, Func<T> body)
    {
        McpBridge.Trace(tool, detail);
        try
        {
            return body();
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "MCP tool {Tool} failed", tool);
            throw new McpException($"{e.GetType().Name}: {e.GetBaseException().Message}");
        }
    }

    private static async Task<T> RunAsync<T>(string tool, string detail, Func<Task<T>> body)
    {
        McpBridge.Trace(tool, detail);
        try
        {
            return await body();
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "MCP tool {Tool} failed", tool);
            throw new McpException($"{e.GetType().Name}: {e.GetBaseException().Message}");
        }
    }

    #endregion
}
