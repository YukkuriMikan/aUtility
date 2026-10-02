using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public class MotionBrowser : AssetBrowserWindow<MotionBrowser.Entry, MotionBrowser.LivePreview> {
	private const string CacheFolderName = "MotionBrowserCache";
	private const string CacheFileName = "motion_cache.txt";
	private const string FavoritesFileName = "motion_favorites.txt";
	private const string PreviewModelKey = "MotionBrowser.PreviewModelPath";
	private const string PreviewModelHistoryKey = "MotionBrowser.PreviewModelHistory";
	private const string PreviewModelFavoritesKey = "MotionBrowser.PreviewModelFavorites";
	private const string PreloadKey = "MotionBrowser.Preload";
	private const string HumanoidFallbackPath = "Avatar/DefaultAvatar.fbx";
	private const string BuiltinPreviewModelId = "builtin:DefaultAvatar";
	private const int MaxPreviewModelHistory = 16;
	private static readonly MethodInfo InstantiateForAnimatorPreviewMethod = typeof(EditorUtility).GetMethod(
		"InstantiateForAnimatorPreview",
		BindingFlags.Static | BindingFlags.NonPublic,
		null,
		new[] { typeof(UnityEngine.Object) },
		null);

	private GameObject _previewModel;
	private readonly List<string> _previewModelHistory = new();
	private readonly List<string> _previewModelFavorites = new();
	private bool _preloadEnabled;
	private int _preloadIndex;
	private static string _cacheFilePath;
	private static string _favoritesFilePath;
	private GUIStyle _clipTypeStyle;
	private Material _builtinPreviewMaterial;

	private GUIStyle ClipTypeStyle => _clipTypeStyle ??= new GUIStyle(EditorStyles.miniBoldLabel) {
		alignment = TextAnchor.MiddleCenter,
		normal = { textColor = Color.white }
	};

	protected override string PrefsKeyPrefix => "MotionBrowser";
	protected override string CountLabel => "Motions";
	protected override string EmptyCacheMessage =>
		"キャッシュが空です。Rescan を押して AnimationClip をスキャンしてください。";
	protected override float DefaultPreviewSize => 128f;
	protected override float DefaultCameraPitch => 15f;
	protected override float DefaultCameraDistance => 5f;
	protected override float MaxPlaybackSpeed => 2f;
	protected override float MaxCameraDistance => 20f;
	protected override string RestartButtonLabel => "Restart";
	protected override string ExportRootFolder => "ExportedMotion";
	protected override string ExportEntryDialogTitle => "Export Motion Package";
	protected override string ExportFavoritesDialogTitle => "Export Favorite Motions";
	protected override string ExportFavoritesDefaultFileName => "MotionFavorites.unitypackage";
	protected override string ExportFavoritesEmptyMessage => "お気に入りに登録されたモーションがありません。";

	[MenuItem("Tools/Motion Browser")]
	private static void OpenWindow() {
		var window = GetWindow<MotionBrowser>("Motion Browser");
		window.minSize = new Vector2(480f, 300f);
		window.LoadFromCache();
	}

	protected override void OnEnable() {
		_preloadEnabled = EditorPrefs.GetBool(PreloadKey, false);
		var previewModelPath = EditorPrefs.GetString(PreviewModelKey, string.Empty);
		_previewModel = string.IsNullOrEmpty(previewModelPath)
			? null
			: AssetDatabase.LoadAssetAtPath<GameObject>(previewModelPath);
		if (_previewModel == null && !string.IsNullOrEmpty(previewModelPath)) {
			EditorPrefs.DeleteKey(PreviewModelKey);
		}
		LoadStoredPathList(PreviewModelHistoryKey, _previewModelHistory);
		LoadStoredPathList(PreviewModelFavoritesKey, _previewModelFavorites);
		PrunePreviewModelList(_previewModelHistory, PreviewModelHistoryKey);
		PrunePreviewModelList(_previewModelFavorites, PreviewModelFavoritesKey);
		RememberPreviewModel();

		base.OnEnable();
	}

	protected override void OnDisable() {
		base.OnDisable();
		if (_builtinPreviewMaterial != null) {
			DestroyImmediate(_builtinPreviewMaterial);
			_builtinPreviewMaterial = null;
		}
	}

	protected override void DrawExtraToolbarButtons() {
		var preloadLabel = !_preloadEnabled
			? "Preload OFF"
			: _preloadIndex < _entries.Count
				? $"Preload {_preloadIndex}/{_entries.Count}"
				: "Preload ON";
		var preloadContent = GetToolbarContent(preloadLabel, "Profiler.Memory",
			"ON: 全モーションのプレビューを段階的に生成し、メモリに保持します。スクロールは軽くなりますが、メモリ使用量が増えます。");
		var newPreloadEnabled = GUILayout.Toggle(_preloadEnabled, preloadContent, EditorStyles.toolbarButton,
			GUILayout.Width(110f));
		if (newPreloadEnabled == _preloadEnabled) {
			return;
		}

		_preloadEnabled = newPreloadEnabled;
		EditorPrefs.SetBool(PreloadKey, _preloadEnabled);
		_preloadIndex = 0;
		if (!_preloadEnabled) {
			ReleaseAllLivePreviews();
		}

		Repaint();
	}

	protected override void DrawExtraToolbarFilters() {
		if (GUILayout.Button(GetToolbarContent("Export ★", "SaveAs",
			    "お気に入りのモーションをUnityパッケージとして書き出します。"),
		    EditorStyles.toolbarButton, GUILayout.Width(85f))) {
			ExportFavoritesAsPackage();
		}
	}

	protected override void DrawExtraToolbarRows() {
		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
			GUILayout.Label(new GUIContent("Preview Model",
				"プレビューに使用するモデルです。モデルをウィンドウへドロップしても変更できます。"),
				EditorStyles.miniLabel, GUILayout.Width(115f));

			var displayedModel = _previewModel != null ? _previewModel : GetBuiltinPreviewModel();
			EditorGUI.BeginChangeCheck();
			var selectedModel = (GameObject)EditorGUILayout.ObjectField(displayedModel, typeof(GameObject), false,
				GUILayout.MinWidth(120f), GUILayout.MaxWidth(280f));
			if (EditorGUI.EndChangeCheck()) {
				SetPreviewModel(selectedModel == displayedModel && _previewModel == null ? null : selectedModel);
			}

			GUILayout.Label(_previewModel == null ? "Unity Built-in DefaultAvatar" : AssetDatabase.GetAssetPath(_previewModel),
				EditorStyles.miniLabel);
			GUILayout.FlexibleSpace();

			using (new EditorGUI.DisabledGroupScope(_previewModel == null)) {
				if (GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(55f))) {
					SetPreviewModel(null);
				}
			}
		}

		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
			GUILayout.Label(new GUIContent("Avatar Lists",
				"プレビューモデルの履歴とお気に入りです。"), EditorStyles.miniLabel, GUILayout.Width(115f));

			var previewModelId = GetPreviewModelId();
			var isFavorite = ContainsStoredPath(_previewModelFavorites, previewModelId);
			var newIsFavorite = GUILayout.Toggle(isFavorite,
				new GUIContent(isFavorite ? "★" : "☆", "現在のプレビューモデルをお気に入りに追加または削除します。"),
				EditorStyles.toolbarButton, GUILayout.Width(28f));
			if (newIsFavorite != isFavorite) {
				ToggleStoredPath(_previewModelFavorites, previewModelId);
				SaveStoredPathList(PreviewModelFavoritesKey, _previewModelFavorites);
			}

			if (GUILayout.Button("History ▾", EditorStyles.toolbarDropDown, GUILayout.Width(75f))) {
				ShowPreviewModelMenu(_previewModelHistory, PreviewModelHistoryKey, "No Preview Model History");
			}

			if (GUILayout.Button("Favorites ▾", EditorStyles.toolbarDropDown, GUILayout.Width(85f))) {
				ShowPreviewModelMenu(_previewModelFavorites, PreviewModelFavoritesKey,
					"No Favorite Preview Models");
			}

			GUILayout.FlexibleSpace();
		}
	}

	protected override void HandleWindowDragAndDrop() {
		var evt = Event.current;
		if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) {
			return;
		}

		var model = GetDroppedModel(DragAndDrop.objectReferences);
		if (model == null) {
			return;
		}

		DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
		if (evt.type == EventType.DragPerform) {
			DragAndDrop.AcceptDrag();
			SetPreviewModel(model);
		}

		evt.Use();
	}

	protected override bool KeepInvisibleLivePreviews => _preloadEnabled;
	protected override double PreviewUpdateInterval => 1d / 30d;

	protected override bool ShouldAdvanceLivePreview(Entry entry, LivePreview live) {
		return IsEntryVisible(entry) && live.Clip != null && live.Clip.length > 0f;
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

	protected override bool PassesTypeFilter(Entry entry) {
		return true;
	}

	protected override string GetEntryName(Entry entry) {
		var sourceName = Path.GetFileNameWithoutExtension(entry.AssetPath);
		return string.Equals(sourceName, entry.Clip.name, StringComparison.OrdinalIgnoreCase)
			? entry.Clip.name
			: $"{entry.Clip.name}\n{sourceName}";
	}

	protected override UnityEngine.Object GetSelectionObject(Entry entry) {
		return entry.Clip;
	}

	protected override void DrawEntryOverlay(Rect previewRect, Entry entry) {
		const float badgeHeight = 16f;
		const float horizontalPadding = 8f;
		var label = entry.Clip.humanMotion ? "Humanoid" : "Generic";
		var width = ClipTypeStyle.CalcSize(new GUIContent(label)).x + horizontalPadding;
		var badgeRect = new Rect(previewRect.x + 2f, previewRect.yMax - badgeHeight - 2f, width, badgeHeight);
		var background = entry.Clip.humanMotion
			? new Color(0.15f, 0.4f, 0.75f, 0.9f)
			: new Color(0.65f, 0.35f, 0.1f, 0.9f);
		EditorGUI.DrawRect(badgeRect, background);
		GUI.Label(badgeRect, label, ClipTypeStyle);
	}

	protected override float GetEntryBottomOverlayHeight(Entry entry) {
		return 18f;
	}

	protected override GameObject LoadPrefabForPreview(Entry entry) {
		if (_previewModel != null) {
			return _previewModel;
		}

		return GetBuiltinPreviewModel();
	}

	protected override GameObject InstantiatePreviewObject(Entry entry, GameObject prefab) {
		if (prefab == null || (_previewModel != null && prefab == _previewModel)) {
			return base.InstantiatePreviewObject(entry, prefab);
		}

		var instance = InstantiateForAnimatorPreviewMethod?.Invoke(null, new object[] { prefab }) as GameObject;
		if (instance == null) {
			// Keep the compatibility fallback inside PreviewRenderUtility's scene so it never appears
			// in the user's current scene or behind this window.
			instance = Instantiate(prefab);
			instance.hideFlags = HideFlags.HideAndDontSave;
			SceneManager.MoveGameObjectToScene(instance, _previewUtility.camera.scene);
		}

		instance.SetActive(true);
		foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
			renderer.enabled = true;
		}
		ApplyBuiltinPreviewMaterial(instance);

		_previewUtility.AddSingleGO(instance);
		return instance;
	}

	protected override LivePreview CreateLivePreview(Entry entry, GameObject instance, int slot, Vector3 origin) {
		var live = new LivePreview {
			Instance = instance,
			Clip = entry.Clip,
			Slot = slot,
			Origin = origin
		};

		if (!entry.Clip.legacy) {
			live.Animator = instance.GetComponentInChildren<Animator>();
			if (live.Animator == null) {
				live.Animator = instance.AddComponent<Animator>();
			}

			live.Animator.applyRootMotion = false;
			live.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
			live.Animator.fireEvents = false;
			live.Animator.runtimeAnimatorController = null;
			live.Animator.Rebind();
			try {
				live.Graph = PlayableGraph.Create($"Motion Browser - {entry.Clip.name}");
				live.Graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
				live.Playable = AnimationClipPlayable.Create(live.Graph, entry.Clip);
				live.Playable.SetSpeed(0d);
				var output = AnimationPlayableOutput.Create(live.Graph, "Animation", live.Animator);
				output.SetSourcePlayable(live.Playable);
				live.Graph.Play();
			} catch (Exception exception) {
				if (live.Graph.IsValid()) {
					live.Graph.Destroy();
				}

				Debug.LogWarning($"PlayableGraph could not be created for {entry.Clip.name}: {exception.Message}");
			}
		}

		SampleLivePreview(live);
		return live;
	}

	protected override void AdvanceLivePreview(LivePreview live, float deltaTime) {
		live.Time += deltaTime;
		if (live.Time >= live.Clip.length) {
			live.Time %= live.Clip.length;
		}

		SampleLivePreview(live);
	}

	protected override void RestartLivePreview(LivePreview live) {
		live.Time = 0f;
		SampleLivePreview(live);
	}

	protected override bool HasPlaybackControls(LivePreview live) {
		return live != null && live.Clip != null;
	}

	protected override bool UseSrpForLivePreview(LivePreview live) {
		return true;
	}

	protected override void ReleaseLivePreviewResources(LivePreview live) {
		if (live.Graph.IsValid()) {
			live.Graph.Destroy();
		}
	}

	protected override string ClassifyDependency(string assetPath) {
		if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return "Prefabs";
		if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) return "Materials";
		if (assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
		    assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) return "Models";

		var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
		if (mainType != null && typeof(Texture).IsAssignableFrom(mainType)) return "Textures";
		return "Others";
	}

	protected override string ClassifyCommonDependency(string assetPath) {
		return ClassifyDependency(assetPath);
	}

	protected override void ScanAssets(List<Entry> entries, List<string> cacheGuids) {
		var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { SearchRoot });
		foreach (var guid in clipGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path) || !visitedPaths.Add(path)) {
				continue;
			}

			var previousCount = entries.Count;
			AddEntriesFromAsset(path, entries);
			if (entries.Count > previousCount) {
				cacheGuids.Add(guid);
			}
		}
	}

	protected override Entry CreateEntryFromCachedPath(string path) {
		foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) {
			if (asset is AnimationClip clip && IsBrowsableClip(clip)) {
				return new Entry(path, clip);
			}
		}

		return null;
	}

	protected override void AddEntriesFromCachedPath(string path, List<Entry> entries) {
		AddEntriesFromAsset(path, entries);
	}

	protected override int CompareEntries(Entry left, Entry right) {
		var pathComparison = string.Compare(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
		if (pathComparison != 0) {
			return pathComparison;
		}

		var nameComparison = string.Compare(left.Clip.name, right.Clip.name, StringComparison.OrdinalIgnoreCase);
		return nameComparison != 0 ? nameComparison : left.LocalId.CompareTo(right.LocalId);
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

	private static void AddEntriesFromAsset(string path, List<Entry> entries) {
		foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) {
			if (asset is AnimationClip clip && IsBrowsableClip(clip)) {
				entries.Add(new Entry(path, clip));
			}
		}
	}

	private static bool IsBrowsableClip(AnimationClip clip) {
		return clip != null && !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase);
	}

	private static GameObject GetBuiltinPreviewModel() {
		return EditorGUIUtility.Load(HumanoidFallbackPath) as GameObject;
	}

	private void ApplyBuiltinPreviewMaterial(GameObject instance) {
		var material = GetBuiltinPreviewMaterial();
		if (material == null) {
			return;
		}

		foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
			var materials = renderer.sharedMaterials;
			for (var index = 0; index < materials.Length; index++) {
				materials[index] = material;
			}

			renderer.sharedMaterials = materials;
		}
	}

	private Material GetBuiltinPreviewMaterial() {
		if (_builtinPreviewMaterial != null) {
			return _builtinPreviewMaterial;
		}

		Shader shader;
		if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset) {
			shader = Shader.Find("Universal Render Pipeline/Lit");
		} else if (GraphicsSettings.currentRenderPipeline != null) {
			shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit");
		} else {
			shader = Shader.Find("Standard");
		}

		if (shader == null) {
			return null;
		}

		_builtinPreviewMaterial = new Material(shader) {
			name = "Motion Browser Default Avatar Material",
			hideFlags = HideFlags.HideAndDontSave
		};
		var color = new Color(0.72f, 0.75f, 0.8f, 1f);
		if (_builtinPreviewMaterial.HasProperty("_BaseColor")) {
			_builtinPreviewMaterial.SetColor("_BaseColor", color);
		}
		if (_builtinPreviewMaterial.HasProperty("_Color")) {
			_builtinPreviewMaterial.SetColor("_Color", color);
		}

		return _builtinPreviewMaterial;
	}

	private static GameObject GetDroppedModel(UnityEngine.Object[] objects) {
		foreach (var obj in objects) {
			if (obj is GameObject model && IsValidPreviewModel(model)) {
				return model;
			}
		}

		return null;
	}

	private static bool IsValidPreviewModel(GameObject model) {
		return model != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(model)) &&
		       model.GetComponentInChildren<Renderer>(true) != null;
	}

	private string GetPreviewModelId() {
		return _previewModel == null ? BuiltinPreviewModelId : AssetDatabase.GetAssetPath(_previewModel);
	}

	private void RememberPreviewModel() {
		AddRecentStoredPath(_previewModelHistory, GetPreviewModelId(), MaxPreviewModelHistory);
		SaveStoredPathList(PreviewModelHistoryKey, _previewModelHistory);
	}

	private void ShowPreviewModelMenu(List<string> modelIds, string prefsKey, string emptyMessage) {
		PrunePreviewModelList(modelIds, prefsKey);
		var currentId = GetPreviewModelId();
		var menu = new GenericMenu();
		if (modelIds.Count == 0) {
			menu.AddDisabledItem(new GUIContent(emptyMessage));
		} else {
			foreach (var storedId in modelIds) {
				var modelId = storedId;
				menu.AddItem(new GUIContent(GetPreviewModelMenuLabel(modelId)),
					string.Equals(modelId, currentId, StringComparison.OrdinalIgnoreCase),
					() => SelectPreviewModel(modelId));
			}
		}

		menu.ShowAsContext();
	}

	private void SelectPreviewModel(string modelId) {
		if (string.Equals(modelId, BuiltinPreviewModelId, StringComparison.Ordinal)) {
			SetPreviewModel(null);
			return;
		}

		var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelId);
		if (IsValidPreviewModel(model)) {
			SetPreviewModel(model);
		}
	}

	private void PrunePreviewModelList(List<string> modelIds, string prefsKey) {
		if (modelIds.RemoveAll(modelId => !IsValidPreviewModelId(modelId)) > 0) {
			SaveStoredPathList(prefsKey, modelIds);
		}
	}

	private static bool IsValidPreviewModelId(string modelId) {
		if (string.Equals(modelId, BuiltinPreviewModelId, StringComparison.Ordinal)) {
			return true;
		}

		return IsValidPreviewModel(AssetDatabase.LoadAssetAtPath<GameObject>(modelId));
	}

	private static string GetPreviewModelMenuLabel(string modelId) {
		if (string.Equals(modelId, BuiltinPreviewModelId, StringComparison.Ordinal)) {
			return "Unity Built-in DefaultAvatar";
		}

		return $"{Path.GetFileNameWithoutExtension(modelId)} — {FormatPathForMenu(modelId)}";
	}

	private void SetPreviewModel(GameObject model) {
		if (model != null && !IsValidPreviewModel(model)) {
			EditorUtility.DisplayDialog("Invalid Preview Model",
				"Project内のRendererを持つモデルまたはPrefabを指定してください。", "OK");
			return;
		}

		if (_previewModel == model) {
			return;
		}

		_previewModel = model;
		if (_previewModel == null) {
			EditorPrefs.DeleteKey(PreviewModelKey);
		} else {
			EditorPrefs.SetString(PreviewModelKey, AssetDatabase.GetAssetPath(_previewModel));
		}
		RememberPreviewModel();

		ReleaseAllLivePreviews();
		_preloadIndex = 0;
		Repaint();
	}

	private static void SampleLivePreview(LivePreview live) {
		if (live.Graph.IsValid()) {
			live.Playable.SetTime(live.Time);
			live.Graph.Evaluate(0f);
		} else {
			live.Clip.SampleAnimation(live.Instance, live.Time);
		}

		live.Instance.transform.position = live.Origin;
	}

	public class LivePreview : LivePreviewBase {
		public Animator Animator;
		public AnimationClip Clip;
		public PlayableGraph Graph;
		public AnimationClipPlayable Playable;
	}

	public class Entry : EntryBase {
		public readonly AnimationClip Clip;
		public readonly long LocalId;

		public Entry(string assetPath, AnimationClip clip) : base(assetPath, GetStableId(assetPath, clip)) {
			Clip = clip;
			AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out LocalId);
		}

		private static string GetStableId(string assetPath, AnimationClip clip) {
			if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out var guid, out long localId)) {
				return $"{guid}:{localId}";
			}

			return $"{AssetDatabase.AssetPathToGUID(assetPath)}:{clip.name}";
		}
	}
}
