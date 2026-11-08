using System;
using System.Collections.Generic;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Services;

[Flags]
public enum UiPathFlags : ushort
{
    None = 0,
    UnderUiCanvas = 1 << 0,
    SubSkillScrollText = 1 << 1,
    HomeDeckSubSkillName = 1 << 2,
    HomeDeckExSkillSlotName = 1 << 3,
    QuestDungeonSkillName = 1 << 4,
    ShouldUseSubSkillPresentation = 1 << 5,
    UnitDetailUiDescription = 1 << 6,
    UnitDetailFontOverride = 1 << 7,
    UnitDetailNoOutline = 1 << 8,
    UnitDetailFixedSpacing = 1 << 9,
    CommonSortFilter = 1 << 10,
    ShopOrPopupLayout = 1 << 11,
    HasCanvasAncestor = 1 << 12,
    InternalUnderlayLayer = 1 << 13
}

/// <summary>
/// Centralizes Transform hierarchy path caching and precomputed bitmask classification
/// so hot scroll/switch paths perform zero IL2CPP string allocations or substring scans on cache hits.
/// </summary>
public static class UiHierarchyClassifier
{
    private const int GenerationCapacity = 2048;

    private static Dictionary<int, CachedHierarchyPath> _primaryCache = new(GenerationCapacity);
    private static Dictionary<int, CachedHierarchyPath> _secondaryCache = new(GenerationCapacity);

    // Track internal underlay/outline GameObject & Component instance IDs so
    // IsUiTextSoftOutlineLayer never calls gameObject.name or GetFlags across IL2CPP.
    private static readonly HashSet<int> InternalLayerIds = new();
    private static readonly HashSet<int> NonInternalLayerIds = new(GenerationCapacity * 2);

    // Active UICanvas / ExtraCanvas root Transforms in the scene so periodic UI discovery
    // only traverses live scene UI hierarchies instead of calling Resources.FindObjectsOfTypeAll<TMP_Text>().
    private static readonly Dictionary<int, Transform> KnownUiCanvasRoots = new();
    private static readonly List<int> StaleCanvasIds = new();
    private static float _nextCanvasRootProbeTime;

    private readonly struct CachedHierarchyPath
    {
        public CachedHierarchyPath(
            string fullPath,
            string normalizedPath,
            UiPathFlags flags,
            int validatedFrame,
            Transform immediateParent,
            Transform hierarchyRoot,
            Transform rootChildNode,
            Transform trackedNode,
            Transform expectedTrackedParent)
        {
            FullPath = fullPath;
            NormalizedPath = normalizedPath;
            Flags = flags;
            ValidatedFrame = validatedFrame;
            ImmediateParent = immediateParent;
            HierarchyRoot = hierarchyRoot;
            RootChildNode = rootChildNode;
            TrackedNode = trackedNode;
            ExpectedTrackedParent = expectedTrackedParent;
        }

        public string FullPath { get; }
        public string NormalizedPath { get; }
        public UiPathFlags Flags { get; }
        public int ValidatedFrame { get; }
        public Transform ImmediateParent { get; }
        public Transform HierarchyRoot { get; }
        public Transform RootChildNode { get; }
        public Transform TrackedNode { get; }
        public Transform ExpectedTrackedParent { get; }

        public CachedHierarchyPath WithValidatedFrame(int frame) =>
            new(FullPath, NormalizedPath, Flags, frame, ImmediateParent, HierarchyRoot, RootChildNode, TrackedNode, ExpectedTrackedParent);
    }

    public static void RegisterInternalLayer(GameObject layerObject, Component layerComponent)
    {
        if (layerObject != null)
        {
            var goId = layerObject.GetInstanceID();
            InternalLayerIds.Add(goId);
            NonInternalLayerIds.Remove(goId);
            if (layerObject.transform != null)
            {
                var trId = layerObject.transform.GetInstanceID();
                InternalLayerIds.Add(trId);
                NonInternalLayerIds.Remove(trId);
            }
        }
        if (layerComponent != null)
        {
            var compId = layerComponent.GetInstanceID();
            InternalLayerIds.Add(compId);
            NonInternalLayerIds.Remove(compId);
            if (layerComponent.transform != null)
            {
                var trId = layerComponent.transform.GetInstanceID();
                InternalLayerIds.Add(trId);
                NonInternalLayerIds.Remove(trId);
            }
        }
    }

