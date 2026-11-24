namespace RpfPreview.Core;

public sealed record ModelColorTexture(RageTextureReference Reference, string ParameterName, bool Layered, bool Secondary, bool NameInferred = false);

public static class ModelTextureBindings
{
    private sealed record Slot(string Name, int Priority, bool Layered = false, bool Secondary = false, bool Inferred = false);
    private static readonly IReadOnlyDictionary<uint, Slot> Slots = new Dictionary<uint, Slot>
    {
        [3370697346] = new("diffusetex", 0),
        [756347250] = new("diffusetexture", 1),
        [0x346394EA] = new("lyr0diffusetex", 2, true, false, true),
        [0x7D4F12E2] = new("lyr1diffusetex", 3, true, true, true),
        [3464205966] = new("diffusetexture_layer0", 10, true),
        [3632507550] = new("diffusetexture_layer1", 11, true, true),
        [851042057] = new("diffusetexture_layer2", 12, true, true),
        [1024521143] = new("diffusetexture_layer3", 13, true, true),
        [3853173992] = new("diffusetexture_layer4", 14, true, true),
        [2464948076] = new("diffusetexture_layer5", 15, true, true),
        [3389132183] = new("diffusetexture_layer6", 16, true, true),
        [3547013225] = new("diffusetexture_layer7", 17, true, true),
        [234123336] = new("diffusetex2", 20, false, true),
        [3831897388] = new("diffusetex3", 21, false, true),
        [3489704073] = new("diffusetexture2", 22, false, true),
        [1380592926] = new("diffusetexture3", 23, false, true)
    };

    public static IReadOnlyList<ModelColorTexture> ColorTextures(RageMaterial material)
        => material.Textures.Where(t => Slots.ContainsKey(t.ParameterHash)).OrderBy(t => Slots[t.ParameterHash].Priority)
            .Select(t => new ModelColorTexture(t, Slots[t.ParameterHash].Name, Slots[t.ParameterHash].Layered, Slots[t.ParameterHash].Secondary, Slots[t.ParameterHash].Inferred)).ToArray();
}
