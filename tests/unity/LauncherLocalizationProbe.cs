using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MCPForUnityLauncher.Editor;
using UnityEditor;
using UnityEngine;

public static class LauncherLocalizationProbe
{
    private const string PreferenceKey = "MCPForUnityLauncher.EditorLanguage";
    private const string UpstreamPreferenceKey = "MCPForUnity.EditorLanguage";
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run()
    {
        bool existed = EditorPrefs.HasKey(PreferenceKey);
        int saved = EditorPrefs.GetInt(PreferenceKey);
        bool upstreamExisted = EditorPrefs.HasKey(UpstreamPreferenceKey);
        int upstreamSaved = EditorPrefs.GetInt(UpstreamPreferenceKey);
        EditorWindow window = null;
        int changes = 0;
        Action changed = () => changes++;
        LauncherLocalization.LanguageChanged += changed;
        try
        {
            var languages = Enum.GetValues(typeof(LauncherLanguage)).Cast<LauncherLanguage>().ToArray();
            Require(languages.Select(l => (int)l).SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "Unexpected language coverage");
            var labels = (string[])typeof(LauncherLocalization).GetField("LanguageLabels", Flags).GetValue(null);
            Require(labels.Length == 5 && labels.All(l => !string.IsNullOrWhiteSpace(l)), "Language selector labels are incomplete");
            Require(labels.SequenceEqual(new[] { "English", "日本語", "한국어", "繁體中文", "简体中文" }), "Language selector order is incorrect");
            var order = (LauncherLanguage[])typeof(LauncherLocalization).GetField("LanguageOrder", Flags).GetValue(null);
            Require(order.Select(l => (int)l).SequenceEqual(new[] { 0, 1, 4, 2, 3 }), "Persisted language IDs or display order changed");
            var texts = (Dictionary<string, string[]>)typeof(LauncherLocalization).GetField("Texts", Flags).GetValue(null);
            Require(texts.Count > 0, "Translations are empty");
            foreach (var item in texts)
            {
                Require(!string.IsNullOrWhiteSpace(item.Key) && item.Key.All(c => c <= 127), "English fallback is invalid");
                Require(item.Value.Length == 4 && item.Value.All(v => !string.IsNullOrWhiteSpace(v)), "Translation coverage is incomplete");
                foreach (var translation in item.Value)
                    Require(Placeholders(item.Key).SequenceEqual(Placeholders(translation)), "Translation format placeholders differ");
            }

            var detect = typeof(LauncherLocalization).GetMethod("DetectLanguage", Flags);
            foreach (SystemLanguage system in Enum.GetValues(typeof(SystemLanguage)))
            {
                var expected = system == SystemLanguage.Japanese ? LauncherLanguage.Japanese :
                    system == SystemLanguage.Korean ? LauncherLanguage.Korean :
                    system == SystemLanguage.ChineseTraditional ? LauncherLanguage.TraditionalChinese :
                    system == SystemLanguage.Chinese || system == SystemLanguage.ChineseSimplified ? LauncherLanguage.SimplifiedChinese : LauncherLanguage.English;
                Require((LauncherLanguage)detect.Invoke(null, new object[] { system }) == expected, "System language mapping is incorrect");
            }
            EditorPrefs.DeleteKey(PreferenceKey);
            Require(LauncherLocalization.CurrentLanguage == (LauncherLanguage)detect.Invoke(null, new object[] { Application.systemLanguage }), "System default was not used");

            foreach (var language in languages)
            {
                LauncherLocalization.SetLanguage(language);
                Require(EditorPrefs.GetInt(PreferenceKey, -1) == (int)language && LauncherLocalization.CurrentLanguage == language, "Language choice did not persist");
                int before = changes;
                LauncherLocalization.SetLanguage(language);
                Require(changes == before, "An unchanged language triggered another refresh");
                foreach (var item in texts)
                {
                    string expected = language == LauncherLanguage.English ? item.Key : item.Value[Array.IndexOf(order, language) - 1];
                    Require(LauncherLocalization.Text(item.Key) == expected, "Selected language did not reach Text");
                    var slots = Placeholders(item.Key);
                    var arguments = Enumerable.Range(0, slots.Length == 0 ? 0 : slots.Max() + 1).Select(i => (object)("probe_" + i)).ToArray();
                    Require(LauncherLocalization.Format(item.Key, arguments) == string.Format(expected, arguments), "Formatted translation is incorrect");
                }
                const string unknown = "Unknown English fallback";
                Require(LauncherLocalization.Text(unknown) == unknown, "Unknown text did not use the English source fallback");
            }

            EditorPrefs.SetInt(PreferenceKey, 99);
            Require(LauncherLocalization.CurrentLanguage == LauncherLanguage.English, "Invalid preference did not fall back to English");
            LauncherLocalization.SetLanguage(LauncherLanguage.Japanese);
            LauncherLocalization.SetLanguage((LauncherLanguage)99);
            Require(LauncherLocalization.CurrentLanguage == LauncherLanguage.English, "Invalid selected language did not fall back to English");

            var dependencyWindow = Type.GetType("MCPForUnityLauncher.Editor.LauncherDependencyWindow, MCPForUnityLauncher.Setup.Editor", true);
            dependencyWindow.GetMethod("Open", Flags).Invoke(null, null);
            window = Resources.FindObjectsOfTypeAll<EditorWindow>().Single(w => w.GetType() == dependencyWindow);
            Require(window.titleContent.text == "MCP Launcher Setup", "Setup window did not use English");
            string english = LauncherLocalization.Text("Language");
            int priorChanges = changes;
            LauncherLocalization.SetLanguage(LauncherLanguage.Japanese);
            Require(changes == priorChanges + 1 && LauncherLocalization.Text("Language") != english, "Language switching did not refresh text immediately");
            Require(window.titleContent.text == LauncherLocalization.Text("MCP Launcher Setup") && window.titleContent.text != "MCP Launcher Setup", "An open window did not update its language");
            Require(EditorPrefs.HasKey(UpstreamPreferenceKey) == upstreamExisted && EditorPrefs.GetInt(UpstreamPreferenceKey) == upstreamSaved, "Launcher modified MCP for Unity's language preference");
            Debug.Log("LAUNCHER_LOCALIZATION_VERIFICATION: passed");
        }
        finally
        {
            if (window != null) window.Close();
            LauncherLocalization.LanguageChanged -= changed;
            if (existed) EditorPrefs.SetInt(PreferenceKey, saved); else EditorPrefs.DeleteKey(PreferenceKey);
        }
    }

    private static int[] Placeholders(string value)
    {
        return Regex.Matches(value, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}")
            .Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).OrderBy(i => i).ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
