// Animal Animation Setup
// For packs that ship the character and each animation as separate FBX files
// (e.g. SK_Chicken.fbx + Chicken@Idle.fbx, Chicken@Walk.fbx, ...). In one click it:
//   1. sets the character to Generic and creates its Avatar,
//   2. sets every Animal@Action file to Generic + Copy From Other Avatar (the character's) and loops cycles,
//   3. creates <Animal>_Controller with a state per animation (Idle default, Speed-driven Idle/Walk/Run),
//   4. drops the animated animal into the open scene and saves <Animal>_Animated.prefab.
//
// Usage: put this file in any "Editor" folder under Assets (e.g. Assets/Editor/), then right-click the
// "FBX Files" folder (or the animal folder, or the FBX files themselves) > Setup Animal Animations.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AnaAlMadinah.EditorTools
{
    public static class AnimalAnimationSetup
    {
        const string Title = "Animal Animation Setup";

        // Animations containing these words play once; everything else loops.
        static readonly string[] OneShotWords =
        {
            "death", "die", "dead", "hit", "attack", "jump", "fall",
            "getup", "standup", "liedown", "sitdown",
        };

        [MenuItem("Tools/Animal Animation Setup/Setup Selected FBX Folder")]
        [MenuItem("Assets/Setup Animal Animations")]
        static void SetupSelected()
        {
            var models = Selection.GetFiltered<UnityEngine.Object>(SelectionMode.Assets)
                .Select(o => AssetDatabase.GetAssetPath(o))
                .SelectMany(ExpandModels)
                .Distinct()
                .ToList();

            if (models.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "Select the folder that contains the animal FBX files.", "OK");
                return;
            }

            var report = new List<string>();
            int placed = 0;
            foreach (var folder in models.GroupBy(p => Path.GetDirectoryName(p).Replace('\\', '/')))
            {
                var characters = folder.Where(p => !FileName(p).Contains("@")).ToList();
                var animations = folder.Where(p => FileName(p).Contains("@")).ToList();
                foreach (var character in characters)
                {
                    var animal = AnimalName(character);
                    var mine = characters.Count == 1
                        ? animations
                        : animations.Where(a => string.Equals(AnimPrefix(a), animal, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (mine.Count == 0) continue;

                    try
                    {
                        EditorUtility.DisplayProgressBar(Title, animal, 0.5f);
                        report.Add(Setup(character, mine, animal, placed++));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{Title}] {character}: {e}");
                        report.Add($"{animal}: FAILED (see Console)");
                    }
                    finally
                    {
                        EditorUtility.ClearProgressBar();
                    }
                }
            }

            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(Title,
                report.Count == 0
                    ? "Found no character FBX (without '@') with matching Animal@Action FBX files."
                    : string.Join("\n\n", report) + "\n\nPress Play to see it animate.",
                "OK");
        }

        static string Setup(string characterPath, List<string> animPaths, string animal, int index)
        {
            // 1) The character owns the skeleton: Generic rig, create the Avatar here.
            var charImporter = (ModelImporter)AssetImporter.GetAtPath(characterPath);
            charImporter.animationType = ModelImporterAnimationType.Generic;
            charImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            charImporter.importAnimation = false;
            charImporter.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(characterPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
                throw new Exception("Unity did not create an Avatar for the character.");

            // 2) Every animation file reuses the character's Avatar.
            var clips = new List<(string name, AnimationClip clip)>();
            foreach (var path in animPaths)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = avatar;
                importer.importAnimation = true;
                importer.SaveAndReimport();

                var action = ActionName(path);
                var settings = importer.clipAnimations;
                if (settings == null || settings.Length == 0) settings = importer.defaultClipAnimations;
                foreach (var s in settings) s.loopTime = ShouldLoop(action + s.name);
                importer.clipAnimations = settings;
                importer.SaveAndReimport();

                var fileClips = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                    .OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                    .ToList();
                foreach (var clip in fileClips)
                    clips.Add((fileClips.Count == 1 ? action : $"{action}_{clip.name}", clip));
            }
            if (clips.Count == 0)
                throw new Exception("No animation clips found in the Animal@Action files.");

            // 3) Animator Controller with one state per clip.
            var animalDir = AnimalFolder(characterPath);
            var controllerPath = AssetDatabase.GenerateUniqueAssetPath($"{animalDir}/{animal}_Controller.controller");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;

            var states = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);
            int row = 0;
            foreach (var (name, clip) in clips)
            {
                var stateName = name;
                for (int n = 2; states.ContainsKey(stateName); n++) stateName = $"{name}_{n}";
                var state = machine.AddState(stateName, new Vector3(300, 60 * row++, 0));
                state.motion = clip;
                states[stateName] = state;
            }

            var idle = FindState(states, "idle");
            var walk = FindState(states, "walk");
            var run = FindState(states, "run");
            machine.defaultState = idle != null ? idle : states.Values.First();
            if (idle != null && walk != null)
            {
                Link(idle, walk, AnimatorConditionMode.Greater, 0.1f);
                Link(walk, idle, AnimatorConditionMode.Less, 0.1f);
            }
            if (walk != null && run != null)
            {
                Link(walk, run, AnimatorConditionMode.Greater, 2f);
                Link(run, walk, AnimatorConditionMode.Less, 2f);
            }
            else if (idle != null && run != null)
            {
                Link(idle, run, AnimatorConditionMode.Greater, 0.1f);
                Link(run, idle, AnimatorConditionMode.Less, 0.1f);
            }
            EditorUtility.SetDirty(controller);

            // 4) Animated instance in the open scene + reusable prefab.
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(characterPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = animal;
            var pivot = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            go.transform.position = pivot + new Vector3(index * 2f, 0, 0);
            if (!go.TryGetComponent(out Animator animator)) animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            Undo.RegisterCreatedObjectUndo(go, Title);

            var prefabDir = $"{animalDir}/Prefabs";
            if (!AssetDatabase.IsValidFolder(prefabDir)) AssetDatabase.CreateFolder(animalDir, "Prefabs");
            var prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{prefabDir}/{animal}_Animated.prefab");
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, prefabPath, InteractionMode.UserAction);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;

            return $"{animal}: {clips.Count} animation(s), default \"{machine.defaultState.name}\"\n" +
                   $"Controller: {controllerPath}\nPrefab: {prefabPath}";
        }

        static void Link(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = 0.15f;
            t.AddCondition(mode, threshold, "Speed");
        }

        // Prefers an exact name ("Idle"), then a prefix ("Idle_01"), then any match.
        static AnimatorState FindState(Dictionary<string, AnimatorState> states, string word)
        {
            var key = states.Keys.FirstOrDefault(k => k.Equals(word, StringComparison.OrdinalIgnoreCase))
                      ?? states.Keys.FirstOrDefault(k => k.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                      ?? states.Keys.FirstOrDefault(k => k.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            return key != null ? states[key] : null;
        }

        static bool ShouldLoop(string text)
        {
            var t = text.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            return !OneShotWords.Any(w => t.Contains(w));
        }

        static IEnumerable<string> ExpandModels(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return AssetDatabase.FindAssets("t:Model", new[] { path })
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .Where(IsFbx);
            return IsFbx(path) ? new[] { path } : Enumerable.Empty<string>();
        }

        static bool IsFbx(string path) => path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);

        static string FileName(string path) => Path.GetFileNameWithoutExtension(path);

        static string AnimPrefix(string path) => FileName(path).Split('@')[0];

        static string ActionName(string path)
        {
            var name = FileName(path);
            return name.Substring(name.IndexOf('@') + 1);
        }

        static string AnimalName(string characterPath)
        {
            var name = FileName(characterPath);
            return name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase) ? name.Substring(3) : name;
        }

        // ".../Chicken/FBX Files/SK_Chicken.fbx" -> ".../Chicken"
        static string AnimalFolder(string characterPath)
        {
            var dir = Path.GetDirectoryName(characterPath).Replace('\\', '/');
            return Path.GetFileName(dir).IndexOf("fbx", StringComparison.OrdinalIgnoreCase) >= 0
                ? Path.GetDirectoryName(dir).Replace('\\', '/')
                : dir;
        }
    }
}
