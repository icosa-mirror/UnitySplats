// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatSpatialQueryTests
    {
        static bool BroadPhase(Bounds localBounds, Matrix4x4 localToWorld,
            float maxLocalSplatRadius, float scaleFactor, Vector3 centerWorld, float radiusWorld)
        {
            var maxScaleMethod = typeof(GsplatRendererImpl).GetMethod("MaxObjectScale",
                BindingFlags.Static | BindingFlags.NonPublic);
            var broadPhaseMethod = typeof(GsplatRendererImpl).GetMethod("SphereOverlapsRendererBounds",
                BindingFlags.Static | BindingFlags.NonPublic);
            float maxObjectScale = (float)maxScaleMethod.Invoke(null, new object[] { localToWorld });
            return (bool)broadPhaseMethod.Invoke(null, new object[]
            {
                localBounds, localToWorld, maxLocalSplatRadius, scaleFactor,
                centerWorld, radiusWorld, maxObjectScale
            });
        }

        [Test]
        public void BroadPhaseHandlesTranslationRotationAndNonUniformScale()
        {
            var localBounds = new Bounds(Vector3.zero, Vector3.zero);
            var transform = Matrix4x4.TRS(
                new Vector3(10f, 2f, -3f),
                Quaternion.Euler(20f, 35f, 10f),
                new Vector3(0.5f, 3f, 2f));

            Assert.IsTrue(BroadPhase(localBounds, transform, 1f, 1f,
                new Vector3(12.9f, 2f, -3f), 0f));
            Assert.IsFalse(BroadPhase(localBounds, transform, 1f, 1f,
                new Vector3(13.1f, 2f, -3f), 0f));
        }

        [Test]
        public void WorldBoundsMatchAllTransformedCorners()
        {
            var localBounds = new Bounds(
                new Vector3(1f, -2f, 0.5f),
                new Vector3(4f, 6f, 8f));
            var transform = Matrix4x4.TRS(
                new Vector3(7f, -3f, 11f),
                Quaternion.Euler(25f, -40f, 12f),
                new Vector3(-2f, 0.5f, 3f));

            Bounds actual = GsplatUtils.CalcWorldBounds(localBounds, transform);
            Bounds expected = default;
            bool first = true;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                Vector3 corner = localBounds.center +
                                 Vector3.Scale(localBounds.extents, new Vector3(x, y, z));
                Vector3 worldCorner = transform.MultiplyPoint3x4(corner);
                if (first)
                {
                    expected = new Bounds(worldCorner, Vector3.zero);
                    first = false;
                }
                else
                {
                    expected.Encapsulate(worldCorner);
                }
            }

            Assert.That(actual.center.x, Is.EqualTo(expected.center.x).Within(0.0001f));
            Assert.That(actual.center.y, Is.EqualTo(expected.center.y).Within(0.0001f));
            Assert.That(actual.center.z, Is.EqualTo(expected.center.z).Within(0.0001f));
            Assert.That(actual.extents.x, Is.EqualTo(expected.extents.x).Within(0.0001f));
            Assert.That(actual.extents.y, Is.EqualTo(expected.extents.y).Within(0.0001f));
            Assert.That(actual.extents.z, Is.EqualTo(expected.extents.z).Within(0.0001f));
        }

        [Test]
        public void BroadPhaseHonorsRendererDownscaleAndQueryRadius()
        {
            var localBounds = new Bounds(Vector3.zero, Vector3.zero);
            var transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 2f);

            Assert.IsFalse(BroadPhase(localBounds, transform, 1f, 0.5f,
                new Vector3(1.6f, 0f, 0f), 0.5f));
            Assert.IsTrue(BroadPhase(localBounds, transform, 1f, 0.5f,
                new Vector3(1.4f, 0f, 0f), 0.5f));
        }

        [Test]
        public void UnavailableQueryReturnsDocumentedMissScore()
        {
            var gameObject = new GameObject("unavailable spatial query");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();

                Assert.IsFalse(renderer.TryIntersectSphere(Vector3.zero, 1f, out float score));
                Assert.AreEqual(-1f, score);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MaxRadiusUsesLargestUncompressedScaleWithoutAllocatingQueryBuffers()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            try
            {
                asset.Scales = new[]
                {
                    new Vector3(0.25f, 0.5f, 1f),
                    new Vector3(4f, 2f, 3f)
                };
                var method = typeof(GsplatRendererImpl).GetMethod("CalculateMaxLocalSplatRadius",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.AreEqual(8f, method.Invoke(null, new object[] { asset }));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void MaxRadiusDecodesSparkScale()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            try
            {
                asset.PackedSplats = new[]
                {
                    GsplatAssetSpark.PackSplat(
                        new Vector4(0f, 0f, 0f, 1f),
                        Vector3.zero,
                        new Vector3(Mathf.Log(0.5f), Mathf.Log(2f), Mathf.Log(4f)),
                        Quaternion.identity)
                };
                var method = typeof(GsplatRendererImpl).GetMethod("CalculateMaxLocalSplatRadius",
                    BindingFlags.Static | BindingFlags.NonPublic);
                float radius = (float)method.Invoke(null, new object[] { asset });

                Assert.That(radius, Is.InRange(7f, 9f));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
