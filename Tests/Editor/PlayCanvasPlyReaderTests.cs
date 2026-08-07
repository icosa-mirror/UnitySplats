// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using Gsplat.Formats;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class PlayCanvasPlyReaderTests
    {
        [Test]
        public void StandardFloat32RecordsAreReadInBulk()
        {
            const int count = 256;
            byte[] bytes = BuildStandardPly(count, false, out int payloadOffset);
            using var stream = new CountingStream(bytes, payloadOffset);

            GsplatDecodedData decoded = PlayCanvasPlyReader.Read(stream, SourceCoordinates.RUF);

            Assert.That(decoded.Count, Is.EqualTo(count));
            Assert.That(decoded.Positions[0].x, Is.EqualTo(1f));
            Assert.That(decoded.Positions[count - 1].x, Is.EqualTo(count));
            Assert.That(stream.PayloadReadCalls, Is.LessThanOrEqualTo(count + 1),
                "The float32 fast path should perform about one payload read per vertex record.");
        }

        [Test]
        public void MixedScalarRecordsRetainGenericFallback()
        {
            const int count = 4;
            byte[] bytes = BuildStandardPly(count, true, out int payloadOffset);
            using var stream = new CountingStream(bytes, payloadOffset);

            GsplatDecodedData decoded = PlayCanvasPlyReader.Read(stream, SourceCoordinates.RUF);

            Assert.That(decoded.Count, Is.EqualTo(count));
            Assert.That(decoded.Positions[count - 1].x, Is.EqualTo(count));
            Assert.That(stream.PayloadReadCalls, Is.GreaterThan(count),
                "A mixed-type record must continue through the scalar fallback.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RuntimeSparkLoadStreamsStandardRecordsInBlocks(bool doublePrecisionX)
        {
            const int count = 10000;
            byte[] bytes = BuildStandardPly(count, doublePrecisionX, out int payloadOffset);
            using var stream = new CountingStream(bytes, payloadOffset);
            GsplatAssetSpark expected = null;
            GsplatAsset asset = null;

            try
            {
                using (var expectedStream = new MemoryStream(bytes, false))
                {
                    GsplatDecodedData decoded = PlayCanvasPlyReader.Read(
                        expectedStream,
                        SourceCoordinates.RUF);
                    expected = UnityEngine.ScriptableObject.CreateInstance<GsplatAssetSpark>();
                    expected.LoadFromDecoded(decoded);
                }

                asset = GsplatRuntimeLoader.Load(
                    stream,
                    GsplatFileFormat.Ply,
                    CompressionMode.Spark,
                    SourceCoordinates.RUF);

                Assert.That(asset, Is.TypeOf<GsplatAssetSpark>());
                Assert.That(asset.SplatCount, Is.EqualTo((uint)count));
                var actual = (GsplatAssetSpark)asset;
                Assert.That(actual.Bounds.center, Is.EqualTo(expected.Bounds.center));
                Assert.That(actual.Bounds.size, Is.EqualTo(expected.Bounds.size));
                CollectionAssert.AreEqual(expected.PackedSplats, actual.PackedSplats);
                Assert.That(stream.PayloadReadCalls, Is.LessThanOrEqualTo(4),
                    "The runtime Spark path should read thousands of records per payload block.");
            }
            finally
            {
                if (expected != null)
                    UnityEngine.Object.DestroyImmediate(expected);
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static byte[] BuildStandardPly(int count, bool doublePrecisionX, out int payloadOffset)
        {
            string xType = doublePrecisionX ? "double" : "float";
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                $"element vertex {count}\n" +
                $"property {xType} x\n" +
                "property float y\n" +
                "property float z\n" +
                "property float f_dc_0\n" +
                "property float f_dc_1\n" +
                "property float f_dc_2\n" +
                "property float opacity\n" +
                "property float scale_0\n" +
                "property float scale_1\n" +
                "property float scale_2\n" +
                "property float rot_0\n" +
                "property float rot_1\n" +
                "property float rot_2\n" +
                "property float rot_3\n" +
                "end_header\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            payloadOffset = headerBytes.Length;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(headerBytes);
            for (int i = 0; i < count; ++i)
            {
                if (doublePrecisionX)
                    writer.Write((double)(i + 1));
                else
                    writer.Write((float)(i + 1));
                writer.Write(2f);
                writer.Write(3f);
                writer.Write(0.1f);
                writer.Write(0.2f);
                writer.Write(0.3f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(1f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
            }
            writer.Flush();
            return stream.ToArray();
        }

        sealed class CountingStream : Stream
        {
            readonly MemoryStream m_Stream;
            readonly int m_PayloadOffset;

            public int PayloadReadCalls { get; private set; }

            public CountingStream(byte[] bytes, int payloadOffset)
            {
                m_Stream = new MemoryStream(bytes, false);
                m_PayloadOffset = payloadOffset;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => m_Stream.Length;
            public override long Position
            {
                get => m_Stream.Position;
                set => m_Stream.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                CountPayloadRead();
                return m_Stream.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                CountPayloadRead();
                return m_Stream.Read(buffer);
            }

            public override int ReadByte()
            {
                CountPayloadRead();
                return m_Stream.ReadByte();
            }

            void CountPayloadRead()
            {
                if (m_Stream.Position >= m_PayloadOffset)
                    ++PayloadReadCalls;
            }

            public override long Seek(long offset, SeekOrigin origin) => m_Stream.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    m_Stream.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
