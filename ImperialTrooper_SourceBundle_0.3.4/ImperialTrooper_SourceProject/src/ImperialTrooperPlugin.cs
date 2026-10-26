using BepInEx;
using BepInEx.Logging;
using R2API;
using RoR2;
using RoR2.Navigation;
using RoR2.Projectile;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace ImperialTrooperMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(DirectorAPI.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(PrefabAPI.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.bepis.r2api.content_management", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class ImperialTrooperPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.pyramidhead.imperialtrooper";
        public const string PluginName = "Imperial Troopers";
        public const string PluginVersion = "0.3.20";

        internal static ImperialTrooperPlugin Instance;
        internal static GameObject RedTracerPrefab;
        internal static GameObject BlasterBoltPrefab;
        internal static Material BlasterBoltMaterial;
        internal const float BlasterBoltSpeed = 24f;
        internal static GameObject[] AudioEffectPrefabs = Array.Empty<GameObject>();
        internal static GameObject TrooperMasterPrefab;
        private static AssetBundle ImportedTrooperAssetBundle;
        private static GameObject ImportedTrooperModelPrefab;
        private static ImperialTrooperRuntimeModelData RuntimeTrooperModelData;
        internal static ImperialTrooperAnimationSet RuntimeTrooperAnimationSet;
        internal static AudioClip BlasterShotClip;
        internal static AudioClip RadioContactClip;
        internal static AudioClip RadioTargetAcquiredClip;
        internal static AudioClip RadioMaintainFormationClip;
        internal static AudioClip RadioTrooperDownClip;
        internal static AudioClip RadioStaticClip;
        internal static AudioClip CommsBeepClip;
        internal static AudioClip BlasterImpactClip;
        internal static ManualLogSource LogSource;
        private static bool initialized;
        private readonly HashSet<int> animationPumpWarnings = new HashSet<int>();
        private int lastAnimationPumpDiagnostic = -1;

        private void Awake()
        {
            Instance = this;
            LogSource = Logger;
            Logger.LogInfo($"{PluginName} v{PluginVersion}: initializing.");
            // Keep a server-side fallback in case another mod or a changed stage DCCS
            // prevents DirectorAPI's normal monster card pool from selecting our trooper.
            Stage.onServerStageBegin += OnServerStageBegin;
            TryLoadImportedTrooperModel();
            StartCoroutine(LoadAudioAssets());
            try
            {
                InitializeTrooper();
            }
            catch (Exception ex)
            {
                Logger.LogError("Imperial Trooper initialization failed: " + ex);
            }
        }

        private void Update()
        {
            // Drive model clips centrally from the always-live plugin object. RoR2 clones and
            // replaces body/model prefab hierarchies during spawn; a MonoBehaviour placed only
            // on the prefab child may retain prefab Transform references or miss runtime ticks.
            ImperialTrooperAnimationSet animationSet = RuntimeTrooperAnimationSet;
            if (animationSet == null || !initialized) return;

            var bodies = CharacterBody.readOnlyInstancesList;
            int foundBodies = 0;
            int tickedPlayers = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                CharacterBody body = bodies[i];
                if (body == null || body.GetComponent<ImperialTrooperBrain>() == null) continue;
                foundBodies++;

                ModelLocator locator = body.GetComponent<ModelLocator>();
                Transform modelRoot = locator != null ? locator.modelTransform : null;
                if (modelRoot == null)
                {
                    CharacterModel model = body.GetComponentInChildren<CharacterModel>(true);
                    if (model != null) modelRoot = model.transform;
                }
                if (modelRoot == null) continue;

                ImperialTrooperAnimationPlayer player = modelRoot.GetComponentInChildren<ImperialTrooperAnimationPlayer>(true);
                if (player == null)
                {
                    // Recover if RoR2's model setup replaced the serialized child component while
                    // keeping the rigged visual. Don't silently leave a valid skinned mesh frozen.
                    Transform importedRoot = null;
                    foreach (Transform child in modelRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (child != null && string.Equals(child.name, "ImperialTrooperImportedVisuals", StringComparison.Ordinal))
                        {
                            importedRoot = child;
                            break;
                        }
                    }

                    if (importedRoot != null)
                    {
                        player = importedRoot.GetComponent<ImperialTrooperAnimationPlayer>();
                        if (player == null) player = importedRoot.gameObject.AddComponent<ImperialTrooperAnimationPlayer>();
                        player.Configure(importedRoot, animationSet, true);
                        Logger.LogWarning("Animation pump restored ImperialTrooperAnimationPlayer on spawned model instance " +
                            body.GetInstanceID() + " after it was missing from the cloned hierarchy.");
                    }
                }

                if (player == null)
                {
                    if (animationPumpWarnings.Add(body.GetInstanceID()))
                        Logger.LogWarning("Animation pump could not find a rigged visual or animation player for trooper body " +
                            body.name + " (instance " + body.GetInstanceID() + ").");
                    continue;
                }

                if (!player.enabled) player.enabled = true;
                player.PrepareFromPlugin();
                tickedPlayers++;
            }

            if (foundBodies > 0 && lastAnimationPumpDiagnostic != Time.frameCount)
            {
                lastAnimationPumpDiagnostic = Time.frameCount;
                // Log only once per newly observed body set, not every frame.
                if (tickedPlayers > 0 && !animationPumpWarnings.Contains(-1))
                {
                    animationPumpWarnings.Add(-1);
                    Logger.LogInfo("Central animation preflight active: trooperBodies=" + foundBodies +
                        ", animationPlayersPrepared=" + tickedPlayers + "; skeletal poses are applied in late frame after model updates.");
                }
            }
        }

        private void OnServerStageBegin(Stage stage)
        {
            if (NetworkServer.active)
                StartCoroutine(EnsureTrooperAppearsOnStage());
        }

        private IEnumerator EnsureTrooperAppearsOnStage()
        {
            // Give normal Director spawns time to run first; only use the fallback if no
            // living Imperial Trooper was actually created on this stage.
            yield return new WaitForSeconds(20f);
            if (!NetworkServer.active || !initialized || TrooperMasterPrefab == null)
                yield break;

            bool alreadyPresent = CharacterBody.readOnlyInstancesList.Any(b =>
                b != null && b.name.IndexOf("ImperialTrooperBody", StringComparison.OrdinalIgnoreCase) >= 0 &&
                b.healthComponent != null && b.healthComponent.alive);
            if (alreadyPresent)
            {
                Logger.LogInfo("Stage spawn check: an Imperial Trooper is already alive; fallback not needed.");
                yield break;
            }

            CharacterBody playerBody = CharacterBody.readOnlyInstancesList.FirstOrDefault(b =>
                b != null && b.isPlayerControlled && b.healthComponent != null && b.healthComponent.alive);
            if (playerBody == null)
            {
                Logger.LogWarning("Stage spawn fallback skipped: no living player body found.");
                yield break;
            }

            Vector3 forward = playerBody.transform.forward;
            Vector3 spawnPosition = playerBody.transform.position + forward * 24f;
            Vector3 rayOrigin = spawnPosition + Vector3.up * 35f;
            RaycastHit hit;
            if (Physics.Raycast(rayOrigin, Vector3.down, out hit, 100f))
                spawnPosition = hit.point + Vector3.up * 1.25f;
            else
                spawnPosition = playerBody.transform.position + forward * 12f;

            try
            {
                CharacterMaster summoned = new MasterSummon
                {
                    masterPrefab = TrooperMasterPrefab,
                    position = spawnPosition,
                    rotation = Quaternion.LookRotation(-forward, Vector3.up),
                    summonerBodyObject = playerBody.gameObject,
                    teamIndexOverride = TeamIndex.Monster
                }.Perform();

                if (summoned != null)
                    Logger.LogWarning("Stage spawn fallback: Director did not create a living Imperial Trooper within 20 seconds; spawned one manually.");
                else
                    Logger.LogError("Stage spawn fallback failed: MasterSummon returned null.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Stage spawn fallback failed: " + ex);
            }
        }

        [ConCommand(commandName = "imperialtrooper_spawn", flags = ConVarFlags.ExecuteOnServer, helpText = "Spawns an Imperial Trooper a short distance in front of the invoking player.")]
        private static void CCSpawnImperialTrooper(ConCommandArgs args)
        {
            if (!NetworkServer.active || TrooperMasterPrefab == null)
            {
                LogSource?.LogWarning("Trooper spawn command called before server setup or before the master prefab was registered.");
                return;
            }

            CharacterBody playerBody = args.senderBody;
            if (playerBody == null)
                playerBody = CharacterBody.readOnlyInstancesList.FirstOrDefault(b => b != null && b.isPlayerControlled && b.healthComponent != null && b.healthComponent.alive);
            if (playerBody == null)
            {
                LogSource?.LogWarning("No living player body found for imperialtrooper_spawn.");
                return;
            }

            Vector3 forward = playerBody.transform.forward;
            new MasterSummon
            {
                masterPrefab = TrooperMasterPrefab,
                position = playerBody.transform.position + forward * 10f,
                rotation = Quaternion.LookRotation(-forward, Vector3.up),
                summonerBodyObject = playerBody.gameObject,
                teamIndexOverride = TeamIndex.Monster
            }.Perform();
        }

        private string ResolveAudioAssetPath(string fileName)
        {
            string pluginDirectory = IOPath.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            bool isRadioAsset = fileName.StartsWith("Radio_", StringComparison.OrdinalIgnoreCase) ||
                                fileName.Equals("Comms_Beep.wav", StringComparison.OrdinalIgnoreCase) ||
                                fileName.Equals("Blaster_Burst.wav", StringComparison.OrdinalIgnoreCase);

            // Radio/dialogue is intentionally user-overridable. The package does not redistribute
            // Lucasfilm dialogue; never play the old synthetic mechanical placeholders as voice lines.
            if (isRadioAsset)
            {
                string overridePath = IOPath.Combine(pluginDirectory, "Audio", "Overrides", fileName);
                if (File.Exists(overridePath))
                {
                    Logger.LogInfo("Using local radio audio override: " + fileName);
                    return overridePath;
                }

                string flattenedOverridePath = IOPath.Combine(pluginDirectory, "Overrides", fileName);
                if (File.Exists(flattenedOverridePath))
                {
                    Logger.LogInfo("Using flattened local radio audio override: " + fileName);
                    return flattenedOverridePath;
                }

                Logger.LogInfo("No local radio override supplied for " + fileName + "; this cue remains silent.");
                return null;
            }

            string nestedPath = IOPath.Combine(pluginDirectory, "Audio", fileName);
            if (File.Exists(nestedPath)) return nestedPath;

            // Thunderstore's local-mod importer flattens some archives. Search both supported layouts.
            string rootPath = IOPath.Combine(pluginDirectory, fileName);
            if (File.Exists(rootPath))
            {
                Logger.LogInfo("Found audio asset in plugin root: " + fileName);
                return rootPath;
            }

            Logger.LogWarning("Audio asset missing from both supported locations: " + nestedPath + " and " + rootPath);
            return nestedPath;
        }

        private IEnumerator LoadAudioAssets()
        {
            string pluginDirectory = IOPath.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            Logger.LogInfo("Loading trooper audio from plugin directory: " + pluginDirectory);
            yield return LoadAudio(ResolveAudioAssetPath("Blaster_Shot.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Blaster_Impact.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Radio_ContactFront.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Radio_TargetAcquired.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Radio_MaintainFormation.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Radio_TrooperDown.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Radio_Static.wav"));
            yield return LoadAudio(ResolveAudioAssetPath("Comms_Beep.wav"));
            int loaded = new AudioClip[] { BlasterShotClip, BlasterImpactClip, RadioContactClip, RadioTargetAcquiredClip,
                RadioMaintainFormationClip, RadioTrooperDownClip, RadioStaticClip, CommsBeepClip }.Count(c => c != null);
            Logger.LogInfo("Trooper audio initialization finished: " + loaded + "/8 non-empty clips loaded. Shot=" + (BlasterShotClip != null) +
                ", Impact=" + (BlasterImpactClip != null) + ", Contact voice override=" + (RadioContactClip != null) +
                ", target voice override=" + (RadioTargetAcquiredClip != null) + ", formation voice override=" + (RadioMaintainFormationClip != null) +
                ", trooper-down voice override=" + (RadioTrooperDownClip != null) + ".");
        }

        private IEnumerator LoadAudio(string path)
        {
            if (string.IsNullOrEmpty(path)) yield break;
            if (!File.Exists(path))
            {
                Logger.LogWarning("Optional audio file was not found: " + path);
                yield break;
            }

            string uri;
            try { uri = new Uri(IOPath.GetFullPath(path)).AbsoluteUri; }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not create audio file URI for " + path + ": " + ex.Message);
                yield break;
            }

            AudioClip loadedClip = null;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                request.timeout = 20;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Logger.LogWarning("Unity WAV decoder failed for " + IOPath.GetFileName(path) + " (" + uri + "): " + request.error);
                    yield break;
                }

                loadedClip = DownloadHandlerAudioClip.GetContent(request);
            }

            // DownloadHandlerAudioClip owns WAV parsing; don't treat a non-null but empty clip as loaded.
            if (loadedClip == null || loadedClip.samples <= 0 || loadedClip.length <= 0.01f)
            {
                int samples = loadedClip != null ? loadedClip.samples : 0;
                float length = loadedClip != null ? loadedClip.length : 0f;
                int frequency = loadedClip != null ? loadedClip.frequency : 0;
                int channels = loadedClip != null ? loadedClip.channels : 0;
                Logger.LogWarning("Unity WAV decoder returned an empty clip for " + IOPath.GetFileName(path) +
                    ": length=" + length.ToString("0.000") + " s, samples=" + samples +
                    ", channels=" + channels + ", frequency=" + frequency + " Hz.");
                if (loadedClip != null) UnityEngine.Object.Destroy(loadedClip);
                yield break;
            }

            string file = IOPath.GetFileName(path);
            loadedClip.name = IOPath.GetFileNameWithoutExtension(file);
            if (file.Equals("Blaster_Shot.wav", StringComparison.OrdinalIgnoreCase)) BlasterShotClip = loadedClip;
            else if (file.Equals("Radio_ContactFront.wav", StringComparison.OrdinalIgnoreCase)) RadioContactClip = loadedClip;
            else if (file.Equals("Radio_TargetAcquired.wav", StringComparison.OrdinalIgnoreCase)) RadioTargetAcquiredClip = loadedClip;
            else if (file.Equals("Radio_MaintainFormation.wav", StringComparison.OrdinalIgnoreCase)) RadioMaintainFormationClip = loadedClip;
            else if (file.Equals("Radio_TrooperDown.wav", StringComparison.OrdinalIgnoreCase)) RadioTrooperDownClip = loadedClip;
            else if (file.Equals("Radio_Static.wav", StringComparison.OrdinalIgnoreCase)) RadioStaticClip = loadedClip;
            else if (file.Equals("Comms_Beep.wav", StringComparison.OrdinalIgnoreCase)) CommsBeepClip = loadedClip;
            else if (file.Equals("Blaster_Impact.wav", StringComparison.OrdinalIgnoreCase)) BlasterImpactClip = loadedClip;

            Logger.LogInfo("Loaded decoded WAV: " + file + " (" + loadedClip.length.ToString("0.00") +
                " sec, " + loadedClip.samples + " frames/channel, " + loadedClip.channels +
                " channels, " + loadedClip.frequency + " Hz).");
        }

        internal static void PlayClip(AudioClip clip, Vector3 position, float volume = 0.7f)
        {
            if (clip == null) return;
            int kind = GetAudioKind(clip);
            bool networkEffectSpawned = false;
            if (NetworkServer.active && kind >= 0 && AudioEffectPrefabs != null &&
                kind < AudioEffectPrefabs.Length && AudioEffectPrefabs[kind] != null)
            {
                try
                {
                    EffectManager.SpawnEffect(AudioEffectPrefabs[kind], new EffectData { origin = position, scale = 1f }, true);
                    networkEffectSpawned = true;
                }
                catch (Exception ex) { LogSource?.LogWarning("Networked audio effect failed; using local playback: " + ex.Message); }
            }
            if (NetworkServer.active || !networkEffectSpawned)
                PlayLocalClip(clip, position, volume, IsRadioClipKind(kind));
        }

        internal static bool IsRadioClipKind(int kind)
        {
            return (kind >= 1 && kind <= 5) || kind == 7;
        }

        internal static void PlayLocalClip(AudioClip clip, Vector3 position, float volume, bool radio)
        {
            if (clip == null) return;
            GameObject audioObject = new GameObject("ImperialTrooperAudioPlayback_" + clip.name);
            audioObject.transform.position = position;
            AudioSource source = audioObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = false;
            source.volume = Mathf.Clamp01(radio ? volume : Mathf.Max(volume, 0.86f));
            source.spatialBlend = radio ? 0.10f : 0.88f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = radio ? 6f : 4f;
            source.maxDistance = radio ? 55f : 85f;
            source.dopplerLevel = 0f;
            source.spread = radio ? 12f : 0f;
            source.Play();
            UnityEngine.Object.Destroy(audioObject, Mathf.Clamp(clip.length + 0.25f, 0.5f, 20f));
        }

        internal static AudioClip GetAudioClip(int kind)
        {
            switch (kind)
            {
                case 0: return BlasterShotClip;
                case 1: return RadioContactClip;
                case 2: return RadioTargetAcquiredClip;
                case 3: return RadioMaintainFormationClip;
                case 4: return RadioTrooperDownClip;
                case 5: return CommsBeepClip;
                case 6: return BlasterImpactClip;
                case 7: return RadioStaticClip;
                default: return null;
            }
        }

        internal static float GetAudioVolume(int kind)
        {
            switch (kind) { case 0: return .72f; case 1: case 2: case 3: return .94f; case 4: return .90f; case 5: return .28f; case 6: return .55f; case 7: return .25f; default: return .35f; }
        }

        private static int GetAudioKind(AudioClip clip)
        {
            if (clip == BlasterShotClip) return 0;
            if (clip == RadioContactClip) return 1;
            if (clip == RadioTargetAcquiredClip) return 2;
            if (clip == RadioMaintainFormationClip) return 3;
            if (clip == RadioTrooperDownClip) return 4;
            if (clip == CommsBeepClip) return 5;
            if (clip == BlasterImpactClip) return 6;
            if (clip == RadioStaticClip) return 7;
            return -1;
        }

        private void RegisterNetworkedAudioEffects()
        {
            AudioEffectPrefabs = new GameObject[8];
            string[] names = { "BlasterShot", "RadioContact", "RadioTarget", "RadioFormation", "TrooperDown", "CommsBeep", "BlasterImpact", "RadioStatic" };
            for (int i = 0; i < AudioEffectPrefabs.Length; i++)
            {
                GameObject prefab = new GameObject("ImperialTrooperAudio_" + names[i]);
                prefab.SetActive(false);
                EffectComponent effect = prefab.AddComponent<EffectComponent>();
                effect.positionAtReferencedTransform = false;
                effect.parentToReferencedTransform = false;
                VFXAttributes vfx = prefab.AddComponent<VFXAttributes>();
                vfx.DoNotPool = true;
                prefab.AddComponent<NetworkIdentity>();
                ImperialTrooperAudioEffect audioEffect = prefab.AddComponent<ImperialTrooperAudioEffect>();
                audioEffect.clipKind = i;
                ContentAddition.AddEffect(prefab);
                AudioEffectPrefabs[i] = prefab;
            }
            Logger.LogInfo("Registered 8 networked Imperial Trooper audio effects.");
        }

        internal static void PlayRadioChatter(Vector3 position, bool firstContact)
        {
            AudioClip clip;
            if (firstContact) clip = RadioContactClip;
            else if (UnityEngine.Random.value < 0.5f) clip = RadioTargetAcquiredClip ?? RadioContactClip;
            else clip = RadioMaintainFormationClip ?? RadioContactClip;
            if (clip != null) PlayClip(clip, position, 0.94f);
            if (firstContact && CommsBeepClip != null) PlayClip(CommsBeepClip, position, 0.28f);
        }

        private Material FindProjectileMaterial(GameObject source)
        {
            if (source == null) return null;
            ProjectileController sourceController = source.GetComponent<ProjectileController>();
            if (sourceController != null && sourceController.ghostPrefab != null)
            {
                Renderer ghostRenderer = sourceController.ghostPrefab.GetComponentInChildren<Renderer>(true);
                if (ghostRenderer != null && ghostRenderer.sharedMaterial != null) return ghostRenderer.sharedMaterial;
            }
            Renderer renderer = source.GetComponentInChildren<Renderer>(true);
            return renderer != null ? renderer.sharedMaterial : null;
        }

        private GameObject CreateRedBlasterGhost(GameObject sourceGhost, Material template)
        {
            if (sourceGhost == null)
                throw new InvalidOperationException("The selected vanilla projectile has no ghost prefab to clone.");
            if (template == null)
                throw new InvalidOperationException("The vanilla projectile supplied no material to build a custom red bolt.");

            // Clone a valid RoR2 ghost prefab (rather than leaving a new GameObject alive at world origin),
            // then replace only its visible effect with a slim red capsule and a straight trail.
            GameObject ghost = PrefabAPI.InstantiateClone(sourceGhost, "ImperialTrooperRedBoltGhost", false);
            ghost.transform.localScale = Vector3.one;
            foreach (Renderer renderer in ghost.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;
            foreach (ParticleSystem particles in ghost.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles == null) continue;
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                particles.Clear(true);
                var emission = particles.emission;
                emission.enabled = false;
                particles.enabled = false;
            }
            // The source FMJ ghost contains its own tinted glow/trail. Stopping particles alone is
            // insufficient: some inherited emitters restart when RoR2 enables a cloned ghost and
            // can leave a blue light trail while our red core is hidden by the material shader.
            foreach (Light inheritedLight in ghost.GetComponentsInChildren<Light>(true))
                if (inheritedLight != null) inheritedLight.enabled = false;
            foreach (TrailRenderer oldTrail in ghost.GetComponentsInChildren<TrailRenderer>(true))
            {
                if (oldTrail == null) continue;
                oldTrail.Clear();
                oldTrail.enabled = false;
            }
            foreach (LineRenderer oldLine in ghost.GetComponentsInChildren<LineRenderer>(true))
                if (oldLine != null) oldLine.enabled = false;
            foreach (Animator oldAnimator in ghost.GetComponentsInChildren<Animator>(true))
                if (oldAnimator != null) oldAnimator.enabled = false;
            foreach (MonoBehaviour behaviour in ghost.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                string typeName = behaviour.GetType().Name;
                if (typeName.IndexOf("ProjectileGhostController", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("ProjectileGhostComponent", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (typeName.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Wobble", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Orbit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Steer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Homing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("TargetFinder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Oscillat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Behaviour motion = behaviour as Behaviour;
                    if (motion != null) motion.enabled = false;
                }
            }

            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            core.name = "RedBlasterBoltCore";
            Collider collider = core.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            core.transform.SetParent(ghost.transform, false);
            core.transform.localPosition = Vector3.zero;
            core.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            core.transform.localScale = new Vector3(0.045f, 0.17f, 0.045f);

            Material boltMaterial = new Material(template) { name = "ImperialTrooperRedBoltMaterial" };
            SetTint(boltMaterial, new Color(1f, 0.018f, 0.012f, 1f));
            if (boltMaterial.HasProperty("_EmissionColor"))
            {
                boltMaterial.EnableKeyword("_EMISSION");
                boltMaterial.SetColor("_EmissionColor", new Color(1f, 0.006f, 0.002f, 1f) * 4.0f);
            }
            Renderer coreRenderer = core.GetComponent<Renderer>();
            if (coreRenderer != null)
            {
                coreRenderer.sharedMaterial = boltMaterial;
                coreRenderer.shadowCastingMode = ShadowCastingMode.Off;
                coreRenderer.receiveShadows = false;
            }

            TrailRenderer trail = ghost.GetComponent<TrailRenderer>();
            if (trail == null) trail = ghost.AddComponent<TrailRenderer>();
            trail.enabled = true;
            trail.Clear();
            trail.material = boltMaterial;
            trail.time = 0.13f;
            trail.startWidth = 0.045f;
            trail.endWidth = 0.002f;
            trail.minVertexDistance = 0.025f;
            trail.numCornerVertices = 2;
            trail.numCapVertices = 2;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.alignment = LineAlignment.View;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] {
                    new GradientColorKey(new Color(1f, 0.015f, 0.008f), 0f),
                    new GradientColorKey(new Color(1f, 0.17f, 0.035f), 1f)
                },
                new[] {
                    new GradientAlphaKey(0.92f, 0f),
                    new GradientAlphaKey(0.52f, 0.65f),
                    new GradientAlphaKey(0f, 1f)
                });
            trail.colorGradient = gradient;
            return ghost;
        }

        private GameObject ResolveProjectileGhost(ProjectileController controller)
        {
            if (controller == null) return null;
            if (controller.ghostPrefab != null) return controller.ghostPrefab;

            // RoR2 1.5 moved vanilla projectile ghosts to ProjectileController.ghostPrefabAddress.
            // Calling AssetReference.LoadAssetAsync is the supported path; reading RuntimeKey via
            // reflection and then passing it to a second Addressables request can return the wrong asset.
            try
            {
                if (controller.ghostPrefabAddress != null)
                    return controller.ghostPrefabAddress.LoadAssetAsync().WaitForCompletion();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not load FMJ ghostPrefabAddress: " + ex.Message);
            }
            return null;
        }

        private void ClearProjectileGhostReferences(ProjectileController controller)
        {
            if (controller == null) return;
            controller.ghostPrefab = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type type = controller.GetType();
            while (type != null)
            {
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (!string.Equals(field.Name, "ghostPrefabAddress", StringComparison.OrdinalIgnoreCase) || field.FieldType.IsValueType) continue;
                    try { field.SetValue(controller, null); }
                    catch (Exception ex) { Logger.LogDebug("Could not clear ghost address field: " + ex.Message); }
                }
                type = type.BaseType;
            }
        }

        private void TryRegisterBlasterBolt()
        {
            try
            {
                // Clone the vanilla straight-moving Commando FMJ projectile so the game keeps its
                // normal projectile collision, hitbox and direct damage path.
                GameObject source = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/Commando/FMJRamping.prefab").WaitForCompletion();
                if (source == null) throw new InvalidOperationException("Addressable FMJRamping projectile prefab returned null.");
                ProjectileController sourceController = source.GetComponent<ProjectileController>();
                GameObject sourceGhost = ResolveProjectileGhost(sourceController);
                if (sourceGhost == null)
                    sourceGhost = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/Commando/CommandoFMJGhost.prefab").WaitForCompletion();
                if (sourceGhost == null)
                    throw new InvalidOperationException("No valid FMJ projectile ghost could be resolved; refusing to use the effect-only red tracer as a projectile ghost.");

                // Do not clone the vanilla ghost's material: its original texture/shader contains a
                // blue/cyan glow that survives simple color tinting on some RoR2 1.5 shader variants.
                // Build a clean red material and use it for the bolt core AND trail.
                Shader boltShader = Shader.Find("Hopoo Games/Deferred/Standard");
                if (boltShader == null) boltShader = Shader.Find("Standard");
                if (boltShader == null) boltShader = Shader.Find("Sprites/Default");
                if (boltShader == null) throw new InvalidOperationException("No supported shader is available for the red blaster bolt.");
                if (BlasterBoltMaterial != null) Destroy(BlasterBoltMaterial);
                BlasterBoltMaterial = new Material(boltShader) { name = "ImperialTrooperRedBoltMaterial" };
                SetTint(BlasterBoltMaterial, new Color(1f, 0.012f, 0.008f, 1f));
                if (BlasterBoltMaterial.HasProperty("_EmissionColor"))
                {
                    BlasterBoltMaterial.EnableKeyword("_EMISSION");
                    BlasterBoltMaterial.SetColor("_EmissionColor", new Color(1f, 0.006f, 0.002f, 1f) * 4f);
                }

                BlasterBoltPrefab = PrefabAPI.InstantiateClone(source, "ImperialTrooperBlasterBolt", true);
                ProjectileController controller = BlasterBoltPrefab.GetComponent<ProjectileController>();
                if (controller == null) throw new InvalidOperationException("FMJ projectile is missing ProjectileController.");
                ProjectileSimple simple = BlasterBoltPrefab.GetComponent<ProjectileSimple>();
                if (simple == null) throw new InvalidOperationException("FMJ projectile is missing ProjectileSimple.");
                simple.desiredForwardSpeed = BlasterBoltSpeed;
                simple.lifetime = 2.5f;
                simple.updateAfterFiring = true;

                ProjectileOverlapAttack overlap = BlasterBoltPrefab.GetComponent<ProjectileOverlapAttack>();
                if (overlap == null) throw new InvalidOperationException("FMJ projectile has no ProjectileOverlapAttack; refusing to register a bolt that cannot damage on contact.");
                overlap.damageCoefficient = 1f;
                overlap.overlapProcCoefficient = 0.35f;

                ProjectileDamage projectileDamage = BlasterBoltPrefab.GetComponent<ProjectileDamage>();
                if (projectileDamage == null) projectileDamage = BlasterBoltPrefab.AddComponent<ProjectileDamage>();
                projectileDamage.damage = 1f;

                ProjectileImpactExplosion explosion = BlasterBoltPrefab.GetComponent<ProjectileImpactExplosion>();
                if (explosion != null) DestroyImmediate(explosion);
                ProjectileDirectionalTargetFinder finder = BlasterBoltPrefab.GetComponent<ProjectileDirectionalTargetFinder>();
                if (finder != null) DestroyImmediate(finder);
                ProjectileSteerTowardTarget steer = BlasterBoltPrefab.GetComponent<ProjectileSteerTowardTarget>();
                if (steer != null) DestroyImmediate(steer);

                Rigidbody rb = BlasterBoltPrefab.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.useGravity = false;
                    rb.drag = 0f;
                    rb.angularDrag = 0f;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                foreach (Renderer renderer in BlasterBoltPrefab.GetComponentsInChildren<Renderer>(true))
                    if (renderer != null) renderer.enabled = false;
                // Strip inherited FMJ projectile visual emitters as well as renderers. The source
                // prefab is only retained for collision/overlap damage and its straight flight logic.
                foreach (ParticleSystem particles in BlasterBoltPrefab.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (particles == null) continue;
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    particles.Clear(true);
                    var emission = particles.emission;
                    emission.enabled = false;
                    particles.enabled = false;
                }
                foreach (TrailRenderer inheritedTrail in BlasterBoltPrefab.GetComponentsInChildren<TrailRenderer>(true))
                {
                    if (inheritedTrail == null) continue;
                    inheritedTrail.Clear();
                    inheritedTrail.enabled = false;
                }
                foreach (LineRenderer inheritedLine in BlasterBoltPrefab.GetComponentsInChildren<LineRenderer>(true))
                    if (inheritedLine != null) inheritedLine.enabled = false;
                foreach (Light inheritedLight in BlasterBoltPrefab.GetComponentsInChildren<Light>(true))
                    if (inheritedLight != null) inheritedLight.enabled = false;

                // Use RoR2's projectile-ghost pipeline. The visual must follow ProjectileGhostController,
                // not be rendered as a capsule on the projectile body (that was the source of the floaty look).
                GameObject customGhost = CreateRedBlasterGhost(sourceGhost, BlasterBoltMaterial);
                ClearProjectileGhostReferences(controller);
                controller.ghostPrefab = customGhost;
                controller.startSound = string.Empty;
                ContentAddition.AddProjectile(BlasterBoltPrefab);

                Logger.LogInfo("Registered FMJRamping-based red E-11 bolt: ghost=ImperialTrooperRedBoltGhost, speed=" +
                    BlasterBoltSpeed + " m/s, lifetime=2.5 s, direct overlap damage preserved, no explosion/wobble/steering.");
            }
            catch (Exception ex)
            {
                BlasterBoltPrefab = null;
                Logger.LogError("FMJ-based E-11 projectile registration failed; hitscan fallback is disabled: " + ex);
            }
        }

        private void AddBoltTrail(GameObject visualRoot)
        {
            if (visualRoot == null || visualRoot.GetComponent<TrailRenderer>() != null) return;
            Shader shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            Material trailMaterial = new Material(shader) { name = "ImperialTrooper_RedBoltTrail" };
            SetTint(trailMaterial, new Color(1f, 0.02f, 0.03f, 1f));
            TrailRenderer trail = visualRoot.AddComponent<TrailRenderer>();
            trail.material = trailMaterial;
            trail.time = 0.18f;
            trail.startWidth = 0.075f;
            trail.endWidth = 0.008f;
            trail.minVertexDistance = 0.015f;
            trail.numCornerVertices = 3;
            trail.numCapVertices = 3;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.alignment = LineAlignment.View;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, .02f, .035f), 0f), new GradientColorKey(new Color(1f, .12f, .04f), 1f) },
                new[] { new GradientAlphaKey(0.98f, 0f), new GradientAlphaKey(0.55f, 0.65f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;
        }

        private void TintBoltVisual(GameObject visualRoot, string materialName)
        {
            if (visualRoot == null) return;
            foreach (Renderer renderer in visualRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.sharedMaterial == null) continue;
                Material tinted = new Material(renderer.sharedMaterial);
                tinted.name = materialName;
                SetTint(tinted, new Color(1f, 0.015f, 0.025f, 1f));
                if (tinted.HasProperty("_EmissionColor"))
                {
                    tinted.EnableKeyword("_EMISSION");
                    tinted.SetColor("_EmissionColor", new Color(1f, 0.015f, 0.025f, 1f) * 3.2f);
                }
                renderer.sharedMaterial = tinted;
            }
        }

        private void SetDeathRewardBackingField(DeathRewards rewards, string fieldName, object value)
        {
            FieldInfo field = typeof(DeathRewards).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                Logger.LogWarning("DeathRewards backing field '" + fieldName + "' was not found. Continuing with vanilla default for this reward.");
                return;
            }
            field.SetValue(rewards, value);
        }

        private void InitializeTrooper()
        {
            if (initialized) return;

            GameObject baseBody = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterBodies/CommandoBody");
            GameObject baseMaster = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/CommandoMonsterMaster");
            if (baseBody == null || baseMaster == null)
            {
                Logger.LogError("Could not load the vanilla Commando body/master prefabs. This build may use changed resource paths; no enemy registered.");
                return;
            }

            GameObject bodyPrefab = PrefabAPI.InstantiateClone(baseBody, "ImperialTrooperBody", true);
            CharacterBody body = bodyPrefab.GetComponent<CharacterBody>();
            if (body == null)
            {
                Logger.LogError("Cloned body has no CharacterBody component.");
                return;
            }

            body.name = "ImperialTrooperBody";
            body.baseNameToken = "Imperial Trooper";
            body.subtitleNameToken = "Imperial Infantry";
            body.bodyColor = new Color(0.87f, 0.89f, 0.84f);
            // Baseline infantry, not elite tanks: fragile enough that a small group is dangerous,
            // but a single trooper should go down in a reasonable number of hits.
            body.baseMaxHealth = 80f;
            body.levelMaxHealth = 20f;
            body.baseDamage = 6.5f;
            body.levelDamage = 1.1f;
            body.baseRegen = 0.15f;
            body.levelRegen = 0.02f;
            DeathRewards deathRewards = bodyPrefab.GetComponent<DeathRewards>();
            if (deathRewards == null) deathRewards = bodyPrefab.AddComponent<DeathRewards>();
            // Avoid compile-time access to DeathRewards reward fields: the runtime game
            // assembly may expose different accessibility than the reference assemblies.
            // Reflection looks up the serialized field in the actual loaded game assembly.
            SetDeathRewardBackingField(deathRewards, "fallbackGold", 5u);
            SetDeathRewardBackingField(deathRewards, "<expReward>k__BackingField", 5u);
            SetDeathRewardBackingField(deathRewards, "<spawnValue>k__BackingField", 1);
            body.baseMoveSpeed = 5.8f;
            body.levelMoveSpeed = 0f;
            body.baseAttackSpeed = 1f;
            body.levelAttackSpeed = 0f;
            body.baseArmor = 0f;
            body.isChampion = false;
            body.hullClassification = HullClassification.Human;
            TeamComponent bodyTeam = bodyPrefab.GetComponent<TeamComponent>();
            if (bodyTeam != null) bodyTeam.teamIndex = TeamIndex.Monster;

            SilenceVanillaSkills(bodyPrefab);
            Transform modelRoot = null;
            ModelLocator modelLocator = bodyPrefab.GetComponent<ModelLocator>();
            if (modelLocator != null) modelRoot = modelLocator.modelTransform;
            if (modelRoot == null)
            {
                CharacterModel cm = bodyPrefab.GetComponentInChildren<CharacterModel>(true);
                if (cm != null) modelRoot = cm.transform;
            }
            if (modelRoot != null)
            {
                // Save vanilla renderers before adding our own. CharacterModel/ModelSkinController can
                // restore the source renderer list later, so hiding them only once is not sufficient.
                Renderer[] vanillaRenderers = modelRoot.GetComponentsInChildren<Renderer>(true);
                GameObject visualRoot = BuildImportedOrProceduralModel(modelRoot);
                OptimizeArmorMeshes(modelRoot.gameObject, vanillaRenderers);
                ImperialTrooperModelGuard modelGuard = modelRoot.gameObject.GetComponent<ImperialTrooperModelGuard>();
                if (modelGuard == null) modelGuard = modelRoot.gameObject.AddComponent<ImperialTrooperModelGuard>();
                modelGuard.Initialize(vanillaRenderers);
                foreach (ModelSkinController skinController in modelRoot.GetComponentsInChildren<ModelSkinController>(true))
                    if (skinController != null) skinController.enabled = false;

                // Keep both RoR2 body and InputBankTest origin references valid. InputBankTest's origin
                // member is not public in every game build, so set it reflectively as well.
                GameObject aimOriginObject = new GameObject("ImperialTrooperAimOrigin");
                aimOriginObject.transform.SetParent(modelRoot, false);
                aimOriginObject.transform.localPosition = new Vector3(0f, 1.58f, 0.02f);
                Transform aimOrigin = aimOriginObject.transform;
                body.aimOriginTransform = aimOrigin;
                Transform muzzle = visualRoot != null
                    ? visualRoot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Muzzle")
                    : null;
                if (muzzle == null)
                    muzzle = bodyPrefab.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Muzzle");
                InputBankTest inputBank = bodyPrefab.GetComponent<InputBankTest>();
                if (inputBank != null)
                {
                    FieldInfo aimField = typeof(InputBankTest).GetField("aimOriginTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (aimField != null) aimField.SetValue(inputBank, aimOrigin);
                    else Logger.LogWarning("InputBankTest aimOriginTransform field was not found; MusicController may still report a missing aim origin.");
                }
                ImperialTrooperBrain brain = bodyPrefab.AddComponent<ImperialTrooperBrain>();
                brain.Configure(muzzle);
                ConfigureCharacterModel(bodyPrefab, modelRoot, visualRoot);
            }
            else
            {
                Logger.LogError("No model root found on cloned Commando body; using vanilla visible body with custom fire AI.");
                bodyPrefab.AddComponent<ImperialTrooperBrain>();
            }

            GameObject masterPrefab = PrefabAPI.InstantiateClone(baseMaster, "ImperialTrooperMaster", true);
            CharacterMaster master = masterPrefab.GetComponent<CharacterMaster>();
            if (master == null)
            {
                Logger.LogError("Cloned Commando master has no CharacterMaster component.");
                return;
            }
            master.name = "ImperialTrooperMaster";
            DisableInheritedAiDrivers(masterPrefab);
            ConfigureTacticalDrivers(masterPrefab);
            master.bodyPrefab = bodyPrefab;
            TrooperMasterPrefab = masterPrefab;

            ContentAddition.AddBody(bodyPrefab);
            ContentAddition.AddMaster(masterPrefab);

            CharacterSpawnCard spawnCard = MakeSpawnCard(masterPrefab);
            DirectorCard directorCard = new DirectorCard
            {
                spawnCard = spawnCard,
                selectionWeight = 5,
                minimumStageCompletions = 0,
                preventOverhead = false,
                spawnDistance = DirectorCore.MonsterSpawnDistance.Standard
            };
            DirectorAPI.Helpers.AddNewMonster(new DirectorAPI.DirectorCardHolder
            {
                Card = directorCard,
                MonsterCategory = DirectorAPI.MonsterCategory.BasicMonsters,
                MonsterCategorySelectionWeight = 1f
            }, false);

            TryRegisterRedTracer();
            TryRegisterBlasterBolt();
            RegisterNetworkedAudioEffects();
            initialized = true;
            Logger.LogInfo("Imperial Trooper body/master and Director card registered (selection weight 5, credit cost 15). Stage fallback enabled.");
        }


        private void TryLoadImportedTrooperModel()
        {
            string pluginDirectory = IOPath.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            string animationPath = IOPath.Combine(pluginDirectory, "Stormtrooper.ianim");
            RuntimeTrooperAnimationSet = null;
            if (File.Exists(animationPath))
            {
                try
                {
                    RuntimeTrooperAnimationSet = ImperialTrooperAnimationSet.Load(animationPath);
                    Logger.LogInfo("Loaded retargeted Stormtrooper skeletal animations (" + RuntimeTrooperAnimationSet.ClipCount + " clips).");
                }
                catch (Exception animationEx)
                {
                    Logger.LogWarning("Could not load Stormtrooper.ianim; using procedural fallback animation: " + animationEx.Message);
                    RuntimeTrooperAnimationSet = null;
                }
            }
            else Logger.LogWarning("Stormtrooper.ianim not found; skeletal clip playback is unavailable and procedural fallback animation will be used.");
            string runtimeModelPath = IOPath.Combine(pluginDirectory, "Stormtrooper.imodel");
            if (File.Exists(runtimeModelPath))
            {
                try
                {
                    RuntimeTrooperModelData = ImperialTrooperRuntimeModelData.Load(runtimeModelPath);
                    if (RuntimeTrooperModelData != null)
                    {
                        Logger.LogInfo("Loaded self-contained rigged Stormtrooper mesh data (" + RuntimeTrooperModelData.VertexCount + " vertices, " + RuntimeTrooperModelData.SkinBoneCount + " skin bones, " + RuntimeTrooperModelData.NodeCount + " rig nodes). No Unity AssetBundle is required.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeTrooperModelData = null;
                    Logger.LogError("Could not load Stormtrooper.imodel; will try an optional AssetBundle and then procedural armor: " + ex);
                }
            }
            else Logger.LogInfo("Stormtrooper.imodel not found; will try an optional AssetBundle and then procedural armor.");

            string bundlePath = IOPath.Combine(pluginDirectory, "imperialtrooperassets");
            if (!File.Exists(bundlePath))
            {
                Logger.LogInfo("Imported Stormtrooper asset bundle not found; procedural armor remains active. Put 'imperialtrooperassets' next to ImperialTrooper.dll to enable the rigged mesh.");
                return;
            }

            try
            {
                ImportedTrooperAssetBundle = AssetBundle.LoadFromFile(bundlePath);
                if (ImportedTrooperAssetBundle == null)
                {
                    Logger.LogError("Could not load imperialtrooperassets AssetBundle; using procedural armor.");
                    return;
                }

                ImportedTrooperModelPrefab = ImportedTrooperAssetBundle.LoadAsset<GameObject>("ImperialTrooperModel");
                if (ImportedTrooperModelPrefab == null)
                    ImportedTrooperModelPrefab = ImportedTrooperAssetBundle.LoadAsset<GameObject>("ImperialTrooperModel.prefab");
                if (ImportedTrooperModelPrefab == null)
                    ImportedTrooperModelPrefab = ImportedTrooperAssetBundle.LoadAsset<GameObject>("Assets/ImperialTrooper/ImperialTrooperModel.prefab");
                if (ImportedTrooperModelPrefab == null)
                {
                    Logger.LogError("AssetBundle loaded but asset 'ImperialTrooperModel' was not found; using procedural armor.");
                    ImportedTrooperAssetBundle.Unload(false);
                    ImportedTrooperAssetBundle = null;
                    return;
                }

                Logger.LogInfo("Loaded imported rigged Stormtrooper model from imperialtrooperassets.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Imported Stormtrooper AssetBundle load failed; using procedural armor: " + ex);
                ImportedTrooperModelPrefab = null;
                if (ImportedTrooperAssetBundle != null) ImportedTrooperAssetBundle.Unload(false);
                ImportedTrooperAssetBundle = null;
            }
        }

        private GameObject BuildImportedOrProceduralModel(Transform modelRoot)
        {
            if (RuntimeTrooperModelData != null)
                return BuildRuntimeStormtrooperModel(modelRoot);

            if (ImportedTrooperModelPrefab == null)
                return BuildArmorModel(modelRoot);

            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;

            GameObject importedRoot = Instantiate(ImportedTrooperModelPrefab, modelRoot, false);
            importedRoot.name = "ImperialTrooperImportedVisuals";
            importedRoot.transform.localPosition = Vector3.zero;
            importedRoot.transform.localRotation = Quaternion.identity;
            // Keep the imported prefab's local scale: the Unity preparation step applies the
            // model's half-scale correction (raw FBX bounds are about 3.7 units tall).

            Animator animator = importedRoot.GetComponentInChildren<Animator>(true);
            Dictionary<HumanBodyBones, Transform> boneMap = ResolveBoneMap(animator, importedRoot.transform);
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = null;
                animator.enabled = false; // the supplied FBX animation is only ~0.033 s; use our own walk/death motion instead
            }

            if (boneMap.Count >= 8)
            {
                ImperialTrooperProceduralAnimator proceduralAnimator = importedRoot.AddComponent<ImperialTrooperProceduralAnimator>();
                proceduralAnimator.Configure(boneMap, importedRoot.transform);
                Logger.LogInfo("Using imported Stormtrooper mesh with procedural Mixamo-bone locomotion (" + boneMap.Count + " mapped bones).");
            }
            else
            {
                Logger.LogWarning("Imported Stormtrooper model loaded but only " + boneMap.Count + " bones were mapped; model will render without articulated gait.");
            }

            Transform existingMuzzle = importedRoot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Muzzle");
            if (existingMuzzle == null && boneMap.TryGetValue(HumanBodyBones.RightHand, out Transform rightHand) && rightHand != null)
            {
                GameObject muzzleObject = new GameObject("Muzzle");
                muzzleObject.transform.SetParent(rightHand, false);
                muzzleObject.transform.localPosition = new Vector3(0f, 0.015f, 0.26f);
                muzzleObject.transform.localRotation = Quaternion.identity;
            }

            return importedRoot;
        }

        private GameObject BuildRuntimeStormtrooperModel(Transform modelRoot)
        {
            // The body prefab is cloned from Commando. Its old Animator must not compete
            // with the independent imported Stormtrooper skeleton.
            int disabledVanillaAnimators = 0;
            foreach (Animator vanillaAnimator in modelRoot.GetComponentsInChildren<Animator>(true))
            {
                if (vanillaAnimator == null) continue;
                vanillaAnimator.applyRootMotion = false;
                vanillaAnimator.runtimeAnimatorController = null;
                if (vanillaAnimator.enabled)
                {
                    vanillaAnimator.enabled = false;
                    disabledVanillaAnimators++;
                }
            }
            Logger.LogInfo("Disabled " + disabledVanillaAnimators + " inherited Commando Animator components before attaching the Stormtrooper rig.");

            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;

            Material ceramic = BuildMaterial("Imperial_RuntimeCeramic", new Color(0.91f, 0.93f, 0.90f), 0.08f, 0.34f, null);
            GameObject importedRoot = RuntimeTrooperModelData.Instantiate(modelRoot, ceramic);
            Dictionary<HumanBodyBones, Transform> boneMap = ResolveBoneMap(null, importedRoot.transform);

            // The FBX contains no texture/materials. Add a small dark visor on the front-facing side.
            Material visorMaterial = BuildMaterial("Imperial_RuntimeVisor", new Color(0.008f, 0.014f, 0.018f), 0.22f, 0.5f, ceramic);
            GameObject visor = CreateRuntimePart(importedRoot.transform, "ImperialTrooperVisor", PrimitiveType.Cube,
                new Vector3(0f, 3.14f, -0.335f), new Vector3(0.41f, 0.075f, 0.028f), visorMaterial, Quaternion.identity);
            if (boneMap.TryGetValue(HumanBodyBones.Head, out Transform head) && head != null)
                visor.transform.SetParent(head, true);

            // The mesh asset is a bare armored body, so build a recognizable E-11-style rifle:
            // tubular barrel/flash hider, ribbed Sterling-style handguard, stock, pistol grip, power cell and scope.
            // The imported character faces -Z. Gun geometry extends along local -Z, so align its +Z
            // axis to the character's +Z; do not guess a 90-degree wrist rotation.
            if (boneMap.TryGetValue(HumanBodyBones.RightHand, out Transform rightHand) && rightHand != null)
            {
                Material gunDark = BuildMaterial("Imperial_RuntimeBlasterDark", new Color(0.018f, 0.024f, 0.028f), 0.26f, 0.34f, ceramic);
                Material gunMetal = BuildMaterial("Imperial_RuntimeBlasterMetal", new Color(0.12f, 0.14f, 0.15f), 0.62f, 0.43f, ceramic);
                Material gunRubber = BuildMaterial("Imperial_RuntimeBlasterRubber", new Color(0.035f, 0.040f, 0.042f), 0.05f, 0.22f, ceramic);
                GameObject gun = new GameObject("ImperialTrooperE11Blaster");
                // Keep the prop directly attached to the actual right-hand bone with a stable local
                // grip origin. Previously its world-space placement was converted to a hand-local
                // offset before animation started; the retargeted clips then moved the hand and left
                // the rifle floating inside/away from the palm.
                gun.transform.SetParent(rightHand, false);
                gun.transform.localScale = Vector3.one * 1.7f;
                gun.transform.localPosition = Vector3.zero;
                gun.transform.localRotation = Quaternion.identity;

                CreateRuntimePart(gun.transform, "E11_Receiver", PrimitiveType.Cube,
                    new Vector3(0f, 0f, -0.105f), new Vector3(0.115f, 0.085f, 0.265f), gunDark, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_Handguard", PrimitiveType.Cube,
                    new Vector3(0f, -0.002f, -0.265f), new Vector3(0.095f, 0.068f, 0.235f), gunRubber, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_Barrel", PrimitiveType.Cylinder,
                    new Vector3(0f, -0.002f, -0.405f), new Vector3(0.025f, 0.105f, 0.025f), gunMetal, Quaternion.Euler(-90f, 0f, 0f));
                CreateRuntimePart(gun.transform, "E11_FlashHider", PrimitiveType.Cylinder,
                    new Vector3(0f, -0.002f, -0.505f), new Vector3(0.036f, 0.023f, 0.036f), gunDark, Quaternion.Euler(-90f, 0f, 0f));
                CreateRuntimePart(gun.transform, "E11_Stock", PrimitiveType.Cube,
                    new Vector3(0f, 0.006f, 0.145f), new Vector3(0.105f, 0.070f, 0.155f), gunRubber, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_PistolGrip", PrimitiveType.Cube,
                    new Vector3(0f, -0.078f, 0.017f), new Vector3(0.060f, 0.112f, 0.070f), gunRubber, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_PowerCell", PrimitiveType.Cube,
                    new Vector3(0.068f, -0.025f, -0.145f), new Vector3(0.040f, 0.080f, 0.122f), gunMetal, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_TopRail", PrimitiveType.Cube,
                    new Vector3(0f, 0.050f, -0.135f), new Vector3(0.040f, 0.018f, 0.265f), gunMetal, Quaternion.identity);
                CreateRuntimePart(gun.transform, "E11_ScopeTube", PrimitiveType.Cylinder,
                    new Vector3(0f, 0.092f, -0.145f), new Vector3(0.031f, 0.058f, 0.031f), gunDark, Quaternion.Euler(-90f, 0f, 0f));
                CreateRuntimePart(gun.transform, "E11_ScopeCap", PrimitiveType.Cylinder,
                    new Vector3(0f, 0.092f, -0.205f), new Vector3(0.036f, 0.010f, 0.036f), gunMetal, Quaternion.Euler(-90f, 0f, 0f));
                CreateRuntimePart(gun.transform, "E11_ChargingHandle", PrimitiveType.Cube,
                    new Vector3(0.066f, 0.020f, -0.105f), new Vector3(0.018f, 0.025f, 0.064f), gunMetal, Quaternion.identity);
                for (int vent = 0; vent < 6; vent++)
                {
                    CreateRuntimePart(gun.transform, "E11_CoolingRib_" + vent, PrimitiveType.Cube,
                        new Vector3(0f, 0.038f, -0.192f - vent * 0.026f),
                        new Vector3(0.100f, 0.008f, 0.012f), gunMetal, Quaternion.identity);
                }

                GameObject muzzle = new GameObject("Muzzle");
                muzzle.transform.SetParent(gun.transform, false);
                muzzle.transform.localPosition = new Vector3(0f, -0.002f, -0.54f);
                muzzle.transform.localRotation = Quaternion.identity;

                Logger.LogInfo("Attached E-11 procedural prop to rig RightHand bone '" + rightHand.name +
                    "'; muzzle position in imported-root space=" +
                    importedRoot.transform.InverseTransformPoint(muzzle.transform.position).ToString("F2") + ".");
            }
            else
            {
                Logger.LogError("Stormtrooper rig has no mapped RightHand; no rifle or muzzle was created.");
            }

            bool usingSkeletalClips = false;
            if (RuntimeTrooperAnimationSet != null && RuntimeTrooperAnimationSet.ClipCount > 0)
            {
                ImperialTrooperAnimationPlayer clipPlayer = importedRoot.AddComponent<ImperialTrooperAnimationPlayer>();
                clipPlayer.Configure(importedRoot.transform, RuntimeTrooperAnimationSet);
                if (clipPlayer.BoundTrackCount >= 12)
                {
                    usingSkeletalClips = true;
                    Logger.LogInfo("Using retargeted skeletal animations on imported Stormtrooper model (" + clipPlayer.BoundTrackCount + " track bindings).");
                }
                else
                {
                    Destroy(clipPlayer);
                    Logger.LogWarning("Retargeted animation clips matched only " + clipPlayer.BoundTrackCount + " tracks; reverting to procedural fallback.");
                }
            }
            if (!usingSkeletalClips && boneMap.Count >= 8)
            {
                ImperialTrooperProceduralAnimator animator = importedRoot.AddComponent<ImperialTrooperProceduralAnimator>();
                animator.Configure(boneMap, importedRoot.transform);
                Logger.LogInfo("Using imported Stormtrooper skin with procedural Mixamo-bone fallback locomotion (" + boneMap.Count + " mapped body bones).");
            }
            else if (!usingSkeletalClips)
            {
                Logger.LogWarning("Stormtrooper mesh loaded, but only " + boneMap.Count + " humanoid bones were mapped; walk cycle may be limited.");
            }
            Logger.LogInfo("Applied plain ceramic material, dark visor and attached E-11-style blaster prop to imported Stormtrooper.");
            return importedRoot;
        }

        private GameObject CreateRuntimePart(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, Quaternion rotation)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;
            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
            return part;
        }

        private void DisableInheritedAiDrivers(GameObject masterPrefab)
        {
            if (masterPrefab == null) return;
            int found = 0;
            int disabled = 0;
            foreach (Component component in masterPrefab.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "AISkillDriver") continue;
                found++;
                Behaviour behaviour = component as Behaviour;
                if (behaviour != null && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    disabled++;
                }
            }
            Logger.LogInfo("Master tactical AI setup: found " + found + " inherited AISkillDriver components, disabled " + disabled +
                "; positional behavior is handled by ImperialTrooperBrain.");
        }

        private void ConfigureTacticalDrivers(GameObject masterPrefab)
        {
            if (masterPrefab == null) return;
            Assembly gameAssembly = typeof(CharacterBody).Assembly;
            Type driverType = gameAssembly.GetType("RoR2.CharacterAI.AISkillDriver") ??
                              gameAssembly.GetType("RoR2.AISkillDriver");
            if (driverType == null)
            {
                Logger.LogError("AISkillDriver type not found; tactical hold-position logic could not be registered.");
                return;
            }

            ConfigureTacticalDriver(masterPrefab, driverType, "ImperialRetreat", 0f, 12f, "FleeMoveTarget", false);
            ConfigureTacticalDriver(masterPrefab, driverType, "ImperialHoldPosition", 12f, 17f, "Stop", false);
            ConfigureTacticalDriver(masterPrefab, driverType, "ImperialAdvance", 17f, 1000f, "ChaseMoveTarget", false);
            Logger.LogInfo("Registered tactical AISkillDrivers: retreat below 12 m, hold from 12-17 m, advance beyond 17 m.");
        }

        private void ConfigureTacticalDriver(GameObject masterPrefab, Type driverType, string name,
            float minDistance, float maxDistance, string movementType, bool sprint)
        {
            Component driver = masterPrefab.AddComponent(driverType);
            SetAISkillDriverMember(driver, "customName", name);
            SetAISkillDriverMember(driver, "skillSlot", "None");
            SetAISkillDriverMember(driver, "requireSkillReady", false);
            SetAISkillDriverMember(driver, "moveTargetType", "CurrentEnemy");
            SetAISkillDriverMember(driver, "minDistance", minDistance);
            SetAISkillDriverMember(driver, "maxDistance", maxDistance);
            SetAISkillDriverMember(driver, "selectionRequiresTargetLoS", false);
            SetAISkillDriverMember(driver, "activationRequiresTargetLoS", false);
            SetAISkillDriverMember(driver, "activationRequiresAimConfirmation", false);
            SetAISkillDriverMember(driver, "movementType", movementType);
            SetAISkillDriverMember(driver, "aimType", "AtCurrentEnemy");
            SetAISkillDriverMember(driver, "ignoreNodeGraph", false);
            SetAISkillDriverMember(driver, "resetCurrentEnemyOnNextDriverSelection", false);
            SetAISkillDriverMember(driver, "noRepeat", false);
            SetAISkillDriverMember(driver, "shouldSprint", sprint);
        }

        private void SetAISkillDriverMember(Component driver, string name, object value)
        {
            if (driver == null || string.IsNullOrEmpty(name)) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = driver.GetType();
            FieldInfo field = type.GetField(name, flags);
            if (field != null)
            {
                object converted = value;
                if (field.FieldType.IsEnum && value is string enumValue)
                    converted = Enum.Parse(field.FieldType, enumValue, true);
                else if (value != null && !field.FieldType.IsInstanceOfType(value))
                    converted = Convert.ChangeType(value, field.FieldType);
                field.SetValue(driver, converted);
                return;
            }

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                object converted = value;
                if (property.PropertyType.IsEnum && value is string enumValue)
                    converted = Enum.Parse(property.PropertyType, enumValue, true);
                else if (value != null && !property.PropertyType.IsInstanceOfType(value))
                    converted = Convert.ChangeType(value, property.PropertyType);
                property.SetValue(driver, converted, null);
                return;
            }
            Logger.LogWarning("AISkillDriver member '" + name + "' was not found on " + type.FullName + ".");
        }

        private void SilenceVanillaSkills(GameObject bodyPrefab)
        {
            foreach (GenericSkill skill in bodyPrefab.GetComponentsInChildren<GenericSkill>(true))
            {
                if (skill == null) continue;
                skill.stock = 0;
                skill.enabled = false;
            }
        }

        private CharacterSpawnCard MakeSpawnCard(GameObject masterPrefab)
        {
            CharacterSpawnCard source = LegacyResourcesAPI.Load<CharacterSpawnCard>("SpawnCards/CharacterSpawnCards/cscCommando");
            CharacterSpawnCard card = source != null ? ScriptableObject.Instantiate(source) : ScriptableObject.CreateInstance<CharacterSpawnCard>();
            card.name = "cscImperialTrooper";
            card.prefab = masterPrefab;
            card.sendOverNetwork = true;
            card.hullSize = HullClassification.Human;
            card.nodeGraphType = MapNodeGroup.GraphType.Ground;
            card.requiredFlags = NodeFlags.None;
            card.forbiddenFlags = NodeFlags.None;
            card.directorCreditCost = 15;
            card.occupyPosition = false;
            card.noElites = false;
            return card;
        }

        private void TryRegisterRedTracer()
        {
            try
            {
                GameObject vanillaTracer = LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/Tracers/TracerCommandoDefault");
                if (vanillaTracer == null)
                {
                    Logger.LogWarning("Vanilla Commando tracer not found; shots will still deal damage, but tracer visuals may be missing.");
                    return;
                }
                RedTracerPrefab = PrefabAPI.InstantiateClone(vanillaTracer, "ImperialTrooperRedTracer", false);
                // Register this as a normal networked, poolable RoR2 effect. Without VFXAttributes,
                // EffectManager logs "Tracer: No owning pool set" for every shot.
                if (RedTracerPrefab.GetComponent<EffectComponent>() == null)
                {
                    EffectComponent effectComponent = RedTracerPrefab.AddComponent<EffectComponent>();
                    effectComponent.positionAtReferencedTransform = false;
                    effectComponent.parentToReferencedTransform = false;
                }
                VFXAttributes vfxAttributes = RedTracerPrefab.GetComponent<VFXAttributes>();
                if (vfxAttributes == null) vfxAttributes = RedTracerPrefab.AddComponent<VFXAttributes>();
                vfxAttributes.DoNotPool = false;
                if (RedTracerPrefab.GetComponent<NetworkIdentity>() == null) RedTracerPrefab.AddComponent<NetworkIdentity>();
                foreach (Renderer renderer in RedTracerPrefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer.sharedMaterial == null) continue;
                    Material tinted = new Material(renderer.sharedMaterial);
                    tinted.name = "ImperialTrooper_RedBlasterMaterial";
                    SetTint(tinted, new Color(1f, 0.08f, 0.035f));
                    if (tinted.HasProperty("_EmissionColor")) tinted.SetColor("_EmissionColor", new Color(1f, 0.015f, 0.002f) * 2.5f);
                    renderer.sharedMaterial = tinted;
                }
                ContentAddition.AddEffect(RedTracerPrefab);
            }
            catch (Exception ex) { Logger.LogWarning("Red tracer setup failed; shots will use the built-in visual if available: " + ex.Message); }
        }

        private Dictionary<HumanBodyBones, Transform> ResolveBoneMap(Animator animator, Transform root)
        {
            HumanBodyBones[] wanted = {
                HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
            };
            var result = new Dictionary<HumanBodyBones, Transform>();
            if (animator != null && animator.isHuman)
            {
                foreach (HumanBodyBones b in wanted)
                {
                    try { Transform t = animator.GetBoneTransform(b); if (t != null) result[b] = t; }
                    catch { }
                }
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (HumanBodyBones b in wanted)
            {
                if (result.ContainsKey(b)) continue;
                string[] aliases = BoneAliases(b);
                Transform match = null;
                // Exact normalized name matches first.
                foreach (Transform t in all)
                {
                    if (t == null) continue;
                    string normalized = NormalizeBoneName(t.name);
                    if (aliases.Any(a => normalized == NormalizeBoneName(a) || normalized.EndsWith(NormalizeBoneName(a), StringComparison.Ordinal)))
                    { match = t; break; }
                }
                if (match != null) result[b] = match;
            }
            return result;
        }

        private static string NormalizeBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.ToLowerInvariant().Replace("mixamorig", string.Empty);
            return new string(s.Where(char.IsLetterOrDigit).ToArray());
        }

        private static string[] BoneAliases(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Hips: return new[] { "Hips", "Pelvis", "Bip001 Pelvis", "RootPelvis" };
                case HumanBodyBones.Spine: return new[] { "Spine", "Bip001 Spine", "Spine1", "Torso" };
                case HumanBodyBones.Chest: return new[] { "Chest", "Spine2", "Spine3", "UpperChest", "Bip001 Spine1" };
                case HumanBodyBones.Neck: return new[] { "Neck", "Bip001 Neck" };
                case HumanBodyBones.Head: return new[] { "Head", "Bip001 Head" };
                case HumanBodyBones.LeftUpperArm: return new[] { "LeftUpperArm", "LeftArm", "L_UpperArm", "UpperArm_L", "Bip001 L UpperArm", "UpperArmL" };
                case HumanBodyBones.LeftLowerArm: return new[] { "LeftLowerArm", "LeftForeArm", "L_Forearm", "L_ForeArm", "ForeArm_L", "Bip001 L Forearm" };
                case HumanBodyBones.LeftHand: return new[] { "LeftHand", "L_Hand", "Hand_L", "Bip001 L Hand" };
                case HumanBodyBones.RightUpperArm: return new[] { "RightUpperArm", "RightArm", "R_UpperArm", "UpperArm_R", "Bip001 R UpperArm", "UpperArmR" };
                case HumanBodyBones.RightLowerArm: return new[] { "RightLowerArm", "RightForeArm", "R_Forearm", "R_ForeArm", "ForeArm_R", "Bip001 R Forearm" };
                case HumanBodyBones.RightHand: return new[] { "RightHand", "R_Hand", "Hand_R", "Bip001 R Hand" };
                case HumanBodyBones.LeftUpperLeg: return new[] { "LeftUpperLeg", "LeftUpLeg", "LeftThigh", "L_Thigh", "Thigh_L", "Bip001 L Thigh", "UpperLegL" };
                case HumanBodyBones.LeftLowerLeg: return new[] { "LeftLowerLeg", "LeftLeg", "LeftCalf", "L_Calf", "L_Shin", "Calf_L", "Bip001 L Calf" };
                case HumanBodyBones.LeftFoot: return new[] { "LeftFoot", "L_Foot", "Foot_L", "Bip001 L Foot" };
                case HumanBodyBones.RightUpperLeg: return new[] { "RightUpperLeg", "RightUpLeg", "RightThigh", "R_Thigh", "Thigh_R", "Bip001 R Thigh", "UpperLegR" };
                case HumanBodyBones.RightLowerLeg: return new[] { "RightLowerLeg", "RightLeg", "RightCalf", "R_Calf", "R_Shin", "Calf_R", "Bip001 R Calf" };
                case HumanBodyBones.RightFoot: return new[] { "RightFoot", "R_Foot", "Foot_R", "Bip001 R Foot" };
                default: return Array.Empty<string>();
            }
        }

        private Dictionary<HumanBodyBones, Transform> CreateProceduralRig(Transform visualRoot)
        {
            var positions = new Dictionary<HumanBodyBones, Vector3>
            {
                { HumanBodyBones.Hips, new Vector3(0f, .92f, 0f) },
                { HumanBodyBones.Spine, new Vector3(0f, 1.17f, 0f) },
                { HumanBodyBones.Chest, new Vector3(0f, 1.36f, 0f) },
                { HumanBodyBones.Neck, new Vector3(0f, 1.55f, 0f) },
                { HumanBodyBones.Head, new Vector3(0f, 1.69f, 0f) },
                { HumanBodyBones.LeftUpperArm, new Vector3(-.285f, 1.445f, 0f) },
                { HumanBodyBones.LeftLowerArm, new Vector3(-.385f, 1.20f, 0f) },
                { HumanBodyBones.LeftHand, new Vector3(-.30f, 1.09f, .20f) },
                { HumanBodyBones.RightUpperArm, new Vector3(.285f, 1.445f, 0f) },
                { HumanBodyBones.RightLowerArm, new Vector3(.385f, 1.20f, 0f) },
                { HumanBodyBones.RightHand, new Vector3(.30f, 1.09f, .20f) },
                { HumanBodyBones.LeftUpperLeg, new Vector3(-.13f, .73f, 0f) },
                { HumanBodyBones.LeftLowerLeg, new Vector3(-.13f, .36f, 0f) },
                { HumanBodyBones.LeftFoot, new Vector3(-.13f, .14f, .07f) },
                { HumanBodyBones.RightUpperLeg, new Vector3(.13f, .73f, 0f) },
                { HumanBodyBones.RightLowerLeg, new Vector3(.13f, .36f, 0f) },
                { HumanBodyBones.RightFoot, new Vector3(.13f, .14f, .07f) }
            };
            var result = new Dictionary<HumanBodyBones, Transform>();
            foreach (var pair in positions)
            {
                GameObject boneObject = new GameObject("ImperialProcBone_" + pair.Key);
                boneObject.transform.SetParent(visualRoot, false);
                boneObject.transform.localPosition = pair.Value;
                boneObject.transform.localRotation = Quaternion.identity;
                result[pair.Key] = boneObject.transform;
            }
            return result;
        }

        private GameObject BuildArmorModel(Transform modelRoot)
        {
            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;

            GameObject root = new GameObject("ImperialTrooperVisuals");
            root.transform.SetParent(modelRoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            Renderer exemplar = modelRoot.GetComponentInChildren<Renderer>(true);
            Material sourceMaterial = exemplar != null ? exemplar.sharedMaterial : null;
            Material ceramic = BuildMaterial("Imperial_Ceramic", new Color(0.90f, 0.92f, 0.87f), 0.2f, 0.36f, sourceMaterial);
            Material bright = BuildMaterial("Imperial_Ceramic_Highlight", new Color(0.99f, 0.99f, 0.93f), 0.18f, 0.42f, sourceMaterial);
            Material charcoal = BuildMaterial("Imperial_Undersuit", new Color(0.018f, 0.026f, 0.032f), 0.1f, 0.22f, sourceMaterial);
            Material graphite = BuildMaterial("Imperial_Graphite", new Color(0.18f, 0.22f, 0.23f), 0.55f, 0.34f, sourceMaterial);
            Material black = BuildMaterial("Imperial_Visor", new Color(0.006f, 0.013f, 0.018f), 0.6f, 0.4f, sourceMaterial);
            Material red = BuildMaterial("Imperial_BlasterEmitter", new Color(1f, 0.025f, 0.01f), 0.05f, 0.3f, sourceMaterial);

            // The copied Commando skeleton is not guaranteed to expose a compatible public humanoid
            // Avatar. Build a deterministic independent rig for the primitive armor instead, and animate
            // its limbs explicitly. This avoids the frozen-robot result when Unity bone mapping fails.
            Dictionary<HumanBodyBones, Transform> boneMap = CreateProceduralRig(root.transform);
            Func<HumanBodyBones, Transform> bone = b => boneMap.TryGetValue(b, out Transform found) ? found : null;
            ImperialTrooperProceduralAnimator proceduralAnimator = root.AddComponent<ImperialTrooperProceduralAnimator>();
            proceduralAnimator.Configure(boneMap, root.transform);
            Logger.LogInfo("Using independent procedural trooper rig with " + boneMap.Count + " articulated transforms.");

            // Boots and leg armor.
            foreach (int side in new[] { -1, 1 })
            {
                HumanBodyBones thigh = side < 0 ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
                HumanBodyBones shin = side < 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                HumanBodyBones foot = side < 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                string s = side < 0 ? "L" : "R";
                PartBox(root.transform, $"{s}_BootSole", new Vector3(side * .13f, .10f, .08f), new Vector3(.18f,.055f,.30f), charcoal, foot, bone);
                PartBox(root.transform, $"{s}_BootShell", new Vector3(side * .13f, .16f, .07f), new Vector3(.17f,.12f,.29f), graphite, foot, bone);
                PartBox(root.transform, $"{s}_ToeCap", new Vector3(side * .13f, .17f, .18f), new Vector3(.18f,.09f,.14f), ceramic, foot, bone);
                PartSphere(root.transform, $"{s}_ShinCore", new Vector3(side*.13f,.34f,.015f), new Vector3(.13f,.29f,.14f), charcoal, shin, bone);
                PartBox(root.transform, $"{s}_Greave", new Vector3(side*.13f,.36f,.095f), new Vector3(.15f,.25f,.08f), ceramic, shin, bone);
                PartBox(root.transform, $"{s}_GreaveRidge", new Vector3(side*.13f,.36f,.14f), new Vector3(.035f,.18f,.014f), bright, shin, bone);
                PartSphere(root.transform, $"{s}_Knee", new Vector3(side*.13f,.535f,.035f), new Vector3(.14f,.13f,.14f), charcoal, shin, bone);
                PartBox(root.transform, $"{s}_KneePad", new Vector3(side*.13f,.545f,.11f), new Vector3(.17f,.085f,.07f), bright, shin, bone);
                PartSphere(root.transform, $"{s}_ThighCore", new Vector3(side*.13f,.73f,0f), new Vector3(.16f,.25f,.15f), charcoal, thigh, bone);
                PartBox(root.transform, $"{s}_ThighPlate", new Vector3(side*.13f,.735f,.085f), new Vector3(.16f,.20f,.07f), ceramic, thigh, bone);
                PartBox(root.transform, $"{s}_ThighInset", new Vector3(side*.13f,.735f,.126f), new Vector3(.055f,.13f,.014f), bright, thigh, bone);
            }

            PartSphere(root.transform, "Pelvis_Core", new Vector3(0,.91f,0), new Vector3(.35f,.22f,.22f), charcoal, HumanBodyBones.Hips, bone);
            PartBox(root.transform, "Pelvis_Plate", new Vector3(0,.93f,.10f), new Vector3(.36f,.16f,.07f), ceramic, HumanBodyBones.Hips, bone);
            PartBox(root.transform, "Utility_Belt", new Vector3(0,1.015f,0), new Vector3(.43f,.075f,.25f), graphite, HumanBodyBones.Hips, bone);
            PartBox(root.transform, "Belt_Buckle", new Vector3(0,1.015f,.135f), new Vector3(.075f,.045f,.02f), bright, HumanBodyBones.Hips, bone);
            for (int i=0; i<4; i++) PartBox(root.transform, "Belt_Canister_"+i, new Vector3((i<2?-1:1)*(i%2==0?.17f:.105f),1.02f,.135f), new Vector3(.045f,.065f,.05f), bright, HumanBodyBones.Hips, bone);

            // Ribs, chest plates, shoulder pauldrons and articulated arms.
            PartBox(root.transform, "Abdomen_Flex", new Vector3(0,1.16f,0), new Vector3(.25f,.22f,.19f), charcoal, HumanBodyBones.Chest, bone);
            for (int i=0;i<4;i++) PartBox(root.transform, "Abdomen_Rib_"+i, new Vector3(0,1.08f+i*.047f,.105f), new Vector3(.24f,.023f,.02f), i%2==0?ceramic:graphite, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Chest_Shell", new Vector3(0,1.36f,.01f), new Vector3(.47f,.31f,.24f), ceramic, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Chest_Central_Plate", new Vector3(0,1.365f,.142f), new Vector3(.19f,.20f,.025f), bright, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Chest_Ridge_L", new Vector3(-.155f,1.38f,.142f), new Vector3(.032f,.20f,.02f), graphite, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Chest_Ridge_R", new Vector3(.155f,1.38f,.142f), new Vector3(.032f,.20f,.02f), graphite, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Collar", new Vector3(0,1.535f,0), new Vector3(.22f,.07f,.19f), graphite, HumanBodyBones.Chest, bone);
            PartBox(root.transform, "Collar_Front", new Vector3(0,1.54f,.105f), new Vector3(.17f,.05f,.025f), bright, HumanBodyBones.Chest, bone);

            foreach (int side in new[] { -1, 1 })
            {
                HumanBodyBones upper = side<0 ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
                HumanBodyBones lower = side<0 ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
                HumanBodyBones hand = side<0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                string s=side<0?"L":"R";
                PartSphere(root.transform,s+"_Shoulder_Core",new Vector3(side*.285f,1.445f,0),new Vector3(.18f,.18f,.18f),charcoal,upper,bone);
                PartSphere(root.transform,s+"_Pauldron",new Vector3(side*.315f,1.475f,.005f),new Vector3(.23f,.16f,.24f),bright,upper,bone);
                PartBox(root.transform,s+"_UpperArm_Plate",new Vector3(side*.385f,1.315f,.015f),new Vector3(.145f,.23f,.17f),ceramic,upper,bone);
                PartBox(root.transform,s+"_UpperArm_Trim",new Vector3(side*.458f,1.315f,.06f),new Vector3(.022f,.15f,.10f),graphite,upper,bone);
                PartSphere(root.transform,s+"_Elbow_Core",new Vector3(side*.385f,1.20f,.015f),new Vector3(.13f,.12f,.13f),charcoal,lower,bone);
                PartBox(root.transform,s+"_Forearm_Gauntlet",new Vector3(side*.30f,1.145f,.075f),new Vector3(.14f,.15f,.23f),ceramic,lower,bone);
                PartBox(root.transform,s+"_Forearm_Ridge",new Vector3(side*.30f,1.16f,.192f),new Vector3(.035f,.09f,.016f),bright,lower,bone);
                PartSphere(root.transform,s+"_Glove",new Vector3(side*.30f,1.09f,.20f),new Vector3(.11f,.08f,.10f),charcoal,hand,bone);
                PartBox(root.transform,s+"_Knuckle_Plate",new Vector3(side*.30f,1.12f,.245f),new Vector3(.08f,.03f,.02f),graphite,hand,bone);
            }

            // A sealed infantry helmet: crown, dark visor, mouth grille, cheek blocks.
            PartSphere(root.transform,"Helmet_Shell",new Vector3(0,1.69f,0),new Vector3(.33f,.33f,.30f),bright,HumanBodyBones.Head,bone);
            PartSphere(root.transform,"Helmet_Crown",new Vector3(0,1.785f,-.005f),new Vector3(.26f,.14f,.25f),ceramic,HumanBodyBones.Head,bone);
            PartBox(root.transform,"Helmet_Brow",new Vector3(0,1.72f,.132f),new Vector3(.28f,.05f,.035f),ceramic,HumanBodyBones.Head,bone);
            PartBox(root.transform,"Helmet_Visor",new Vector3(0,1.678f,.151f),new Vector3(.265f,.075f,.025f),black,HumanBodyBones.Head,bone);
            PartBox(root.transform,"Helmet_Visor_Glint",new Vector3(0,1.70f,.168f),new Vector3(.19f,.008f,.006f),graphite,HumanBodyBones.Head,bone);
            PartBox(root.transform,"Helmet_Muzzle",new Vector3(0,1.615f,.12f),new Vector3(.205f,.07f,.07f),ceramic,HumanBodyBones.Head,bone);
            PartBox(root.transform,"Helmet_Chin",new Vector3(0,1.582f,.10f),new Vector3(.15f,.035f,.06f),bright,HumanBodyBones.Head,bone);
            foreach(int side in new[]{-1,1})
            {
                PartBox(root.transform,(side<0?"L":"R")+"_Helmet_Cheek",new Vector3(side*.13f,1.635f,.095f),new Vector3(.075f,.10f,.09f),ceramic,HumanBodyBones.Head,bone);
                for(int i=0;i<3;i++) PartBox(root.transform,(side<0?"L":"R")+"_Helmet_Vent_"+i,new Vector3(side*.145f,1.595f+i*.014f,.134f),new Vector3(.025f,.005f,.006f),graphite,HumanBodyBones.Head,bone);
            }
            PartBox(root.transform,"Helmet_Status_Sensor",new Vector3(0,1.755f,.158f),new Vector3(.025f,.018f,.009f),red,HumanBodyBones.Head,bone);

            // Compact energy carbine, placed in the right-hand firing pose.
            HumanBodyBones weaponBone = HumanBodyBones.RightHand;
            PartBox(root.transform,"Blaster_Receiver",new Vector3(.18f,1.205f,.32f),new Vector3(.12f,.10f,.28f),graphite,weaponBone,bone);
            PartBox(root.transform,"Blaster_Top_Rail",new Vector3(.18f,1.264f,.31f),new Vector3(.07f,.018f,.30f),bright,weaponBone,bone);
            PartBox(root.transform,"Blaster_Stock",new Vector3(.18f,1.20f,.16f),new Vector3(.10f,.075f,.13f),graphite,weaponBone,bone);
            PartBox(root.transform,"Blaster_Grip",new Vector3(.18f,1.13f,.29f),new Vector3(.055f,.11f,.06f),charcoal,weaponBone,bone);
            PartBox(root.transform,"Blaster_Foregrip",new Vector3(.18f,1.15f,.42f),new Vector3(.055f,.07f,.065f),bright,weaponBone,bone);
            PartCylinder(root.transform,"Blaster_Barrel",new Vector3(.18f,1.22f,.52f),.025f,.28f,graphite,weaponBone,bone);
            PartCylinder(root.transform,"Blaster_Muzzle",new Vector3(.18f,1.22f,.67f),.034f,.045f,bright,weaponBone,bone);
            PartCylinder(root.transform,"Blaster_Emitter",new Vector3(.18f,1.22f,.696f),.018f,.008f,red,weaponBone,bone);
            PartBox(root.transform,"Blaster_PowerCell",new Vector3(.18f,1.16f,.33f),new Vector3(.06f,.04f,.10f),ceramic,weaponBone,bone);

            GameObject muzzleObject = new GameObject("Muzzle");
            muzzleObject.transform.SetParent(root.transform, false);
            muzzleObject.transform.localPosition = new Vector3(.18f,1.22f,.715f);
            muzzleObject.transform.localRotation = Quaternion.identity;
            Transform rightHand = bone(HumanBodyBones.RightHand);
            if (rightHand != null) muzzleObject.transform.SetParent(rightHand, true);
            return root;
        }


        private void OptimizeArmorMeshes(GameObject modelRoot, Renderer[] vanillaRenderers)
        {
            var originalSet = new HashSet<Renderer>(vanillaRenderers ?? Array.Empty<Renderer>());
            MeshFilter[] filters = modelRoot.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f != null && f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null
                    && !originalSet.Contains(f.GetComponent<MeshRenderer>()))
                .ToArray();
            var groups = filters.GroupBy(f => new
            {
                parent = f.transform.parent,
                material = f.GetComponent<MeshRenderer>().sharedMaterial
            }).ToArray();

            int mergedCount = 0;
            foreach (var group in groups)
            {
                Transform parent = group.Key.parent;
                Material material = group.Key.material;
                if (parent == null || material == null) continue;
                MeshFilter[] sourceFilters = group.ToArray();
                var combines = new CombineInstance[sourceFilters.Length];
                int vertexCount = 0;
                for (int i = 0; i < sourceFilters.Length; i++)
                {
                    combines[i] = new CombineInstance
                    {
                        mesh = sourceFilters[i].sharedMesh,
                        subMeshIndex = 0,
                        transform = parent.worldToLocalMatrix * sourceFilters[i].transform.localToWorldMatrix
                    };
                    vertexCount += sourceFilters[i].sharedMesh.vertexCount;
                }

                Mesh merged = new Mesh { name = "ImperialArmorMesh_" + (++mergedCount) };
                if (vertexCount > 65535) merged.indexFormat = IndexFormat.UInt32;
                merged.CombineMeshes(combines, true, true, false);
                GameObject combinedObject = new GameObject("ImperialArmorMerged_" + mergedCount);
                combinedObject.transform.SetParent(parent, false);
                combinedObject.transform.localPosition = Vector3.zero;
                combinedObject.transform.localRotation = Quaternion.identity;
                combinedObject.transform.localScale = Vector3.one;
                combinedObject.AddComponent<MeshFilter>().sharedMesh = merged;
                MeshRenderer combinedRenderer = combinedObject.AddComponent<MeshRenderer>();
                combinedRenderer.sharedMaterial = material;
                combinedRenderer.shadowCastingMode = ShadowCastingMode.On;
                combinedRenderer.receiveShadows = true;

                foreach (MeshFilter source in sourceFilters)
                {
                    MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
                    if (sourceRenderer != null) sourceRenderer.enabled = false;
                    Destroy(source.gameObject);
                }
            }
            Logger.LogInfo("Merged trooper armor meshes into " + mergedCount + " bone/material renderers (" + filters.Length + " primitive meshes processed).");
        }

        private void ConfigureCharacterModel(GameObject bodyPrefab, Transform modelRoot, GameObject visualRoot)
        {
            CharacterModel model = modelRoot.GetComponent<CharacterModel>();
            if (model == null) model = modelRoot.GetComponentInChildren<CharacterModel>(true);
            if (model == null) return;
            var infos = new List<CharacterModel.RendererInfo>();
            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                infos.Add(new CharacterModel.RendererInfo
                {
                    renderer = renderer,
                    defaultMaterial = renderer.sharedMaterial,
                    defaultShadowCastingMode = ShadowCastingMode.On,
                    ignoreOverlays = false
                });
            }
            model.baseRendererInfos = infos.ToArray();
        }

        private Material BuildMaterial(string name, Color color, float metallic, float smoothness, Material fallback)
        {
            Shader shader = Shader.Find("Hopoo Games/Deferred/Standard");
            if (shader == null) shader = Shader.Find("Standard");
            Material m = shader != null ? new Material(shader) : (fallback != null ? new Material(fallback) : null);
            if (m == null) return fallback;
            m.name = name;
            SetTint(m, color);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (name.Contains("Emitter") && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * 2.0f);
            }
            return m;
        }

        private static void SetTint(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", color);
        }

        private void PartBox(Transform root, string name, Vector3 position, Vector3 size, Material material, HumanBodyBones targetBone, Func<HumanBodyBones, Transform> bone)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            go.transform.SetParent(root, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            Renderer r = go.GetComponent<Renderer>(); if (r != null) r.sharedMaterial = material;
            Transform b = bone(targetBone); if (b != null) go.transform.SetParent(b, true);
        }

        private void PartSphere(Transform root, string name, Vector3 position, Vector3 size, Material material, HumanBodyBones targetBone, Func<HumanBodyBones, Transform> bone)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            Collider collider = go.GetComponent<Collider>(); if (collider != null) Destroy(collider);
            go.transform.SetParent(root, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            Renderer r=go.GetComponent<Renderer>(); if(r!=null) r.sharedMaterial=material;
            Transform b=bone(targetBone); if(b!=null) go.transform.SetParent(b,true);
        }

        private void PartCylinder(Transform root, string name, Vector3 position, float radius, float length, Material material, HumanBodyBones targetBone, Func<HumanBodyBones, Transform> bone)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Collider collider=go.GetComponent<Collider>(); if(collider!=null) Destroy(collider);
            go.transform.SetParent(root,false);
            go.transform.localPosition=position;
            go.transform.localRotation=Quaternion.Euler(90f,0f,0f);
            go.transform.localScale=new Vector3(radius*2f,length/2f,radius*2f);
            Renderer r=go.GetComponent<Renderer>(); if(r!=null) r.sharedMaterial=material;
            Transform b=bone(targetBone); if(b!=null) go.transform.SetParent(b,true);
        }
    }

    public sealed class ImperialTrooperBoltVisual : MonoBehaviour
    {
        [SerializeField] private Material boltMaterial;
        private GameObject coreObject;
        private TrailRenderer trail;
        private bool prepared;

        internal void Initialize(Material material)
        {
            if (material != null) boltMaterial = material;
            // The projectile prefab is stored inactive; its visual children are created on OnEnable
            // for each spawned projectile instance, not while the definition prefab is registered.
        }

        private void Awake() { EnsureVisual(); }
        private void OnEnable() { EnsureVisual(); }

        private void EnsureVisual()
        {
            Transform clonedCore = transform.Find("ImperialTrooperRedBoltCore");
            TrailRenderer clonedTrail = GetComponent<TrailRenderer>();
            bool coreBelongsToThisProjectile = coreObject != null &&
                (coreObject.transform == transform || coreObject.transform.IsChildOf(transform));
            if (prepared && coreBelongsToThisProjectile && clonedTrail != null)
            {
                trail = clonedTrail;
                return;
            }

            // Runtime-only references cached while configuring the prefab can point back to the
            // prefab after InstantiateClone. Re-resolve the core and trail on each actual projectile.
            prepared = false;
            coreObject = clonedCore != null ? clonedCore.gameObject : null;
            trail = clonedTrail;
            Material material = boltMaterial != null ? boltMaterial : ImperialTrooperPlugin.BlasterBoltMaterial;
            if (material == null)
            {
                ImperialTrooperPlugin.LogSource?.LogWarning("Red bolt visual could not initialize because its material is missing.");
                return;
            }
            boltMaterial = material;

            Transform existing = transform.Find("ImperialTrooperRedBoltCore");
            if (existing != null) coreObject = existing.gameObject;
            if (coreObject == null)
            {
                coreObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                coreObject.name = "ImperialTrooperRedBoltCore";
                Collider collider = coreObject.GetComponent<Collider>();
                if (collider != null) Destroy(collider);
                coreObject.transform.SetParent(transform, false);
                coreObject.transform.localPosition = Vector3.zero;
                coreObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                coreObject.transform.localScale = new Vector3(0.045f, 0.17f, 0.045f);
            }

            Renderer coreRenderer = coreObject.GetComponent<Renderer>();
            if (coreRenderer != null)
            {
                coreRenderer.sharedMaterial = boltMaterial;
                coreRenderer.shadowCastingMode = ShadowCastingMode.Off;
                coreRenderer.receiveShadows = false;
            }

            trail = GetComponent<TrailRenderer>();
            if (trail == null) trail = gameObject.AddComponent<TrailRenderer>();
            trail.sharedMaterial = boltMaterial;
            trail.enabled = true;
            trail.time = 0.14f;
            trail.startWidth = 0.042f;
            trail.endWidth = 0.0015f;
            trail.minVertexDistance = 0.018f;
            trail.numCornerVertices = 2;
            trail.numCapVertices = 2;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.alignment = LineAlignment.View;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] {
                    new GradientColorKey(new Color(1f, 0.012f, 0.006f), 0f),
                    new GradientColorKey(new Color(1f, 0.14f, 0.025f), 1f)
                },
                new[] {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.60f, 0.62f),
                    new GradientAlphaKey(0f, 1f)
                });
            trail.colorGradient = gradient;
            trail.Clear();
            prepared = true;
        }

        private void OnDisable()
        {
            if (trail != null) trail.Clear();
        }
    }

    public sealed class ImperialTrooperAudioEffect : MonoBehaviour
    {
        public int clipKind;

        private IEnumerator Start()
        {
            // Each peer loads its own MP3 clip. Wait briefly if a remote effect reaches this client
            // while the initial audio-loading coroutine is still running.
            AudioClip clip = null;
            for (int i = 0; i < 120 && clip == null; i++)
            {
                clip = ImperialTrooperPlugin.GetAudioClip(clipKind);
                if (clip == null) yield return new WaitForSeconds(0.05f);
            }
            if (clip != null)
            {
                // On the host, ImperialTrooperPlugin.PlayClip already plays locally; play this
                // replicated effect only on remote clients to avoid double volume in solo games.
                if (!NetworkServer.active)
                {
                    try { ImperialTrooperPlugin.PlayLocalClip(clip, transform.position, ImperialTrooperPlugin.GetAudioVolume(clipKind), ImperialTrooperPlugin.IsRadioClipKind(clipKind)); }
                    catch (Exception ex) { ImperialTrooperPlugin.LogSource?.LogWarning("Networked trooper audio playback failed: " + ex.Message); }
                }
                yield return new WaitForSeconds(Mathf.Min(clip.length + 0.15f, 12f));
            }
            else yield return new WaitForSeconds(0.25f);
            if (gameObject != null) Destroy(gameObject);
        }
    }

    [DefaultExecutionOrder(10000)]
    public sealed class ImperialTrooperModelGuard : MonoBehaviour
    {
        private Renderer[] vanillaRenderers = Array.Empty<Renderer>();

        internal void Initialize(Renderer[] renderers)
        {
            vanillaRenderers = renderers ?? Array.Empty<Renderer>();
            HideVanillaRenderers();
        }

        private void LateUpdate() { HideVanillaRenderers(); }

        private void HideVanillaRenderers()
        {
            for (int i = 0; i < vanillaRenderers.Length; i++)
            {
                Renderer renderer = vanillaRenderers[i];
                if (renderer != null && renderer.enabled) renderer.enabled = false;
            }
        }
    }

    public sealed class ImperialTrooperProceduralAnimator : MonoBehaviour
    {
        private CharacterBody body;
        private Transform visualRoot;
        private Transform leftUpperArm, leftLowerArm, rightUpperArm, rightLowerArm;
        private Transform leftUpperLeg, leftLowerLeg, rightUpperLeg, rightLowerLeg;
        private Quaternion lArm0, llArm0, rArm0, rlArm0, lLeg0, llLeg0, rLeg0, rlLeg0;
        private Vector3 rootPosition;
        private Quaternion rootRotation;
        private bool deathStarted;
        private float deathStartTime;

        internal void Configure(Dictionary<HumanBodyBones, Transform> bones, Transform root)
        {
            visualRoot = root;
            if (bones == null) return;
            bones.TryGetValue(HumanBodyBones.LeftUpperArm, out leftUpperArm);
            bones.TryGetValue(HumanBodyBones.LeftLowerArm, out leftLowerArm);
            bones.TryGetValue(HumanBodyBones.RightUpperArm, out rightUpperArm);
            bones.TryGetValue(HumanBodyBones.RightLowerArm, out rightLowerArm);
            bones.TryGetValue(HumanBodyBones.LeftUpperLeg, out leftUpperLeg);
            bones.TryGetValue(HumanBodyBones.LeftLowerLeg, out leftLowerLeg);
            bones.TryGetValue(HumanBodyBones.RightUpperLeg, out rightUpperLeg);
            bones.TryGetValue(HumanBodyBones.RightLowerLeg, out rightLowerLeg);
            lArm0 = leftUpperArm != null ? leftUpperArm.localRotation : Quaternion.identity;
            llArm0 = leftLowerArm != null ? leftLowerArm.localRotation : Quaternion.identity;
            rArm0 = rightUpperArm != null ? rightUpperArm.localRotation : Quaternion.identity;
            rlArm0 = rightLowerArm != null ? rightLowerArm.localRotation : Quaternion.identity;
            lLeg0 = leftUpperLeg != null ? leftUpperLeg.localRotation : Quaternion.identity;
            llLeg0 = leftLowerLeg != null ? leftLowerLeg.localRotation : Quaternion.identity;
            rLeg0 = rightUpperLeg != null ? rightUpperLeg.localRotation : Quaternion.identity;
            rlLeg0 = rightLowerLeg != null ? rightLowerLeg.localRotation : Quaternion.identity;
            rootPosition = root != null ? root.localPosition : Vector3.zero;
            rootRotation = root != null ? root.localRotation : Quaternion.identity;
        }

        private void Awake() { body = GetComponentInParent<CharacterBody>(); }

        private void LateUpdate()
        {
            if (visualRoot == null) return;
            bool dead = body != null && body.healthComponent != null && !body.healthComponent.alive;
            if (dead)
            {
                if (!deathStarted) { deathStarted = true; deathStartTime = Time.time; }
                float t = Mathf.Clamp01((Time.time - deathStartTime) / 0.48f);
                // A quick, readable fall instead of a standing model disappearing in-place.
                visualRoot.localRotation = rootRotation * Quaternion.Euler(68f * t, 0f, 32f * t);
                visualRoot.localPosition = rootPosition + Vector3.down * (0.05f + 0.45f * t);
                return;
            }

            float speed = body != null && body.characterMotor != null ? body.characterMotor.velocity.magnitude : 0f;
            float moving = Mathf.Clamp01(speed / 2.5f);
            float phase = Time.time * (6.2f + 1.6f * moving);
            float gait = Mathf.Sin(phase) * moving;
            float kneeL = Mathf.Max(0f, -Mathf.Sin(phase)) * moving * 34f;
            float kneeR = Mathf.Max(0f, Mathf.Sin(phase)) * moving * 34f;

            if (leftUpperLeg != null) leftUpperLeg.localRotation = lLeg0 * Quaternion.Euler(gait * 34f, 0f, 0f);
            if (rightUpperLeg != null) rightUpperLeg.localRotation = rLeg0 * Quaternion.Euler(-gait * 34f, 0f, 0f);
            if (leftLowerLeg != null) leftLowerLeg.localRotation = llLeg0 * Quaternion.Euler(kneeL, 0f, 0f);
            if (rightLowerLeg != null) rightLowerLeg.localRotation = rlLeg0 * Quaternion.Euler(kneeR, 0f, 0f);
            // Infantry advance with the blaster shoulder-high: arms do not swing freely like a civilian walk.
            if (leftUpperArm != null) leftUpperArm.localRotation = lArm0 * Quaternion.Euler(-8f - gait * 5f, 0f, -4f);
            if (rightUpperArm != null) rightUpperArm.localRotation = rArm0 * Quaternion.Euler(-10f + gait * 3f, 0f, 2f);
            if (leftLowerArm != null) leftLowerArm.localRotation = llArm0 * Quaternion.Euler(-18f, 0f, 0f);
            if (rightLowerArm != null) rightLowerArm.localRotation = rlArm0 * Quaternion.Euler(-22f, 0f, 0f);
            visualRoot.localRotation = rootRotation;
            visualRoot.localPosition = rootPosition + Vector3.up * (0.018f * moving * Mathf.Abs(Mathf.Sin(phase * 2f)));
        }
    }

    [DefaultExecutionOrder(10000)]
    public sealed class ImperialTrooperBrain : MonoBehaviour
    {
        public float DetectionRange = 32f;
        public float FireInterval = 1.65f;
        public float PreferredRange = 13f;
        public float SpreadDegrees = 2.7f;
        public int BurstShotCount = 3;
        public float BurstShotInterval = 0.24f;
        private CharacterBody body;
        private Transform muzzle;
        private float nextShotTime;
        private float nextBurstShotTime;
        private float nextVoiceTime;
        private float nextTargetScanTime;
        private int shotsRemainingInBurst;
        private float assignedPreferredRange;
        private CharacterBody cachedTarget;
        private bool previouslyHadTarget;
        private ImperialTrooperAnimationPlayer animationPlayer;

        internal void Configure(Transform muzzleTransform) { muzzle = null; }

        private void Awake()
        {
            body = GetComponent<CharacterBody>();
            // Resolve from this spawned body's actual model hierarchy. The muzzle captured while
            // constructing a prefab is not a reliable runtime reference after RoR2 clones the body.
            muzzle = ResolveLiveMuzzle();
            CacheAnimationPlayer();
            assignedPreferredRange = PreferredRange + UnityEngine.Random.Range(-0.6f, 0.6f);
            nextShotTime = Time.time + UnityEngine.Random.Range(0.7f, 1.6f);
            nextBurstShotTime = nextShotTime;
            nextVoiceTime = Time.time + UnityEngine.Random.Range(1.2f, 2.6f);
            SilenceCopiedSkillStocks();

            if (muzzle == null)
                ImperialTrooperPlugin.LogSource?.LogError("Spawned trooper " + GetInstanceID() + " has no live Muzzle transform; firing is disabled for this instance.");
            else
                ImperialTrooperPlugin.LogSource?.LogInfo("Bound spawned trooper " + GetInstanceID() + " to live muzzle '" +
                    muzzle.name + "' at body-local position " + transform.InverseTransformPoint(muzzle.position).ToString("F2") + ".");
        }

        private Transform ResolveLiveMuzzle()
        {
            Transform root = null;
            ModelLocator locator = GetComponent<ModelLocator>();
            if (locator != null) root = locator.modelTransform;
            if (root == null)
            {
                CharacterModel characterModel = GetComponentInChildren<CharacterModel>(true);
                if (characterModel != null) root = characterModel.transform;
            }

            if (root != null)
            {
                Transform[] liveTransforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < liveTransforms.Length; i++)
                {
                    Transform candidate = liveTransforms[i];
                    if (candidate != null && string.Equals(candidate.name, "Muzzle", StringComparison.Ordinal))
                        return candidate;
                }
            }

            Transform[] bodyTransforms = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < bodyTransforms.Length; i++)
            {
                Transform candidate = bodyTransforms[i];
                if (candidate != null && string.Equals(candidate.name, "Muzzle", StringComparison.Ordinal))
                    return candidate;
            }
            return null;
        }

        private void CacheAnimationPlayer()
        {
            if (animationPlayer != null) return;
            Transform root = GetComponent<ModelLocator>()?.modelTransform;
            if (root != null) animationPlayer = root.GetComponentInChildren<ImperialTrooperAnimationPlayer>(true);
        }

        private ImperialTrooperAnimationPlayer GetAnimationPlayer()
        {
            if (animationPlayer == null) CacheAnimationPlayer();
            return animationPlayer;
        }

        private void SilenceCopiedSkillStocks()
        {
            foreach (GenericSkill skill in GetComponentsInChildren<GenericSkill>(true))
            {
                if (skill == null) continue;
                skill.stock = 0;
                skill.enabled = false;
            }

            // Disable copied Commando roam/strafing drivers; the custom brain supplies movement vectors.
            int disabledDrivers = 0;
            foreach (Component component in GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "AISkillDriver") continue;
                Behaviour behaviour = component as Behaviour;
                if (behaviour != null && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    disabledDrivers++;
                }
            }
            ImperialTrooperPlugin.LogSource?.LogInfo("Disabled " + disabledDrivers + " inherited AISkillDriver components; tactical positioning is active.");
        }

        private void SetMoveVector(Vector3 direction)
        {
            // BaseAI + AISkillDriver own character movement. Do not write to the same moveVector.
            GetAnimationPlayer()?.SetMovement(false);
        }

        private void UpdateTacticalMovement(InputBankTest input, Vector3 toTarget, float planarDistance, Vector3 origin, Vector3 targetPoint)
        {
            // The driver ranges decide whether to retreat, hold, or advance. Only pass that intent to
            // animation; direct InputBankTest writes here used to fight BaseAI and create random turns.
            if (input == null) return;
            GetAnimationPlayer()?.SetMovement(planarDistance < 12f || planarDistance > 17f);
        }

        private void FixedUpdate()
        {
            if (!NetworkServer.active || body == null || body.healthComponent == null || !body.healthComponent.alive) return;
            if (Time.time >= nextTargetScanTime || cachedTarget == null || cachedTarget.healthComponent == null || !cachedTarget.healthComponent.alive)
            {
                cachedTarget = FindTarget();
                nextTargetScanTime = Time.time + 0.22f;
            }
            CharacterBody target = cachedTarget;
            if (target == null || IsPlayerCloaked(target))
            {
                GetAnimationPlayer()?.SetCombat(false);
                SetMoveVector(Vector3.zero);
                shotsRemainingInBurst = 0;
                cachedTarget = null;
                previouslyHadTarget = false;
                return;
            }
            if ((target.corePosition - body.corePosition).sqrMagnitude > DetectionRange * DetectionRange)
            {
                GetAnimationPlayer()?.SetCombat(false);
                SetMoveVector(Vector3.zero);
                shotsRemainingInBurst = 0;
                cachedTarget = null;
                previouslyHadTarget = false;
                return;
            }

            GetAnimationPlayer()?.SetCombat(true);
            if (muzzle == null || !muzzle.gameObject.activeInHierarchy)
                muzzle = ResolveLiveMuzzle();
            if (muzzle == null)
            {
                SetMoveVector(Vector3.zero);
                return;
            }

            Vector3 origin = muzzle.position;
            Vector3 targetPoint = target.corePosition + Vector3.up * 0.15f;
            Vector3 aim = targetPoint - origin;
            if (aim.sqrMagnitude < 0.01f) return;

            InputBankTest input = GetComponent<InputBankTest>();
            float planarDistance = Vector3.Distance(new Vector3(body.corePosition.x, 0f, body.corePosition.z),
                new Vector3(target.corePosition.x, 0f, target.corePosition.z));
            if (input != null)
            {
                input.aimDirection = aim.normalized;
                UpdateTacticalMovement(input, target.corePosition - body.corePosition, planarDistance, origin, targetPoint);
                Vector3 planarAim = new Vector3(aim.x, 0f, aim.z);
                CharacterDirection characterDirection = body.characterDirection;
                if (characterDirection != null && planarAim.sqrMagnitude > 0.001f)
                    characterDirection.forward = Vector3.Slerp(characterDirection.forward, planarAim.normalized,
                        Mathf.Clamp01(Time.fixedDeltaTime * 5f)).normalized;
                GetAnimationPlayer()?.SetAimDirection(aim.normalized);
            }

            if (Time.time > nextVoiceTime)
            {
                ImperialTrooperPlugin.PlayRadioChatter(body.corePosition, !previouslyHadTarget);
                nextVoiceTime = Time.time + UnityEngine.Random.Range(6f, 10f);
            }
            previouslyHadTarget = true;

            // Three controlled blaster shots followed by a brief reset, rather than isolated random shots.
            if (shotsRemainingInBurst <= 0 && Time.time >= nextShotTime)
            {
                shotsRemainingInBurst = Mathf.Clamp(BurstShotCount, 1, 5);
                nextBurstShotTime = Time.time;
            }
            if (shotsRemainingInBurst > 0 && Time.time >= nextBurstShotTime)
            {
                bool firstShotInBurst = shotsRemainingInBurst == Mathf.Clamp(BurstShotCount, 1, 5);
                FireBlaster(origin, aim.normalized, firstShotInBurst);
                shotsRemainingInBurst--;
                if (shotsRemainingInBurst > 0)
                    nextBurstShotTime = Time.time + Mathf.Max(0.12f, BurstShotInterval);
                else
                    nextShotTime = Time.time + FireInterval / Mathf.Max(0.75f, body.attackSpeed) * UnityEngine.Random.Range(0.9f, 1.12f);
            }
        }

        private void OnDestroy()
        {
            if (NetworkServer.active && body != null && body.healthComponent != null && !body.healthComponent.alive)
                ImperialTrooperPlugin.PlayClip(ImperialTrooperPlugin.RadioTrooperDownClip, body.corePosition, 0.85f);
        }

        private static bool HasBuffNamed(CharacterBody candidate, string buffName)
        {
            if (candidate == null) return false;
            BuffIndex index = BuffCatalog.FindBuffIndex(buffName);
            return index != BuffIndex.None && candidate.HasBuff(index);
        }

        private static bool IsPlayerCloaked(CharacterBody candidate)
        {
            // RoR2 can represent stealth from equipment, items and survivor-specific abilities with
            // different buff names. Don't reveal a hidden player just because they're the nearest body.
            return HasBuffNamed(candidate, "bdCloak") ||
                   HasBuffNamed(candidate, "bdCloakSpeed") ||
                   HasBuffNamed(candidate, "bdCloakStalker") ||
                   HasBuffNamed(candidate, "bdCloakPermanent") ||
                   HasBuffNamed(candidate, "bdStealthMode");
        }

        private CharacterBody FindTarget()
        {
            CharacterBody best = null;
            float bestSqr = DetectionRange * DetectionRange;
            var bodies = CharacterBody.readOnlyInstancesList;
            for (int i=0; i<bodies.Count; i++)
            {
                CharacterBody candidate = bodies[i];
                if (candidate == null || candidate == body || candidate.healthComponent == null || !candidate.healthComponent.alive) continue;
                TeamComponent tc = candidate.teamComponent;
                if (tc == null || tc.teamIndex != TeamIndex.Player) continue;
                if (IsPlayerCloaked(candidate)) continue;
                float sqr = (candidate.corePosition - body.corePosition).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = candidate; }
            }
            return best;
        }

        private void FireBlaster(Vector3 origin, Vector3 direction, bool playBurstSound)
        {
            if (body == null || !body || body.healthComponent == null || !body.healthComponent.alive) return;
            if (ImperialTrooperPlugin.BlasterBoltPrefab == null) return;
            if (playBurstSound) GetAnimationPlayer()?.PlayShot();

            Quaternion aimRotation = Quaternion.LookRotation(direction);
            Vector3 shotDirection = aimRotation * Quaternion.Euler(
                UnityEngine.Random.Range(-SpreadDegrees, SpreadDegrees),
                UnityEngine.Random.Range(-SpreadDegrees, SpreadDegrees), 0f) * Vector3.forward;
            float shotDamage = body.damage * 0.75f;
            bool critical = Util.CheckRoll(body.crit * 0.25f, body.master);
            FireProjectileInfo info = new FireProjectileInfo
            {
                projectilePrefab = ImperialTrooperPlugin.BlasterBoltPrefab,
                position = origin,
                rotation = Quaternion.LookRotation(shotDirection),
                owner = gameObject,
                damage = shotDamage,
                force = 10f,
                crit = critical,
                damageColorIndex = DamageColorIndex.Default,
                useSpeedOverride = true,
                speedOverride = ImperialTrooperPlugin.BlasterBoltSpeed
            };
            ProjectileManager.instance.FireProjectile(info);

            // The burst is already three separately simulated projectiles. Play the actual CC0-style
            // blaster shot once per bolt; do not substitute the old mechanical "burst" placeholder.
            if (ImperialTrooperPlugin.BlasterShotClip != null)
                ImperialTrooperPlugin.PlayClip(ImperialTrooperPlugin.BlasterShotClip, origin, 0.92f);
        }
    }
}
