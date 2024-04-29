// Copyright (c) 2023-2024 Koji Hasegawa.
// This software is released under the MIT License.

using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace TestHelper.RuntimeInternals
{
    /// <summary>
    /// Helper class for scene management.
    /// </summary>
    public static class SceneManagerHelper
    {
        /// <summary>
        /// Loading scene file.
        /// This attribute has the following benefits:
        /// - Can be use same code for running Edit Mode tests, Play Mode tests in Editor, and on Player.
        /// - Can be specified scene path by [glob](https://en.wikipedia.org/wiki/Glob_(programming)) pattern. However, there are restrictions, top level and scene name cannot be omitted.
        /// - Can be specified scene path by relative path from the test class file.
        /// </summary>
        /// <param name="path">Scene file path.
        /// The path starts with `Assets/` or `Packages/` or `.`.
        /// And package name using `name` instead of `displayName`, when scenes in the package.
        /// (e.g., `Packages/com.nowsprinting.test-helper/Tests/Scenes/Scene.unity`)
        /// </param>
        /// <returns></returns>
        /// <remarks>
        /// When loading the scene that is not in "Scenes in Build", use <see cref="TestHelper.Attributes.BuildSceneAttribute"/>.
        /// </remarks>
        [SuppressMessage("ReSharper", "InvalidXmlDocComment")]
        public static IEnumerator LoadSceneAsync(string path, [CallerFilePath] string callerFilePath = null)
        {
            var existScenePath = GetExistScenePath(path, callerFilePath);
            AsyncOperation loadSceneAsync = null;

            if (Application.isEditor)
            {
#if UNITY_EDITOR
                if (Application.isPlaying)
                {
                    // Play Mode tests running in Editor
                    loadSceneAsync = EditorSceneManager.LoadSceneAsyncInPlayMode(
                        existScenePath,
                        new LoadSceneParameters(LoadSceneMode.Single));
                }
                else
                {
                    // Edit Mode tests
                    EditorSceneManager.OpenScene(existScenePath);
                }
#endif
            }
            else
            {
                // Play Mode tests running on Player
                loadSceneAsync = SceneManager.LoadSceneAsync(existScenePath);
            }

            yield return loadSceneAsync;
        }

        /// <summary>
        /// Get existing scene file path matches a glob pattern.
        /// </summary>
        /// <param name="path">Scene file path. Can be specified path by glob pattern. However, there are restrictions, top level and scene name cannot be omitted.</param>
        /// <param name="callerFilePath">CallerFilePath via <c>LoadSceneAttribute</c>, <c>BuildSceneAttribute</c>, and <c>LoadSceneAsync</c>.</param>
        /// <returns>Existing scene file path</returns>
        /// <exception cref="ArgumentException">Invalid path format</exception>
        /// <exception cref="FileNotFoundException">Scene file not found</exception>
        internal static string GetExistScenePath(string path, string callerFilePath)
        {
            if (path.StartsWith("."))
            {
                path = GetAbsolutePath(path, callerFilePath);
            }

            ValidatePath(path);
#if UNITY_EDITOR
            return GetExistScenePathInEditor(path);
#else
            return GetExistScenePathOnPlayer(path);
#endif
        }

        internal static string GetAbsolutePath(string relativePath, string callerFilePath)
        {
            var callerDirectory = Path.GetDirectoryName(callerFilePath);
            // ReSharper disable once AssignNullToNotNullAttribute
            var absolutePath = Path.GetFullPath(Path.Combine(callerDirectory, relativePath));

            var assetsIndexOf = absolutePath.IndexOf("Assets", StringComparison.Ordinal);
            if (assetsIndexOf > 0)
            {
                return absolutePath.Substring(assetsIndexOf);
            }

            var packageIndexOf = absolutePath.IndexOf("Packages", StringComparison.Ordinal);
            if (packageIndexOf > 0)
            {
                return absolutePath.Substring(packageIndexOf);
            }

            Debug.LogError(
                $"Can not resolve absolute path. relativePath: {relativePath}, callerFilePath: {callerFilePath}");
            return null;
            // Note: Do not use Exception (and Assert). Because freezes async tests on UTF v1.3.4, See UUM-25085.
        }

        private static void ValidatePath(string path)
        {
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
            {
                throw new ArgumentException($"Scene path must start with `Assets/` or `Packages/`. path: {path}");
            }

            var split = path.Split('/');
            if (split[0].Equals("Packages") && split[1].IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                throw new ArgumentException($"Wildcards cannot be used in the package name of path: {path}");
            }

            if (!path.EndsWith(".unity"))
            {
                throw new ArgumentException($"Scene path must ends with `.unity`. path: {path}");
            }

            if (split.Last().IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                throw new ArgumentException($"Wildcards cannot be used in the most right section of path: {path}");
            }
        }

        private static string SearchFolder(string path)
        {
            var searchFolder = new StringBuilder();
            var split = path.Split('/');
            for (var i = 0; i < split.Length - 1; i++)
            {
                var s = split[i];
                if (s.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    break;
                }

                searchFolder.Append($"{s}/");
            }

            return searchFolder.ToString();
        }

        private static Regex ConvertRegexFromGlob(string glob)
        {
            var regex = new StringBuilder();
            foreach (var c in glob)
            {
                switch (c)
                {
                    case '*':
                        regex.Append("[^/]*");
                        break;
                    case '?':
                        regex.Append("[^/]");
                        break;
                    case '.':
                        regex.Append("\\.");
                        break;
                    case '\\':
                        regex.Append("\\\\");
                        break;
                    default:
                        regex.Append(c);
                        break;
                }
            }

            regex.Replace("[^/]*[^/]*", ".*"); // globstar (**)
            regex.Insert(0, "^");
            regex.Append("$");
            return new Regex(regex.ToString());
        }

#if UNITY_EDITOR
        /// <summary>
        /// For the run in editor, use <c>AssetDatabase</c> to search scenes and compare paths.
        /// </summary>
        /// <param name="path"></param>
        private static string GetExistScenePathInEditor(string path)
        {
            var regex = ConvertRegexFromGlob(path);
            foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset", new[] { SearchFolder(path) }))
            {
                var existScenePath = AssetDatabase.GUIDToAssetPath(guid);
                if (regex.IsMatch(existScenePath))
                {
                    return existScenePath;
                }
            }

            throw new FileNotFoundException($"Scene `{path}` is not found in AssetDatabase");
        }
#endif

        /// <summary>
        /// For the run on player, returns scene name from the path.
        /// </summary>
        /// <param name="path"></param>
        internal static string GetExistScenePathOnPlayer(string path)
        {
            return path.Split('/').Last().Split('.').First();
        }
    }
}
