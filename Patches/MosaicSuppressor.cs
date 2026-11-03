using System;
using System.Collections.Generic;
using MonsterMusumeTDMod.Services;
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

public sealed class MosaicSuppressor : MonoBehaviour
{
    private const float ActiveScanIntervalSeconds = 0.5f;
    private const float IdleScanIntervalSeconds = 1.5f;

    private static MosaicSuppressor _instance;

    private float _nextScanTime;
    private int _lastRenderFrame = -1;
    private bool _hasActiveR18Character;
    private readonly HashSet<int> _detectedSpineMosaics = new();
    private readonly HashSet<int> _currentSpineMosaicIds = new();
    private readonly Dictionary<int, SpineMosaic> _activeSpineMosaics = new();
    private readonly Dictionary<int, (int ChildCount, Renderer[] Renderers)> _cachedRenderers = new();
    private readonly List<int> _removedSpineMosaicIds = new();
    private readonly Dictionary<int, Material> _transparentOverlayMaterials = new();
    private Camera.CameraCallback _preRenderCallback;
    private Texture2D _transparentTexture;

    public MosaicSuppressor(IntPtr pointer)
        : base(pointer)
    {
    }

    public static bool HasActiveR18Character()
    {
        if (_instance != null)
        {
            if (Time.unscaledTime >= _instance._nextScanTime)
            {
                _instance.ApplyToLoadedSpineMosaics();
                var interval = _instance._activeSpineMosaics.Count > 0
                    ? ActiveScanIntervalSeconds
                    : IdleScanIntervalSeconds;
                _instance._nextScanTime = Time.unscaledTime + interval;
            }
            return _instance._hasActiveR18Character;
        }

        return ScanForActiveR18CharacterFallback();
    }

    public void Awake()
    {
        _instance = this;
        _preRenderCallback = (Camera.CameraCallback)(Action<Camera>)ApplyTransparentOverlaysBeforeRender;
        _nextScanTime = Time.unscaledTime + 1.2f;
        Camera.onPreRender += _preRenderCallback;
    }

    public void OnDestroy()
    {
        if (_instance == this)
            _instance = null;

        if (_preRenderCallback != null)
            Camera.onPreRender -= _preRenderCallback;

        foreach (var material in _transparentOverlayMaterials.Values)
            if (material != null)
                UnityEngine.Object.Destroy(material);
        _transparentOverlayMaterials.Clear();
        _cachedRenderers.Clear();

        if (_transparentTexture != null)
        {
            UnityEngine.Object.Destroy(_transparentTexture);
            _transparentTexture = null;
        }
    }

    public void Update()
    {
        if (Time.unscaledTime < _nextScanTime)
            return;

        ApplyToLoadedSpineMosaics();
        var interval = _activeSpineMosaics.Count > 0
            ? ActiveScanIntervalSeconds
            : IdleScanIntervalSeconds;
        _nextScanTime = Time.unscaledTime + interval;
    }

    // SpineMosaic initializes from native Unity callbacks, which do not reliably reach Harmony on this IL2CPP build.
    private void ApplyToLoadedSpineMosaics()
    {
        _currentSpineMosaicIds.Clear();
        var foundR18 = false;

#if ANDROID
        var mosaics = Resources.FindObjectsOfTypeAll<SpineMosaic>();
#else
        var mosaics = UnityEngine.Object.FindObjectsByType<SpineMosaic>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var mosaic in mosaics)
        {
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            var instanceId = mosaic.GetInstanceID();
            _currentSpineMosaicIds.Add(instanceId);
            _activeSpineMosaics[instanceId] = mosaic;

            var objectPath = UiHierarchyClassifier.GetHierarchyPath(mosaic.transform);
            if (!foundR18 &&
                (objectPath.Contains("/layer_R18/", StringComparison.OrdinalIgnoreCase) ||
                 objectPath.Contains("/r18_scenes/", StringComparison.OrdinalIgnoreCase)))
            {
                foundR18 = true;
            }

            if (_detectedSpineMosaics.Add(instanceId))
            {
                Core.Plugin.Log.LogInfo(
                    $"Detected SpineMosaic on {objectPath} " +
                    $"(children: {mosaic.transform.childCount})");
            }
        }

        _hasActiveR18Character = foundR18;

