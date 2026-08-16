using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Elypha.UnityToolkit
{
    public static class HierarchyClipboard
    {
        private const string CopyMenuPath = "GameObject/Elypha/Copy Hierarchy Shell";
        private const string PasteMenuPath = "GameObject/Elypha/Paste Hierarchy Shell as Child";
        private const string ClipboardPrefix = "ELY_HIERARCHY_CLIPBOARD_V1:";
        private const string TemporaryAssetPrefix = "Assets/__ElyphaHierarchyClipboard_";

        [MenuItem(CopyMenuPath, false, 3)]
        private static void CopyHierarchyShell()
        {
            GameObject source = Selection.activeGameObject;
            if (source == null || EditorUtility.IsPersistent(source))
            {
                ShowError("Select one GameObject in the Hierarchy.");
                return;
            }

            if (Selection.gameObjects.Length != 1)
            {
                ShowError("Copy Hierarchy Shell currently requires exactly one selected GameObject.");
                return;
            }

            if (PrefabUtility.IsPartOfPrefabInstance(source)
                && PrefabUtility.GetOutermostPrefabInstanceRoot(source) != source)
            {
                ShowError("The selected object is inside a Prefab instance. Select its outermost Prefab root or wrap it in a plain GameObject first.");
                return;
            }

            string temporaryAssetPath = CreateTemporaryAssetPath();
            try
            {
                bool savedSuccessfully;
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, temporaryAssetPath, out savedSuccessfully);
                if (!savedSuccessfully || prefab == null)
                {
                    throw new InvalidOperationException("Unity could not serialize the selected hierarchy as a temporary Prefab.");
                }

                string prefabYaml = File.ReadAllText(ToAbsoluteAssetPath(temporaryAssetPath), Encoding.UTF8);
                string payload = Convert.ToBase64String(Compress(Encoding.UTF8.GetBytes(prefabYaml)));
                GUIUtility.systemCopyBuffer = ClipboardPrefix + payload;

                Debug.Log("Copied hierarchy shell '" + source.name + "' to the system clipboard ("
                    + FormatBytes(payload.Length) + " clipboard text). No dependencies were copied.", source);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ShowError("Copy failed. See the Console for details.");
            }
            finally
            {
                DeleteTemporaryAsset(temporaryAssetPath);
            }
        }

        [MenuItem(CopyMenuPath, true, 3)]
        private static bool ValidateCopyHierarchyShell()
        {
            return Selection.gameObjects.Length == 1
                && Selection.activeGameObject != null
                && !EditorUtility.IsPersistent(Selection.activeGameObject);
        }

        [MenuItem(PasteMenuPath, false, 4)]
        private static void PasteHierarchyShellAsChild()
        {
            byte[] prefabBytes;
            string error;
            if (!TryReadClipboard(out prefabBytes, out error))
            {
                ShowError(error);
                return;
            }

            Transform parent = Selection.activeTransform;
            if (parent != null && (!parent.gameObject.scene.IsValid() || EditorUtility.IsPersistent(parent)))
            {
                parent = null;
            }

            string temporaryAssetPath = CreateTemporaryAssetPath();
            GameObject instance = null;
            try
            {
                File.WriteAllBytes(ToAbsoluteAssetPath(temporaryAssetPath), prefabBytes);
                AssetDatabase.ImportAsset(
                    temporaryAssetPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(temporaryAssetPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException("Unity could not import the hierarchy stored in the clipboard. A required root Prefab may be missing.");
                }

                if (parent != null)
                {
                    instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
                }
                else
                {
                    Scene activeScene = SceneManager.GetActiveScene();
                    if (!activeScene.IsValid() || !activeScene.isLoaded)
                    {
                        throw new InvalidOperationException("There is no loaded active Scene to paste into.");
                    }

                    instance = PrefabUtility.InstantiatePrefab(prefab, activeScene) as GameObject;
                }

                if (instance == null)
                {
                    throw new InvalidOperationException("Unity could not instantiate the temporary Prefab.");
                }

                PrefabUtility.UnpackPrefabInstance(
                    instance,
                    PrefabUnpackMode.OutermostRoot,
                    InteractionMode.AutomatedAction);

                Undo.RegisterCreatedObjectUndo(instance, "Paste Hierarchy Shell");
                Selection.activeGameObject = instance;
                EditorGUIUtility.PingObject(instance);
                EditorSceneManager.MarkSceneDirty(instance.scene);

                Debug.Log("Pasted hierarchy shell '" + instance.name + "'"
                    + (parent != null ? " under '" + parent.name + "'." : " at the active Scene root.")
                    + " Asset references were resolved only by their existing GUIDs.", instance);
            }
            catch (Exception exception)
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }

                Debug.LogException(exception);
                ShowError("Paste failed. See the Console for details.");
            }
            finally
            {
                DeleteTemporaryAsset(temporaryAssetPath);
            }
        }

        [MenuItem(PasteMenuPath, true, 4)]
        private static bool ValidatePasteHierarchyShellAsChild()
        {
            string clipboard = GUIUtility.systemCopyBuffer;
            return !string.IsNullOrEmpty(clipboard)
                && clipboard.StartsWith(ClipboardPrefix, StringComparison.Ordinal);
        }

        private static bool TryReadClipboard(out byte[] prefabBytes, out string error)
        {
            prefabBytes = null;
            error = null;

            string clipboard = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clipboard)
                || !clipboard.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            {
                error = "The clipboard does not contain an Elypha hierarchy shell.";
                return false;
            }

            try
            {
                string encoded = clipboard.Substring(ClipboardPrefix.Length);
                prefabBytes = Decompress(Convert.FromBase64String(encoded));
                if (prefabBytes.Length == 0)
                {
                    throw new InvalidDataException("The hierarchy payload is empty.");
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "The hierarchy data in the clipboard is invalid: " + exception.Message;
                return false;
            }
        }

        private static byte[] Compress(byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }

                return output.ToArray();
            }
        }

        private static byte[] Decompress(byte[] bytes)
        {
            using (var input = new MemoryStream(bytes))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        private static string CreateTemporaryAssetPath()
        {
            return TemporaryAssetPrefix + Guid.NewGuid().ToString("N") + ".prefab";
        }

        private static string ToAbsoluteAssetPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void DeleteTemporaryAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            if (AssetDatabase.DeleteAsset(assetPath))
            {
                return;
            }

            string absolutePath = ToAbsoluteAssetPath(assetPath);
            bool deletedFallbackFile = false;
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                deletedFallbackFile = true;
            }

            string metaPath = absolutePath + ".meta";
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
                deletedFallbackFile = true;
            }

            if (deletedFallbackFile)
            {
                AssetDatabase.Refresh();
            }
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024f).ToString("0.0") + " KiB";
            }

            return (bytes / (1024f * 1024f)).ToString("0.0") + " MiB";
        }

        private static void ShowError(string message)
        {
            EditorUtility.DisplayDialog("Hierarchy Clipboard", message, "OK");
        }
    }
}
