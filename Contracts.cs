using System.Collections.Generic;

namespace PathOfIdleEditor;

// ============================================================
// 【协议文件】桌面端与桥接 Mod 之间的命名管道协议定义。
//
// 客户端 PathOfIdleEditor.App.Contracts 里有一份几乎相同的类型定义，
// 两边必须逐字段对齐（名称、类型、可空性）。
//
// - 新增/删除/重命名字段 → 必须同步修改两侧。
// - JsonSerializer 会静默忽略缺失/多余的字段，字段不对齐时不会报错，
//   而是静默表现为「读不到数据 / 写不进去」。
// - 修改本文件后，请同步提升 BridgeProtocol.Version。
// ============================================================
internal static class BridgeProtocol
{
    // 协议版本号：两端不一致时客户端会主动拒绝响应。
    // 任何字段增删/重命名/类型变化都必须 +1。
    // v2：新增装备质量(mainAttrGrade)、词条取值分类与参考值、强制越界开关、解析值/存档值。
    // v3：EquipmentRules 新增 MaximumCorruptCount（圣碑腐蚀次数上限，用于词条额度说明）。
    // v4：污染词条独立成组 —— AffixEdit 新增 IsCorrupt/IsCorruptLocked（读回并原样回写）、
    //     AffixOption 新增 IsCorruptPool、EquipmentInventoryEntry 新增 LegacyCorruptText、
    //     EquipmentRules 新增 CorruptGroupName。
    // v5：开孔控制 —— EquipmentEdit 新增 SlotCount（-1 = 原生随机）、EquipmentRules 新增孔数候选/上限/等级门槛、
    //     EquipmentInventoryEntry 新增 SlotCount/MaximumSlotCount/Slots（SlotRuneEdit）。
    // v6：孔位类型可选 —— EquipmentEdit 新增 SlotTypes（1 攻击/2 防御/0 随机）、
    //     EquipmentRules 新增 IsWeaponSlotLayout（默认布局：武器攻击孔 / 防具防御孔）。
    // v7：开孔并入词条表 —— 孔位作为 AffixValueKind.SlotType 的词条行（AffixEdit.SocketType = 0/1/2）；
    //     移除 EquipmentEdit.SlotCount/SlotTypes 与 EquipmentInventoryEntry.Slots、SlotTypeEdit、SlotRuneEdit；
    //     EquipmentRules 新增 SocketGroupName。
    // v8：越界添加 —— EquipmentEdit 新增 OverrideLimits（忽略词条/孔位数量限制；行级 ForceOverflow 语义不变）。
    // v9：额度区间显示 —— EquipmentRules 新增 AffixQualityMinimums（各档位原生下限，与上限配对显示成 N~M）。
    internal const int Version = 9;
}

// 词条的取值方式：决定「词条等级」是否可编辑、以及参考数值怎么显示。
// 依据原生实现（见 docs/WORKFLOW.md 13.3/13.7）：
//   档位 1/2/3 走 CreateEquipmentAffix，数值 = 档位倍率 × 品质倍率 × 多项式(等级) × 词条池倍率 → 随等级变化；
//   档位 4/5 非特殊与神话特殊、档位 8 由创建流程直接写入，等级被钉死，数值不再随等级变化。
internal enum AffixValueKind
{
    // 按装备质量档位计算，等级可编辑，参考值是三个档位各自的实际值。
    GradeBased = 0,
    // 固定值（原生 valueType 0/2/4：取下端或上端）。
    Fixed = 1,
    // 区间随机（原生 valueType 1 均匀随机、3 有符号特殊随机）。
    RandomRange = 2,
    // 镶嵌孔位行（开孔逻辑并入词条表）：这行的「数值」是孔位类型码（0 随机 / 1 攻击 / 2 防御），
    // Id/Name/Level 只是回显该孔已镶嵌的符文（runeId / 名字 / 等级，没镶嵌则为 0 / 空 / 0）。
    SlotType = 3
}

