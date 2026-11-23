using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RobotFramework;
using RobotFramework.Components;
using UnityEditor;
using UnityEngine;

namespace MoSim.EditorTools
{
    /// <summary>
    /// Rebuilds the SpawnSelect prefabs for Bomb Squad (16) and Breakaway (3937) from their
    /// full robot prefabs, keeping rendering components and non-trigger colliders and adding the
    /// root Rigidbody and SpawnpointSelectionSettings required by SpawnSelectionMovement.
    /// </summary>
    internal static class RebuildSpawnSelectPrefabs
    {
        private readonly struct RobotTarget
        {
            public readonly string MetadataPath;
            public readonly string SourcePrefabPath;
            public readonly string SpawnPrefabPath;
            public readonly string SpawnName;
            public readonly string[] BumperMeshNames;

            public RobotTarget(string metadataPath, string sourcePrefabPath, string spawnPrefabPath, string spawnName,
                string[] bumperMeshNames)
            {
                MetadataPath = metadataPath;
                SourcePrefabPath = sourcePrefabPath;
                SpawnPrefabPath = spawnPrefabPath;
                SpawnName = spawnName;
                BumperMeshNames = bumperMeshNames;
            }
        }

        private const string BlueBumperMatPath = "Assets/Materials/DarkBlue.mat";
        private const string RedBumperMatPath = "Assets/Materials/DarkRed.mat";

        private static readonly RobotTarget[] Targets =
        {
            new RobotTarget(
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/BombSquad/BombSquad.asset",
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/BombSquad/BombSquad.prefab",
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/BombSquad/BombSquadSpawnSelect.prefab",
                "BombSquadSpawnSelect",
                new[] { "Part 1.068", "Part 1.072" }),
            new RobotTarget(
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/Breakaway.asset",
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/Breakaway.prefab",
                "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/BreakawaySpawnSelect.prefab",
                "BreakawaySpawnSelect",
                new[] { "Part 8.002", "Part 8.005" }),
        };

        [MenuItem("Tools/Modding/Rebuild SpawnSelect Prefabs (16 & 3937)")]
        private static void RunMenu() => Run();

