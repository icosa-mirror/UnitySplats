// Copyright (c) 2026 The Open Brush Authors
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using Gsplat.Formats;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatImportFilterTests
    {
        static GsplatDecodedData Data(byte bands = 4)
        {
            var data = new GsplatDecodedData(4, bands);
            data.Bounds = new Bounds(new Vector3(1.5f, 0, 0), new Vector3(3, 0, 0));
            int coefficients = GsplatUtils.SHBandsToCoefficientCount(bands);
            for (int i = 0; i < 4; ++i)
            {
                data.Positions[i] = new Vector3(i, 0, 0);
                data.Scales[i] = Vector3.one * (i == 2 ? 0.01f : 1f);
                data.Colors[i] = new Vector4(i * 0.1f, 0, 0, i == 1 ? 0.9f : 0.01f);
                data.Rotations[i] = new Vector4(1, 0, 0, 0);
                for (int c = 0; c < coefficients; ++c)
                    data.SHs[i * coefficients + c] = new Vector3(i * 0.01f, c * 0.001f, 0);
            }
            return data;
        }

        [Test]
        public void CanonicalFilteringUsesBothConditionsAndKeepsAllAttributesAligned()
        {
            var original = Data();
            var data = Data();
            new GsplatImportFilter(0.05f, 0.1f).Apply(data, data.Bounds);
            Assert.AreEqual(2, data.Count);
            Assert.AreEqual(original.Bounds, data.Bounds);
            for (int i = 0; i < 2; ++i)
            {
                Assert.AreEqual(original.Positions[i + 1], data.Positions[i]);
                Assert.AreEqual(original.Scales[i + 1], data.Scales[i]);
                Assert.AreEqual(original.Colors[i + 1], data.Colors[i]);
                Assert.AreEqual(original.Rotations[i + 1], data.Rotations[i]);
            }
            CollectionAssert.AreEqual(original.SHs.Skip(24).Take(48), data.SHs);
            data.Validate();
        }

        [Test]
        public void ThresholdsAreStrictAndDegenerateBoundsKeepSplats()
        {
            var filter = new GsplatImportFilter(0.05f, 0.1f);
            Assert.IsFalse(filter.Removes(Vector3.one, 0.05f, 0.3));
            Assert.IsFalse(filter.Removes(Vector3.one, 0.01f, 1));
            Assert.IsTrue(filter.Removes(new Vector3(0, 2, 0), 0.01f, 1));
            var data = Data();
            filter.Apply(data, new Bounds(Vector3.zero, Vector3.zero));
            Assert.AreEqual(4, data.Count);
            new GsplatImportFilter(0, 0.1f).Apply(data, Data().Bounds);
            Assert.AreEqual(4, data.Count);
        }

        [TestCase(float.NaN, 0.1f)]
        [TestCase(float.PositiveInfinity, 0.1f)]
        [TestCase(-0.1f, 0.1f)]
        [TestCase(1.1f, 0.1f)]
        [TestCase(0.1f, 0)]
        [TestCase(0.1f, float.NaN)]
        public void InvalidThresholdsAreRejected(float opacity, float scale) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new GsplatImportFilter(opacity, scale));

        [TestCase((byte)0)]
        [TestCase((byte)1)]
        [TestCase((byte)2)]
        [TestCase((byte)3)]
        [TestCase((byte)4)]
        public void GrowingSparkOutputMatchesPackingOnlySurvivors(byte bands)
        {
            var expected = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            var actual = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            try
            {
                var data = Data(bands);
                var filter = new GsplatImportFilter(0.05f, 0.1f);
                actual.ImportContext = new GsplatImportContext(filter, data.Bounds);
                actual.LoadFromDecoded(data);
                filter.Apply(data, data.Bounds);
                expected.LoadFromDecoded(data);
                CollectionAssert.AreEqual(expected.PackedSplats, actual.PackedSplats);
                CollectionAssert.AreEqual(expected.PackedSH1, actual.PackedSH1);
                CollectionAssert.AreEqual(expected.PackedSH2, actual.PackedSH2);
                CollectionAssert.AreEqual(expected.PackedSH3, actual.PackedSH3);
                CollectionAssert.AreEqual(expected.PackedSH4, actual.PackedSH4);
                Assert.AreEqual(expected.Bounds, actual.Bounds);
            }
            finally { UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual); }
        }

        [Test]
        public void WorkerBuffersGrowWithoutLosingSourceIndicesOrSHBands()
        {
            var buffer = new GsplatImportBuffer(4);
            for (int i = 0; i < 2050; ++i)
            {
                int slot = buffer.Append(i * 2);
                buffer.Splats[slot] = new Unity.Mathematics.uint4((uint)i);
                for (int c = 0; c < 2; ++c) buffer.SH1[slot * 2 + c] = (uint)(i * 10 + c);
                for (int c = 0; c < 4; ++c)
                    buffer.SH2[slot * 4 + c] = buffer.SH3[slot * 4 + c] = buffer.SH4[slot * 4 + c] = (uint)(i * 10 + c);
            }
            for (int i = 0; i < buffer.Count; ++i)
            {
                Assert.AreEqual(i * 2, buffer.SourceIndices[i]);
                Assert.AreEqual(new Unity.Mathematics.uint4((uint)i), buffer.Splats[i]);
                Assert.AreEqual((uint)(i * 10 + 1), buffer.SH1[i * 2 + 1]);
                Assert.AreEqual((uint)(i * 10 + 3), buffer.SH2[i * 4 + 3]);
                Assert.AreEqual((uint)(i * 10 + 3), buffer.SH3[i * 4 + 3]);
                Assert.AreEqual((uint)(i * 10 + 3), buffer.SH4[i * 4 + 3]);
            }
        }

        [Test]
        public void BoundsCacheInvalidatesOnSourceChangeAndIgnoresCorruption()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"gsplat-filter-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "source.ply");
                File.WriteAllText(path, "source");
                var info = new FileInfo(path);
                Bounds bounds = Data().Bounds;
                GsplatBoundsCache.Write(path, SourceCoordinates.RUF, dir, info.Length, info.LastWriteTimeUtc.Ticks, bounds);
                Assert.IsTrue(GsplatBoundsCache.TryRead(path, SourceCoordinates.RUF, dir, out var loaded));
                Assert.AreEqual(bounds, loaded);
                Assert.IsFalse(GsplatBoundsCache.TryRead(path, SourceCoordinates.RUB, dir, out _));
                File.SetLastWriteTimeUtc(path, info.LastWriteTimeUtc.AddSeconds(2));
                Assert.IsFalse(GsplatBoundsCache.TryRead(path, SourceCoordinates.RUF, dir, out _));
                File.WriteAllBytes(GsplatBoundsCache.CachePath(path, SourceCoordinates.RUF, dir), new byte[] { 1, 2 });
                Assert.IsFalse(GsplatBoundsCache.TryRead(path, SourceCoordinates.RUF, dir, out _));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void FileLoaderCachesOriginalBoundsAndChangingThresholdsDoesNotRescan()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"gsplat-filter-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            GsplatAsset first = null, second = null;
            try
            {
                string path = Path.Combine(dir, "capture.ply");
                byte[] bytes = Ply();
                File.WriteAllBytes(path, bytes);
                int scans = 0;
                ProgressCallback progress = (message, _) => { if (message == "Scanning source splat bounds") ++scans; };
                first = GsplatRuntimeLoader.LoadFile(path, progress: progress,
                    filter: new GsplatImportFilter(0.05f, 0.1f), boundsCacheDirectory: dir);
                Assert.AreEqual(2, first.SplatCount);
                Assert.AreEqual(1, scans);
                second = GsplatRuntimeLoader.LoadFile(path, progress: progress,
                    filter: new GsplatImportFilter(0.005f, 0.1f), boundsCacheDirectory: dir);
                Assert.AreEqual(4, second.SplatCount);
                Assert.AreEqual(first.Bounds, second.Bounds);
                Assert.AreEqual(1, scans);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                Directory.Delete(dir, true);
            }
        }

        static byte[] Ply()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            string[] properties = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
                "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };
            string header = $"ply\nformat binary_little_endian 1.0\nelement vertex 4\n{string.Join("", properties.Select(p => $"property float {p}\n"))}end_header\n";
            writer.Write(Encoding.ASCII.GetBytes(header));
            for (int i = 0; i < 4; ++i)
            {
                float opacity = i == 1 ? 0.9f : 0.01f;
                float scale = i == 2 ? 0.01f : 1f;
                foreach (float value in new[] { (float)i, 0, 0, 0, 0, 0, Mathf.Log(opacity / (1 - opacity)),
                    Mathf.Log(scale), Mathf.Log(scale), Mathf.Log(scale), 1, 0, 0, 0 }) writer.Write(value);
            }
            return stream.ToArray();
        }
    }
}
