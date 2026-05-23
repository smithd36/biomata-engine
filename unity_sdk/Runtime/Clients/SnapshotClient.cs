// Biomata.SDK — SnapshotClient.cs
// Save / restore complete simulation state; persist snapshots to disk.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;
using UnityEngine;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Save and restore complete simulation state.
    ///
    /// Snapshot bytes are opaque (pickle-serialized Python objects on the server).
    /// Pass them verbatim to <see cref="RestoreAsync"/>. File persistence uses
    /// <c>Application.persistentDataPath</c> by default.
    /// </summary>
    public class SnapshotClient
    {
        private readonly ITransport _transport;

        internal SnapshotClient(ITransport transport)
        {
            _transport = transport;
        }

        // ── Capture / Restore ─────────────────────────────────────────────────

        public Task<SnapshotData> CaptureAsync(CancellationToken ct = default)
            => _transport.SnapshotAsync(ct);

        public async Task RestoreAsync(SnapshotData snapshot, CancellationToken ct = default)
        {
            if (snapshot == null)      throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Data == null) throw new ArgumentException("SnapshotData.Data is null", nameof(snapshot));
            await _transport.RestoreAsync(snapshot.Data, ct);
        }

        // ── File persistence (unchanged from the prior implementation) ────────

        public async Task SaveToFileAsync(SnapshotData snapshot, string fileName)
        {
            if (snapshot?.Data == null) throw new ArgumentNullException(nameof(snapshot));
            var path = ResolvePath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteAllBytesAsync(path, snapshot.Data);
            snapshot.FilePath = path;
            Debug.Log($"[BiomataSDK] Snapshot saved to {path} (tick {snapshot.Tick})");
        }

        public async Task<SnapshotData> LoadFromFileAsync(string fileName)
        {
            var path = ResolvePath(fileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Snapshot file not found: {path}");
            var data = await ReadAllBytesAsync(path);
            Debug.Log($"[BiomataSDK] Snapshot loaded from {path} ({data.Length} bytes)");
            return new SnapshotData { Data = data, IsFromFile = true, FilePath = path };
        }

        public bool FileExists(string fileName) => File.Exists(ResolvePath(fileName));

        public void DeleteFile(string fileName)
        {
            var path = ResolvePath(fileName);
            if (File.Exists(path)) File.Delete(path);
        }

        public async Task<SnapshotData> CaptureAndSaveAsync(string fileName, CancellationToken ct = default)
        {
            var snap = await CaptureAsync(ct);
            await SaveToFileAsync(snap, fileName);
            return snap;
        }

        public async Task LoadAndRestoreAsync(string fileName, CancellationToken ct = default)
        {
            var snap = await LoadFromFileAsync(fileName);
            await RestoreAsync(snap, ct);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string ResolvePath(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return fileName;
            return Path.Combine(Application.persistentDataPath, "biomata", fileName);
        }

        private static async Task WriteAllBytesAsync(string path, byte[] data)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(data, 0, data.Length);
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var buf = new byte[fs.Length];
            await fs.ReadAsync(buf, 0, buf.Length);
            return buf;
        }
    }
}
