// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Gsplat.Tests
{
    public class GsplatDepthRenderingTests
    {
        sealed class RecordingGsplat : MonoBehaviour, IGsplat
        {
            public static readonly List<string> Calls = new();
            public uint RemainingCount => 1;
            public ISorterResource SorterResource => null;
            public bool Valid => true;
            public bool ComputeSortRequired => false;
            public GsplatResource GsplatResource => null;
            public uint SplatCount => 1;
            public byte SHBands => 0;

            public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv)
            {
            }

            public void Draw()
            {
            }

            public void RenderColor(CommandBuffer cmd) => Calls.Add($"color:{name}");
            public void RenderDepth(CommandBuffer cmd) => Calls.Add($"depth:{name}");
#if UNITY_6000_0_OR_NEWER
            public void RenderColor(RasterCommandBuffer cmd) => Calls.Add($"color:{name}");
            public void RenderDepth(RasterCommandBuffer cmd) => Calls.Add($"depth:{name}");
#endif
        }

        [Test]
        public void ExplicitDrawsRespectCameraCullingMask()
        {
            var visibleObject = new GameObject("visible splat") { layer = 8 };
            var hiddenObject = new GameObject("hidden splat") { layer = 9 };
            var cameraObject = new GameObject("splat culling camera");
            var commandBuffer = new CommandBuffer();
            var sorter = GsplatSorter.Instance;
            var activeField = typeof(GsplatSorter).GetField("m_activeGsplats",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var active = (IList)activeField.GetValue(sorter);
            var previous = new object[active.Count];
            active.CopyTo(previous, 0);
            var globalField = typeof(GsplatSorter).GetField("<GlobalRenderEnabled>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var previousGlobal = globalField.GetValue(sorter);

            try
            {
                var visible = visibleObject.AddComponent<RecordingGsplat>();
                var hidden = hiddenObject.AddComponent<RecordingGsplat>();
                var camera = cameraObject.AddComponent<Camera>();
                camera.cullingMask = 1 << visibleObject.layer;
                active.Clear();
                active.Add(visible);
                active.Add(hidden);
                globalField.SetValue(sorter, false);
                RecordingGsplat.Calls.Clear();

                sorter.RenderColor(commandBuffer, camera);
                sorter.RenderDepth(commandBuffer, camera);

                CollectionAssert.AreEqual(new[]
                {
                    $"color:{visible.name}",
                    $"depth:{visible.name}"
                }, RecordingGsplat.Calls);
            }
            finally
            {
                active.Clear();
                foreach (var item in previous)
                    active.Add(item);
                globalField.SetValue(sorter, previousGlobal);
                RecordingGsplat.Calls.Clear();
                commandBuffer.Dispose();
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(hiddenObject);
                Object.DestroyImmediate(visibleObject);
            }
        }

        [Test]
        public void ExplicitDrawOrderingReadsRendererRenderOrder()
        {
            var gameObject = new GameObject("splat render-order test");
            try
            {
                var renderer = gameObject.AddComponent<GsplatRenderer>();
                renderer.RenderOrder = 7;
                var method = typeof(GsplatSorter).GetMethod("GetRenderOrder",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.AreEqual(7u, method.Invoke(null, new object[] { renderer }));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ExplicitColorPropertiesClearRelightingForNonRelitDraws()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            var gameObject = new GameObject("Non-relit explicit color properties");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
#pragma warning disable SYSLIB0050
            var implementation = (GsplatRendererImpl)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(GsplatRendererImpl));
#pragma warning restore SYSLIB0050
            var properties = new MaterialPropertyBlock();
            properties.SetTexture("_GsplatRelightMap", Texture2D.whiteTexture);
            properties.SetVector("_GsplatRelightParams", Vector4.one);
            properties.SetVector("_GsplatRelightScreen", Vector4.one);
            typeof(GsplatRendererImpl).GetField("m_propertyBlock", flags).SetValue(implementation, properties);
            typeof(GsplatRendererImpl).GetField("m_gsplatAsset", flags).SetValue(implementation, asset);
            try
            {
                // Both explicit color APIs use this shared setup. A previous relit draw must
                // not leave lighting enabled when the next draw has no relighting source.
                typeof(GsplatRendererImpl).GetMethod("ConfigureColorProperties", flags).Invoke(
                    implementation, new object[] { gameObject.transform, false, 0, 1f, 1f, null, 1f, 2f, 1f });
                Assert.AreEqual(Vector4.zero, properties.GetVector("_GsplatRelightParams"));
                Assert.AreEqual(Vector4.zero, properties.GetVector("_GsplatRelightScreen"));
                Assert.AreSame(Texture2D.blackTexture, properties.GetTexture("_GsplatRelightMap"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

    }
}
