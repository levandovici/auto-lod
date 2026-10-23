using System;
using UnityEngine;

namespace ComplexLODs.Core
{
    /// <summary>
    /// Controls the quadric-error simplifier. All preservation weights are
    /// multipliers on top of the area-weighted face quadrics.
    /// </summary>
    [Serializable]
    public class SimplifyOptions
    {
        [Tooltip("Target triangle count of the simplified mesh.")]
        public int targetTriangleCount = 1000;

        [Tooltip("Positions closer than this are welded before simplifying. 0 = exact match only.")]
        public float weldTolerance = 0f;

        [Header("Openings / borders (doors, windows, entrances)")]
        [Tooltip("Extra plane constraint weight on mesh boundary edges (holes, openings).")]
        public float boundaryWeight = 500f;
        [Tooltip("Fully lock every vertex on a mesh boundary. Maximum protection for openings.")]
        public bool lockBoundaries = false;

        [Header("Sharp features")]
        [Tooltip("Dihedral angle (degrees) above which an edge is treated as a crease.")]
        public float creaseAngle = 35f;
        [Tooltip("Constraint weight applied along sharp crease edges.")]
        public float creaseWeight = 25f;
        [Tooltip("Constraint weight applied along UV / normal seam edges.")]
        public float seamWeight = 40f;

        [Header("Extremities / silhouette")]
        [Tooltip("Extra importance for vertices far from the bounding-box center (spires, roof tops, corners).")]
        [Range(0f, 50f)] public float extremesWeight = 6f;
        [Tooltip("Exponent shaping the radial extremity weight.")]
        [Range(1f, 8f)] public float extremesPower = 3f;
        [Tooltip("Lock the N highest vertices (+Y). Protects rooftop/parapet tips.")]
        public int lockTopVertices = 0;
        [Tooltip("Lock the 6 axis extremes of the bounding box.")]
        public bool lockBoundsExtremes = false;

        [Header("Importance mask")]
        [Tooltip("Read per-vertex importance from a vertex color channel (paint it on the source mesh).")]
        public bool useVertexColorImportance = false;
        [Tooltip("0=R 1=G 2=B 3=A channel used for the importance mask.")]
        [Range(0, 3)] public int vertexColorChannel = 3;
        [Tooltip("Maximum weight multiplier applied where the mask is 1.")]
        public float importanceMax = 10f;

        [Header("Validity")]
        [Tooltip("Reject collapses that would flip a triangle normal.")]
        public bool preventFlips = true;
        [Tooltip("Reject collapses joining two locked vertices.")]
        public bool neverCollapseLocked = true;
        [Tooltip("Link condition: reject collapses that would create non-manifold topology.")]
        public bool preserveTopology = true;
        [Tooltip("Reject collapses that merge two boundary loops, split a loop in two, or annihilate a small opening.")]
        public bool preserveBoundaryLoops = true;

        [Header("Output")]
        [Tooltip("Recalculate smooth normals instead of carrying interpolated ones.")]
        public bool recalculateNormals = false;

        [NonSerialized] public Action<int, int, int, int> progress;
        [NonSerialized] public Func<bool> cancelRequested;
    }

    public class SimplifyResult
    {
        public Mesh mesh;
        public int sourceTriangles;
        public int resultTriangles;
        public int sourceVertices;
        public int resultVertices;
        public int collapses;
        public float maxError;
        public float rmsError;
        public int sourceBoundaryComponents;
        public int resultBoundaryComponents;
        public bool reachedTarget;
        public bool cancelled;
        public int releaseStage;
        public double elapsedMs;
        public string warning;
    }
}
