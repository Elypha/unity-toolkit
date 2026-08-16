using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    public static class BlendShapeCopyMenu {
        private const string ClipboardHeader = "ShapeKeyCopyTool v1";

        [MenuItem("CONTEXT/SkinnedMeshRenderer/Elypha/Copy BlendShapes", false, 1)]
        private static void CopyBlendShapes(MenuCommand command) {
            if (!TryGetRendererAndMesh(command, out SkinnedMeshRenderer renderer, out Mesh mesh)) {
                return;
            }

            var text = new StringBuilder(ClipboardHeader);

            for (int index = 0; index < mesh.blendShapeCount; index++) {
                text.AppendLine();
                text.Append(mesh.GetBlendShapeName(index));
                text.Append(':');
                text.Append(renderer.GetBlendShapeWeight(index).ToString("R", CultureInfo.InvariantCulture));
            }

            GUIUtility.systemCopyBuffer = text.ToString();
            Debug.Log($"Copied {mesh.blendShapeCount} BlendShape values from '{renderer.name}' to the clipboard.", renderer);
        }

        [MenuItem("CONTEXT/SkinnedMeshRenderer/Elypha/Paste BlendShapes", false, 2)]
        private static void PasteBlendShapes(MenuCommand command) {
            PasteBlendShapes(command, partial: false);
        }

        [MenuItem("CONTEXT/SkinnedMeshRenderer/Elypha/Paste BlendShapes Partial", false, 3)]
        private static void PasteBlendShapesPartial(MenuCommand command) {
            PasteBlendShapes(command, partial: true);
        }

        private static void PasteBlendShapes(MenuCommand command, bool partial) {
            if (!TryGetRendererAndMesh(command, out SkinnedMeshRenderer renderer, out Mesh mesh)
                || !TryReadClipboard(out Dictionary<string, float> copiedValues)) {
                return;
            }

            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < mesh.blendShapeCount; index++) {
                targetNames.Add(mesh.GetBlendShapeName(index));
            }

            if (!partial && !targetNames.SetEquals(copiedValues.Keys)) {
                Debug.LogWarning(
                    "Paste BlendShapes cancelled because the BlendShape name sets differ. "
                    + "Use 'Paste BlendShapes Partial' to apply matching names only.",
                    renderer);
                return;
            }

            var valuesToApply = new List<(int index, float value)>();
            for (int index = 0; index < mesh.blendShapeCount; index++) {
                if (copiedValues.TryGetValue(mesh.GetBlendShapeName(index), out float value)) {
                    valuesToApply.Add((index, value));
                }
            }

            if (valuesToApply.Count == 0) {
                Debug.LogWarning("The clipboard and target have no matching BlendShape names.", renderer);
                return;
            }

            Undo.RecordObject(renderer, partial ? "Paste BlendShapes Partial" : "Paste BlendShapes");
            foreach (var (index, value) in valuesToApply) {
                renderer.SetBlendShapeWeight(index, value);
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            Debug.Log($"Pasted {valuesToApply.Count} BlendShape values to '{renderer.name}'.", renderer);
        }

        private static bool TryGetRendererAndMesh(
            MenuCommand command,
            out SkinnedMeshRenderer renderer,
            out Mesh mesh) {
            renderer = command.context as SkinnedMeshRenderer;
            mesh = renderer != null ? renderer.sharedMesh : null;

            if (mesh != null) {
                return true;
            }

            Debug.LogWarning("The selected SkinnedMeshRenderer has no mesh.", renderer);
            return false;
        }

        private static bool TryReadClipboard(out Dictionary<string, float> copiedValues) {
            copiedValues = new Dictionary<string, float>(StringComparer.Ordinal);

            using (var reader = new StringReader(GUIUtility.systemCopyBuffer)) {
                if (reader.ReadLine() != ClipboardHeader) {
                    Debug.LogWarning("The clipboard does not contain ShapeKeyCopyTool BlendShape data.");
                    return false;
                }

                string line;
                while ((line = reader.ReadLine()) != null) {
                    int separator = line.LastIndexOf(':');
                    if (separator <= 0
                        || !float.TryParse(
                            line[(separator + 1)..],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float value)
                        || float.IsNaN(value)
                        || float.IsInfinity(value)) {
                        Debug.LogWarning("The clipboard contains invalid BlendShape data.");
                        return false;
                    }

                    copiedValues[line.Substring(0, separator)] = value;
                }
            }

            return true;
        }
    }
}