    public static void UnregisterInternalLayer(GameObject layerObject, Component layerComponent)
    {
        if (layerObject != null)
        {
            InternalLayerIds.Remove(layerObject.GetInstanceID());
            if (layerObject.transform != null)
                InternalLayerIds.Remove(layerObject.transform.GetInstanceID());
        }
        if (layerComponent != null)
        {
            InternalLayerIds.Remove(layerComponent.GetInstanceID());
            if (layerComponent.transform != null)
                InternalLayerIds.Remove(layerComponent.transform.GetInstanceID());
        }
    }

    private static void RegisterUiCanvasRoot(Transform canvasTransform)
    {
        if (canvasTransform == null)
            return;
        KnownUiCanvasRoots[canvasTransform.GetInstanceID()] = canvasTransform;
    }

    public static void CollectUiCanvasTexts(List<TMP_Text> buffer)
    {
        if (buffer == null)
            return;

        buffer.Clear();
        StaleCanvasIds.Clear();
        foreach (var pair in KnownUiCanvasRoots)
        {
            var root = pair.Value;
            if (root == null || root.gameObject == null || !root.gameObject.activeInHierarchy)
                StaleCanvasIds.Add(pair.Key);
        }
        for (var i = 0; i < StaleCanvasIds.Count; i++)
            KnownUiCanvasRoots.Remove(StaleCanvasIds[i]);

        var now = Time.unscaledTime;
        if (KnownUiCanvasRoots.Count == 0 || now >= _nextCanvasRootProbeTime)
        {
            _nextCanvasRootProbeTime = now + 10f;
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null || canvas.gameObject == null || !canvas.gameObject.activeInHierarchy)
                    continue;
                var name = canvas.name;
                if (!string.IsNullOrEmpty(name) &&
                    (name.StartsWith("UICanvas", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "ExtraCanvas", StringComparison.OrdinalIgnoreCase)))
                {
                    RegisterUiCanvasRoot(canvas.transform);
                }
            }
        }

        foreach (var root in KnownUiCanvasRoots.Values)
        {
            if (root == null || root.gameObject == null || !root.gameObject.activeInHierarchy)
                continue;
            var texts = root.GetComponentsInChildren<TMP_Text>(false);
            if (texts == null)
                continue;
            for (var i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text != null)
                    buffer.Add(text);
            }
        }
    }

    public static bool IsKnownInternalLayerId(int instanceId) =>
        InternalLayerIds.Count > 0 && InternalLayerIds.Contains(instanceId);

    public static bool IsUiTextSoftOutlineLayer(object component)
    {
        if (InternalLayerIds.Count == 0 || component is not UnityEngine.Object unityObj || unityObj == null)
            return false;

        var id = unityObj.GetInstanceID();
        if (InternalLayerIds.Contains(id))
            return true;
        if (NonInternalLayerIds.Contains(id))
            return false;

        var name = unityObj.name;
        if (!string.IsNullOrEmpty(name) &&
            name.IndexOf("__MMTDUnderlay", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            InternalLayerIds.Add(id);
            return true;
        }

        if (NonInternalLayerIds.Count >= GenerationCapacity * 2)
            NonInternalLayerIds.Clear();
        NonInternalLayerIds.Add(id);
        return false;
    }

    public static string GetHierarchyPath(object component) =>
        component switch
        {
            Transform transform => GetHierarchyPath(transform),
            Component unityComponent when unityComponent != null => GetHierarchyPath(unityComponent.transform),
            GameObject gameObject when gameObject != null => GetHierarchyPath(gameObject.transform),
            _ => string.Empty
        };

    public static string GetHierarchyPath(Transform transform) =>
        GetOrCreateCachedPath(transform).FullPath ?? string.Empty;

    public static string GetNormalizedHierarchyPath(object component) =>
        component switch
        {
            Transform transform => GetNormalizedHierarchyPath(transform),
            Component unityComponent when unityComponent != null => GetNormalizedHierarchyPath(unityComponent.transform),
            GameObject gameObject when gameObject != null => GetNormalizedHierarchyPath(gameObject.transform),
            _ => string.Empty
        };

    public static string GetNormalizedHierarchyPath(Transform transform) =>
        GetOrCreateCachedPath(transform).NormalizedPath ?? string.Empty;

    public static UiPathFlags GetFlags(object component) =>
        component switch
        {
            Transform transform => GetOrCreateCachedPath(transform).Flags,
            Component unityComponent when unityComponent != null => GetOrCreateCachedPath(unityComponent.transform).Flags,
            GameObject gameObject when gameObject != null => GetOrCreateCachedPath(gameObject.transform).Flags,
            _ => UiPathFlags.None
        };

    public static UiPathFlags GetFlags(Transform transform) =>
        GetOrCreateCachedPath(transform).Flags;

    public static string GetHierarchyPathAndFlags(object component, out UiPathFlags flags)
    {
        var entry = component switch
        {
            Transform transform => GetOrCreateCachedPath(transform),
            Component unityComponent when unityComponent != null => GetOrCreateCachedPath(unityComponent.transform),
            GameObject gameObject when gameObject != null => GetOrCreateCachedPath(gameObject.transform),
            _ => default
        };
        flags = entry.Flags;
        return entry.FullPath ?? string.Empty;
    }

    public static string GetHierarchyPathAndFlags(
        object component,
        out string normalizedPath,
        out UiPathFlags flags)
    {
        var entry = component switch
        {
            Transform transform => GetOrCreateCachedPath(transform),
            Component unityComponent when unityComponent != null => GetOrCreateCachedPath(unityComponent.transform),
            GameObject gameObject when gameObject != null => GetOrCreateCachedPath(gameObject.transform),
            _ => default
        };
        normalizedPath = entry.NormalizedPath ?? string.Empty;
        flags = entry.Flags;
        return entry.FullPath ?? string.Empty;
    }

    private static bool IsValidCachedEntry(in CachedHierarchyPath cached, Transform transform)
    {
        if (transform == null)
            return false;

        var isUnderUiCanvas = (cached.Flags & UiPathFlags.UnderUiCanvas) != 0;
        if (!isUnderUiCanvas && cached.ValidatedFrame != Time.frameCount)
            return false;

        if (transform.parent != cached.ImmediateParent)
            return false;

        // A detached prefab can be parented while every descendant keeps its
        // immediate/tracked parent. Its former top-level root must stay unparented.
        if (cached.HierarchyRoot != null && cached.HierarchyRoot.parent != null)
            return false;

        // If the hierarchy root was a staging container like WindowPageStockObject,
        // its direct child must still be parented to that root.
        if (cached.RootChildNode != null && cached.RootChildNode.parent != cached.HierarchyRoot)
            return false;

        if (cached.TrackedNode != null && cached.TrackedNode.parent != cached.ExpectedTrackedParent)
            return false;

        return true;
    }

    private static bool TryGetValidCachedPath(int instanceId, Transform transform, out CachedHierarchyPath cached)
    {
        var currentFrame = Time.frameCount;
        if (_primaryCache.TryGetValue(instanceId, out cached))
        {
            if (IsValidCachedEntry(in cached, transform))
            {
                if (cached.ValidatedFrame != currentFrame)
                {
                    cached = cached.WithValidatedFrame(currentFrame);
                    _primaryCache[instanceId] = cached;
                }
                return true;
            }
            _primaryCache.Remove(instanceId);
        }
        else if (_secondaryCache.TryGetValue(instanceId, out cached))
        {
            if (IsValidCachedEntry(in cached, transform))
            {
                if (cached.ValidatedFrame != currentFrame)
                    cached = cached.WithValidatedFrame(currentFrame);
                PromoteOrInsert(instanceId, cached);
                return true;
            }
            _secondaryCache.Remove(instanceId);
        }
        cached = default;
        return false;
    }

    private static string StripCloneSuffix(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) < 0)
            return name ?? string.Empty;
        return name.Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanvasOrAdvRootName(string name) =>
        !string.IsNullOrEmpty(name) &&
        (string.Equals(name, "Canvas", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("UICanvas", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "ExtraCanvas", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "AdvEngine", StringComparison.OrdinalIgnoreCase));

    private static bool IsMovableWindowContainerName(string parentName) =>
        string.Equals(parentName, "WindowPageStockObject", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parentName, "Page", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parentName, "WindowState", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parentName, "CanvasManagerContent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parentName, "Content", StringComparison.OrdinalIgnoreCase);

    private static CachedHierarchyPath GetOrCreateCachedPath(Transform transform)
    {
        if (transform == null)
            return default;

        var instanceId = transform.GetInstanceID();
        if (TryGetValidCachedPath(instanceId, transform, out var cached))
            return cached;

        try
        {
            var chain = new List<(int Id, string Name, Transform Node)>(16);
            CachedHierarchyPath baseCached = default;
            var hasBaseCached = false;

            for (var current = transform; current != null; current = current.parent)
            {
                var currentId = current.GetInstanceID();
                // Never reuse a cached ancestor unless that ancestor is already under UICanvas;
                // otherwise a pooled window moved out of WindowPageStockObject would inherit
                // its stale WindowPageStockObject prefix from an intermediate ancestor.
                if (current != transform &&
                    TryGetValidCachedPath(currentId, current, out var ancestorCached) &&
                    (ancestorCached.Flags & UiPathFlags.UnderUiCanvas) != 0)
                {
                    baseCached = ancestorCached;
                    hasBaseCached = true;
                    break;
                }

                var currentName = current.name ?? string.Empty;
                chain.Add((currentId, currentName, current));
                if (currentName.StartsWith("UICanvas", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(currentName, "ExtraCanvas", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterUiCanvasRoot(current);
                }
            }

            var currentFrame = Time.frameCount;
            var fullPath = hasBaseCached ? baseCached.FullPath : string.Empty;
            var normalizedPath = hasBaseCached ? baseCached.NormalizedPath : string.Empty;
            var hierarchyRoot = hasBaseCached
                ? baseCached.HierarchyRoot
                : (chain.Count > 0 ? chain[^1].Node : null);
            var rootChildNode = hasBaseCached
                ? baseCached.RootChildNode
                : (chain.Count >= 2 ? chain[^2].Node : null);
            var trackedNode = hasBaseCached
                ? baseCached.TrackedNode
                : (chain.Count > 0 ? chain[^1].Node : null);
            var expectedTrackedParent = hasBaseCached
                ? baseCached.ExpectedTrackedParent
                : trackedNode?.parent;
            CachedHierarchyPath result = default;

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var item = chain[i];
                var parentNode = item.Node.parent;
                var parentName = parentNode != null ? parentNode.name : null;
                var isDirectChildOfStagingRoot =
                    !hasBaseCached &&
                    i == chain.Count - 2 &&
                    !IsCanvasOrAdvRootName(chain[^1].Name);
                if (isDirectChildOfStagingRoot ||
                    (!string.IsNullOrEmpty(parentName) && IsMovableWindowContainerName(parentName)))
                {
                    trackedNode = item.Node;
                    expectedTrackedParent = parentNode;
                }

                var normalizedLeaf = StripCloneSuffix(item.Name);
                fullPath = string.IsNullOrEmpty(fullPath) ? item.Name : string.Concat(fullPath, "/", item.Name);
                normalizedPath = string.IsNullOrEmpty(normalizedPath)
                    ? normalizedLeaf
                    : string.Concat(normalizedPath, "/", normalizedLeaf);
                var flags = ComputeFlags(fullPath, item.Name);
                result = new CachedHierarchyPath(
                    fullPath,
                    normalizedPath,
                    flags,
                    currentFrame,
                    parentNode,
                    hierarchyRoot,
                    rootChildNode,
                    trackedNode,
                    expectedTrackedParent);
                PromoteOrInsert(item.Id, result);
            }

            return result;
        }
        catch
        {
            return default;
        }
    }

    private static UiPathFlags ComputeFlags(string path, string nodeName)
    {
        if (string.IsNullOrEmpty(path))
            return UiPathFlags.None;

        var flags = UiPathFlags.None;

        if ((nodeName != null && nodeName.Contains("__MMTDUnderlay", StringComparison.OrdinalIgnoreCase)) ||
            path.Contains("__MMTDUnderlay", StringComparison.OrdinalIgnoreCase))
        {
            flags |= UiPathFlags.InternalUnderlayLayer;
        }

        if (path.Contains("Canvas", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase))
            flags |= UiPathFlags.HasCanvasAncestor;

        if (path.Contains("UICanvas", StringComparison.OrdinalIgnoreCase) ||
            (path.Contains("GameStartLoadingScreen", StringComparison.OrdinalIgnoreCase) &&
             path.Contains("ExtraCanvas", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= UiPathFlags.UnderUiCanvas | UiPathFlags.HasCanvasAncestor;
        }

        var isEquippedAttach = IsEquippedSubSkillAttachPath(path);
        var isEquippedSubSkillAttach =
            isEquippedAttach &&
            path.Contains("/AbilityRoot/Image", StringComparison.OrdinalIgnoreCase);

        if (isEquippedSubSkillAttach)
            flags |= UiPathFlags.HomeDeckSubSkillName;

        var isHomeDeckExSkill =
            (path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("Unit_DeckList", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("DeckUnitSlot", StringComparison.OrdinalIgnoreCase)) &&
            path.Contains("/ExSkillChangeButton/SkillName/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/SkillNameText", StringComparison.OrdinalIgnoreCase);
        if (isHomeDeckExSkill)
            flags |= UiPathFlags.HomeDeckExSkillSlotName;

        var isQuestDungeonSkill =
            (path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("Quest_Dungeon", StringComparison.OrdinalIgnoreCase)) &&
            path.Contains("/QuestContentDungeon", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/Back/FeaturedSkillName", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/Back/UniqueSkillName", StringComparison.OrdinalIgnoreCase));
        if (isQuestDungeonSkill)
            flags |= UiPathFlags.QuestDungeonSkillName;

        var isGenericSubSkillEntry =
            path.Contains("/Subskill", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "Name", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "Text", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "Description", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "SkillNameText", StringComparison.OrdinalIgnoreCase));

        var isAttachAbilityList =
            path.Contains("Unit_Detail_AttachAbility", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/WindowBase/ScrollView/", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/Content/Title/Text", StringComparison.OrdinalIgnoreCase);

        var isUnitDetailSkillDialog =
            path.Contains("Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SkillDialog/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillTextArea", StringComparison.OrdinalIgnoreCase);

        var isSubSkillCombineText =
            path.Contains("SubSkillCombine_Popup", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/CombineIcon/Name", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/CombineIcon/Title", StringComparison.OrdinalIgnoreCase) ||
             (path.Contains("/MaterialIconContent/", StringComparison.OrdinalIgnoreCase) &&
              path.EndsWith("/GrayoutObj/Title", StringComparison.OrdinalIgnoreCase)));

        var isSubSkillRecipeName =
            path.Contains("/ScrollView_SubSkillRecipe/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillRecipeBanner", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/SubSkillName_Text", StringComparison.OrdinalIgnoreCase);

        var isBattleResultAcquiredSkill =
            path.Contains("/UICanvasBattleResult/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Result_Summary_skip", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/AcquiredSkillSkip/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Ef_GetSkill", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/Skill/Name", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/Skill/Text", StringComparison.OrdinalIgnoreCase));

        var isSubSkillScroll =
            isGenericSubSkillEntry ||
            isAttachAbilityList ||
            isUnitDetailSkillDialog ||
            isSubSkillCombineText ||
            isSubSkillRecipeName ||
            isBattleResultAcquiredSkill ||
            isEquippedSubSkillAttach;

        if (isSubSkillScroll)
            flags |= UiPathFlags.SubSkillScrollText;

        if (isSubSkillScroll ||
            isQuestDungeonSkill ||
            isEquippedAttach ||
            ((path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) ||
              path.Contains("Unit_DeckList", StringComparison.OrdinalIgnoreCase) ||
              path.Contains("DeckUnitSlot", StringComparison.OrdinalIgnoreCase)) &&
             path.Contains("ExSkillChangeButton/SkillName/", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= UiPathFlags.ShouldUseSubSkillPresentation;
        }

        if (path.Contains("/Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
            (path.Contains("/ExSkillArea/ExSkillTextArea/", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/StatusDialog/UnitTribeTextArea/", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= UiPathFlags.UnitDetailUiDescription;
        }

        var isUnitDetailOverride = path.Contains("/UICanvasTopUnitDetail/", StringComparison.OrdinalIgnoreCase);
        if (isUnitDetailOverride)
            flags |= UiPathFlags.UnitDetailFontOverride;

        var isCompactDesc = IsUnitDetailCompactDescription(path);
        if (isCompactDesc)
            flags |= UiPathFlags.UnitDetailNoOutline;

        if (isCompactDesc ||
            path.Contains("/UnitTribeTextArea/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/UnitClassTextArea/", StringComparison.OrdinalIgnoreCase) ||
            (isUnitDetailOverride && path.EndsWith("/TextArea/Text", StringComparison.OrdinalIgnoreCase)) ||
            (path.Contains("/Unit_Detail_ExSkill", StringComparison.OrdinalIgnoreCase) &&
             path.EndsWith("/WindowBase/ExSkillChangeView/SkillView1/ExSkillTextArea/TextArea/Text", StringComparison.OrdinalIgnoreCase)) ||
            (path.Contains("/Bom_Detail", StringComparison.OrdinalIgnoreCase) &&
             path.EndsWith("/Main/UI/Bom_Detail_StatusView/Skill1Btn/TextArea/Text", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= UiPathFlags.UnitDetailFixedSpacing;
        }

        if (path.Contains("/Common_SortFilter", StringComparison.OrdinalIgnoreCase))
            flags |= UiPathFlags.CommonSortFilter;

        if ((path.Contains("Common_SimpleDetailPopup", StringComparison.OrdinalIgnoreCase) &&
             path.EndsWith("/Text_Explanation", StringComparison.OrdinalIgnoreCase)) ||
            (path.Contains("Home_Shop", StringComparison.OrdinalIgnoreCase) &&
             path.Contains("/Shop_SaleContent", StringComparison.OrdinalIgnoreCase) &&
             path.EndsWith("/Name", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= UiPathFlags.ShopOrPopupLayout;
        }

        return flags;
    }

    private static void PromoteOrInsert(int instanceId, CachedHierarchyPath entry)
    {
        if (_primaryCache.Count >= GenerationCapacity)
        {
            _secondaryCache.Clear();
            (_primaryCache, _secondaryCache) = (_secondaryCache, _primaryCache);
        }
        _primaryCache[instanceId] = entry;
    }

    public static string GetHierarchyPath(Component component, int maxNodes)
    {
        if (component == null || component.transform == null)
            return string.Empty;

        var fullPath = GetHierarchyPath(component.transform);
        if (maxNodes <= 0 || string.IsNullOrEmpty(fullPath))
            return fullPath;

        // Slice the cached fullPath from the right when deeper than maxNodes,
        // avoiding any repeated IL2CPP parent traversal or name allocations.
        var slashesRemaining = maxNodes;
        for (var i = fullPath.Length - 1; i >= 0; i--)
        {
            if (fullPath[i] == '/' && --slashesRemaining == 0)
                return fullPath[(i + 1)..];
        }
        return fullPath;
    }

    public static void InvalidateTransform(Transform transform)
    {
        for (var current = transform; current != null; current = current.parent)
        {
            var id = current.GetInstanceID();
            _primaryCache.Remove(id);
            _secondaryCache.Remove(id);
        }
    }

    public static void ClearCache()
    {
        _primaryCache.Clear();
        _secondaryCache.Clear();
        NonInternalLayerIds.Clear();
        _nextCanvasRootProbeTime = 0f;
    }

    private static string BuildHierarchyPath(Transform transform, int maxNodes, out string leafName)
    {
        leafName = string.Empty;
        try
        {
            var stack = new List<string>(16);
            for (var current = transform;
                 current != null && (maxNodes <= 0 || stack.Count < maxNodes);
                 current = current.parent)
            {
                var currentName = current.name ?? string.Empty;
                if (stack.Count == 0)
                    leafName = currentName;
                stack.Add(currentName);
                if (currentName.StartsWith("UICanvas", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(currentName, "ExtraCanvas", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterUiCanvasRoot(current);
                }
            }

            stack.Reverse();
            return string.Join("/", stack);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsUnderUiCanvas(object component) =>
        (GetFlags(component) & UiPathFlags.UnderUiCanvas) != 0;

    public static bool IsUiTranslationExcluded(object _) => false;

    public static bool IsSubSkillText(object component) =>
        (GetFlags(component) & UiPathFlags.SubSkillScrollText) != 0;

    public static bool IsSubSkillScrollText(Transform transform) =>
        (GetFlags(transform) & UiPathFlags.SubSkillScrollText) != 0;

    public static bool IsCompleteSubSkillHierarchy(string hierarchy) =>
        !string.IsNullOrEmpty(hierarchy) &&
        ((hierarchy.Contains("unit_detail_rework", StringComparison.OrdinalIgnoreCase) &&
          hierarchy.Contains("skilldialog", StringComparison.OrdinalIgnoreCase) &&
          hierarchy.Contains("subskilltextarea", StringComparison.OrdinalIgnoreCase)) ||
         IsAttachAbilitySubSkillHierarchy(hierarchy));

    public static bool IsAttachAbilitySubSkillHierarchy(string hierarchy) =>
        !string.IsNullOrEmpty(hierarchy) &&
        hierarchy.Contains("unit_detail_attachability", StringComparison.OrdinalIgnoreCase) &&
        hierarchy.Contains("windowbase/scrollview", StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacySubSkillListPath(string hierarchy) =>
        !string.IsNullOrEmpty(hierarchy) &&
        hierarchy.Contains("unit_detail_rework", StringComparison.OrdinalIgnoreCase) &&
        hierarchy.Contains("skilldialog", StringComparison.OrdinalIgnoreCase) &&
        hierarchy.Contains("scroll view", StringComparison.OrdinalIgnoreCase);

    public static bool IsHomeDeckSubSkillName(Transform transform) =>
        (GetFlags(transform) & UiPathFlags.HomeDeckSubSkillName) != 0;

    public static bool IsEquippedSubSkillAttachPath(string path) =>
        !string.IsNullOrEmpty(path) &&
        (path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("Unit_DeckList", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("DeckUnitSlot", StringComparison.OrdinalIgnoreCase)) &&
        path.Contains("/AttachAbilityButton", StringComparison.OrdinalIgnoreCase);

    public static bool IsHomeDeckExSkillSlotName(Transform transform) =>
        (GetFlags(transform) & UiPathFlags.HomeDeckExSkillSlotName) != 0;

    public static bool IsQuestDungeonSkillName(Transform transform) =>
        (GetFlags(transform) & UiPathFlags.QuestDungeonSkillName) != 0;

    public static bool ShouldUseSubSkillPresentation(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.ShouldUseSubSkillPresentation) != 0;

    public static bool IsUnitDetailUiDescription(object component) =>
        (GetFlags(component) & UiPathFlags.UnitDetailUiDescription) != 0;

    public static bool IsUnitDetailFontOverride(object component) =>
        (GetFlags(component) & UiPathFlags.UnitDetailFontOverride) != 0;

    public static bool IsUnitDetailNoOutlineText(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.UnitDetailNoOutline) != 0;

    public static bool IsUnitDetailCompactDescription(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var isExSkillDescription =
            path.Contains("/Unit_Detail_ExSkill", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/WindowBase/Rein/Desc", StringComparison.OrdinalIgnoreCase);
        var isUniqueWeaponAbility =
            path.Contains("/UniqueWeapon_CreateDetailView", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(
                "/UniqueWeapon_StatusView/LayoutGroup/Ability/AbilityText",
                StringComparison.OrdinalIgnoreCase);

        return isExSkillDescription || isUniqueWeaponAbility;
    }

    public static bool IsUnitDetailFixedSpacingText(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.UnitDetailFixedSpacing) != 0;

    public static bool IsCommonSortFilterText(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.CommonSortFilter) != 0;

    public static bool IsShopOrPopupLayoutText(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.ShopOrPopupLayout) != 0;

    public static bool IsStoryMessageText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        if (IsCurrentStoryDialogueText(text))
            return true;

        var fullPath = GetHierarchyPath(text.transform);
        if (!fullPath.Contains("AdvEngine/", StringComparison.OrdinalIgnoreCase))
            return false;

        var isStoryHistoryText =
            fullPath.Contains("/BackLog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/Backlog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/FullScreenLog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/History", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/Log", StringComparison.OrdinalIgnoreCase);
        var isStorySelectionText =
            fullPath.Contains("/Selection/Content/SelectionItem", StringComparison.OrdinalIgnoreCase);

        return (isStoryHistoryText || isStorySelectionText) &&
               !fullPath.Contains("/BackLogButton/", StringComparison.OrdinalIgnoreCase) &&
               !fullPath.Contains("/Skip/", StringComparison.OrdinalIgnoreCase) &&
               !fullPath.Contains("/Auto/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentStoryDialogueText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var fullPath = GetHierarchyPath(text.transform);
        return fullPath.Contains(
                   "AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/MessageTexts/",
                   StringComparison.Ordinal) &&
               (fullPath.EndsWith("/MessageText", StringComparison.Ordinal) ||
                fullPath.EndsWith("/NameText", StringComparison.Ordinal));
    }

    public static bool IsStorySpeakerNameText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        if (!path.Contains("AdvEngine/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!path.EndsWith("/NameText", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/Name", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/CharacterName", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Contains("/MessageWindow", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/BackLog", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/History", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Log", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDialogueBackground(Graphic graphic)
    {
        if (graphic is not Image && graphic is not RawImage)
            return false;

        var path = GetHierarchyPath(graphic.transform);
        return path.Contains(
            "AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/",
            StringComparison.Ordinal);
    }

    public static bool IsDialogueControlLabel(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        return path.Contains(
                   "AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/",
                   StringComparison.Ordinal) &&
               IsDialogueControlPath(path);
    }

    public static bool IsDialogueControlPath(string path) =>
        !string.IsNullOrEmpty(path) &&
        (path.Contains("/Auto/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/Skip/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/Log/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/BackLogButton/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/LogButton/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/BackLog/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/Backlog/", StringComparison.OrdinalIgnoreCase));

    public static bool IsBackLogButtonBackground(Graphic graphic) =>
        graphic != null &&
        graphic.transform != null &&
        GetHierarchyPath(graphic.transform).EndsWith("/BackLogButton", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetStoryOutlineColor(Text text, Color darkOutlineColor, out Color outlineColor)
    {
        outlineColor = default;
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        if (!path.Contains(
                "AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/MessageTexts/",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (path.EndsWith("/MessageText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = Color.white;
            return true;
        }

        if (path.EndsWith("/NameText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = darkOutlineColor;
            return true;
        }

        return false;
    }
}
