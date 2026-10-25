using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PathOfIdleEditor.App;

// ============================================================
// 【协议文件】桌面端与桥接 Mod 之间的命名管道协议定义。
//
// 服务端 PathOfIdleEditor.Contracts 里有一份几乎相同的类型定义，
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
    // v4：污染词条独立成组 —— AffixEdit 新增 IsCorrupt/IsCorruptLocked、AffixOption 新增
    //     IsCorruptPool、EquipmentInventoryEntry 新增 LegacyCorruptText、EquipmentRules 新增 CorruptGroupName。
    // v5：开孔控制 —— EquipmentEdit 新增 SlotCount、EquipmentRules 新增孔数候选/上限/等级门槛、
    //     EquipmentInventoryEntry 新增 SlotCount/MaximumSlotCount/Slots（SlotRuneEdit）。
    // v6：孔位类型可选 —— EquipmentEdit 新增 SlotTypes、EquipmentRules 新增 IsWeaponSlotLayout。
    // v7：开孔并入词条表 —— 孔位作为 AffixValueKind.SlotType 的词条行（AffixEdit.SocketType = 0/1/2）；
    //     移除 EquipmentEdit.SlotCount/SlotTypes、EquipmentInventoryEntry.Slots、SlotTypeEdit、SlotRuneEdit；
    //     EquipmentRules 新增 SocketGroupName。
    // v8：越界添加 —— EquipmentEdit 新增 OverrideLimits（忽略词条/孔位数量限制）。
    // v9：额度区间显示 —— EquipmentRules 新增 AffixQualityMinimums（各档位原生下限）。
    internal const int Version = 9;
}

// 词条的取值方式：决定「词条等级」是否可编辑、以及参考数值怎么显示。
public enum AffixValueKind
{
    // 按装备质量档位计算，等级可编辑，参考值是三个档位各自的实际值。
    GradeBased = 0,
    // 固定值（原生 valueType 0/2/4：取下端或上端）。
    Fixed = 1,
    // 区间随机（原生 valueType 1 均匀随机、3 有符号特殊随机）。
    RandomRange = 2,
    // 镶嵌孔位行：这行的「数值」是孔位类型码（0 随机 / 1 攻击 / 2 防御），
    // Id/Name/Level 只回显该孔已镶嵌的符文（没有则 0 / 空 / 0）。
    SlotType = 3
}

// 数值纠正规则，供「词条等级」与「装备 / 锻造等级」共用（需求 2.1.2 与 3.2 是同一套规则）。
internal static class ValueCoercion
{
    // 小于下界 → 下界；范围内的小数 → 取整；其它非法值 → 上界。
    internal static int Coerce(double raw, int minimum, int maximum)
    {
        if (double.IsNaN(raw)) return maximum;
        if (raw < minimum) return minimum;
        if (raw > maximum) return maximum;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (rounded < minimum) return minimum;
        return rounded > maximum ? maximum : rounded;
    }

    // 文本入口：空 / 非数字 → 上界。
    internal static int Coerce(string? text, int minimum, int maximum)
        => double.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var raw)
            ? Coerce(raw, minimum, maximum)
            : maximum;

    // 数量列修正（需求）：超出上限 → 上限；低于下限 → 0；小数 → 取整；其它非法值 → 当前值。
    // maximum <= 0 表示无上限（与原版 GetResMax <= 0 的语义一致）。
    internal static int CoerceCount(string? text, int current, int maximum)
    {
        var fallback = Math.Max(0, current);
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var raw) || double.IsNaN(raw))
            return fallback;
        if (raw <= 0) return 0;
        if (raw >= int.MaxValue) return maximum > 0 ? maximum : int.MaxValue;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (rounded < 0) return 0;
        return maximum > 0 && rounded > maximum ? maximum : rounded;
    }
}

public sealed class EditorRequest
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

public sealed class EditorResponse
{
    public int ProtocolVersion { get; set; } = BridgeProtocol.Version;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public EditorSnapshot? Snapshot { get; set; }
    public EquipmentRules? EquipmentRules { get; set; }
    public EquipmentInventorySnapshot? EquipmentInventory { get; set; }
    // "affixReference" 的回包：与请求同序，但补全了参考值字段。
    public List<AffixEdit>? AffixReferences { get; set; }
    public InventorySnapshot? Inventory { get; set; }
    public LordEdit? Lord { get; set; }
}

public sealed class EditorSnapshot
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

