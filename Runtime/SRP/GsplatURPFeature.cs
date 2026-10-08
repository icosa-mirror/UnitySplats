// Originated from the GaussianSplatHDRPPass in aras-p/UnityGaussianSplatting by Aras Pranckevičius
// https://github.com/aras-p/UnityGaussianSplatting/blob/main/package/Runtime/GaussianSplatHDRPPass.cs
// Copyright (c) 2023 Aras Pranckevičius
// Modified by Yize Wu
// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

#if GSPLAT_ENABLE_URP

using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Gsplat
{
    class GsplatURPFeature : ScriptableRendererFeature
    {
        class GsplatSortPass : ScriptableRenderPass
        {
            public CommandBuffer CommandBuffer;

#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(GsplatSorter.k_passName, out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.AllowPassCulling(false);
                // Relighting binds camera-specific shader globals even on the CPU-sort fallback.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    GsplatSorter.Instance.DispatchSort(commandBuffer, data.CameraData.camera);
                });
            }
#endif

#if !UNITY_6000_4_OR_NEWER
#pragma warning disable CS0672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (CommandBuffer == null)
                    return;
                GsplatSorter.Instance.DispatchSort(CommandBuffer, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(CommandBuffer);
            }
#pragma warning restore CS0672
#endif
        }

        class GsplatColorPass : ScriptableRenderPass
        {
#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                using var builder = renderGraph.AddRasterRenderPass<PassData>("Gsplat.Color", out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    GsplatSorter.Instance.RenderColor(context.cmd, data.CameraData.camera);
                });
            }
#endif

#if !UNITY_6000_4_OR_NEWER
#pragma warning disable CS0672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get("Gsplat.Color");
                GsplatSorter.Instance.RenderColor(cmd, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0672
#endif
        }

        class GsplatDepthPass : ScriptableRenderPass
        {
#if UNITY_6000_0_OR_NEWER
            class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                using var builder =
                    renderGraph.AddRasterRenderPass<PassData>("Gsplat.Depth", out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    GsplatSorter.Instance.RenderDepth(context.cmd, data.CameraData.camera);
                });
            }
#endif

#if !UNITY_6000_4_OR_NEWER
#pragma warning disable CS0672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get("Gsplat.Depth");
                GsplatSorter.Instance.RenderDepth(cmd, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0672
#endif
        }

        GsplatSortPass m_sortPass;
        GsplatColorPass m_colorPass;
        GsplatDepthPass m_depthPass;
        bool m_hasGsplats;

        public override void Create()
        {
            // Splat color must blend before the alpha-tested depth surface is written. All three
            // passes run before ordinary URP transparents so later additive content sees depth.
            m_sortPass = new GsplatSortPass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 3)
            };
            m_colorPass = new GsplatColorPass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 2)
            };
            m_depthPass = new GsplatDepthPass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 1)
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_hasGsplats = GsplatSorter.Instance.GatherGsplatsForCamera(cameraData.camera);
            m_sortPass.CommandBuffer ??= new CommandBuffer { name = GsplatSorter.k_passName };
            m_sortPass.CommandBuffer.Clear();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!GsplatSettings.Instance.Valid || !m_hasGsplats)
                return;

            renderer.EnqueuePass(m_sortPass);
            renderer.EnqueuePass(m_colorPass);
            renderer.EnqueuePass(m_depthPass);
        }

        protected override void Dispose(bool disposing)
        {
            m_sortPass.CommandBuffer?.Dispose();
            m_sortPass.CommandBuffer = null;
            m_sortPass = null;
            m_colorPass = null;
            m_depthPass = null;
        }
    }
}

#endif
