// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatRendererDefaultsTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RelitAndNonRelitDrawsKeepIndependentMaterialKeywords(bool global)
        {
            var shader = Shader.Find(global ? "Gsplat/Global" : "Gsplat/Standard");
            Assert.IsNotNull(shader);
            var source = new Material(shader);
            var individual = ScriptableObject.CreateInstance<GsplatMaterial>();
            var merged = ScriptableObject.CreateInstance<GsplatGlobalMaterial>();
            Material relit = null;
            try
            {
                // Even a template with relighting enabled must produce an unlit default.
                source.EnableKeyword(GsplatUtils.k_RelightingKeyword);
                individual.DefaultMaterial = source;
                merged.DefaultMaterial = source;
                var unlit = global ? merged.GetColorMaterial(0, false) : individual.GetColorMaterial(0, 0, false);
                relit = global ? merged.GetColorMaterial(0, true) : individual.GetColorMaterial(0, 0, true);
                var nextUnlit = global ? merged.GetColorMaterial(0, false) : individual.GetColorMaterial(0, 0, false);
                Assert.AreSame(unlit, nextUnlit);
                Assert.AreNotSame(unlit, relit);
                Assert.IsFalse(unlit.IsKeywordEnabled(GsplatUtils.k_RelightingKeyword));
                Assert.IsTrue(relit.IsKeywordEnabled(GsplatUtils.k_RelightingKeyword));
                Assert.AreEqual(unlit.renderQueue, relit.renderQueue);
                Assert.IsTrue(relit.IsKeywordEnabled("SH_BANDS_0"));
            }
            finally
            {
                Object.DestroyImmediate(relit);
                if (global)
                    foreach (var material in merged.Materials)
                        Object.DestroyImmediate(material);
                else
                    foreach (var band in individual.Materials)
                        foreach (var material in band)
                            Object.DestroyImmediate(material);
                Object.DestroyImmediate(individual);
                Object.DestroyImmediate(merged);
                Object.DestroyImmediate(source);
            }
        }

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
        public void NewRendererParticipatesInGlobalSort()
        {
            var gameObject = new GameObject("GsplatRenderer global-sort default test");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();

                Assert.IsTrue(renderer.ParticipateInGlobalSort);
                Assert.IsTrue(new SerializedObject(renderer)
                    .FindProperty("m_participateInGlobalSort").boolValue);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GlobalSortParticipationSurvivesInstantiation()
        {
            var original = new GameObject("GsplatRenderer global-sort serialization test");
            GameObject clone = null;
            try
            {
                original.AddComponent<GsplatRenderer>().ParticipateInGlobalSort = false;
                clone = Object.Instantiate(original);

                Assert.IsFalse(clone.GetComponent<GsplatRenderer>().ParticipateInGlobalSort);
            }
            finally
            {
                Object.DestroyImmediate(clone);
                Object.DestroyImmediate(original);
            }
        }

        [Test]
        public void RuntimeParticipationChangeInvalidatesGlobalBuffers()
        {
            var gameObject = new GameObject("GsplatRenderer global-sort invalidation test");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                var sorter = GsplatSorter.Instance;
                var globalRenderer = typeof(GsplatSorter).GetField("m_globalRenderer",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
                var dirtyField = globalRenderer.GetType().GetField("m_globalBuffersDirty",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                dirtyField.SetValue(globalRenderer, false);

                renderer.ParticipateInGlobalSort = false;

                Assert.IsTrue((bool)dirtyField.GetValue(globalRenderer));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void NonParticipantIsIndependentWithoutBlockingOtherParticipants()
        {
            var sorter = GsplatSorter.Instance;
            var active = (IList)typeof(GsplatSorter).GetField("m_activeGsplats",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
            var global = (IList)typeof(GsplatSorter).GetField("m_globalGsplats",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
            var independent = (IList)typeof(GsplatSorter).GetField("m_independentGsplats",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
            var previousActive = Snapshot(active);
            var previousGlobal = Snapshot(global);
            var previousIndependent = Snapshot(independent);
            var gameObjects = new[]
            {
                new GameObject("GsplatRenderer global-sort participant A"),
                new GameObject("GsplatRenderer global-sort participant B"),
                new GameObject("GsplatRenderer independent preview")
            };
            try
            {
                var first = gameObjects[0].AddComponent<GsplatRenderer>();
                var second = gameObjects[1].AddComponent<GsplatRenderer>();
                var preview = gameObjects[2].AddComponent<GsplatRenderer>();
                preview.ParticipateInGlobalSort = false;

                active.Clear();
                active.Add(first);
                active.Add(second);
                active.Add(preview);
                typeof(GsplatSorter).GetMethod("PartitionGlobalSortGsplats",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(sorter, null);

                CollectionAssert.AreEquivalent(new[] { first, second }, global);
                CollectionAssert.AreEqual(new[] { preview }, independent);
            }
            finally
            {
                Restore(active, previousActive);
                Restore(global, previousGlobal);
                Restore(independent, previousIndependent);
                foreach (var gameObject in gameObjects)
                    Object.DestroyImmediate(gameObject);
            }
        }

        static object[] Snapshot(IList list)
        {
            var snapshot = new object[list.Count];
            list.CopyTo(snapshot, 0);
            return snapshot;
        }

        static void Restore(IList list, object[] snapshot)
        {
            list.Clear();
            foreach (var item in snapshot)
                list.Add(item);
        }

        [Test]
        public void ProxyChildRelightingUsesPlayCanvasCompensationDefaults()
        {
            var splatObject = new GameObject("GsplatRenderer relighting defaults test");
            var proxyObject = new GameObject("Gsplat relighting proxy defaults test");
            proxyObject.SetActive(false);
            try
            {
                splatObject.AddComponent<GsplatRenderer>();
                proxyObject.transform.SetParent(splatObject.transform);
                var relighting = proxyObject.AddComponent<GsplatProxyRelighting>();

                Assert.AreEqual(0.5f, relighting.TextureScale);
                Assert.AreEqual(1f, relighting.Blend);
                Assert.AreEqual(2f, relighting.Brightness);
                Assert.AreEqual(1f, relighting.Background);
                Assert.IsNull(typeof(GsplatRenderer).GetField("RelightingEnabled"));
                Assert.IsNull(typeof(GsplatRenderer).GetField("RelightingProxy"));
                Assert.IsNull(typeof(GsplatRenderer).GetField("RelightingSource"));
            }
            finally
            {
                Object.DestroyImmediate(splatObject);
            }
        }

        [Test]
        public void RelightingUsesNormalCameraRenderingUnlessExplicitlyOnDemand()
        {
            var cameraObject = new GameObject("GsplatRelighting camera scheduling test");
            try
            {
                cameraObject.AddComponent<Camera>();
                var relighting = cameraObject.AddComponent<GsplatRelighting>();
                relighting.ProxyLayers = 1 << 30;
                relighting.Refresh();

                var field = typeof(GsplatRelighting).GetField(
                    "m_proxyCamera",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field);
                var proxyCamera = field.GetValue(relighting) as Camera;
                Assert.IsNotNull(proxyCamera);
                Assert.IsTrue(proxyCamera.enabled,
                    "Game-camera proxy rendering must use Unity's normal camera loop.");

                relighting.RenderOnDemand = true;
                relighting.Refresh();
                Assert.IsFalse(proxyCamera.enabled,
                    "Only explicit on-demand users such as Scene view should disable scheduling.");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void RendererDiscoversRelightingOnProxyChild()
        {
            var cameraObject = new GameObject("Gsplat shared relighting camera test");
            var splatObject = new GameObject("Gsplat proxy discovery test");
            var proxyObject = new GameObject("Gsplat proxy child test");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                GsplatRelighting source = cameraObject.AddComponent<GsplatRelighting>();
                source.ProxyLayers = 1 << 30;
                source.Refresh();

                GsplatRenderer renderer = splatObject.AddComponent<GsplatRenderer>();
                proxyObject.transform.SetParent(splatObject.transform);
                proxyObject.layer = 30;
                GsplatProxyRelighting proxy = proxyObject.AddComponent<GsplatProxyRelighting>();
                proxy.SourceCamera = camera;
                proxy.Refresh();

                Assert.IsTrue(renderer.TryGetActiveRelighting(out GsplatRelighting resolved));
                Assert.AreSame(source, resolved);
            }
            finally
            {
                Object.DestroyImmediate(splatObject);
                Object.DestroyImmediate(cameraObject);
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
