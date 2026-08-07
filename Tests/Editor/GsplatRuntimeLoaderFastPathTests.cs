// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gsplat.Formats;
using NUnit.Framework;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using GZipStream = System.IO.Compression.GZipStream;
using ZipArchive = System.IO.Compression.ZipArchive;
using ZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace Gsplat.Tests
{
    public class GsplatRuntimeLoaderFastPathTests
    {
        static readonly Dictionary<string, byte[]> s_SogImages = new()
        {
            ["means-low.webp"] = Convert.FromBase64String(
                "UklGRiAAAABXRUJQVlA4TBMAAAAvAUAAAIXCtm1QXBiv/9n/UM4DAA=="),
            ["means-high.webp"] = Convert.FromBase64String(
                "UklGRiAAAABXRUJQVlA4TBQAAAAvAUAAAIWCtm1YjKJ3p//Z/4DnAQ=="),
            ["scales.webp"] = Convert.FromBase64String(
                "UklGRiAAAABXRUJQVlA4TBMAAAAvAUAAAIWQgIwdk2VZxevR/8AGAA=="),
            ["quats.webp"] = Convert.FromBase64String(
                "UklGRjgAAABXRUJQVlA4TCwAAAAvAUAAEFDbNgocW/teoENN2wYM8nTdBS2mIUIrYG6hFQIIgPg7N3ERPB/qHg=="),
            ["sh0.webp"] = Convert.FromBase64String(
                "UklGRigAAABXRUJQVlA4TBsAAAAvAUAAEB9wCpQroNwDpSiB/yAboEwVZhH9jx0A")
        };

        [TestCase(null, SourceCoordinates.RUB)]
        [TestCase(12u, SourceCoordinates.LDB)]
        public void SparkSpzFastPathMatchesCanonicalPacking(
            uint? storedCoordinateSystem,
            SourceCoordinates fallbackCoordinates)
        {
            byte[] bytes = BuildGzipSpz(257, 3, storedCoordinateSystem);
            GsplatAssetSpark expected = null;
            GsplatAsset actual = null;

            try
            {
                GsplatDecodedData decoded = PlayCanvasSpzReader.Read(
                    bytes,
                    fallbackCoordinates);
                expected = ScriptableObject.CreateInstance<GsplatAssetSpark>();
                expected.LoadFromDecoded(decoded);

                actual = GsplatRuntimeLoader.Load(
                    bytes,
                    GsplatFileFormat.Spz,
                    CompressionMode.Spark,
                    fallbackCoordinates);

                Assert.That(actual, Is.TypeOf<GsplatAssetSpark>());
                var actualSpark = (GsplatAssetSpark)actual;
                Assert.That(actualSpark.SplatCount, Is.EqualTo(expected.SplatCount));
                Assert.That(actualSpark.SHBands, Is.EqualTo(expected.SHBands));
                Assert.That(actualSpark.Antialiased, Is.EqualTo(expected.Antialiased));
                Assert.That(actualSpark.Bounds.center, Is.EqualTo(expected.Bounds.center));
                Assert.That(actualSpark.Bounds.size, Is.EqualTo(expected.Bounds.size));
                CollectionAssert.AreEqual(expected.PackedSplats, actualSpark.PackedSplats);
                CollectionAssert.AreEqual(expected.PackedSH1, actualSpark.PackedSH1);
                CollectionAssert.AreEqual(expected.PackedSH2, actualSpark.PackedSH2);
                CollectionAssert.AreEqual(expected.PackedSH3, actualSpark.PackedSH3);
            }
            finally
            {
                if (expected != null) UnityEngine.Object.DestroyImmediate(expected);
                if (actual != null) UnityEngine.Object.DestroyImmediate(actual);
            }
        }

        [Test]
        public void SparkSogFastPathMatchesCanonicalPacking()
        {
            string metadata = BuildSogMetadata();
            byte[] bundle = BuildSogBundle(metadata);
            GsplatAssetSpark expected = null;
            GsplatAsset actual = null;

            try
            {
                GsplatDecodedData decoded = PlayCanvasSogReader.ReadUnpacked(
                    metadata,
                    filename => s_SogImages[filename],
                    SourceCoordinates.RUB);
                expected = ScriptableObject.CreateInstance<GsplatAssetSpark>();
                expected.LoadFromDecoded(decoded);

                actual = GsplatRuntimeLoader.Load(
                    bundle,
                    GsplatFileFormat.Sog,
                    CompressionMode.Spark,
                    SourceCoordinates.RUB);

                Assert.That(actual, Is.TypeOf<GsplatAssetSpark>());
                var actualSpark = (GsplatAssetSpark)actual;
                Assert.That(actualSpark.SplatCount, Is.EqualTo(expected.SplatCount));
                Assert.That(actualSpark.SHBands, Is.EqualTo(expected.SHBands));
                Assert.That(actualSpark.Antialiased, Is.EqualTo(expected.Antialiased));
                Assert.That(actualSpark.Bounds.center, Is.EqualTo(expected.Bounds.center));
                Assert.That(actualSpark.Bounds.size, Is.EqualTo(expected.Bounds.size));
                CollectionAssert.AreEqual(expected.PackedSplats, actualSpark.PackedSplats);
            }
            finally
            {
                if (expected != null) UnityEngine.Object.DestroyImmediate(expected);
                if (actual != null) UnityEngine.Object.DestroyImmediate(actual);
            }
        }

        static byte[] BuildGzipSpz(int count, byte shDegree, uint? storedCoordinateSystem)
        {
            const uint magic = 0x5053474e;
            const uint version = 2;
            const byte fractionalBits = 12;
            byte flags = (byte)(1 | (storedCoordinateSystem.HasValue ? 2 : 0));
            int shCoefficients = shDegree * (shDegree + 2);

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(magic);
                writer.Write(version);
                writer.Write((uint)count);
                writer.Write(shDegree);
                writer.Write(fractionalBits);
                writer.Write(flags);
                writer.Write((byte)0);

                WritePattern(writer, checked(count * 9), 17, 31);
                WritePattern(writer, count, 0, 29);
                WritePattern(writer, checked(count * 3), 53, 19);
                WritePattern(writer, checked(count * 3), 113, 11);
                for (int i = 0; i < count; ++i)
                {
                    writer.Write((byte)128);
                    writer.Write((byte)128);
                    writer.Write((byte)128);
                }
                WritePattern(writer, checked(count * shCoefficients * 3), 71, 23);

                if (storedCoordinateSystem.HasValue)
                {
                    writer.Write(0xADBE0003u);
                    writer.Write(sizeof(uint));
                    writer.Write(storedCoordinateSystem.Value);
                }
            }

            return compressed.ToArray();
        }

        static string BuildSogMetadata()
        {
            string codebook = string.Join(",", Enumerable.Range(0, 256).Select(index =>
                ((index - 128) / 16f).ToString(CultureInfo.InvariantCulture)));
            return
                "{" +
                "\"version\":2," +
                "\"count\":4," +
                "\"antialias\":true," +
                "\"means\":{" +
                "\"mins\":[-1,-2,-3]," +
                "\"maxs\":[1,2,3]," +
                "\"files\":[\"means-low.webp\",\"means-high.webp\"]}," +
                "\"scales\":{" +
                $"\"codebook\":[{codebook}]," +
                "\"files\":[\"scales.webp\"]}," +
                "\"quats\":{" +
                "\"files\":[\"quats.webp\"]}," +
                "\"sh0\":{" +
                $"\"codebook\":[{codebook}]," +
                "\"files\":[\"sh0.webp\"]}" +
                "}";
        }

        static byte[] BuildSogBundle(string metadata)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                WriteZipEntry(archive, "meta.json", Encoding.UTF8.GetBytes(metadata));
                foreach ((string filename, byte[] bytes) in s_SogImages)
                    WriteZipEntry(archive, filename, bytes);
            }
            return output.ToArray();
        }

        static void WriteZipEntry(ZipArchive archive, string filename, byte[] bytes)
        {
            using Stream stream = archive.CreateEntry(filename).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        static void WritePattern(BinaryWriter writer, int count, int offset, int multiplier)
        {
            for (int i = 0; i < count; ++i)
                writer.Write((byte)(offset + i * multiplier));
        }
    }
}
