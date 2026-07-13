using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Small modal-style popup for entering a tag name, used by asset browser windows.
/// </summary>
public class TagInputWindow : EditorWindow {
	private string _tagText = string.Empty;
	private Action<string> _onSubmit;
	private bool _focusRequested;

	public static void Open(EditorWindow owner, Action<string> onSubmit) {
		var window = CreateInstance<TagInputWindow>();
		window._onSubmit = onSubmit;
		window.titleContent = new GUIContent("Add Tag");

		var size = new Vector2(280f, 54f);
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
		var evt = Event.current;
		if (evt.type == EventType.KeyDown) {
			if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) {
				Submit();
				evt.Use();
				return;
			}

			if (evt.keyCode == KeyCode.Escape) {
				Close();
				evt.Use();
				return;
			}
		}

		EditorGUILayout.Space(4f);

		using (new EditorGUILayout.HorizontalScope()) {
			GUI.SetNextControlName("TagInputField");
			_tagText = EditorGUILayout.TextField(_tagText);

			if (GUILayout.Button("Add", GUILayout.Width(60f))) {
				Submit();
				return;
			}

			if (GUILayout.Button("Cancel", GUILayout.Width(60f))) {
				Close();
				return;
			}
		}

		if (!_focusRequested) {
			_focusRequested = true;
			EditorGUI.FocusTextInControl("TagInputField");
		}
	}

	private void Submit() {
		var tag = _tagText?.Trim();
		if (!string.IsNullOrEmpty(tag)) {
			_onSubmit?.Invoke(tag);
		}

		Close();
	}
}
