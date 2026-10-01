using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class ModelBrowser : AssetBrowserWindow<ModelBrowser.Entry, ModelBrowser.LivePreview> {
	private const string CacheFolderName = "ModelBrowserCache";
	private const string CacheFileName = "model_cache.txt";
	private const string FavoritesFileName = "model_favorites.txt";
	private const string ShowFbxKey = "ModelBrowser.ShowFbx";
	private const string ShowMeshPrefabKey = "ModelBrowser.ShowMeshPrefab";
	private const string ShowSkinnedMeshPrefabKey = "ModelBrowser.ShowSkinnedMeshPrefab";
	private const string PreloadKey = "ModelBrowser.Preload";

	public enum AssetType {
		Fbx,
		MeshPrefab,
		SkinnedMeshPrefab
	}

	private bool _showFbx = true;
	private bool _showMeshPrefab = true;
	private bool _showSkinnedMeshPrefab = true;
	private bool _preloadEnabled;
	private int _preloadIndex;
	private static string _cacheFilePath;
	private static string _favoritesFilePath;

	protected override string PrefsKeyPrefix => "ModelBrowser";
	protected override string CountLabel => "Models";
	protected override string EmptyCacheMessage => "キャッシュが空です。Rescan を押して FBX/Prefab をスキャンしてください。";
	protected override float DefaultPreviewSize => 128f;
	protected override float DefaultCameraPitch => 15f;
	protected override float DefaultCameraDistance => 5.0f;
	protected override float MaxPlaybackSpeed => 2f;
	protected override float MaxCameraDistance => 20f;
	protected override string RestartButtonLabel => "Restart";
	protected override string ExportRootFolder => "ExportedModel";
	protected override string ExportEntryDialogTitle => "Export Model Package";
	protected override string ExportFavoritesDialogTitle => "Export Favorite Models";
	protected override string ExportFavoritesDefaultFileName => "ModelFavorites.unitypackage";
	protected override string ExportFavoritesEmptyMessage => "お気に入りに登録されたモデルがありません。";

	[MenuItem("Tools/Model Browser")]
	private static void OpenWindow() {
		var window = GetWindow<ModelBrowser>("Model Browser");
		window.minSize = new Vector2(480f, 300f);
		window.LoadFromCache();
	}

	protected override void OnEnable() {
		_showFbx = EditorPrefs.GetBool(ShowFbxKey, true);
		_showMeshPrefab = EditorPrefs.GetBool(ShowMeshPrefabKey, true);
		_showSkinnedMeshPrefab = EditorPrefs.GetBool(ShowSkinnedMeshPrefabKey, true);
		_preloadEnabled = EditorPrefs.GetBool(PreloadKey, false);
		base.OnEnable();
	}

	protected override void DrawExtraToolbarButtons() {
		if (GUILayout.Button(GetToolbarContent("ScreenShot", "Camera Icon",
			    "表示中のモデルを画像として保存します。"), EditorStyles.toolbarButton, GUILayout.Width(105f))) {
			ModelBrowserScreenshotWindow.Open(this);
		}

		if (GUILayout.Button(GetToolbarContent("Export Favorites", "SaveAs",
			    "お気に入りのモデルをUnityパッケージとして書き出します。"), EditorStyles.toolbarButton,
		    GUILayout.Width(125f))) {
			ExportFavoritesAsPackage();
		}

		var preloadLabel = !_preloadEnabled
			? "Preload OFF"
			: _preloadIndex < _entries.Count
				? $"Preload {_preloadIndex}/{_entries.Count}"
				: "Preload ON";
		var preloadContent = GetToolbarContent(preloadLabel, "Profiler.Memory",
			"ON: 全モデルのプレビューを段階的に生成し、メモリに保持します。スクロールは軽くなりますが、メモリ使用量が増えます。");
		var newPreloadEnabled = GUILayout.Toggle(_preloadEnabled, preloadContent, EditorStyles.toolbarButton,
			GUILayout.Width(110f));
		if (newPreloadEnabled != _preloadEnabled) {
			_preloadEnabled = newPreloadEnabled;
			EditorPrefs.SetBool(PreloadKey, _preloadEnabled);
			_preloadIndex = 0;
			if (!_preloadEnabled) {
				ReleaseAllLivePreviews();
			}

			Repaint();
		}
	}

	protected override bool KeepInvisibleLivePreviews => _preloadEnabled;
	protected override double PreviewUpdateInterval => 1d / 30d;

	protected override bool ShouldAdvanceLivePreview(Entry entry, LivePreview live) {
		return live.Clip != null && IsEntryVisible(entry);
	}

	protected override void UpdateBackgroundWork() {
		if (!_preloadEnabled || _preloadIndex >= _entries.Count || EditorApplication.isCompiling ||
		    EditorApplication.isUpdating) {
			return;
		}

		GetOrCreateLivePreview(_entries[_preloadIndex]);
		_preloadIndex++;
		Repaint();
	}

	protected override void OnEntriesChanged() {
		_preloadIndex = 0;
	}

	internal int ScreenshotItemCount => GetFilteredEntryCount();

	internal void SaveScreenshots(string outputFolder, int width, int height,
		ModelBrowserScreenshotFormat imageFormat) {
		var entries = GetFilteredEntriesSnapshot();
		if (entries.Count == 0) {
			EditorUtility.DisplayDialog("ScreenShot", "保存対象のモデルがありません。", "OK");
			return;
		}

		var extension = imageFormat == ModelBrowserScreenshotFormat.Png ? ".png" : ".jpg";
		var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var savedCount = 0;
		var failedCount = 0;
		var canceled = false;
		try {
			for (var index = 0; index < entries.Count; index++) {
				var entry = entries[index];
				canceled = EditorUtility.DisplayCancelableProgressBar("Saving Screenshots",
					$"{GetEntryName(entry)} ({index + 1}/{entries.Count})", (float)index / entries.Count);
				if (canceled) {
					break;
				}

				var wasLoaded = _livePreviews.ContainsKey(entry);
				Texture2D screenshot = null;
				try {
					screenshot = CaptureEntryScreenshot(entry, width, height);
					if (screenshot == null) {
						failedCount++;
						continue;
					}

					var bytes = imageFormat == ModelBrowserScreenshotFormat.Png
						? screenshot.EncodeToPNG()
						: screenshot.EncodeToJPG(95);
					var fileName = SanitizeFileName(GetEntryName(entry));
					var outputPath = GetUniqueScreenshotPath(outputFolder, fileName, extension, usedPaths);
					File.WriteAllBytes(outputPath, bytes);
					savedCount++;
				} catch (Exception exception) {
					failedCount++;
					Debug.LogError($"Failed to save screenshot for {entry.AssetPath}: {exception}");
				} finally {
					if (screenshot != null) {
						DestroyImmediate(screenshot);
					}

					if (!wasLoaded && !_preloadEnabled) {
						ReleaseLivePreview(entry);
					}
				}
			}
		} finally {
			EditorUtility.ClearProgressBar();
			Repaint();
		}

		if (savedCount > 0) {
			EditorUtility.RevealInFinder(outputFolder);
		}

		var result = canceled ? $"キャンセルしました。\n保存: {savedCount} 件" : $"保存: {savedCount} 件";
		if (failedCount > 0) {
			result += $"\n失敗: {failedCount} 件";
		}

		EditorUtility.DisplayDialog("ScreenShot", result, "OK");
	}

	private static string GetUniqueScreenshotPath(string outputFolder, string fileName, string extension,
		HashSet<string> usedPaths) {
		var path = Path.Combine(outputFolder, fileName + extension);
		if (!File.Exists(path) && usedPaths.Add(path)) {
			return path;
		}

		for (var index = 1;; index++) {
			path = Path.Combine(outputFolder, $"{fileName} {index}{extension}");
			if (!File.Exists(path) && usedPaths.Add(path)) {
				return path;
			}
		}
	}

	protected override void DrawExtraToolbarFilters() {
		GUILayout.Space(8f);

		var newFbx = GUILayout.Toggle(_showFbx,
			GetToolbarContent("FBX", "ModelImporter Icon", "FBXモデルを表示します。"),
			EditorStyles.toolbarButton, GUILayout.Width(55f));
		if (newFbx != _showFbx) {
			_showFbx = newFbx;
			EditorPrefs.SetBool(ShowFbxKey, _showFbx);
			_filterDirty = true;
		}

		var newMesh = GUILayout.Toggle(_showMeshPrefab,
			GetToolbarContent("Mesh", "Mesh Icon", "MeshRendererを含むPrefabを表示します。"),
			EditorStyles.toolbarButton, GUILayout.Width(65f));
		if (newMesh != _showMeshPrefab) {
			_showMeshPrefab = newMesh;
			EditorPrefs.SetBool(ShowMeshPrefabKey, _showMeshPrefab);
			_filterDirty = true;
		}

		var newSkinned = GUILayout.Toggle(_showSkinnedMeshPrefab,
			GetToolbarContent("Skinned", "SkinnedMeshRenderer Icon", "SkinnedMeshRendererを含むPrefabを表示します。"),
			EditorStyles.toolbarButton, GUILayout.Width(80f));
		if (newSkinned != _showSkinnedMeshPrefab) {
			_showSkinnedMeshPrefab = newSkinned;
			EditorPrefs.SetBool(ShowSkinnedMeshPrefabKey, _showSkinnedMeshPrefab);
			_filterDirty = true;
		}
	}

	protected override bool PassesTypeFilter(Entry entry) {
		if (!_showFbx && entry.Type == AssetType.Fbx) return false;
		if (!_showMeshPrefab && entry.Type == AssetType.MeshPrefab) return false;
		if (!_showSkinnedMeshPrefab && entry.Type == AssetType.SkinnedMeshPrefab) return false;
		return true;
	}

	protected override string GetEntryName(Entry entry) {
		return Path.GetFileNameWithoutExtension(entry.AssetPath);
	}

	protected override UnityEngine.Object GetSelectionObject(Entry entry) {
		return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.AssetPath);
	}

	protected override void HandleEntryDragAndDrop(Rect previewRect, Entry entry) {
		var evt = Event.current;
		if (!previewRect.Contains(evt.mousePosition)) return;

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

	private static AnimationClip GetDroppedClip(UnityEngine.Object[] objects) {
		foreach (var obj in objects) {
			if (obj is AnimationClip clip) return clip;
		}

		return null;
	}

	protected override void DrawEntryExtraLabel(Rect labelRect, Entry entry) {
		_livePreviews.TryGetValue(entry, out var live);
		if (live != null && live.Clip != null) {
			var animLabelRect = new Rect(labelRect.x, labelRect.yMax, labelRect.width, 16f);
			EditorGUI.LabelField(animLabelRect, $"[{live.Clip.name}]",
				new GUIStyle(EditorStyles.miniLabel)
					{ alignment = TextAnchor.UpperCenter, normal = { textColor = Color.cyan } });
		}
	}

	protected override void AddContextMenuItems(GenericMenu menu, Entry entry) {
		menu.AddItem(new GUIContent("Export All Favorites..."), false, () => {
			var window = GetWindow<ModelBrowser>();
			if (window != null) {
				window.ExportFavoritesAsPackage();
			}
		});
	}

	protected override GameObject LoadPrefabForPreview(Entry entry) {
		return AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
	}

	protected override LivePreview CreateLivePreview(Entry entry, GameObject instance, int slot, Vector3 origin) {
		var live = new LivePreview {
			Instance = instance,
			Slot = slot,
			Origin = origin,
			Animator = instance.GetComponent<Animator>()
		};
		if (live.Animator != null) {
			live.Animator.enabled = false;
		}

		return live;
	}

	protected override void AdvanceLivePreview(LivePreview live, float deltaTime) {
		if (live.Clip == null) {
			return;
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

	protected override void RestartLivePreview(LivePreview live) {
		live.Time = 0f;
	}

	protected override bool HasPlaybackControls(LivePreview live) {
		return live != null && live.Clip != null;
	}

	protected override bool UseSrpForLivePreview(LivePreview live) {
		return true;
	}

	protected override string ClassifyDependency(string assetPath) {
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

	protected override string ClassifyCommonDependency(string assetPath) {
		var category = ClassifyDependency(assetPath);
		return category == "Models" ? "Others" : category;
	}

	protected override void ScanAssets(List<Entry> entries, List<string> cacheGuids) {
		// Scan Models (FBX)
		var searchFolders = new[] { SearchRoot };
		var modelGuids = AssetDatabase.FindAssets("t:Model", searchFolders);
		foreach (var guid in modelGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;
			if (!ContainsMesh(path)) continue;
			entries.Add(new Entry(path, AssetType.Fbx));
			cacheGuids.Add(guid);
		}

		// Scan Prefabs
		var prefabGuids = AssetDatabase.FindAssets("t:Prefab", searchFolders);
		foreach (var guid in prefabGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;

			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab == null) continue;

			if (prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) {
				entries.Add(new Entry(path, AssetType.SkinnedMeshPrefab));
				cacheGuids.Add(guid);
			} else if (prefab.GetComponentInChildren<MeshRenderer>(true) != null) {
				entries.Add(new Entry(path, AssetType.MeshPrefab));
				cacheGuids.Add(guid);
			}
		}
	}

	protected override Entry CreateEntryFromCachedPath(string path) {
		var type = AssetType.Fbx;
		if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) {
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab == null) {
				return null;
			}

			if (prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) {
				type = AssetType.SkinnedMeshPrefab;
			} else if (prefab.GetComponentInChildren<MeshRenderer>(true) != null) {
				type = AssetType.MeshPrefab;
			} else {
				// Not a mesh prefab, skip
				return null;
			}
		} else if (!ContainsMesh(path)) {
			// Animation-only FBX and other model assets without meshes are not previewable.
			return null;
		}

		return new Entry(path, type);
	}

	private static bool ContainsMesh(string assetPath) {
		var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
		foreach (var asset in assets) {
			if (asset is Mesh) {
				return true;
			}
		}

		return false;
	}

	protected override string CacheFilePath {
		get {
			if (_cacheFilePath != null) return _cacheFilePath;
			var projectRoot = Path.GetDirectoryName(Application.dataPath);
			if (projectRoot == null) return null;
			_cacheFilePath = Path.Combine(projectRoot, "Library", CacheFolderName, CacheFileName);
			return _cacheFilePath;
		}
	}

	protected override string FavoritesFilePath {
		get {
			if (_favoritesFilePath != null) return _favoritesFilePath;
			var cacheFilePath = CacheFilePath;
			if (string.IsNullOrEmpty(cacheFilePath)) return null;
			_favoritesFilePath = Path.Combine(Path.GetDirectoryName(cacheFilePath) ?? "Assets", FavoritesFileName);
			return _favoritesFilePath;
		}
	}

	public class LivePreview : LivePreviewBase {
		public Animator Animator;
		public AnimationClip Clip;
	}

	public class Entry : EntryBase {
		public readonly AssetType Type;

		public Entry(string assetPath, AssetType type) : base(assetPath) {
			Type = type;
		}
	}
}

