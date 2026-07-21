// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    /// <summary>Creates ready-to-render GameObjects when imported splat assets are dragged into a scene.</summary>
    [InitializeOnLoad]
    static class GsplatAssetDragAndDrop
    {
        const string UndoName = "Create UnitySplats Renderer";

        static GsplatAssetDragAndDrop()
        {
            // Keep the InstanceID overloads for Unity 6000.0/6000.1 compatibility. Unity 6000.3
            // marks them obsolete in favor of EntityId, but they remain the common Unity 6 API.
#pragma warning disable CS0618
            DragAndDrop.AddDropHandler(OnHierarchyDrop);
            DragAndDrop.AddDropHandler(OnSceneDrop);
#pragma warning restore CS0618
        }

        static DragAndDropVisualMode OnHierarchyDrop(
            int dropTargetInstanceId,
            HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects,
            bool perform)
        {
            GsplatAsset[] assets = GetDraggedAssets();
            if (assets.Length == 0)
                return DragAndDropVisualMode.None;

            if (perform)
            {
                Transform parent = ResolveHierarchyParent(
                    dropTargetInstanceId, dropMode, parentForDraggedObjects);
                CreateRenderers(assets, parent, parent ? parent.position : Vector3.zero, false);
            }

            return DragAndDropVisualMode.Copy;
        }

        static DragAndDropVisualMode OnSceneDrop(
            Object dropUpon,
            Vector3 worldPosition,
            Vector2 viewportPosition,
            Transform parentForDraggedObjects,
            bool perform)
        {
            GsplatAsset[] assets = GetDraggedAssets();
            if (assets.Length == 0)
                return DragAndDropVisualMode.None;

            if (perform)
            {
                if (!IsFinite(worldPosition))
                    worldPosition = SceneView.lastActiveSceneView
                        ? SceneView.lastActiveSceneView.pivot
                        : Vector3.zero;
                CreateRenderers(assets, parentForDraggedObjects, worldPosition, true);
            }

            return DragAndDropVisualMode.Copy;
        }

        static GsplatAsset[] GetDraggedAssets()
        {
            Object[] dragged = DragAndDrop.objectReferences;
            if (dragged == null || dragged.Length == 0)
                return System.Array.Empty<GsplatAsset>();

            var assets = new List<GsplatAsset>(dragged.Length);
            var seen = new HashSet<int>();
            foreach (Object draggedObject in dragged)
            {
                if (draggedObject is GsplatAsset asset && seen.Add(asset.GetInstanceID()))
                    assets.Add(asset);
            }
            return assets.ToArray();
        }

        static Transform ResolveHierarchyParent(
            int dropTargetInstanceId,
            HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects)
        {
            if (parentForDraggedObjects)
                return parentForDraggedObjects;

#pragma warning disable CS0618
            var target = EditorUtility.InstanceIDToObject(dropTargetInstanceId) as GameObject;
#pragma warning restore CS0618
            if (!target)
                return null;

            return (dropMode & HierarchyDropFlags.DropUpon) != 0
                ? target.transform
                : target.transform.parent;
        }

        static void CreateRenderers(
            IReadOnlyList<GsplatAsset> assets,
            Transform parent,
            Vector3 position,
            bool useWorldPosition)
        {
            DragAndDrop.AcceptDrag();
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            var created = new Object[assets.Count];

            for (int i = 0; i < assets.Count; ++i)
            {
                GsplatAsset asset = assets[i];
                var gameObject = new GameObject(asset.name);
                Undo.RegisterCreatedObjectUndo(gameObject, UndoName);

                if (parent)
                    Undo.SetTransformParent(gameObject.transform, parent, UndoName);

                if (useWorldPosition)
                    gameObject.transform.position = position;
                else
                    gameObject.transform.localPosition = Vector3.zero;
                gameObject.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
                gameObject.transform.localScale = Vector3.one;

                GsplatRenderer renderer = Undo.AddComponent<GsplatRenderer>(gameObject);
                var serializedRenderer = new SerializedObject(renderer);
                serializedRenderer.FindProperty(nameof(GsplatRenderer.GsplatAsset)).objectReferenceValue = asset;
                serializedRenderer.ApplyModifiedProperties();
                created[i] = gameObject;
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.objects = created;
        }

        static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }
    }
}
