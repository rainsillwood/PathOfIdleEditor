using HarmonyLib;

namespace PathOfIdleEditor;

// 显示/生效值的最终来源是 SaveItemData.GetResolvedEquipAffixValue；
// 它按 id + quality + level 从表里重算，忽略 SaveAffixData.value。
//
// 我们通过「双字段哨兵」标记用户手动指定过数值的词条：
//   - talentLevel = 1
//   - abilityId   = int.MinValue（魔数，游戏中不存在）
// 两者必须同时满足，Patch 才直接返回存档值。
//
// 之所以不用单字段哨兵：当前版本中 talentLevel 恰好只有符文之语使用，
// 但这是靠实现细节的巧合维持的；一旦游戏更新让普通词条也启用 talentLevel，
// 单字段判断就会误命中。abilityId 在普通词条（effectType==1）下是保留字段，
// 用 int.MinValue 作为魔数可以杜绝未来的碰撞。
[HarmonyPatch(typeof(SaveItemData), nameof(SaveItemData.GetResolvedEquipAffixValue))]
internal static class SaveAffixValuePatch
{
    // 与 GameEditorService 写入时使用的魔数保持一致。
    internal const int ManualValueAbilityIdSentinel = int.MinValue;

    [HarmonyPrefix]
    private static bool Prefix(SaveAffixData affixData, ref int __result)
    {
        if (affixData == null)
            return true;
        if (affixData.talentLevel == 1 &&
            affixData.abilityId == ManualValueAbilityIdSentinel)
        {
            __result = affixData.value;
            return false;   // 跳过原方法
        }
        return true;
    }
}