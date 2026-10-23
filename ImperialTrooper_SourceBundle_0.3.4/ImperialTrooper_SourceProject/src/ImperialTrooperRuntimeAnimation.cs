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
        private bool deathStarted;
        private bool loggedFirstPose;
        private Transform leftArmProbe;
        private Transform rightArmProbe;
        private Transform leftLegProbe;
        private int actualSkinBoneBindings;
        internal int BoundTrackCount { get; private set; }

        internal void Configure(Transform root, ImperialTrooperAnimationSet animationSet)
        {
            if (root == null || animationSet == null) return;
            if (configured && visualRoot == root && set == animationSet &&
                targetBindings != null && targetBindings.Count > 0)
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

        private void EnsureInstanceBinding()
        {
            // Rebind if this is a spawned clone; the prefab's Transform/dictionary references
            // are not a valid substitute for a fresh per-instance binding.
            if (configured && visualRoot == transform && set != null && targetBindings != null && targetBindings.Count > 0)
            {
                CharacterBody parentBody = GetComponentInParent<CharacterBody>();
                if (parentBody != null) body = parentBody;
                return;
            }

            ImperialTrooperAnimationSet animationSet = ImperialTrooperPlugin.RuntimeTrooperAnimationSet;
            if (animationSet != null)
                Configure(transform, animationSet);
        }

        internal void SetCombat(bool active)
        {
            inCombat = active;
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

        private void LateUpdate()
        {
            // These components are attached while constructing the prefab, then instantiated by
            // RoR2. Private runtime references are not valid instance bindings after cloning.
            EnsureInstanceBinding();
            if (!configured || visualRoot == null || set == null || targetBindings == null || targetBindings.Count == 0)
                return;

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
                    // RoR2 can instantiate the prefab before its HealthComponent has started.
                    // Do not mistake that startup interval for a death and freeze the death state.
                    if (!loggedInitialDeadState && healthKnown)
                    {
                        loggedInitialDeadState = true;
                        ImperialTrooperPlugin.LogSource?.LogInfo("Stormtrooper instance " + GetInstanceID() +
                            " is waiting for initial health startup; keeping weapon-ready pose.");
                    }
                    ImperialTrooperAnimationSet.Clip spawnPose = aim ?? idle ?? walk ?? jog;
                    if (spawnPose != null) ApplyClip(spawnPose, 0f, false);
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

            float speed = body != null && body.characterMotor != null ? body.characterMotor.velocity.magnitude : 0f;
            if (action != null && Time.time < actionEndTime)
            {
                ApplyClip(action, Time.time - actionStartTime, false);
                LogPoseApplied(action.Name);
                return;
            }
            action = null;

            ImperialTrooperAnimationSet.Clip next;
            if (speed > 4.0f && jog != null) next = jog;
            else if (speed > 0.35f && walk != null) next = walk;
            else if (inCombat && aim != null) next = aim;
            else next = aim ?? idle ?? walk ?? jog ?? death;

            if (next == null) return;
            if (current != next)
            {
                current = next;
                currentStartTime = Time.time;
            }
            ApplyClip(current, Time.time - currentStartTime, current.Loop);
            if ((current == walk || current == jog) && inCombat && aim != null)
                ApplyUpperBodyClip(aim, Time.time - currentStartTime);
            LogPoseApplied(current.Name);
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
