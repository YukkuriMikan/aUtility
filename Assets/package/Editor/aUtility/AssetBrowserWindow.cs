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
	protected float _playbackSpeed = 1f;
	protected bool _paused;
	protected string _searchText = string.Empty;
	protected bool _filterDirty = true;
	protected bool _favoritesOnly;
	protected readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
	protected readonly Dictionary<string, List<string>> _entryTags = new(StringComparer.Ordinal);
	protected PreviewRenderUtility _previewUtility;
	private double _lastUpdateTime;
	protected bool _draggingGizmo;

	protected float PreviewSize => _previewSize;
	protected float CellWidth => _previewSize + 32f;
	protected float CellHeight => _previewSize + LabelHeight + (CellPadding * 3f);

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
	protected virtual void HandleEntryDragAndDrop(Rect previewRect, TEntry entry) { }
	protected virtual void DrawEntryExtraLabel(Rect labelRect, TEntry entry) { }
	protected virtual void AddContextMenuItems(GenericMenu menu, TEntry entry) { }

	protected virtual void OnEnable() {
		_previewSize = EditorPrefs.GetFloat(PrefsKeyPrefix + ".PreviewSize", DefaultPreviewSize);
		_cameraYaw = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraYaw", 0f);
		_cameraPitch = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraPitch", DefaultCameraPitch);
		_cameraDistance = EditorPrefs.GetFloat(PrefsKeyPrefix + ".CameraDistance", DefaultCameraDistance);
		_playbackSpeed = EditorPrefs.GetFloat(PrefsKeyPrefix + ".PlaybackSpeed", 1f);
		_lastUpdateTime = EditorApplication.timeSinceStartup;
		LoadFavorites();
		LoadTags();
		LoadFromCache();
	}

	protected virtual void OnDisable() {
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
			if (live.Paused) {
				continue;
			}

			AdvanceLivePreview(live, deltaTime);
		}

		Repaint();
	}

	private void OnGUI() {
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

			if (GUILayout.Button(RestartButtonLabel, EditorStyles.toolbarButton, GUILayout.Width(60f))) {
				RestartAllLivePreviews();
			}

			if (GUILayout.Button(_paused ? "Resume" : "Pause", EditorStyles.toolbarButton, GUILayout.Width(60f))) {
				_paused = !_paused;
			}

			DrawExtraToolbarButtons();

			GUILayout.Space(8f);

			var newFavoritesOnly = GUILayout.Toggle(_favoritesOnly, "★ Favorites", EditorStyles.toolbarButton,
				GUILayout.Width(80f));
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

		if (_entries.Count == 0) {
			EditorGUILayout.HelpBox(EmptyCacheMessage, MessageType.Info);
		}
	}

	private static Rect GetCameraGizmoRect(Rect viewportRect) {
		const float margin = 12f;
		return new Rect(viewportRect.xMax - GizmoSize - margin, viewportRect.y + margin, GizmoSize, GizmoSize);
	}

	private void HandleCameraGizmoEvents(Rect gizmoRect) {
		var evt = Event.current;
		var controlId = GUIUtility.GetControlID((PrefsKeyPrefix + "CameraGizmo").GetHashCode(), FocusType.Passive,
			gizmoRect);

		switch (evt.GetTypeForControl(controlId)) {
			case EventType.MouseDown:
				if (evt.button == 0 && gizmoRect.Contains(evt.mousePosition)) {
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
		var y = previewRect.yMax - tagHeight - 2f;

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
		_previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);

		if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) {
			var cameraData = _previewUtility.camera.GetUniversalAdditionalCameraData();
			cameraData.renderType = CameraRenderType.Base;
			cameraData.renderPostProcessing = false;
			cameraData.antialiasing = AntialiasingMode.None;
		}
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

		var instance = _previewUtility.InstantiatePrefabInScene(prefab);
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
		foreach (var renderer in live.Instance.GetComponentsInChildren<Renderer>()) {
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
			var live = _livePreviews[entry];
			if (live.Instance != null) {
				DestroyImmediate(live.Instance);
			}

			_freePreviewSlots.Enqueue(live.Slot);
			_livePreviews.Remove(entry);
		}
	}

	private void ReleaseAllLivePreviews() {
		foreach (var live in _livePreviews.Values) {
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

	protected void Refresh() {
		var cacheGuids = new List<string>();
		_entries.Clear();
		_filterDirty = true;

		ScanAssets(_entries, cacheGuids);

		_entries.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
		SaveCache(cacheGuids);
	}

	protected void LoadFromCache() {
		_entries.Clear();
		_filterDirty = true;

		var cacheFilePath = CacheFilePath;
		if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) {
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

			var entry = CreateEntryFromCachedPath(path);
			if (entry != null) {
				_entries.Add(entry);
			}
		}

		_entries.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
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
		foreach (var entry in favoriteEntries) {
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

		protected EntryBase(string assetPath) {
			AssetPath = assetPath;
			Guid = AssetDatabase.AssetPathToGUID(assetPath);
		}
	}

	public class LivePreviewBase {
		public GameObject Instance;
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
