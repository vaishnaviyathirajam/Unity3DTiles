﻿/*
 * Copyright 2018, by the California Institute of Technology. ALL RIGHTS 
 * RESERVED. United States Government Sponsorship acknowledged. Any 
 * commercial use must be negotiated with the Office of Technology 
 * Transfer at the California Institute of Technology.
 * 
 * This software may be subject to U.S.export control laws.By accepting 
 * this software, the user agrees to comply with all applicable 
 * U.S.export laws and regulations. User has the responsibility to 
 * obtain export licenses, or other export authority as may be required 
 * before exporting such information to foreign countries or providing 
 * access to foreign persons.
 */
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace Unity3DTiles
{

    public class Unity3DTilesetTraversal
    {
        private int frameCount = 0;
        private Unity3DTileset tileset;
        AsyncOperation lastUnloadAssets = null;

        public Unity3DTilesetTraversal(Unity3DTileset tileset)
        {
            this.tileset = tileset;

        }

        public void Run()
        {
            frameCount++;
            tileset.LRUContent.MarkAllUnused();
            // Move any tiles with downloaded content to the ready state
            for (int i = 0; i < this.tileset.Options.MaximumTilesToProcessPerFrame && this.tileset.ProcessingQueue.Count != 0; i++)
            {
                var tile = this.tileset.ProcessingQueue.Dequeue();
                tile.Process();
            }

            SSECalculator sse = new SSECalculator(this.tileset);
            foreach (Camera cam in tileset.Options.ClippingCameras)
            {
                if (cam == null)
                {
                    continue;
                }
                // All of our bounding boxes and tiles are using tileset coordinate frame so lets get our frustrum planes
                // in tileset frame.  This way we only need to transform our planes, not every bounding box we need to check against
                Matrix4x4 cameraMatrix = cam.projectionMatrix * cam.worldToCameraMatrix * tileset.Behaviour.transform.localToWorldMatrix;
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cameraMatrix);

                sse.Configure(cam);
                Vector3 cameraPositionInTilesetFrame = tileset.Behaviour.transform.InverseTransformPoint(cam.transform.position);
                DetermineFrustumSet(tileset.Root, planes, sse, cameraPositionInTilesetFrame, PlaneClipMask.GetDefaultMask());
            }
            MarkUsedSetLeaves(tileset.Root);
            SkipTraversal(tileset.Root);
            UnloadUnusedContent();
            ToggleTiles(tileset.Root);
            this.tileset.RequestManager.Process();
        }

        class SSECalculator
        {
            private Camera cam;
            private float sseDenominator;    // used for perspective
            private float pixelSize;         // used for orthographic      
            private Unity3DTileset tileset;

            public SSECalculator(Unity3DTileset tileset)
            {
                this.tileset = tileset;
            }

            public void Configure(Camera cam)
            {
                this.cam = cam;
                if (cam.orthographic)
                {
                    pixelSize = Mathf.Max(cam.orthographicSize * 2, cam.orthographicSize * 2 * cam.aspect) / Mathf.Max(cam.pixelHeight, cam.pixelWidth);
                }
                else
                {
                    sseDenominator = 2 * Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);
                }                
            }

            public float PixelError(float tileError, float distFromCamera)
            {
                if (tileError == 0)
                {
                    return 0; // Leaf tiles have no screenspace error
                }
                if (this.cam.orthographic)
                {
                    return tileError / pixelSize;
                }
                else
                {
                    distFromCamera = Mathf.Max(distFromCamera, 0.00001f);
                    float error =  (tileError * cam.pixelHeight) / (distFromCamera * sseDenominator);
                    return error;
                }
            }

            float Fog(float distFromCamera, float density)
            {  
                float scalar = distFromCamera * density;
                return 1.0f - Mathf.Exp(-(scalar * scalar));
            }
        }
        
        /// <summary>
        /// After calling this method all tiles within a camera frustum will be marked as being in the frustum set and as being used starting at the root and stopping when:
        /// 1) A tile is found that has a screen space error less than or equal to our target SSE
        /// 2) MaxDepth is reached (Optional)
        /// Tiles with no content are ignored (i.e. we recurse to the nearest complete set of descendents with content)
        /// 
        /// If the LoadSiblings criteria is enabled we add additional tiles to the used set. Specificlly, if a tile is in the Frustum set, we gurantee that all of its siblings
        /// are marked as used.  If the siblings have empty conetent, we mark the first set of decendents that have content as used.  This is useful for tree traversals where
        /// we want to load content or do computation on tiles that are outside the users current view but results in a slower traversal. 
        ///  
        /// After this method is run, only tiles that are in the used set are considered by the rest of the traversal algorithm for this frame.  Unused tiles may be subject to being unloaded.
        /// </summary>
        /// <param name="tile"></param>
        /// <param name="planes"></param>
        /// <returns></returns>
        bool DetermineFrustumSet(Unity3DTile tile, Plane[] planes, SSECalculator sse, Vector3 cameraPosInTilesetFrame, PlaneClipMask mask)
        {
            // Reset frame state if needed
            tile.FrameState.Reset(this.frameCount);
            // Check to see if we are in the fustrum
            mask = tile.BoundingVolume.IntersectPlanes(planes, mask);
            if (mask.Intersection == IntersectionType.OUTSIDE)
            {
                return false;
            }
            // We are in frustum and at a rendereable level of detail, mark as used and as visible
            tile.MarkUsed();  //  mark content as used in LRUContent so it won't be unloaded
            tile.FrameState.InFrustumSet = true;
            this.tileset.Statistics.FrustumSetCount += 1;
            // Skip screen space error check if this node has empty content, 
            // we need to keep recursing until we find a node with content regardless of error
            if (!tile.HasEmptyContent)
            {
                // Check to see if this tile meets the on screen error level of detail requirement
                float distance = tile.BoundingVolume.DistanceTo(cameraPosInTilesetFrame);
                tile.FrameState.DistanceToCamera = Mathf.Min(distance, tile.FrameState.DistanceToCamera); // We take the min in case multiple cameras, reset dist to max float on frame reset
                tile.FrameState.ScreenSpaceError = sse.PixelError((float)tile.GeometricError, distance);
                if (tile.FrameState.ScreenSpaceError <= tileset.Options.MaximumScreenSpaceError)
                {
                    return true;
                }
            }
            if (tileset.Options.MaxDepth > 0 && tile.Depth >= tileset.Options.MaxDepth)
            {
                return true;
            }
            // Recurse on children
            bool anyChildUsed = false;
            for (int i = 0; i < tile.Children.Count; i++)
            {
                bool r = DetermineFrustumSet(tile.Children[i], planes, sse, cameraPosInTilesetFrame, mask);
                anyChildUsed = anyChildUsed || r;
            }
            // If any children are in the workingset, mark all of them as being used (siblings/atomic split criteria).  
            if (anyChildUsed && this.tileset.Options.LoadSiblings)
            {
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    MarkUsedRecursivley(tile.Children[i]);
                }
            }
            return true;
        }

        /// <summary>
        /// Mark this tile as used.  In the case that this tile has empty content,
        /// recurse until a complete set of leaf nodes with content are found
        /// This is only needed to handle the case of empty tiles
        /// </summary>
        /// <param name="tile"></param>
        void MarkUsedRecursivley(Unity3DTile tile)
        {
            // We need to reset as we go in case we find tiles that weren't previously explored
            // If they have already been reset this frame this has no effect
            tile.FrameState.Reset(this.frameCount);
            tile.MarkUsed();
            if(tile.HasEmptyContent)
            {
                for(int i = 0; i < tile.Children.Count; i++)
                {
                    MarkUsedRecursivley(tile.Children[i]);
                }
            }
        }

        /// <summary>
        /// Identify the deepest set of tiles that are in the used set this frame and mark them as "used set leaves"
        /// After this point we will not consider any tiles that are beyond a used set leaf.
        /// Leafs are the content we ideally want to show this frame
        /// </summary>
        /// <param name="tile"></param>
        void MarkUsedSetLeaves(Unity3DTile tile)
        {
            // A used leaf is a node that is used but has no children in the used set
            if(!tile.FrameState.IsUsedThisFrame(this.frameCount))
            {
                // Not used this frame, can't be a used leaf and neither can anything beneath us
                return;
            }
            this.tileset.Statistics.UsedSetCount += 1;
            // If any child is used, then we are not a leaf
            bool anyChildrenUsed = false;
            for(int i = 0; i < tile.Children.Count; i++)
            {
                anyChildrenUsed = anyChildrenUsed || tile.Children[i].FrameState.IsUsedThisFrame(this.frameCount);
            }
            if(!anyChildrenUsed)
            {
                tile.FrameState.IsUsedSetLeaf = true;
                if(!tile.HasEmptyContent)
                {
                    this.tileset.Statistics.LeafContentRequired++;
                    if(tile.ContentState == Unity3DTileContentState.READY)
                    {
                        this.tileset.Statistics.LeafContentLoaded++;
                    }
                }
            }
            else
            {
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    MarkUsedSetLeaves(tile.Children[i]);
                }
            }
        }

        /// <summary>
        /// Traverse the tree, request tiles, and enable visible tiles
        /// Skip parent tiles that have a screen space error larger than MaximumScreenSpaceError*SkipScreenSpaceErrorMultiplier
        /// </summary>
        /// <param name="tile"></param>
        void SkipTraversal(Unity3DTile tile)
        {
            if (!tile.FrameState.IsUsedThisFrame(this.frameCount))
            {
                return;
            }
            if (tile.FrameState.IsUsedSetLeaf)
            {
                if (tile.ContentState == Unity3DTileContentState.READY)
                {
                    if (tile.FrameState.InFrustumSet)
                    {
                        tile.FrameState.InRenderSet = true;
                        UpdateVisibleStatstics(tile);
                    }
                    tile.FrameState.InColliderSet = true;
                    this.tileset.Statistics.ColliderTileCount += 1;
                }
                else
                {
                    RequestTile(tile);
                }
                return;
            }

            // Draw a parent tile iff
            // 1) meets SSE cuttoff
            // 2) has content and is not empty
            // 3) one or more of its chidlren don't have content
            bool meetsSSE = tile.FrameState.ScreenSpaceError < (tileset.Options.MaximumScreenSpaceError * tileset.Options.SkipScreenSpaceErrorMultiplier);
            bool hasContent = tile.ContentState == Unity3DTileContentState.READY && !tile.HasEmptyContent;
            bool allChildrenHaveContent = true;
            for (int i = 0; i < tile.Children.Count; i++)
            {
                if (tile.Children[i].FrameState.IsUsedThisFrame(this.frameCount))
                {
                    bool childContent = tile.Children[i].ContentState == Unity3DTileContentState.READY || tile.HasEmptyContent;
                    allChildrenHaveContent = allChildrenHaveContent && childContent;
                }
            }
            if(meetsSSE && !hasContent)
            {
                RequestTile(tile);
            }
            if (meetsSSE && hasContent && !allChildrenHaveContent)
            {
                if (tile.FrameState.InFrustumSet)
                {
                    tile.FrameState.InRenderSet = true;
                    UpdateVisibleStatstics(tile);
                }
                tile.FrameState.InColliderSet = true;
                this.tileset.Statistics.ColliderTileCount += 1;
                // Request children
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    if (tile.Children[i].FrameState.IsUsedThisFrame(this.frameCount))
                    {
                        RequestTile(tile.Children[i]);
                    }
                }
                return;
            }
            // Otherwise keep decending
            for (int i = 0; i < tile.Children.Count; i++)
            {
                if (tile.Children[i].FrameState.IsUsedThisFrame(this.frameCount))
                {
                    SkipTraversal(tile.Children[i]);
                }
            }
        }

        void ToggleTiles(Unity3DTile tile)
        {
            // Only consider tiles that were used this frame or the previous frame
            if (tile.FrameState.IsUsedThisFrame(this.frameCount) || tile.FrameState.UsedLastFrame)
            {
                tile.FrameState.UsedLastFrame = false;
                if (!tile.FrameState.IsUsedThisFrame(this.frameCount))
                {
                    // This tile was active last frame but isn't active any more
                    if (tile.Content != null)
                    {
                        tile.Content.SetActive(false);
                    }
                }
                else 
                {
                    // this tile is in the used set this frame
                    if (tile.Content != null)
                    {
                        tile.Content.SetActive(tile.FrameState.InColliderSet || tile.FrameState.InRenderSet);
                        tile.Content.EnableColliders(tile.FrameState.InColliderSet);
                        if (this.tileset.Options.Show)
                        {
                            if (tile.FrameState.InRenderSet)
                            {
                                tile.Content.EnableRenderers(true);
                                tile.Content.SetShadowMode(this.tileset.Options.ShadowCastingMode, this.tileset.Options.RecieveShadows);
                                if (this.tileset.Style != null)
                                {
                                    tileset.Style.ApplyStyle(tile);
                                }
                            }
                            else if (tile.FrameState.InColliderSet && this.tileset.Options.ShadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                            {
                                tile.Content.SetShadowMode(UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly, false);
                            }
                        }
                    }
                    tile.FrameState.UsedLastFrame = true;
                }
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    ToggleTiles(tile.Children[i]);
                }
            }
        }

        void UpdateVisibleStatstics(Unity3DTile tile)
        {
            tileset.Statistics.VisibleTileCount += 1;
            if(tile.Content != null)
            {
                tileset.Statistics.VisibleFaces += tile.Content.FaceCount;
                tileset.Statistics.VisibleTextures += tile.Content.TextureCount;
                tileset.Statistics.VisiblePixels += tile.Content.PixelCount;
            }
        }


        /// <summary>
        /// Request a tile
        /// If the tile has siblings in the used set, request them at the same time since we will need all of them to split the parent tile
        /// Set request priority based on the closest sibling, request all siblings with the same priority since we need all of them to load before
        /// any of them can be made visible
        /// </summary>
        /// <param name="tile"></param>
        void RequestTile(Unity3DTile tile)
        {
            if (tile.Parent == null)
            {
                tile.RequestContent(-tile.FrameState.DistanceToCamera);
            }
            else
            {
                float minDist = float.MaxValue;
                for (int i = 0; i < tile.Parent.Children.Count; i++)
                {
                    if (tile.Parent.Children[i].FrameState.IsUsedThisFrame(this.frameCount))
                    {
                        minDist = Mathf.Min(tile.Parent.Children[i].FrameState.DistanceToCamera, minDist);
                    }
                }
                for (int i = 0; i < tile.Parent.Children.Count; i++)
                {
                    if (tile.Parent.Children[i].FrameState.IsUsedThisFrame(this.frameCount))
                    {
                        tile.Parent.Children[i].RequestContent(-minDist);
                    }
                }
            }
        }
        
        /// <summary>
        /// Unloads content from unused nodes
        /// </summary>
        void UnloadUnusedContent()
        {            
            if (this.tileset.LRUContent.Count > this.tileset.Options.LRUCacheMaxSize)
            {
                List<Unity3DTile> unused = this.tileset.LRUContent.GetUnused();
                var sortedUnused = unused.OrderBy(node => -node.Depth).ToArray();
                int nodesToUnload = (int)(this.tileset.Options.LRUCacheMaxSize * this.tileset.Options.LRUMaxFrameUnloadRatio);
                nodesToUnload = Math.Min(sortedUnused.Length, nodesToUnload);
                for (int i = 0; i < nodesToUnload; i++)
                {
                    sortedUnused[i].UnloadContent();
                    this.tileset.LRUContent.Remove(sortedUnused[i]);
                }
                if (lastUnloadAssets == null || lastUnloadAssets.isDone)
                {
                    lastUnloadAssets = Resources.UnloadUnusedAssets();
                }
            }
        }

        public void DrawDebug()
        {
            DebugDrawUsedSet(tileset.Root);
            DebugDrawFrustumSet(tileset.Root);
        }

        void DebugDrawUsedSet(Unity3DTile tile)
        {
            if (tile.FrameState.IsUsedThisFrame(this.frameCount))
            {
                if (tile.FrameState.IsUsedSetLeaf)
                {
                    tile.BoundingVolume.DebugDraw(Color.white, this.tileset.Behaviour.transform);
                }
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    DebugDrawUsedSet(tile.Children[i]);
                }
            }
        }

        void DebugDrawFrustumSet(Unity3DTile tile)
        {
            if (tile.FrameState.IsUsedThisFrame(this.frameCount) && tile.FrameState.InFrustumSet)
            {
                if (tile.FrameState.IsUsedSetLeaf)
                {
                    tile.BoundingVolume.DebugDraw(Color.green, this.tileset.Behaviour.transform);
                }
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    DebugDrawFrustumSet(tile.Children[i]);
                }
            }
        }
    }
}