// 管道协议只传输普通 CLR 对象，不能直接序列化 IL2CPP 游戏对象。
internal sealed class EditorRequest
{
    public string Action { get; set; } = "";
    public EquipmentEdit? Equipment { get; set; }
    public EquipmentReplaceEdit? EquipmentReplace { get; set; }
    public HeroEdit? Hero { get; set; }
    public InventoryItemEdit? InventoryItem { get; set; }
    // 需求：表格内一次改多行 —— 批量提交改动过的行。
    public List<InventoryItemEdit>? InventoryItems { get; set; }
    public InventoryAddEdit? InventoryAdd { get; set; }
    public LordEdit? Lord { get; set; }
}

internal sealed class EditorResponse
{
    // 每个响应都带上服务端当前的协议版本号，客户端据此判断两边是否兼容。
    public int ProtocolVersion { get; set; } = BridgeProtocol.Version;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public EditorSnapshot? Snapshot { get; set; }
    public EquipmentInventorySnapshot? EquipmentInventory { get; set; }
    public EquipmentRules? EquipmentRules { get; set; }
    // "affixReference" 的回包：与请求同序，但补全了参考值字段。
    public List<AffixEdit>? AffixReferences { get; set; }
    public InventorySnapshot? Inventory { get; set; }
    public LordEdit? Lord { get; set; }
}

internal sealed class EditorSnapshot
{
    public List<EquipmentTemplate> EquipmentTemplates { get; set; } = new();
    public List<RuleOption> EquipmentQualities { get; set; } = new();
    // 装备质量档位（原生的 TEquipGrade：1/2/3），用于「装备质量」下拉。
    public List<RuleOption> EquipmentGrades { get; set; } = new();
    public List<int> EquipmentLevels { get; set; } = new();
    public List<int> BlessingLevels { get; set; } = new();
    public List<RuleOption> HeroQualities { get; set; } = new();
    public List<HeroEdit> Heroes { get; set; } = new();
    public InventorySnapshot Inventory { get; set; } = new();
    public LordEdit Lord { get; set; } = new();
}

internal sealed class LordEdit
{
    public int Level { get; set; }
    public int MaximumLevel { get; set; }
    public List<LordJobEdit> Jobs { get; set; } = new();
    public List<LordJobLevelRule> JobLevelRules { get; set; } = new();
}

internal sealed class LordJobLevelRule
{
    public int Level { get; set; }
    public int RequiredLordLevel { get; set; }
    public int TotalAttributePoints { get; set; }
    public int MaximumTalentBonusLevel { get; set; }
}

internal sealed class LordJobEdit
{
    public int JobId { get; set; }
    public string JobName { get; set; } = "";
    public int Level { get; set; }
    public int MaximumLevel { get; set; }
    public int RequiredLordLevel { get; set; }
    public int TotalAttributePoints { get; set; }
    public int Strength { get; set; }
    public int StrengthMinimum { get; set; }
    public int StrengthMaximum { get; set; }
    public int Dexterity { get; set; }
    public int DexterityMinimum { get; set; }
    public int DexterityMaximum { get; set; }
    public int Intelligence { get; set; }
    public int IntelligenceMinimum { get; set; }
    public int IntelligenceMaximum { get; set; }
    public List<LordJobAttributeRule> AttributeRules { get; set; } = new();
    public List<LordTalentBonusEdit> TalentBonuses { get; set; } = new();
}

internal sealed class LordJobAttributeRule
{
    public int Level { get; set; }
    public int TotalAttributePoints { get; set; }
    public int StrengthMinimum { get; set; }
    public int StrengthMaximum { get; set; }
    public int DexterityMinimum { get; set; }
    public int DexterityMaximum { get; set; }
    public int IntelligenceMinimum { get; set; }
    public int IntelligenceMaximum { get; set; }
}

internal sealed class LordTalentBonusEdit
{
    public int TalentId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaximumLevel { get; set; }
}

internal sealed class InventorySnapshot
{
    public List<InventoryTemplate> AvailableItems { get; set; } = new();
    public List<InventoryItemEdit> BagItems { get; set; } = new();
}

internal sealed class InventoryTemplate
{
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }
    public int Level { get; set; }
    public string LevelDescription { get; set; } = "";
}

