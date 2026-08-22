using System;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    /// <summary>
    /// Reuses Unity 2022.3's Scene Picking cell as an explicit build-state control.
    ///
    /// Modifier-clicks pass through to Unity's original Scene Picking control.
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyBuildStateToggle
    {
        private const string Untagged = "Untagged";
        private const string EditorOnly = "EditorOnly";
        private const string HarmonyId = "elypha.unity-toolkit.hierarchy-build-state-toggle";
        private const string PickingGuiTypeName = "UnityEditor.SceneVisibilityHierarchyGUI";
        private const string PickingGuiMethodName = "DrawGameObjectItemPicking";

        private static readonly GUIContent IncludedContent = new GUIContent(
            "✓",
            "参与 Build\n点击排除。修饰键点击使用 Scene Picking。");

        private static readonly GUIContent InactiveIncludedContent = new GUIContent(
            "✓",
            "参与 Build，Editor 默认 Inactive\n点击排除。修饰键点击使用 Scene Picking。");

        private static readonly GUIContent ExcludedContent = new GUIContent(
            "×",
            "不参与 Build\n点击恢复为 active + Untagged。修饰键点击使用 Scene Picking。");

        private static readonly GUIContent ActiveEditorOnlyContent = new GUIContent(
            "!",
            "警告：Active + EditorOnly\n点击恢复为 active + Untagged。修饰键点击使用 Scene Picking。");

        private static GUIStyle cellStyle;
        private static Harmony harmony;

        static HierarchyBuildStateToggle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Unpatch;

            // Harmony resolves the target method immediately; during assembly reload,
            // that initializes Unity's named styles without a current GUISkin.
            EditorApplication.hierarchyWindowItemOnGUI += ApplyPatchOnHierarchyGUI;
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void ApplyPatchOnHierarchyGUI(int instanceId, Rect selectionRect)
        {
            EditorApplication.hierarchyWindowItemOnGUI -= ApplyPatchOnHierarchyGUI;
            ApplyPatch();
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void ApplyPatch()
        {
            var pickingGuiType = typeof(EditorApplication).Assembly.GetType(PickingGuiTypeName, true);
            var target = pickingGuiType.GetMethod(
                PickingGuiMethodName,
                BindingFlags.NonPublic | BindingFlags.Static);

            if (target == null)
                throw new MissingMethodException(PickingGuiTypeName, PickingGuiMethodName);

            var prefix = typeof(HierarchyBuildStateToggle).GetMethod(
                nameof(DrawGameObjectItemPickingPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            harmony = new Harmony(HarmonyId);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        private static void Unpatch()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= ApplyPatchOnHierarchyGUI;
            harmony?.UnpatchAll(HarmonyId);
        }

        private static bool DrawGameObjectItemPickingPrefix(
            Rect __0,
            GameObject __1,
            bool __2,
            bool __3)
        {
            var gameObject = __1;
            if (gameObject == null
                || (gameObject.hideFlags & HideFlags.NotEditable) != 0
                || EditorApplication.isPlayingOrWillChangePlaymode
                || Event.current.modifiers != EventModifiers.None)
                return true;

            var tag = gameObject.tag;
            if (tag != Untagged && tag != EditorOnly)
                return true;

            var state = GetState(gameObject, tag);
            var clicked = GUI.Button(__0, GUIContent.none, GUIStyle.none);

            if (Event.current.type == EventType.Repaint)
                DrawCell(__0, state, __2);

            if (clicked)
            {
                var included = state == BuildState.Excluded
                               || state == BuildState.ActiveEditorOnly;
                SetIncluded(gameObject, included);
            }

            return false;
        }

        private static BuildState GetState(GameObject gameObject, string tag)
        {
            if (gameObject.activeSelf && tag == Untagged)
                return BuildState.Included;

            if (!gameObject.activeSelf && tag == EditorOnly)
                return BuildState.Excluded;

            if (!gameObject.activeSelf && tag == Untagged)
                return BuildState.InactiveIncluded;

            return BuildState.ActiveEditorOnly;
        }

        private static void SetIncluded(GameObject gameObject, bool included)
        {
            Undo.RecordObject(gameObject, included ? "Include GameObject in Build" : "Exclude GameObject from Build");

            gameObject.tag = included ? Untagged : EditorOnly;
            gameObject.SetActive(included);

            if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);

            EditorApplication.RepaintHierarchyWindow();
        }

        private static void DrawCell(Rect rect, BuildState state, bool hovered)
        {
            GUIContent content;
            Color foreground;

            switch (state)
            {
                case BuildState.Included:
                    content = IncludedContent;
                    foreground = new Color(0.35f, 0.78f, 0.72f, 0.95f);
                    break;
                case BuildState.InactiveIncluded:
                    content = InactiveIncludedContent;
                    foreground = new Color(0.88f, 0.78f, 0.42f, 0.95f);
                    break;
                case BuildState.Excluded:
                    content = ExcludedContent;
                    foreground = new Color(0.76f, 0.43f, 0.34f, 0.90f);
                    break;
                default:
                    content = ActiveEditorOnlyContent;
                    foreground = new Color(0.95f, 0.64f, 0.20f, 1f);
                    break;
            }

            var persistent = state == BuildState.Excluded
                             || state == BuildState.ActiveEditorOnly;
            if (!hovered && !persistent)
                return;

            var previousDepth = GUI.depth;
            var previousColor = GUI.color;
            GUI.depth = -1000;

            if (hovered)
            {
                EditorGUI.DrawRect(
                    new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f),
                    new Color(1f, 1f, 1f, 0.08f));
            }

            GUI.color = foreground;
            GUI.Label(rect, content, CellStyle);

            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }

        private static GUIStyle CellStyle
        {
            get
            {
                if (cellStyle == null)
                {
                    cellStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        clipping = TextClipping.Clip,
                        contentOffset = new Vector2(0f, -1f),
                        fontSize = 14,
                        padding = new RectOffset()
                    };
                    cellStyle.normal.textColor = Color.white;
                }

                return cellStyle;
            }
        }

        private enum BuildState
        {
            Included,
            InactiveIncluded,
            Excluded,
            ActiveEditorOnly
        }
    }
}