public enum ModelBrowserScreenshotFormat {
	Png,
	Jpg
}

public class ModelBrowserScreenshotWindow : EditorWindow {
	private const string WidthKey = "ModelBrowser.Screenshot.Width";
	private const string HeightKey = "ModelBrowser.Screenshot.Height";
	private const string FormatKey = "ModelBrowser.Screenshot.Format";
	private const int MinImageSize = 16;
	private const int MaxImageSize = 8192;

	private static readonly string[] FormatLabels = { "PNG", "JPG" };

	private ModelBrowser _owner;
	private int _width = 512;
	private int _height = 512;
	private ModelBrowserScreenshotFormat _imageFormat = ModelBrowserScreenshotFormat.Png;

	public static void Open(ModelBrowser owner) {
		var window = CreateInstance<ModelBrowserScreenshotWindow>();
		window._owner = owner;
		window._width = Mathf.Clamp(EditorPrefs.GetInt(WidthKey, 512), MinImageSize, MaxImageSize);
		window._height = Mathf.Clamp(EditorPrefs.GetInt(HeightKey, 512), MinImageSize, MaxImageSize);
		window._imageFormat = (ModelBrowserScreenshotFormat)Mathf.Clamp(EditorPrefs.GetInt(FormatKey, 0), 0,
			FormatLabels.Length - 1);
		window.titleContent = new GUIContent("ScreenShot");

		var size = new Vector2(320f, 150f);
		var center = owner != null
			? owner.position.center
			: new Vector2(Screen.currentResolution.width * 0.5f, Screen.currentResolution.height * 0.5f);
		window.position = new Rect(center.x - (size.x * 0.5f), center.y - (size.y * 0.5f), size.x, size.y);
		window.minSize = size;
		window.maxSize = size;
		window.ShowUtility();
		window.Focus();
	}

