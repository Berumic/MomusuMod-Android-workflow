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
    // IsUiTextSoftOutlineLayer never calls gameObject.name across IL2CPP.
    private static readonly HashSet<int> InternalLayerIds = new();

    private readonly struct CachedHierarchyPath
    {
        public CachedHierarchyPath(
            string fullPath,
            string normalizedPath,
            UiPathFlags flags,
            int validatedFrame)
        {
            FullPath = fullPath;
            NormalizedPath = normalizedPath;
            Flags = flags;
            ValidatedFrame = validatedFrame;
        }

        public string FullPath { get; }
        public string NormalizedPath { get; }
        public UiPathFlags Flags { get; }
        public int ValidatedFrame { get; }
    }

    public static void RegisterInternalLayer(GameObject layerObject, Component layerComponent)
    {
        if (layerObject != null)
        {
            InternalLayerIds.Add(layerObject.GetInstanceID());
            if (layerObject.transform != null)
                InternalLayerIds.Add(layerObject.transform.GetInstanceID());
        }
        if (layerComponent != null)
        {
            InternalLayerIds.Add(layerComponent.GetInstanceID());
            if (layerComponent.transform != null)
                InternalLayerIds.Add(layerComponent.transform.GetInstanceID());
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

    public static bool IsUiTextSoftOutlineLayer(object component)
    {
        if (component is not UnityEngine.Object unityObj || unityObj == null)
            return false;
        if (InternalLayerIds.Contains(unityObj.GetInstanceID()))
            return true;

        var transform = component switch
        {
            Transform t => t,
            Component c => c.transform,
            GameObject go => go.transform,
            _ => null
        };
        if (transform != null &&
            (InternalLayerIds.Contains(transform.GetInstanceID()) ||
             (GetFlags(transform) & UiPathFlags.InternalUnderlayLayer) != 0))
        {
            InternalLayerIds.Add(unityObj.GetInstanceID());
            return true;
        }
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

    private static CachedHierarchyPath GetOrCreateCachedPath(Transform transform)
    {
        if (transform == null)
            return default;

        var instanceId = transform.GetInstanceID();
        var currentFrame = Time.frameCount;

        if (_primaryCache.TryGetValue(instanceId, out var cached))
        {
            // Once attached under a Canvas, OnTransformParentChanged / OnCanvasHierarchyChanged
            // explicitly invalidates the entry if it ever moves. For unparented/detached nodes,
            // re-check once per frame in case they were parented without firing a TMP hook.
            if ((cached.Flags & UiPathFlags.HasCanvasAncestor) != 0 ||
                cached.ValidatedFrame == currentFrame)
            {
                return cached;
            }
        }
        else if (_secondaryCache.TryGetValue(instanceId, out cached))
        {
            if ((cached.Flags & UiPathFlags.HasCanvasAncestor) != 0 ||
                cached.ValidatedFrame == currentFrame)
            {
                PromoteOrInsert(instanceId, cached);
                return cached;
            }
        }

        var fullPath = BuildHierarchyPath(transform, maxNodes: 0, out var leafName);
        var normalizedPath = UiStylePolicy.NormalizePath(fullPath);
        var flags = ComputeFlags(fullPath, leafName);
        cached = new CachedHierarchyPath(fullPath, normalizedPath, flags, currentFrame);
        PromoteOrInsert(instanceId, cached);
        return cached;
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

        if (path.Contains("Canvas", StringComparison.OrdinalIgnoreCase))
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
            path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/ExSkillChangeButton/SkillName/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/SkillNameText", StringComparison.OrdinalIgnoreCase);
        if (isHomeDeckExSkill)
            flags |= UiPathFlags.HomeDeckExSkillSlotName;

        var isQuestDungeonSkill =
            path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Quest_Dungeon", StringComparison.OrdinalIgnoreCase) &&
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
            (path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) &&
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

    public static void InvalidateTransform(Transform root)
    {
        if (root == null)
            return;

        // Clear caches when hierarchy parenting changes so children re-evaluate paths cleanly.
        _primaryCache.Clear();
        _secondaryCache.Clear();
    }

    public static void ClearCache()
    {
        _primaryCache.Clear();
        _secondaryCache.Clear();
    }

    public static bool IsUnderUiCanvas(object component) =>
        (GetFlags(component) & UiPathFlags.UnderUiCanvas) != 0;

    public static bool IsUiTranslationExcluded(object component)
    {
        if (IsUiTextSoftOutlineLayer(component))
            return true;
        return component switch
        {
            Text => true,
            Image => true,
            RawImage => true,
            _ => false
        };
    }

    public static bool IsSubSkillText(object component) =>
        (GetFlags(component) & UiPathFlags.SubSkillScrollText) != 0;

    public static bool IsSubSkillScrollText(Transform root) =>
        (GetFlags(root) & UiPathFlags.SubSkillScrollText) != 0;

    // These path predicates remain part of the shared scanner contract.  Keep
    // them alongside the cached classifier so callers do not duplicate the
    // old hierarchy string checks after the cache refactor.
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

    public static bool IsHomeDeckSubSkillName(Transform root) =>
        (GetFlags(root) & UiPathFlags.HomeDeckSubSkillName) != 0;

    public static bool IsHomeDeckExSkillSlotName(Transform root) =>
        (GetFlags(root) & UiPathFlags.HomeDeckExSkillSlotName) != 0;

    public static bool IsQuestDungeonSkillName(Transform root) =>
        (GetFlags(root) & UiPathFlags.QuestDungeonSkillName) != 0;

    public static bool ShouldUseSubSkillPresentation(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.ShouldUseSubSkillPresentation) != 0;

    public static bool IsUnitDetailUiDescription(object component) =>
        (GetFlags(component) & UiPathFlags.UnitDetailUiDescription) != 0;

    public static bool IsUnitDetailFontOverride(object component) =>
        component is TMP_Text text &&
        (GetFlags(text.transform) & UiPathFlags.UnitDetailFontOverride) != 0;

    public static bool IsUnitDetailNoOutlineText(TMP_Text text) =>
        text != null && (GetFlags(text.transform) & UiPathFlags.UnitDetailNoOutline) != 0;

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

        var path = GetHierarchyPath(text.transform);
        return IsStoryMessagePath(path, text.transform.name);
    }

    public static bool IsCurrentStoryDialogueText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        return (path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Utage", StringComparison.OrdinalIgnoreCase)) &&
               path.Contains("MessageWindow", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(text.transform.name, "Text", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("BackLog", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStorySpeakerNameText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        if (string.Equals(text.transform.name, "NameText", StringComparison.OrdinalIgnoreCase))
            return true;

        var path = GetHierarchyPath(text.transform);
        return (path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Utage", StringComparison.OrdinalIgnoreCase)) &&
               path.Contains("BackLog", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith("/Name", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStoryMessagePath(string path, string leafName = null)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (!path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("Utage", StringComparison.OrdinalIgnoreCase))
            return false;

        var nodeName = leafName ?? GetLeafName(path);
        var isLiveDialogue =
            path.Contains("MessageWindow", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "Text", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "NameText", StringComparison.OrdinalIgnoreCase));
        var isBackLog =
            path.Contains("BackLog", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "Text", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "Name", StringComparison.OrdinalIgnoreCase));
        var isSelection =
            path.Contains("Selection", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nodeName, "Text", StringComparison.OrdinalIgnoreCase);
        return isLiveDialogue || isBackLog || isSelection;
    }

    private static string GetLeafName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    private static bool IsEquippedSubSkillAttachPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var isDeckSubSkill =
            path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillChangeButton/", StringComparison.OrdinalIgnoreCase);
        var isUnitDetailSubSkill =
            path.Contains("Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillArea/", StringComparison.OrdinalIgnoreCase);
        var isQuestSubSkill =
            path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase) &&
            (path.Contains("/Quest_Challenge", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/Quest_Story", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/Quest_Event", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/Quest_Daily", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/Quest_Dungeon", StringComparison.OrdinalIgnoreCase)) &&
            path.Contains("/SubSkill", StringComparison.OrdinalIgnoreCase);
        var isAttachPopup =
            path.Contains("Unit_Detail_AttachAbility", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/WindowBase/ScrollView/", StringComparison.OrdinalIgnoreCase);

        return (isDeckSubSkill || isUnitDetailSubSkill || isQuestSubSkill || isAttachPopup) &&
               path.Contains("SubSkillAttach", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnitDetailCompactDescription(string path) =>
        path.Contains("/Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
        ((path.Contains("/ExSkillArea/ExSkillTextArea/", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)) ||
         (path.Contains("/SkillDialog/SkillTextArea/", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)) ||
         (path.Contains("/SkillDialog/SubSkillTextArea", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)) ||
         (path.Contains("/StatusDialog/AwakeningBonusArea/", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)) ||
         (path.Contains("/StatusDialog/ClassChangeBonusArea/", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)) ||
         (path.Contains("/StatusDialog/RaceFeatureArea/", StringComparison.OrdinalIgnoreCase) &&
          path.EndsWith("/Text", StringComparison.OrdinalIgnoreCase)));

    public static bool IsDialogueBackground(Graphic graphic)
    {
        if (graphic is not Image && graphic is not RawImage)
            return false;

        var path = GetHierarchyPath(graphic.transform);
        return path.Contains(
            "MessageWindowManager/MessageWindow/RootChildren/",
            StringComparison.OrdinalIgnoreCase) &&
            (path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("Utage", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDialogueControlLabel(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        return IsDialogueControlPath(path) &&
               (path.Contains("AdvEngine", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Utage", StringComparison.OrdinalIgnoreCase));
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
        GetHierarchyPath(graphic.transform).EndsWith(
            "/BackLogButton", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetStoryOutlineColor(
        Text text,
        Color darkOutlineColor,
        out Color outlineColor)
    {
        outlineColor = default;
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        var leaf = text.transform.name;
        if (!IsStoryMessagePath(path, leaf))
            return false;

        if (leaf.Equals("MessageText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = Color.white;
            return true;
        }

        if (leaf.Equals("NameText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = darkOutlineColor;
            return true;
        }

        return false;
    }

    private static string BuildHierarchyPath(Transform current, int maxNodes, out string leafName)
    {
        leafName = string.Empty;
        if (current == null)
            return string.Empty;

        leafName = current.name ?? string.Empty;
        var names = new List<string>(16);
        var depth = 0;
        while (current != null && (maxNodes <= 0 || depth < maxNodes))
        {
            names.Add(current.name);
            current = current.parent;
            depth++;
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
