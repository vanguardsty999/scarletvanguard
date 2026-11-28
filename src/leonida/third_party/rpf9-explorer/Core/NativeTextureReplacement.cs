using System.Security.Cryptography;

namespace RpfPreview.Core;

public sealed record NativeTextureReplacementResult(byte[] ResourceData, int ResourceSystemBytes,
    RageTextureInfo Texture, int ChangedGraphicsBytes, string SourceGraphicsSha256, string StagedGraphicsSha256)
{
    public string Format => "r9texdata";
    public string Description => "Staged decoded native resource: original system bytes followed by graphics with the selected DDS retiled. Original archive/index is not rewritten.";
}





public static class NativeTextureReplacement
{
    public static NativeTextureReplacementResult Stage(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics,
        int textureIndex, byte[] replacementDds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacementDds); cancellationToken.ThrowIfCancellationRequested();
        if ((long)system.Length + graphics.Length > RageTextureDecoder.MaxGraphicsBytes)
            throw new NotSupportedException("The decoded resource exceeds the 256MiB staging limit.");
        var dictionary = RageTextureDecoder.Inspect(system, graphics) ?? throw new InvalidDataException("Source is not a supported validated native texture dictionary.");
        if ((uint)textureIndex >= dictionary.Textures.Count) throw new ArgumentOutOfRangeException(nameof(textureIndex));
        byte[] staged = RageTextureDecoder.RetileReplacement(system, graphics, textureIndex, replacementDds, cancellationToken);
        int changed = 0;
        for (int i = 0; i < graphics.Length; i++)
        {
            if ((i & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (graphics[i] != staged[i]) changed++;
        }
        byte[] bundle = new byte[checked(system.Length + staged.Length)]; system.CopyTo(bundle); staged.CopyTo(bundle, system.Length);
        return new(bundle, system.Length, dictionary.Textures[textureIndex], changed,
            Convert.ToHexString(SHA256.HashData(graphics)), Convert.ToHexString(SHA256.HashData(staged)));
    }
}
