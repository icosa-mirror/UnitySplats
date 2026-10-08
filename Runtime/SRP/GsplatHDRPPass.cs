// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

#if GSPLAT_ENABLE_HDRP
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Gsplat
{
    public class GsplatHDRPPass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx)
        {
            if (!GsplatSettings.Instance.Valid ||
                !GsplatSorter.Instance.GatherGsplatsForCamera(ctx.hdCamera.camera))
                return;

            GsplatSorter.Instance.DispatchSort(ctx.cmd, ctx.hdCamera.camera);

            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer);
            GsplatSorter.Instance.RenderColor(ctx.cmd, ctx.hdCamera.camera);
            GsplatSorter.Instance.RenderDepth(ctx.cmd, ctx.hdCamera.camera);
        }
    }
}

#endif
