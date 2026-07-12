using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class ModelBrowser : EditorWindow {
	private const float CellPadding = 8f;
	private const float LabelHeight = 32f;
	private const float MinPreviewSize = 48f;
	private const float MaxPreviewSize = 512f;
	private const float GizmoSize = 84f;
	private const float PreviewSlotSpacing = 2000f;
	private const string CacheFolderName = "ModelBrowserCache";
	private const string CacheFileName = "model_cache.txt";
	private const string FavoritesFileName = "model_favorites.txt";
	private const string PreviewSizeKey = "ModelBrowser.PreviewSize";
	private const string CameraYawKey = "ModelBrowser.CameraYaw";
	private const string CameraPitchKey = "ModelBrowser.CameraPitch";
	private const string CameraDistanceKey = "ModelBrowser.CameraDistance";
	private const string PlaybackSpeedKey = "ModelBrowser.PlaybackSpeed";
	private const string ShowFbxKey = "ModelBrowser.ShowFbx";
	private const string ShowMeshPrefabKey = "ModelBrowser.ShowMeshPrefab";
	private const string ShowSkinnedMeshPrefabKey = "ModelBrowser.ShowSkinnedMeshPrefab";

	private enum AssetType {
		Fbx,
		MeshPrefab,
		SkinnedMeshPrefab
	}

	private readonly List<Entry> _entries = new();
	private readonly List<Entry> _filteredEntries = new();
	private readonly Dictionary<Entry, LivePreview> _livePreviews = new();
	private readonly Queue<int> _freePreviewSlots = new();
	private int _nextPreviewSlot;
	private readonly HashSet<Entry> _visibleEntries = new();
	private Vector2 _scrollPosition;
	private float _previewSize = 128f;
	private float _cameraYaw;
	private float _cameraPitch = 15f;
	private float _cameraDistance = 5.0f;
	private float _playbackSpeed = 1f;
	private bool _paused;
	private string _searchText = string.Empty;
	private bool _showFbx = true;
	private bool _showMeshPrefab = true;
	private bool _showSkinnedMeshPrefab = true;
	private bool _filterDirty = true;
	private bool _favoritesOnly;
	private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
	private static string _cacheFilePath;
	private static string _favoritesFilePath;
	private PreviewRenderUtility _previewUtility;
	private double _lastUpdateTime;
	private bool _draggingGizmo;

	private float PreviewSize => _previewSize;
	private float CellWidth => _previewSize + 32f;
	private float CellHeight => _previewSize + LabelHeight + (CellPadding * 3f);

	[MenuItem("Tools/Model Browser")]
	private static void OpenWindow() {
		var window = GetWindow<ModelBrowser>("Model Browser");
		window.minSize = new Vector2(480f, 300f);
		window.LoadFromCache();
	}

	private void OnEnable() {
		_previewSize = EditorPrefs.GetFloat(PreviewSizeKey, 128f);
		_cameraYaw = EditorPrefs.GetFloat(CameraYawKey, 0f);
		_cameraPitch = EditorPrefs.GetFloat(CameraPitchKey, 15f);
		_cameraDistance = EditorPrefs.GetFloat(CameraDistanceKey, 5.0f);
		_playbackSpeed = EditorPrefs.GetFloat(PlaybackSpeedKey, 1f);
		_showFbx = EditorPrefs.GetBool(ShowFbxKey, true);
		_showMeshPrefab = EditorPrefs.GetBool(ShowMeshPrefabKey, true);
		_showSkinnedMeshPrefab = EditorPrefs.GetBool(ShowSkinnedMeshPrefabKey, true);
		_lastUpdateTime = EditorApplication.timeSinceStartup;
		LoadFavorites();
		LoadFromCache();
	}

	private void OnDisable() {
		ReleaseAllLivePreviews();

		if (_previewUtility != null) {
			_previewUtility.Cleanup();
			_previewUtility = null;
		}
	}

	private void Update() {
		var now = EditorApplication.timeSinceStartup;
		var deltaTime = Mathf.Clamp((float)(now - _lastUpdateTime), 0f, 0.1f) * _playbackSpeed;
		_lastUpdateTime = now;

		if (_livePreviews.Count == 0 || _paused) {
			return;
		}

		foreach (var live in _livePreviews.Values) {
			if (live.Paused || live.Clip == null) {
				continue;
			}

			live.Time += deltaTime;
			if (live.Time >= live.Clip.length) {
				if (live.Clip.wrapMode == WrapMode.Loop || live.Clip.isLooping) {
					live.Time %= live.Clip.length;
				} else {
					live.Time = live.Clip.length;
					live.Paused = true;
				}
			}

			if (live.Clip != null) {
				live.Clip.SampleAnimation(live.Instance, live.Time);
			}
		}

		Repaint();
	}

	private void OnGUI() {
		DrawToolbar();
		UpdateFilter();

		EditorGUILayout.Space(4f);
		EditorGUILayout.LabelField($"Models: {_filteredEntries.Count} / {_entries.Count}", EditorStyles.boldLabel);
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

		if (Event.current.type == EventType.Repaint) {
			ReleaseInvisibleLivePreviews();
			_visibleEntries.Clear();
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

			if (!_showFbx && entry.Type == AssetType.Fbx) continue;
			if (!_showMeshPrefab && entry.Type == AssetType.MeshPrefab) continue;
			if (!_showSkinnedMeshPrefab && entry.Type == AssetType.SkinnedMeshPrefab) continue;

			if (string.IsNullOrEmpty(search)) {
				_filteredEntries.Add(entry);
				continue;
			}

			var name = Path.GetFileNameWithoutExtension(entry.AssetPath);
			if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) {
				_filteredEntries.Add(entry);
			}
		}
	}

	private void DrawToolbar() {
		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
			if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(90f))) {
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

			if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(60f))) {
				RestartAllLivePreviews();
			}

			if (GUILayout.Button(_paused ? "Resume" : "Pause", EditorStyles.toolbarButton, GUILayout.Width(60f))) {
				_paused = !_paused;
			}

			if (GUILayout.Button("Export Favorites", EditorStyles.toolbarButton, GUILayout.Width(110f))) {
				ExportFavoritesAsPackage();
			}

			GUILayout.Space(8f);

			var newFavoritesOnly = GUILayout.Toggle(_favoritesOnly, "★ Favorites", EditorStyles.toolbarButton,
				GUILayout.Width(80f));
			if (newFavoritesOnly != _favoritesOnly) {
				_favoritesOnly = newFavoritesOnly;
				_filterDirty = true;
				_scrollPosition = Vector2.zero;
			}

			GUILayout.Space(8f);

			var newFbx = GUILayout.Toggle(_showFbx, "FBX", EditorStyles.toolbarButton, GUILayout.Width(40f));
			if (newFbx != _showFbx) {
				_showFbx = newFbx;
				EditorPrefs.SetBool(ShowFbxKey, _showFbx);
				_filterDirty = true;
			}

			var newMesh = GUILayout.Toggle(_showMeshPrefab, "Mesh", EditorStyles.toolbarButton, GUILayout.Width(50f));
			if (newMesh != _showMeshPrefab) {
				_showMeshPrefab = newMesh;
				EditorPrefs.SetBool(ShowMeshPrefabKey, _showMeshPrefab);
				_filterDirty = true;
			}

			var newSkinned = GUILayout.Toggle(_showSkinnedMeshPrefab, "Skinned", EditorStyles.toolbarButton,
				GUILayout.Width(60f));
			if (newSkinned != _showSkinnedMeshPrefab) {
				_showSkinnedMeshPrefab = newSkinned;
				EditorPrefs.SetBool(ShowSkinnedMeshPrefabKey, _showSkinnedMeshPrefab);
				_filterDirty = true;
			}

			GUILayout.FlexibleSpace();

			GUILayout.Label("Speed", EditorStyles.miniLabel);
			var newSpeed = GUILayout.HorizontalSlider(_playbackSpeed, 0f, 2f, GUILayout.Width(100f));
			if (!Mathf.Approximately(newSpeed, _playbackSpeed)) {
				_playbackSpeed = newSpeed;
				EditorPrefs.SetFloat(PlaybackSpeedKey, _playbackSpeed);
			}

			GUILayout.Space(8f);

			GUILayout.Label("Distance", EditorStyles.miniLabel);
			var newDistance = GUILayout.HorizontalSlider(_cameraDistance, 1f, 20f, GUILayout.Width(100f));
			if (!Mathf.Approximately(newDistance, _cameraDistance)) {
				_cameraDistance = newDistance;
				EditorPrefs.SetFloat(CameraDistanceKey, _cameraDistance);
			}

			GUILayout.Space(8f);

			GUILayout.Label("Size", EditorStyles.miniLabel);
			var newSize =
				GUILayout.HorizontalSlider(_previewSize, MinPreviewSize, MaxPreviewSize, GUILayout.Width(150f));
			if (!Mathf.Approximately(newSize, _previewSize)) {
				_previewSize = newSize;
				EditorPrefs.SetFloat(PreviewSizeKey, _previewSize);
			}
		}

		if (_entries.Count == 0) {
			EditorGUILayout.HelpBox("キャッシュが空です。Rescan を押して FBX/Prefab をスキャンしてください。", MessageType.Info);
		}
	}

	private static Rect GetCameraGizmoRect(Rect viewportRect) {
		const float margin = 12f;
		return new Rect(viewportRect.xMax - GizmoSize - margin, viewportRect.y + margin, GizmoSize, GizmoSize);
	}

	private void HandleCameraGizmoEvents(Rect gizmoRect) {
		var evt = Event.current;
		var controlId = GUIUtility.GetControlID("ModelBrowserCameraGizmo".GetHashCode(), FocusType.Passive, gizmoRect);

		switch (evt.GetTypeForControl(controlId)) {
			case EventType.MouseDown:
				if (evt.button == 0 && gizmoRect.Contains(evt.mousePosition)) {
					GUIUtility.hotControl = controlId;
					evt.Use();
				}

				break;
			case EventType.MouseDrag:
				if (GUIUtility.hotControl == controlId) {
					_cameraYaw += evt.delta.x * 0.75f;
					if (_cameraYaw > 180f) _cameraYaw -= 360f;
					else if (_cameraYaw < -180f) _cameraYaw += 360f;

					_cameraPitch = Mathf.Clamp(_cameraPitch + (evt.delta.y * 0.75f), -89f, 89f);
					EditorPrefs.SetFloat(CameraYawKey, _cameraYaw);
					EditorPrefs.SetFloat(CameraPitchKey, _cameraPitch);
					evt.Use();
					Repaint();
				}

				break;
			case EventType.MouseUp:
				if (GUIUtility.hotControl == controlId) {
					GUIUtility.hotControl = 0;
					evt.Use();
				}

				break;
		}

		EditorGUIUtility.AddCursorRect(gizmoRect, MouseCursor.Orbit);
	}

	private void DrawCameraGizmo(Rect viewportRect) {
		var evt = Event.current;
		if (evt.type != EventType.Repaint) return;

		var gizmoRect = GetCameraGizmoRect(viewportRect);
		var center = gizmoRect.center;
		var radius = (GizmoSize * 0.5f) - 6f;
		var hovered = gizmoRect.Contains(evt.mousePosition);
		var dragging = GUIUtility.hotControl != 0 && gizmoRect.Contains(evt.mousePosition); // Simplified check
		var backgroundColor = dragging || hovered ? new Color(0f, 0f, 0f, 0.45f) : new Color(0f, 0f, 0f, 0.3f);

		Handles.BeginGUI();
		var previousColor = Handles.color;
		Handles.color = backgroundColor;
		Handles.DrawSolidDisc(center, Vector3.forward, radius + 4f);
		Handles.color = new Color(1f, 1f, 1f, 0.25f);
		Handles.DrawWireDisc(center, Vector3.forward, radius + 4f);

		var cameraRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
		var viewRotation = Quaternion.Inverse(cameraRotation);
		DrawGizmoAxis(center, radius, viewRotation * Vector3.right, new Color(0.9f, 0.3f, 0.3f, 1f), "X");
		DrawGizmoAxis(center, radius, viewRotation * Vector3.up, new Color(0.4f, 0.85f, 0.3f, 1f), "Y");
		DrawGizmoAxis(center, radius, viewRotation * Vector3.forward, new Color(0.3f, 0.55f, 0.95f, 1f), "Z");

		Handles.color = previousColor;
		Handles.EndGUI();
	}

	private static void DrawGizmoAxis(Vector2 center, float radius, Vector3 direction, Color color, string label) {
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

	private void DrawEntry(Rect cellRect, Entry entry) {
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
				if (preview != null) GUI.DrawTexture(previewRect, preview, ScaleMode.ScaleToFit);
				else if (entry.PreviewFailed)
					EditorGUI.LabelField(previewRect, "No Preview",
						new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
			}
		}

		HandleDragAndDrop(previewRect, entry);
		DrawEntryPlaybackButtons(previewRect, entry);
		DrawEntryFavoriteButton(previewRect, entry);

		if (Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
		    previewRect.Contains(Event.current.mousePosition)) {
			ShowEntryContextMenu(entry);
			Event.current.Use();
		}

		if (GUI.Button(previewRect, GUIContent.none, GUIStyle.none)) {
			var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.AssetPath);
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

		var label = Path.GetFileNameWithoutExtension(entry.AssetPath);
		EditorGUI.LabelField(labelRect, label, labelStyle);

		_livePreviews.TryGetValue(entry, out var live);
		if (live != null && live.Clip != null) {
			var animLabelRect = new Rect(labelRect.x, labelRect.yMax, labelRect.width, 16f);
			EditorGUI.LabelField(animLabelRect, $"[{live.Clip.name}]",
				new GUIStyle(EditorStyles.miniLabel)
					{ alignment = TextAnchor.UpperCenter, normal = { textColor = Color.cyan } });
		}
	}

	private void HandleDragAndDrop(Rect rect, Entry entry) {
		var evt = Event.current;
		if (!rect.Contains(evt.mousePosition)) return;

		switch (evt.type) {
			case EventType.DragUpdated:
			case EventType.DragPerform:
				var clip = GetDroppedClip(DragAndDrop.objectReferences);
				if (clip != null) {
					DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
					if (evt.type == EventType.DragPerform) {
						DragAndDrop.AcceptDrag();
						var live = GetOrCreateLivePreview(entry);
						if (live != null) {
							live.Clip = clip;
							live.Time = 0f;
							live.Paused = false;
						}
					}

					evt.Use();
				}

				break;
		}
	}

	private AnimationClip GetDroppedClip(UnityEngine.Object[] objects) {
		foreach (var obj in objects) {
			if (obj is AnimationClip clip) return clip;
		}

		return null;
	}

	private void DrawEntryPlaybackButtons(Rect previewRect, Entry entry) {
		var buttonSize = 18f;
		var playRect = new Rect(previewRect.x + 2f, previewRect.y + 2f, buttonSize, buttonSize);
		var pauseRect = new Rect(playRect.xMax + 2f, playRect.y, buttonSize, buttonSize);

		_livePreviews.TryGetValue(entry, out var live);
		if (live == null || live.Clip == null) return;

		if (GUI.Button(playRect, "▶", EditorStyles.miniButton)) {
			live.Time = 0f;
			live.Paused = false;
		}

		if (GUI.Button(pauseRect, live.Paused ? "‖▶" : "‖", EditorStyles.miniButton)) {
			live.Paused = !live.Paused;
		}
	}

	private void DrawEntryFavoriteButton(Rect previewRect, Entry entry) {
		const float buttonSize = 18f;
		var favoriteRect = new Rect(previewRect.xMax - buttonSize - 2f, previewRect.y + 2f, buttonSize, buttonSize);
		var isFavorite = _favorites.Contains(entry.Guid);
		var style = new GUIStyle(EditorStyles.miniButton) {
			normal = { textColor = isFavorite ? new Color(1f, 0.8f, 0.1f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f) }
		};

		if (GUI.Button(favoriteRect, isFavorite ? "★" : "☆", style)) {
			if (isFavorite) _favorites.Remove(entry.Guid);
			else _favorites.Add(entry.Guid);
			SaveFavorites();
			if (_favoritesOnly) _filterDirty = true;
		}
	}

	private void RestartAllLivePreviews() {
		_paused = false;
		foreach (var live in _livePreviews.Values) {
			live.Time = 0f;
			live.Paused = false;
		}

		Repaint();
	}

	private static void ShowEntryContextMenu(Entry entry) {
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Export as Asset Package..."), false, () => ExportEntryAsPackage(entry));
		menu.AddItem(new GUIContent("Export All Favorites..."), false, () => {
			var window = GetWindow<ModelBrowser>();
			if (window != null) {
				window.ExportFavoritesAsPackage();
			}
		});
		menu.ShowAsContext();
	}

	private void ExportFavoritesAsPackage() {
		var favoriteEntries = new List<Entry>();
		foreach (var entry in _entries) {
			if (_favorites.Contains(entry.Guid)) {
				favoriteEntries.Add(entry);
			}
		}

		if (favoriteEntries.Count == 0) {
			EditorUtility.DisplayDialog("Export Favorites", "お気に入りに登録されたモデルがありません。", "OK");
			return;
		}

		var savePath = EditorUtility.SaveFilePanel("Export Favorite Models", string.Empty,
			"ModelFavorites.unitypackage", "unitypackage");
		if (string.IsNullOrEmpty(savePath)) {
			return;
		}

		try {
			var exports = CollectFavoriteExports(favoriteEntries);
			WriteUnityPackage(savePath, exports);
			EditorUtility.RevealInFinder(savePath);
			Debug.Log($"Exported {exports.Count} assets ({favoriteEntries.Count} models) to {savePath}");
		} catch (Exception exception) {
			Debug.LogError($"Failed to export favorites package: {exception}");
			EditorUtility.DisplayDialog("Export Failed", exception.Message, "OK");
		}
	}

	private static List<ExportAsset> CollectFavoriteExports(List<Entry> favoriteEntries) {
		var exports = new List<ExportAsset>();
		var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Count how many favorite models reference each dependency to detect shared assets.
		var dependencyOwners = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
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
					owners = new List<Entry>();
					dependencyOwners.Add(dependency, owners);
				}

				if (!owners.Contains(entry)) {
					owners.Add(entry);
				}
			}
		}

		// Favorite model roots go into their own folders.
		foreach (var entry in favoriteEntries) {
			var guid = AssetDatabase.AssetPathToGUID(entry.AssetPath);
			if (string.IsNullOrEmpty(guid) || !File.Exists(entry.AssetPath)) {
				continue;
			}

			var folderName = SanitizeFileName(Path.GetFileNameWithoutExtension(entry.AssetPath));
			var targetPath = MakeUniquePath($"ExportedModel/{folderName}/{Path.GetFileName(entry.AssetPath)}",
				usedPaths);
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
				// Shared by multiple favorite models: place under Common.
				targetPath = $"ExportedModel/Common/{ClassifyCommonDependency(dependency)}/{fileName}";
			} else {
				var owner = owners[0];
				var folderName = SanitizeFileName(Path.GetFileNameWithoutExtension(owner.AssetPath));
				targetPath = $"ExportedModel/{folderName}/{ClassifyDependency(dependency)}/{fileName}";
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

	private static string ClassifyCommonDependency(string assetPath) {
		var category = ClassifyDependency(assetPath);
		return category == "Models" ? "Others" : category;
	}

	private static void ExportEntryAsPackage(Entry entry) {
		var modelName = Path.GetFileNameWithoutExtension(entry.AssetPath);
		var savePath = EditorUtility.SaveFilePanel("Export Model Package", string.Empty,
			modelName + ".unitypackage", "unitypackage");
		if (string.IsNullOrEmpty(savePath)) {
			return;
		}

		try {
			var exports = CollectExports(entry.AssetPath, modelName);
			WriteUnityPackage(savePath, exports);
			EditorUtility.RevealInFinder(savePath);
			Debug.Log($"Exported {exports.Count} assets to {savePath}");
		} catch (Exception exception) {
			Debug.LogError($"Failed to export package: {exception}");
			EditorUtility.DisplayDialog("Export Failed", exception.Message, "OK");
		}
	}

	private static List<ExportAsset> CollectExports(string modelPath, string modelName) {
		var folderName = SanitizeFileName(modelName);
		var rootFolder = $"ExportedModel/{folderName}";
		var exports = new List<ExportAsset>();
		var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var dependencies = AssetDatabase.GetDependencies(modelPath, true);
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
			if (string.Equals(dependency, modelPath, StringComparison.OrdinalIgnoreCase)) {
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

	private static string ClassifyDependency(string assetPath) {
		if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) {
			return "Prefabs";
		}

		if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) {
			return "Materials";
		}

		if (assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
		    assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) {
			return "Models";
		}

		var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
		if (mainType != null && typeof(Texture).IsAssignableFrom(mainType)) {
			return "Textures";
		}

		if (mainType != null && typeof(AnimationClip).IsAssignableFrom(mainType)) {
			return "Animations";
		}

		return "Others";
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

	private static string SanitizeFileName(string name) {
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

	private void EnsurePreviewUtility() {
		if (_previewUtility != null) return;
		_previewUtility = new PreviewRenderUtility();
		_previewUtility.camera.fieldOfView = 30f;
		_previewUtility.camera.nearClipPlane = 0.01f;
		_previewUtility.camera.farClipPlane = 1000f;
		_previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
		_previewUtility.camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
		_previewUtility.lights[0].intensity = 1.2f;
		_previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);

		if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) {
			var cameraData = _previewUtility.camera.GetUniversalAdditionalCameraData();
			cameraData.renderType = CameraRenderType.Base;
			cameraData.renderPostProcessing = false;
			cameraData.antialiasing = AntialiasingMode.None;
		}
	}

	private LivePreview GetOrCreateLivePreview(Entry entry) {
		if (_livePreviews.TryGetValue(entry, out var live)) return live;

		var model = AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
		if (model == null) return null;

		EnsurePreviewUtility();
		var instance = _previewUtility.InstantiatePrefabInScene(model);
		if (instance == null) return null;

		var slot = _freePreviewSlots.Count > 0 ? _freePreviewSlots.Dequeue() : _nextPreviewSlot++;
		var origin = new Vector3(((slot % 64) - 32) * PreviewSlotSpacing, 0f, ((slot / 64) - 32) * PreviewSlotSpacing);
		instance.transform.position = origin;

		live = new LivePreview {
			Instance = instance,
			Slot = slot,
			Origin = origin,
			Animator = instance.GetComponent<Animator>()
		};
		if (live.Animator != null) {
			live.Animator.enabled = false;
		}

		_livePreviews.Add(entry, live);
		return live;
	}

	private Texture RenderLivePreview(Entry entry, Rect rect) {
		var live = GetOrCreateLivePreview(entry);
		if (live == null) return null;

		var bounds = new Bounds(live.Origin, Vector3.one);
		var hasBounds = false;
		foreach (var renderer in live.Instance.GetComponentsInChildren<Renderer>()) {
			if (!hasBounds) {
				bounds = renderer.bounds;
				hasBounds = true;
			} else bounds.Encapsulate(renderer.bounds);
		}

		var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
		live.Radius = Mathf.Max(live.Radius, radius);

		_previewUtility.BeginPreview(rect, GUIStyle.none);
		var previewCamera = _previewUtility.camera;
		var rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
		var offset = rotation * new Vector3(0f, 0f, -live.Radius * _cameraDistance);
		previewCamera.transform.position = bounds.center + offset;
		previewCamera.transform.LookAt(bounds.center);

		var useSrp = GraphicsSettings.currentRenderPipeline != null;
		var previousSrpFlag = Unsupported.useScriptableRenderPipeline;
		var previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
		Unsupported.useScriptableRenderPipeline = useSrp;
		ShaderUtil.allowAsyncCompilation = false;
		try {
			previewCamera.Render();
		} finally {
			Unsupported.useScriptableRenderPipeline = previousSrpFlag;
			ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
		}

		return _previewUtility.EndPreview();
	}

	private void ReleaseInvisibleLivePreviews() {
		List<Entry> toRelease = null;
		foreach (var pair in _livePreviews) {
			if (!_visibleEntries.Contains(pair.Key)) {
				toRelease ??= new List<Entry>();
				toRelease.Add(pair.Key);
			}
		}

		if (toRelease != null) {
			foreach (var entry in toRelease) {
				var live = _livePreviews[entry];
				if (live.Instance != null) DestroyImmediate(live.Instance);
				_freePreviewSlots.Enqueue(live.Slot);
				_livePreviews.Remove(entry);
			}
		}
	}

	private void ReleaseAllLivePreviews() {
		foreach (var live in _livePreviews.Values) {
			if (live.Instance != null) DestroyImmediate(live.Instance);
		}

		_livePreviews.Clear();
		_freePreviewSlots.Clear();
		_nextPreviewSlot = 0;
	}

	private Texture2D GetPreview(Entry entry) {
		if (entry.Preview != null) return entry.Preview;
		if (entry.PreviewFailed) return null;

		var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.AssetPath);
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

	private void Refresh() {
		var cache = new CacheData();
		_entries.Clear();
		_filterDirty = true;

		// Scan Models (FBX)
		var modelGuids = AssetDatabase.FindAssets("t:Model");
		foreach (var guid in modelGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;
			_entries.Add(new Entry(path, AssetType.Fbx));
			cache.Guids.Add(guid);
		}

		// Scan Prefabs
		var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
		foreach (var guid in prefabGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;

			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab == null) continue;

			if (prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) {
				_entries.Add(new Entry(path, AssetType.SkinnedMeshPrefab));
				cache.Guids.Add(guid);
			} else if (prefab.GetComponentInChildren<MeshRenderer>(true) != null) {
				_entries.Add(new Entry(path, AssetType.MeshPrefab));
				cache.Guids.Add(guid);
			}
		}

		_entries.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
		SaveCache(cache);
	}

	private void LoadFromCache() {
		_entries.Clear();
		_filterDirty = true;
		var cacheFilePath = GetCacheFilePath();
		if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) return;

		foreach (var guid in File.ReadAllLines(cacheFilePath)) {
			if (string.IsNullOrEmpty(guid)) continue;
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;

			var type = AssetType.Fbx;
			if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) {
				var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
				if (prefab != null) {
					if (prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) {
						type = AssetType.SkinnedMeshPrefab;
					} else if (prefab.GetComponentInChildren<MeshRenderer>(true) != null) {
						type = AssetType.MeshPrefab;
					} else {
						// Not a mesh prefab, skip
						continue;
					}
				} else {
					continue;
				}
			}

			_entries.Add(new Entry(path, type));
		}

		_entries.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
	}

	private static void SaveCache(CacheData cache) {
		var cacheFilePath = GetCacheFilePath();
		if (string.IsNullOrEmpty(cacheFilePath)) return;
		Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath));
		File.WriteAllLines(cacheFilePath, cache.Guids);
	}

	private void LoadFavorites() {
		_favorites.Clear();
		var favoritesFilePath = GetFavoritesFilePath();
		if (string.IsNullOrEmpty(favoritesFilePath) || !File.Exists(favoritesFilePath)) return;
		foreach (var guid in File.ReadAllLines(favoritesFilePath)) {
			if (!string.IsNullOrEmpty(guid)) _favorites.Add(guid);
		}
	}

	private void SaveFavorites() {
		var favoritesFilePath = GetFavoritesFilePath();
		if (string.IsNullOrEmpty(favoritesFilePath)) return;
		Directory.CreateDirectory(Path.GetDirectoryName(favoritesFilePath));
		File.WriteAllLines(favoritesFilePath, _favorites);
	}

	private static string GetFavoritesFilePath() {
		if (_favoritesFilePath != null) return _favoritesFilePath;
		var cacheFilePath = GetCacheFilePath();
		if (string.IsNullOrEmpty(cacheFilePath)) return null;
		_favoritesFilePath = Path.Combine(Path.GetDirectoryName(cacheFilePath) ?? "Assets", FavoritesFileName);
		return _favoritesFilePath;
	}

	private static string GetCacheFilePath() {
		if (_cacheFilePath != null) return _cacheFilePath;
		var projectRoot = Path.GetDirectoryName(Application.dataPath);
		if (projectRoot == null) return null;
		_cacheFilePath = Path.Combine(projectRoot, "Library", CacheFolderName, CacheFileName);
		return _cacheFilePath;
	}

	private class LivePreview {
		public GameObject Instance;
		public Animator Animator;
		public AnimationClip Clip;
		public float Time;
		public float Radius;
		public bool Paused;
		public int Slot;
		public Vector3 Origin;
	}

	private class Entry {
		public readonly string AssetPath;
		public readonly string Guid;
		public readonly AssetType Type;
		public Texture2D Preview;
		public bool PreviewFailed;

		public Entry(string assetPath, AssetType type) {
			AssetPath = assetPath;
			Guid = AssetDatabase.AssetPathToGUID(assetPath);
			Type = type;
		}
	}

	private class ExportAsset {
		public string Guid;
		public string SourcePath;
		public string TargetPath;
	}

	[Serializable]
	private class CacheData {
		public List<string> Guids = new List<string>();
	}
}