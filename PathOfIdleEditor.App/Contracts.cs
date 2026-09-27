using System.ComponentModel;
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
    internal const int Version = 1;
}

public sealed class EditorRequest
{
    public string Action { get; set; } = "";
    public EquipmentEdit? Equipment { get; set; }
    public EquipmentReplaceEdit? EquipmentReplace { get; set; }
    public HeroEdit? Hero { get; set; }
    public InventoryItemEdit? InventoryItem { get; set; }
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
    public InventorySnapshot? Inventory { get; set; }
    public LordEdit? Lord { get; set; }
}

public sealed class EditorSnapshot
{
    public List<EquipmentTemplate> EquipmentTemplates { get; set; } = new();
    public List<RuleOption> EquipmentQualities { get; set; } = new();
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

public sealed class InventoryItemEdit
{
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
    public List<AffixEdit> Affixes { get; set; } = new();
    public string Display =>
        $"[{FieldIndex}] {Name} · {QualityName} · 等级 {Level} · 锻造 +{ForgeLevel} · {Affixes.Count} 条词条";
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
    public List<AffixEdit> Affixes { get; set; } = new();
}

public sealed class EquipmentRules
{
    public int MaximumAffixCount { get; set; }
    public int MaximumAffixLevel { get; set; }
    public List<int> AllowedForgeLevels { get; set; } = new();
    public Dictionary<int, int> AffixQualityLimits { get; set; } = new();
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
    public List<AffixValueRange> ValueRanges { get; set; } = new();
    public string Display => $"{Id} · {Name}";
}

public sealed class AffixEdit : INotifyPropertyChanged
{
    private int _level;
    private int? _value;
    public int Id { get; set; }
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueRange)));
            var range = ValueRanges.FirstOrDefault(item => item.Level == value);
            if (range != null)
                Value = range.Maximum;
        }
    }
    public int? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
    private List<AffixValueRange> _valueRanges = new();
    public List<AffixValueRange> ValueRanges
    {
        get => _valueRanges;
        set
        {
            _valueRanges = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueRanges)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueRange)));
        }
    }
    public string ValueRange
    {
        get
        {
            var range = ValueRanges.FirstOrDefault(item => item.Level == Level);
            return range == null ? "游戏特殊随机/未提供" : $"{range.Minimum}-{range.Maximum}";
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AffixValueRange
{
    public int Level { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; }
}

public sealed class AffixCategoryOption
{
    public int Quality { get; set; }
    public string Name { get; set; } = "";
    public int Limit { get; set; }
    public string Display => $"{Name} · 最多 {Limit} 条";
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
    public float Value { get; set; }
    public int MinimumValue { get; set; }
    public int MaximumValue { get; set; }
    public string ValueRange => $"{MinimumValue}-{MaximumValue}";
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