public sealed class LordEdit
{
    public int Level { get; set; }
    public int MaximumLevel { get; set; }
    public List<LordJobEdit> Jobs { get; set; } = new();
    public List<LordJobLevelRule> JobLevelRules { get; set; } = new();
}

public sealed class LordJobLevelRule
{
    public int Level { get; set; }
    public int RequiredLordLevel { get; set; }
    public int TotalAttributePoints { get; set; }
    public int MaximumTalentBonusLevel { get; set; }
}

public sealed class LordJobEdit : INotifyPropertyChanged
{
    private int _level;
    private int _requiredLordLevel;
    private int _totalAttributePoints;
    private int _strength;
    private int _strengthMinimum;
    private int _strengthMaximum;
    private int _dexterity;
    private int _dexterityMinimum;
    private int _dexterityMaximum;
    private int _intelligence;
    private int _intelligenceMinimum;
    private int _intelligenceMaximum;

    public int JobId { get; set; }
    public string JobName { get; set; } = "";

    public int Level
    {
        get => _level;
        set => SetField(ref _level, value);
    }

    public int MaximumLevel { get; set; }

    public int RequiredLordLevel
    {
        get => _requiredLordLevel;
        set => SetField(ref _requiredLordLevel, value);
    }

    public int TotalAttributePoints
    {
        get => _totalAttributePoints;
        set => SetField(ref _totalAttributePoints, value);
    }

    public int Strength
    {
        get => _strength;
        set => SetField(ref _strength, value);
    }

    public int StrengthMinimum
    {
        get => _strengthMinimum;
        set { if (SetField(ref _strengthMinimum, value)) OnPropertyChanged(nameof(StrengthRange)); }
    }

    public int StrengthMaximum
    {
        get => _strengthMaximum;
        set { if (SetField(ref _strengthMaximum, value)) OnPropertyChanged(nameof(StrengthRange)); }
    }

    public int Dexterity
    {
        get => _dexterity;
        set => SetField(ref _dexterity, value);
    }

    public int DexterityMinimum
    {
        get => _dexterityMinimum;
        set { if (SetField(ref _dexterityMinimum, value)) OnPropertyChanged(nameof(DexterityRange)); }
    }

    public int DexterityMaximum
    {
        get => _dexterityMaximum;
        set { if (SetField(ref _dexterityMaximum, value)) OnPropertyChanged(nameof(DexterityRange)); }
    }

    public int Intelligence
    {
        get => _intelligence;
        set => SetField(ref _intelligence, value);
    }

    public int IntelligenceMinimum
    {
        get => _intelligenceMinimum;
        set { if (SetField(ref _intelligenceMinimum, value)) OnPropertyChanged(nameof(IntelligenceRange)); }
    }

    public int IntelligenceMaximum
    {
        get => _intelligenceMaximum;
        set { if (SetField(ref _intelligenceMaximum, value)) OnPropertyChanged(nameof(IntelligenceRange)); }
    }

    public List<LordJobAttributeRule> AttributeRules { get; set; } = new();
    public List<LordTalentBonusEdit> TalentBonuses { get; set; } = new();

    public string StrengthRange => $"{StrengthMinimum}-{StrengthMaximum}";
    public string DexterityRange => $"{DexterityMinimum}-{DexterityMaximum}";
    public string IntelligenceRange => $"{IntelligenceMinimum}-{IntelligenceMaximum}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class LordJobAttributeRule
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

public sealed class LordTalentBonusEdit : INotifyPropertyChanged
{
    private int _level;
    private int _maximumLevel;

    public int TalentId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";

    public int Level
    {
        get => _level;
        set => SetField(ref _level, value);
    }

