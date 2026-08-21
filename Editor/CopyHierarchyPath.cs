using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    public static class CopyHierarchyPath {
        private const string MenuPath = "GameObject/Elypha/Copy Hierarchy Path";
        private const string ShortcutTag = "Elypha.CopyContextPath";

        [InitializeOnLoadMethod]
        private static void RegisterShortcutTag() {
            ShortcutManager.RegisterTag(ShortcutTag);
        }

        [MenuItem(MenuPath, false, 1)]
        private static void Copy(MenuCommand command) {
            var gameObject = command.context as GameObject ?? Selection.activeGameObject;
            Copy(gameObject);
        }

        [Shortcut(
            "Elypha/Copy Context Path",
            null,
            ShortcutTag,
            KeyCode.C,
            ShortcutModifiers.Action | ShortcutModifiers.Alt)]
        private static void CopyContextPath() {
            var gameObject = Selection.activeGameObject;
            if (gameObject != null && gameObject.scene.IsValid()) {
                Copy(gameObject);
                return;
            }

            var assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(assetPath)) {
                EditorApplication.ExecuteMenuItem("Assets/Copy Path");
            }
        }

        private static void Copy(GameObject gameObject) {
            if (gameObject == null || !gameObject.scene.IsValid()) {
                return;
            }

            var names = new List<string>();
            for (var current = gameObject.transform; current != null; current = current.parent) {
                names.Add(current.name);
            }
            names.Reverse();

            var hierarchyPath = string.Join("/", names);
            EditorGUIUtility.systemCopyBuffer = $"{gameObject.scene.name}.unity :: {hierarchyPath}";
        }

        [MenuItem(MenuPath, true, 1)]
        private static bool ValidateCopy() {
            var gameObject = Selection.activeGameObject;
            return gameObject != null && gameObject.scene.IsValid();
        }
    }
}
