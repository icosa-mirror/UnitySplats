// Copyright (c) 2026 The Open Brush Authors
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Gsplat
{
    // Cache only ORIGINAL bounds. Threshold changes must never change the reference size.
    internal static class GsplatBoundsCache
    {
        const int Version = 1;

        internal static string CachePath(string source, SourceCoordinates coordinates, string directory)
        {
            using var sha = SHA256.Create();
            byte[] key = sha.ComputeHash(Encoding.UTF8.GetBytes($"{Path.GetFullPath(source)}\n{coordinates}"));
            return Path.Combine(directory, $"{BitConverter.ToString(key).Replace("-", "")}.bounds");
        }

        internal static bool TryRead(string source, SourceCoordinates coordinates, string directory, out Bounds bounds)
        {
            bounds = default;
            try
            {
                var info = new FileInfo(source);
                using var reader = new BinaryReader(File.OpenRead(CachePath(source, coordinates, directory)));
                if (reader.ReadInt32() != Version || reader.ReadInt64() != info.Length ||
                    reader.ReadInt64() != info.LastWriteTimeUtc.Ticks) return false;
                var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var size = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!Finite(center) || !Finite(size) || size.x < 0 || size.y < 0 || size.z < 0 ||
                    reader.BaseStream.Position != reader.BaseStream.Length) return false;
                bounds = new Bounds(center, size);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return false;
            }
        }

        internal static void Write(string source, SourceCoordinates coordinates, string directory,
            long length, long timestamp, Bounds bounds)
        {
            string temporary = null;
            try
            {
                var info = new FileInfo(source);
                if (info.Length != length || info.LastWriteTimeUtc.Ticks != timestamp) return;
                Directory.CreateDirectory(directory);
                string target = CachePath(source, coordinates, directory);
                temporary = $"{target}.{Guid.NewGuid():N}.tmp";
                using (var writer = new BinaryWriter(File.Create(temporary)))
                {
                    writer.Write(Version);
                    writer.Write(length);
                    writer.Write(timestamp);
                    writer.Write(bounds.center.x); writer.Write(bounds.center.y); writer.Write(bounds.center.z);
                    writer.Write(bounds.size.x); writer.Write(bounds.size.y); writer.Write(bounds.size.z);
                }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Debug.LogWarning($"[SPLAT_PRUNE_20260907] Bounds cache could not be saved: {e.Message}");
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        static bool Finite(Vector3 v) => GsplatImportFilter.Finite(v.x) &&
            GsplatImportFilter.Finite(v.y) && GsplatImportFilter.Finite(v.z);
    }
}