    public int MaximumLevel
    {
        get => _maximumLevel;
        set => SetField(ref _maximumLevel, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class InventorySnapshot
{
    public List<InventoryTemplate> AvailableItems { get; set; } = new();
    public List<InventoryItemEdit> BagItems { get; set; } = new();
}

public sealed class InventoryTemplate
{
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Quality { get; set; }
    public int Level { get; set; }
    public string LevelDescription { get; set; } = "";
    public string Display => string.IsNullOrWhiteSpace(LevelDescription)
        ? $"{Id} · {Name}"
        : $"{Id} · {Name} · {LevelDescription}";
}

public sealed class InventoryItemEdit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
    // 客户端专用：绑定快照时的原始数量，用于找出「手动改动过」的行（不参与协议字段对齐）。
    public int OriginalCount { get; set; }

    // 表格里编辑的文本：超出上限→上限，低于下限→0，小数→取整，其它非法值→保持当前值。
    public string CountText
    {
        get => Count.ToString(CultureInfo.InvariantCulture);
        set
        {
            Count = ValueCoercion.CoerceCount(value, Count, Maximum);
            Raise(nameof(Count));
            Raise(nameof(CountText));
        }
    }
}

public sealed class InventoryAddEdit
{
    public int Type { get; set; }
    public int Id { get; set; }
    public int Quality { get; set; }
    public int Level { get; set; }
    public int Count { get; set; }
}

public sealed class EquipmentInventoryEntry
{
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
    // 装备编辑器列表：按背包 slot 排序并显示 slot-名称-品质-等级（需求 5.1）。
    public string Display => $"{FieldIndex}-{Name}-{QualityName}-{Level}";
}


public sealed class EquipmentInventorySnapshot
{
    public List<EquipmentInventoryEntry> Entries { get; set; } = new();
}

public sealed class EquipmentReplaceEdit
{
    public string Guid { get; set; } = "";
    public EquipmentEdit Equipment { get; set; } = new();
}

public sealed class RuleOption
{
    public int Value { get; set; }
    public string Name { get; set; } = "";
    public string Display => $"{Value} · {Name}";
}

public sealed class EquipmentTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Part { get; set; }
    public string PartName { get; set; } = "";
    public int BaseQuality { get; set; }
    public List<int> AllowedQualities { get; set; } = new();
    public string Display => $"{Id} · {Name}  /  {PartName}";
}

public sealed class EquipmentEdit
{
    public int TemplateId { get; set; }
    public int Quality { get; set; }
    public int Level { get; set; }
    public int ForgeLevel { get; set; }
    // 主属性档位（1/2/3）。0 表示不指定，沿用原生 CreateEquip 的随机结果。
    public int MainAttrGrade { get; set; }
    // 越界添加：忽略词条/孔位的数量限制（行级 ForceOverflow 只作用于该行，两者独立）。
    public bool OverrideLimits { get; set; }
    // 开孔与孔位类型都在 Affixes 里（ValueKind == AffixValueKind.SlotType 的行）；
    // 镶嵌组为空 = 不干预开孔，交给原生 CreateEquip 随机。
    public List<AffixEdit> Affixes { get; set; } = new();
}

public sealed class EquipmentRules
{
    public int MaximumAffixCount { get; set; }
    public int MaximumAffixLevel { get; set; }
    // 单件装备当前可用的圣碑腐蚀次数上限；已计入 AffixQualityLimits[稀有] 与 MaximumAffixCount，
    // 单独回传用于界面标注。
    public int MaximumCorruptCount { get; set; }
    // 「污染」分组名（由桥接下发，界面用它给污染词条单独分组）。
    public string CorruptGroupName { get; set; } = "污染";
    // ── 开孔（SaveItemData.slotCount / slotRuneList）──
    public List<int> SlotCountCandidates { get; set; } = new();
    public List<int> ForgeSlotCountCandidates { get; set; } = new();
    public int MaximumSlotCount { get; set; }
    // 开孔等级门槛（TConstant.equipSlotLevel）。
    public int SlotLevelRequirement { get; set; }
    // 「镶嵌」分组名（组名会拼上上限：镶嵌·最多 N 条）。
    public string SocketGroupName { get; set; } = "镶嵌";
    // 默认孔位布局：true = 武器（默认攻击孔，原生"特殊孔位"是最后一个）；false = 防具（默认防御孔，特殊孔位是第一个）。
    public bool IsWeaponSlotLayout { get; set; }
    public List<int> AllowedForgeLevels { get; set; } = new();
    public Dictionary<int, int> AffixQualityLimits { get; set; } = new();
    // 每个档位的原生下限（与上限配对显示成区间，例如 "稀有 2~4 条"）。添加校验仍用上限。
    public Dictionary<int, int> AffixQualityMinimums { get; set; } = new();
    public Dictionary<int, string> AffixQualityNames { get; set; } = new();
    public List<AffixOption> AllowedAffixes { get; set; } = new();
    public List<AffixEdit> GeneratedAffixes { get; set; } = new();
}

public sealed class AffixOption
{
    public int Id { get; set; }
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Name { get; set; } = "";
    // ── 参考数值（由游戏规则算出，仅作展示）──
    public AffixValueKind ValueKind { get; set; } = AffixValueKind.GradeBased;
    public List<int> GradeValues { get; set; } = new();
    public int FixedValue { get; set; }
    public int RandomMinimum { get; set; }
    public int RandomMaximum { get; set; }
    // ── 镶嵌孔位行（ValueKind == AffixValueKind.SlotType 时有效）──
    // 孔位类型码：0 = 随机、1 = 攻击孔、2 = 防御孔（参考数值列固定显示 "0:随机;1:攻击;2:防御"）。
    // 「强制越界」勾选后可以写入不符合原生规则的孔位类型。
    public int SocketType { get; set; }

