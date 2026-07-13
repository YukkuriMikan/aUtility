using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class ParticlePrefabBrowser : AssetBrowserWindow<ParticlePrefabBrowser.Entry, ParticlePrefabBrowser.LivePreview> {
	private const string CacheFolderName = "ParticlePrefabBrowserCache";
	private const string CacheFileName = "particle_prefab_cache.txt";
	private const string FavoritesFileName = "particle_prefab_favorites.txt";

	private static readonly Dictionary<Shader, bool> GrabPassShaderCache = new();
	private static string _cacheFilePath;
	private static string _favoritesFilePath;

	protected override string PrefsKeyPrefix => "ParticlePrefabBrowser";
	protected override string CountLabel => "Particle Prefabs";
	protected override string EmptyCacheMessage => "キャッシュが空です。Rescan を押して一覧を更新してください。";
	protected override float DefaultPreviewSize => 72f;
	protected override float DefaultCameraPitch => 8f;
	protected override float DefaultCameraDistance => 2.8f;
	protected override float MaxPlaybackSpeed => 1f;
	protected override float MaxCameraDistance => 10f;
	protected override string RestartButtonLabel => "Play";
	protected override string ExportRootFolder => "Assets";
	protected override string ExportEntryDialogTitle => "Export Particle Prefab Package";
	protected override string ExportFavoritesDialogTitle => "Export Favorite Particle Prefabs";
	protected override string ExportFavoritesDefaultFileName => "ParticleFavorites.unitypackage";
	protected override string ExportFavoritesEmptyMessage => "お気に入りに登録されたプレハブがありません。";

	[MenuItem("Tools/Particle Prefab Browser")]
	private static void OpenWindow() {
		var window = GetWindow<ParticlePrefabBrowser>("Particle Prefab Browser");
		window.minSize = new Vector2(480f, 300f);
		window.LoadFromCache();
	}

	protected override void DrawExtraToolbarFilters() {
		if (GUILayout.Button("Export ★", EditorStyles.toolbarButton, GUILayout.Width(70f))) {
			ExportFavoritesAsPackage();
		}
	}

	protected override bool PassesTypeFilter(Entry entry) {
		return true;
	}

	protected override string GetEntryName(Entry entry) {
		return entry.Prefab != null ? entry.Prefab.name : Path.GetFileNameWithoutExtension(entry.AssetPath);
	}

	protected override UnityEngine.Object GetSelectionObject(Entry entry) {
		return entry.Prefab;
	}

	protected override GameObject LoadPrefabForPreview(Entry entry) {
		return entry.Prefab;
	}

	protected override LivePreview CreateLivePreview(Entry entry, GameObject instance, int slot, Vector3 origin) {
		var useBuiltinPipeline = UsesBuiltinOnlyShader(instance);
		DisableGrabPassRenderers(instance);

		var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
		var duration = 1f;
		foreach (var system in systems) {
			system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			if (system.useAutoRandomSeed) {
				system.useAutoRandomSeed = false;
			}

			duration = Mathf.Max(duration, system.main.duration);
			system.Simulate(0f, false, true);
		}

		return new LivePreview {
			Instance = instance,
			Systems = systems,
			Duration = duration,
			UseBuiltinPipeline = useBuiltinPipeline,
			Slot = slot,
			Origin = origin
		};
	}

	protected override void AdvanceLivePreview(LivePreview live, float deltaTime) {
		live.Time += deltaTime;
		if (live.Time >= live.Duration) {
			live.Time = 0f;
			foreach (var system in live.Systems) {
				system.Simulate(0f, false, true);
			}
		} else {
			foreach (var system in live.Systems) {
				system.Simulate(deltaTime, false, false);
			}
		}
	}

	protected override void RestartLivePreview(LivePreview live) {
		live.Time = 0f;
		foreach (var system in live.Systems) {
			system.Simulate(0f, false, true);
		}
	}

	protected override bool HasPlaybackControls(LivePreview live) {
		return true;
	}

	protected override bool UseSrpForLivePreview(LivePreview live) {
		return !live.UseBuiltinPipeline;
	}

	protected override Texture RenderLivePreview(Entry entry, Rect rect) {
		var live = GetOrCreateLivePreview(entry);
		if (live == null || live.Systems.Length == 0) {
			return null;
		}

		return base.RenderLivePreview(entry, rect);
	}

	private static bool UsesBuiltinOnlyShader(GameObject instance) {
		if (GraphicsSettings.currentRenderPipeline == null) {
			return false;
		}

		foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
			foreach (var material in renderer.sharedMaterials) {
				if (material == null || material.shader == null) {
					continue;
				}

				if (IsBuiltinOnlyShader(material.shader)) {
					return true;
				}
			}
		}

		return false;
	}

	private static bool IsBuiltinOnlyShader(Shader shader) {
		var name = shader.name;
		return name.StartsWith("Legacy Shaders/", StringComparison.Ordinal) ||
		       name.StartsWith("Particles/", StringComparison.Ordinal) ||
		       name.StartsWith("Mobile/Particles/", StringComparison.Ordinal) ||
		       name.StartsWith("Mobile/", StringComparison.Ordinal) && name.Contains("Particle");
	}

	private static void DisableGrabPassRenderers(GameObject instance) {
		if (GraphicsSettings.currentRenderPipeline == null) {
			return;
		}

		foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
			foreach (var material in renderer.sharedMaterials) {
				if (material != null && UsesGrabPass(material.shader)) {
					renderer.enabled = false;
					break;
				}
			}
		}
	}

	private static bool UsesGrabPass(Shader shader) {
		if (shader == null) {
			return false;
		}

		if (GrabPassShaderCache.TryGetValue(shader, out var uses)) {
			return uses;
		}

		uses = false;
		var path = AssetDatabase.GetAssetPath(shader);
		if (!string.IsNullOrEmpty(path) && path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) &&
		    File.Exists(path)) {
			try {
				uses = File.ReadAllText(path).Contains("GrabPass");
			} catch (Exception) {
				uses = false;
			}
		}

		GrabPassShaderCache.Add(shader, uses);

		return uses;
	}

	protected override string ClassifyDependency(string assetPath) {
		if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) {
			return "DependencyPrefabs";
		}

		if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) {
			return "Materials";
		}

		var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
		if (mainType != null && typeof(Texture).IsAssignableFrom(mainType)) {
			return "Textures";
		}

		return "Others";
	}

	protected override string ClassifyCommonDependency(string assetPath) {
		var category = ClassifyDependency(assetPath);
		return category == "DependencyPrefabs" ? "Others" : category;
	}

	protected override void ScanAssets(List<Entry> entries, List<string> cacheGuids) {
		var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
		foreach (var guid in prefabGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			if (!ContainsParticleSystem(path)) {
				continue;
			}

			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab == null) {
				continue;
			}

			entries.Add(new Entry(prefab, path));
			cacheGuids.Add(guid);
		}
	}

	protected override Entry CreateEntryFromCachedPath(string path) {
		var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
		if (prefab == null) {
			return null;
		}

		return new Entry(prefab, path);
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

	private static bool ContainsParticleSystem(string assetPath) {
		var prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
		try {
			return prefabRoot != null && prefabRoot.GetComponentInChildren<ParticleSystem>(true) != null;
		} finally {
			if (prefabRoot != null) {
				PrefabUtility.UnloadPrefabContents(prefabRoot);
			}
		}
	}

	public class LivePreview : LivePreviewBase {
		public ParticleSystem[] Systems;
		public float Duration;
		public bool UseBuiltinPipeline;
	}

	public class Entry : EntryBase {
		public readonly GameObject Prefab;

		public Entry(GameObject prefab, string assetPath) : base(assetPath) {
			Prefab = prefab;
		}
	}
}
