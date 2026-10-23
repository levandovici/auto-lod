using System.IO;
using ComplexLODs.Core;
using UnityEditor;
using UnityEngine;

namespace ComplexLODs
{
    /// <summary>
    /// Main editor window for ComplexLODs.
    /// Window &gt; ComplexLODs, or right-click a MeshRenderer &gt; ComplexLODs...
    /// </summary>
    public class ComplexLODWindow : EditorWindow
    {
        private const string DefaultProfilePath = "Assets/ComplexLODs/DefaultLODProfile.asset";

        private GameObject target;
        private ComplexLODProfile profile;
        private Editor profileEditor;
        private Vector2 scroll;
        private ComplexLODGenerator.Report lastReport;
        private bool showReport;

        [MenuItem("Window/ComplexLODs")]
        public static void ShowWindow()
        {
            var win = GetWindow<ComplexLODWindow>(false, "ComplexLODs");
            win.minSize = new Vector2(320, 420);
            win.Show();
        }

        [MenuItem("CONTEXT/MeshRenderer/ComplexLODs...")]
        [MenuItem("CONTEXT/SkinnedMeshRenderer/ComplexLODs...")]
        private static void ContextOpen(MenuCommand cmd)
        {
            ShowWindow();
            var win = GetWindow<ComplexLODWindow>();
            win.target = ((Renderer)cmd.context).gameObject;
        }

        [MenuItem("CONTEXT/LODGroup/Generate Missing LODs/ComplexLODs")]
        private static void ContextFill(MenuCommand cmd)
        {
            var group = (LODGroup)cmd.context;
            ComplexLODGenerator.FillLODGroup(group, GetOrCreateDefaultProfile());
        }

        internal static ComplexLODProfile GetOrCreateDefaultProfile()
        {
            var p = AssetDatabase.LoadAssetAtPath<ComplexLODProfile>(DefaultProfilePath);
            if (p == null)
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "ComplexLODs"));
                p = CreateInstance<ComplexLODProfile>();
                AssetDatabase.CreateAsset(p, DefaultProfilePath);
                AssetDatabase.SaveAssets();
            }
            return p;
        }

        private void OnEnable()
        {
            if (profile == null) profile = GetOrCreateDefaultProfile();
            if (target == null && Selection.activeGameObject != null)
                target = Selection.activeGameObject;
        }

        private void OnGUI()
        {
            // Branded header bar
            Rect header = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(header, new Color(0.07f, 0.34f, 0.28f));
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 15
            };
            titleStyle.normal.textColor = new Color(0.55f, 1f, 0.85f);
            GUI.Label(new Rect(header.x + 10, header.y, header.width - 70, header.height), "COMPLEX LODS", titleStyle);
            var subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight
            };
            subStyle.normal.textColor = new Color(0.45f, 0.8f, 0.65f);
            GUI.Label(new Rect(header.x + header.width - 130, header.y, 120, header.height), "smart LOD pipeline", subStyle);
            EditorGUILayout.Space();

            // --- Target ---------------------------------------------------
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            target = (GameObject)EditorGUILayout.ObjectField("Root object", target, typeof(GameObject), true);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Use selection", EditorStyles.miniButton))
                target = Selection.activeGameObject;
            if (GUILayout.Button("Use selection's root", EditorStyles.miniButton) && Selection.activeGameObject != null)
                target = Selection.activeGameObject.transform.root.gameObject;
            EditorGUILayout.EndHorizontal();

            Rect drop = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drag & drop a scene object or prefab asset", EditorStyles.helpBox);
            if (drop.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.DragPerform)
                {
                    foreach (var o in DragAndDrop.objectReferences)
                        if (o is GameObject go) { target = go; break; }
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndVertical();

            // --- Profile ---------------------------------------------------
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Profile", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var newProfile = (ComplexLODProfile)EditorGUILayout.ObjectField(profile, typeof(ComplexLODProfile), false);
            if (EditorGUI.EndChangeCheck() || newProfile != profile)
            {
                profile = newProfile != null ? newProfile : GetOrCreateDefaultProfile();
                if (profileEditor != null) DestroyImmediate(profileEditor);
                profileEditor = null;
            }
            EditorGUILayout.EndVertical();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (profile != null)
            {
                if (profileEditor == null) profileEditor = Editor.CreateEditor(profile);
                profileEditor.OnInspectorGUI();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            // --- Generate ---------------------------------------------------
            using (new EditorGUI.DisabledScope(target == null || profile == null))
            {
                if (GUILayout.Button("Generate LODs", GUILayout.Height(32)))
                    lastReport = ComplexLODGenerator.Generate(target, profile);
            }

            if (lastReport != null)
            {
                showReport = EditorGUILayout.Foldout(showReport, "Report (" + lastReport.renderers.Count + " renderers)");
                if (showReport) DrawReport(lastReport);
            }
        }

        private static void DrawReport(ComplexLODGenerator.Report r)
        {
            foreach (var rr in r.renderers)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(rr.name + "  (LOD0: " + rr.sourceTriangles + " tris, " +
                    rr.sourceBoundaryComponents + " openings)", EditorStyles.boldLabel);
                for (int i = 0; i < rr.levels.Count; i++)
                {
                    var l = rr.levels[i];
                    string warn = !l.reachedTarget ? "  (target not reached)" : "";
                    string openings = l.boundaryComponents == rr.sourceBoundaryComponents
                        ? "openings OK"
                        : "openings " + l.boundaryComponents + "/" + rr.sourceBoundaryComponents;
                    EditorGUILayout.LabelField(
                        "  LOD" + (i + 1) + ": " + l.triangles + " tris, " + l.vertices + " verts | " +
                        openings + " | err " + l.rmsError.ToString("0.###") + " | " + l.ms.ToString("0") + " ms" + warn,
                        EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
        }
    }
}
