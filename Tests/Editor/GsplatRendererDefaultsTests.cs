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

                var sorter = GsplatSorter.Instance;
                var active = (IList)typeof(GsplatSorter).GetField("m_activeGsplats",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
                active.Clear();
                active.Add(first);
                active.Add(second);
                active.Add(preview);
                typeof(GsplatSorter).GetMethod("PartitionGlobalSortGsplats",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(sorter, null);

                var global = (IList)typeof(GsplatSorter).GetField("m_globalGsplats",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
                var independent = (IList)typeof(GsplatSorter).GetField("m_independentGsplats",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sorter);
                CollectionAssert.AreEquivalent(new[] { first, second }, global);
                CollectionAssert.AreEqual(new[] { preview }, independent);
            }
            finally
            {
                foreach (var gameObject in gameObjects)
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
