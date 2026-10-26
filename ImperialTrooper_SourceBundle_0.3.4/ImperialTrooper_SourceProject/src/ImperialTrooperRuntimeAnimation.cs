using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using RoR2;

namespace ImperialTrooperMod
{
    internal sealed class ImperialTrooperAnimationSet
    {
        internal sealed class Key
        {
            internal float Time;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Scale;
        }

        internal sealed class Track
        {
            internal string NodeName;
            internal Key[] Keys;
        }

        internal sealed class Clip
        {
            internal string Name;
            internal float Duration;
            internal Track[] Tracks;
            internal bool Loop => Name != "death" && Name != "fire";
        }

        private readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
        internal int ClipCount => clips.Count;

        internal Clip Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            clips.TryGetValue(name, out Clip clip);
            return clip;
        }

        internal static ImperialTrooperAnimationSet Load(string path)
        {
            if (!File.Exists(path)) return null;
            var set = new ImperialTrooperAnimationSet();
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != (byte)'I' || magic[1] != (byte)'T' ||
                    magic[2] != (byte)'A' || magic[3] != (byte)'N')
                    throw new InvalidDataException("Invalid Stormtrooper animation file header.");
                int version = reader.ReadInt32();
                if (version != 1) throw new InvalidDataException("Unsupported Stormtrooper animation file version " + version + ".");
                int clipCount = ReadCount(reader, "clip", 16);
                for (int c = 0; c < clipCount; c++)
                {
                    string clipName = ReadString(reader, "clip name");
                    float duration = reader.ReadSingle();
                    int trackCount = ReadCount(reader, "track", 256);
                    if (string.IsNullOrWhiteSpace(clipName) || duration <= 0f || duration > 120f)
                        throw new InvalidDataException("Invalid animation clip metadata for '" + clipName + "'.");

                    var tracks = new Track[trackCount];
                    for (int i = 0; i < trackCount; i++)
                    {
                        string nodeName = ReadString(reader, "bone name");
                        int keyCount = ReadCount(reader, "key", 2000);
                        if (keyCount < 2) throw new InvalidDataException("Animation track '" + nodeName + "' has fewer than two keys.");
                        var keys = new Key[keyCount];
                        for (int k = 0; k < keyCount; k++)
                        {
                            var key = new Key
                            {
                                Time = reader.ReadSingle(),
                                Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                                Scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                            };
                            float qLength = Mathf.Sqrt(key.Rotation.x * key.Rotation.x + key.Rotation.y * key.Rotation.y +
                                key.Rotation.z * key.Rotation.z + key.Rotation.w * key.Rotation.w);
                            if (qLength > 0.0001f)
                                key.Rotation = new Quaternion(key.Rotation.x / qLength, key.Rotation.y / qLength,
                                    key.Rotation.z / qLength, key.Rotation.w / qLength);
                            else
                                key.Rotation = Quaternion.identity;
                            if (key.Time < 0f || key.Time > duration + 0.05f)
                                throw new InvalidDataException("Animation key is outside its clip duration.");
                            keys[k] = key;
                        }
                        tracks[i] = new Track { NodeName = nodeName, Keys = keys };
                    }
                    set.clips[clipName] = new Clip { Name = clipName, Duration = duration, Tracks = tracks };
                }
                if (stream.Position != stream.Length)
                    ImperialTrooperPlugin.LogSource?.LogWarning("Stormtrooper animation file has " + (stream.Length - stream.Position) + " trailing bytes.");
            }
            return set;
        }

        private static int ReadCount(BinaryReader reader, string label, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum) throw new InvalidDataException("Invalid animation " + label + " count " + value + ".");
            return value;
        }

        private static string ReadString(BinaryReader reader, string label)
        {
            int length = ReadCount(reader, label + " byte", 4096);
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException("Unexpected end of animation file while reading " + label + ".");
            return Encoding.UTF8.GetString(data);
        }
    }

    [DefaultExecutionOrder(20000)]
    public sealed class ImperialTrooperAnimationPlayer : MonoBehaviour
    {
        private CharacterBody body;
        private Transform visualRoot;
        private ImperialTrooperAnimationSet set;
        // Animation data is shared and immutable. Transform targets must be bound per spawned
        // enemy instance, otherwise prefab clones either inherit no runtime state or animate a
        // different trooper's bones.
        private Dictionary<ImperialTrooperAnimationSet.Track, Transform> targetBindings =
            new Dictionary<ImperialTrooperAnimationSet.Track, Transform>();
        private bool configured;
        private bool loggedUpdateEntry;
        private bool observedAlive;
        private bool loggedInitialDeadState;
        private bool loggedDeathClip;
        private ImperialTrooperAnimationSet.Clip idle;
        private ImperialTrooperAnimationSet.Clip walk;
        private ImperialTrooperAnimationSet.Clip jog;
        private ImperialTrooperAnimationSet.Clip death;
        private ImperialTrooperAnimationSet.Clip aim;
        private ImperialTrooperAnimationSet.Clip fire;
        private ImperialTrooperAnimationSet.Clip current;
        private float currentStartTime;
        private ImperialTrooperAnimationSet.Clip action;
        private float actionStartTime;
        private float actionEndTime;
        private bool inCombat;
        private bool movementCommand;
        private float lastMovementSignalTime = -100f;
        private Vector3 aimDirection;
        private Transform rightHandBone;
        private Transform leftUpperArmBone;
        private Transform leftLowerArmBone;
        private Transform leftHandBone;
        private Transform weaponTransform;
        private string lastLoggedAnimationState;
        private bool deathStarted;
        private bool loggedFirstPose;
        private Transform leftArmProbe;
        private Transform rightArmProbe;
        private Transform leftLegProbe;
        private int actualSkinBoneBindings;
        private int lastPumpedFrame = -1;
        internal int BoundTrackCount { get; private set; }

        internal void Configure(Transform root, ImperialTrooperAnimationSet animationSet, bool forceRebind = false)
        {
            if (root == null || animationSet == null) return;
            if (!forceRebind && configured && visualRoot == root && set == animationSet && BindingsBelongToRoot(root))
            {
                CharacterBody parentBody = GetComponentInParent<CharacterBody>();
                if (parentBody != null) body = parentBody;
                return;
            }

            visualRoot = root;
            set = animationSet;
            if (targetBindings == null)
                targetBindings = new Dictionary<ImperialTrooperAnimationSet.Track, Transform>();
            targetBindings.Clear();

            idle = set.Find("idle");
            walk = set.Find("walk");
            jog = set.Find("jog");
            death = set.Find("death");
            aim = set.Find("aim");
            fire = set.Find("fire");

            var transforms = root.GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in transforms)
                if (t != null && !byName.ContainsKey(t.name)) byName.Add(t.name, t);

            var skinBones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.bones == null) continue;
                foreach (Transform bone in renderer.bones)
                    if (bone != null) skinBones.Add(bone);
            }

            actualSkinBoneBindings = 0;
            leftArmProbe = null;
            rightArmProbe = null;
            leftLegProbe = null;
            rightHandBone = FindNamedTransform(transforms, "RightHand");
            leftUpperArmBone = FindNamedTransform(transforms, "LeftArm", "LeftUpperArm");
            leftLowerArmBone = FindNamedTransform(transforms, "LeftForeArm", "LeftLowerArm");
            leftHandBone = FindNamedTransform(transforms, "LeftHand");
            weaponTransform = transforms.FirstOrDefault(t => t != null && t.name == "ImperialTrooperE11Blaster");
            aimDirection = root.forward * -1f;
            int bound = 0;
            foreach (ImperialTrooperAnimationSet.Clip clip in new[] { idle, walk, jog, death, aim, fire })
            {
                if (clip == null) continue;
                foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
                {
                    if (track == null) continue;
                    if (byName.TryGetValue(track.NodeName, out Transform target))
                    {
                        targetBindings[track] = target;
                        bound++;
                        if (skinBones.Contains(target)) actualSkinBoneBindings++;
                        string boneName = track.NodeName.ToLowerInvariant();
                        if (leftArmProbe == null && (boneName.EndsWith("leftarm") || boneName.EndsWith("leftupperarm"))) leftArmProbe = target;
                        if (rightArmProbe == null && (boneName.EndsWith("rightarm") || boneName.EndsWith("rightupperarm"))) rightArmProbe = target;
                        if (leftLegProbe == null && (boneName.EndsWith("leftupleg") || boneName.EndsWith("leftupperleg") || boneName.EndsWith("leftthigh"))) leftLegProbe = target;
                    }
                }
            }

            BoundTrackCount = bound;
            body = GetComponentInParent<CharacterBody>();
            current = aim ?? idle ?? walk ?? jog;
            currentStartTime = Time.time;
            action = null;
            deathStarted = false;
            observedAlive = false;
            loggedInitialDeadState = false;
            loggedDeathClip = false;
            configured = true;

            if (current != null)
            {
                ApplyClip(current, 0f, current.Loop);
                ApplyWeaponPose();
                ImperialTrooperPlugin.LogSource?.LogInfo("Initial authored Stormtrooper pose applied to instance " +
                    GetInstanceID() + ": clip=" + current.Name +
                    ", rootActive=" + gameObject.activeInHierarchy +
                    ", parentBody=" + (body != null ? body.name : "<none>") +
                    ", leftArmEuler=" + (leftArmProbe != null ? leftArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                    ", rightArmEuler=" + (rightArmProbe != null ? rightArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                    ", leftLegEuler=" + (leftLegProbe != null ? leftLegProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") + ".");
            }

            ImperialTrooperPlugin.LogSource?.LogInfo("Bound Stormtrooper animation instance " + GetInstanceID() +
                ": clips=" + set.ClipCount + ", tracks=" + bound +
                ", toActualSkinBones=" + actualSkinBoneBindings +
                ", idle=" + (idle != null) + ", walk=" + (walk != null) + ", jog=" + (jog != null) +
                ", aim=" + (aim != null) + ", fire=" + (fire != null) + ", death=" + (death != null) +
                ", instanceRoot=" + root.name + ", rootActive=" + root.gameObject.activeInHierarchy + ".");
        }

        private static string NormalizeBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string withoutPrefix = name.ToLowerInvariant().Replace("mixamorig", string.Empty);
            return new string(withoutPrefix.Where(char.IsLetterOrDigit).ToArray());
        }

        private static Transform FindNamedTransform(Transform[] transforms, params string[] aliases)
        {
            if (transforms == null || aliases == null) return null;
            string[] normalizedAliases = aliases.Select(NormalizeBoneName).ToArray();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null) continue;
                string name = NormalizeBoneName(candidate.name);
                if (normalizedAliases.Any(alias => name == alias || name.EndsWith(alias, StringComparison.Ordinal)))
                    return candidate;
            }
            return null;
        }

        private bool BindingsBelongToRoot(Transform root)
        {
            if (root == null || targetBindings == null || targetBindings.Count == 0) return false;
            foreach (KeyValuePair<ImperialTrooperAnimationSet.Track, Transform> binding in targetBindings)
            {
                Transform target = binding.Value;
                if (target == null || (target != root && !target.IsChildOf(root)))
                    return false;
            }
            return true;
        }

        private void EnsureInstanceBinding()
        {
            CharacterBody parentBody = GetComponentInParent<CharacterBody>();
            if (parentBody != null) body = parentBody;

            // A cloned MonoBehaviour may keep managed references to the prefab's Transform objects.
            // A valid binding must prove every target bone belongs to THIS spawned visual root.
            if (configured && visualRoot == transform && set != null && BindingsBelongToRoot(transform))
                return;

            ImperialTrooperAnimationSet animationSet = ImperialTrooperPlugin.RuntimeTrooperAnimationSet;
            if (animationSet != null)
                Configure(transform, animationSet, true);
        }

        internal void SetCombat(bool active)
        {
            inCombat = active;
        }

        internal void SetMovement(bool moving)
        {
            movementCommand = moving;
            if (moving) lastMovementSignalTime = Time.time;
        }

        internal void SetAimDirection(Vector3 direction)
        {
            if (direction.sqrMagnitude > 0.001f)
                aimDirection = direction.normalized;
        }

        internal void PlayShot()
        {
            if (fire == null || deathStarted) return;
            action = fire;
            actionStartTime = Time.time;
            actionEndTime = Time.time + Mathf.Max(0.1f, fire.Duration);
        }

        private void Awake()
        {
            body = GetComponentInParent<CharacterBody>();
            EnsureInstanceBinding();
        }

        private void OnEnable()
        {
            EnsureInstanceBinding();
        }

        internal void PrepareFromPlugin()
        {
            if (!enabled) enabled = true;
            EnsureInstanceBinding();
        }

        private void LateUpdate()
        {
            // Apply after RoR2 has finished updating the live character/model for this frame.
            // The central plugin Update only prepares bindings; it must never write skeletal poses.
            PrepareFromPlugin();
            if (lastPumpedFrame == Time.frameCount) return;
            if (!configured || visualRoot == null || set == null ||
                targetBindings == null || targetBindings.Count == 0)
                return;

            lastPumpedFrame = Time.frameCount;
            TickAnimation();
        }

        private void TickAnimation()
        {
            if (body == null) body = GetComponentInParent<CharacterBody>();
            if (!loggedUpdateEntry)
            {
                loggedUpdateEntry = true;
                bool aliveAtFirstTick = body != null && body.healthComponent != null && body.healthComponent.alive;
                ImperialTrooperPlugin.LogSource?.LogInfo("Stormtrooper animation tick active: instance=" + GetInstanceID() +
                    ", rootActive=" + gameObject.activeInHierarchy + ", boundTracks=" + targetBindings.Count +
                    ", parentBody=" + (body != null ? body.name : "<none>") + ", aliveAtFirstTick=" + aliveAtFirstTick + ".");
            }

            bool healthKnown = body != null && body.healthComponent != null;
            bool aliveNow = healthKnown && body.healthComponent.alive;
            if (!observedAlive)
            {
                if (!healthKnown || !aliveNow)
                {
                    if (!loggedInitialDeadState && healthKnown)
                    {
                        loggedInitialDeadState = true;
                        ImperialTrooperPlugin.LogSource?.LogInfo("Stormtrooper instance " + GetInstanceID() +
                            " is waiting for initial health startup; keeping weapon-ready pose.");
                    }
                    ImperialTrooperAnimationSet.Clip spawnPose = aim ?? idle ?? walk ?? jog;
                    if (spawnPose != null) ApplyClip(spawnPose, 0f, false);
                    ApplyWeaponPose();
                    return;
                }

                observedAlive = true;
                ImperialTrooperPlugin.LogSource?.LogInfo("Stormtrooper instance " + GetInstanceID() + " entered alive state; animation loop started.");
            }

            if (healthKnown && !aliveNow)
            {
                if (!deathStarted)
                {
                    deathStarted = true;
                    action = null;
                    current = death;
                    currentStartTime = Time.time;
                    if (!loggedDeathClip)
                    {
                        loggedDeathClip = true;
                        ImperialTrooperPlugin.LogSource?.LogInfo("Stormtrooper instance " + GetInstanceID() +
                            " entered death clip; clipAvailable=" + (death != null) +
                            ", bindingCount=" + targetBindings.Count + ".");
                    }
                }
                if (death != null) ApplyClip(death, Time.time - currentStartTime, false);
                LogPoseApplied("death");
                return;
            }

            Vector3 planarVelocity = body != null && body.characterMotor != null
                ? body.characterMotor.velocity : Vector3.zero;
            planarVelocity.y = 0f;
            float speed = planarVelocity.magnitude;
            bool movementSignal = movementCommand || speed > 0.65f;
            if (movementSignal) lastMovementSignalTime = Time.time;
            // Motor velocity and AI distance drivers are not perfectly in phase. Keep the gait active
            // briefly after an input transition so the animation never re-selects idle/walk every frame.
            bool moving = movementSignal || (Time.time - lastMovementSignalTime) < 0.28f;
            bool firingAction = action != null && Time.time < actionEndTime;
            if (!firingAction) action = null;

            ImperialTrooperAnimationSet.Clip next;
            if (moving && speed > 4.6f && jog != null) next = jog;
            else if (moving && walk != null) next = walk;
            else if (inCombat && aim != null) next = aim;
            else next = idle ?? aim ?? walk ?? jog ?? death;

            if (next == null) return;
            if (current != next)
            {
                current = next;
                currentStartTime = Time.time;
                if (lastLoggedAnimationState != next.Name)
                {
                    lastLoggedAnimationState = next.Name;
                    ImperialTrooperPlugin.LogSource?.LogInfo("Trooper animation state -> " + next.Name +
                        " (instance=" + GetInstanceID() + ", planarSpeed=" + speed.ToString("0.00") +
                        ", movementCommand=" + movementCommand + ").");
                }
            }

            ApplyClip(current, Time.time - currentStartTime, current.Loop);
            if (moving && inCombat && aim != null)
                ApplyUpperBodyClip(aim, Time.time - currentStartTime);
            if (firingAction && action != null)
                ApplyUpperBodyClip(action, Time.time - actionStartTime);

            // The authored clips are generic pistol motions. Re-aim the rifle wrist and solve the
            // left-arm reach to the foregrip after clip playback so the weapon remains two-handed
            // and is actually aligned with the target.
            ApplyWeaponPose();

        }

        private void LogPoseApplied(string clipName)
        {
            if (loggedFirstPose) return;
            loggedFirstPose = true;
            ImperialTrooperPlugin.LogSource?.LogInfo("First skeletal pose applied at runtime: instance=" + GetInstanceID() +
                ", clip=" + clipName + ", active=" + gameObject.activeInHierarchy +
                ", boundTracks=" + (targetBindings != null ? targetBindings.Count : 0) +
                ", leftArmEuler=" + (leftArmProbe != null ? leftArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                ", rightArmEuler=" + (rightArmProbe != null ? rightArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                ", leftLegEuler=" + (leftLegProbe != null ? leftLegProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") + ".");
        }

        private void ApplyWeaponPose()
        {
            if (visualRoot == null || weaponTransform == null || rightHandBone == null) return;

            Vector3 direction = aimDirection.sqrMagnitude > 0.001f ? aimDirection.normalized : -visualRoot.forward;
            // The modeled E-11 barrel points along local -Z. Reset its grip transform every frame:
            // animation data can reposition the hand bone, but must not preserve an old converted
            // world-space offset on the child rifle.
            weaponTransform.localPosition = Vector3.zero;
            weaponTransform.localRotation = Quaternion.identity;
            Quaternion desiredWeaponRotation = Quaternion.LookRotation(-direction, visualRoot.up);
            rightHandBone.rotation = desiredWeaponRotation;

            if (leftUpperArmBone == null || leftLowerArmBone == null || leftHandBone == null) return;
            Vector3 foregrip = weaponTransform.TransformPoint(new Vector3(0f, -0.030f, -0.235f));
            ApplyTwoBoneGripIk(leftUpperArmBone, leftLowerArmBone, leftHandBone, foregrip, -visualRoot.right);
            leftHandBone.rotation = weaponTransform.rotation * Quaternion.Euler(8f, 0f, 5f);
        }

        private static void ApplyTwoBoneGripIk(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
        {
            if (upper == null || lower == null || hand == null) return;
            Vector3 root = upper.position;
            float upperLength = Vector3.Distance(root, lower.position);
            float lowerLength = Vector3.Distance(lower.position, hand.position);
            Vector3 toTarget = target - root;
            float distance = toTarget.magnitude;
            if (upperLength < 0.01f || lowerLength < 0.01f || distance < 0.01f) return;

            float minDistance = Mathf.Abs(upperLength - lowerLength) + 0.01f;
            float maxDistance = Mathf.Max(minDistance + 0.01f, upperLength + lowerLength - 0.01f);
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
            Vector3 axis = toTarget.normalized;
            Vector3 bend = Vector3.ProjectOnPlane(pole, axis);
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.ProjectOnPlane(upper.right, axis);
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.Cross(axis, Vector3.up);
            bend.Normalize();

            float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) /
                          (2f * distance);
            float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            Vector3 elbowTarget = root + axis * along + bend * height;

            Quaternion upperDelta = Quaternion.FromToRotation(lower.position - upper.position, elbowTarget - upper.position);
            upper.rotation = upperDelta * upper.rotation;
            Quaternion lowerDelta = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position);
            lower.rotation = lowerDelta * lower.rotation;
        }

        private void ApplyUpperBodyClip(ImperialTrooperAnimationSet.Clip clip, float elapsed)
        {
            if (clip == null || clip.Tracks == null) return;
            float t = clip.Loop ? Mathf.Repeat(elapsed, clip.Duration) : Mathf.Clamp(elapsed, 0f, clip.Duration);
            foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
            {
                if (track == null || !targetBindings.TryGetValue(track, out Transform target) || target == null ||
                    track.Keys == null || track.Keys.Length == 0) continue;
                string name = track.NodeName.ToLowerInvariant();
                bool upperBody = name.EndsWith("leftarm") || name.EndsWith("rightarm") ||
                    name.EndsWith("leftforearm") || name.EndsWith("rightforearm") ||
                    name.EndsWith("lefthand") || name.EndsWith("righthand") ||
                    name.EndsWith("leftshoulder") || name.EndsWith("rightshoulder") ||
                    name.EndsWith("spine") || name.EndsWith("spine1") || name.EndsWith("spine2") ||
                    name.EndsWith("neck") || name.EndsWith("head");
                if (upperBody) ApplyTrack(track, target, t);
            }
        }

        private void ApplyClip(ImperialTrooperAnimationSet.Clip clip, float elapsed, bool loop)
        {
            if (clip == null || clip.Tracks == null) return;
            float t = loop ? Mathf.Repeat(elapsed, clip.Duration) : Mathf.Clamp(elapsed, 0f, clip.Duration);
            foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
            {
                if (track == null || targetBindings == null || !targetBindings.TryGetValue(track, out Transform target)) continue;
                ApplyTrack(track, target, t);
            }
        }

        private static void ApplyTrack(ImperialTrooperAnimationSet.Track track, Transform target, float t)
        {
            if (track == null || target == null || track.Keys == null || track.Keys.Length == 0) return;
            ImperialTrooperAnimationSet.Key a = track.Keys[0];
            ImperialTrooperAnimationSet.Key b = track.Keys[track.Keys.Length - 1];
            for (int i = 0; i < track.Keys.Length - 1; i++)
            {
                if (track.Keys[i].Time <= t && track.Keys[i + 1].Time >= t)
                {
                    a = track.Keys[i];
                    b = track.Keys[i + 1];
                    break;
                }
            }
            float span = b.Time - a.Time;
            float blend = span > 0.0001f ? Mathf.Clamp01((t - a.Time) / span) : 0f;
            target.localPosition = Vector3.LerpUnclamped(a.Position, b.Position, blend);
            target.localRotation = Quaternion.SlerpUnclamped(a.Rotation, b.Rotation, blend);
            target.localScale = Vector3.LerpUnclamped(a.Scale, b.Scale, blend);
        }
    }
}
