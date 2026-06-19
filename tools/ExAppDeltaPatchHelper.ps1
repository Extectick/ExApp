$script:ExAppDeltaPatchHelperLoaded = $script:ExAppDeltaPatchHelperLoaded -as [bool]
if ($script:ExAppDeltaPatchHelperLoaded) {
    return
}

Add-Type -Language CSharp -TypeDefinition @'
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExApp.Tools;

public static class DeltaPatchHelper
{
    private const int MinChunkSize = 8 * 1024;
    private const int AverageChunkBits = 15;
    private const int AverageChunkSize = 1 << AverageChunkBits;
    private const int MaxChunkSize = 64 * 1024;
    private const ulong BoundaryMask = (1UL << AverageChunkBits) - 1UL;
    private static readonly ulong[] Gear = BuildGearTable();

    public static string? CreatePatch(
        string relativePath,
        string basePath,
        string targetPath,
        string dataPath,
        string dataRelativePath,
        string targetSha256,
        double maxPatchRatio)
    {
        if (!File.Exists(basePath) || !File.Exists(targetPath))
        {
            return null;
        }

        var baseBytes = File.ReadAllBytes(basePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        if (targetBytes.Length < MaxChunkSize * 2 || baseBytes.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        var operations = new List<PatchOperationDto>();
        long dataOffset = 0;
        var copiedBytes = 0L;
        using (var dataStream = File.Create(dataPath))
        {
            var prefixLength = CommonPrefixLength(baseBytes, targetBytes);
            var suffixLength = CommonSuffixLength(baseBytes, targetBytes, prefixLength);
            if (prefixLength > 0)
            {
                AddOperation(operations, "copy", 0, 0, prefixLength);
                copiedBytes += prefixLength;
            }

            var baseMiddleOffset = prefixLength;
            var baseMiddleLength = baseBytes.Length - prefixLength - suffixLength;
            var targetMiddleOffset = prefixLength;
            var targetMiddleLength = targetBytes.Length - prefixLength - suffixLength;

            if (targetMiddleLength > 0)
            {
                if (baseMiddleLength >= MinChunkSize && targetMiddleLength >= MinChunkSize)
                {
                    var baseChunks = Chunk(baseBytes, baseMiddleOffset, baseMiddleLength);
                    var targetChunks = Chunk(targetBytes, targetMiddleOffset, targetMiddleLength);
                    var chunkMap = new Dictionary<string, Queue<ChunkInfo>>(StringComparer.Ordinal);
                    foreach (var chunk in baseChunks)
                    {
                        var key = $"{chunk.Hash}|{chunk.Length}";
                        if (!chunkMap.TryGetValue(key, out var queue))
                        {
                            queue = new Queue<ChunkInfo>();
                            chunkMap[key] = queue;
                        }

                        queue.Enqueue(chunk);
                    }

                    foreach (var targetChunk in targetChunks)
                    {
                        var key = $"{targetChunk.Hash}|{targetChunk.Length}";
                        if (chunkMap.TryGetValue(key, out var queue) &&
                            TryDequeueMatchingChunk(queue, baseBytes, targetBytes, targetChunk, out var baseChunk))
                        {
                            AddOperation(
                                operations,
                                "copy",
                                baseChunk.Offset,
                                0,
                                targetChunk.Length);
                            copiedBytes += targetChunk.Length;
                            continue;
                        }

                        dataStream.Write(targetBytes, targetChunk.Offset, targetChunk.Length);
                        AddOperation(
                            operations,
                            "data",
                            0,
                            dataOffset,
                            targetChunk.Length);
                        dataOffset += targetChunk.Length;
                    }
                }
                else
                {
                    dataStream.Write(targetBytes, targetMiddleOffset, targetMiddleLength);
                    AddOperation(
                        operations,
                        "data",
                        0,
                        dataOffset,
                        targetMiddleLength);
                    dataOffset += targetMiddleLength;
                }
            }

            if (suffixLength > 0)
            {
                AddOperation(operations, "copy", baseBytes.Length - suffixLength, 0, suffixLength);
                copiedBytes += suffixLength;
            }
        }

        var estimatedPatchSize = dataOffset + operations.Count * 96L;
        var maximumUsefulPatchSize = (long)(targetBytes.LongLength * maxPatchRatio);
        if (copiedBytes == 0 ||
            operations.Count == 0 ||
            estimatedPatchSize >= maximumUsefulPatchSize)
        {
            File.Delete(dataPath);
            return null;
        }

        var patch = new PatchDto
        {
            Path = relativePath,
            BlockSize = AverageChunkSize,
            BaseSize = baseBytes.LongLength,
            BaseSha256 = Sha256Hex(baseBytes),
            TargetSize = targetBytes.LongLength,
            TargetSha256 = targetSha256,
            DataPath = dataRelativePath.Replace('\\', '/'),
            Operations = operations
        };

        return JsonSerializer.Serialize(patch);
    }

    private static int CommonPrefixLength(byte[] baseBytes, byte[] targetBytes)
    {
        var length = Math.Min(baseBytes.Length, targetBytes.Length);
        var index = 0;
        while (index < length && baseBytes[index] == targetBytes[index])
        {
            index++;
        }

        return index;
    }

    private static int CommonSuffixLength(byte[] baseBytes, byte[] targetBytes, int prefixLength)
    {
        var maxLength = Math.Min(baseBytes.Length, targetBytes.Length) - prefixLength;
        var length = 0;
        while (length < maxLength &&
               baseBytes[baseBytes.Length - length - 1] == targetBytes[targetBytes.Length - length - 1])
        {
            length++;
        }

        return length;
    }

    private static List<ChunkInfo> Chunk(byte[] bytes, int start, int count)
    {
        var chunks = new List<ChunkInfo>();
        var chunkStart = start;
        var hash = 0UL;
        var end = start + count;
        for (var index = start; index < end; index++)
        {
            hash = (hash << 1) + Gear[bytes[index]];
            var length = index - chunkStart + 1;
            if (length < MinChunkSize)
            {
                continue;
            }

            if ((hash & BoundaryMask) != 0 && length < MaxChunkSize)
            {
                continue;
            }

            chunks.Add(new ChunkInfo(chunkStart, length, Sha256Hex(bytes, chunkStart, length)));
            chunkStart = index + 1;
            hash = 0;
        }

        if (chunkStart < end)
        {
            chunks.Add(new ChunkInfo(chunkStart, end - chunkStart, Sha256Hex(bytes, chunkStart, end - chunkStart)));
        }

        return chunks;
    }

    private static bool TryDequeueMatchingChunk(
        Queue<ChunkInfo> queue,
        byte[] baseBytes,
        byte[] targetBytes,
        ChunkInfo targetChunk,
        out ChunkInfo baseChunk)
    {
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (candidate.Length == targetChunk.Length &&
                baseBytes.AsSpan(candidate.Offset, candidate.Length).SequenceEqual(targetBytes.AsSpan(targetChunk.Offset, targetChunk.Length)))
            {
                baseChunk = candidate;
                return true;
            }
        }

        baseChunk = default;
        return false;
    }

    private static void AddOperation(
        List<PatchOperationDto> operations,
        string type,
        long offset,
        long dataOffset,
        int length)
    {
        if (operations.Count > 0)
        {
            var last = operations[^1];
            if (type == "copy" &&
                last.Type == "copy" &&
                last.Offset + last.Length == offset)
            {
                last.Length += length;
                return;
            }

            if (type == "data" &&
                last.Type == "data" &&
                last.DataOffset + last.Length == dataOffset)
            {
                last.Length += length;
                return;
            }
        }

        operations.Add(new PatchOperationDto
        {
            Type = type,
            Offset = offset,
            DataOffset = dataOffset,
            Length = length
        });
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Sha256Hex(byte[] bytes, int offset, int length)
    {
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, length))).ToLowerInvariant();
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        var state = 0x9E3779B97F4A7C15UL;
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = SplitMix64(ref state);
        }

        return table;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private readonly record struct ChunkInfo(int Offset, int Length, string Hash);

    private sealed class PatchDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("blockSize")]
        public int BlockSize { get; init; }

        [JsonPropertyName("baseSize")]
        public long BaseSize { get; init; }

        [JsonPropertyName("baseSha256")]
        public string BaseSha256 { get; init; } = string.Empty;

        [JsonPropertyName("targetSize")]
        public long TargetSize { get; init; }

        [JsonPropertyName("targetSha256")]
        public string TargetSha256 { get; init; } = string.Empty;

        [JsonPropertyName("dataPath")]
        public string DataPath { get; init; } = string.Empty;

        [JsonPropertyName("operations")]
        public List<PatchOperationDto> Operations { get; init; } = [];
    }

    private sealed class PatchOperationDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("offset")]
        public long Offset { get; init; }

        [JsonPropertyName("dataOffset")]
        public long DataOffset { get; init; }

        [JsonPropertyName("length")]
        public int Length { get; set; }
    }
}
'@

$script:ExAppDeltaPatchHelperLoaded = $true
