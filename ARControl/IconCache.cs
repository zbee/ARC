using System;
using System.Collections.Generic;
using Dalamud.Interface.Internal;
using Dalamud.Plugin.Services;

namespace ARControl;

internal sealed class IconCache : IDisposable
{
    private readonly ITextureProvider _textureProvider;
    private readonly Dictionary<uint, TextureContainer> _textureWraps = new();

    public IconCache(ITextureProvider textureProvider)
    {
        _textureProvider = textureProvider;
    }

    public IDalamudTextureWrap? GetIcon(uint iconId)
    {
        if (_textureWraps.TryGetValue(iconId, out TextureContainer? container))
            return container.Texture;

        var iconTex = _textureProvider.GetIcon(iconId);
        if (iconTex != null)
        {
            if (iconTex.ImGuiHandle != nint.Zero)
            {
                _textureWraps[iconId] = new TextureContainer { Texture = iconTex };
                return iconTex;
            }

            iconTex.Dispose();
        }

        _textureWraps[iconId] = new TextureContainer { Texture = null };
        return null;
    }

    public void Dispose()
    {
        foreach (TextureContainer container in _textureWraps.Values)
            container.Dispose();

        _textureWraps.Clear();
    }

    private sealed class TextureContainer : IDisposable
    {
        public required IDalamudTextureWrap? Texture { get; init; }

        public void Dispose() => Texture?.Dispose();
    }
}