    // 等级可编辑区间：精良/稀有 = 1..affixMaxLevel；独特 = 1；传说/神话 = affixMaxLevel（固定）。
    public int LevelMinimum { get; set; }
    public int LevelMaximum { get; set; }
    // 污染候选池标记：真表示这条候选也能作为「污染」词条加入（原生腐蚀取自档位3池）。
    public bool IsCorruptPool { get; set; }
    public string Display => $"{Id} · {Name}";
}

public sealed class AffixEdit : INotifyPropertyChanged
{
    private int _level;
    private int _displayValue;
    private bool _forceOverflow;
    private bool _isCorrupt;
    private bool _isCorruptLocked;
    private int _socketType;

    public int Id { get; set; }
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Name { get; set; } = "";

    // ── 参考数值（桥接下发的游戏规则）──
    public AffixValueKind ValueKind { get; set; } = AffixValueKind.GradeBased;
    // GradeBased：装备质量档位 1/2/3 各自的实际值。
    public List<int> GradeValues { get; set; } = new();
    public int FixedValue { get; set; }
    public int RandomMinimum { get; set; }
    public int RandomMaximum { get; set; }

    // ── 装备实例上的数值（仅响应）──
    // 游戏解析出来的实际值。
    public int ResolvedValue { get; set; }
    // 存档里保存的原始 value。
    public int StoredValue { get; set; }

    // ── 污染（圣碑腐蚀）──
    // 原生 SaveAffixData.isCorrupt：1.1.0 起重做后的腐蚀固定给一条随机污染词缀（等级 1/3/5）。
    // 读回后必须原样回写，否则游戏面板不再显示污染、也无法用【涤魂泪滴】净化。
    public bool IsCorrupt
    {
        get => _isCorrupt;
        set
        {
            if (_isCorrupt == value) return;
            _isCorrupt = value;
            Raise(nameof(IsCorrupt));
            Raise(nameof(CategoryText));
            Raise(nameof(GroupName));
        }
    }

    // 原生 SaveAffixData.isCorruptLocked（终焉阶段 6/7 开放，上限见 TDoomStage.affixLockCount）。
    public bool IsCorruptLocked
    {
        get => _isCorruptLocked;
        set
        {
            if (_isCorruptLocked == value) return;
            _isCorruptLocked = value;
            Raise(nameof(IsCorruptLocked));
            Raise(nameof(CategoryText));
        }
    }

    // 表格「词条描述」列：孔位行没镶嵌符文时写「未镶嵌」，其余原样显示名字。
    public string DescriptionText => IsSocket && string.IsNullOrEmpty(Name) ? "未镶嵌" : Name;

    // 表格「类别」列：污染行/孔位行单独显示，锁定状态附在后面。
    public string CategoryText => IsSocket
        ? "镶嵌"
        : IsCorrupt
            ? (IsCorruptLocked ? "污染 · 已锁定" : "污染")
            : QualityName;

    // 表格分组键：污染词条、镶嵌孔位各自单独成组，其余按档位名分组。
    public string GroupName => IsSocket ? SocketGroupLabel : IsCorrupt ? "污染" : QualityName;

    // 分组配色用的短标识（镶嵌组名带「·最多N条」，不能用组名精确匹配触发器）：
    // 返回档位名 / 「污染」 / 「镶嵌」，界面按它选分组头的底色与文字色。
    public string GroupStyleKey => IsSocket ? "镶嵌" : IsCorrupt ? "污染" : QualityName;

    // 词条等级上限（由 EquipmentRules 注入，不参与协议），用于校验用户输入。
    public int MaximumLevel { get; set; } = 1;
    // ── 桥接下发的等级可编辑区间（协议字段）──
    // 固定档位（独特 = 1、传说/神话 = affixMaxLevel）时两者相等，输入会被修正回该值。
    public int LevelMinimum { get; set; }
    public int LevelMaximum { get; set; }
    // 用户输入时的等级上下界：优先用桥接下发的区间，缺失时退回规则注入的上限。
    private int LevelLowerBound => Math.Max(1, LevelMinimum);
    private int LevelUpperBound => LevelMaximum > 0 ? Math.Max(1, LevelMaximum) : Math.Max(1, MaximumLevel);