	private void OnGUI() {
		EditorGUILayout.Space(8f);

		EditorGUI.BeginChangeCheck();
		_width = Mathf.Clamp(EditorGUILayout.DelayedIntField("Width", _width), MinImageSize, MaxImageSize);
		_height = Mathf.Clamp(EditorGUILayout.DelayedIntField("Height", _height), MinImageSize, MaxImageSize);
		_imageFormat = (ModelBrowserScreenshotFormat)EditorGUILayout.Popup("Image Format", (int)_imageFormat,
			FormatLabels);
		if (EditorGUI.EndChangeCheck()) {
			EditorPrefs.SetInt(WidthKey, _width);
			EditorPrefs.SetInt(HeightKey, _height);
			EditorPrefs.SetInt(FormatKey, (int)_imageFormat);
		}

		EditorGUILayout.Space(4f);
		if (_owner == null) {
			EditorGUILayout.HelpBox("Model Browser が閉じられています。", MessageType.Warning);
			return;
		}

		EditorGUILayout.LabelField($"保存対象: {_owner.ScreenshotItemCount} 件", EditorStyles.miniLabel);
		EditorGUILayout.Space(4f);

		if (GUILayout.Button("保存", GUILayout.Height(24f))) {
			var outputFolder = EditorUtility.OpenFolderPanel("スクリーンショットの保存先", string.Empty,
				string.Empty);
			if (!string.IsNullOrEmpty(outputFolder)) {
				_owner.SaveScreenshots(outputFolder, _width, _height, _imageFormat);
			}
		}
	}
}
