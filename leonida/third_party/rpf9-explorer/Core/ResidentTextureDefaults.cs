using System.Security.Cryptography;

namespace RpfPreview.Core;

public sealed record ResidentTextureDefault(ulong AssetKey, uint NameHash, string Name,
    int Width, int Height, IReadOnlyList<int> AllowedFormats, string PixelSha256);

public static class ResidentTextureDefaults
{
    public static IReadOnlyList<ResidentTextureDefault> Profiles { get; } = Array.AsReadOnly(new[]
    {
        new ResidentTextureDefault(0x1E2C9340E14A7D07, 0x82079169, "blank_white", 4, 4, Array.AsReadOnly(new[] { 72, 87 }),
            "8667E718294E9E0DF1D30600BA3EEB201F764AAD2DAD72748643E4A285E1D1F7"),
        new ResidentTextureDefault(0x2E4EE77724C11A54, 0x82DD5BD8, "blank_white2x1", 8, 4, Array.AsReadOnly(new[] { 72 }),
            "E9175DB65A9789096CA9CB5524D3ABC2107DF03E3C9BA3AF1ACA628F9C5D3BD2"),
        new ResidentTextureDefault(0xAA3EEFF23A15DC75, 0xA082D829, "blank_ma", 4, 4, Array.AsReadOnly(new[] { 71 }),
            "73DB2A85EF58266233629093BF4CB68E74327560745510E741133DD7276C98C0"),
        new ResidentTextureDefault(0x4FADAEAD4CD8F15F, 0xEF8E2912, "blank_mb_temp", 4, 4, Array.AsReadOnly(new[] { 72 }),
            "510A355C88E58F527DEEE6A3FC8BBBF7EB633909E892F9A9E6CFA4796F7AB787")
    });

    public static ResidentTextureDefault? Find(ulong assetKey, uint nameHash)
        => Profiles.FirstOrDefault(p => p.AssetKey == assetKey && p.NameHash == nameHash);

    public static bool MatchesMetadata(ResidentTextureDefault profile, RageTextureInfo texture)
        => Registered(profile) && texture.Name == profile.Name && texture.NameHash == profile.NameHash &&
            texture.Width == profile.Width && texture.Height == profile.Height && texture.Depth == 1 &&
            texture.Dimension == 1 && texture.MipCount == 1 && profile.AllowedFormats.Contains(texture.DxgiFormat) &&
            (texture.AssetKey == null || texture.AssetKey == profile.AssetKey);

    public static bool MatchesPixels(ResidentTextureDefault profile, RageTexturePixels pixels)
        => Registered(profile) && pixels.Width == profile.Width && pixels.Height == profile.Height &&
            pixels.Bgra32.Length == profile.Width * profile.Height * 4 &&
            Convert.ToHexString(SHA256.HashData(pixels.Bgra32)) == profile.PixelSha256;

    private static bool Registered(ResidentTextureDefault profile)
        => ReferenceEquals(Find(profile.AssetKey, profile.NameHash), profile);
}
