using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

public sealed class MosaicSuppressor : MonoBehaviour
{
    private const float ScanIntervalSeconds = 0.5f;

    private float _nextScanTime;
    private readonly HashSet<int> _detectedSpineMosaics = new();
    private readonly HashSet<int> _currentSpineMosaicIds = new();
    private readonly Dictionary<int, SpineMosaic> _activeSpineMosaics = new();
    private readonly List<int> _removedSpineMosaicIds = new();
    private readonly Dictionary<int, Material> _transparentOverlayMaterials = new();
    private Camera.CameraCallback _preRenderCallback;
    private Texture2D _transparentTexture;

    public MosaicSuppressor(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Awake()
    {
        _preRenderCallback = (Camera.CameraCallback)(Action<Camera>)ApplyTransparentOverlaysBeforeRender;
        Camera.onPreRender += _preRenderCallback;
    }

    public void OnDestroy()
    {
        if (_preRenderCallback != null)
            Camera.onPreRender -= _preRenderCallback;
    }

    public void Update()
    {
        if (!Core.Plugin.Settings.EnableSpineMosaicReplacement.Value ||
            Time.unscaledTime < _nextScanTime)
            return;

        _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;
        ApplyToLoadedSpineMosaics();
    }

    // SpineMosaic initializes from native Unity callbacks, which do not reliably reach Harmony on this IL2CPP build.
    private void ApplyToLoadedSpineMosaics()
    {
        _currentSpineMosaicIds.Clear();
        foreach (var mosaic in Resources.FindObjectsOfTypeAll<SpineMosaic>())
        {
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            var instanceId = mosaic.GetInstanceID();
            _currentSpineMosaicIds.Add(instanceId);
            _activeSpineMosaics[instanceId] = mosaic;

            if (_detectedSpineMosaics.Add(instanceId))
            {
                var objectPath = GetObjectPath(mosaic.gameObject);
                Core.Plugin.Log.LogInfo(
                    $"Detected SpineMosaic on {objectPath} " +
                    $"(children: {mosaic.transform.childCount})");
            }
        }

        _removedSpineMosaicIds.Clear();
        foreach (var instanceId in _activeSpineMosaics.Keys)
            if (!_currentSpineMosaicIds.Contains(instanceId))
                _removedSpineMosaicIds.Add(instanceId);
        foreach (var instanceId in _removedSpineMosaicIds)
            _activeSpineMosaics.Remove(instanceId);
    }

    // This runs after game Update/LateUpdate and immediately before each camera renders.
    // Spine may rebuild its material array during its own updates, so the replacement must happen here.
    private void ApplyTransparentOverlaysBeforeRender(Camera _)
    {
        if (!Core.Plugin.Settings.EnableSpineMosaicReplacement.Value)
            return;

        foreach (var mosaic in _activeSpineMosaics.Values)
        {
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            ReplaceRuntimeMosaicMaterials(mosaic);
        }
    }

    private void ReplaceRuntimeMosaicMaterials(SpineMosaic mosaic)
    {
        try
        {
            foreach (var renderer in mosaic.gameObject.GetComponentsInChildren<Renderer>(includeInactive: true))
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
        if (_transparentOverlayMaterials.TryGetValue(materialKey, out var existingMaterial))
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

    private static string GetObjectPath(GameObject gameObject)
    {
        var names = new List<string>();
        for (var transform = gameObject.transform; transform != null; transform = transform.parent)
            names.Add(transform.name);

        names.Reverse();
        return string.Join("/", names);
    }
}
