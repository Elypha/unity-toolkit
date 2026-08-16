using UnityEngine;
using UnityEditor;
using System.Text;

namespace Elypha.UnityToolkit
{
    public static class CopyObjectNames
    {
        private const string MenuPath = "GameObject/Elypha/Copy Selected Object Names";

        [MenuItem(MenuPath, false, 2)]
        private static void CopyNamesToClipboard(MenuCommand command)
        {
            GameObject[] selectedObjects = Selection.gameObjects;

            if (selectedObjects.Length == 0)
            {
                Debug.Log("No objects selected to copy names from.");
                return;
            }

            var objectNames = new StringBuilder();

            foreach (var obj in selectedObjects)
            {
                objectNames.AppendLine(obj.name);
            }

            // Remove the last newline character
            if (objectNames.Length > 0) objectNames.Length--;

            EditorGUIUtility.systemCopyBuffer = objectNames.ToString();
        }

        [MenuItem(MenuPath, true, 2)]
        private static bool ValidateCopyNamesToClipboard()
        {
            // ensure the menu item is only active if at least one GameObject is selected.
            return Selection.gameObjects.Length > 0;
        }
    }
}
