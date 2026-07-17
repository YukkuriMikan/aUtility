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

	public enum AssetType {
		Fbx,
		MeshPrefab,
		SkinnedMeshPrefab
	}

	private bool _showFbx = true;
	private bool _showMeshPrefab = true;
	private bool _showSkinnedMeshPrefab = true;
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
		base.OnEnable();
	}

	protected override void DrawExtraToolbarButtons() {
		if (GUILayout.Button("Export Favorites", EditorStyles.toolbarButton, GUILayout.Width(110f))) {
			ExportFavoritesAsPackage();
		}
	}

	protected override void DrawExtraToolbarFilters() {
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
		var modelGuids = AssetDatabase.FindAssets("t:Model");
		foreach (var guid in modelGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path)) continue;
			entries.Add(new Entry(path, AssetType.Fbx));
			cacheGuids.Add(guid);
		}

		// Scan Prefabs
		var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
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
		}

		return new Entry(path, type);
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