    // 取值范围是否随词条等级变动：只有按档位计算的词条才是（需求 2.1.1）。
    public bool LevelApplicable => ValueKind == AffixValueKind.GradeBased;

    // 词条等级。不适用时固定为 -1（表格显示为 *）。
    // 注意这里**不做**纠正：反序列化时 MaximumLevel 还没注入，纠正会误伤响应里的值。
    public int Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            Raise(nameof(Level));
            Raise(nameof(LevelText));
        }
    }

    // 表格里编辑的文本：始终显示实际等级（不再显示 *）。
    // 孔位行（镶嵌组）显示的是该孔已镶嵌符文的等级，没镶嵌就是 0，且不允许编辑。
    public string LevelText
    {
        get => _level.ToString();
        set
        {
            if (IsSocket) return;
            // 超下界取下界、超上界取上界、小数取整、其它非法值取上界。
            Level = ValueCoercion.Coerce(value, LevelLowerBound, LevelUpperBound);
            Raise(nameof(LevelText));
        }
    }

    // 装备等级变化后重新夹紧等级（需求 2.1.3）。
    public void ClampLevel()
    {
        if (IsSocket) return;
        Level = ValueCoercion.Coerce(_level, LevelLowerBound, LevelUpperBound);
        Raise(nameof(LevelText));
    }

    // 装备等级变化时，把「等级影响数值」的词条等级顶到上界（生成器需求）：
    // 装备等级提高后，精良/稀有的词条等级默认跟着用满，不必逐个手改。
    // 固定档位（独特=1、传说/神话=affixMaxLevel）不受影响。
    internal void LevelToUpperBound()
    {
        if (!LevelApplicable)
            return;
        Level = LevelUpperBound;
        Raise(nameof(LevelText));
    }

    // 参考数值（需求 2.2）：档位相关 → 低;中;高；随机 → 下界~上界；固定 → 单值。
    // 孔位行固定显示类型码图例。
    public string ReferenceText => ValueKind switch
    {
        AffixValueKind.GradeBased => GradeValues.Count > 0 ? string.Join(";", GradeValues) : "—",
        AffixValueKind.RandomRange => $"{RandomMinimum}~{RandomMaximum}",
        AffixValueKind.SlotType => "0:随机;1:攻击;2:防御",
        _ => FixedValue.ToString()
    };

    // ── 镶嵌孔位行 ──
    // ValueKind == SlotType 的行代表一个孔位：Id/Name/Level 是已镶嵌符文的回显，SocketType 才是可编辑内容。
    public bool IsSocket => ValueKind == AffixValueKind.SlotType;

    // 镶嵌组的组名（含上限，例如「镶嵌·最多3条」），由界面在构造行时按规则注入。
    public string SocketGroupLabel { get; set; } = "镶嵌";

