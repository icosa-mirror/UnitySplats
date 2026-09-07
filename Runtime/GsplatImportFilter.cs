// Copyright (c) 2026 The Open Brush Authors
// SPDX-License-Identifier: MIT

using System;
using Gsplat.Formats;
using UnityEngine;

namespace Gsplat
{
    /// <summary>Optional import-time pruning. A splat is removed only when BOTH conditions match.</summary>
    public sealed class GsplatImportFilter
    {
        public float MaxOpacity { get; }
        public float MinScaleFraction { get; }

        /// <param name="maxOpacity">Opacity must be strictly below this value (0–1).</param>
        /// <param name="minScaleFraction">Largest linear Gaussian scale must exceed this positive
        /// fraction of the source bounds' longest dimension. This is not a projected pixel size.</param>
        public GsplatImportFilter(float maxOpacity, float minScaleFraction)
        {
            if (!Finite(maxOpacity) || maxOpacity < 0 || maxOpacity > 1)
                throw new ArgumentOutOfRangeException(nameof(maxOpacity));
            if (!Finite(minScaleFraction) || minScaleFraction <= 0)
                throw new ArgumentOutOfRangeException(nameof(minScaleFraction));
            MaxOpacity = maxOpacity;
            MinScaleFraction = minScaleFraction;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal double ScaleThreshold(Bounds bounds)
        {
            var size = bounds.size;
            var center = bounds.center;
            if (!Finite(size.x) || !Finite(size.y) || !Finite(size.z) ||
                !Finite(center.x) || !Finite(center.y) || !Finite(center.z) ||
                size.x < 0 || size.y < 0 || size.z < 0)
                throw new ArgumentException("Source bounds must be finite and non-negative.", nameof(bounds));
            double extent = Math.Max(size.x, Math.Max(size.y, size.z));
            // A point capture has no meaningful relative scale. Keep it unchanged.
            return extent > 0 ? extent * MinScaleFraction : double.PositiveInfinity;
        }

        internal bool Removes(Vector3 linearScale, float opacity, double threshold) =>
            opacity < MaxOpacity && Math.Max(linearScale.x, Math.Max(linearScale.y, linearScale.z)) > threshold;

        internal void Apply(GsplatDecodedData data, Bounds bounds)
        {
            data.Validate();
            double threshold = ScaleThreshold(bounds);
            int coefficients = GsplatUtils.SHBandsToCoefficientCount(data.SHBands);
            int kept = 0;
            for (int i = 0; i < data.Count; ++i)
            {
                if (Removes(data.Scales[i], data.Colors[i].w, threshold)) continue;
                if (kept != i)
                {
                    data.Positions[kept] = data.Positions[i];
                    data.Scales[kept] = data.Scales[i];
                    data.Rotations[kept] = data.Rotations[i];
                    data.Colors[kept] = data.Colors[i];
                    Array.Copy(data.SHs, i * coefficients, data.SHs, kept * coefficients, coefficients);
                }
                ++kept;
            }
            if (kept != data.Count)
            {
                data.Count = kept;
                Array.Resize(ref data.Positions, kept);
                Array.Resize(ref data.Scales, kept);
                Array.Resize(ref data.Rotations, kept);
                Array.Resize(ref data.Colors, kept);
                Array.Resize(ref data.SHs, checked(kept * coefficients));
            }
            data.Bounds = bounds;
        }
    }
}