        _removedSpineMosaicIds.Clear();
        foreach (var instanceId in _activeSpineMosaics.Keys)
            if (!_currentSpineMosaicIds.Contains(instanceId))
                _removedSpineMosaicIds.Add(instanceId);
        foreach (var instanceId in _removedSpineMosaicIds)
        {
            _activeSpineMosaics.Remove(instanceId);
            _cachedRenderers.Remove(instanceId);
        }
    }

    // This runs after game Update/LateUpdate and immediately before each camera renders.
    // Spine may rebuild its material array during its own updates, so the replacement must happen here.
    // Guarded by frameCount so multi-camera scenes only run the material check once per frame.
    private void ApplyTransparentOverlaysBeforeRender(Camera _)
    {
        if (_lastRenderFrame == Time.frameCount ||
            _activeSpineMosaics.Count == 0 ||
            !Core.Plugin.Settings.EnableSpineMosaicReplacement.Value)
            return;

        _lastRenderFrame = Time.frameCount;

        foreach (var pair in _activeSpineMosaics)
        {
            var mosaic = pair.Value;
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            ReplaceRuntimeMosaicMaterials(pair.Key, mosaic);
        }
    }

    private void ReplaceRuntimeMosaicMaterials(int instanceId, SpineMosaic mosaic)
    {
        try
        {
            var childCount = mosaic.transform.childCount;
            if (!_cachedRenderers.TryGetValue(instanceId, out var cached) ||
                cached.ChildCount != childCount ||
                cached.Renderers == null)
            {
                var il2cppRenderers = mosaic.gameObject.GetComponentsInChildren<Renderer>(includeInactive: true);
                var managedRenderers = new Renderer[il2cppRenderers.Length];
                for (var i = 0; i < il2cppRenderers.Length; i++)
                    managedRenderers[i] = il2cppRenderers[i];
                cached = (childCount, managedRenderers);
                _cachedRenderers[instanceId] = cached;
            }

            foreach (var renderer in cached.Renderers)
            {
                if (renderer == null)
                    continue;

                var materials = renderer.sharedMaterials;
                Material baseMaterial = null;
                foreach (var candidate in materials)
                {
                    if (candidate != null && candidate.shader != null && candidate.shader.name == "Spine/Skeleton Tint")
                    {
                        baseMaterial = candidate;
                        break;
                    }
                }

                if (baseMaterial == null)
                    continue;

                var changed = false;
                for (var index = 0; index < materials.Length; index++)
                {
                    var material = materials[index];
                    if (material == null || material.shader == null || material.shader.name != "Exzeal/Spine/SpineMosaic")
                        continue;

                    materials[index] = GetTransparentOverlayMaterial(baseMaterial);
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = materials;
            }
        }
        catch (Exception exception)
        {
            Core.Plugin.Log.LogWarning(
                $"Failed to replace runtime SpineMosaic material: {exception.Message}");
        }
    }

    private Material GetTransparentOverlayMaterial(Material baseMaterial)
    {
        var materialKey = baseMaterial.GetInstanceID();
        if (_transparentOverlayMaterials.TryGetValue(materialKey, out var existingMaterial) && existingMaterial != null)
            return existingMaterial;

        var transparentOverlay = new Material(baseMaterial)
        {
            name = $"{baseMaterial.name}_Transparent"
        };
        transparentOverlay.mainTexture = GetTransparentTexture();
        transparentOverlay.color = Color.clear;
        _transparentOverlayMaterials[materialKey] = transparentOverlay;
        return transparentOverlay;
    }

    private Texture2D GetTransparentTexture()
    {
        if (_transparentTexture != null)
            return _transparentTexture;

        _transparentTexture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
        {
            name = "MonsterMusumeTDMod_TransparentOverlay"
        };
        _transparentTexture.SetPixel(0, 0, new Color(1f, 1f, 1f, 0f));
        _transparentTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return _transparentTexture;
    }

    private static bool ScanForActiveR18CharacterFallback()
    {
#if ANDROID
        var mosaics = Resources.FindObjectsOfTypeAll<SpineMosaic>();
#else
        var mosaics = UnityEngine.Object.FindObjectsByType<SpineMosaic>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var mosaic in mosaics)
        {
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            var path = UiHierarchyClassifier.GetHierarchyPath(mosaic.transform);
            if (path.Contains("/layer_R18/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/r18_scenes/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
