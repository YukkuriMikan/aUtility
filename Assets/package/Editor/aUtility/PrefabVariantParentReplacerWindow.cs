using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class PrefabVariantParentReplacerWindow : EditorWindow {
	private GameObject _newParentPrefab;
	private bool _tryRepairReferences = true;

	[MenuItem("Tools/Replace Prefab Variant Parent")]
	private static void OpenWindow() {
		var window = GetWindow<PrefabVariantParentReplacerWindow>("Replace Variant Parent");
		window.minSize = new Vector2(420f, 150f);
	}

	private void OnGUI() {
		EditorGUILayout.LabelField("Prefab Variant Parent Replacer", EditorStyles.boldLabel);
		EditorGUILayout.HelpBox("Project ウィンドウで選択したプレハブバリアントの親を差し替えます。", MessageType.Info);

		_newParentPrefab = (GameObject)EditorGUILayout.ObjectField(
			"新しい親プレハブ", _newParentPrefab, typeof(GameObject), false);
		_tryRepairReferences = EditorGUILayout.Toggle(
			"参照の修復を試みる", _tryRepairReferences);

		using (new EditorGUI.DisabledScope(!CanReplace())) {
			if (GUILayout.Button("親プレハブを差し替える", GUILayout.Height(28f))) {
				ReplaceSelectedVariants();
			}
		}
	}

	private bool CanReplace() {
		return IsPrefabAsset(_newParentPrefab) && GetSelectedVariants().Count > 0;
	}

	private static bool IsPrefabAsset(GameObject prefab) {
		return prefab != null && PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.NotAPrefab;
	}

	private static List<GameObject> GetSelectedVariants() {
		var variants = new List<GameObject>();
		foreach (var selectedObject in Selection.objects) {
			var prefab = selectedObject as GameObject;
			if (prefab != null && PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant) {
				variants.Add(prefab);
			}
		}

		return variants;
	}

	private void ReplaceSelectedVariants() {
		var variants = GetSelectedVariants();
		if (variants.Count == 0) {
			EditorUtility.DisplayDialog("差し替えできません", "プロジェクトウィンドウでプレハブバリアントを選択してください。", "OK");
			return;
		}

		if (!EditorUtility.DisplayDialog(
			"親プレハブを差し替えますか？",
			$"{variants.Count} 件のプレハブバリアントを差し替えます。\nこの操作は元に戻せません。",
			"差し替える", "キャンセル")) {
			return;
		}

		try {
			for (var index = 0; index < variants.Count; index++) {
				var variant = variants[index];
				EditorUtility.DisplayProgressBar("親プレハブを差し替え中", variant.name, (float)index / variants.Count);
				ReplaceVariantParent(variant, _newParentPrefab, _tryRepairReferences);
			}
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
		} catch (System.Exception exception) {
			Debug.LogException(exception);
			EditorUtility.DisplayDialog("差し替えに失敗しました", exception.Message, "OK");
		} finally {
			EditorUtility.ClearProgressBar();
		}
	}

	private static void ReplaceVariantParent(GameObject variant, GameObject newParentPrefab, bool tryRepairReferences) {
		if (variant == newParentPrefab) {
			throw new System.InvalidOperationException("差し替え先には対象と異なるプレハブを指定してください。");
		}

		var variantPath = AssetDatabase.GetAssetPath(variant);
		var instance = PrefabUtility.InstantiatePrefab(variant) as GameObject;
		if (instance == null) {
			throw new System.InvalidOperationException($"プレハブをインスタンス化できませんでした: {variantPath}");
		}

		try {
			// 手動手順の「Unpack」に相当。バリアント層を 1 段だけ剥がすと、
			// インスタンスは旧親プレハブのインスタンスになる。
			PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

			if (PrefabUtility.IsAnyPrefabInstanceRoot(instance)) {
				// 手動手順の「Replace and keep overrides」に相当。
				PrefabUtility.ReplacePrefabAssetOfPrefabInstance(instance, newParentPrefab, new PrefabReplacingSettings {
					changeRootNameToAssetName = false,
					logInfo = false,
					objectMatchMode = ObjectMatchMode.ByHierarchy,
					prefabOverridesOptions = tryRepairReferences
						? PrefabOverridesOptions.KeepAllPossibleOverrides
						: PrefabOverridesOptions.ClearAllNonDefaultOverrides
				}, InteractionMode.AutomatedAction);
			} else {
				// 完全に Unpack された場合（旧親との接続が無い場合）のフォールバック。
				PrefabUtility.ConvertToPrefabInstance(instance, newParentPrefab, new ConvertToPrefabInstanceSettings {
					changeRootNameToAssetName = false,
					objectMatchMode = ObjectMatchMode.ByHierarchy,
					componentsNotMatchedBecomesOverride = tryRepairReferences,
					gameObjectsNotMatchedBecomesOverride = tryRepairReferences,
					recordPropertyOverridesOfMatches = true
				}, InteractionMode.AutomatedAction);
			}

			// 手動手順の「差し替えたいプレハブへドラッグ&ドロップ」に相当。
			PrefabUtility.SaveAsPrefabAsset(instance, variantPath, out var success);
			if (!success) {
				throw new System.InvalidOperationException($"プレハブの保存に失敗しました: {variantPath}");
			}
		} finally {
			DestroyImmediate(instance);
		}
	}
}