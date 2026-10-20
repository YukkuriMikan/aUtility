using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class ShaderBrowser : AssetBrowserWindow<ShaderBrowser.Entry, ShaderBrowser.LivePreview> {
	private const string CacheFolderName = "ShaderBrowserCache";
	private const string CacheFileName = "shader_cache.txt";
	private const string FavoritesFileName = "shader_favorites.txt";
	private const string ShowShaderBallKey = "ShaderBrowser.ShowShaderBall";
	private const string ShowSurfaceKey = "ShaderBrowser.ShowSurface";
	private const string ShowUguiKey = "ShaderBrowser.ShowUgui";
	private const string ShaderBallGuid = "bceb74a43bc34eb5b669a8675b694d9a";
	private const string LegacyShaderBallPath =
		"Assets/package/Editor/aUtility/Browser/ShaderBrowserAssets/ShaderBall.fbx";

	private static string _cacheFilePath;
	private static string _favoritesFilePath;
	private static string _shaderBallPath;
	private GUIStyle _previewTypeStyle;
	private bool _showShaderBall = true;
	private bool _showSurface = true;
	private bool _showUgui = true;

	public enum PreviewType {
		ShaderBall,
		SurfacePlane,
		UguiPanel
	}

	protected override string PrefsKeyPrefix => "ShaderBrowser";
	protected override string CountLabel => "Shaders";
	protected override string EmptyCacheMessage =>
		"キャッシュが空です。Rescan を押して Shader アセットをスキャンしてください。";
	protected override float DefaultPreviewSize => 144f;
	protected override float DefaultCameraPitch => 12f;
	protected override float DefaultCameraDistance => 3.5f;
	protected override float MaxPlaybackSpeed => 1f;
	protected override float MaxCameraDistance => 20f;
	protected override string RestartButtonLabel => "Restart";
	protected override string ExportRootFolder => "ExportedShader";
	protected override string ExportEntryDialogTitle => "Export Shader Package";
	protected override string ExportFavoritesDialogTitle => "Export Favorite Shaders";
	protected override string ExportFavoritesDefaultFileName => "ShaderFavorites.unitypackage";
	protected override string ExportFavoritesEmptyMessage => "お気に入りに登録されたシェーダーがありません。";
	protected override bool ShowPlaybackToolbar => false;
	protected override bool SupportsSceneUsage => true;
	protected override bool UsageMatchesReferencedAssetDependencies => true;
	protected override double PreviewUpdateInterval => 1d;

	[MenuItem("Tools/Shader Browser")]
	private static void OpenWindow() {
		var window = GetWindow<ShaderBrowser>("Shader Browser");
		window.minSize = new Vector2(480f, 300f);
		window.LoadFromCache();
	}

	protected override void OnEnable() {
		_showShaderBall = EditorPrefs.GetBool(ShowShaderBallKey, true);
		_showSurface = EditorPrefs.GetBool(ShowSurfaceKey, true);
		_showUgui = EditorPrefs.GetBool(ShowUguiKey, true);
		base.OnEnable();
	}

	protected override void DrawExtraToolbarButtons() {
		if (GUILayout.Button(GetToolbarContent("Export ★", "SaveAs",
			    "お気に入りのシェーダーをUnityパッケージとして書き出します。"),
		    EditorStyles.toolbarButton, GUILayout.Width(85f))) {
			ExportFavoritesAsPackage();
		}
	}

	protected override void DrawExtraToolbarFilters() {
		GUILayout.Space(8f);
		DrawTypeFilter(ref _showShaderBall, ShowShaderBallKey, "Ball", "シェーダーボール表示を使用するシェーダー");
		DrawTypeFilter(ref _showSurface, ShowSurfaceKey, "Surface", "Surface Shader（Plane表示）");
		DrawTypeFilter(ref _showUgui, ShowUguiKey, "uGUI", "uGUI Shader（Panel表示）");
	}

	private void DrawTypeFilter(ref bool value, string key, string label, string tooltip) {
		var newValue = GUILayout.Toggle(value, new GUIContent(label, tooltip), EditorStyles.toolbarButton,
			GUILayout.Width(65f));
		if (newValue == value) {
			return;
		}

		value = newValue;
		EditorPrefs.SetBool(key, value);
		_filterDirty = true;
	}

	protected override bool PassesTypeFilter(Entry entry) {
		return entry.Type switch {
			PreviewType.SurfacePlane => _showSurface,
			PreviewType.UguiPanel => _showUgui,
			_ => _showShaderBall
		};
	}

	protected override string GetEntryName(Entry entry) {
		return entry.Shader != null ? entry.Shader.name : Path.GetFileNameWithoutExtension(entry.AssetPath);
	}

	protected override UnityEngine.Object GetSelectionObject(Entry entry) {
		return entry.Shader;
	}

	protected override float GetEntryBottomOverlayHeight(Entry entry) {
		return 20f;
	}

	protected override void DrawEntryOverlay(Rect previewRect, Entry entry) {
		_previewTypeStyle ??= new GUIStyle(EditorStyles.miniBoldLabel) {
			alignment = TextAnchor.MiddleCenter,
			clipping = TextClipping.Clip,
			padding = new RectOffset(5, 5, 1, 1),
			normal = { textColor = Color.white }
		};

		var text = entry.Type switch {
			PreviewType.SurfacePlane => "Surface / Plane",
			PreviewType.UguiPanel => "uGUI / Panel",
			_ => "Shader Ball"
		};
		var size = _previewTypeStyle.CalcSize(new GUIContent(text));
		var badgeRect = new Rect(previewRect.x + 3f, previewRect.yMax - 21f,
			Mathf.Min(previewRect.width - 6f, size.x + 10f), 18f);
		EditorGUI.DrawRect(badgeRect, new Color(0.08f, 0.08f, 0.08f, 0.82f));
		GUI.Label(badgeRect, text, _previewTypeStyle);
	}

	protected override GameObject LoadPrefabForPreview(Entry entry) {
		return AssetDatabase.LoadAssetAtPath<GameObject>(GetShaderBallPath());
	}

	private static string GetShaderBallPath() {
		if (!string.IsNullOrEmpty(_shaderBallPath) &&
		    AssetDatabase.LoadAssetAtPath<GameObject>(_shaderBallPath) != null) {
			return _shaderBallPath;
		}

		// UPM で導入した場合は Packages/com.anest.autility 以下へ配置されるため、
		// Assets/package 固定ではなく同梱 FBX の GUID から実際のパスを解決する。
		_shaderBallPath = AssetDatabase.GUIDToAssetPath(ShaderBallGuid);
		if (string.IsNullOrEmpty(_shaderBallPath)) {
			_shaderBallPath = LegacyShaderBallPath;
		}

		return _shaderBallPath;
	}

	protected override GameObject InstantiatePreviewObject(Entry entry, GameObject prefab) {
		switch (entry.Type) {
			case PreviewType.SurfacePlane:
				return CreateSurfacePlane();
			case PreviewType.UguiPanel:
				return CreateUguiPanel();
			default:
				return base.InstantiatePreviewObject(entry, prefab);
		}
	}

	private GameObject CreateSurfacePlane() {
		var mesh = Resources.GetBuiltinResource<Mesh>("New-Plane.fbx");
		if (mesh == null) {
			return null;
		}

		var plane = EditorUtility.CreateGameObjectWithHideFlags("Surface Shader Plane", HideFlags.HideAndDontSave,
			typeof(MeshFilter), typeof(MeshRenderer));
		plane.GetComponent<MeshFilter>().sharedMesh = mesh;
		plane.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
		plane.transform.localScale = Vector3.one * 0.2f;
		_previewUtility.AddSingleGO(plane);
		return plane;
	}

	private GameObject CreateUguiPanel() {
		var root = EditorUtility.CreateGameObjectWithHideFlags("uGUI Shader Preview", HideFlags.HideAndDontSave,
			typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
		var canvasRect = root.GetComponent<RectTransform>();
		canvasRect.sizeDelta = new Vector2(200f, 200f);
		root.transform.localScale = Vector3.one * 0.01f;

		var canvas = root.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.worldCamera = _previewUtility.camera;
		canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
		                                  AdditionalCanvasShaderChannels.TexCoord2 |
		                                  AdditionalCanvasShaderChannels.TexCoord3 |
		                                  AdditionalCanvasShaderChannels.Normal |
		                                  AdditionalCanvasShaderChannels.Tangent;

		var panel = EditorUtility.CreateGameObjectWithHideFlags("Panel", HideFlags.HideAndDontSave,
			typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		panel.transform.SetParent(root.transform, false);
		var panelRect = panel.GetComponent<RectTransform>();
		panelRect.anchorMin = Vector2.zero;
		panelRect.anchorMax = Vector2.one;
		panelRect.offsetMin = Vector2.zero;
		panelRect.offsetMax = Vector2.zero;
		panel.GetComponent<Image>().color = Color.white;

		_previewUtility.AddSingleGO(root);
		return root;
	}

	protected override LivePreview CreateLivePreview(Entry entry, GameObject instance, int slot, Vector3 origin) {
		if (entry.Shader == null) {
			return null;
		}

		var material = new Material(entry.Shader) {
			hideFlags = HideFlags.HideAndDontSave
		};
		SetFallbackTexture(material, "_MainTex");
		SetFallbackTexture(material, "_BaseMap");

		if (entry.Type == PreviewType.UguiPanel) {
			var image = instance.GetComponentInChildren<Image>(true);
			if (image == null) {
				DestroyImmediate(material);
				return null;
			}

			image.material = material;
			Canvas.ForceUpdateCanvases();
		} else {
			foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
				var materials = renderer.sharedMaterials;
				if (materials.Length == 0) {
					materials = new Material[1];
				}

				for (var index = 0; index < materials.Length; index++) {
					materials[index] = material;
				}

				renderer.sharedMaterials = materials;
			}
		}

		return new LivePreview {
			Instance = instance,
			Slot = slot,
			Origin = origin,
			Material = material,
			Type = entry.Type
		};
	}

	private static void SetFallbackTexture(Material material, string propertyName) {
		if (material.HasProperty(propertyName) && material.GetTexture(propertyName) == null) {
			material.SetTexture(propertyName, Texture2D.whiteTexture);
		}
	}

	protected override Bounds GetPreviewBounds(Entry entry, LivePreview live) {
		if (entry.Type == PreviewType.UguiPanel) {
			return new Bounds(live.Origin, new Vector3(2f, 2f, 0.05f));
		}

		return base.GetPreviewBounds(entry, live);
	}

	protected override bool ShouldShowGround(Entry entry, LivePreview live) {
		return entry.Type == PreviewType.ShaderBall;
	}

	protected override void ReleaseLivePreviewResources(LivePreview live) {
		if (live.Material != null) {
			DestroyImmediate(live.Material);
			live.Material = null;
		}
	}

	protected override void AdvanceLivePreview(LivePreview live, float deltaTime) { }

	protected override bool ShouldAdvanceLivePreview(Entry entry, LivePreview live) {
		return false;
	}

	protected override void RestartLivePreview(LivePreview live) { }

	protected override bool HasPlaybackControls(LivePreview live) {
		return false;
	}

	protected override bool UseSrpForLivePreview(LivePreview live) {
		return GraphicsSettings.currentRenderPipeline != null;
	}

	protected override string ClassifyDependency(string assetPath) {
		if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
		    assetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
		    assetPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase)) {
			return "Shaders";
		}

		if (assetPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase) ||
		    assetPath.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase)) {
			return "Includes";
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
		return ClassifyDependency(assetPath);
	}

	protected override void ScanAssets(List<Entry> entries, List<string> cacheGuids) {
		var shaderGuids = AssetDatabase.FindAssets("t:Shader", new[] { SearchRoot });
		foreach (var guid in shaderGuids) {
			var path = AssetDatabase.GUIDToAssetPath(guid);
			var entry = CreateEntry(path);
			if (entry == null) {
				continue;
			}

			entries.Add(entry);
			cacheGuids.Add(guid);
		}
	}

	protected override Entry CreateEntryFromCachedPath(string path) {
		return CreateEntry(path);
	}

	private static Entry CreateEntry(string path) {
		if (string.IsNullOrEmpty(path)) {
			return null;
		}

		var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
		if (shader == null) {
			return null;
		}

		return new Entry(path, shader, DeterminePreviewType(path, shader));
	}

	private static PreviewType DeterminePreviewType(string assetPath, Shader shader) {
		if (IsUguiShader(assetPath, shader)) {
			return PreviewType.UguiPanel;
		}

		if (IsSurfaceShader(assetPath)) {
			return PreviewType.SurfacePlane;
		}

		return PreviewType.ShaderBall;
	}

	private static bool IsUguiShader(string assetPath, Shader shader) {
		var shaderName = shader.name ?? string.Empty;
		var normalizedPath = assetPath.Replace('\\', '/');
		if (shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase) ||
		    shaderName.IndexOf("/UI/", StringComparison.OrdinalIgnoreCase) >= 0 ||
		    normalizedPath.IndexOf("/UI/", StringComparison.OrdinalIgnoreCase) >= 0) {
			return true;
		}

		return shader.FindPropertyIndex("_StencilComp") >= 0 && shader.FindPropertyIndex("_Stencil") >= 0 &&
		       shader.FindPropertyIndex("_ColorMask") >= 0;
	}

	private static bool IsSurfaceShader(string assetPath) {
		if (!assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || !File.Exists(assetPath)) {
			return false;
		}

		try {
			return File.ReadAllText(assetPath).IndexOf("#pragma surface", StringComparison.OrdinalIgnoreCase) >= 0;
		} catch (IOException) {
			return false;
		} catch (UnauthorizedAccessException) {
			return false;
		}
	}

	protected override int CompareEntries(Entry left, Entry right) {
		return string.Compare(GetEntryName(left), GetEntryName(right), StringComparison.OrdinalIgnoreCase);
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
		public Material Material;
		public PreviewType Type;
	}

	public class Entry : EntryBase {
		public readonly Shader Shader;
		public readonly PreviewType Type;

		public Entry(string assetPath, Shader shader, PreviewType type) : base(assetPath) {
			Shader = shader;
			Type = type;
		}
	}
}