    // 孔位类型码（0 随机 / 1 攻击 / 2 防御）；表格「数值」列直接编辑它。
    public int SocketType
    {
        get => _socketType;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (_socketType == clamped) return;
            _socketType = clamped;
            Raise(nameof(SocketType));
            Raise(nameof(ValueText));
        }
    }

    // 「装备自带」：档位 8 独有技能词条与传说/神话专属词条由装备本身决定。
    // 这类行不允许删除（由桥接下发，数值仍看值能否写入存档）。
    public bool EquipmentFixed { get; set; }

    // 传说(4)/神话(5)/独特(8) 词条：只作展示，禁止删除；数值只有在「能写进存档」且勾选
    // 「强制越界」之后才可改（档位 4/8 与神话非 specialType==1 的行是运行时现算，永远不可改）。
    public bool IsTierLocked => Quality is 4 or 5 or 8;

    // 供界面绑定：控制删除按钮是否可用。
    public bool CanRemove => !EquipmentFixed && !IsTierLocked;

    // 数值是否只读（按档位区分，实测依据见 docs/WORKFLOW.md §18）：
    //   · 4 传说 / 8 独特技能：展示期按模板现算，条目不在 affixList、哨兵无处依附 → 永远只读；
    //   · 5 神话的**普通行**（ValueKind != RandomRange）：同样现算 → 永远只读；
    //   · 5 神话的 specialType==1 行（ValueKind == RandomRange）：走 AddMythAffix 持久化，**可写**，
    //     但按需求必须先勾「强制越界」才放行（勾选后哨兵生效）；
    //   · 1/2/3 通用池：不在此列 —— 直接可写（打哨兵后原生重算会跳过）。
    public bool ValueReadOnly => IsTierLocked &&
                                 (Quality == 4 || Quality == 8 ||
                                  (Quality == 5 && (ValueKind != AffixValueKind.RandomRange || !ForceOverflow)));

    // 是否占用「品级词条额度」：只有会真正写进存档的行才占（档位 1/2/3，以及档位 5 的 specialType==1 行）。
    // 运行时现算并且不落存档的行——传说专属档位 4、独有技能档位 8、神话里非 specialType==1 的档位 5——
    // 不占额度（原生只在运行时按模板重建它们，见 GameAssembly.c:1623227-1623245 / :1623344-1623349）。
    // 判定与桥接侧的计数规则保持一致。孔位行（镶嵌组）不算词条额度。
    public bool ConsumesAffixQuota => !IsSocket &&
                                      (Quality is 1 or 2 or 3 ||
                                       (Quality == 5 && ValueKind == AffixValueKind.RandomRange));

    // 强制越界（需求 2.3）：勾选后数值写入存档并用哨兵生效，未勾选则清除哨兵。
    // 对神话专属行（档位5 specialType==1）它还是"解锁编辑"的开关 —— ValueReadOnly 依赖它。
    public bool ForceOverflow
    {
        get => _forceOverflow;
        set
        {
            if (_forceOverflow == value) return;
            _forceOverflow = value;
            Raise(nameof(ForceOverflow));
            Raise(nameof(ValueText));
            Raise(nameof(ValueReadOnly));
        }
    }

    // 表格里显示的数值（需求 2.4）；孔位行显示类型码（0 随机 / 1 攻击 / 2 防御）。
    public string ValueText
    {
        // 打字中的未完成文本（"-5"、"12."）优先返回：
        // 这样规则/参考值的异步刷新触发 PropertyChanged 时，只会把用户正在输入的内容原样推回文本框，
        // 而不是用显示值（如上界 10）把它抹掉 —— 这是「负号输不进去」的真正原因。
        get => _pendingText ?? (IsSocket ? _socketType.ToString() : _valueCleared ? "" : _displayValue.ToString());
        set
        {
            if (!IsSocket)
            {
                SetDisplayValue(value);
                return;
            }
            // 孔位行：只接受 0/1/2，其它输入按 0（随机）处理。
            _pendingText = null;
            _socketType = int.TryParse(value?.Trim(), out var parsed) ? Math.Clamp(parsed, 0, 2) : 0;
            Raise(nameof(SocketType));
            Raise(nameof(ValueText));
        }
    }

    private bool _valueEdited;
    // 「留空」= 交给游戏随机。生成器与修改器共用同一套规则。
    private bool _valueCleared;
    // 打字过程中的未完成文本（"-"、"-5." 等），提交时才做修正。
    private string? _pendingText;

    // 供克隆复制「用户是否改过数值」（需求 2.3 修正）。
    internal bool ValueEdited
    {
        get => _valueEdited;
        set => _valueEdited = value;
    }

    // 克隆时把用户编辑过的**显示值本身**也带过去。
    // 关键：ApplyResponse() 会把克隆体的显示值重置回存档/参考值，若只复制 ValueEdited 标记，
    // 克隆体就会「声称被编辑过、但显示值是旧值」，提交出去的仍是旧值（实测六轮日志均如此）。
    internal void CopyEditedValueFrom(AffixEdit source)
    {
        // 只看真正的「用户编辑过」标记。
        // 不能用「显示值 ≠ 存档值」判断：生成器行的存档值是 0、显示的是参考上界，恒不相等，
        // 会把所有未编辑的随机词条一并钉成参考值（实测就是这样把 10 写满了）。
        if (!source._valueEdited)
            return;
        _valueCleared = source._valueCleared;
        _displayValue = source._displayValue;
        _valueEdited = true;
        Raise(nameof(ValueText));
    }

    // 提交给桥接的数值（需求 2.3 修正：随机词条不必勾选强制越界也能改）：
    //   · 勾选强制越界 → 一律写入用户设定值（越界也照写）；
    //   · 随机词条（神话专属 specialType==1）→ 用户改过就写入改后的值，不必勾选；
    //   · 编辑器里已有的随机词条 → 保留存档里的值，避免再次提交时被重新随机；
    //   · 其余情况 → null，由桥接走原生计算（档位计算 / 取下端 / 随机）。
    public int? Value
    {
        get
        {
            // 留空 → 交给游戏原生随机（生成器与修改器共用）。
            if (_valueCleared)
                return null;
            // 只读档位（传说/独特/神话普通行）连守卫都不下发：
            // 它们没有可依附的存档条目，打哨兵只会污染存档且毫无效果。
            if (ValueReadOnly)
                return null;
            if (ForceOverflow)
                return _displayValue;
            if (ValueKind != AffixValueKind.RandomRange)
                return null;
            // 随机档位（神话专属）：一律用界面显示值 ——
            //   生成器未修改时显示值就是参考上界 → 生成结果 = 上界（不再"显示上界、实际随机"）；
            //   修改器里显示值是当前存档值 → 原样保留；
            //   用户改过 → 用改后的值。
            // 只有留空（上面的 _valueCleared）才交回原生随机。
            return _displayValue;
        }
    }

    // 当前显示的数值：未勾选时为游戏解析值，勾选后为用户设定值。提交时由它生成 Value。
    public int DisplayValue => _displayValue;

    // 纠正上限：勾选强制越界时，非数值输入修正到参考上界。
    private int ReferenceUpperBound => ValueKind switch
    {
        AffixValueKind.GradeBased => GradeValues.Count > 0 ? GradeValues.Max() : ResolvedValue,
        AffixValueKind.RandomRange => RandomMaximum,
        _ => FixedValue
    };

    // 绑定用 LostFocus 触发：setter 只在提交（离开单元格/回车）时被调用，
    // 所以这里直接做修正 —— 打字过程中的内容不会经过这里，F2/删除/覆盖输入都不受影响。
    private void SetDisplayValue(string? text)
    {
        CommitValueText(text);
        Raise(nameof(ValueText));
    }

    // 回车 / 失焦时提交：这时才按规则修正。
    //   留空 → 交给游戏随机；小数 → 截断取整；越界 → 夹到上下界（勾强制越界则不夹）；非法 → 参考上界。
    internal void CommitValueText(string? text)
    {
        // 只读行（传说/独特专属）：数值由装备模板展示期现算，任何输入都无法生效，直接还原显示值。
        if (ValueReadOnly)
        {
            _pendingText = null;
            Raise(nameof(ValueText));
            return;
        }
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            // 留空 = 交给游戏原生随机（生成器与修改器共用）。
            _valueCleared = true;
            _valueEdited = true;
            _pendingText = null;
            Raise(nameof(ValueText));
            return;
        }
        _pendingText = null;
        _valueCleared = false;
        var isNumber = double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var raw);
        int coerced;
        if (_forceOverflow)
        {
            // 勾选强制越界：不修正上下界，只把小数截断为整数；非数字回退参考上界。
            coerced = isNumber && !double.IsNaN(raw) ? (int)raw : ReferenceUpperBound;
        }
        else if (ValueKind == AffixValueKind.RandomRange)
        {
            var minimum = Math.Min(RandomMinimum, RandomMaximum);
            var maximum = Math.Max(RandomMinimum, RandomMaximum);
            // 非法值取上界（原规则）；小数截断；再夹到区间。
            coerced = Math.Clamp(isNumber && !double.IsNaN(raw) ? (int)raw : maximum, minimum, maximum);
        }
        else
        {
            // 非随机值自动纠正为游戏算出来的值。
            coerced = ResolvedValue;
        }
        // 只有「值真的被改动」才算用户编辑：否则未编辑的行会被当成手动值发出去。
        if (coerced != _displayValue)
            _valueEdited = true;
        _displayValue = coerced;
        Raise(nameof(ValueText));
    }

    // 用桥接下发的数据刷新显示值（等级、参考值、数值）。
    // 注意：这里**不**夹紧等级 —— MaximumLevel 是客户端专有字段，克隆/反序列化时可能还是默认值 1，
    // 在此夹紧会把桥接下发的等级误伤成 1。夹紧只由 LevelText 的 setter 与显式 ClampLevel() 负责。
    public void ApplyResponse()
    {
        // 桥接下发的数据是权威值：清掉「用户改过」标记与打字中的临时文本。
        _valueEdited = false;
        _pendingText = null;
        // 随机档位（神话专属）在存档里的值就是它的实际生效值，优先显示它；
        // 新生成（还没存档值）时才退回参考上界。否则应用成功后界面会一直显示参考值 10。
        _displayValue = _forceOverflow || (ValueKind == AffixValueKind.RandomRange && StoredValue != 0)
            ? StoredValue
            : ResolvedValue == 0 && StoredValue != 0 ? StoredValue : ResolvedValue;
        Raise(nameof(Level));
        Raise(nameof(LevelText));
        Raise(nameof(ReferenceText));
        Raise(nameof(ValueText));
    }

    // 只刷新参考值（需求 2.2：词条等级变动时参考数值联动）。同样不动等级。
    public void ApplyReference()
    {
        // 规则刷新只负责参考列。已有存档值的随机档位（神话专属）要显示**存档值本身** ——
        // 否则每次规则刷新都会把它盖成参考上界（实测：改成 7 游戏内已生效，界面却仍显示 10）。
        var keepStored = ValueKind == AffixValueKind.RandomRange && StoredValue != 0;
        if (!_forceOverflow && !_valueEdited && !keepStored)
            _displayValue = ResolvedValue;
        Raise(nameof(ReferenceText));
        Raise(nameof(ValueText));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AffixCategoryOption
{
    public int Quality { get; set; }
    public string Name { get; set; } = "";
    public int Limit { get; set; }
    // 真表示这是独立的「污染」分组：候选池取 AffixOption.IsCorruptPool，新增的行会带上污染标记。
    public bool IsCorrupt { get; set; }
    // 真表示这是「镶嵌」分组：每行一个孔位，新增的行是 AffixValueKind.SlotType 的孔位行。
    public bool IsSocket { get; set; }
    public string Display => IsSocket
        ? $"{Name} · 最多 {Limit} 条"
        : IsCorrupt
            ? $"{Name} · 可腐蚀 {Limit} 次"
            : $"{Name} · 最多 {Limit} 条";
}

public sealed class HeroEdit
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
    public string Display => $"{Name}  ·  ID {UniqueId}";
}

