using System;
using UnityEngine;

namespace MonsterMusumeTDMod.Services;

internal static class MaterialKeywords
{
    public static bool HasShaderKeyword(this Material material, string keyword)
    {
#if ANDROID
        // IsKeywordEnabled was stripped from the Android player. shaderKeywords
        // has a real native method in this APK; the generated ICall stub does not.
        var keywords = material.shaderKeywords;
        if (keywords != null)
            for (var i = 0; i < keywords.Length; i++)
                if (string.Equals(keywords[i], keyword, StringComparison.Ordinal))
                    return true;
        return false;
#else
        return material.IsKeywordEnabled(keyword);
#endif
    }
}
