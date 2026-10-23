using UnityEditor;
using UnityEngine;

namespace ComplexLODs
{
    /// <summary>
    /// Inspector for LOD profiles - deliberately minimal. The tool is fully
    /// automatic: quality preset, texture LODs and the output folder are the
    /// only settings.
    /// </summary>
    [CustomEditor(typeof(ComplexLODProfile), true)]
    public class ComplexLODProfileEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("preset"));
            var p = (ComplexLODProfile)target;
            EditorGUILayout.LabelField(
                p.EffectiveLodCount + " LODs, x" + p.EffectiveReductionRate.ToString("0.#") +
                " reduction per level", EditorStyles.miniLabel);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("textureLODs"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("outputFolder"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
