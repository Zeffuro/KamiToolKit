using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Dalamud;

namespace KamiToolKit.Extensions;

public static unsafe class AtkUldPartExtensions {

    private static readonly ConcurrentDictionary<string, bool> FileExistsCache = new(StringComparer.Ordinal);

    private static bool FileExistsCached(string path)
        => FileExistsCache.GetOrAdd(path, static p => Services.DataManager.FileExists(p));

    public static void ClearFileExistsCache() {
        FileExistsCache.Clear();
        ResolvedPathCache.Clear();
        ThemeModifierCache.Clear();
        IconSubFolderCache.Clear();
    }

    private static AtkStage* cachedStage;

    private static AtkStage* Stage {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => cachedStage is not null ? cachedStage : (cachedStage = AtkStage.Instance());
    }

    private static readonly ConcurrentDictionary<byte, string> ThemeModifierCache = new();


    private readonly record struct ResolvedPathKey(string Path, byte ThemeType, bool ResolveTheme) {
        public override int GetHashCode() => Path.GetHashCode();
    }
    private static readonly ConcurrentDictionary<ResolvedPathKey, string?> ResolvedPathCache = new();

    private static string? ResolveTexturePath(string path, byte themeType, bool resolveTheme) {
        var key = new ResolvedPathKey(path, themeType, resolveTheme);
        if (ResolvedPathCache.TryGetValue(key, out var cached)) return cached;

        var texturePath = path.Replace("_hr1", string.Empty);

        if (resolveTheme && themeType is not 0) {
            var modifier = ThemeModifierCache.GetOrAdd(themeType, static t => $"uld/img{t:00}");
            var themedPath = texturePath.Replace("uld", modifier);
            if (FileExistsCached(themedPath)) {
                texturePath = themedPath;
            }
        }

        var result = FileExistsCached(texturePath) ? texturePath : null;
        ResolvedPathCache[key] = result;
        return result;
    }

    extension(ref AtkUldPart part) {
        public bool IsTextureReady => part.UldAsset is not null && part.UldAsset->AtkTexture.IsTextureReady();
        public Vector2 LoadedTextureSize => part.GetActualTextureSize();
        public string LoadedPath => part.GetLoadedPath();

        public void LoadTexture(string path, bool resolveTheme = true) {
            try {
                if (part.UldAsset is null) return;

                var themeType = Stage->AtkUIColorHolder->ActiveColorThemeType;
                var resolved = ResolveTexturePath(path, themeType, resolveTheme);
                if (resolved is null) return;

                part.TryUnloadTexture();
                part.UldAsset->AtkTexture.LoadTextureWithDefaultVersion(resolved);
            }
            catch (Exception e) {
                Services.Log.Error(e, "Error in AtkUldPartExtensions LoadTexture");
            }
        }

        public void LoadIcon(uint iconId)
            => part.UldAsset->AtkTexture.LoadIconTexture(iconId, GetIconSubFolder(iconId));

        private Vector2 GetActualTextureSize() {
            if (part.UldAsset is null) return Vector2.Zero;
            if (!part.UldAsset->AtkTexture.IsTextureReady()) return Vector2.Zero;
            if (part.UldAsset->AtkTexture.TextureType is 0) return Vector2.Zero;
            if (part.UldAsset->AtkTexture.KernelTexture is null) return Vector2.Zero;

            var width = part.UldAsset->AtkTexture.GetTextureWidth();
            var height = part.UldAsset->AtkTexture.GetTextureHeight();
            return new Vector2(width, height);
        }

        public void LoadTexture(Texture* texture) {
            if (part.UldAsset is null) return;

            part.TryUnloadTexture();
            part.UldAsset->AtkTexture.KernelTexture = texture;
            part.UldAsset->AtkTexture.TextureType = TextureType.KernelTexture;
        }

        public void LoadTexture(IDalamudTextureWrap textureWrap) {
            var texturePointer = (Texture*)Services.TextureProvider.ConvertToKernelTexture(textureWrap, true);
            if (texturePointer is null) return;

            part.LoadTexture(texturePointer);
        }

        private string GetLoadedPath() {
            if (part.UldAsset is null) return string.Empty;
            if (part.UldAsset->AtkTexture.Resource is null) return string.Empty;
            if (part.UldAsset->AtkTexture.Resource->TexFileResourceHandle is null) return string.Empty;

            return part.UldAsset->AtkTexture.Resource->TexFileResourceHandle->FileName.ToString();
        }

        private void TryUnloadTexture() {
            if (part.UldAsset is null) return;
            if (part.UldAsset->AtkTexture.TextureType is 0) return;
            if (part.UldAsset->AtkTexture.KernelTexture is null) return;
            if (!part.UldAsset->AtkTexture.IsTextureReady()) return;

            part.UldAsset->AtkTexture.ReleaseTexture();
            part.UldAsset->AtkTexture.KernelTexture = null;
            part.UldAsset->AtkTexture.TextureType = 0;
        }
    }


    public static IconSubFolder GetIconSubFolder(uint iconId) {
        var textureManager = Stage->AtkTextureResourceManager;
        var textureScale = textureManager->DefaultTextureScale;
        var iconLanguage = textureManager->IconLanguage;

        var key = new IconSubFolderKey(iconId, iconLanguage, textureScale);
        if (IconSubFolderCache.TryGetValue(key, out var cachedFolder)) return cachedFolder;

        Span<byte> buffer = stackalloc byte[0x100];
        buffer.Clear();
        var bytePointer = (byte*)Unsafe.AsPointer(ref buffer[0]);

        var textureScale = textureManager->DefaultTextureScale;
        var targetFolder = (IconSubFolder)textureManager->IconLanguage;

        // Try to resolve the path using the current language
        AtkTexture.GetIconPath(bytePointer, iconId, textureScale, targetFolder);
        var pathResult = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(bytePointer).String;

        // If the resolved path doesn't exist, fall back to the default folder
        var resolved = FileExistsCached(pathResult) ? targetFolder : IconSubFolder.None;
        IconSubFolderCache[key] = resolved;
        return resolved;
    }

    private readonly record struct IconSubFolderKey(uint IconId, int IconLanguage, int TextureScale);
    private static readonly ConcurrentDictionary<IconSubFolderKey, IconSubFolder> IconSubFolderCache = new();
}
