using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    [InitializeOnLoad]
    internal static class BatchPackageImportQueue
    {
        [Serializable]
        private sealed class QueueState
        {
            public bool running;
            public string[] paths = Array.Empty<string>();
            public int nextIndex;
            public int inFlightIndex = -1;
        }

        private const string StateKey = "Elypha.UnityToolkit.BatchPackageImporter.QueueState";
        private const string StatusKey = "Elypha.UnityToolkit.BatchPackageImporter.Status";
        private static bool _waitingForEditor;
        private static int _recoveryFrames;

        public static event Action Changed;

        static BatchPackageImportQueue()
        {
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            EditorApplication.delayCall += ResumeAfterReload;
        }

        public static bool IsRunning => Load().running;

        public static string Status => SessionState.GetString(StatusKey, string.Empty);

        public static void Start(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) throw new ArgumentException("At least one package is required.", nameof(paths));
            if (IsRunning) throw new InvalidOperationException("A batch package import is already running.");

            var state = new QueueState
            {
                running = true,
                paths = paths.ToArray(),
                nextIndex = 0,
                inFlightIndex = -1,
            };
            Save(state);
            SetStatus($"Ready to import {state.paths.Length} packages.");
            ScheduleNext();
        }

        private static void ResumeAfterReload()
        {
            QueueState state = Load();
            if (!state.running) return;

            if (state.inFlightIndex >= 0)
            {
                // Importing scripts can reload this assembly before the managed completion callback runs.
                // Wait until Unity is idle and give the native callback two frames to arrive first.
                _recoveryFrames = 2;
            }

            ScheduleNext();
        }

        private static void ScheduleNext()
        {
            if (_waitingForEditor) return;
            _waitingForEditor = true;
            EditorApplication.update += ProcessWhenReady;
        }

        private static void ProcessWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (_recoveryFrames > 0)
            {
                _recoveryFrames--;
                return;
            }

            EditorApplication.update -= ProcessWhenReady;
            _waitingForEditor = false;

            QueueState state = Load();
            if (!state.running) return;

            if (state.inFlightIndex >= 0)
            {
                int recoveredIndex = state.inFlightIndex;
                state.inFlightIndex = -1;
                Save(state);
                Debug.Log($"[BatchPackageImporter] Resuming after script reload; package {recoveredIndex + 1}/{state.paths.Length} finished before the callback could be retained.");
            }

            ImportNext();
        }

        private static void ImportNext()
        {
            QueueState state = Load();
            if (!state.running || state.inFlightIndex >= 0) return;

            if (state.nextIndex >= state.paths.Length)
            {
                Finish(state.paths.Length);
                return;
            }

            int index = state.nextIndex;
            string path = state.paths[index];
            if (!File.Exists(path))
            {
                Fail($"Package file disappeared before import: {path}");
                return;
            }

            state.nextIndex = index + 1;
            state.inFlightIndex = index;
            Save(state);
            SetStatus($"Importing {index + 1}/{state.paths.Length}: {Path.GetFileName(path)}");
            Debug.Log($"[BatchPackageImporter] Importing {index + 1}/{state.paths.Length}: {path}");

            try
            {
                AssetDatabase.ImportPackage(path, false);
            }
            catch (Exception exception)
            {
                Fail($"Failed to start import for '{path}': {exception.Message}");
            }
        }

        private static void OnImportCompleted(string packageName)
        {
            QueueState state = Load();
            if (!state.running || state.inFlightIndex < 0) return;

            int completedIndex = state.inFlightIndex;
            state.inFlightIndex = -1;
            Save(state);
            SetStatus($"Imported {completedIndex + 1}/{state.paths.Length}: {packageName}");
            ScheduleNext();
        }

        private static void OnImportFailed(string packageName, string errorMessage)
        {
            QueueState state = Load();
            if (!state.running || state.inFlightIndex < 0) return;
            Fail($"Import failed for '{packageName}': {errorMessage}");
        }

        private static void OnImportCancelled(string packageName)
        {
            QueueState state = Load();
            if (!state.running || state.inFlightIndex < 0) return;
            Fail($"Import was cancelled for '{packageName}'.");
        }

        private static void Finish(int count)
        {
            var state = new QueueState();
            Save(state);
            SetStatus($"Imported {count} packages in order. Final refresh requested.");
            Debug.Log($"[BatchPackageImporter] Imported {count} packages. Requesting the final AssetDatabase.Refresh.");
            AssetDatabase.Refresh();
        }

        private static void Fail(string message)
        {
            var state = new QueueState();
            Save(state);
            SetStatus(message);
            Debug.LogError($"[BatchPackageImporter] {message} The queue stopped; later packages were not imported.");
            AssetDatabase.Refresh();
        }

        private static QueueState Load()
        {
            string json = SessionState.GetString(StateKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return new QueueState();

            try
            {
                return JsonUtility.FromJson<QueueState>(json) ?? new QueueState();
            }
            catch
            {
                return new QueueState();
            }
        }

        private static void Save(QueueState state)
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(state));
            Changed?.Invoke();
        }

        private static void SetStatus(string status)
        {
            SessionState.SetString(StatusKey, status ?? string.Empty);
            Changed?.Invoke();
        }
    }
}
