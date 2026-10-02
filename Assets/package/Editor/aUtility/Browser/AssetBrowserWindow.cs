using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Generic grid-based asset browser window with search, favorites, live previews,
/// camera gizmo, cache persistence, and unitypackage export.
/// </summary>
public abstract class AssetBrowserWindow<TEntry, TLivePreview> : EditorWindow
	where TEntry : AssetBrowserWindow<TEntry, TLivePreview>.EntryBase
	where TLivePreview : AssetBrowserWindow<TEntry, TLivePreview>.LivePreviewBase {

	protected const string TagsFolderName = "BrowserTags";

	protected const float CellPadding = 8f;
	protected const float LabelHeight = 32f;
	protected const float MinPreviewSize = 48f;
	protected const float MaxPreviewSize = 512f;
	protected const float GizmoSize = 84f;
	protected const float PreviewSlotSpacing = 2000f;
	private const string DefaultSearchRoot = "Assets";
	private const float DefaultLightYaw = 40f;
	private const float DefaultLightPitch = 40f;
	private const float GizmoMargin = 12f;
	private const float GizmoGap = 8f;

	protected readonly List<TEntry> _entries = new();
	protected readonly List<TEntry> _filteredEntries = new();
	protected readonly Dictionary<TEntry, TLivePreview> _livePreviews = new();
	private readonly Queue<int> _freePreviewSlots = new();
	private int _nextPreviewSlot;
	private readonly HashSet<TEntry> _visibleEntries = new();
	protected Vector2 _scrollPosition;
	protected float _previewSize;
	protected float _cameraYaw;
	protected float _cameraPitch;
	protected float _cameraDistance;
	protected float _lightYaw;
	protected float _lightPitch;
	protected float _playbackSpeed = 1f;
	protected bool _paused;
	protected string _searchText = string.Empty;
	private string _searchRoot = DefaultSearchRoot;
	protected bool _filterDirty = true;
	protected bool _favoritesOnly;
	protected readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
	protected readonly Dictionary<string, List<string>> _entryTags = new(StringComparer.Ordinal);
	protected PreviewRenderUtility _previewUtility;
	private static readonly Dictionary<string, Texture2D> ToolbarIconCache = new(StringComparer.Ordinal);
	private double _lastUpdateTime;
	protected bool _draggingGizmo;
	protected bool _draggingLightGizmo;

	protected float PreviewSize => _previewSize;
	protected float CellWidth => _previewSize + 32f;
	protected float CellHeight => _previewSize + LabelHeight + (CellPadding * 3f);
	protected string SearchRoot => _searchRoot;

	// ---- Per-browser customization points ----
	protected abstract string PrefsKeyPrefix { get; }
	protected abstract string CacheFilePath { get; }
	protected abstract string FavoritesFilePath { get; }
	protected abstract string CountLabel { get; }
	protected abstract string EmptyCacheMessage { get; }
	protected abstract float DefaultPreviewSize { get; }
	protected abstract float DefaultCameraPitch { get; }
	protected abstract float DefaultCameraDistance { get; }
	protected abstract float MaxPlaybackSpeed { get; }
	protected abstract float MaxCameraDistance { get; }
	protected abstract string RestartButtonLabel { get; }

	protected abstract void ScanAssets(List<TEntry> entries, List<string> cacheGuids);
	protected abstract TEntry CreateEntryFromCachedPath(string path);
	protected abstract bool PassesTypeFilter(TEntry entry);
	protected abstract string GetEntryName(TEntry entry);
	protected abstract UnityEngine.Object GetSelectionObject(TEntry entry);
	protected abstract TLivePreview CreateLivePreview(TEntry entry, GameObject instance, int slot, Vector3 origin);
	protected abstract GameObject LoadPrefabForPreview(TEntry entry);
	protected abstract void AdvanceLivePreview(TLivePreview live, float deltaTime);
	protected abstract void RestartLivePreview(TLivePreview live);
	protected abstract bool HasPlaybackControls(TLivePreview live);
	protected abstract bool UseSrpForLivePreview(TLivePreview live);
	protected abstract string ExportRootFolder { get; }
	protected abstract string ClassifyDependency(string assetPath);
	protected abstract string ClassifyCommonDependency(string assetPath);

	// Optional hooks
	protected virtual void DrawExtraToolbarFilters() { }
	protected virtual void DrawExtraToolbarButtons() { }
	protected virtual void DrawExtraToolbarRows() { }
	protected virtual void HandleWindowDragAndDrop() { }
	protected virtual void HandleEntryDragAndDrop(Rect previewRect, TEntry entry) { }
	protected virtual void DrawEntryOverlay(Rect previewRect, TEntry entry) { }
	protected virtual float GetEntryBottomOverlayHeight(TEntry entry) => 0f;
	protected virtual void DrawEntryExtraLabel(Rect labelRect, TEntry entry) { }
	protected virtual void AddContextMenuItems(GenericMenu menu, TEntry entry) { }
	protected virtual void AddEntriesFromCachedPath(string path, List<TEntry> entries) {
		var entry = CreateEntryFromCachedPath(path);
		if (entry != null) {
			entries.Add(entry);
		}
	}

	protected virtual int CompareEntries(TEntry left, TEntry right) {
		return string.Compare(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
	}

	protected virtual void ReleaseLivePreviewResources(TLivePreview live) { }
	protected virtual GameObject InstantiatePreviewObject(TEntry entry, GameObject prefab) {
		return prefab == null ? null : _previewUtility.InstantiatePrefabInScene(prefab);
	}
	protected virtual bool KeepInvisibleLivePreviews => false;
	protected virtual double PreviewUpdateInterval => 0d;
	protected virtual bool ShouldAdvanceLivePreview(TEntry entry, TLivePreview live) => true;
	protected virtual void UpdateBackgroundWork() { }
	protected virtual void OnEntriesChanged() { }
	protected bool IsEntryVisible(TEntry entry) => _visibleEntries.Contains(entry);

	protected virtual void OnEnable() {
		var searchRootKey = PrefsKeyPrefix + ".SearchRoot";
		var storedSearchRoot = NormalizeAssetPath(EditorPrefs.GetString(searchRootKey, DefaultSearchRoot));
		var searchRootInvalid = !IsValidSearchRoot(storedSearchRoot);
		_searchRoot = searchRootInvalid ? DefaultSearchRoot : storedSearchRoot;
		if (searchRootInvalid) {
			EditorPrefs.SetString(searchRootKey, _searchRoot);
		}

		_previewSize = EditorPrefs.GetFloat(PrefsKeyPrefix + ".PreviewSize", DefaultPreviewSize);
		_cameraYaw = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraYaw", 0f);
		_cameraPitch = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraPitch", DefaultCameraPitch);
		_cameraDistance = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraDistance", DefaultCameraDistance);
		_lightYaw = EditorPrefs.GetFloat(PrefsKeyPrefix + ".LightYaw", DefaultLightYaw);
		_lightPitch = EditorPrefs.GetFloat(PrefsKeyPrefix + ".LightPitch", DefaultLightPitch);
		_playbackSpeed = EditorPrefs.GetFloat(PrefsKeyPrefix + ".PlaybackSpeed", 1f);
		_lastUpdateTime = EditorApplication.timeSinceStartup;
		LoadFavorites();
		LoadTags();

		var cachedSearchRoot = NormalizeAssetPath(EditorPrefs.GetString(
			PrefsKeyPrefix + ".CachedSearchRoot", DefaultSearchRoot));
		if (searchRootInvalid || !string.Equals(cachedSearchRoot, _searchRoot, StringComparison.Ordinal)) {
			Refresh();
		} else {
			LoadFromCache();
		}
	}

	protected virtual void OnDisable() {
		ReleaseAllLivePreviews();

		if (_previewUtility != null) {
			_previewUtility.Cleanup();
			_previewUtility = null;
		}
	}

	private void Update() {
		UpdateBackgroundWork();

		var now = EditorApplication.timeSinceStartup;
		if (now - _lastUpdateTime < PreviewUpdateInterval) {
			return;
		}

		var deltaTime = Mathf.Clamp((float)(now - _lastUpdateTime), 0f, 0.1f) * _playbackSpeed;
		_lastUpdateTime = now;

		if (_livePreviews.Count == 0 || _paused) {
			return;
		}

		var repaintNeeded = false;
		foreach (var pair in _livePreviews) {
			var live = pair.Value;
			if (live.Paused || !ShouldAdvanceLivePreview(pair.Key, live)) {
				continue;
			}

			AdvanceLivePreview(live, deltaTime);
			repaintNeeded = true;
		}

		if (repaintNeeded) {
			Repaint();
		}
	}

	private void OnGUI() {
		HandleWindowDragAndDrop();

		var isRepaint = Event.current.type == EventType.Repaint;
		if (isRepaint) {
			_visibleEntries.Clear();
		}

		DrawToolbar();
		UpdateFilter();

		EditorGUILayout.Space(4f);
		EditorGUILayout.LabelField($"{CountLabel}: {_filteredEntries.Count} / {_entries.Count}",
			EditorStyles.boldLabel);
		EditorGUILayout.Space(4f);

		var viewportRect = GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f, GUILayout.ExpandWidth(true),
			GUILayout.ExpandHeight(true));
		var availableWidth = Mathf.Max(0f, viewportRect.width - 16f);
		var columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / CellWidth));
		var rowCount = Mathf.CeilToInt((float)_filteredEntries.Count / columns);
		var contentWidth = columns * CellWidth;
		var contentHeight = rowCount * CellHeight;
		var contentRect = new Rect(0f, 0f, contentWidth, contentHeight);

		HandleCameraGizmoEvents(GetCameraGizmoRect(viewportRect));
		HandleLightGizmoEvents(GetLightGizmoRect(viewportRect));

		_scrollPosition = GUI.BeginScrollView(viewportRect, _scrollPosition, contentRect);

		var visibleStartRow =
			Mathf.Clamp(Mathf.FloorToInt(_scrollPosition.y / CellHeight), 0, Mathf.Max(0, rowCount - 1));
		var visibleRowCount = Mathf.CeilToInt(viewportRect.height / CellHeight) + 1;
		var visibleEndRow = Mathf.Min(rowCount - 1, visibleStartRow + visibleRowCount - 1);

		for (int row = visibleStartRow; row <= visibleEndRow; row++) {
			var startIndex = row * columns;
			var endIndex = Mathf.Min(_filteredEntries.Count, startIndex + columns);
			for (int index = startIndex; index < endIndex; index++) {
				var column = index - startIndex;
				var cellRect = new Rect(column * CellWidth, row * CellHeight, CellWidth, CellHeight);
				DrawEntry(cellRect, _filteredEntries[index]);
			}
		}

		GUI.EndScrollView();

		DrawCameraGizmo(viewportRect);
		DrawLightGizmo(viewportRect);

		if (isRepaint) {
			ReleaseInvisibleLivePreviews();
		}
	}

	private void UpdateFilter() {
		if (!_filterDirty) {
			return;
		}

		_filterDirty = false;
		_filteredEntries.Clear();

		var search = _searchText?.Trim();

		foreach (var entry in _entries) {
			if (_favoritesOnly && !_favorites.Contains(entry.Guid)) {
				continue;
			}

			if (!PassesTypeFilter(entry)) {
				continue;
			}

			if (string.IsNullOrEmpty(search)) {
				_filteredEntries.Add(entry);
				continue;
			}

			var name = GetEntryName(entry);
			if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) {
				_filteredEntries.Add(entry);
				continue;
			}

			if (_entryTags.TryGetValue(entry.Guid, out var tags)) {
				foreach (var tag in tags) {
					if (tag.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) {
						_filteredEntries.Add(entry);
						break;
					}
				}
			}
		}
	}

	private void DrawToolbar() {
		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
			if (GUILayout.Button(GetToolbarContent("Rescan", "Refresh", "アセット一覧を再スキャンします。"),
			    EditorStyles.toolbarButton, GUILayout.Width(90f))) {
				Refresh();
			}

			GUILayout.Space(8f);

			var newSearch = GUILayout.TextField(_searchText, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120f),
				GUILayout.MaxWidth(240f));
			if (!string.Equals(newSearch, _searchText, StringComparison.Ordinal)) {
				_searchText = newSearch;
				_filterDirty = true;
				_scrollPosition = Vector2.zero;
			}

			GUILayout.Space(8f);

			if (GUILayout.Button(GetToolbarContent(RestartButtonLabel, "PlayButton",
				    "すべてのプレビューを先頭から再生します。"), EditorStyles.toolbarButton, GUILayout.Width(70f))) {
				RestartAllLivePreviews();
			}

			var pauseContent = _paused
				? GetToolbarContent("Resume", "PlayButton", "プレビューの再生を再開します。")
				: GetToolbarContent("Pause", "PauseButton", "プレビューの再生を一時停止します。");
			if (GUILayout.Button(pauseContent, EditorStyles.toolbarButton, GUILayout.Width(70f))) {
				_paused = !_paused;
			}

			DrawExtraToolbarButtons();

			GUILayout.Space(8f);

			var favoritesContent = GetToolbarContent("Favorites", "Favorite",
				"お気に入りに登録したアセットだけを表示します。");
			var newFavoritesOnly = GUILayout.Toggle(_favoritesOnly, favoritesContent, EditorStyles.toolbarButton,
				GUILayout.Width(90f));
			if (newFavoritesOnly != _favoritesOnly) {
				_favoritesOnly = newFavoritesOnly;
				_filterDirty = true;
				_scrollPosition = Vector2.zero;
			}

			DrawExtraToolbarFilters();

			GUILayout.FlexibleSpace();

			GUILayout.Label("Speed", EditorStyles.miniLabel);
			var newSpeed = GUILayout.HorizontalSlider(_playbackSpeed, 0f, MaxPlaybackSpeed, GUILayout.Width(100f));
			if (!Mathf.Approximately(newSpeed, _playbackSpeed)) {
				_playbackSpeed = newSpeed;
				EditorPrefs.SetFloat(PrefsKeyPrefix + ".PlaybackSpeed", _playbackSpeed);
			}

			GUILayout.Space(8f);

			GUILayout.Label("Distance", EditorStyles.miniLabel);
			var newDistance = GUILayout.HorizontalSlider(_cameraDistance, 1f, MaxCameraDistance,
				GUILayout.Width(100f));
			if (!Mathf.Approximately(newDistance, _cameraDistance)) {
				_cameraDistance = newDistance;
				EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraDistance", _cameraDistance);
			}

			GUILayout.Space(8f);

			GUILayout.Label("Size", EditorStyles.miniLabel);
			var newSize =
				GUILayout.HorizontalSlider(_previewSize, MinPreviewSize, MaxPreviewSize, GUILayout.Width(150f));
			if (!Mathf.Approximately(newSize, _previewSize)) {
				_previewSize = newSize;
				EditorPrefs.SetFloat(PrefsKeyPrefix + ".PreviewSize", _previewSize);
			}
		}

		DrawSearchRootToolbar();
		DrawExtraToolbarRows();

		if (_entries.Count == 0) {
			EditorGUILayout.HelpBox(EmptyCacheMessage, MessageType.Info);
		}
	}

	private void DrawSearchRootToolbar() {
		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
			GUILayout.Label(new GUIContent("Search Root",
				"指定したフォルダー以下のアセットを再帰的に探索します。"), EditorStyles.miniLabel,
				GUILayout.Width(105f));
			GUILayout.Label(_searchRoot, EditorStyles.miniLabel);
			GUILayout.FlexibleSpace();

			if (GUILayout.Button("Select...", EditorStyles.toolbarButton, GUILayout.Width(70f))) {
				SelectSearchRoot();
			}

			using (new EditorGUI.DisabledGroupScope(string.Equals(_searchRoot, DefaultSearchRoot,
				       StringComparison.Ordinal))) {
				if (GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(55f))) {
					SetSearchRoot(DefaultSearchRoot);
				}
			}
		}
	}

	private void SelectSearchRoot() {
		var projectRoot = Path.GetDirectoryName(Application.dataPath);
		var initialFolder = projectRoot == null
			? Application.dataPath
			: Path.GetFullPath(Path.Combine(projectRoot, _searchRoot));
		var selectedFolder = EditorUtility.OpenFolderPanel("Select Search Root", initialFolder, string.Empty);
		if (string.IsNullOrEmpty(selectedFolder)) {
			return;
		}

		var assetPath = NormalizeAssetPath(FileUtil.GetProjectRelativePath(selectedFolder));
		if (!IsValidSearchRoot(assetPath)) {
			EditorUtility.DisplayDialog("Invalid Search Root",
				"Assets フォルダーまたはその配下のフォルダーを選択してください。", "OK");
			return;
		}

		SetSearchRoot(assetPath);
	}

	private void SetSearchRoot(string assetPath) {
		assetPath = NormalizeAssetPath(assetPath);
		if (string.Equals(_searchRoot, assetPath, StringComparison.Ordinal)) {
			return;
		}

		_searchRoot = assetPath;
		EditorPrefs.SetString(PrefsKeyPrefix + ".SearchRoot", _searchRoot);
		_scrollPosition = Vector2.zero;
		Refresh();
		Repaint();
	}

	private static bool IsValidSearchRoot(string assetPath) {
		return AssetDatabase.IsValidFolder(assetPath) &&
		       (string.Equals(assetPath, DefaultSearchRoot, StringComparison.Ordinal) ||
		        assetPath.StartsWith(DefaultSearchRoot + "/", StringComparison.Ordinal));
	}

	private static string NormalizeAssetPath(string assetPath) {
		return string.IsNullOrWhiteSpace(assetPath)
			? string.Empty
			: assetPath.Replace('\\', '/').TrimEnd('/');
	}

	private static Rect GetCameraGizmoRect(Rect viewportRect) {
		return new Rect(viewportRect.xMax - GizmoSize - GizmoMargin, viewportRect.y + GizmoMargin, GizmoSize,
			GizmoSize);
	}

	private static Rect GetLightGizmoRect(Rect viewportRect) {
		var cameraRect = GetCameraGizmoRect(viewportRect);
		return new Rect(cameraRect.x - GizmoSize - GizmoGap, cameraRect.y, GizmoSize, GizmoSize);
	}

	private void HandleCameraGizmoEvents(Rect gizmoRect) {
		var evt = Event.current;
		var controlId = GUIUtility.GetControlID((PrefsKeyPrefix + "CameraGizmo").GetHashCode(), FocusType.Passive,
			gizmoRect);

		switch (evt.GetTypeForControl(controlId)) {
			case EventType.MouseDown:
				if (evt.button == 2 && gizmoRect.Contains(evt.mousePosition)) {
					ResetCameraView();
					evt.Use();
				} else if (evt.button == 0 && gizmoRect.Contains(evt.mousePosition)) {
					GUIUtility.hotControl = controlId;
					_draggingGizmo = true;
					evt.Use();
				}

				break;
			case EventType.MouseDrag:
				if (GUIUtility.hotControl == controlId) {
					_cameraYaw += evt.delta.x * 0.75f;
					if (_cameraYaw > 180f) {
						_cameraYaw -= 360f;
					} else if (_cameraYaw < -180f) {
						_cameraYaw += 360f;
					}

					_cameraPitch = Mathf.Clamp(_cameraPitch + (evt.delta.y * 0.75f), -89f, 89f);
					EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraYaw", _cameraYaw);
					EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraPitch", _cameraPitch);
					evt.Use();
					Repaint();
				}

				break;
			case EventType.MouseUp:
				if (GUIUtility.hotControl == controlId) {
					GUIUtility.hotControl = 0;
					_draggingGizmo = false;
					evt.Use();
				}

				break;
		}

		EditorGUIUtility.AddCursorRect(gizmoRect, MouseCursor.Orbit);
	}

	private void HandleLightGizmoEvents(Rect gizmoRect) {
		var evt = Event.current;
		var controlId = GUIUtility.GetControlID((PrefsKeyPrefix + "LightGizmo").GetHashCode(), FocusType.Passive,
			gizmoRect);

		switch (evt.GetTypeForControl(controlId)) {
			case EventType.MouseDown:
				if (evt.button == 2 && gizmoRect.Contains(evt.mousePosition)) {
					ResetLightView();
					evt.Use();
				} else if (evt.button == 0 && gizmoRect.Contains(evt.mousePosition)) {
					GUIUtility.hotControl = controlId;
					_draggingLightGizmo = true;
					evt.Use();
				}

				break;
			case EventType.MouseDrag:
				if (GUIUtility.hotControl == controlId) {
					_lightYaw = NormalizeAngle(_lightYaw + (evt.delta.x * 0.75f));
					_lightPitch = Mathf.Clamp(_lightPitch + (evt.delta.y * 0.75f), -89f, 89f);
					EditorPrefs.SetFloat(PrefsKeyPrefix + ".LightYaw", _lightYaw);
					EditorPrefs.SetFloat(PrefsKeyPrefix + ".LightPitch", _lightPitch);
					ApplyPreviewLightRotation();
					evt.Use();
					Repaint();
				}

				break;
			case EventType.MouseUp:
				if (GUIUtility.hotControl == controlId) {
					GUIUtility.hotControl = 0;
					_draggingLightGizmo = false;
					evt.Use();
				}

				break;
		}

		EditorGUIUtility.AddCursorRect(gizmoRect, MouseCursor.Orbit);
	}

	private void ResetCameraView() {
		_cameraYaw = 0f;
		_cameraPitch = DefaultCameraPitch;
		_cameraDistance = DefaultCameraDistance;
		EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraYaw", _cameraYaw);
		EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraPitch", _cameraPitch);
		EditorPrefs.SetFloat(PrefsKeyPrefix + ".CameraDistance", _cameraDistance);
		Repaint();
	}

	private void ResetLightView() {
		_lightYaw = DefaultLightYaw;
		_lightPitch = DefaultLightPitch;
		EditorPrefs.SetFloat(PrefsKeyPrefix + ".LightYaw", _lightYaw);
		EditorPrefs.SetFloat(PrefsKeyPrefix + ".LightPitch", _lightPitch);
		ApplyPreviewLightRotation();
		Repaint();
	}

	private static float NormalizeAngle(float angle) {
		if (angle > 180f) {
			return angle - 360f;
		}
		if (angle < -180f) {
			return angle + 360f;
		}

		return angle;
	}

	private void DrawCameraGizmo(Rect viewportRect) {
		var evt = Event.current;
		if (evt.type != EventType.Repaint) {
			return;
		}

		var gizmoRect = GetCameraGizmoRect(viewportRect);
		var center = gizmoRect.center;
		var radius = (GizmoSize * 0.5f) - 6f;
		var hovered = gizmoRect.Contains(evt.mousePosition);
		var backgroundColor = _draggingGizmo || hovered
			? new Color(0f, 0f, 0f, 0.45f)
			: new Color(0f, 0f, 0f, 0.3f);

		Handles.BeginGUI();
		var previousColor = Handles.color;

		Handles.color = backgroundColor;
		Handles.DrawSolidDisc(center, Vector3.forward, radius + 4f);
		Handles.color = new Color(1f, 1f, 1f, 0.25f);
		Handles.DrawWireDisc(center, Vector3.forward, radius + 4f);

		// Draw axis directions as seen from the current camera orientation.
		var cameraRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
		var viewRotation = Quaternion.Inverse(cameraRotation);
		DrawGizmoAxis(center, radius, viewRotation * Vector3.right, new Color(0.9f, 0.3f, 0.3f, 1f), "X");
		DrawGizmoAxis(center, radius, viewRotation * Vector3.up, new Color(0.4f, 0.85f, 0.3f, 1f), "Y");
		DrawGizmoAxis(center, radius, viewRotation * Vector3.forward, new Color(0.3f, 0.55f, 0.95f, 1f), "Z");

		Handles.color = previousColor;
		Handles.EndGUI();
	}

	private void DrawLightGizmo(Rect viewportRect) {
		var evt = Event.current;
		if (evt.type != EventType.Repaint) {
			return;
		}

		var gizmoRect = GetLightGizmoRect(viewportRect);
		var center = gizmoRect.center;
		var radius = (GizmoSize * 0.5f) - 6f;
		var hovered = gizmoRect.Contains(evt.mousePosition);
		var backgroundColor = _draggingLightGizmo || hovered
			? new Color(0.16f, 0.12f, 0.02f, 0.78f)
			: new Color(0.08f, 0.07f, 0.02f, 0.65f);
		var lightColor = new Color(1f, 0.78f, 0.16f, 1f);

		Handles.BeginGUI();
		var previousHandlesColor = Handles.color;
		var previousGuiColor = GUI.color;

		Handles.color = backgroundColor;
		Handles.DrawSolidDisc(center, Vector3.forward, radius + 4f);
		Handles.color = new Color(lightColor.r, lightColor.g, lightColor.b, 0.55f);
		Handles.DrawWireDisc(center, Vector3.forward, radius + 4f);

		var lightRotation = Quaternion.Euler(_lightPitch, _lightYaw, 0f);
		var lightDirection = lightRotation * Vector3.forward;
		var projectedDirection = new Vector2(lightDirection.x, -lightDirection.y);
		var sunCenter = center + (projectedDirection * (radius - 16f));
		var arrowDirection = center - sunCenter;
		if (arrowDirection.sqrMagnitude < 1f) {
			arrowDirection = Vector2.down;
		}
		arrowDirection.Normalize();

		Handles.color = new Color(lightColor.r, lightColor.g, lightColor.b, 0.75f);
		Handles.DrawAAPolyLine(2.5f, sunCenter, center);
		var arrowBase = center - (arrowDirection * 7f);
		var arrowPerpendicular = new Vector2(-arrowDirection.y, arrowDirection.x) * 4f;
		Handles.DrawAAConvexPolygon(center, arrowBase + arrowPerpendicular, arrowBase - arrowPerpendicular);

		const int rayCount = 8;
		const float sunRadius = 6f;
		for (var index = 0; index < rayCount; index++) {
			var angle = (Mathf.PI * 2f * index) / rayCount;
			var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
			Handles.DrawAAPolyLine(2f, sunCenter + (direction * 9f), sunCenter + (direction * 13f));
		}

		Handles.color = lightColor;
		Handles.DrawSolidDisc(sunCenter, Vector3.forward, sunRadius);
		Handles.color = new Color(1f, 0.95f, 0.6f, 1f);
		Handles.DrawWireDisc(sunCenter, Vector3.forward, sunRadius);

		GUI.color = lightColor;
		GUI.Label(new Rect(gizmoRect.x + 12f, gizmoRect.yMax - 20f, gizmoRect.width - 24f, 16f),
			new GUIContent("LIGHT", "左ドラッグ: ライト回転 / 中クリック: リセット"),
			EditorStyles.centeredGreyMiniLabel);

		GUI.color = previousGuiColor;
		Handles.color = previousHandlesColor;
		Handles.EndGUI();
	}

	private static void DrawGizmoAxis(Vector2 center, float radius, Vector3 direction, Color color, string label) {
		// GUI space has Y pointing down, so flip the Y component.
		var end = center + (new Vector2(direction.x, -direction.y) * radius);
		var isFront = direction.z <= 0f;

		Handles.color = isFront ? color : new Color(color.r, color.g, color.b, 0.35f);
		Handles.DrawAAPolyLine(2f, center, end);
		Handles.DrawSolidDisc(end, Vector3.forward, 5f);

		var labelStyle = new GUIStyle(EditorStyles.miniBoldLabel) {
			alignment = TextAnchor.MiddleCenter,
			normal = { textColor = Color.black }
		};
		GUI.Label(new Rect(end.x - 8f, end.y - 8f, 16f, 16f), label, labelStyle);
	}

	private void DrawEntry(Rect cellRect, TEntry entry) {
		var iconX = cellRect.x + ((cellRect.width - PreviewSize) * 0.5f);
		var previewRect = new Rect(iconX, cellRect.y + CellPadding, PreviewSize, PreviewSize);

		EditorGUI.DrawRect(previewRect, new Color(0.19f, 0.19f, 0.19f, 1f));
		var frameRect = new Rect(previewRect.x - 1f, previewRect.y - 1f, previewRect.width + 2f,
			previewRect.height + 2f);
		Handles.DrawSolidRectangleWithOutline(frameRect, Color.clear, new Color(0f, 0f, 0f, 0.35f));

		if (Event.current.type == EventType.Repaint) {
			_visibleEntries.Add(entry);

			var liveTexture = RenderLivePreview(entry, previewRect);
			if (liveTexture != null) {
				GUI.DrawTexture(previewRect, liveTexture, ScaleMode.StretchToFill, false);
			} else {
				var preview = GetPreview(entry);
				if (preview != null) {
					GUI.DrawTexture(previewRect, preview, ScaleMode.ScaleToFit);
				} else if (entry.PreviewFailed) {
					var placeholderStyle = new GUIStyle(EditorStyles.miniLabel) {
						alignment = TextAnchor.MiddleCenter,
						wordWrap = true,
						clipping = TextClipping.Clip,
						normal = { textColor = new Color(0.75f, 0.75f, 0.75f, 1f) }
					};

					EditorGUI.LabelField(previewRect, "No Preview", placeholderStyle);
				}
			}
		}

		HandleEntryDragAndDrop(previewRect, entry);
		DrawEntryPlaybackButtons(previewRect, entry);
		DrawEntryFavoriteButton(previewRect, entry);
		DrawEntryTags(previewRect, entry);
		DrawEntryOverlay(previewRect, entry);

		if (Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
		    previewRect.Contains(Event.current.mousePosition)) {
			ShowEntryContextMenu(entry);
			Event.current.Use();
		}

		if (GUI.Button(previewRect, GUIContent.none, GUIStyle.none)) {
			var obj = GetSelectionObject(entry);
			Selection.activeObject = obj;
			EditorGUIUtility.PingObject(obj);
		}

		var labelRect = new Rect(cellRect.x + CellPadding, previewRect.yMax + CellPadding,
			cellRect.width - (CellPadding * 2f), LabelHeight);
		var labelStyle = new GUIStyle(EditorStyles.miniLabel) {
			alignment = TextAnchor.UpperCenter,
			wordWrap = true,
			clipping = TextClipping.Clip
		};

		EditorGUI.LabelField(labelRect, GetEntryName(entry), labelStyle);
		DrawEntryExtraLabel(labelRect, entry);
	}

	private void DrawEntryPlaybackButtons(Rect previewRect, TEntry entry) {
		const float buttonSize = 18f;
		var playRect = new Rect(previewRect.x + 2f, previewRect.y + 2f, buttonSize, buttonSize);
		var pauseRect = new Rect(playRect.xMax + 2f, playRect.y, buttonSize, buttonSize);

		_livePreviews.TryGetValue(entry, out var live);
		if (!HasPlaybackControls(live)) {
			return;
		}

		if (GUI.Button(playRect, "▶", EditorStyles.miniButton)) {
			live ??= GetOrCreateLivePreview(entry);
			if (live != null) {
				RestartLivePreview(live);
				live.Paused = false;
			}
		}

		var paused = live != null && live.Paused;
		if (GUI.Button(pauseRect, paused ? "‖▶" : "‖", EditorStyles.miniButton)) {
			live ??= GetOrCreateLivePreview(entry);
			if (live != null) {
				live.Paused = !live.Paused;
			}
		}
	}

	private void DrawEntryFavoriteButton(Rect previewRect, TEntry entry) {
		const float buttonSize = 18f;
		var favoriteRect = new Rect(previewRect.xMax - buttonSize - 2f, previewRect.y + 2f, buttonSize, buttonSize);
		var isFavorite = _favorites.Contains(entry.Guid);

		var style = new GUIStyle(EditorStyles.miniButton) {
			normal = { textColor = isFavorite ? new Color(1f, 0.8f, 0.1f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f) }
		};

		if (GUI.Button(favoriteRect, isFavorite ? "★" : "☆", style)) {
			if (isFavorite) {
				_favorites.Remove(entry.Guid);
			} else {
				_favorites.Add(entry.Guid);
			}

			SaveFavorites();
			if (_favoritesOnly) {
				_filterDirty = true;
			}
		}
	}

	protected void RestartAllLivePreviews() {
		_paused = false;
		foreach (var live in _livePreviews.Values) {
			RestartLivePreview(live);
			live.Paused = false;
		}

		Repaint();
	}

	private void ShowEntryContextMenu(TEntry entry) {
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Export as Asset Package..."), false, () => ExportEntryAsPackage(entry));
		AddTagMenuItems(menu, entry);
		AddContextMenuItems(menu, entry);
		menu.ShowAsContext();
	}

	// ---- Tags ----

	protected virtual string TagsFilePath {
		get {
			var projectRoot = Path.GetDirectoryName(Application.dataPath);
			if (projectRoot == null) return null;
			return Path.Combine(projectRoot, TagsFolderName, PrefsKeyPrefix.ToLowerInvariant() + "_tags.txt");
		}
	}

	private void AddTagMenuItems(GenericMenu menu, TEntry entry) {
		menu.AddItem(new GUIContent("Add Tag..."), false,
			() => TagInputWindow.Open(this, tag => AddTag(entry, tag)));

		var knownTags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var tags in _entryTags.Values) {
			foreach (var tag in tags) {
				knownTags.Add(tag);
			}
		}

		_entryTags.TryGetValue(entry.Guid, out var entryTags);
		foreach (var tag in knownTags) {
			var hasTag = entryTags != null && entryTags.Contains(tag);
			var captured = tag;
			menu.AddItem(new GUIContent("Tags/" + tag), hasTag, () => {
				if (hasTag) {
					RemoveTag(entry, captured);
				} else {
					AddTag(entry, captured);
				}
			});
		}
	}

	protected void AddTag(TEntry entry, string tag) {
		tag = tag?.Trim();
		if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(entry.Guid)) {
			return;
		}

		if (!_entryTags.TryGetValue(entry.Guid, out var tags)) {
			tags = new List<string>();
			_entryTags.Add(entry.Guid, tags);
		}

		if (tags.Contains(tag)) {
			return;
		}

		tags.Add(tag);
		tags.Sort(StringComparer.OrdinalIgnoreCase.Compare);
		SaveTags();
		_filterDirty = true;
		Repaint();
	}

	protected void RemoveTag(TEntry entry, string tag) {
		if (!_entryTags.TryGetValue(entry.Guid, out var tags)) {
			return;
		}

		if (!tags.Remove(tag)) {
			return;
		}

		if (tags.Count == 0) {
			_entryTags.Remove(entry.Guid);
		}

		SaveTags();
		_filterDirty = true;
		Repaint();
	}

	private void DrawEntryTags(Rect previewRect, TEntry entry) {
		if (!_entryTags.TryGetValue(entry.Guid, out var tags) || tags.Count == 0) {
			return;
		}

		var style = new GUIStyle(EditorStyles.miniLabel) {
			alignment = TextAnchor.MiddleCenter,
			fontSize = 9,
			clipping = TextClipping.Clip,
			normal = { textColor = Color.white }
		};

		const float tagHeight = 14f;
		const float tagPadding = 6f;
		const float tagGap = 2f;
		var x = previewRect.x + 2f;
		var y = previewRect.yMax - tagHeight - 2f - GetEntryBottomOverlayHeight(entry);

		foreach (var tag in tags) {
			var width = Mathf.Min(style.CalcSize(new GUIContent(tag)).x + tagPadding, previewRect.width - 4f);
			if (x + width > previewRect.xMax - 2f && x > previewRect.x + 2f) {
				x = previewRect.x + 2f;
				y -= tagHeight + tagGap;
			}

			if (y < previewRect.y) {
				break;
			}

			var tagRect = new Rect(x, y, width, tagHeight);
			EditorGUI.DrawRect(tagRect, GetTagColor(tag));
			GUI.Label(tagRect, tag, style);
			x += width + tagGap;
		}
	}

	private static Color GetTagColor(string tag) {
		var hash = 0;
		foreach (var character in tag) {
			hash = (hash * 31) + character;
		}

		var hue = Mathf.Abs(hash % 360) / 360f;
		var color = Color.HSVToRGB(hue, 0.55f, 0.5f);
		color.a = 0.85f;
		return color;
	}

	private void LoadTags() {
		_entryTags.Clear();

		var tagsFilePath = TagsFilePath;
		if (string.IsNullOrEmpty(tagsFilePath) || !File.Exists(tagsFilePath)) {
			return;
		}

		foreach (var line in File.ReadAllLines(tagsFilePath)) {
			if (string.IsNullOrEmpty(line)) {
				continue;
			}

			var parts = line.Split('\t');
			if (parts.Length < 2 || string.IsNullOrEmpty(parts[0])) {
				continue;
			}

			var tags = new List<string>();
			for (int index = 1; index < parts.Length; index++) {
				var tag = parts[index].Trim();
				if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag)) {
					tags.Add(tag);
				}
			}

			if (tags.Count > 0) {
				tags.Sort(StringComparer.OrdinalIgnoreCase.Compare);
				_entryTags[parts[0]] = tags;
			}
		}
	}

	private void SaveTags() {
		var tagsFilePath = TagsFilePath;
		if (string.IsNullOrEmpty(tagsFilePath)) {
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(tagsFilePath));

		var lines = new List<string>();
		var guids = new List<string>(_entryTags.Keys);
		guids.Sort(StringComparer.Ordinal);
		foreach (var guid in guids) {
			lines.Add(guid + "\t" + string.Join("\t", _entryTags[guid]));
		}

		File.WriteAllLines(tagsFilePath, lines);
	}

	private void EnsurePreviewUtility() {
		if (_previewUtility != null) {
			return;
		}

		_previewUtility = new PreviewRenderUtility();
		_previewUtility.camera.fieldOfView = 30f;
		_previewUtility.camera.nearClipPlane = 0.01f;
		_previewUtility.camera.farClipPlane = 1000f;
		_previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
		_previewUtility.camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
		_previewUtility.lights[0].intensity = 1.2f;
		ApplyPreviewLightRotation();

		if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) {
			var cameraData = _previewUtility.camera.GetUniversalAdditionalCameraData();
			cameraData.renderType = CameraRenderType.Base;
			cameraData.renderPostProcessing = false;
			cameraData.antialiasing = AntialiasingMode.None;
		}
	}

	private void ApplyPreviewLightRotation() {
		if (_previewUtility == null) {
			return;
		}

		_previewUtility.lights[0].transform.rotation = Quaternion.Euler(_lightPitch, _lightYaw, 0f);
	}

	protected TLivePreview GetOrCreateLivePreview(TEntry entry) {
		if (_livePreviews.TryGetValue(entry, out var live)) {
			return live;
		}

		var prefab = LoadPrefabForPreview(entry);
		if (prefab == null) {
			return null;
		}

		EnsurePreviewUtility();

		var instance = InstantiatePreviewObject(entry, prefab);
		if (instance == null) {
			return null;
		}

		// Give each live preview a unique, far-apart origin. All instances share the same
		// preview scene, so overlapping positions would leak other prefabs into the render.
		var slot = _freePreviewSlots.Count > 0 ? _freePreviewSlots.Dequeue() : _nextPreviewSlot++;
		var origin = new Vector3(((slot % 64) - 32) * PreviewSlotSpacing, 0f, ((slot / 64) - 32) * PreviewSlotSpacing);
		instance.transform.position = origin;

		live = CreateLivePreview(entry, instance, slot, origin);
		if (live == null) {
			DestroyImmediate(instance);
			_freePreviewSlots.Enqueue(slot);
			return null;
		}

		live.Renderers = instance.GetComponentsInChildren<Renderer>();

		_livePreviews.Add(entry, live);

		return live;
	}

	protected virtual Texture RenderLivePreview(TEntry entry, Rect rect) {
		var live = GetOrCreateLivePreview(entry);
		if (live == null) {
			return null;
		}

		var bounds = new Bounds(live.Origin, Vector3.one);
		var hasBounds = false;
		foreach (var renderer in live.Renderers) {
			if (!hasBounds) {
				bounds = renderer.bounds;
				hasBounds = true;
			} else {
				bounds.Encapsulate(renderer.bounds);
			}
		}

		var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
		live.Radius = Mathf.Max(live.Radius, radius);

		_previewUtility.BeginPreview(rect, GUIStyle.none);
		var previewCamera = _previewUtility.camera;
		var rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
		var offset = rotation * new Vector3(0f, 0f, -live.Radius * _cameraDistance);
		previewCamera.transform.position = bounds.center + offset;
		previewCamera.transform.LookAt(bounds.center);

		var useSrp = GraphicsSettings.currentRenderPipeline != null && UseSrpForLivePreview(live);
		var previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
		ShaderUtil.allowAsyncCompilation = false;
		try {
			_previewUtility.Render(useSrp);
		} finally {
			ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
		}

		return _previewUtility.EndPreview();
	}

	private void ReleaseInvisibleLivePreviews() {
		if (KeepInvisibleLivePreviews) {
			return;
		}

		List<TEntry> toRelease = null;
		foreach (var pair in _livePreviews) {
			if (!_visibleEntries.Contains(pair.Key)) {
				toRelease ??= new List<TEntry>();
				toRelease.Add(pair.Key);
			}
		}

		if (toRelease == null) {
			return;
		}

		foreach (var entry in toRelease) {
			ReleaseLivePreview(entry);
		}
	}

	protected void ReleaseLivePreview(TEntry entry) {
		if (!_livePreviews.TryGetValue(entry, out var live)) {
			return;
		}

		ReleaseLivePreviewResources(live);
		if (live.Instance != null) {
			DestroyImmediate(live.Instance);
		}

		_freePreviewSlots.Enqueue(live.Slot);
		_livePreviews.Remove(entry);
	}

	protected void ReleaseAllLivePreviews() {
		foreach (var live in _livePreviews.Values) {
			ReleaseLivePreviewResources(live);
			if (live.Instance != null) {
				DestroyImmediate(live.Instance);
			}
		}

		_livePreviews.Clear();
		_freePreviewSlots.Clear();
		_nextPreviewSlot = 0;
	}

	private Texture2D GetPreview(TEntry entry) {
		if (entry.Preview != null) {
			return entry.Preview;
		}

		if (entry.PreviewFailed) {
			return null;
		}

		var obj = GetSelectionObject(entry);
		if (obj == null) {
			entry.PreviewFailed = true;
			return null;
		}

		var preview = AssetPreview.GetAssetPreview(obj);
		if (preview != null) {
			entry.Preview = preview;
			return entry.Preview;
		}

		if (AssetPreview.IsLoadingAssetPreview(obj.GetInstanceID())) {
			Repaint();
			return null;
		}

		entry.PreviewFailed = true;

		return null;
	}

	protected List<TEntry> GetFilteredEntriesSnapshot() {
		UpdateFilter();
		return new List<TEntry>(_filteredEntries);
	}

	protected int GetFilteredEntryCount() {
		UpdateFilter();
		return _filteredEntries.Count;
	}

	protected Texture2D CaptureEntryScreenshot(TEntry entry, int width, int height) {
		var source = RenderLivePreview(entry, new Rect(0f, 0f, width, height));
		if (source == null) {
			return null;
		}

		var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
			RenderTextureReadWrite.sRGB);
		var previousActive = RenderTexture.active;
		Texture2D screenshot = null;
		try {
			Graphics.Blit(source, target);
			RenderTexture.active = target;
			screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
			screenshot.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
			screenshot.Apply(false, false);
			return screenshot;
		} catch {
			if (screenshot != null) {
				DestroyImmediate(screenshot);
			}

			throw;
		} finally {
			RenderTexture.active = previousActive;
			RenderTexture.ReleaseTemporary(target);
		}
	}

	protected void Refresh() {
		var cacheGuids = new List<string>();
		ReleaseAllLivePreviews();
		_entries.Clear();
		_filterDirty = true;

		ScanAssets(_entries, cacheGuids);

		_entries.Sort(CompareEntries);
		SaveCache(cacheGuids);
		EditorPrefs.SetString(PrefsKeyPrefix + ".CachedSearchRoot", _searchRoot);
		OnEntriesChanged();
	}

	protected static GUIContent GetToolbarContent(string text, string iconName, string tooltip) {
		if (!ToolbarIconCache.TryGetValue(iconName, out var icon)) {
			icon = EditorGUIUtility.FindTexture(iconName);
			ToolbarIconCache.Add(iconName, icon);
		}

		return icon != null ? new GUIContent(text, icon, tooltip) : new GUIContent(text, tooltip);
	}

	protected void LoadFromCache() {
		ReleaseAllLivePreviews();
		_entries.Clear();
		_filterDirty = true;

		var cacheFilePath = CacheFilePath;
		if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) {
			OnEntriesChanged();
			return;
		}

		foreach (var guid in File.ReadAllLines(cacheFilePath)) {
			if (string.IsNullOrEmpty(guid)) {
				continue;
			}

			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) {
				continue;
			}

			AddEntriesFromCachedPath(path, _entries);
		}

		_entries.Sort(CompareEntries);
		OnEntriesChanged();
	}

	private void SaveCache(List<string> cacheGuids) {
		var cacheFilePath = CacheFilePath;
		if (string.IsNullOrEmpty(cacheFilePath)) {
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath));
		File.WriteAllLines(cacheFilePath, cacheGuids);
	}

	private void LoadFavorites() {
		_favorites.Clear();

		var favoritesFilePath = FavoritesFilePath;
		if (string.IsNullOrEmpty(favoritesFilePath) || !File.Exists(favoritesFilePath)) {
			return;
		}

		foreach (var guid in File.ReadAllLines(favoritesFilePath)) {
			if (!string.IsNullOrEmpty(guid)) {
				_favorites.Add(guid);
			}
		}
	}

	private void SaveFavorites() {
		var favoritesFilePath = FavoritesFilePath;
		if (string.IsNullOrEmpty(favoritesFilePath)) {
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(favoritesFilePath));
		File.WriteAllLines(favoritesFilePath, _favorites);
	}

	protected virtual string ExportEntryDialogTitle => "Export Asset Package";
	protected virtual string ExportFavoritesDialogTitle => "Export Favorites";
	protected virtual string ExportFavoritesDefaultFileName => "Favorites.unitypackage";
	protected virtual string ExportFavoritesEmptyMessage => "お気に入りに登録されたアセットがありません。";

	protected void ExportEntryAsPackage(TEntry entry) {
		var assetName = Path.GetFileNameWithoutExtension(entry.AssetPath);
		var savePath = EditorUtility.SaveFilePanel(ExportEntryDialogTitle, string.Empty,
			assetName + ".unitypackage", "unitypackage");
		if (string.IsNullOrEmpty(savePath)) {
			return;
		}

		try {
			var exports = CollectExports(entry.AssetPath, assetName);
			WriteUnityPackage(savePath, exports);
			EditorUtility.RevealInFinder(savePath);
			Debug.Log($"Exported {exports.Count} assets to {savePath}");
		} catch (Exception exception) {
			Debug.LogError($"Failed to export package: {exception}");
			EditorUtility.DisplayDialog("Export Failed", exception.Message, "OK");
		}
	}

	protected void ExportFavoritesAsPackage() {
		var favoriteEntries = new List<TEntry>();
		foreach (var entry in _entries) {
			if (_favorites.Contains(entry.Guid)) {
				favoriteEntries.Add(entry);
			}
		}

		if (favoriteEntries.Count == 0) {
			EditorUtility.DisplayDialog("Export Favorites", ExportFavoritesEmptyMessage, "OK");
			return;
		}

		var savePath = EditorUtility.SaveFilePanel(ExportFavoritesDialogTitle, string.Empty,
			ExportFavoritesDefaultFileName, "unitypackage");
		if (string.IsNullOrEmpty(savePath)) {
			return;
		}

		try {
			var exports = CollectFavoriteExports(favoriteEntries);
			WriteUnityPackage(savePath, exports);
			EditorUtility.RevealInFinder(savePath);
			Debug.Log($"Exported {exports.Count} assets ({favoriteEntries.Count} entries) to {savePath}");
		} catch (Exception exception) {
			Debug.LogError($"Failed to export favorites package: {exception}");
			EditorUtility.DisplayDialog("Export Failed", exception.Message, "OK");
		}
	}

	private List<ExportAsset> CollectFavoriteExports(List<TEntry> favoriteEntries) {
		var exports = new List<ExportAsset>();
		var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Count how many favorite entries reference each dependency to detect shared assets.
		var dependencyOwners = new Dictionary<string, List<TEntry>>(StringComparer.OrdinalIgnoreCase);
		var rootPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in favoriteEntries) {
			rootPaths.Add(entry.AssetPath);
		}

		foreach (var entry in favoriteEntries) {
			foreach (var dependency in AssetDatabase.GetDependencies(entry.AssetPath, true)) {
				if (!dependency.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
				    rootPaths.Contains(dependency)) {
					continue;
				}

				if (!dependencyOwners.TryGetValue(dependency, out var owners)) {
					owners = new List<TEntry>();
					dependencyOwners.Add(dependency, owners);
				}

				if (!owners.Contains(entry)) {
					owners.Add(entry);
				}
			}
		}

		// Favorite entry roots go into their own folders.
		var exportedRootPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in favoriteEntries) {
			if (!exportedRootPaths.Add(entry.AssetPath)) {
				continue;
			}

			var guid = AssetDatabase.AssetPathToGUID(entry.AssetPath);
			if (string.IsNullOrEmpty(guid) || !File.Exists(entry.AssetPath)) {
				continue;
			}

			var folderName = SanitizeFileName(Path.GetFileNameWithoutExtension(entry.AssetPath));
			var targetPath = MakeUniquePath(
				$"{ExportRootFolder}/{folderName}/{Path.GetFileName(entry.AssetPath)}", usedPaths);
			exports.Add(new ExportAsset {
				Guid = guid,
				SourcePath = entry.AssetPath,
				TargetPath = targetPath
			});
		}

		foreach (var pair in dependencyOwners) {
			var dependency = pair.Key;
			var owners = pair.Value;
			var guid = AssetDatabase.AssetPathToGUID(dependency);
			if (string.IsNullOrEmpty(guid) || !File.Exists(dependency)) {
				continue;
			}

			var fileName = Path.GetFileName(dependency);
			string targetPath;
			if (owners.Count > 1) {
				// Shared by multiple favorite entries: place under Common.
				targetPath = $"{ExportRootFolder}/Common/{ClassifyCommonDependency(dependency)}/{fileName}";
			} else {
				var owner = owners[0];
				var folderName = SanitizeFileName(Path.GetFileNameWithoutExtension(owner.AssetPath));
				targetPath = $"{ExportRootFolder}/{folderName}/{ClassifyDependency(dependency)}/{fileName}";
			}

			targetPath = MakeUniquePath(targetPath, usedPaths);
			exports.Add(new ExportAsset {
				Guid = guid,
				SourcePath = dependency,
				TargetPath = targetPath
			});
		}

		return exports;
	}

	private List<ExportAsset> CollectExports(string assetPath, string assetName) {
		var folderName = SanitizeFileName(assetName);
		var rootFolder = $"{ExportRootFolder}/{folderName}";
		var exports = new List<ExportAsset>();
		var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var dependencies = AssetDatabase.GetDependencies(assetPath, true);
		foreach (var dependency in dependencies) {
			if (!dependency.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			var guid = AssetDatabase.AssetPathToGUID(dependency);
			if (string.IsNullOrEmpty(guid) || !File.Exists(dependency)) {
				continue;
			}

			var fileName = Path.GetFileName(dependency);
			string targetPath;
			if (string.Equals(dependency, assetPath, StringComparison.OrdinalIgnoreCase)) {
				targetPath = $"{rootFolder}/{fileName}";
			} else {
				targetPath = $"{rootFolder}/{ClassifyDependency(dependency)}/{fileName}";
			}

			targetPath = MakeUniquePath(targetPath, usedPaths);
			exports.Add(new ExportAsset {
				Guid = guid,
				SourcePath = dependency,
				TargetPath = targetPath
			});
		}

		return exports;
	}

	private static string MakeUniquePath(string path, HashSet<string> usedPaths) {
		if (usedPaths.Add(path)) {
			return path;
		}

		var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
		var baseName = Path.GetFileNameWithoutExtension(path);
		var extension = Path.GetExtension(path);
		for (int index = 1;; index++) {
			var candidate = $"{directory}/{baseName} {index}{extension}";
			if (usedPaths.Add(candidate)) {
				return candidate;
			}
		}
	}

	protected static string SanitizeFileName(string name) {
		var invalidChars = Path.GetInvalidFileNameChars();
		var builder = new StringBuilder(name.Length);
		foreach (var character in name) {
			builder.Append(Array.IndexOf(invalidChars, character) >= 0 ? '_' : character);
		}

		return builder.ToString();
	}

	private static void WriteUnityPackage(string savePath, List<ExportAsset> exports) {
		using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
		using var gzipStream =
			new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionLevel.Optimal);

		foreach (var export in exports) {
			var assetBytes = File.ReadAllBytes(export.SourcePath);
			WriteTarEntry(gzipStream, $"{export.Guid}/asset", assetBytes);

			var metaPath = export.SourcePath + ".meta";
			if (File.Exists(metaPath)) {
				WriteTarEntry(gzipStream, $"{export.Guid}/asset.meta", File.ReadAllBytes(metaPath));
			}

			var pathnameBytes = Encoding.UTF8.GetBytes(export.TargetPath + "\n00");
			WriteTarEntry(gzipStream, $"{export.Guid}/pathname", pathnameBytes);
		}

		// End-of-archive marker: two 512-byte zero blocks.
		gzipStream.Write(new byte[1024], 0, 1024);
	}

	private static void WriteTarEntry(Stream stream, string entryName, byte[] content) {
		var header = new byte[512];
		WriteTarString(header, 0, 100, entryName);
		WriteTarString(header, 100, 8, "0000644");
		WriteTarString(header, 108, 8, "0000000");
		WriteTarString(header, 116, 8, "0000000");
		WriteTarString(header, 124, 12, Convert.ToString(content.Length, 8).PadLeft(11, '0'));
		WriteTarString(header, 136, 12,
			Convert.ToString(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8).PadLeft(11, '0'));
		header[156] = (byte)'0';
		WriteTarString(header, 257, 6, "ustar");
		header[263] = (byte)'0';
		header[264] = (byte)'0';

		for (int index = 148; index < 156; index++) {
			header[index] = (byte)' ';
		}

		var checksum = 0;
		foreach (var value in header) {
			checksum += value;
		}

		WriteTarString(header, 148, 7, Convert.ToString(checksum, 8).PadLeft(6, '0'));

		stream.Write(header, 0, header.Length);
		stream.Write(content, 0, content.Length);

		var remainder = content.Length % 512;
		if (remainder != 0) {
			stream.Write(new byte[512 - remainder], 0, 512 - remainder);
		}
	}

	private static void WriteTarString(byte[] buffer, int offset, int length, string value) {
		var bytes = Encoding.ASCII.GetBytes(value);
		var count = Mathf.Min(bytes.Length, length - 1);
		Array.Copy(bytes, 0, buffer, offset, count);
	}

	public class EntryBase {
		public readonly string AssetPath;
		public readonly string Guid;
		public Texture2D Preview;
		public bool PreviewFailed;

		protected EntryBase(string assetPath, string stableId = null) {
			AssetPath = assetPath;
			Guid = stableId ?? AssetDatabase.AssetPathToGUID(assetPath);
		}
	}

	public class LivePreviewBase {
		public GameObject Instance;
		public Renderer[] Renderers;
		public float Time;
		public float Radius;
		public bool Paused;
		public int Slot;
		public Vector3 Origin;
	}

	protected class ExportAsset {
		public string Guid;
		public string SourcePath;
		public string TargetPath;
	}
}
