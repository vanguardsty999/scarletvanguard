using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RpfPreview.Core;

public sealed class BcpackProvisionalException : NotSupportedException
{
    public BcpackProvisionalException(string message) : base(message) { }
}

public sealed record BcpackDecodeResult(byte[] Data, IReadOnlySet<string> ProvisionalEntries);

/// <summary>
/// Clean-room Xbox Series DirectStorage BCPack bitstream decoder.
/// Implements the 12/16-lane 16-bit interleaved rANS entropy layer (8-byte/cycle prefetch
/// scheduling, banded exponential probability models, legal end-state validation) and
/// block rebuilders for DSTORAGE_BCPACK_MODE codecs 1 (BC1), 3 (BC3), 4 (BC4), and 5 (BC5).
/// </summary>
public static class BcpackDecoder
{
    public const uint RansL = 1u << 24;
    public const int MaxWidth = 10;
    public const int PrefetchBudget = 8;
    public const int MaxVirtualZeros = 8;
    public const int ElementBytes = 8;
    public const int MaxOutputBytes = 256 * 1024 * 1024;

    private readonly record struct CodecSpec(int Id, string Name, int BlockBytes, int[] LaneWidths, bool SupportsRebuild);

    private static readonly CodecSpec[] CodecSpecs = BuildCodecSpecs();
    private static readonly RansModel[][] Models = BuildModels();
    private static readonly IReadOnlySet<string> EmptyProvisional = FrozenSet<string>.Empty;

    public static bool IsSupportedCodec(int codec) => TryGetCodecSpec(codec, out var spec) && spec.SupportsRebuild;

    public static bool IsEntropyCodec(int codec) => TryGetCodecSpec(codec, out _);

    public static string GetCodecName(int codec) => codec switch
    {
        2 => "BC2",
        _ => TryGetCodecSpec(codec, out var spec) ? spec.Name : $"codec {codec}"
    };

    public static int GetBlockBytes(int codec) =>
        TryGetCodecSpec(codec, out var spec)
            ? spec.BlockBytes
            : throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

    private static bool TryGetCodecSpec(int codec, out CodecSpec spec)
    {
        if ((uint)codec < (uint)CodecSpecs.Length && CodecSpecs[codec].BlockBytes != 0)
        {
            spec = CodecSpecs[codec];
            return true;
        }
        spec = default;
        return false;
    }

    private static CodecSpec[] BuildCodecSpecs()
    {
        var specs = new CodecSpec[8];
        specs[1] = new(1, "BC1", 8, [8, 8, 5, 6, 5, 5, 6, 5, 8, 8, 8, 8], true);
        specs[3] = new(3, "BC3", 16, [4, 4, 8, 6, 6, 6, 6, 5, 6, 5, 8, 8], true);
        specs[4] = new(4, "BC4", 8, [8, 8, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6], true);
        specs[5] = new(5, "BC5", 16, [4, 4, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6], true);
        specs[6] = new(6, "BC6H", 16, [4, 4, 8, 8, 8, 5, 5, 5, 6, 6, 6, 8, 8, 8, 8, 5], false);
        specs[7] = new(7, "BC7", 16, [4, 4, 8, 8, 8, 8, 8, 7, 7, 5, 6, 8, 8, 8, 8, 5], false);
        return specs;
    }

    /// <summary>
    /// Decodes one BCPack chunk into <paramref name="outputSize"/> bytes of standard BCn blocks.
    /// Runs in strict confirmed-history mode by default (<paramref name="allowProvisional"/> = false).
    /// </summary>
    public static byte[] Decode(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional = false,
        CancellationToken cancellationToken = default)
        => DecodeWithInfo(packed, codec, outputSize, allowProvisional, cancellationToken).Data;

    /// <summary>
    /// Decodes one BCPack chunk and returns both the BCn block payload and any provisional
    /// built-in history labels used (always empty when <paramref name="allowProvisional"/> is false).
    /// </summary>
    public static BcpackDecodeResult DecodeWithInfo(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCodecSpec(codec, out var spec) || !spec.SupportsRebuild)
            throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

        ValidateOutputSize(spec, outputSize);
        var (lanes, _) = DecodeLanes(packed, spec, outputSize, cancellationToken);
        int blockCount = outputSize / spec.BlockBytes;
        HashSet<string>? provisional = allowProvisional ? new HashSet<string>(StringComparer.Ordinal) : null;