        public static string InspectSpawnPrefabs()
        {
            var sb = new StringBuilder();
            foreach (var target in Targets)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target.SpawnPrefabPath);
                sb.AppendLine($"=== {prefab.name} ===");
                sb.AppendLine($"Root: tag={prefab.tag} layer={prefab.layer} ({LayerMask.LayerToName(prefab.layer)})");
                var tagged = prefab.GetComponentsInChildren<Transform>(true)
                    .Where(t => !t.CompareTag("Untagged"))
                    .Select(t => $"{t.name} (tag={t.tag}, layer={t.gameObject.layer}, hasCol={t.GetComponent<Collider>() != null})");
                sb.AppendLine("Tagged objects: " + string.Join("; ", tagged));
                var cols = prefab.GetComponentsInChildren<Collider>(true);
                var nonConvex = cols.OfType<MeshCollider>().Where(mc => !mc.convex).Select(mc => mc.name).ToArray();
                sb.AppendLine($"Colliders: total={cols.Length}, nonConvexMesh={nonConvex.Length} ({string.Join(", ", nonConvex)})");
            }
            return sb.ToString();
        }

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (var target in Targets)
            {
                ConfigureSourceBumpers(target);
                sb.AppendLine(RebuildTarget(target));
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var result = sb.ToString().TrimEnd();
            Debug.Log("[RebuildSpawnSelectPrefabs]\n" + result);
            return result;
        }

        private static void ConfigureSourceBumpers(RobotTarget target)
        {
            var blueMat = AssetDatabase.LoadAssetAtPath<Material>(BlueBumperMatPath);
            var redMat = AssetDatabase.LoadAssetAtPath<Material>(RedBumperMatPath);
            if (blueMat == null || redMat == null)
                throw new FileNotFoundException($"Missing MoSim bumper materials ({BlueBumperMatPath} / {RedBumperMatPath}).");

            var root = PrefabUtility.LoadPrefabContents(target.SourcePrefabPath);
            try
            {
                var nameSet = new HashSet<string>(target.BumperMeshNames, StringComparer.Ordinal);
                var matched = root.GetComponentsInChildren<MeshRenderer>(true)
                    .Where(mr => mr != null && nameSet.Contains(mr.gameObject.name))
                    .ToArray();

                if (matched.Length == 0)
                    throw new InvalidOperationException($"No bumper meshes found on {target.SourcePrefabPath}");

                foreach (var mr in matched)
                {
                    var mats = mr.sharedMaterials;
                    if (mats.Length > 0)
                    {
                        mats[0] = blueMat;
                        mr.sharedMaterials = mats;
                    }
                }

                var robotBase = root.GetComponent<RobotBase>();
                if (robotBase != null)
                {
                    var so = new SerializedObject(robotBase);
                    var bumpersProp = so.FindProperty("bumpers");
                    if (bumpersProp != null)
                    {
                        var meshesProp = bumpersProp.FindPropertyRelative("bumperMeshes");
                        meshesProp.arraySize = matched.Length;
                        for (int i = 0; i < matched.Length; i++)
                        {
                            meshesProp.GetArrayElementAtIndex(i).objectReferenceValue = matched[i];
                        }

                        bumpersProp.FindPropertyRelative("materialIndex").intValue = 0;
                        bumpersProp.FindPropertyRelative("blueBumperMaterial").objectReferenceValue = blueMat;
                        bumpersProp.FindPropertyRelative("redBumperMaterial").objectReferenceValue = redMat;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, target.SourcePrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string RebuildTarget(RobotTarget target)
        {
            if (!File.Exists(target.SourcePrefabPath))
                throw new FileNotFoundException("Missing source prefab: " + target.SourcePrefabPath);

            GameObject root = PrefabUtility.LoadPrefabContents(target.SourcePrefabPath);
            try
            {
                root.name = target.SpawnName;

                // Capture bumper settings and stow intake before stripping RobotBase/GenericJoint.
                MeshRenderer[] bumperMeshes = Array.Empty<MeshRenderer>();
                int materialIndex = 0;
                string intakeInfo = "intake=none";
                var robotBase = root.GetComponent<RobotBase>();
                if (robotBase != null)
                {
                    var so = new SerializedObject(robotBase);
                    var bumpersProp = so.FindProperty("bumpers");
                    if (bumpersProp != null)
                    {
                        var meshesProp = bumpersProp.FindPropertyRelative("bumperMeshes");
                        if (meshesProp != null && meshesProp.arraySize > 0)
                        {
                            bumperMeshes = new MeshRenderer[meshesProp.arraySize];
                            for (int i = 0; i < meshesProp.arraySize; i++)
                            {
                                bumperMeshes[i] = meshesProp.GetArrayElementAtIndex(i).objectReferenceValue as MeshRenderer;
                            }
                        }

                        var matIndexProp = bumpersProp.FindPropertyRelative("materialIndex");
                        if (matIndexProp != null) materialIndex = matIndexProp.intValue;
                    }

                    var intakeJointProp = so.FindProperty("intakeJoint");
                    var stowedAngleProp = so.FindProperty("intakeStowedAngle");
                    if (intakeJointProp != null && stowedAngleProp != null &&
                        intakeJointProp.objectReferenceValue is GenericJoint intakeJoint)
                    {
                        var jointSo = new SerializedObject(intakeJoint);
                        bool flip = jointSo.FindProperty("flipDirection")?.boolValue ?? false;
                        float stowedAngle = stowedAngleProp.floatValue;
                        float localAngle = flip ? -stowedAngle : stowedAngle;

                        var beforeBounds = ComputeRendererBounds(intakeJoint.gameObject);
                        intakeJoint.transform.localRotation *= Quaternion.AngleAxis(localAngle, Vector3.right);
                        var afterBounds = ComputeRendererBounds(intakeJoint.gameObject);
                        intakeInfo = $"intake={intakeJoint.name} angle={localAngle:F1} maxZ:{beforeBounds.max.z:F2}->{afterBounds.max.z:F2} maxY:{beforeBounds.max.y:F2}->{afterBounds.max.y:F2}";
                    }
                }

                StripHierarchyToVisualsAndColliders(root);

                // Drop any trigger colliders so only solid chassis/wheel colliders remain,
                // and ensure any MeshColliders are convex for the dynamic Rigidbody.
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null) continue;
                    if (col.isTrigger)
                    {
                        UnityEngine.Object.DestroyImmediate(col);
                    }
                    else if (col is MeshCollider mc && !mc.convex)
                    {
                        mc.convex = true;
                    }
                }

                // Match working Rebuilt SpawnSelect prefabs (RatchetRockersSpawn, CCSpawn, CitrusCircuits/Spawn, 2910B_Spawn):
                // root tag "BlueRobot", all objects on the "Robot" layer (layer 6), and bumper colliders
                // tagged "Bumpers" so the starting-line trigger detects the robot and allows saving.
                root.tag = "BlueRobot";
                int robotLayer = LayerMask.NameToLayer("Robot");
                if (robotLayer >= 0)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        t.gameObject.layer = robotLayer;
                    }
                }

                var bumperNameSet = new HashSet<string>(target.BumperMeshNames, StringComparer.Ordinal);
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr == null || !bumperNameSet.Contains(mr.gameObject.name)) continue;
                    mr.gameObject.tag = "Bumpers";
                    if (mr.GetComponent<Collider>() == null)
                    {
                        mr.gameObject.AddComponent<BoxCollider>();
                    }
                }

                // Ensure root Rigidbody matches BaseRobot_Spawn defaults.
                var rb = root.GetComponent<Rigidbody>();
                if (rb == null) rb = root.AddComponent<Rigidbody>();
                rb.mass = 98f;
                rb.drag = 4f;
                rb.angularDrag = 5f;
                rb.useGravity = true;
                rb.isKinematic = false;
                rb.constraints = RigidbodyConstraints.None;

                // Ensure root SpawnpointSelectionSettings is present and populated.
                var settings = root.GetComponent<SpawnpointSelectionSettings>();
                if (settings == null) settings = root.AddComponent<SpawnpointSelectionSettings>();
                settings.bumperMeshes = bumperMeshes;
                settings.materialIndex = materialIndex;

                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, target.SpawnPrefabPath);
                if (savedPrefab == null)
                    throw new InvalidOperationException("Failed to save prefab: " + target.SpawnPrefabPath);

                // Re-wire RobotMetadataSO.mainRobot.spawnSelectionPrefab in case root fileID changed.
                var metadata = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(target.MetadataPath);
                if (metadata != null)
                {
                    var metaSo = new SerializedObject(metadata);
                    metaSo.FindProperty("mainRobot.spawnSelectionPrefab").objectReferenceValue = savedPrefab;
                    metaSo.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(metadata);
                }

                int renderers = savedPrefab.GetComponentsInChildren<MeshRenderer>(true).Length;
                int colliders = savedPrefab.GetComponentsInChildren<Collider>(true).Length;
                return $"{target.SpawnName}: saved {target.SpawnPrefabPath} (renderers={renderers}, colliders={colliders}, layer={savedPrefab.layer}, tag={savedPrefab.tag}, bumperMeshes={bumperMeshes.Length}, {intakeInfo})";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void StripHierarchyToVisualsAndColliders(GameObject root)
        {
            // Peel components across the whole hierarchy in dependency order (dependents first)
            // so joints/rigidbodies/behaviours with RequireComponent never log removal errors.
            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            var remaining = new List<Component>();
            foreach (var t in allTransforms)
            {
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null || IsKeptComponent(c)) continue;
                    remaining.Add(c);
                }
            }

            // Remove MonoBehaviours and Joints first, then Rigidbodies and everything else.
            for (int pass = 0; pass < 8 && remaining.Count > 0; pass++)
            {
                bool removedAny = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var c = remaining[i];
                    if (c == null)
                    {
                        remaining.RemoveAt(i);
                        continue;
                    }

                    if (IsRequiredByAnyOther(c, c.gameObject)) continue;
                    if (c is Rigidbody && HasJointReferencing(root, c as Rigidbody)) continue;

                    UnityEngine.Object.DestroyImmediate(c);
                    remaining.RemoveAt(i);
                    removedAny = true;
                }

                if (!removedAny) break;
            }
        }

        private static bool IsKeptComponent(Component c)
        {
            if (c is Transform || c is MeshFilter || c is Renderer || c is LODGroup) return true;
            if (c is WheelCollider) return false;
            return c is Collider || c is Collider2D;
        }

        private static bool HasJointReferencing(GameObject root, Rigidbody rb)
        {
            if (rb == null) return false;
            foreach (var j in root.GetComponentsInChildren<Joint>(true))
            {
                if (j != null && (j.connectedBody == rb || j.gameObject == rb.gameObject))
                    return true;
            }
            return false;
        }

        private static Bounds ComputeRendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static bool IsRequiredByAnyOther(Component component, GameObject owner)
        {
            var type = component.GetType();
            foreach (var other in owner.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (var req in other.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>())
                {
                    if ((req.m_Type0 != null && req.m_Type0.IsAssignableFrom(type)) ||
                        (req.m_Type1 != null && req.m_Type1.IsAssignableFrom(type)) ||
                        (req.m_Type2 != null && req.m_Type2.IsAssignableFrom(type)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
