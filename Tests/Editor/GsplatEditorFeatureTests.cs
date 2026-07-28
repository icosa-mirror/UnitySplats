// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using Gsplat.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatEditorFeatureTests
    {
        [Test]
        public void RendererInspectorSupportsMultiObjectEditing()
        {
            Assert.IsTrue(Attribute.IsDefined(typeof(GsplatRendererEditor),
                typeof(CanEditMultipleObjects), true));
        }

        [Test]
        public void ScenePickerIntersectsTransformedAssetBounds()
        {
            var transform = Matrix4x4.TRS(
                new Vector3(3f, -2f, 7f),
                Quaternion.Euler(15f, 35f, -20f),
                new Vector3(2f, 0.5f, 3f));
            var bounds = new Bounds(new Vector3(0.25f, -0.5f, 0.75f),
                new Vector3(4f, 2f, 6f));
            Vector3 worldCenter = transform.MultiplyPoint3x4(bounds.center);
            Vector3 direction = (worldCenter - new Vector3(-12f, 8f, -20f)).normalized;
            var hitRay = new Ray(worldCenter - direction * 50f, direction);

            Assert.IsTrue(GsplatScenePicker.IntersectLocalBounds(
                hitRay, transform.inverse, bounds, out float distance));
            Assert.Greater(distance, 0f);

            var missRay = new Ray(hitRay.origin + Vector3.up * 100f, hitRay.direction);
            Assert.IsFalse(GsplatScenePicker.IntersectLocalBounds(
                missRay, transform.inverse, bounds, out _));
        }

        [Test]
        public void NewSettingsEnableCrossRendererSorting()
        {
            var settings = ScriptableObject.CreateInstance<GsplatSettings>();
            try
            {
                Assert.IsTrue(settings.EnableGlobalSort);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void NewSettingsDisableDepthWriting()
        {
            var settings = ScriptableObject.CreateInstance<GsplatSettings>();
            try
            {
                Assert.AreEqual(1.1f, settings.DepthPrepassAlphaCutoff);
                Assert.Greater(settings.DepthPrepassAlphaCutoff, 1f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase(0.01f, true)]
        [TestCase(0.5f, true)]
        [TestCase(1f, true)]
        [TestCase(1.1f, false)]
        [TestCase(-0.01f, false)]
        public void DepthWritingUsesDocumentedCutoffRange(float cutoff, bool expected)
        {
            var settings = ScriptableObject.CreateInstance<GsplatSettings>();
            try
            {
                settings.DepthPrepassAlphaCutoff = cutoff;
                Assert.AreEqual(expected, settings.DepthWritingEnabled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase("GsplatSpark.asset", "Gsplat/DepthOnly")]
        [TestCase("GsplatUncompressed.asset", "Gsplat/DepthOnly")]
        public void PerRendererDepthMaterialsAreWiredAndCached(string assetName, string shaderName)
        {
            var material = AssetDatabase.LoadAssetAtPath<GsplatMaterial>(
                $"{GsplatUtils.k_PackagePath}Runtime/Materials/{assetName}");

            Assert.IsNotNull(material);
            Assert.IsNotNull(material.DefaultDepthMaterial);
            Assert.AreEqual(shaderName, material.DefaultDepthMaterial.shader.name);
            Assert.AreSame(material.DepthMaterials, material.DepthMaterials,
                "Depth material variants must be cached rather than allocated per draw.");
        }

        [Test]
        public void GlobalDepthMaterialIsWiredAndCached()
        {
            var material = AssetDatabase.LoadAssetAtPath<GsplatGlobalMaterial>(
                $"{GsplatUtils.k_PackagePath}Runtime/Materials/GsplatGlobal.asset");

            Assert.IsNotNull(material);
            Assert.IsTrue(material.DepthValid());
            Assert.AreEqual("Gsplat/GlobalDepthOnly", material.DefaultDepthMaterial.shader.name);
            Assert.AreSame(material.DepthMaterials, material.DepthMaterials,
                "Global depth material variants must be cached rather than allocated per draw.");
        }
    }
}
