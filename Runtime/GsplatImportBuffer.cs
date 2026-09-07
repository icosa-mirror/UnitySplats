// Copyright (c) 2026 The Open Brush Authors
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Gsplat
{
    // Each existing decoder worker owns a growing buffer. No per-splat locks are needed.
    // Completion restores source order, including matching SH data, before GPU allocation.
    internal sealed class GsplatImportBuffer
    {
        internal int Count;
        internal int[] SourceIndices = Array.Empty<int>();
        internal uint4[] Splats = Array.Empty<uint4>();
        internal uint[] SH1 = Array.Empty<uint>(), SH2 = Array.Empty<uint>();
        internal uint[] SH3 = Array.Empty<uint>(), SH4 = Array.Empty<uint>();
        readonly byte m_bands;

        internal GsplatImportBuffer(byte bands) => m_bands = bands;

        internal int Append(int sourceIndex)
        {
            if (Count == Splats.Length)
            {
                int capacity = Splats.Length == 0 ? 256 : checked(Splats.Length * 2);
                Array.Resize(ref SourceIndices, capacity);
                Array.Resize(ref Splats, capacity);
                if (m_bands >= 1) Array.Resize(ref SH1, checked(capacity * 2));
                if (m_bands >= 2) Array.Resize(ref SH2, checked(capacity * 4));
                if (m_bands >= 3) Array.Resize(ref SH3, checked(capacity * 4));
                if (m_bands >= 4) Array.Resize(ref SH4, checked(capacity * 4));
            }
            SourceIndices[Count] = sourceIndex;
            return Count++;
        }
    }

    internal sealed class GsplatImportContext
    {
        internal readonly bool BoundsOnly;
        internal readonly Bounds? SourceBounds;
        internal int SourceCount;
        readonly double m_logScaleThreshold;
        readonly double m_opacityLogitThreshold;
        readonly List<GsplatImportBuffer> m_buffers = new();

        internal GsplatImportContext() => BoundsOnly = true;

        internal GsplatImportContext(GsplatImportFilter filter, Bounds bounds)
        {
            SourceBounds = bounds;
            m_logScaleThreshold = Math.Log(filter.ScaleThreshold(bounds));
            m_opacityLogitThreshold = filter.MaxOpacity == 0 ? double.NegativeInfinity
                : filter.MaxOpacity == 1 ? double.PositiveInfinity
                : Math.Log((double)filter.MaxOpacity / (1.0 - filter.MaxOpacity));
        }

        internal bool Removes(Vector3 logScale, float opacityLogit) =>
            opacityLogit < m_opacityLogitThreshold &&
            Math.Max(logScale.x, Math.Max(logScale.y, logScale.z)) > m_logScaleThreshold;

        internal GsplatImportBuffer CreateBuffer(byte bands)
        {
            if (BoundsOnly) return null;
            var buffer = new GsplatImportBuffer(bands);
            lock (m_buffers) m_buffers.Add(buffer);
            return buffer;
        }

        readonly struct Location : IComparable<Location>
        {
            internal readonly GsplatImportBuffer Buffer;
            internal readonly int Offset;
            internal Location(GsplatImportBuffer buffer, int offset) { Buffer = buffer; Offset = offset; }
            public int CompareTo(Location other) =>
                Buffer.SourceIndices[Offset].CompareTo(other.Buffer.SourceIndices[other.Offset]);
        }

        internal void Complete(GsplatAssetSpark asset)
        {
            int count = 0;
            foreach (var buffer in m_buffers) count = checked(count + buffer.Count);
            var order = new Location[count];
            int next = 0;
            foreach (var buffer in m_buffers)
                for (int i = 0; i < buffer.Count; ++i) order[next++] = new Location(buffer, i);
            Array.Sort(order);
            asset.SplatCount = (uint)count;
            asset.Allocate();
            for (int i = 0; i < order.Length; ++i)
            {
                var source = order[i];
                var buffer = source.Buffer;
                asset.PackedSplats[i] = buffer.Splats[source.Offset];
                if (asset.SHBands >= 1) Array.Copy(buffer.SH1, source.Offset * 2, asset.PackedSH1, i * 2, 2);
                if (asset.SHBands >= 2) Array.Copy(buffer.SH2, source.Offset * 4, asset.PackedSH2, i * 4, 4);
                if (asset.SHBands >= 3) Array.Copy(buffer.SH3, source.Offset * 4, asset.PackedSH3, i * 4, 4);
                if (asset.SHBands >= 4) Array.Copy(buffer.SH4, source.Offset * 4, asset.PackedSH4, i * 4, 4);
            }
            m_buffers.Clear();
            Debug.Log($"[SPLAT_PRUNE_20260907] Retained {count} of {SourceCount} splats; removed {SourceCount - count} before GPU upload.");
        }
    }
}