internal sealed class InventoryItemEdit
{
    // 0 为普通背包，1 为优先存放道具的 5x5 道具袋。
    public int Container { get; set; }
    public string ContainerName { get; set; } = "";
    public int FieldIndex { get; set; }
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }
    public int Level { get; set; }
    public int Count { get; set; }
    // 数量上限，来自原生规则；0 表示无上限（背包物品 StackItem 是纯累加，没有上限）。
    public int Maximum { get; set; }
}

internal sealed class InventoryAddEdit
{
    public int Type { get; set; }
    public int Id { get; set; }
    public int Quality { get; set; }
    public int Level { get; set; }
    public int Count { get; set; }
}

internal sealed class RuleOption
{
    public int Value { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class EquipmentInventoryEntry
{
    // 桥接侧会话级标识，仅在下一次 equipmentInventory 前有效。
    public string Guid { get; set; } = "";
    public int FieldIndex { get; set; }
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public int Level { get; set; }
    public int ForgeLevel { get; set; }
    // 该实例当前的主属性档位（原生的 mainAttrGrade），用于回显「装备质量」。
    public int MainAttrGrade { get; set; }
    // 旧版（1.1.0 之前）污染残留的效果文案（SaveItemData.GetCorruptInfoText）。
    // 非空表示这件装备带着重做前的污染效果，只能用【涤魂泪滴】清除。
    public string LegacyCorruptText { get; set; } = "";
    // 已开孔数与该品级的上限（SaveItemData.slotCount / TEquipQuality.slotCountArr 最大值）。
    public int SlotCount { get; set; }
    public int MaximumSlotCount { get; set; }
    // 孔位明细以「镶嵌组」的词条行形式下发（AffixEdit.ValueKind == AffixValueKind.SlotType）。
    public List<AffixEdit> Affixes { get; set; } = new();
}

// 单个孔位（SaveSlotRuneData：slotIndex / slotType / runeId / quality / affixList）。
internal sealed class EquipmentInventorySnapshot
{
    public List<EquipmentInventoryEntry> Entries { get; set; } = new();
}

internal sealed class EquipmentReplaceEdit
{
    // GUID 由 equipmentInventory 返回；缺失或过期都会拒绝。
    public string Guid { get; set; } = "";
    public EquipmentEdit Equipment { get; set; } = new();
}

internal sealed class EquipmentTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Part { get; set; }
    public string PartName { get; set; } = "";
    public int BaseQuality { get; set; }
    public List<int> AllowedQualities { get; set; } = new();
}

internal sealed class EquipmentEdit
{
    public int TemplateId { get; set; }
    public int Quality { get; set; }
    public int Level { get; set; }
    public int ForgeLevel { get; set; }
    // 主属性档位（1/2/3）。0 表示不指定，沿用原生 CreateEquip 的随机结果。
    public int MainAttrGrade { get; set; }
    // 越界添加（界面上「添加」前的勾选框）：勾选后忽略词条/孔位的**数量**限制。
    // 注意与行级 AffixEdit.ForceOverflow 区分：后者只作用于该行的数值/孔位类型。
    public bool OverrideLimits { get; set; }
    public List<AffixEdit> Affixes { get; set; } = new();
}

internal sealed class EquipmentRules
{
    // 这些限制由当前游戏表和原生方法实时计算，桌面端不维护规则副本。
    public int MaximumAffixCount { get; set; }
    public int MaximumAffixLevel { get; set; }
    // 单件装备当前可用的圣碑腐蚀次数上限（MapDoomData.GetEquipCorruptCountMax，
    // 由终焉阶段 TDoomStage.equipCorruptCount 决定）。每次腐蚀会往存档追加 1 条档位3词条，
    // 所以它已经计入 AffixQualityLimits[稀有] 与 MaximumAffixCount；这里单独回传只为界面说明。
    public int MaximumCorruptCount { get; set; }
    // 「污染」分组名（圣碑腐蚀产生的词条在编辑器里单独成组）。
    public string CorruptGroupName { get; set; } = "污染";
    // 「镶嵌」分组名（开孔逻辑并入词条表后，孔位行单独成组；组名带上限，见 EquipmentRules.MaximumSlotCount）。
    public string SocketGroupName { get; set; } = "镶嵌";
    // ── 开孔（SaveItemData.slotCount / slotRuneList）──
    // 该品级可开出的孔数候选（TEquipQuality.slotCountArr；锻造开孔用 forgeSlotCountArr）。
    public List<int> SlotCountCandidates { get; set; } = new();
    public List<int> ForgeSlotCountCandidates { get; set; } = new();
    public int MaximumSlotCount { get; set; }
    // 开孔等级门槛（TConstant.equipSlotLevel）：装备等级低于它，原生 EquipRandomSlot 直接不开孔。
    public int SlotLevelRequirement { get; set; }
    // 默认孔位布局：true = 武器（part <= 3，默认攻击孔，原生"特殊孔位"是最后一个孔）；
    // false = 防具（默认防御孔，原生"特殊孔位"是第一个孔）。原生那一孔有 50% 概率换成另一种类型。
    public bool IsWeaponSlotLayout { get; set; }
    public List<int> AllowedForgeLevels { get; set; } = new();
    public Dictionary<int, int> AffixQualityLimits { get; set; } = new();
    // 每个档位的**原生下限**（与上限配对显示成区间）。来源：
    //   · 档位1 基础：TEquip.baseAffix 非空则恒 1
    //   · 档位2 精良：TEquipQuality.fineAffixCount（固定条数）
    //   · 档位3 稀有：品级 3/6/8 → RandomRangeInt(2, rareAffixCount+1) 的下限 2；品级 4/5 → legendAffixCount（固定）
    //   · 档位5 神话专属：模板里 specialType==1 的行数（固定）
    // 客户端显示 "最多 N 条" 或 "N~M 条"；添加校验仍用上限。
    public Dictionary<int, int> AffixQualityMinimums { get; set; } = new();
    public Dictionary<int, string> AffixQualityNames { get; set; } = new();
    public List<AffixOption> AllowedAffixes { get; set; } = new();
    public List<AffixEdit> GeneratedAffixes { get; set; } = new();
}

internal sealed class AffixOption
{
    public int Id { get; set; }
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Name { get; set; } = "";
    // ── 参考数值（全部由游戏规则算出，仅作展示，不限制输入）──
    public AffixValueKind ValueKind { get; set; } = AffixValueKind.GradeBased;
    // GradeBased：装备质量档位 1/2/3 各自的实际值（低、中、高）。
    public List<int> GradeValues { get; set; } = new();
    // Fixed：唯一的固定值。
    public int FixedValue { get; set; }
    // RandomRange：原生随机区间的上下界。
    public int RandomMinimum { get; set; }
    public int RandomMaximum { get; set; }
    // 等级可编辑区间：精良/稀有 = 1..affixMaxLevel；独特 = 1；传说/神话 = affixMaxLevel（固定）。
    public int LevelMinimum { get; set; }
    public int LevelMaximum { get; set; }
    // 污染候选池标记：真表示这条候选也能作为「污染」词条加入（原生腐蚀就是从档位3池里挑未用过的 id，
    // GameAssembly.c（1.1.4）:1618301）。客户端据此把「污染」分组的候选池独立出来。
    public bool IsCorruptPool { get; set; }
}

internal sealed class AffixEdit
{
    public int Id { get; set; }
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Name { get; set; } = "";
    // 词条等级。精良/稀有 为 1..affixMaxLevel 随机；传说/神话固定为 affixMaxLevel；独特固定为 1。
    public int Level { get; set; }
    // 等级可编辑区间：固定档位时两者相等，客户端会把输入修正回该值。
    public int LevelMinimum { get; set; }
    public int LevelMaximum { get; set; }
    // null 表示沿用游戏原生随机值；非 null 时在创建完成后覆盖存档中的整数值。
    public int? Value { get; set; }
    // 勾选后把 Value 写入存档并用哨兵使其生效；未勾选时清除哨兵，交回原生计算。
    public bool ForceOverflow { get; set; }
    // 「装备自带」：档位 8 独有技能词条与传说/神话 legendAffixArr 专属词条。
    // 它们由装备本身决定，客户端不允许增删（数值仍可编辑）。
    public bool EquipmentFixed { get; set; }
    // ── 污染（圣碑腐蚀）──
    // 原生 SaveAffixData.isCorrupt：1.1.0 起腐蚀固定给一条随机污染词缀（等级 1/3/5）。
    // 必须原样回写，否则装备面板不再显示污染、也无法用【涤魂泪滴】净化。
    public bool IsCorrupt { get; set; }
    // 原生 SaveAffixData.isCorruptLocked：终焉阶段 6/7 开放，可锁定的污染词条条数上限见 TDoomStage.affixLockCount。
    public bool IsCorruptLocked { get; set; }
    // ── 镶嵌孔位行（ValueKind == AffixValueKind.SlotType 时有效）──
    // 孔位类型码：0 = 随机（复刻原生 50/50）、1 = 攻击孔、2 = 防御孔。
    // 未勾选「强制越界」时，桥接只接受符合原生规则的组合（武器只能攻击孔、防具只能防御孔，
    // 例外是原生的"特殊孔位"：武器最后一孔 / 防具第一孔，且孔数 > 1）。
    public int SocketType { get; set; }
    // ── 参考数值（同上）──
    public AffixValueKind ValueKind { get; set; } = AffixValueKind.GradeBased;
    public List<int> GradeValues { get; set; } = new();
    public int FixedValue { get; set; }
    public int RandomMinimum { get; set; }
    public int RandomMaximum { get; set; }
    // ── 仅响应使用：装备实例上该词条的数值 ──
    // 游戏解析出来的实际值（GetResolvedEquipAffixValue），未勾选强制越界时显示这个。
    public int ResolvedValue { get; set; }
    // 存档里保存的原始 value，勾选强制越界时显示/编辑这个。
    public int StoredValue { get; set; }
}

internal sealed class HeroEdit
{
    public int UniqueId { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaximumLevel { get; set; }
    public int BlessingLevel { get; set; }
    public int Quality { get; set; }
    public float Strength { get; set; }
    public float Dexterity { get; set; }
    public float Intelligence { get; set; }
    public int RemainingSkillPoints { get; set; }
    public int MaximumAlienSkills { get; set; }
    public int AlienSkillCount { get; set; }
    public int MaximumInspiredTalents { get; set; }
    public int InspiredTalentCount { get; set; }
    public List<GrowthAttributeEdit> GrowthAttributes { get; set; } = new();
    public List<TalentSlotEdit> TalentSlots { get; set; } = new();
}

internal sealed class GrowthAttributeEdit
{
    public int Type { get; set; }
    public string Name { get; set; } = "";
    public float Value { get; set; }
    public int MinimumValue { get; set; }
    public int MaximumValue { get; set; }
    /// <summary>当前投入的点数（生命/魔力取自 baseAttrDic，力敏智取自 mainAttrDic）。</summary>
    public float Points { get; set; }
    public int PointMinimum { get; set; }
    public int PointMaximum { get; set; }
    /// <summary>"base"=生命/魔力（写回 baseAttrDic/baseAttrUpDic）；"main"=力/敏/智（写回 mainAttrDic/mainAttrUpDic）。</summary>
    public string Group { get; set; } = "";
}

internal sealed class TalentSlotEdit
{
    // SlotId 是存档字典键，TalentId 是该位置当前选择的天赋/技能定义 ID。
    public int SlotId { get; set; }
    public int TalentId { get; set; }
    public int SkillId { get; set; }
    public string Kind { get; set; } = "";
    public string Category { get; set; } = "";
    public int CategoryOrder { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MinimumLevel { get; set; }
    public int MaximumLevel { get; set; }
    public bool IsAlien { get; set; }
    public bool IsInspired { get; set; }
    public bool CanChangeTalent { get; set; }
    public List<SkillOption> SkillOptions { get; set; } = new();
}

internal sealed class SkillOption
{
    public int TalentId { get; set; }
    public int SkillId { get; set; }
    public string Name { get; set; } = "";
    public string JobName { get; set; } = "";
    public int MaximumLevel { get; set; }
}
