using System.Buffers.Binary;
using System.Numerics;

namespace RpfPreview.Core;

public sealed record RageJoint(uint Hash, int? Parent, Matrix4x4 InverseBindMatrix);

public sealed class RageSkeleton
{
    private const int MaxJoints = 4096;
    private const int BoneStride = 0x50;

    public IReadOnlyList<RageJoint> Joints { get; }

    private RageSkeleton(RageJoint[] joints) => Joints = Array.AsReadOnly(joints);

    public static RageSkeleton? ReadForDrawable(ReadOnlySpan<byte> system, int drawableOffset)
    {
        if (!Fits(system, drawableOffset, 0x20) ||
            !TryPointer(system, drawableOffset + 0x18, 0x70, out int header)) return null;
        return Read(system, header);
    }

    public static RageSkeleton? Read(ReadOnlySpan<byte> system, int header)
    {
        if (!Fits(system, header, 0x70)) return null;
        int count = U16(system, header + 0x6c);
        if (count is < 1 or > MaxJoints || count != U16(system, header + 0x6e) ||
            !TryPointer(system, header + 0x28, count * BoneStride, out int boneArray) ||
            !TryPointer(system, header + 0x30, count * 64, out int inverseBinds) ||
            !TryPointer(system, header + 0x40, count * 2, out int parents)) return null;

        var joints = new RageJoint[count];
        for (int i = 0; i < count; i++)
        {
            int bone = boneArray + i * BoneStride;
            ushort parent = U16(system, parents + i * 2);
            if (U16(system, bone + 0x48) != i || U16(system, bone + 0x46) != parent ||
                (parent != ushort.MaxValue && parent >= count) ||
                !TryInverseBind(system.Slice(inverseBinds + i * 64, 64), out var inverseBind)) return null;
            joints[i] = new(U32(system, bone + 0x1c), parent == ushort.MaxValue ? null : parent, inverseBind);
        }
        return IsAcyclic(joints) ? new(joints) : null;
    }

    public static IReadOnlyList<uint>? ReadModelPalette(ReadOnlySpan<byte> system, int modelOffset)
    {
        if (!Fits(system, modelOffset, 0x30)) return null;
        int count = U16(system, modelOffset + 0x14);
        if (count is < 1 or > MaxJoints ||
            !TryPointer(system, modelOffset + 0x28, count * 4, out int palette)) return null;
        var hashes = new uint[count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = U32(system, palette + i * 4);
        return Array.AsReadOnly(hashes);
    }

    public bool TryResolvePalette(IReadOnlyList<uint> palette, out int[] jointIndices)
    {
        jointIndices = [];
        if (palette is null) return false;
        int count = palette.Count;
        if (count is < 0 or > MaxJoints) return false;
        var byHash = new Dictionary<uint, int>(Joints.Count);
        for (int i = 0; i < Joints.Count; i++)
            if (!byHash.TryAdd(Joints[i].Hash, i)) byHash[Joints[i].Hash] = -1;

        var indices = new int[count];
        for (int i = 0; i < count; i++)
            if (!byHash.TryGetValue(palette[i], out indices[i]) || indices[i] < 0) return false;
        jointIndices = indices;
        return true;
    }

    private static bool IsAcyclic(RageJoint[] joints)
    {
        var state = new byte[joints.Length];
        for (int i = 0; i < joints.Length; i++)
        {
            int? joint = i;
            while (joint is int k && state[k] == 0)
            {
                state[k] = 1;
                joint = joints[k].Parent;
            }
            if (joint is int ancestor && state[ancestor] == 1) return false;
            joint = i;
            while (joint is int k && state[k] == 1)
            {
                state[k] = 2;
                joint = joints[k].Parent;
            }
        }
        return true;
    }

    private static bool TryInverseBind(ReadOnlySpan<byte> data, out Matrix4x4 result)
    {
        Span<float> m = stackalloc float[16];
        result = default;
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
            if (i != 15 && !float.IsFinite(m[i])) return false;
        }
        if (m[3] != 0 || m[7] != 0 || m[11] != 0) return false;
        var matrix = new Matrix4x4(m[0], m[1], m[2], 0, m[4], m[5], m[6], 0,
            m[8], m[9], m[10], 0, m[12], m[13], m[14], 1);
        float determinant = matrix.GetDeterminant();
        if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-12f) return false;
        result = matrix;
        return true;
    }

    private static bool Fits(ReadOnlySpan<byte> data, int offset, int size) =>
        offset >= 0 && offset <= data.Length - size;

    private static bool TryPointer(ReadOnlySpan<byte> data, int field, int size, out int offset)
    {
        offset = 0;
        if (!Fits(data, field, 8)) return false;
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(data[field..]);
        if (pointer is < 0x50000000UL or >= 0x60000000UL) return false;
        offset = (int)(pointer - 0x50000000UL);
        return Fits(data, offset, size);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
