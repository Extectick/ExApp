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

        var baseInfo = new FileInfo(basePath);
        var targetInfo = new FileInfo(targetPath);
        if (targetInfo.Length < MaxChunkSize * 2 || baseInfo.Length == 0)
        {
            return null;
        }

        var baseSha256 = Sha256Hex(basePath);
        var baseChunks = BuildChunkIndex(basePath);
        if (baseChunks.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        var operations = new List<PatchOperationDto>();
        long dataOffset = 0;
        var copiedBytes = 0L;
        using (var targetStream = File.OpenRead(targetPath))
        using (var baseStream = File.OpenRead(basePath))
        using (var dataStream = File.Create(dataPath))
        {
            foreach (var targetChunk in EnumerateChunks(targetStream))
            {
                var key = GetChunkKey(targetChunk.Hash, targetChunk.Length);
                if (baseChunks.TryGetValue(key, out var queue) &&
                    TryDequeueMatchingChunk(queue, baseStream, targetChunk.Bytes, targetChunk.Length, out var baseChunk))
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

                dataStream.Write(targetChunk.Bytes, 0, targetChunk.Length);
                AddOperation(
                    operations,
                    "data",
                    0,
                    dataOffset,
                    targetChunk.Length);
                dataOffset += targetChunk.Length;
            }
        }

        var estimatedPatchSize = dataOffset + operations.Count * 96L;
        var maximumUsefulPatchSize = (long)(targetInfo.Length * maxPatchRatio);
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
            BaseSize = baseInfo.Length,
            BaseSha256 = baseSha256,
            TargetSize = targetInfo.Length,
            TargetSha256 = targetSha256,
            DataPath = dataRelativePath.Replace('\\', '/'),
            Operations = operations
        };

        return JsonSerializer.Serialize(patch);
    }

    private static Dictionary<string, Queue<ChunkInfo>> BuildChunkIndex(string path)
    {
        var chunks = new Dictionary<string, Queue<ChunkInfo>>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        foreach (var chunk in EnumerateChunks(stream))
        {
            var key = GetChunkKey(chunk.Hash, chunk.Length);
            if (!chunks.TryGetValue(key, out var queue))
            {
                queue = new Queue<ChunkInfo>();
                chunks[key] = queue;
            }

            queue.Enqueue(new ChunkInfo(chunk.Offset, chunk.Length, chunk.Hash));
        }

        return chunks;
    }

    private static IEnumerable<ChunkBytes> EnumerateChunks(Stream stream)
    {
        var readBuffer = new byte[81920];
        var chunkBuffer = new byte[MaxChunkSize];
        var chunkStart = 0L;
        var streamOffset = 0L;
        var chunkLength = 0;
        var hash = 0UL;
        int bytesRead;
        while ((bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            for (var index = 0; index < bytesRead; index++)
            {
                var value = readBuffer[index];
                chunkBuffer[chunkLength++] = value;
                hash = (hash << 1) + Gear[value];
                streamOffset++;

                if (chunkLength < MinChunkSize &&
                    chunkLength < MaxChunkSize)
                {
                    continue;
                }

                if ((hash & BoundaryMask) != 0 &&
                    chunkLength < MaxChunkSize)
                {
                    continue;
                }

                yield return CreateChunkBytes(chunkStart, chunkBuffer, chunkLength);
                chunkStart = streamOffset;
                chunkLength = 0;
                hash = 0;
            }
        }

        if (chunkLength > 0)
        {
            yield return CreateChunkBytes(chunkStart, chunkBuffer, chunkLength);
        }
    }

    private static bool TryDequeueMatchingChunk(
        Queue<ChunkInfo> queue,
        FileStream baseStream,
        byte[] targetBytes,
        int targetLength,
        out ChunkInfo baseChunk)
    {
        var candidateBuffer = new byte[targetLength];
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (candidate.Length != targetLength)
            {
                continue;
            }

            baseStream.Seek(candidate.Offset, SeekOrigin.Begin);
            baseStream.ReadExactly(candidateBuffer.AsSpan(0, targetLength));
            if (candidateBuffer.AsSpan(0, targetLength).SequenceEqual(targetBytes.AsSpan(0, targetLength)))
            {
                baseChunk = candidate;
                return true;
            }
        }

        baseChunk = default;
        return false;
    }

    private static ChunkBytes CreateChunkBytes(long offset, byte[] buffer, int length)
    {
        var bytes = new byte[length];
        Buffer.BlockCopy(buffer, 0, bytes, 0, length);
        return new ChunkBytes(offset, length, Sha256Hex(bytes), bytes);
    }

    private static string GetChunkKey(string hash, int length) => $"{hash}|{length}";

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

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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

    private readonly record struct ChunkInfo(long Offset, int Length, string Hash);

    private readonly record struct ChunkBytes(long Offset, int Length, string Hash, byte[] Bytes);

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
