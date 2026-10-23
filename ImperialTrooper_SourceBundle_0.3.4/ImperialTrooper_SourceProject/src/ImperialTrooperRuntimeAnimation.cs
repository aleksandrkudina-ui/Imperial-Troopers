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
            internal Transform Target;
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
            visualRoot = root;
            set = animationSet;
            if (set == null) return;

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
            int bound = 0;
            foreach (ImperialTrooperAnimationSet.Clip clip in new[] { idle, walk, jog, death, aim, fire })
            {
                if (clip == null) continue;
                foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
                {
                    if (track == null || track.Target != null) continue;
                    if (byName.TryGetValue(track.NodeName, out Transform target))
                    {
                        track.Target = target;
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
            // Default to weapon-ready stance instead of a potentially T-pose-like civilian idle.
            current = aim ?? idle ?? walk ?? jog;
            currentStartTime = Time.time;
            ImperialTrooperPlugin.LogSource?.LogInfo("Retargeted Stormtrooper skeletal clips loaded: " + set.ClipCount +
                " clips; idle=" + (idle != null) + ", walk=" + (walk != null) + ", jog=" + (jog != null) +
                ", aim=" + (aim != null) + ", fire=" + (fire != null) + ", death=" + (death != null) +
                "; bound animation tracks=" + bound + ", tracks bound to actual SkinnedMeshRenderer bones=" + actualSkinBoneBindings +
                ", probe bones left arm=" + (leftArmProbe != null ? leftArmProbe.name : "<missing>") +
                ", right arm=" + (rightArmProbe != null ? rightArmProbe.name : "<missing>") + ".");
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
            if (body == null) body = GetComponentInParent<CharacterBody>();
        }

        private void LateUpdate()
        {
            if (visualRoot == null || set == null) return;
            if (body == null) body = GetComponentInParent<CharacterBody>();

            bool deadNow = body != null && body.healthComponent != null && !body.healthComponent.alive;
            if (deadNow)
            {
                if (!deathStarted)
                {
                    deathStarted = true;
                    action = null;
                    current = death;
                    currentStartTime = Time.time;
                }
                if (death != null) ApplyClip(death, Time.time - currentStartTime, false);
                return;
            }

            float speed = body != null && body.characterMotor != null ? body.characterMotor.velocity.magnitude : 0f;
            if (action != null && Time.time < actionEndTime)
            {
                ApplyClip(action, Time.time - actionStartTime, false);
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
            if (!loggedFirstPose && current != null)
            {
                loggedFirstPose = true;
                ImperialTrooperPlugin.LogSource?.LogInfo("First skeletal pose applied: clip=" + current.Name +
                    ", active=" + gameObject.activeInHierarchy +
                    ", leftArmEuler=" + (leftArmProbe != null ? leftArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                    ", rightArmEuler=" + (rightArmProbe != null ? rightArmProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") +
                    ", leftLegEuler=" + (leftLegProbe != null ? leftLegProbe.localRotation.eulerAngles.ToString("F1") : "<missing>") + ".");
            }
        }

        private void ApplyUpperBodyClip(ImperialTrooperAnimationSet.Clip clip, float elapsed)
        {
            if (clip == null || clip.Tracks == null) return;
            float t = clip.Loop ? Mathf.Repeat(elapsed, clip.Duration) : Mathf.Clamp(elapsed, 0f, clip.Duration);
            foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
            {
                if (track == null || track.Target == null || track.Keys == null || track.Keys.Length == 0) continue;
                string name = track.NodeName.ToLowerInvariant();
                bool upperBody = name.EndsWith("leftarm") || name.EndsWith("rightarm") ||
                    name.EndsWith("leftforearm") || name.EndsWith("rightforearm") ||
                    name.EndsWith("lefthand") || name.EndsWith("righthand") ||
                    name.EndsWith("leftshoulder") || name.EndsWith("rightshoulder") ||
                    name.EndsWith("spine") || name.EndsWith("spine1") || name.EndsWith("spine2") ||
                    name.EndsWith("neck") || name.EndsWith("head");
                if (upperBody) ApplyTrack(track, t);
            }
        }

        private void ApplyClip(ImperialTrooperAnimationSet.Clip clip, float elapsed, bool loop)
        {
            if (clip == null || clip.Tracks == null) return;
            float t = loop ? Mathf.Repeat(elapsed, clip.Duration) : Mathf.Clamp(elapsed, 0f, clip.Duration);
            foreach (ImperialTrooperAnimationSet.Track track in clip.Tracks)
                ApplyTrack(track, t);
        }

        private static void ApplyTrack(ImperialTrooperAnimationSet.Track track, float t)
        {
            if (track == null || track.Target == null || track.Keys == null || track.Keys.Length == 0) return;
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
            track.Target.localPosition = Vector3.LerpUnclamped(a.Position, b.Position, blend);
            track.Target.localRotation = Quaternion.SlerpUnclamped(a.Rotation, b.Rotation, blend);
            track.Target.localScale = Vector3.LerpUnclamped(a.Scale, b.Scale, blend);
        }
    }
}
