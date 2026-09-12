// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2019-2024, Jiaqi (0x7c13) Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Windows.Storage;

    /// <summary>
    /// Provides bounded-memory access to a text file split at encoding-safe byte offsets.
    /// It is intended to back a virtualized editor viewport; only requested chunks are decoded.
    /// </summary>
    public sealed class ChunkedTextDocument : IDisposable
    {
        public const int DefaultChunkSize = 1024 * 1024;
        private const int DefaultCacheCapacity = 8;
        private const int ReadBufferSize = 128 * 1024;

        private readonly StorageFile _sourceFile;
        private readonly Encoding _encoding;
        private readonly IReadOnlyList<Chunk> _chunks;
        private readonly int _cacheCapacity;
        private readonly bool _hasByteOrderMark;
        private readonly Dictionary<int, LinkedListNode<CachedChunk>> _cache = new Dictionary<int, LinkedListNode<CachedChunk>>();
        private readonly LinkedList<CachedChunk> _leastRecentlyUsed = new LinkedList<CachedChunk>();
        private readonly Dictionary<int, string> _modifiedChunks = new Dictionary<int, string>();
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        private bool _disposed;

        private ChunkedTextDocument(
            StorageFile sourceFile,
            Encoding encoding,
            IReadOnlyList<Chunk> chunks,
            int cacheCapacity,
            bool hasByteOrderMark)
        {
            _sourceFile = sourceFile;
            _encoding = encoding;
            _chunks = chunks;
            _cacheCapacity = cacheCapacity;
            _hasByteOrderMark = hasByteOrderMark;
        }

        public StorageFile SourceFile => _sourceFile;

        public Encoding Encoding => _encoding;

        public int ChunkCount => _chunks.Count;

        public ulong FileSize { get; private set; }

        public bool HasUnsavedChanges => _modifiedChunks.Count != 0;

        public ChunkInfo GetChunkInfo(int index)
        {
            ThrowIfDisposed();
            ValidateChunkIndex(index);
            var chunk = _chunks[index];
            return new ChunkInfo(index, chunk.Offset, chunk.Length);
        }

        public static bool CanVirtualize(Encoding encoding)
        {
            if (encoding == null) return false;

            return encoding.IsSingleByte || encoding is UTF8Encoding ||
                   encoding.CodePage == Encoding.Unicode.CodePage ||
                   encoding.CodePage == Encoding.BigEndianUnicode.CodePage ||
                   encoding.CodePage == Encoding.UTF32.CodePage ||
                   encoding.CodePage == 12001;
        }

        public static async Task<ChunkedTextDocument> CreateAsync(
            StorageFile sourceFile,
            Encoding encoding,
            int chunkSize = DefaultChunkSize,
            int cacheCapacity = DefaultCacheCapacity)
        {
            if (sourceFile == null) throw new ArgumentNullException(nameof(sourceFile));
            if (!CanVirtualize(encoding))
            {
                throw new NotSupportedException("Chunked text documents require a single-byte, UTF-8, UTF-16, or UTF-32 encoding.");
            }
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (cacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

            var properties = await sourceFile.GetBasicPropertiesAsync();
            var chunks = await BuildChunkIndexAsync(sourceFile, encoding, chunkSize).ConfigureAwait(false);
            var hasByteOrderMark = await HasByteOrderMarkAsync(sourceFile, encoding).ConfigureAwait(false);

            return new ChunkedTextDocument(sourceFile, encoding, chunks, cacheCapacity, hasByteOrderMark)
            {
                FileSize = properties.Size
            };
        }

        public async Task<string> GetChunkAsync(int index)
        {
            ThrowIfDisposed();
            ValidateChunkIndex(index);

            await _cacheLock.WaitAsync();
            try
            {
                if (_modifiedChunks.TryGetValue(index, out var modified)) return modified;
                if (_cache.TryGetValue(index, out var cached))
                {
                    _leastRecentlyUsed.Remove(cached);
                    _leastRecentlyUsed.AddFirst(cached);
                    return cached.Value.Content;
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            var content = await ReadChunkAsync(_chunks[index], index == 0).ConfigureAwait(false);

            await _cacheLock.WaitAsync();
            try
            {
                if (_modifiedChunks.TryGetValue(index, out var modified)) return modified;
                if (_cache.TryGetValue(index, out var cached)) return cached.Value.Content;

                var node = _leastRecentlyUsed.AddFirst(new CachedChunk(index, content));
                _cache[index] = node;
                if (_cache.Count > _cacheCapacity)
                {
                    var leastRecent = _leastRecentlyUsed.Last;
                    _cache.Remove(leastRecent.Value.Index);
                    _leastRecentlyUsed.RemoveLast();
                }

                return content;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Replaces one loaded viewport chunk. Saving writes unmodified chunks directly from disk.
        /// </summary>
        public async Task ReplaceChunkAsync(int index, string content)
        {
            ThrowIfDisposed();
            ValidateChunkIndex(index);
            if (content == null) throw new ArgumentNullException(nameof(content));

            await _cacheLock.WaitAsync();
            try
            {
                _modifiedChunks[index] = content;
                if (_cache.TryGetValue(index, out var cached))
                {
                    cached.Value.Content = content;
                    _leastRecentlyUsed.Remove(cached);
                    _leastRecentlyUsed.AddFirst(cached);
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Saves a chunked document without materializing the unmodified parts in memory.
        /// The destination must differ from the source so callers can replace it atomically.
        /// </summary>
        public async Task SaveAsAsync(StorageFile destinationFile)
        {
            ThrowIfDisposed();
            if (destinationFile == null) throw new ArgumentNullException(nameof(destinationFile));
            if (string.Equals(destinationFile.Path, _sourceFile.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Save to a temporary file, then replace the source file.", nameof(destinationFile));
            }

            Dictionary<int, string> modifiedChunks;
            await _cacheLock.WaitAsync();
            try
            {
                modifiedChunks = new Dictionary<int, string>(_modifiedChunks);
            }
            finally
            {
                _cacheLock.Release();
            }

            using (var source = await _sourceFile.OpenStreamForReadAsync().ConfigureAwait(false))
            using (var destination = await destinationFile.OpenStreamForWriteAsync().ConfigureAwait(false))
            {
                destination.SetLength(0);
                for (var index = 0; index < _chunks.Count; index++)
                {
                    if (modifiedChunks.TryGetValue(index, out var content))
                    {
                        if (index == 0 && _hasByteOrderMark)
                        {
                            var preamble = _encoding.GetPreamble();
                        await destination.WriteAsync(preamble, 0, preamble.Length).ConfigureAwait(false);
                        }

                        var encoded = _encoding.GetBytes(content);
                        await destination.WriteAsync(encoded, 0, encoded.Length).ConfigureAwait(false);
                    }
                    else
                    {
                        await CopyChunkAsync(source, destination, _chunks[index]).ConfigureAwait(false);
                    }
                }

                await destination.FlushAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cacheLock.Dispose();
            _cache.Clear();
            _leastRecentlyUsed.Clear();
            _modifiedChunks.Clear();
            _disposed = true;
        }

        private async Task<string> ReadChunkAsync(Chunk chunk, bool detectByteOrderMark)
        {
            var bytes = new byte[chunk.Length];
            using (var stream = await _sourceFile.OpenStreamForReadAsync().ConfigureAwait(false))
            {
                stream.Position = chunk.Offset;
                await ReadExactlyAsync(stream, bytes, bytes.Length).ConfigureAwait(false);
            }

            var byteOffset = detectByteOrderMark && _hasByteOrderMark ? _encoding.GetPreamble().Length : 0;
            return _encoding.GetString(bytes, byteOffset, bytes.Length - byteOffset);
        }

        private static async Task<IReadOnlyList<Chunk>> BuildChunkIndexAsync(StorageFile file, Encoding encoding, int chunkSize)
        {
            var chunks = new List<Chunk>();
            var lineFeed = encoding.GetBytes("\n");
            var buffer = new byte[ReadBufferSize];
            var currentOffset = 0L;
            var position = 0L;
            var lineFeedMatchLength = 0;

            using (var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false))
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    for (var offset = 0; offset < bytesRead; offset++, position++)
                    {
                        var value = buffer[offset];
                        lineFeedMatchLength = value == lineFeed[lineFeedMatchLength]
                            ? lineFeedMatchLength + 1
                            : value == lineFeed[0] ? 1 : 0;

                        var chunkLength = position + 1 - currentOffset;
                        if (lineFeedMatchLength == lineFeed.Length)
                        {
                            if (chunkLength >= chunkSize &&
                                IsCharacterBoundary(position + 1 - lineFeed.Length, encoding))
                            {
                                chunks.Add(new Chunk(currentOffset, checked((int)chunkLength)));
                                currentOffset = position + 1;
                            }

                            lineFeedMatchLength = 0;
                        }
                        else if (chunkLength >= chunkSize && lineFeedMatchLength == 0 &&
                                 IsChunkBoundary(buffer[offset], position, encoding))
                        {
                            var lengthBeforeCurrentCharacter = checked((int)(position - currentOffset));
                            if (lengthBeforeCurrentCharacter > 0)
                            {
                                chunks.Add(new Chunk(currentOffset, lengthBeforeCurrentCharacter));
                                currentOffset = position;
                            }
                        }
                    }
                }
            }

            if (position > currentOffset)
            {
                chunks.Add(new Chunk(currentOffset, checked((int)(position - currentOffset))));
            }
            else if (chunks.Count == 0)
            {
                chunks.Add(new Chunk(0, 0));
            }

            return chunks;
        }

        private static bool IsChunkBoundary(byte value, long position, Encoding encoding)
        {
            if (encoding is UTF8Encoding)
            {
                return (value & 0xC0) != 0x80;
            }

            return IsCharacterBoundary(position, encoding);
        }

        private static bool IsCharacterBoundary(long position, Encoding encoding)
        {

            if (encoding.CodePage == Encoding.Unicode.CodePage || encoding.CodePage == Encoding.BigEndianUnicode.CodePage)
            {
                return position % 2 == 0;
            }

            if (encoding.CodePage == Encoding.UTF32.CodePage || encoding.CodePage == 12001)
            {
                return position % 4 == 0;
            }

            return true;
        }

        private static async Task<bool> HasByteOrderMarkAsync(StorageFile file, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            if (preamble.Length == 0) return false;

            var header = new byte[preamble.Length];
            using (var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false))
            {
                var bytesRead = await stream.ReadAsync(header, 0, header.Length).ConfigureAwait(false);
                if (bytesRead != header.Length) return false;
            }

            for (var index = 0; index < preamble.Length; index++)
            {
                if (header[index] != preamble[index]) return false;
            }

            return true;
        }

        private static async Task CopyChunkAsync(Stream source, Stream destination, Chunk chunk)
        {
            source.Position = chunk.Offset;
            var buffer = new byte[Math.Min(ReadBufferSize, chunk.Length)];
            var remaining = chunk.Length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The source file changed while it was being saved.");
                await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                remaining -= read;
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The source file changed while it was being read.");
                offset += read;
            }
        }

        private void ValidateChunkIndex(int index)
        {
            if (index < 0 || index >= _chunks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ChunkedTextDocument));
        }

        public struct ChunkInfo
        {
            public ChunkInfo(int index, long offset, int length)
            {
                Index = index;
                Offset = offset;
                Length = length;
            }

            public int Index { get; }

            public long Offset { get; }

            public int Length { get; }
        }

        private struct Chunk
        {
            public Chunk(long offset, int length)
            {
                Offset = offset;
                Length = length;
            }

            public long Offset { get; }

            public int Length { get; }
        }

        private sealed class CachedChunk
        {
            public CachedChunk(int index, string content)
            {
                Index = index;
                Content = content;
            }

            public int Index { get; }

            public string Content { get; set; }
        }
    }
}