        byte[] data = spec.Id switch
        {
            1 => RebuildBc1(lanes, blockCount, provisional, cancellationToken),
            3 => RebuildBc3(lanes, blockCount, provisional, cancellationToken),
            4 => RebuildBc4(lanes, blockCount, provisional, cancellationToken),
            5 => RebuildBc5(lanes, blockCount, provisional, cancellationToken),
            _ => throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.")
        };

        return new BcpackDecodeResult(data, provisional is not null ? provisional : EmptyProvisional);
    }

    /// <summary>
    /// Entropy layer: decodes <paramref name="packed"/> into per-lane symbol arrays and bytes consumed.
    /// Supports all six active modes (1=BC1, 3=BC3, 4=BC4, 5=BC5, 6=BC6H, 7=BC7).
    /// </summary>
    internal static (ushort[][] Lanes, int BytesUsed) DecodeLanes(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCodecSpec(codec, out var spec))
            throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

        ValidateOutputSize(spec, outputSize);
        return DecodeLanes(packed, spec, outputSize, cancellationToken);
    }

    private static void ValidateOutputSize(CodecSpec spec, int outputSize)
    {
        if (outputSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), outputSize, "Decoded BCPack output size must be positive.");
        if (outputSize > MaxOutputBytes)
            throw new NotSupportedException($"Decoded BCPack size {outputSize} exceeds the {MaxOutputBytes / (1024 * 1024)} MiB output limit.");
        if (outputSize % spec.BlockBytes != 0)
            throw new InvalidDataException($"Decoded size {outputSize} is not a whole number of {spec.Name} blocks.");
    }

    private static (ushort[][] Lanes, int BytesUsed) DecodeLanes(
        ReadOnlySpan<byte> packed,
        CodecSpec spec,
        int outputSize,
        CancellationToken cancellationToken)
    {
        int symbolsPerLane = outputSize / ElementBytes;
        var (lanes, bytesUsed) = DecodeLanesRaw(packed, spec.LaneWidths, symbolsPerLane, cancellationToken);

        if (bytesUsed < packed.Length && packed[bytesUsed..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("BCPack stream has non-zero data left over after the last lane finished.");

        return (lanes, bytesUsed);
    }

    private static (ushort[][] Lanes, int BytesUsed) DecodeLanesRaw(
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<int> defaultWidths,
        int symbolsPerLane,
        CancellationToken cancellationToken)
    {
        const byte HeaderByte0 = 0;
        const byte HeaderByte1 = 1;
        const byte HeaderByte2 = 2;
        const byte ProcessPayload = 3;

        int laneCount = defaultWidths.Length;
        Span<byte> controlState = stackalloc byte[laneCount];
        Span<int> symbolsRemaining = stackalloc int[laneCount];
        Span<int> symbolWidth = stackalloc int[laneCount];
        Span<int> modelQ = stackalloc int[laneCount];
        Span<byte> isThreeByteHeader = stackalloc byte[laneCount];
        Span<byte> isFlushFragment = stackalloc byte[laneCount];
        Span<bool> laneDone = stackalloc bool[laneCount];
        Span<bool> inputFull = stackalloc bool[laneCount];
        Span<bool> symbolHeld = stackalloc bool[laneCount];
        Span<byte> virtualZeroCount = stackalloc byte[laneCount];
        Span<byte> inputByte = stackalloc byte[laneCount];
        Span<int> decodedSymbol = stackalloc int[laneCount];
        Span<int> payloadBytesRemaining = stackalloc int[laneCount];
        Span<ulong> stateBeforeMerge = stackalloc ulong[laneCount];
        Span<ulong> stateAfterStep = stackalloc ulong[laneCount];
        Span<bool> outputSlotFull = stackalloc bool[laneCount];
        Span<ushort> outputSlotValue = stackalloc ushort[laneCount];
        Span<bool> laneStalled = stackalloc bool[laneCount];
        Span<int> outputCount = stackalloc int[laneCount];

        var models = new RansModel?[laneCount];
        var output = new ushort[laneCount][];
        for (int i = 0; i < laneCount; i++)
        {
            controlState[i] = HeaderByte0;
            symbolsRemaining[i] = symbolsPerLane;
            laneDone[i] = symbolsPerLane <= 0;
            output[i] = new ushort[symbolsPerLane];
        }

        int inputPos = 0;
        int inputLength = raw.Length;
        uint cycle = 0;

        static void Fail(string msg, int lane, int width, int qVal, int bytePos)
            => throw new InvalidDataException($"BCPack lane {lane} (width {width}, q {qVal}) at input byte {bytePos}: {msg}");

        while (true)
        {
            if ((cycle++ & 0xFFFFu) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            bool allDone = true;
            for (int i = 0; i < laneCount; i++)
            {
                if (!laneDone[i] || outputSlotFull[i])
                {
                    allDone = false;
                    break;
                }
            }
            if (allDone)
                return (output, inputPos);

            // Phase 1: state update, prefetch arbitration (at most 8 bytes/cycle),
            // and symbol release for non-stalled lanes.
            int prefetchRemaining = PrefetchBudget;
            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] || (symbolHeld[i] && outputSlotFull[i]))
                {
                    laneStalled[i] = true;
                    continue;
                }
                laneStalled[i] = false;
                if (controlState[i] == ProcessPayload)
                {
                    stateBeforeMerge[i] = symbolHeld[i] ? models[i]!.Advance(decodedSymbol[i], stateAfterStep[i]) : stateAfterStep[i];
                }
                if (!inputFull[i] && (controlState[i] != ProcessPayload || payloadBytesRemaining[i] > 0))
                {
                    if (prefetchRemaining == 0)
                    {
                        laneStalled[i] = true;
                        continue;
                    }
                    if (inputPos >= inputLength)
                        Fail("input ran out", i, symbolWidth[i], modelQ[i], inputPos);
                    inputByte[i] = raw[inputPos++];
                    inputFull[i] = true;
                    prefetchRemaining--;
                }
                if (symbolHeld[i])
                {
                    outputSlotFull[i] = true;
                    outputSlotValue[i] = (ushort)decodedSymbol[i];
                    symbolHeld[i] = false;
                }
            }

            // Phase 2: header parsing, byte merge, virtual zero fill on flush, and symbol lookup.
            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] || laneStalled[i])
                    continue;

                byte ctrl = controlState[i];
                if (ctrl == HeaderByte0)
                {
                    if (!inputFull[i]) continue;
                    byte hdr0 = inputByte[i];
                    modelQ[i] = hdr0 & 15;
                    isFlushFragment[i] = (byte)((hdr0 >> 4) & 1);
                    isThreeByteHeader[i] = (byte)((hdr0 >> 5) & 1);
                    symbolWidth[i] = defaultWidths[i] - (hdr0 >> 6);
                    inputFull[i] = false;
                    controlState[i] = HeaderByte1;
                    virtualZeroCount[i] = 0;
                }
                else if (ctrl == HeaderByte1)
                {
                    if (!inputFull[i]) continue;
                    inputFull[i] = false;
                    int width = symbolWidth[i];
                    if (width < 1 || width > MaxWidth)
                        Fail("symbol width out of range", i, width, modelQ[i], inputPos);
                    models[i] = Models[width - 1][modelQ[i]];
                    if (isThreeByteHeader[i] != 0)
                    {
                        payloadBytesRemaining[i] = inputByte[i];
                        controlState[i] = HeaderByte2;
                    }
                    else
                    {
                        payloadBytesRemaining[i] = inputByte[i] + 1;
                        controlState[i] = ProcessPayload;
                    }
                }
                else if (ctrl == HeaderByte2)
                {
                    if (!inputFull[i]) continue;
                    payloadBytesRemaining[i] = ((inputByte[i] << 8) | (payloadBytesRemaining[i] & 0xFF)) + 257;
                    controlState[i] = ProcessPayload;
                    inputFull[i] = false;
                }
                else
                {
                    ulong state = stateBeforeMerge[i];
                    if (state < RansL)
                    {
                        if (payloadBytesRemaining[i] > 0)
                        {
                            if (inputFull[i])
                            {
                                state = (state << 8) | inputByte[i];
                                payloadBytesRemaining[i]--;
                                inputFull[i] = false;
                                if (payloadBytesRemaining[i] == 0 && isFlushFragment[i] != 0)
                                {
                                    while (state < RansL && state != 0 && virtualZeroCount[i] < MaxVirtualZeros)
                                    {
                                        state <<= 8;
                                        virtualZeroCount[i]++;
                                    }
                                }
                            }
                        }
                        else if (isFlushFragment[i] != 0)
                        {
                            if (state == 0 || virtualZeroCount[i] >= MaxVirtualZeros)
                                Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                            while (state < RansL && state != 0 && virtualZeroCount[i] < MaxVirtualZeros)
                            {
                                state <<= 8;
                                virtualZeroCount[i]++;
                            }
                        }
                        else
                        {
                            stateAfterStep[i] = state;
                            if (symbolsRemaining[i] > 0)
                                controlState[i] = HeaderByte0;
                            else
                                laneDone[i] = true;
                            continue;
                        }
                    }

                    bool hasNextSymbol = state > RansL && symbolsRemaining[i] > 0;
                    if (hasNextSymbol)
                    {
                        int sym = models[i]!.Lookup((int)(state & 0xFFFFu));
                        decodedSymbol[i] = sym;
                        symbolsRemaining[i]--;
                        if ((sym >> symbolWidth[i]) != 0)
                            Fail("symbol outside its width", i, symbolWidth[i], modelQ[i], inputPos);
                    }
                    symbolHeld[i] = hasNextSymbol;
                    stateAfterStep[i] = state;

                    if (payloadBytesRemaining[i] == 0)
                    {
                        if (isFlushFragment[i] != 0)
                        {
                            if (state == RansL)
                            {
                                if (symbolsRemaining[i] > 0)
                                {
                                    controlState[i] = HeaderByte0;
                                    stateBeforeMerge[i] = 0;
                                    stateAfterStep[i] = 0;
                                }
                                else
                                {
                                    laneDone[i] = true;
                                }
                            }
                            else if (state > RansL && !hasNextSymbol)
                            {
                                Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                            }
                        }
                        else if (symbolsRemaining[i] == 0 && !hasNextSymbol)
                        {
                            laneDone[i] = true;
                        }
                        else if (state == RansL)
                        {
                            Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                        }
                    }
                    else if (state == RansL || (symbolsRemaining[i] == 0 && !hasNextSymbol && state > RansL))
                    {
                        Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                    }
                }
            }

            // Phase 3: emit one synchronized element when every active lane has filled its slot.
            bool ready = true;
            bool anyFull = false;
            for (int i = 0; i < laneCount; i++)
            {
                if (outputSlotFull[i])
                {
                    anyFull = true;
                }
                else if (!(laneDone[i] && !symbolHeld[i]))
                {
                    ready = false;
                    break;
                }
            }
            if (ready && anyFull)
            {
                for (int i = 0; i < laneCount; i++)
                {
                    if (outputSlotFull[i])
                    {
                        int idx = outputCount[i];
                        if ((uint)idx >= (uint)symbolsPerLane)
                            Fail("produced more symbols than expected", i, symbolWidth[i], modelQ[i], inputPos);
                        output[i][idx] = outputSlotValue[i];
                        outputCount[i] = idx + 1;
                        outputSlotFull[i] = false;
                    }
                }
            }
            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] && symbolHeld[i] && !outputSlotFull[i])
                {
                    outputSlotFull[i] = true;
                    outputSlotValue[i] = (ushort)decodedSymbol[i];
                    symbolHeld[i] = false;
                }
            }
        }
    }

    private static void EnsureZeroLane1(ushort[] lane1, string codecName)
    {
        if (lane1.AsSpan().IndexOfAnyExcept((ushort)0) >= 0)
            throw new InvalidDataException($"{codecName} lane 1 is non-zero (unexpected).");
    }

    private static bool ResolveBuiltinHistoryStatus(
        BcpackHistoryStatus status,
        string codecName,
        int blockIndex,
        int entryIndex,
        HashSet<string>? provisional,
        bool allowUnknownFallback,
        ref SortedSet<int>? unknownEntries)
    {
        if (status == BcpackHistoryStatus.Confirmed)
            return true;

        if (status == BcpackHistoryStatus.Provisional)
        {
            if (provisional is null)
                throw new BcpackProvisionalException($"{codecName} block {blockIndex} references unconfirmed built-in history entry {entryIndex}.");
            provisional.Add($"{codecName} history {entryIndex}");
            return true;
        }

        if (provisional is null || !allowUnknownFallback)
            throw new BcpackProvisionalException($"{codecName} block {blockIndex} references unknown built-in history entry {entryIndex}.");

        unknownEntries ??= new SortedSet<int>();
        unknownEntries.Add(entryIndex);
        return false;
    }

    private static void FinalizeUnknownHistory(
        SortedSet<int>? unknownEntries,
        int lostBlocks,
        int blockCount,
        string codecName,
        HashSet<string>? provisional)
    {
        if (unknownEntries is null)
            return;

        if (lostBlocks > LooseUnknownBlockLimit(blockCount))
            throw new BcpackProvisionalException($"{codecName} chunk has {lostBlocks}/{blockCount} blocks from unknown built-in history entries.");

        provisional!.Add($"{codecName} unknown history [{string.Join(", ", unknownEntries)}] ({lostBlocks} blocks)");
    }

    private static byte[] RebuildBc1(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC1");

        byte[] quad = BcpackTables.Bc1Quadrant;
        Bc1HistoryEntry[] builtin = BcpackTables.Bc1History;
        byte[] output = new byte[checked(blockCount * 8)];
        Span<Bc1Endpoints> history = stackalloc Bc1Endpoints[256];
        SortedSet<int>? unusedUnknown = null;

        ushort[] refLane = lanes[0];
        ushort[] r0Lane = lanes[2], g0Lane = lanes[3], b0Lane = lanes[4];
        ushort[] r1Lane = lanes[5], g1Lane = lanes[6], b1Lane = lanes[7];
        ushort[] q0Lane = lanes[8], q1Lane = lanes[9], q2Lane = lanes[10], q3Lane = lanes[11];

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int v = refLane[i];
            int dist = (v & 1) != 0 ? 256 - ((v + 1) >> 1) : (v >> 1);
            Bc1Endpoints pred;
            if (dist == 0)
            {
                pred = default;
            }
            else if (i >= dist)
            {
                pred = history[(i - dist) & 255];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                ResolveBuiltinHistoryStatus(h.Status, "BC1", i, entry, provisional, allowUnknownFallback: false, ref unusedUnknown);
                pred = new(h.R0, h.G0, h.B0, h.R1, h.G1, h.B1);
            }

            byte e0 = (byte)((pred.R0 + DecodeZigZag(r0Lane[i])) & 31);
            byte e1 = (byte)((pred.G0 + DecodeZigZag(g0Lane[i])) & 63);
            byte e2 = (byte)((pred.B0 + DecodeZigZag(b0Lane[i])) & 31);
            byte e3 = (byte)((pred.R1 + DecodeZigZag(r1Lane[i])) & 31);
            byte e4 = (byte)((pred.G1 + DecodeZigZag(g1Lane[i])) & 63);
            byte e5 = (byte)((pred.B1 + DecodeZigZag(b1Lane[i])) & 31);
            history[i & 255] = new(e0, e1, e2, e3, e4, e5);

            int at = i * 8;
            ushort col0 = (ushort)((e0 << 11) | (e1 << 5) | e2);
            ushort col1 = (ushort)((e3 << 11) | (e4 << 5) | e5);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at, 2), col0);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 2, 2), col1);

            byte qa = quad[q0Lane[i]], qb = quad[q1Lane[i]], qc = quad[q2Lane[i]], qd = quad[q3Lane[i]];
            output[at + 4] = (byte)((qa & 15) | ((qb & 15) << 4));
            output[at + 5] = (byte)((qa >> 4) | ((qb >> 4) << 4));
            output[at + 6] = (byte)((qc & 15) | ((qd & 15) << 4));
            output[at + 7] = (byte)((qc >> 4) | ((qd >> 4) << 4));
        }

        return output;
    }

    private static byte[] RebuildBc3(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC3");

        byte[] pairs = BcpackTables.Bc3AlphaPair;
        byte[] quads = BcpackTables.Bc3ColourQuadrant;
        Bc3HistoryEntry[] builtin = BcpackTables.Bc3History;
        byte[] output = new byte[checked(blockCount * 16)];
        Span<Bc3Endpoints> history = stackalloc Bc3Endpoints[256];
        Span<bool> lostHistory = stackalloc bool[256];
        int lostTotal = 0;
        SortedSet<int>? unknownEntries = null;

        ushort[] refLane = lanes[0], alphaLane = lanes[2];
        ushort[] row0 = lanes[3], row1 = lanes[4], row2 = lanes[5], row3 = lanes[6];
        ushort[] rLane = lanes[7], gLane = lanes[8], bLane = lanes[9];
        ushort[] topQuad = lanes[10], bottomQuad = lanes[11];
        Bc3Endpoints defaultEndpoints = new(128, 128, 0, 0, 0, 0, 0, 0);

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, bIdx = a + 1;
            int dist = DecodeZigZag(InterleaveNibbles(refLane[a], refLane[bIdx])) & 255;
            Bc3Endpoints pred;
            bool isLost = false;
            if (dist == 0)
            {
                pred = defaultEndpoints;
            }
            else if (i >= dist)
            {
                int prev = (i - dist) & 255;
                pred = history[prev];
                isLost = lostHistory[prev];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                if (ResolveBuiltinHistoryStatus(h.Status, "BC3", i, entry, provisional, allowUnknownFallback: true, ref unknownEntries))
                {
                    pred = new(h.A0, h.A1, h.R0, h.G0, h.B0, h.R1, h.G1, h.B1);
                }
                else
                {
                    pred = defaultEndpoints;
                    isLost = true;
                }
            }

            lostHistory[i & 255] = isLost;
            if (isLost) lostTotal++;

            byte ea0 = (byte)((pred.A0 + DecodeZigZag(alphaLane[a])) & 255);
            byte ea1 = (byte)((pred.A1 + DecodeZigZag(alphaLane[bIdx])) & 255);
            byte er0 = (byte)((pred.R0 + DecodeZigZag(rLane[a])) & 31);
            byte eg0 = (byte)((pred.G0 + DecodeZigZag(gLane[a])) & 63);
            byte eb0 = (byte)((pred.B0 + DecodeZigZag(bLane[a])) & 31);
            byte er1 = (byte)((pred.R1 + DecodeZigZag(rLane[bIdx])) & 31);
            byte eg1 = (byte)((pred.G1 + DecodeZigZag(gLane[bIdx])) & 63);
            byte eb1 = (byte)((pred.B1 + DecodeZigZag(bLane[bIdx])) & 31);
            history[i & 255] = new(ea0, ea1, er0, eg0, eb0, er1, eg1, eb1);

            ulong alphaBits =
                PackRow12(pairs[row0[a]], pairs[row0[bIdx]])
                | (PackRow12(pairs[row1[a]], pairs[row1[bIdx]]) << 12)
                | (PackRow12(pairs[row2[a]], pairs[row2[bIdx]]) << 24)
                | (PackRow12(pairs[row3[a]], pairs[row3[bIdx]]) << 36);

            int at = i * 16;
            output[at] = ea0;
            output[at + 1] = ea1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), alphaBits);

            ushort col0 = (ushort)((er0 << 11) | (eg0 << 5) | eb0);
            ushort col1 = (ushort)((er1 << 11) | (eg1 << 5) | eb1);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 8, 2), col0);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 10, 2), col1);

            byte qa = quads[topQuad[a]], qb = quads[topQuad[bIdx]];
            byte qc = quads[bottomQuad[a]], qd = quads[bottomQuad[bIdx]];
            output[at + 12] = (byte)((qa & 15) | ((qb & 15) << 4));
            output[at + 13] = (byte)((qa >> 4) | ((qb >> 4) << 4));
            output[at + 14] = (byte)((qc & 15) | ((qd & 15) << 4));
            output[at + 15] = (byte)((qc >> 4) | ((qd >> 4) << 4));
        }

        FinalizeUnknownHistory(unknownEntries, lostTotal, blockCount, "BC3", provisional);
        return output;
    }

    private static byte[] RebuildBc4(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC4");

        byte[] pairs = BcpackTables.Bc4Pair;
        Bc4HistoryEntry[] builtin = BcpackTables.Bc4History;
        byte[] output = new byte[checked(blockCount * 8)];
        Span<Bc4Endpoints> history = stackalloc Bc4Endpoints[256];
        SortedSet<int>? unusedUnknown = null;

        ushort[] refLane = lanes[0], a0Lane = lanes[2], a1Lane = lanes[3];
        ushort[] r0L = lanes[4], r0R = lanes[5], r1L = lanes[6], r1R = lanes[7];
        ushort[] r2L = lanes[8], r2R = lanes[9], r3L = lanes[10], r3R = lanes[11];

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int v = refLane[i];
            int dist = (v & 1) != 0 ? 256 - ((v + 1) >> 1) : (v >> 1);
            Bc4Endpoints pred;
            if (dist == 0)
            {
                pred = new(128, 128);
            }
            else if (i >= dist)
            {
                pred = history[(i - dist) & 255];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                ResolveBuiltinHistoryStatus(h.Status, "BC4", i, entry, provisional, allowUnknownFallback: false, ref unusedUnknown);
                pred = new(h.A0, h.A1);
            }

            byte a0 = (byte)((pred.A0 + DecodeZigZag(a0Lane[i])) & 255);
            byte a1 = (byte)((pred.A1 + DecodeZigZag(a1Lane[i])) & 255);
            history[i & 255] = new(a0, a1);

            ulong indexBits =
                PackRow12(pairs[r0L[i]], pairs[r0R[i]])
                | (PackRow12(pairs[r1L[i]], pairs[r1R[i]]) << 12)
                | (PackRow12(pairs[r2L[i]], pairs[r2R[i]]) << 24)
                | (PackRow12(pairs[r3L[i]], pairs[r3R[i]]) << 36);

            int at = i * 8;
            output[at] = a0;
            output[at + 1] = a1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), indexBits);
        }

        return output;
    }

    private static byte[] RebuildBc5(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC5");

        byte[] pairs = BcpackTables.Bc5Pair;
        Bc5HistoryEntry[] builtin = BcpackTables.Bc5History;
        byte[] output = new byte[checked(blockCount * 16)];
        Span<Bc5Endpoints> history = stackalloc Bc5Endpoints[256];
        Span<bool> lostHistory = stackalloc bool[256];
        int lostTotal = 0;
        SortedSet<int>? unknownEntries = null;

        ushort[] refLane = lanes[0], redLane = lanes[2], greenLane = lanes[3];
        ushort[] r0 = lanes[4], r1 = lanes[5], r2 = lanes[6], r3 = lanes[7];
        ushort[] g0 = lanes[8], g1 = lanes[9], g2 = lanes[10], g3 = lanes[11];
        Bc5Endpoints defaultEndpoints = new(128, 128, 128, 128);

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, bIdx = a + 1;
            int dist = DecodeZigZag(InterleaveNibbles(refLane[a], refLane[bIdx])) & 255;
            Bc5Endpoints pred;
            bool isLost = false;
            if (dist == 0)
            {
                pred = defaultEndpoints;
            }
            else if (i >= dist)
            {
                int prev = (i - dist) & 255;
                pred = history[prev];
                isLost = lostHistory[prev];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                if (ResolveBuiltinHistoryStatus(h.Status, "BC5", i, entry, provisional, allowUnknownFallback: true, ref unknownEntries))
                {
                    pred = new(h.R0, h.R1, h.G0, h.G1);
                }
                else
                {
                    pred = defaultEndpoints;
                    isLost = true;
                }
            }

            lostHistory[i & 255] = isLost;
            if (isLost) lostTotal++;

            byte er0 = (byte)((pred.R0 + DecodeZigZag(redLane[a])) & 255);
            byte er1 = (byte)((pred.R1 + DecodeZigZag(redLane[bIdx])) & 255);
            byte eg0 = (byte)((pred.G0 + DecodeZigZag(greenLane[a])) & 255);
            byte eg1 = (byte)((pred.G1 + DecodeZigZag(greenLane[bIdx])) & 255);
            history[i & 255] = new(er0, er1, eg0, eg1);

            ulong redBits =
                PackRow12(pairs[r0[a]], pairs[r0[bIdx]])
                | (PackRow12(pairs[r1[a]], pairs[r1[bIdx]]) << 12)
                | (PackRow12(pairs[r2[a]], pairs[r2[bIdx]]) << 24)
                | (PackRow12(pairs[r3[a]], pairs[r3[bIdx]]) << 36);

            ulong greenBits =
                PackRow12(pairs[g0[a]], pairs[g0[bIdx]])
                | (PackRow12(pairs[g1[a]], pairs[g1[bIdx]]) << 12)
                | (PackRow12(pairs[g2[a]], pairs[g2[bIdx]]) << 24)
                | (PackRow12(pairs[g3[a]], pairs[g3[bIdx]]) << 36);

            int at = i * 16;
            output[at] = er0;
            output[at + 1] = er1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), redBits);
            output[at + 8] = eg0;
            output[at + 9] = eg1;
            WriteUInt48LittleEndian(output.AsSpan(at + 10, 6), greenBits);
        }

        FinalizeUnknownHistory(unknownEntries, lostTotal, blockCount, "BC5", provisional);
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeZigZag(int v) => (v & 1) != 0 ? -((v + 1) >> 1) : (v >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InterleaveNibbles(int lowNibble, int highNibble)
        => (lowNibble & 1) | ((highNibble & 1) << 1)
         | ((lowNibble & 2) << 1) | ((highNibble & 2) << 2)
         | ((lowNibble & 4) << 2) | ((highNibble & 4) << 3)
         | ((lowNibble & 8) << 3) | ((highNibble & 8) << 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackRow12(byte left, byte right) => (uint)(left | (right << 6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LooseUnknownBlockLimit(int blockCount) => Math.Max(2, blockCount / 1024);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt48LittleEndian(Span<byte> dest, ulong value)
    {
        dest[0] = (byte)value;
        dest[1] = (byte)(value >> 8);
        dest[2] = (byte)(value >> 16);
        dest[3] = (byte)(value >> 24);
        dest[4] = (byte)(value >> 32);
        dest[5] = (byte)(value >> 40);
    }

    private static RansModel[][] BuildModels()
    {
        var table = BcpackTables.ProbTables;
        var models = new RansModel[MaxWidth][];
        for (int w = 1; w <= MaxWidth; w++)
        {
            var row = new RansModel[16];
            for (int q = 0; q < 16; q++)
                row[q] = new RansModel(w, table[w - 1][q]);
            models[w - 1] = row;
        }
        return models;
    }

    private readonly record struct Bc1Endpoints(byte R0, byte G0, byte B0, byte R1, byte G1, byte B1);
    private readonly record struct Bc3Endpoints(byte A0, byte A1, byte R0, byte G0, byte B0, byte R1, byte G1, byte B1);
    private readonly record struct Bc4Endpoints(byte A0, byte A1);
    private readonly record struct Bc5Endpoints(byte R0, byte R1, byte G0, byte G1);

    private sealed class RansModel
    {
        private readonly int _width;
        private readonly int[] _freq;
        private readonly int[] _base;
        private readonly ushort[] _symFreq;
        private readonly ushort[] _symBase;

        internal RansModel(int width, int[] freq)
        {
            _width = width;
            _freq = freq;
            _base = new int[freq.Length];
            int acc = 0;
            for (int p = 0; p < freq.Length; p++)
            {
                if (freq[p] <= 0)
                    throw new InvalidDataException($"Probability table ({width}) has non-positive band frequency.");
                _base[p] = acc;
                acc += freq[p] * (p == 0 ? 1 : 1 << (p - 1));
            }
            if (acc != 65536)
                throw new InvalidDataException($"Probability table ({width}) does not sum to 65536 (got {acc}).");

            int symCount = 1 << width;
            _symFreq = new ushort[symCount];
            _symBase = new ushort[symCount];
            for (int s = 0; s < symCount; s++)
            {
                int p = s == 0 ? 0 : BitOperations.Log2((uint)s) + 1;
                int f = freq[p];
                int c = _base[p] + f * (s - (p == 0 ? 0 : 1 << (p - 1)));
                if (c == 0)
                {
                    f -= 1;
                    c = 1;
                }
                _symFreq[s] = (ushort)f;
                _symBase[s] = (ushort)c;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Lookup(int cf)
        {
            int[] b = _base;
            if (cf < b[1]) return 0;
            int p = 1;
            int w = _width;
            while (p < w && cf >= b[p + 1]) p++;
            return (1 << (p - 1)) + (int)((uint)(cf - b[p]) / (uint)_freq[p]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Advance(int s, ulong x)
            => (ulong)_symFreq[s] * (x >> 16) + (x & 0xFFFFu) - (ulong)_symBase[s];
    }
}
