using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class PerimeterWallGeneratorWindow : EditorWindow
{
    private const string DefaultParentName = "Generated Perimeter Walls";

    private GameObject targetObject;
    private Material wallMaterial;
    private string outputParentName = DefaultParentName;
    private float wallHeight = 0.5f;
    private float wallThickness = 0.1f;
    private float minimumSegmentLength = 0.25f;
    private float baseY = 0.05f;
    private bool overlapCorners = true;
    private float wallWeight = 1f;
    private bool clearPrevious = true;

    [MenuItem("Tools/Blockout/Perimeter Wall Generator")]
    private static void Open()
    {
        GetWindow<PerimeterWallGeneratorWindow>("Perimeter Walls");
    }

    [MenuItem("GameObject/Blockout/Generate Perimeter Walls", false, 10)]
    private static void GenerateFromSelection()
    {
        var window = GetWindow<PerimeterWallGeneratorWindow>("Perimeter Walls");
        window.targetObject = Selection.activeGameObject;
        window.Generate();
    }

    private void OnEnable()
    {
        if (targetObject == null)
        {
            targetObject = Selection.activeGameObject;
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Perimeter Wall Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);

        using (new EditorGUILayout.HorizontalScope())
        {
            targetObject = (GameObject)EditorGUILayout.ObjectField("Target", targetObject, typeof(GameObject), true);

            if (GUILayout.Button("Use Selection", GUILayout.Width(110f)))
            {
                targetObject = Selection.activeGameObject;
            }
        }

        wallHeight = Mathf.Max(0.01f, EditorGUILayout.FloatField("Wall Height", wallHeight));
        wallThickness = Mathf.Max(0.01f, EditorGUILayout.FloatField("Wall Thickness", wallThickness));
        baseY = EditorGUILayout.FloatField("Base Y", baseY);
        minimumSegmentLength = Mathf.Max(0.01f, EditorGUILayout.FloatField("Minimum Segment Length", minimumSegmentLength));
        overlapCorners = EditorGUILayout.Toggle("Overlap Corners", overlapCorners);
        wallWeight = Mathf.Max(0f, EditorGUILayout.FloatField("Wall Weight", wallWeight));
        wallMaterial = (Material)EditorGUILayout.ObjectField("Wall Material", wallMaterial, typeof(Material), false);
        outputParentName = EditorGUILayout.TextField("Output Parent Name", outputParentName);
        clearPrevious = EditorGUILayout.Toggle("Clear Previous", clearPrevious);

        EditorGUILayout.Space(8f);

        using (new EditorGUI.DisabledScope(targetObject == null))
        {
            if (GUILayout.Button("Generate Perimeter Walls", GUILayout.Height(28f)))
            {
                Generate();
            }
        }

        EditorGUILayout.HelpBox(
            "Select a GameObject that contains the floor primitives. The tool projects its child meshes onto X/Z, finds exposed mesh-outline segments, and creates rotated cube walls as children.",
            MessageType.Info);
    }

    private void Generate()
    {
        if (targetObject == null)
        {
            EditorUtility.DisplayDialog("Perimeter Walls", "Select a target GameObject first.", "OK");
            return;
        }

        var footprints = CollectFootprints(targetObject.transform);
        if (footprints.Count == 0)
        {
            EditorUtility.DisplayDialog("Perimeter Walls", "No usable MeshFilter footprints were found under the selected GameObject.", "OK");
            return;
        }

        if (clearPrevious)
        {
            ClearPreviousOutput(targetObject.transform);
        }

        var parent = new GameObject(string.IsNullOrWhiteSpace(outputParentName) ? DefaultParentName : outputParentName);
        Undo.RegisterCreatedObjectUndo(parent, "Create perimeter wall parent");
        parent.transform.SetParent(targetObject.transform, false);

        var edges = BuildBoundaryEdges(footprints, minimumSegmentLength);
        var wallCount = 0;

        foreach (var edge in edges)
        {
            wallCount++;
            CreateWall(parent.transform, edge, wallCount);
        }

        Selection.activeGameObject = parent;
        EditorSceneManager.MarkSceneDirty(targetObject.scene);
        Debug.Log($"Generated {wallCount} perimeter walls for '{targetObject.name}'.", parent);
    }

    private List<Footprint> CollectFootprints(Transform target)
    {
        var targetToLocal = target.worldToLocalMatrix;
        var meshFilters = target.GetComponentsInChildren<MeshFilter>(true);
        var footprints = new List<Footprint>();

        foreach (var meshFilter in meshFilters)
        {
            if (meshFilter.sharedMesh == null || IsGeneratedOutput(meshFilter.transform, target))
            {
                continue;
            }

            var points = new List<Vector2>();
            var matrix = targetToLocal * meshFilter.transform.localToWorldMatrix;
            var vertices = meshFilter.sharedMesh.vertices;

            foreach (var vertex in vertices)
            {
                var local = matrix.MultiplyPoint3x4(vertex);
                points.Add(new Vector2(local.x, local.z));
            }

            var hull = BuildConvexHull(points);
            if (hull.Count < 3)
            {
                continue;
            }

            footprints.Add(new Footprint(hull));
        }

        return footprints;
    }

    private bool IsGeneratedOutput(Transform current, Transform stopAt)
    {
        while (current != null && current != stopAt)
        {
            if (current.name == outputParentName ||
                current.name == DefaultParentName ||
                current.name.StartsWith("Perimeter Walls"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private void ClearPreviousOutput(Transform target)
    {
        var names = new HashSet<string> { outputParentName, DefaultParentName };
        var childrenToRemove = new List<GameObject>();

        foreach (Transform child in target)
        {
            if (names.Contains(child.name) || child.name.StartsWith("Perimeter Walls"))
            {
                childrenToRemove.Add(child.gameObject);
            }
        }

        foreach (var child in childrenToRemove)
        {
            Undo.DestroyObjectImmediate(child);
        }
    }

    private List<BoundaryEdge> BuildBoundaryEdges(List<Footprint> footprints, float minLength)
    {
        var edges = new List<BoundaryEdge>();

        for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
        {
            var footprint = footprints[footprintIndex];

            foreach (var sourceEdge in footprint.Edges)
            {
                var cuts = new List<float> { 0f, 1f };

                for (var otherIndex = 0; otherIndex < footprints.Count; otherIndex++)
                {
                    if (otherIndex == footprintIndex)
                    {
                        continue;
                    }

                    foreach (var otherEdge in footprints[otherIndex].Edges)
                    {
                        if (TryGetSegmentIntersectionFactor(sourceEdge.start, sourceEdge.end, otherEdge.start, otherEdge.end, out var factor))
                        {
                            cuts.Add(Mathf.Clamp01(factor));
                        }
                    }
                }

                cuts.Sort();
                cuts = DeduplicateFactors(cuts);

                for (var i = 0; i < cuts.Count - 1; i++)
                {
                    var startFactor = cuts[i];
                    var endFactor = cuts[i + 1];
                    var start = Vector2.Lerp(sourceEdge.start, sourceEdge.end, startFactor);
                    var end = Vector2.Lerp(sourceEdge.start, sourceEdge.end, endFactor);

                    if (Vector2.Distance(start, end) < minLength)
                    {
                        continue;
                    }

                    var midpoint = (start + end) * 0.5f;
                    var coveredByOther = false;

                    for (var otherIndex = 0; otherIndex < footprints.Count; otherIndex++)
                    {
                        if (otherIndex == footprintIndex)
                        {
                            continue;
                        }

                        if (footprints[otherIndex].ContainsOrTouches(midpoint))
                        {
                            coveredByOther = true;
                            break;
                        }
                    }

                    if (!coveredByOther)
                    {
                        edges.Add(new BoundaryEdge(start, end));
                    }
                }
            }
        }

        return edges;
    }

    private static List<float> DeduplicateFactors(List<float> values)
    {
        var result = new List<float>();

        foreach (var value in values)
        {
            if (result.Count == 0 || Mathf.Abs(value - result[result.Count - 1]) > 0.0001f)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool TryGetSegmentIntersectionFactor(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float factor)
    {
        factor = 0f;
        var r = b - a;
        var s = d - c;
        var denominator = Cross(r, s);

        if (Mathf.Abs(denominator) < 0.000001f)
        {
            return false;
        }

        var delta = c - a;
        var t = Cross(delta, s) / denominator;
        var u = Cross(delta, r) / denominator;

        if (t <= 0.0001f || t >= 0.9999f || u <= 0.0001f || u >= 0.9999f)
        {
            return false;
        }

        factor = t;
        return true;
    }

    private void CreateWall(Transform parent, BoundaryEdge edge, int index)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(wall, "Create perimeter wall");
        wall.name = $"Perimeter Wall {index:000}";
        wall.transform.SetParent(parent, false);

        var delta = edge.End - edge.Start;
        var length = delta.magnitude;
        var overlap = overlapCorners ? wallThickness * wallWeight : 0f;
        var angle = -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        var center = (edge.Start + edge.End) * 0.5f;

        wall.transform.localPosition = new Vector3(center.x, baseY + wallHeight * 0.5f, center.y);
        wall.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
        wall.transform.localScale = new Vector3(length + overlap, wallHeight, wallThickness);

        if (wallMaterial != null && wall.TryGetComponent<Renderer>(out var renderer))
        {
            renderer.sharedMaterial = wallMaterial;
        }
    }

    private static List<Vector2> BuildConvexHull(List<Vector2> points)
    {
        var sorted = points
            .Distinct()
            .OrderBy(p => p.x)
            .ThenBy(p => p.y)
            .ToList();

        if (sorted.Count <= 1)
        {
            return sorted;
        }

        var lower = new List<Vector2>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0f)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Vector2>();
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            var point = sorted[i];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0f)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static float Cross(Vector2 origin, Vector2 a, Vector2 b)
    {
        return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private sealed class Footprint
    {
        private readonly List<Vector2> points;
        private const float BoundaryTolerance = 0.001f;

        public Footprint(List<Vector2> points)
        {
            this.points = points;
            Min = new Vector2(points.Min(p => p.x), points.Min(p => p.y));
            Max = new Vector2(points.Max(p => p.x), points.Max(p => p.y));
        }

        public Vector2 Min { get; }
        public Vector2 Max { get; }

        public IEnumerable<(Vector2 start, Vector2 end)> Edges
        {
            get
            {
                for (var i = 0; i < points.Count; i++)
                {
                    yield return (points[i], points[(i + 1) % points.Count]);
                }
            }
        }

        public bool ContainsOrTouches(Vector2 point)
        {
            if (point.x < Min.x - BoundaryTolerance ||
                point.x > Max.x + BoundaryTolerance ||
                point.y < Min.y - BoundaryTolerance ||
                point.y > Max.y + BoundaryTolerance)
            {
                return false;
            }

            foreach (var edge in Edges)
            {
                if (DistanceToSegment(point, edge.start, edge.end) <= BoundaryTolerance)
                {
                    return true;
                }
            }

            var inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[i];
                var b = points[j];

                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var segment = b - a;
            var t = Vector2.Dot(point - a, segment) / segment.sqrMagnitude;
            t = Mathf.Clamp01(t);
            return Vector2.Distance(point, a + segment * t);
        }
    }

    private readonly struct BoundaryEdge
    {
        public readonly Vector2 Start;
        public readonly Vector2 End;

        public BoundaryEdge(Vector2 start, Vector2 end)
        {
            Start = start;
            End = end;
        }
    }
}
