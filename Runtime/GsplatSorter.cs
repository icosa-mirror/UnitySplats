// Copyright (c) 2025 Yize Wu
// Copyright (c) 2026 Keir Rice
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    public interface IGsplat
    {
        public Transform transform { get; }
        public uint RemainingCount { get; }
        public ISorterResource SorterResource { get; }
        public bool isActiveAndEnabled { get; }
        public bool Valid { get; }
        public bool ComputeSortRequired { get; }
        public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv);
        public void Draw();

        // Used by GsplatSorter to populate the global packed buffer.
        public GsplatResource GsplatResource { get; }
        public uint SplatCount { get; }
        public byte SHBands { get; }
    }

    public interface ISorterResource
    {
        public GraphicsBuffer OrderBuffer { get; }
        public GraphicsBuffer InputKeys { get; }
        public bool Initialized { get; set; }
        public void Dispose();
    }

    // some codes of this class originated from the GaussianSplatRenderSystem in aras-p/UnityGaussianSplatting by Aras Pranckevičius
    // https://github.com/aras-p/UnityGaussianSplatting/blob/main/package/Runtime/GaussianSplatRenderer.cs
    public class GsplatSorter
    {
        const int k_cpuSortKeyCount = 1 << 16;
        const int k_cpuCancellationCheckMask = (1 << 14) - 1;

        sealed class CpuCenterCache
        {
            public readonly Vector3[] Centers;
            public int RefCount;

            public CpuCenterCache(Vector3[] centers)
            {
                Centers = centers;
            }
        }

        sealed class CpuSortWorkspace
        {
            public ushort[] Keys = Array.Empty<ushort>();
            public uint[] Order = Array.Empty<uint>();
            public readonly int[] Histogram = new int[k_cpuSortKeyCount];

            public void EnsureCapacity(int count)
            {
                if (Keys.Length < count)
                    Keys = new ushort[count];
                if (Order.Length < count)
                    Order = new uint[count];
            }
        }

        readonly struct CpuSortRequest
        {
            public readonly Vector3[] Centers;
            public readonly uint[] SourceIds;
            public readonly int Count;
            public readonly int Generation;
            public readonly float M20;
            public readonly float M21;
            public readonly float M22;
            public readonly float M23;

            public CpuSortRequest(Vector3[] centers, uint[] sourceIds, int count, int generation,
                Matrix4x4 matrixMv)
            {
                Centers = centers;
                SourceIds = sourceIds;
                Count = count;
                Generation = generation;
                M20 = matrixMv.m20;
                M21 = matrixMv.m21;
                M22 = matrixMv.m22;
                M23 = matrixMv.m23;
            }

            public float Depth(Vector3 center) =>
                M20 * center.x + M21 * center.y + M22 * center.z + M23;

            public uint SourceId(int selectionIndex) =>
                SourceIds == null ? (uint)selectionIndex : SourceIds[selectionIndex];
        }

        readonly struct CpuSortResult
        {
            public readonly int Generation;
            public readonly int Count;

            public CpuSortResult(int generation, int count)
            {
                Generation = generation;
                Count = count;
            }
        }

        class Resource : ISorterResource
        {
            readonly bool m_usesGpuSort;
            bool m_disposed;

            Vector3[] m_cpuCenters;
            uint[] m_activeSourceIds;
            int m_cpuGeneration;
            CpuSortRequest m_pendingCpuRequest;
            bool m_hasPendingCpuRequest;
            Task<CpuSortResult> m_cpuTask;
            CancellationTokenSource m_cpuCancellation;
            CpuSortWorkspace m_idleCpuWorkspace = new();
            CpuSortWorkspace m_activeCpuWorkspace;
            readonly Texture2D m_orderTexture;
            readonly Color32[] m_orderTextureData;
            readonly int m_capacity;

            public GraphicsBuffer OrderBuffer { get; }
            public GraphicsBuffer InputKeys { get; private set; }
            public GsplatSortPass.SupportResources Resources { get; }
            public bool Initialized { get; set; }
            public bool UsesGpuSort => m_usesGpuSort;
            public GsplatAsset CpuAsset { get; private set; }
            public uint LastCpuRequestedCount { get; private set; } = uint.MaxValue;

            public Resource(uint count, GraphicsBuffer orderBuffer, bool useGpuSort, Texture2D orderTexture)
            {
                OrderBuffer = orderBuffer;
                m_usesGpuSort = useGpuSort;
                m_orderTexture = orderTexture;
                m_capacity = checked((int)count);
                if (m_orderTexture)
                    m_orderTextureData = new Color32[checked(m_orderTexture.width * m_orderTexture.height)];
                if (useGpuSort)
                {
                    InputKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)count, sizeof(uint));
                    Resources = GsplatSortPass.SupportResources.Load(count);
                }
                else
                {
                    InputKeys = null;
                    Resources = default;
                }

                Initialized = false;
            }

            public void BindCpuCenters(GsplatAsset asset, Vector3[] centers)
            {
                if (m_usesGpuSort || m_disposed)
                    return;

                CancelCpuWork();
                CpuAsset = asset;
                m_cpuCenters = centers ?? Array.Empty<Vector3>();
                m_cpuGeneration++;
                m_hasPendingCpuRequest = false;
                LastCpuRequestedCount = uint.MaxValue;

                // A valid identity order lets streaming assets render immediately while the
                // first background sort is still running. This method is called from the
                // renderer's Update path, never from an SRP render callback.
                var workspace = m_idleCpuWorkspace ?? new CpuSortWorkspace();
                workspace.EnsureCapacity(m_capacity);
                for (var i = 0; i < m_capacity; ++i)
                    workspace.Order[i] = (uint)i;
                if (m_capacity > 0)
                    UploadOrder(workspace.Order, m_capacity);
                m_idleCpuWorkspace = workspace;
                Initialized = true;
            }

            public void SetActiveSourceIds(uint[] sourceIds)
            {
                if (m_disposed)
                    return;
                if (sourceIds == null)
                    throw new ArgumentNullException(nameof(sourceIds));
                if (sourceIds.Length > m_capacity)
                    throw new ArgumentOutOfRangeException(nameof(sourceIds),
                        "Active source-ID count exceeds the renderer order-buffer capacity.");

                CancelCpuWork();
                m_cpuGeneration++;
                m_hasPendingCpuRequest = false;
                LastCpuRequestedCount = uint.MaxValue;
                m_activeSourceIds = sourceIds;
                if (sourceIds.Length > 0)
                    UploadOrder(sourceIds, sourceIds.Length);
                Initialized = true;
            }

            public void ClearActiveSourceIds()
            {
                if (m_disposed)
                    return;

                CancelCpuWork();
                m_cpuGeneration++;
                m_hasPendingCpuRequest = false;
                LastCpuRequestedCount = uint.MaxValue;
                m_activeSourceIds = null;

                if (m_usesGpuSort)
                {
                    // The next GPU sort seeds dense identity order before computing depth.
                    Initialized = false;
                    return;
                }

                var workspace = m_idleCpuWorkspace ?? new CpuSortWorkspace();
                workspace.EnsureCapacity(m_capacity);
                for (var i = 0; i < m_capacity; ++i)
                    workspace.Order[i] = (uint)i;
                if (m_capacity > 0)
                    UploadOrder(workspace.Order, m_capacity);
                m_idleCpuWorkspace = workspace;
                Initialized = true;
            }

            public void UnbindCpuCenters()
            {
                if (m_usesGpuSort)
                    return;

                CancelCpuWork();
                CpuAsset = null;
                m_cpuCenters = null;
                m_activeSourceIds = null;
                m_cpuGeneration++;
                m_hasPendingCpuRequest = false;
                LastCpuRequestedCount = uint.MaxValue;
                Initialized = false;
            }

            public void QueueCpuSort(Matrix4x4 matrixMv, uint requestedCount, bool force)
            {
                if (m_usesGpuSort || m_disposed || m_cpuCenters == null)
                    return;

                var selectableCount = m_activeSourceIds?.LongLength ?? m_cpuCenters.LongLength;
                var count = (uint)Math.Min((long)requestedCount, selectableCount);
                if (!force && count == LastCpuRequestedCount)
                    return;

                LastCpuRequestedCount = count;
                if (count == 0)
                    return;

                m_pendingCpuRequest = new CpuSortRequest(m_cpuCenters, m_activeSourceIds, (int)count,
                    m_cpuGeneration, matrixMv);
                m_hasPendingCpuRequest = true;
            }

            // Called only from GsplatPlayerLoopHook. Completed CPU work is uploaded here so
            // GraphicsBuffer.SetData is never invoked from a render-thread/SRP callback.
            public void UpdateCpuWork()
            {
                if (m_usesGpuSort || m_disposed)
                    return;

                CompleteCpuWork();
                if (m_cpuTask == null && m_hasPendingCpuRequest)
                    StartCpuWork();
            }

            void StartCpuWork()
            {
                var request = m_pendingCpuRequest;
                m_hasPendingCpuRequest = false;

                m_activeCpuWorkspace = m_idleCpuWorkspace ?? new CpuSortWorkspace();
                m_idleCpuWorkspace = null;
                m_activeCpuWorkspace.EnsureCapacity(request.Count);
                m_cpuCancellation = new CancellationTokenSource();
                var token = m_cpuCancellation.Token;
                var workspace = m_activeCpuWorkspace;
#if UNITY_WEBGL && !UNITY_EDITOR
                // Unity Web has no managed background threads. Complete the same counting sort
                // on the player thread; the result is uploaded by the next UpdateCpuWork call.
                try
                {
                    m_cpuTask = Task.FromResult(SortCpu(request, workspace, token));
                }
                catch (Exception exception)
                {
                    m_cpuTask = Task.FromException<CpuSortResult>(exception);
                }
#else
                m_cpuTask = Task.Run(() => SortCpu(request, workspace, token), token);
#endif
            }

            void CompleteCpuWork()
            {
                if (m_cpuTask is not { IsCompleted: true })
                    return;

                if (m_cpuTask.Status == TaskStatus.RanToCompletion)
                {
                    var result = m_cpuTask.Result;
                    if (result.Generation == m_cpuGeneration && result.Count > 0 &&
                        result.Count == (int)LastCpuRequestedCount &&
                        (OrderBuffer != null || m_orderTexture != null) && !m_disposed)
                    {
                        UploadOrder(m_activeCpuWorkspace.Order, result.Count);
                        Initialized = true;
                    }
                }
                else if (m_cpuTask.IsFaulted &&
                         m_cpuTask.Exception?.GetBaseException() is not OperationCanceledException)
                {
                    Debug.LogException(m_cpuTask.Exception?.GetBaseException());
                }

                m_cpuCancellation?.Dispose();
                m_cpuCancellation = null;
                m_cpuTask = null;
                if (m_idleCpuWorkspace == null ||
                    m_idleCpuWorkspace.Order.Length < m_activeCpuWorkspace.Order.Length)
                    m_idleCpuWorkspace = m_activeCpuWorkspace;
                m_activeCpuWorkspace = null;
            }

            void CancelCpuWork()
            {
                m_cpuCancellation?.Cancel();
            }

            public void Dispose()
            {
                m_disposed = true;
                CancelCpuWork();
                m_hasPendingCpuRequest = false;
                m_cpuCenters = null;
                m_activeSourceIds = null;
                if (m_orderTextureData != null)
                    Array.Clear(m_orderTextureData, 0, m_orderTextureData.Length);
                InputKeys?.Dispose();
                Resources.Dispose();
                InputKeys = null;
            }

            void UploadOrder(uint[] source, int count)
            {
                if (!m_orderTexture)
                {
                    OrderBuffer.SetData(source, 0, 0, count);
                    return;
                }

                for (int i = 0; i < count; ++i)
                    m_orderTextureData[i] = GsplatUtils.PackVertexTextureUInt(source[i]);
                GsplatUtils.UploadVertexDataTexture(m_orderTexture, m_orderTextureData,
                    m_orderTextureData.Length, 1, false);
            }
        }

        public static GsplatSorter Instance => s_instance ??= new GsplatSorter();
        static GsplatSorter s_instance;

        CommandBuffer m_commandBuffer;
        readonly HashSet<IGsplat> m_gsplats = new();
        readonly HashSet<Camera> m_camerasInjected = new();
        readonly List<IGsplat> m_activeGsplats = new();
        readonly HashSet<UnityEngine.Object> m_warnedUncompressed = new();
        readonly HashSet<UnityEngine.Object> m_warnedGlobalIndexLimit = new();
        readonly HashSet<UnityEngine.Object> m_warnedGlobalLayer = new();
        readonly HashSet<UnityEngine.Object> m_warnedGlobalRenderOrder = new();
        bool m_warnedGlobalCapacity;
        readonly Dictionary<GsplatAsset, CpuCenterCache> m_cpuCenterCaches = new();
        GsplatSortPass m_sortPass;
        Camera m_cpuSortCamera;
        bool m_warnedCpuFallback;
        public const string k_passName = "SortGsplats";
        const string k_depthPassName = "Gsplat.ComputeDepth";
        const string k_radixSortPassName = "Gsplat.RadixSort";

        readonly GsplatGlobalRenderer m_globalRenderer = new();

        /// <summary>True when 2+ active renderers are being globally merged this frame.</summary>
        public bool GlobalRenderEnabled { get; private set; }

        /// <summary>True when the wave-op GPU radix sorter is available.</summary>
        public bool GpuSortEnabled => !GsplatUtils.UseVertexDataTextures && m_sortPass is { Valid: true };

        /// <summary>True when sorting is using the portable CPU fallback.</summary>
        public bool CpuFallbackEnabled => !GpuSortEnabled && CpuFallbackSupported;

        public bool Valid => GpuSortEnabled || CpuFallbackEnabled;

        static bool CpuFallbackSupported =>
            SystemInfo.supportsInstancing &&
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null &&
            (GsplatUtils.UseVertexDataTextures || SystemInfo.maxGraphicsBufferSize > 0);

        public void InitSorter(ComputeShader computeShader)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                m_sortPass = null;
                return;
            }

            try
            {
                m_sortPass = computeShader ? new GsplatSortPass(computeShader) : null;
            }
            catch (Exception)
            {
                // Some backends strip wave-op kernels entirely. The fallback warning below
                // reports the selected path once a renderer actually needs it.
                m_sortPass = null;
            }
        }

        public void InitGlobal(GsplatGlobalMaterial globalMaterial)
        {
            m_globalRenderer.InitGlobal(globalMaterial);
        }

        public void RegisterGsplat(IGsplat gsplat)
        {
            if (m_gsplats.Count == 0)
            {
                if (!GraphicsSettings.currentRenderPipeline)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_gsplats.Add(gsplat);
            m_globalRenderer.MarkGlobalBuffersDirty();
        }

        public void UnregisterGsplat(IGsplat gsplat)
        {
            if (!m_gsplats.Remove(gsplat))
                return;

            if (gsplat is UnityEngine.Object obj)
            {
                m_warnedUncompressed.Remove(obj);
                m_warnedGlobalIndexLimit.Remove(obj);
                m_warnedGlobalLayer.Remove(obj);
                m_warnedGlobalRenderOrder.Remove(obj);
            }

            m_globalRenderer.MarkGlobalBuffersDirty();

            if (m_gsplats.Count != 0) return;

            if (m_camerasInjected != null)
            {
                if (m_commandBuffer != null)
                    foreach (var cam in m_camerasInjected.Where(cam => cam))
                        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_commandBuffer);
                m_camerasInjected.Clear();
            }

            m_activeGsplats.Clear();
            m_cpuSortCamera = null;
            m_commandBuffer?.Dispose();
            m_commandBuffer = null;
            Camera.onPreCull -= OnPreCullCamera;
            m_globalRenderer.DisposeGlobalBuffers();
        }

        public bool GatherGsplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            if (CpuFallbackEnabled)
                RememberCpuSortCamera(cam);

            m_activeGsplats.Clear();
            foreach (var gs in m_gsplats.Where(gs =>
                         gs is { isActiveAndEnabled: true, Valid: true } && gs.RemainingCount != 0))
                m_activeGsplats.Add(gs);

            return m_activeGsplats.Count != 0;
        }

        // Decides scene-wide whether the global merge can run this frame.
        bool CanRenderGlobally()
        {
            // renderer_id is packed into 8 bits ([31:24]); max 255 renderers.
            if (m_activeGsplats.Count > 255)
            {
                Debug.LogError(
                    "[GsplatSorter] Global merge supports at most 255 renderers. Falling back to per-renderer rendering.");
                return false;
            }

            var commonLayer = (m_activeGsplats[0] as Component)?.gameObject.layer ?? 0;
            ulong totalSplatCount = 0;

            // Global merge requires every active renderer to use SPARK compression.
            foreach (var gs in m_activeGsplats)
            {
                var obj = gs as UnityEngine.Object;
                // Keep drawing streaming assets independently until their full GPU buffers
                // are resident. Rebuilding the concatenated global buffers for every upload
                // batch causes large allocation/copy spikes and can expose partial ranges.
                if (gs.SorterResource?.OrderBuffer != null &&
                    gs.SplatCount < (uint)gs.SorterResource.OrderBuffer.count)
                    return false;

                if (gs.GsplatResource is not GsplatResourceSpark)
                {
                    if (m_warnedUncompressed.Add(obj))
                        Debug.LogWarning(
                            $"[GsplatSorter] '{obj?.name}' uses an uncompressed asset; global sort requires every active renderer to use SPARK compression. Disabling global sort for this scene — all renderers fall back to per-renderer rendering.");
                    return false;
                }

                // Empty explicit selections are still valid renderers, but bypass the global
                // merge so it never dispatches a zero-group merge. Individual drawing is a no-op.
                if (gs.RemainingCount == 0)
                    return false;

                var layer = (gs as Component)?.gameObject.layer ?? 0;
                if (layer != commonLayer)
                {
                    if (m_warnedGlobalLayer.Add(obj))
                        Debug.LogWarning(
                            $"[GsplatSorter] '{obj?.name}' is on layer {layer}, while another active splat renderer is on layer {commonLayer}. A single merged draw cannot preserve different camera-culling layers, so global sort is disabled for this scene.");
                    return false;
                }

                if (gs is GsplatRenderer { RenderOrder: not 0 } renderer)
                {
                    if (m_warnedGlobalRenderOrder.Add(obj))
                        Debug.LogWarning(
                            $"[GsplatSorter] '{renderer.name}' uses custom Render Order {renderer.RenderOrder}. A single merged draw cannot preserve per-renderer transparent queues, so global sort is disabled for this scene.");
                    return false;
                }

                // GlobalOrderBuffer packs the local splat ID into 24 bits.
                if (gs.SplatCount > 0x01000000u)
                {
                    if (m_warnedGlobalIndexLimit.Add(obj))
                        Debug.LogWarning(
                            $"[GsplatSorter] '{obj?.name}' has {gs.SplatCount} splats; global sort supports at most 16,777,216 splats per renderer. Falling back to per-renderer rendering.");
                    return false;
                }

                totalSplatCount += gs.SplatCount;
            }

            var maxBufferBytes = (ulong)Math.Max(0L, SystemInfo.maxGraphicsBufferSize);
            if (totalSplatCount > int.MaxValue || totalSplatCount * 16UL > maxBufferBytes)
            {
                if (!m_warnedGlobalCapacity)
                {
                    m_warnedGlobalCapacity = true;
                    Debug.LogWarning(
                        $"[GsplatSorter] The merged scene needs {totalSplatCount:N0} splats in one structured buffer, which exceeds this device's global-buffer capacity. Falling back to per-renderer rendering.");
                }
                return false;
            }

            return true;
        }

        public void MarkGlobalBuffersDirty() => m_globalRenderer.MarkGlobalBuffersDirty();

        void InitialClearCmdBuffer(Camera cam)
        {
            m_commandBuffer ??= new CommandBuffer { name = k_passName };
            if (!GraphicsSettings.currentRenderPipeline && cam &&
                !m_camerasInjected.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_commandBuffer);
                m_camerasInjected.Add(cam);
            }

            m_commandBuffer.Clear();
        }

        void OnPreCullCamera(Camera camera)
        {
            // This command buffer is persistent and can be attached to several built-in
            // cameras. Always erase the previous camera/frame's work before any early exit;
            // otherwise an all-empty LOD transition can replay dispatches that reference a
            // renderer which has since released its GraphicsBuffers.
            m_commandBuffer?.Clear();
            if (!Valid || !GsplatSettings.Instance.Valid || !GatherGsplatsForCamera(camera))
                return;

            // Gather records the camera for the next player-loop CPU update. Never upload a
            // GraphicsBuffer from this render callback.
            if (CpuFallbackEnabled)
                return;

            InitialClearCmdBuffer(camera);
            DispatchSort(m_commandBuffer, camera);
        }

        public void DispatchSort(CommandBuffer cmd, Camera camera)
        {
            if (!GpuSortEnabled)
            {
                if (CpuFallbackEnabled)
                    RememberCpuSortCamera(camera);
                return;
            }

            // Seed dense identity order before depth calculation. Interval-selected resources
            // are already initialized with their exact persistent source-ID map.
            foreach (var gs in m_activeGsplats)
            {
                if (gs.RemainingCount == 0 ||
                    gs.SorterResource is not Resource { UsesGpuSort: true } res || res.Initialized)
                    continue;
                m_sortPass.InitPayload(cmd, res.OrderBuffer, (uint)res.OrderBuffer.count);
                res.Initialized = true;
            }

            // --- Per-renderer depth computation ---
            cmd.BeginSample(k_depthPassName);
            foreach (var gs in m_activeGsplats)
            {
                if (gs.RemainingCount <= 0) continue;
                gs.ComputeDepth(cmd, camera.worldToCameraMatrix * gs.transform.localToWorldMatrix);
            }

            cmd.EndSample(k_depthPassName);

            // --- Per-renderer radix sort ---
            cmd.BeginSample(k_radixSortPassName);
            foreach (var gs in m_activeGsplats)
            {
                if (gs.SorterResource is not Resource { UsesGpuSort: true } res) continue;
                if (!gs.ComputeSortRequired || gs.RemainingCount <= 0)
                    continue;

                m_sortPass.Dispatch(cmd, new GsplatSortPass.Args
                {
                    Count = gs.RemainingCount,
                    MatrixMv = camera.worldToCameraMatrix * gs.transform.localToWorldMatrix,
                    InputKeys = res.InputKeys,
                    InputValues = res.OrderBuffer,
                    Resources = res.Resources
                });
            }

            cmd.EndSample(k_radixSortPassName);

            // --- Global K-way merge ---
            if (GlobalRenderEnabled)
                m_globalRenderer.DispatchMerge(cmd, m_activeGsplats);
        }

        // Called by GsplatPlayerLoopHook once per frame, before Unity's PostLateUpdate phase.
        // CPU sorting is scheduled and completed here, outside all camera render callbacks.
        public void Update()
        {
            if (!Valid || !GsplatSettings.Instance.Valid)
            {
                DisableGlobalRendering();
                return;
            }

            if (CpuFallbackEnabled)
            {
                DisableGlobalRendering();
                UpdateCpuFallback();
                DrawActiveIndividually();
                return;
            }

            // Rebuild before testing the count: the last camera gather can be stale when a
            // renderer is disabled or its asset is cleared before this player-loop phase.
            m_activeGsplats.Clear();
            foreach (var gs in m_gsplats.Where(gs => gs is { isActiveAndEnabled: true, Valid: true }))
            {
                if (gs is GsplatRenderer renderer)
                    renderer.PrepareForSortAndDraw();
                if (gs.RemainingCount != 0)
                    m_activeGsplats.Add(gs);
            }

            GlobalRenderEnabled = m_globalRenderer.Valid && GsplatSettings.Instance.EnableGlobalSort &&
                                  m_activeGsplats.Count >= 2 && CanRenderGlobally();
            if (GlobalRenderEnabled)
                m_globalRenderer.Update(m_activeGsplats);
            else
            {
                // A previous multi-renderer merge duplicates the complete packed/SH/order
                // data. Release that memory as soon as LOD filtering leaves no eligible
                // global set instead of retaining it beside the resident per-file buffers.
                m_globalRenderer.DisposeGlobalBuffers();
                m_globalRenderer.MarkGlobalBuffersDirty();
                DrawActiveIndividually();
            }
        }

        void DisableGlobalRendering()
        {
            GlobalRenderEnabled = false;
            m_globalRenderer.DisposeGlobalBuffers();
            m_globalRenderer.MarkGlobalBuffersDirty();
        }

        void DrawActiveIndividually()
        {
            foreach (var gs in m_activeGsplats)
                gs.Draw();
        }

        void UpdateCpuFallback()
        {
            if (!m_warnedCpuFallback && m_gsplats.Count != 0)
            {
                m_warnedCpuFallback = true;
                string vertexData = GsplatUtils.UseVertexDataTextures
                    ? $" Portable texture draw path is active ({SystemInfo.maxComputeBufferInputsVertex} vertex SSBO slots reported); splat data and draw order use RGBA8 byte-packed sampled textures."
                    : string.Empty;
                string scheduling = GsplatUtils.SupportsBackgroundCpuSort
                    ? "asynchronous"
                    : "main-thread";
                Debug.LogWarning(
                    $"[GsplatSorter] GPU wave-op radix sorting is unavailable on {SystemInfo.graphicsDeviceType}; using the {scheduling} 16-bit CPU counting-sort fallback.{vertexData} Global merge and compute cutouts are disabled in this mode.");
            }

            m_activeGsplats.Clear();
            foreach (var gs in m_gsplats.Where(gs => gs is { isActiveAndEnabled: true, Valid: true }))
            {
                if (gs is GsplatRenderer renderer)
                    renderer.PrepareForSortAndDraw();
                if (gs.RemainingCount == 0)
                    continue;
                m_activeGsplats.Add(gs);
                if (gs.SorterResource is Resource { UsesGpuSort: false } res)
                    res.UpdateCpuWork();
            }

            var camera = ResolveCpuSortCamera();
            if (!camera)
                return;

            var matrixV = camera.worldToCameraMatrix;
            foreach (var gs in m_activeGsplats)
            {
                if (gs.SorterResource is not Resource { UsesGpuSort: false } res)
                    continue;

                var countChanged = gs.RemainingCount != res.LastCpuRequestedCount;
                if (gs.ComputeSortRequired || countChanged || !res.Initialized)
                    res.QueueCpuSort(matrixV * gs.transform.localToWorldMatrix, gs.RemainingCount,
                        gs.ComputeSortRequired || !res.Initialized);
                res.UpdateCpuWork();
            }
        }

        void RememberCpuSortCamera(Camera camera)
        {
            if (!camera)
                return;

            // Prefer a game camera over Scene view/reflection cameras. One order buffer is
            // shared by all cameras, matching the package's existing per-renderer behavior.
            if (!m_cpuSortCamera || camera.cameraType == CameraType.Game ||
                m_cpuSortCamera.cameraType != CameraType.Game)
                m_cpuSortCamera = camera;
        }

        Camera ResolveCpuSortCamera()
        {
            if (m_cpuSortCamera && m_cpuSortCamera.isActiveAndEnabled)
                return m_cpuSortCamera;

            var main = Camera.main;
            if (main && main.isActiveAndEnabled)
            {
                m_cpuSortCamera = main;
                return main;
            }

            foreach (var camera in Camera.allCameras)
            {
                if (!camera || !camera.isActiveAndEnabled || camera.cameraType == CameraType.Preview)
                    continue;
                m_cpuSortCamera = camera;
                return camera;
            }

            m_cpuSortCamera = null;
            return null;
        }

        public ISorterResource CreateSorterResource(uint count, GraphicsBuffer orderBuffer,
            Texture2D orderTexture = null)
        {
            return new Resource(count, orderBuffer, GpuSortEnabled, orderTexture);
        }

        public void SetActiveSourceIds(ISorterResource sorterResource, uint[] sourceIds)
        {
            if (sorterResource is Resource resource)
                resource.SetActiveSourceIds(sourceIds);
        }

        public void ClearActiveSourceIds(ISorterResource sorterResource)
        {
            if (sorterResource is Resource resource)
                resource.ClearActiveSourceIds();
        }

        public void BindSorterAsset(ISorterResource sorterResource, GsplatAsset asset)
        {
            if (sorterResource is not Resource { UsesGpuSort: false } res || !asset)
                return;

            UnbindSorterAsset(sorterResource);
            if (!m_cpuCenterCaches.TryGetValue(asset, out var cache))
            {
                cache = new CpuCenterCache(CreateCpuCenters(asset));
                m_cpuCenterCaches.Add(asset, cache);
            }

            cache.RefCount++;
            res.BindCpuCenters(asset, cache.Centers);
        }

        public void UnbindSorterAsset(ISorterResource sorterResource)
        {
            if (sorterResource is not Resource { UsesGpuSort: false } res)
                return;

            var asset = res.CpuAsset;
            res.UnbindCpuCenters();
            // A destroyed Unity object compares equal to null, but its managed reference is
            // still the cache key and must be released.
            if (ReferenceEquals(asset, null) || !m_cpuCenterCaches.TryGetValue(asset, out var cache))
                return;

            cache.RefCount--;
            if (cache.RefCount <= 0)
                m_cpuCenterCaches.Remove(asset);
        }

        static Vector3[] CreateCpuCenters(GsplatAsset asset)
        {
            if (asset is GsplatAssetUncompressed uncompressed)
                return uncompressed.Positions ?? Array.Empty<Vector3>();

            if (asset is not GsplatAssetSpark spark || spark.PackedSplats == null)
                return Array.Empty<Vector3>();

            var packedSplats = spark.PackedSplats;
            var centers = new Vector3[packedSplats.Length];
            for (var i = 0; i < packedSplats.Length; ++i)
            {
                var packed = packedSplats[i];
                centers[i] = new Vector3(
                    HalfToFloat(packed.y & 0xffffu),
                    HalfToFloat((packed.y >> 16) & 0xffffu),
                    HalfToFloat(packed.z & 0xffffu));
            }

            return centers;
        }

        static float HalfToFloat(uint value)
        {
            var sign = (value & 0x8000u) << 16;
            var exponent = (value >> 10) & 0x1fu;
            var mantissa = value & 0x3ffu;
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    var unbiasedExponent = -14;
                    while ((mantissa & 0x400u) == 0)
                    {
                        mantissa <<= 1;
                        unbiasedExponent--;
                    }

                    mantissa &= 0x3ffu;
                    bits = sign | (uint)(unbiasedExponent + 127) << 23 | mantissa << 13;
                }
            }
            else if (exponent == 0x1fu)
            {
                bits = sign | 0x7f800000u | mantissa << 13;
            }
            else
            {
                bits = sign | (exponent + 112u) << 23 | mantissa << 13;
            }

            return BitConverter.Int32BitsToSingle((int)bits);
        }

        static CpuSortResult SortCpu(CpuSortRequest request, CpuSortWorkspace workspace,
            CancellationToken cancellationToken)
        {
            var minDepth = float.PositiveInfinity;
            var maxDepth = float.NegativeInfinity;
            for (var i = 0; i < request.Count; ++i)
            {
                if ((i & k_cpuCancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var sourceId = request.SourceId(i);
                var depth = request.Depth(request.Centers[sourceId]);
                if (float.IsNaN(depth) || float.IsInfinity(depth))
                    continue;
                if (depth < minDepth) minDepth = depth;
                if (depth > maxDepth) maxDepth = depth;
            }

            Array.Clear(workspace.Histogram, 0, workspace.Histogram.Length);
            var depthRange = maxDepth - minDepth;
            if (float.IsInfinity(minDepth) || depthRange <= 1e-6f)
            {
                for (var i = 0; i < request.Count; ++i)
                    workspace.Order[i] = request.SourceId(i);
                return new CpuSortResult(request.Generation, request.Count);
            }

            var keyScale = (k_cpuSortKeyCount - 1) / (double)depthRange;
            for (var i = 0; i < request.Count; ++i)
            {
                if ((i & k_cpuCancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var sourceId = request.SourceId(i);
                var depth = request.Depth(request.Centers[sourceId]);
                int key;
                if (float.IsNaN(depth) || float.IsPositiveInfinity(depth))
                    key = k_cpuSortKeyCount - 1;
                else if (float.IsNegativeInfinity(depth) || depth <= minDepth)
                    key = 0;
                else if (depth >= maxDepth)
                    key = k_cpuSortKeyCount - 1;
                else
                    key = (int)((depth - minDepth) * keyScale);

                workspace.Keys[i] = (ushort)key;
                workspace.Histogram[key]++;
            }

            var offset = 0;
            for (var key = 0; key < k_cpuSortKeyCount; ++key)
            {
                var count = workspace.Histogram[key];
                workspace.Histogram[key] = offset;
                offset += count;
            }

            // Unity view-space forward is -Z. Ascending view Z is therefore back-to-front.
            // Iterating source IDs in order makes equal-key buckets stable.
            for (var i = 0; i < request.Count; ++i)
            {
                if ((i & k_cpuCancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var key = workspace.Keys[i];
                workspace.Order[workspace.Histogram[key]++] = request.SourceId(i);
            }

            return new CpuSortResult(request.Generation, request.Count);
        }
    }
}
