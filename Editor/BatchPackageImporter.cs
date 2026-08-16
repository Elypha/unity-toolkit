using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    public sealed class BatchPackageImporter : EditorWindow
    {
        private const float IndexWidth = 28.0f;
        private const float MetricColumnPadding = 8.0f;
        private const float IndexPackageGap = MetricColumnPadding;
        private const float MinimumPackageWidth = 100.0f;

        [SerializeField] private List<string> _packagePaths = new List<string>();
        private BatchPackageScanReport _scan;
        private string _scanError;
        private Vector2 _scrollPosition;
        private ReorderableList _packageList;
        private readonly Dictionary<string, bool> _detailsExpanded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _packageSizeBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private bool _queueWasRunning;

        private GUIStyle _tableHeaderStyle;
        private GUIStyle _tableHeaderLeftStyle;
        private GUIStyle _indexStyle;
        private GUIStyle _tableValueStyle;
        private GUIStyle _tablePackageStyle;
        private GUIStyle _tableSummaryValueStyle;
        private GUIStyle _tableSummaryPackageStyle;
        private GUIStyle _extensionStyle;
        private GUIStyle _extensionSeparatorStyle;
        private GUIStyle _detailHeaderStyle;
        private GUIStyle _detailHeaderLeftStyle;
        private GUIStyle _detailMarkerStyle;
        private GUIStyle _detailPathStyle;
        private GUIStyle _conflictSeverityStyle;
        private GUIStyle _errorSeverityStyle;
        private Font _indexFont;
        private float _entriesWidth;
        private float _newWidth;
        private float _sameWidth;
        private float _projectWidth;
        private float _repeatWidth;
        private float _queueWidth;
        private float _conflictWidth;
        private float _errorWidth;
        private bool _metricWidthsReady;

        [MenuItem("Elypha/Batch Package Importer", false, 1)]
        public static void ShowWindow()
        {
            var window = GetWindow<BatchPackageImporter>("Batch Package Importer");
            window.minSize = new Vector2(520.0f, 360.0f);
        }

        private void OnEnable()
        {
            if (_packagePaths == null) _packagePaths = new List<string>();
            ResetStyles();
            minSize = new Vector2(520.0f, 360.0f);
            RefreshPackageSizeCache();
            CreatePackageList();
            BatchPackageImportQueue.Changed += OnQueueChanged;
            _queueWasRunning = BatchPackageImportQueue.IsRunning;
        }

        private void OnDisable()
        {
            BatchPackageImportQueue.Changed -= OnQueueChanged;
            if (_indexFont != null) DestroyImmediate(_indexFont);
        }

        private void CreatePackageList()
        {
            _packageList = new ReorderableList(_packagePaths, typeof(string), true, true, false, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 4.0f,
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, $"Packages ({_packagePaths.Count}) — drag rows to change import order"),
                drawElementCallback = (rect, index, isActive, isFocused) =>
                {
                    if (index < 0 || index >= _packagePaths.Count) return;
                    rect.y += 2.0f;
                    rect.height = EditorGUIUtility.singleLineHeight;
                    var indexRect = new Rect(rect.x, rect.y, IndexWidth, rect.height);
                    var packageRect = new Rect(indexRect.xMax + IndexPackageGap, rect.y, rect.width - IndexWidth - IndexPackageGap, rect.height);
                    EditorGUI.LabelField(indexRect, (index + 1).ToString(), _indexStyle);
                    EditorGUI.LabelField(packageRect, new GUIContent(GetPackageLabel(_packagePaths[index]), _packagePaths[index]), _tablePackageStyle);
                },
                onReorderCallback = list => InvalidateValidation(),
                onRemoveCallback = list =>
                {
                    ReorderableList.defaultBehaviours.DoRemoveButton(list);
                    RefreshPackageSizeCache();
                    InvalidateValidation();
                },
                onCanRemoveCallback = list => list.count > 0,
            };
        }

        private void EnsureStyles()
        {
            if (_tableHeaderStyle == null) _tableHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleRight };
            if (_tableHeaderLeftStyle == null) _tableHeaderLeftStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
            if (_indexStyle == null)
            {
                _indexStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };
                _indexFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" }, 12);
                if (_indexFont != null)
                {
                    _indexFont.hideFlags = HideFlags.HideAndDontSave;
                    _indexStyle.font = _indexFont;
                }
            }
            if (_tableValueStyle == null) _tableValueStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };
            if (_tablePackageStyle == null) _tablePackageStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
            if (_tableSummaryValueStyle == null) _tableSummaryValueStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleRight };
            if (_tableSummaryPackageStyle == null) _tableSummaryPackageStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
            if (_extensionStyle == null) _extensionStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };
            if (_extensionSeparatorStyle == null) _extensionSeparatorStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter };
            if (_detailHeaderStyle == null) _detailHeaderStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
            if (_detailHeaderLeftStyle == null) _detailHeaderLeftStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft };
            if (_detailMarkerStyle == null) _detailMarkerStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
            if (_detailPathStyle == null) _detailPathStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            if (_conflictSeverityStyle == null) _conflictSeverityStyle = CreateColouredStyle(EditorStyles.boldLabel, new Color(0.92f, 0.52f, 0.28f));
            if (_errorSeverityStyle == null) _errorSeverityStyle = CreateColouredStyle(EditorStyles.boldLabel, new Color(0.92f, 0.38f, 0.34f));
            if (!_metricWidthsReady) CalculateMetricWidths();
        }

        private void ResetStyles()
        {
            _tableHeaderStyle = null;
            _tableHeaderLeftStyle = null;
            _indexStyle = null;
            _tableValueStyle = null;
            _tablePackageStyle = null;
            _tableSummaryValueStyle = null;
            _tableSummaryPackageStyle = null;
            _extensionStyle = null;
            _extensionSeparatorStyle = null;
            _detailHeaderStyle = null;
            _detailHeaderLeftStyle = null;
            _detailMarkerStyle = null;
            _detailPathStyle = null;
            _conflictSeverityStyle = null;
            _errorSeverityStyle = null;
            _metricWidthsReady = false;
        }

        private void CalculateMetricWidths()
        {
            _entriesWidth = CalculateMetricWidth("Assets");
            _newWidth = CalculateMetricWidth("New");
            _sameWidth = CalculateMetricWidth("Same");
            _projectWidth = CalculateMetricWidth("Project");
            _repeatWidth = CalculateMetricWidth("Repeat");
            _queueWidth = CalculateMetricWidth("Queue");
            _conflictWidth = CalculateMetricWidth("Conflict");
            _errorWidth = CalculateMetricWidth("Error");
            _metricWidthsReady = true;
        }

        private float CalculateMetricWidth(string header)
        {
            float headerWidth = _tableHeaderStyle.CalcSize(new GUIContent(header)).x;
            float numberWidth = _tableValueStyle.CalcSize(new GUIContent("0000")).x;
            return Mathf.Ceil(Mathf.Max(headerWidth, numberWidth) + MetricColumnPadding);
        }

        private static GUIStyle CreateColouredStyle(GUIStyle source, Color colour)
        {
            var style = new GUIStyle(source);
            style.normal.textColor = colour;
            return style;
        }

        private void OnQueueChanged()
        {
            bool running = BatchPackageImportQueue.IsRunning;
            if (_queueWasRunning && !running) InvalidateValidation();
            _queueWasRunning = running;
            Repaint();
        }

        private void OnGUI()
        {
            EnsureStyles();
            bool running = BatchPackageImportQueue.IsRunning;
            DrawQueueStatus(running);

            EditorGUI.BeginDisabledGroup(running);
            DrawDropArea();
            EditorGUI.EndDisabledGroup();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, false, false, GUILayout.ExpandHeight(true));
            EditorGUI.BeginDisabledGroup(running);
            _packageList.DoLayoutList();
            EditorGUI.EndDisabledGroup();
            DrawValidationResult();
            EditorGUILayout.EndScrollView();

            DrawActions(running);
        }

        private void DrawQueueStatus(bool running)
        {
            string status = BatchPackageImportQueue.Status;
            if (string.IsNullOrEmpty(status)) return;

            MessageType type = running ? MessageType.Info :
                status.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || status.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 || status.IndexOf("disappeared", StringComparison.OrdinalIgnoreCase) >= 0
                    ? MessageType.Error
                    : MessageType.Info;
            EditorGUILayout.HelpBox(status, type);
        }

        private void DrawDropArea()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0.0f, 76.0f, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "Drag .unitypackage files here\nDropping and reordering do not scan package contents.", new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter });

            Event current = Event.current;
            if (!dropArea.Contains(current.mousePosition)) return;

            if (current.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDrop.paths.Any(IsUnityPackage) ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                current.Use();
            }
            else if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                bool changed = false;
                foreach (string path in DragAndDrop.paths.Where(IsUnityPackage))
                {
                    string fullPath = Path.GetFullPath(path);
                    if (_packagePaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) continue;
                    _packagePaths.Add(fullPath);
                    CachePackageSize(fullPath);
                    changed = true;
                }

                if (changed) InvalidateValidation();
                current.Use();
            }
        }

        private void DrawValidationResult()
        {
            EditorGUILayout.Space(10.0f);
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(_scanError))
            {
                DrawValidationStatus("Validation failed", _scanError, new Color(0.92f, 0.42f, 0.38f));
                return;
            }

            if (_scan == null)
            {
                string message = _packagePaths.Count == 0
                    ? "Add packages to build an import queue."
                    : "Click Validate to inspect package contents, project and queue changes, and detected problems.";
                DrawValidationStatus("Not validated", message, EditorGUIUtility.isProSkin ? new Color(0.72f, 0.72f, 0.72f) : new Color(0.32f, 0.32f, 0.32f));
                return;
            }

            DrawValidationState();
            DrawPackageTable();
        }

        private void DrawValidationState()
        {
            if (_scan.ErrorCount > 0)
            {
                DrawValidationStatus("Import blocked", "One or more packages could not be fully inspected. Open the affected rows and inspect them manually.", new Color(0.92f, 0.42f, 0.38f));
            }
            else if (_scan.ConflictCount > 0)
            {
                DrawValidationStatus("Import blocked", "GUID or path ownership conflicts were detected. Open the affected rows and inspect them manually.", new Color(0.92f, 0.58f, 0.30f));
            }
            else if (_scan.ChangeCount > 0)
            {
                DrawValidationStatus("Ready with changes", "Changes to current project assets or earlier package contents were detected. Expand a package to inspect asset and meta changes.", new Color(0.92f, 0.68f, 0.28f));
            }
            else
            {
                DrawValidationStatus("Ready to import", "No project changes, queue changes or problems were detected.", new Color(0.38f, 0.76f, 0.42f));
            }
        }

        private static void DrawValidationStatus(string title, string message, Color colour)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel);
            titleStyle.normal.textColor = colour;
            EditorGUILayout.LabelField(title, titleStyle);
            EditorGUILayout.LabelField(message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawPackageTable()
        {
            EditorGUILayout.Space(5.0f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawPackageTableHeader();

            for (int index = 0; index < _scan.Packages.Count; index++)
            {
                BatchPackageReport package = _scan.Packages[index];
                string key = package.PackagePath ?? string.Empty;
                bool hasDetails = package.ChangeCount > 0 || package.Issues.Count > 0;
                _detailsExpanded.TryGetValue(key, out bool expanded);

                Rect row = GUILayoutUtility.GetRect(0.0f, EditorGUIUtility.singleLineHeight + 3.0f, GUILayout.ExpandWidth(true));
                GetTableRects(row, out Rect indexRect, out Rect packageRect, out Rect entriesRect, out Rect newRect, out Rect sameRect, out Rect projectOverwriteRect, out Rect repeatRect, out Rect queueOverwriteRect, out Rect conflictRect, out Rect errorRect);
                EditorGUI.LabelField(indexRect, (index + 1).ToString(), _indexStyle);
                if (hasDetails)
                {
                    expanded = EditorGUI.Foldout(packageRect, expanded, new GUIContent(GetPackageLabel(package.PackagePath), package.PackagePath), true);
                    _detailsExpanded[key] = expanded;
                }
                else
                {
                    EditorGUI.LabelField(packageRect, new GUIContent(GetPackageLabel(package.PackagePath), package.PackagePath), _tablePackageStyle);
                }

                DrawTableValue(entriesRect, package.AssetCount);
                DrawTableValue(newRect, package.NewCount);
                DrawTableValue(sameRect, package.UnchangedCount);
                DrawTableValue(projectOverwriteRect, package.ProjectChangeCount);
                DrawTableValue(repeatRect, package.DuplicateCount);
                DrawTableValue(queueOverwriteRect, package.QueueChangeCount);
                DrawTableValue(conflictRect, package.ConflictCount);
                DrawTableValue(errorRect, package.ErrorCount);

                if (hasDetails && expanded) DrawPackageDetails(package);
                if (index < _scan.Packages.Count - 1) DrawTableSeparator(1.0f, 0.08f);
            }

            DrawTableSeparator(2.0f, 0.24f);
            DrawPackageTableSummary();
            EditorGUILayout.EndVertical();
        }

        private void DrawPackageTableHeader()
        {
            Rect row = GUILayoutUtility.GetRect(0.0f, EditorGUIUtility.singleLineHeight + 2.0f, GUILayout.ExpandWidth(true));
            GetTableRects(row, out Rect indexRect, out Rect packageRect, out Rect entriesRect, out Rect newRect, out Rect sameRect, out Rect projectOverwriteRect, out Rect repeatRect, out Rect queueOverwriteRect, out Rect conflictRect, out Rect errorRect);
            EditorGUI.LabelField(indexRect, new GUIContent("#", "Import order."), _tableHeaderStyle);
            EditorGUI.LabelField(packageRect, new GUIContent("Package", "Package filename and compressed file size."), _tableHeaderLeftStyle);
            EditorGUI.LabelField(entriesRect, new GUIContent("Assets", "Asset entries contained in this package."), _tableHeaderStyle);
            EditorGUI.LabelField(newRect, new GUIContent("New", "Assets whose paths and GUIDs do not exist in the current project."), _tableHeaderStyle);
            EditorGUI.LabelField(sameRect, new GUIContent("Same", "Assets already present with identical contents and metadata."), _tableHeaderStyle);
            EditorGUI.LabelField(projectOverwriteRect, new GUIContent("Project", "Assets or metadata that differ from the current project at the same path and GUID."), _tableHeaderStyle);
            EditorGUI.LabelField(repeatRect, new GUIContent("Repeat", "Identical assets already supplied by an earlier package in this queue."), _tableHeaderStyle);
            EditorGUI.LabelField(queueOverwriteRect, new GUIContent("Queue", "Assets or metadata that differ from a version supplied by an earlier package in this queue."), _tableHeaderStyle);
            EditorGUI.LabelField(conflictRect, new GUIContent("Conflict", "GUID ownership, path or asset-type conflicts requiring review."), _tableHeaderStyle);
            EditorGUI.LabelField(errorRect, new GUIContent("Error", "Packages or entries that could not be fully inspected."), _tableHeaderStyle);
        }

        private void DrawPackageTableSummary()
        {
            Rect row = GUILayoutUtility.GetRect(0.0f, EditorGUIUtility.singleLineHeight + 3.0f, GUILayout.ExpandWidth(true));
            GetTableRects(row, out Rect indexRect, out Rect packageRect, out Rect entriesRect, out Rect newRect, out Rect sameRect, out Rect projectOverwriteRect, out Rect repeatRect, out Rect queueOverwriteRect, out Rect conflictRect, out Rect errorRect);
            EditorGUI.LabelField(indexRect, string.Empty, _tableSummaryValueStyle);
            EditorGUI.LabelField(packageRect, new GUIContent("Summary", $"Totals for {_scan.Packages.Count} packages."), _tableSummaryPackageStyle);
            DrawSummaryValue(entriesRect, _scan.AssetCount);
            DrawSummaryValue(newRect, _scan.NewCount);
            DrawSummaryValue(sameRect, _scan.UnchangedCount);
            DrawSummaryValue(projectOverwriteRect, _scan.ProjectChangeCount);
            DrawSummaryValue(repeatRect, _scan.DuplicateCount);
            DrawSummaryValue(queueOverwriteRect, _scan.QueueChangeCount);
            DrawSummaryValue(conflictRect, _scan.ConflictCount);
            DrawSummaryValue(errorRect, _scan.ErrorCount);
        }

        private static void DrawTableSeparator(float height, float alpha)
        {
            Rect separator = GUILayoutUtility.GetRect(1.0f, height, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(separator, EditorGUIUtility.isProSkin ? new Color(1.0f, 1.0f, 1.0f, alpha) : new Color(0.0f, 0.0f, 0.0f, alpha));
        }

        private void GetTableRects(Rect row, out Rect index, out Rect package, out Rect entries, out Rect added, out Rect same, out Rect projectOverwrite, out Rect repeat, out Rect queueOverwrite, out Rect conflict, out Rect error)
        {
            float valueColumnsWidth = _entriesWidth + _newWidth + _sameWidth + _projectWidth + _repeatWidth + _queueWidth + _conflictWidth + _errorWidth;
            float packageWidth = Mathf.Max(MinimumPackageWidth, row.width - IndexWidth - IndexPackageGap - valueColumnsWidth);
            float x = row.x;

            index = new Rect(x, row.y, IndexWidth, row.height); x += IndexWidth + IndexPackageGap;
            package = new Rect(x, row.y, packageWidth, row.height); x += packageWidth;
            entries = new Rect(x, row.y, _entriesWidth, row.height); x += _entriesWidth;
            added = new Rect(x, row.y, _newWidth, row.height); x += _newWidth;
            same = new Rect(x, row.y, _sameWidth, row.height); x += _sameWidth;
            projectOverwrite = new Rect(x, row.y, _projectWidth, row.height); x += _projectWidth;
            repeat = new Rect(x, row.y, _repeatWidth, row.height); x += _repeatWidth;
            queueOverwrite = new Rect(x, row.y, _queueWidth, row.height); x += _queueWidth;
            conflict = new Rect(x, row.y, _conflictWidth, row.height); x += _conflictWidth;
            error = new Rect(x, row.y, _errorWidth, row.height);
        }

        private void DrawTableValue(Rect rect, int value)
        {
            EditorGUI.LabelField(rect, value == 0 ? "-" : value.ToString("N0"), _tableValueStyle);
        }

        private void DrawSummaryValue(Rect rect, int value)
        {
            EditorGUI.LabelField(rect, value == 0 ? "-" : value.ToString("N0"), _tableSummaryValueStyle);
        }

        private void DrawPackageDetails(BatchPackageReport package)
        {
            DrawTableSeparator(1.0f, 0.16f);
            EditorGUILayout.BeginVertical();

            DrawChanges("Project changes", package.ProjectChanges, false);
            DrawChanges("Queue changes", package.QueueChanges, true);

            if (package.Issues.Count > 0)
            {
                EditorGUILayout.Space(3.0f);
                EditorGUILayout.LabelField("Problems", EditorStyles.boldLabel);
            }

            foreach (BatchPackageIssue issue in package.Issues.OrderByDescending(item => item.Severity).ThenBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase))
            {
                EditorGUILayout.BeginHorizontal();
                string severity = issue.Severity == BatchPackageIssueSeverity.Conflict ? "CONFLICT" : "ERROR";
                GUIStyle severityStyle = issue.Severity == BatchPackageIssueSeverity.Conflict ? _conflictSeverityStyle : _errorSeverityStyle;
                GUILayout.Label(severity, severityStyle, GUILayout.Width(76.0f));

                EditorGUILayout.BeginVertical();
                if (!string.IsNullOrEmpty(issue.AssetPath)) EditorGUILayout.SelectableLabel(issue.AssetPath, EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField(issue.Message, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            DrawTableSeparator(1.0f, 0.16f);
        }

        private void DrawChanges(string title, IReadOnlyList<BatchPackageChange> changes, bool showSourcePackage)
        {
            if (changes.Count == 0) return;

            EditorGUILayout.Space(3.0f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            DrawExtensionSummary(changes);
            EditorGUILayout.Space(2.0f);
            DrawChangeRow(null, showSourcePackage, true);
            foreach (BatchPackageChange change in changes.OrderBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase))
            {
                DrawChangeRow(change, showSourcePackage, false);
            }
        }

        private void DrawExtensionSummary(IReadOnlyList<BatchPackageChange> changes)
        {
            foreach (IGrouping<string, BatchPackageChange> group in changes
                         .GroupBy(change => GetExtensionLabel(change.AssetPath), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                int assetChanges = group.Count(change => change.AssetChanged);
                int metaChanges = group.Count(change => change.MetaChanged);
                string summary = $"{FormatChangeCount(assetChanges, "asset")}, {FormatChangeCount(metaChanges, "meta")}";
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(group.Key, _extensionStyle, GUILayout.Width(72.0f));
                GUILayout.Label("|", _extensionSeparatorStyle, GUILayout.Width(16.0f));
                GUILayout.Label(summary, _tablePackageStyle);
                EditorGUILayout.EndHorizontal();
            }
        }

        private static string GetExtensionLabel(string assetPath)
        {
            string extension = Path.GetExtension(assetPath);
            return string.IsNullOrEmpty(extension) ? "(none)" : extension.ToLowerInvariant();
        }

        private static string FormatChangeCount(int count, string kind)
        {
            return $"{count:N0} {kind} change{(count == 1 ? string.Empty : "s")}";
        }

        private void DrawChangeRow(BatchPackageChange change, bool showSourcePackage, bool header)
        {
            const float markerWidth = 42.0f;
            const float sourceWidth = 180.0f;
            Rect row = GUILayoutUtility.GetRect(0.0f, EditorGUIUtility.singleLineHeight + 1.0f, GUILayout.ExpandWidth(true));

            var assetRect = new Rect(row.x, row.y, markerWidth, row.height);
            var metaRect = new Rect(assetRect.xMax, row.y, markerWidth, row.height);
            float sourceColumnWidth = showSourcePackage ? Mathf.Min(sourceWidth, Mathf.Max(100.0f, row.width * 0.28f)) : 0.0f;
            var pathRect = new Rect(metaRect.xMax, row.y, Mathf.Max(100.0f, row.width - markerWidth * 2.0f - sourceColumnWidth), row.height);
            var sourceRect = new Rect(pathRect.xMax, row.y, sourceColumnWidth, row.height);

            if (header)
            {
                EditorGUI.LabelField(assetRect, new GUIContent("Asset", "The asset file contents differ."), _detailHeaderStyle);
                EditorGUI.LabelField(metaRect, new GUIContent("Meta", "The .meta file contents differ."), _detailHeaderStyle);
                EditorGUI.LabelField(pathRect, "Path", _detailHeaderLeftStyle);
                if (showSourcePackage) EditorGUI.LabelField(sourceRect, new GUIContent("From", "Earlier package whose version will be changed."), _detailHeaderLeftStyle);
                return;
            }

            EditorGUI.LabelField(assetRect, change.AssetChanged ? "●" : string.Empty, _detailMarkerStyle);
            EditorGUI.LabelField(metaRect, change.MetaChanged ? "●" : string.Empty, _detailMarkerStyle);
            EditorGUI.SelectableLabel(pathRect, change.AssetPath, _detailPathStyle);
            if (showSourcePackage)
            {
                string source = Path.GetFileName(change.PreviousPackagePath);
                EditorGUI.LabelField(sourceRect, new GUIContent(source, change.PreviousPackagePath), _detailPathStyle);
            }
        }

        private void DrawActions(bool running)
        {
            EditorGUILayout.Space(4.0f);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(running || _packagePaths.Count == 0);
            if (GUILayout.Button("Validate", GUILayout.Height(30.0f))) ValidateNow();
            if (GUILayout.Button("Clear Queue", GUILayout.Height(30.0f)))
            {
                _packagePaths.Clear();
                _packageSizeBytes.Clear();
                InvalidateValidation();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            bool canImport = !running && _packagePaths.Count > 0 && _scan != null && _scan.ErrorCount == 0 && _scan.ConflictCount == 0;
            EditorGUI.BeginDisabledGroup(!canImport);
            GUI.backgroundColor = new Color(0.7f, 1.0f, 0.7f);
            if (GUILayout.Button("Import All in Order", GUILayout.Height(40.0f))) BeginImport();
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();
        }

        private void BeginImport()
        {
            string message = $"Import {_packagePaths.Count} packages in the displayed order?\n\n{_scan.ChangeCount} changed entries were detected. The queue stops on the first import failure.";
            if (!EditorUtility.DisplayDialog("Batch Package Import", message, "Import", "Cancel")) return;

            BatchPackageImportQueue.Start(_packagePaths.ToArray());
        }

        private void ValidateNow()
        {
            InvalidateValidation();
            if (_packagePaths.Count == 0) return;
            RefreshPackageSizeCache();

            try
            {
                EditorUtility.DisplayProgressBar("Batch Package Importer", "Reading package contents and comparing project assets...", 0.5f);
                _scan = BatchPackageScanner.Scan(_packagePaths);
            }
            catch (Exception exception)
            {
                _scanError = exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void InvalidateValidation()
        {
            _scan = null;
            _scanError = null;
            _detailsExpanded.Clear();
        }

        private void RefreshPackageSizeCache()
        {
            _packageSizeBytes.Clear();
            foreach (string path in _packagePaths) CachePackageSize(path);
        }

        private void CachePackageSize(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                _packageSizeBytes[path] = File.Exists(path) ? new FileInfo(path).Length : -1L;
            }
            catch
            {
                _packageSizeBytes[path] = -1L;
            }
        }

        private string GetPackageLabel(string path)
        {
            if (!_packageSizeBytes.TryGetValue(path, out long bytes))
            {
                CachePackageSize(path);
                _packageSizeBytes.TryGetValue(path, out bytes);
            }

            string size = bytes >= 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.00}MB", bytes / (1024.0 * 1024.0)) : "unavailable";
            return $"{Path.GetFileName(path)} ({size})";
        }

        private static bool IsUnityPackage(string path)
        {
            return !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".unitypackage", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
    }
}