public sealed class GrowthAttributeEdit
{
    public int Type { get; set; }
    public string Name { get; set; } = "";
    /// <summary>每级成长值。</summary>
    public float Value { get; set; }
    public int MinimumValue { get; set; }
    public int MaximumValue { get; set; }
    /// <summary>当前点数。</summary>
    public float Points { get; set; }
    public int PointMinimum { get; set; }
    public int PointMaximum { get; set; }
    /// <summary>"base"=生命/魔力，"main"=力量/敏捷/智力。</summary>
    public string Group { get; set; } = "";
    /// <summary>界面显示名：hp/mp 显示为生命/魔力。</summary>
    public string DisplayName => Name switch
    {
        "hp" => "生命",
        "mp" => "魔力",
        _ => Name
    };
    public string PointRange => PointMinimum == PointMaximum ? $"{PointMinimum}" : $"{PointMinimum} ~ {PointMaximum}";
    public string ValueRange => MinimumValue == MaximumValue ? $"{MinimumValue}" : $"{MinimumValue} ~ {MaximumValue}";
}

public sealed class TalentSlotEdit : INotifyPropertyChanged
{
    private int _talentId;
    private int _skillId;
    private string _name = "";
    private int _level;
    private int _minimumLevel;
    private int _maximumLevel;
    private List<SkillOption> _skillOptions = new();

