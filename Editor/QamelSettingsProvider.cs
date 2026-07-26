using System.IO;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Project Settings > Qamel. Creates and edits the QamelSettings asset so setup
    /// is: install package, paste API key, press play.
    /// </summary>
    internal static class QamelSettingsProvider
    {
        const string AssetDir = "Assets/Qamel/Resources";
        const string AssetPath = AssetDir + "/" + QamelSettings.ResourceName + ".asset";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Qamel", SettingsScope.Project)
            {
                label = "Qamel",
                keywords = new[] { "qamel", "capture", "bug", "report", "playtest", "qa" },
                guiHandler = _ => Draw(),
            };
        }

        static void Draw()
        {
            EditorGUILayout.Space(8);

            var settings = QamelSettings.LoadFromResources();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Qamel Capture is installed but not configured yet.\n" +
                    "Create the settings asset, then paste your project API key.",
                    MessageType.Info);
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Create Qamel settings", GUILayout.Width(220)))
                {
                    Directory.CreateDirectory(AssetDir);
                    var asset = ScriptableObject.CreateInstance<QamelSettings>();
                    AssetDatabase.CreateAsset(asset, AssetPath);
                    AssetDatabase.SaveAssets();
                    Selection.activeObject = asset;
                }
                return;
            }

            var serialized = new SerializedObject(settings);
            var property = serialized.GetIterator();
            property.NextVisible(true); // skip m_Script
            while (property.NextVisible(false))
            {
                EditorGUILayout.PropertyField(property, true);
            }
            if (serialized.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Qamel keeps captured data only in memory on the device and delivers it to the " +
                "Qamel servers. Nothing is written to the player's disk, so an API key and " +
                "endpoint are required for reports to go anywhere.",
                MessageType.None);
        }
    }
}
