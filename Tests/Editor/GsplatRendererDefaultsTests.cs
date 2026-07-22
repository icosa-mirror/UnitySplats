// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatRendererDefaultsTests
    {
        [Test]
        public void NewRendererEnablesGammaToLinear()
        {
            var gameObject = new GameObject("GsplatRenderer defaults test");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                Assert.IsTrue(renderer.GammaToLinear);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BoxColliderFitsAssetLocalBounds()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            var gameObject = new GameObject("GsplatRenderer collider test");
            try
            {
                asset.Bounds = new Bounds(
                    new Vector3(1.5f, -2f, 3.25f),
                    new Vector3(8f, 4.5f, 2f));
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                var boxCollider = gameObject.AddComponent<BoxCollider>();
                renderer.GsplatAsset = asset;

                Assert.IsTrue(renderer.FitBoxColliderToAssetBounds());
                Assert.AreEqual(asset.Bounds.center, boxCollider.center);
                Assert.AreEqual(asset.Bounds.size, boxCollider.size);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(asset);
            }
        }
    }
}