    public int SlotId { get; set; }
    public bool IsAlien { get; set; }
    public bool IsInspired { get; set; }
    public bool CanChangeTalent { get; set; }
    public int TalentId
    {
        get => _talentId;
        set => SetField(ref _talentId, value);
    }
    public int SkillId
    {
        get => _skillId;
        set => SetField(ref _skillId, value);
    }
    public string Kind { get; set; } = "";
    public string Category { get; set; } = "";
    public int CategoryOrder { get; set; }
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }
    public int Level
    {
        get => _level;
        set => SetField(ref _level, value);
    }
    public int MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetField(ref _minimumLevel, value))
                OnPropertyChanged(nameof(LevelRange));
        }
    }
    public int MaximumLevel
    {
        get => _maximumLevel;
        set
        {
            if (SetField(ref _maximumLevel, value))
                OnPropertyChanged(nameof(LevelRange));
        }
    }
    public List<SkillOption> SkillOptions
    {
        get => _skillOptions;
        set => SetField(ref _skillOptions, value);
    }
    public string LevelRange => $"{MinimumLevel} - {MaximumLevel}";

    public event PropertyChangedEventHandler? PropertyChanged;

    // 仅通知当前技能行发生变化，避免刷新整张 DataGrid 后递归创建 ComboBox。
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SkillOption
{
    public int TalentId { get; set; }
    public int SkillId { get; set; }
    public string Name { get; set; } = "";
    public string JobName { get; set; } = "";
    public int MaximumLevel { get; set; }
    public string Display => string.IsNullOrWhiteSpace(JobName)
        ? $"{Name}  ·  上限 {MaximumLevel}"
        : $"{Name}  ·  {JobName}  ·  上限 {MaximumLevel}";
}
