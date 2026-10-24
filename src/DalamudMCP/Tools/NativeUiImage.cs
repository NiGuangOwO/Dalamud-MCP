using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Read only the selected image atlas part; never decode a texture union without its discriminator.</summary>
internal static unsafe class NativeUiImage
{
    internal static JObject Read(nint address)
    {
        var image = MemoryProbe.TryReadStruct<AtkImageNode>((ulong)address, out _);
        if (!image.HasValue || image.Value.Type != NodeType.Image)
            return new JObject { ["available"] = false, ["reason"] = "not a readable Image node" };
        var result = new JObject { ["available"] = false, ["partId"] = image.Value.PartId };
        var parts = MemoryProbe.TryReadStruct<AtkUldPartsList>((ulong)image.Value.PartsList, out _);
        if (!parts.HasValue) { result["reason"] = "image parts list unreadable"; return result; }
        result["partsListId"] = parts.Value.Id; result["partCount"] = parts.Value.PartCount;
        if (parts.Value.PartCount is 0 or > 4096 || image.Value.PartId >= parts.Value.PartCount || parts.Value.Parts == null)
        { result["reason"] = "selected image part is out of the bounded SDK parts array"; return result; }
        ulong partAddress;
        try { partAddress = checked((ulong)parts.Value.Parts + (ulong)image.Value.PartId * (ulong)sizeof(AtkUldPart)); }
        catch (OverflowException) { result["reason"] = "image part address overflow"; return result; }
        var part = MemoryProbe.TryReadStruct<AtkUldPart>(partAddress, out _);
        if (!part.HasValue) { result["reason"] = "selected image part unreadable"; return result; }
        result["available"] = true;
        result["u"] = part.Value.U; result["v"] = part.Value.V;
        result["width"] = part.Value.Width; result["height"] = part.Value.Height;
        var asset = MemoryProbe.TryReadStruct<AtkUldAsset>((ulong)part.Value.UldAsset, out _);
        result["assetAvailable"] = asset.HasValue;
        if (!asset.HasValue) return result;
        result["assetId"] = asset.Value.Id;
        result["textureType"] = asset.Value.AtkTexture.TextureType.ToString();
        if (asset.Value.AtkTexture.TextureType == TextureType.Resource)
        {
            var resource = MemoryProbe.TryReadStruct<AtkTextureResource>((ulong)asset.Value.AtkTexture.Resource, out _);
            result["resourceAvailable"] = resource.HasValue;
            if (resource.HasValue)
            {
                result["nativeIconId"] = resource.Value.IconId;
                result["iconId"] = resource.Value.IconId is 0 or uint.MaxValue ? null : resource.Value.IconId;
                result["texturePathHash"] = resource.Value.TexPathHash;
            }
        }
        return result;
    }
}
