using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace PathOfIdleEditor.App;

public partial class MainWindow : Window
{
    // 需求 4：生成器与编辑器各自持有一份完整状态，切换时保存/恢复，两边所有值互不影响。
    // 之前两边共用 _affixes / _equipmentRules，导致切换子页或刷新会互相覆盖（也是刷新后无法生成的根因）。
    private sealed class EquipmentModeState
    {
        internal EquipmentRules? Rules;
        internal List<AffixEdit> Affixes = new();
        internal int? TemplateId;
        internal int Quality;
        internal int Level;
        internal int ForgeLevel;
        internal int MainAttrGrade;
        internal EquipmentInventoryEntry? EditEntry;
        // 上次加载规则时用的「模板 + 品级」，用来区分「结构变化」与「仅数值变化」：
        // 只有结构变化才重建默认词条，改装备等级 / 装备质量不会覆盖用户已编辑的词条。
        internal int LastTemplateId;
        internal int LastQuality;
        // 上次规则刷新用的装备等级：用于识别「装备等级变化」并顶高受等级影响的词条等级。
        internal int LastLevel;
    }

    private readonly ObservableCollection<AffixEdit> _affixes = new();
    private readonly ICollectionView _affixView;
    private readonly EquipmentModeState _generatorState = new();
    private readonly EquipmentModeState _editorState = new();
    private EditorSnapshot? _snapshot;
    private ICollectionView? _equipmentView;
    private bool _equipmentEditMode;          // false=生成器，true=编辑器
    // 「词条编辑」信息栏正文（不含镶嵌信息）；镶嵌信息由 UpdateSlotInfoText() 追加在后面。
    private string _affixRuleBaseText = "选择装备、品级和等级后读取游戏词条池。";
    private ICollectionView? _equipmentInventoryView;
    private ICollectionView? _inventoryTemplateView;
    private ICollectionView? _inventoryView;
    private readonly Dictionary<int, List<SkillOption>> _talentSkillOptionCatalogs = new();
    private HeroEdit? _talentSkillCatalogHero;
    private bool _refreshingTalentSkillOptions;
    private bool _loadingControls;
    private int _rulesRequestVersion;
    private int _affixReferenceVersion;

    private EquipmentModeState ActiveState => _equipmentEditMode ? _editorState : _generatorState;

    // 规则与「当前编辑的装备」都按模式隔离。
    private EquipmentRules? _equipmentRules
    {
        get => ActiveState.Rules;
        set => ActiveState.Rules = value;
    }

    private EquipmentInventoryEntry? _currentEditEntry
    {
        get => ActiveState.EditEntry;
        set => ActiveState.EditEntry = value;
    }

    public MainWindow()
    {
        InitializeComponent();
        _affixView = CollectionViewSource.GetDefaultView(_affixes);
        // 污染词条按 GroupName 单独成组（GroupName 对污染行返回「污染」，其余返回档位名）。
        // 注意：AffixesGrid 的分组配色用 DataTrigger 匹配字面量「污染」，改名要同步改 XAML。
        _affixView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AffixEdit.GroupName)));
        AffixesGrid.ItemsSource = _affixView;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            // 快照包含游戏当前版本的装备表、等级表、品级规则和角色技能树规则。
            var response = await BridgeClient.SendAsync(new EditorRequest { Action = "snapshot" });
            EnsureSuccess(response);
            _snapshot = response.Snapshot ?? throw new InvalidDataException("桥接没有返回游戏数据。");

            _loadingControls = true;
            _equipmentView = CollectionViewSource.GetDefaultView(_snapshot.EquipmentTemplates);
            // 只在生成器模式绑定模板列表；编辑器模式由 RefreshEquipmentListButton 自己维护背包视图。
            if (!_equipmentEditMode)
            {
                EquipmentTemplateCombo.ItemsSource = _equipmentView;
                EquipmentTemplateCombo.SelectedIndex = _snapshot.EquipmentTemplates.Count > 0 ? 0 : -1;
            }
            EquipmentGradeCombo.ItemsSource = _snapshot.EquipmentGrades;
            BlessingLevelCombo.ItemsSource = _snapshot.BlessingLevels;
            HeroQualityCombo.ItemsSource = _snapshot.HeroQualities;
            HeroCombo.ItemsSource = _snapshot.Heroes;
            BindInventory(_snapshot.Inventory);
            BindLord(_snapshot.Lord);
            HeroCombo.SelectedIndex = _snapshot.Heroes.Count > 0 ? 0 : -1;
            RefreshQualityOptions();
            // 需求 3：装备等级与锻造等级改为输入框 + 滑块，范围来自游戏表。
            ApplyLevelRange(ActiveState.Level);
            SelectGrade(ActiveState.MainAttrGrade);
            _loadingControls = false;
            await ReloadEquipmentInventoryAsync(applySelection: true);
            await LoadEquipmentRulesAsync();
            return $"已读取当前游戏：{_snapshot.EquipmentTemplates.Count} 个装备模板，{_snapshot.Inventory.BagItems.Count} 组背包物品，{_snapshot.Heroes.Count} 名角色。";
        });
    }

    private void BindInventory(InventorySnapshot inventory)
    {
        // 桥接返回的是全新快照；用稳定字段恢复选项，避免添加后跳回当前类型的第一项。
        var selectedTemplate = InventoryTemplateCombo.SelectedItem as InventoryTemplate;
        var selectedBagItem = InventoryGrid.SelectedItem as InventoryItemEdit;
        if (_snapshot != null)
            _snapshot.Inventory = inventory;
        // 记录每行的原始数量：表格内直接编辑后，用它找出真正改动过的行批量提交。
        foreach (var bagItem in inventory.BagItems)
            bagItem.OriginalCount = bagItem.Count;
        _inventoryTemplateView = CollectionViewSource.GetDefaultView(inventory.AvailableItems);
        _inventoryView = CollectionViewSource.GetDefaultView(inventory.BagItems);
        InventoryTemplateCombo.ItemsSource = _inventoryTemplateView;
        InventoryGrid.ItemsSource = _inventoryView;
        RefreshInventoryFilters();

        if (selectedTemplate != null)
        {
            var restoredTemplate = inventory.AvailableItems.FirstOrDefault(item =>
                item.Type == selectedTemplate.Type && item.Id == selectedTemplate.Id &&
                item.Quality == selectedTemplate.Quality && item.Level == selectedTemplate.Level);
            if (restoredTemplate != null && _inventoryTemplateView.Contains(restoredTemplate))
                InventoryTemplateCombo.SelectedItem = restoredTemplate;
        }
        EnsureInventoryTemplateSelection();

        if (selectedBagItem != null)
        {
            var restoredBagItem = inventory.BagItems.FirstOrDefault(item =>
                item.Container == selectedBagItem.Container && item.Type == selectedBagItem.Type && item.FieldIndex == selectedBagItem.FieldIndex &&
                item.Id == selectedBagItem.Id && item.Quality == selectedBagItem.Quality && item.Level == selectedBagItem.Level);
            if (restoredBagItem != null && _inventoryView.Contains(restoredBagItem))
                InventoryGrid.SelectedItem = restoredBagItem;
        }
    }

    private void InventoryTemplateSearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inventoryTemplateView == null)
            return;
        RefreshInventoryFilters();
        EnsureInventoryTemplateSelection();
    }

    private void BindLord(LordEdit lord)
    {
        if (_snapshot != null)
            _snapshot.Lord = lord;
        LordLevelText.Text = lord.Level.ToString(CultureInfo.InvariantCulture);
        LordLevelRuleText.Text = $"合法等级读取自当前游戏 TLordLevel 表：1-{lord.MaximumLevel}。";
        LordJobsGrid.ItemsSource = lord.Jobs;
        LordJobsGrid.SelectedIndex = lord.Jobs.Count > 0 ? 0 : -1;
    }

    private void LordJobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LordJobsGrid.SelectedItem is not LordJobEdit job)
        {
            LordTalentsGrid.ItemsSource = null;
            LordTalentRuleText.Text = "请选择一个职业魔偶。";
            return;
        }
        SyncLordJobRule(job);
        LordTalentsGrid.ItemsSource = job.TalentBonuses;
        LordTalentRuleText.Text = $"{job.JobName}：魔偶 {job.Level} 级要求崇拜者至少 {job.RequiredLordLevel} 级；" +
            $"力量总加成 {job.StrengthMinimum}-{job.StrengthMaximum}，敏捷总加成 {job.DexterityMinimum}-{job.DexterityMaximum}，" +
            $"智力总加成 {job.IntelligenceMinimum}-{job.IntelligenceMaximum}，三项累计总和必须为 {job.TotalAttributePoints}。";
    }

    private void LordJobsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
            return;
        if (e.Row.Item is not LordJobEdit job)
            return;

        // CellEditEnding 时绑定尚未写回数据源；用 Dispatcher 排到下一帧后再重算派生列。
        Dispatcher.BeginInvoke(() => SyncLordJobRule(job), DispatcherPriority.Background);
    }

    private void SyncLordJobRule(LordJobEdit job)
    {
        var rule = _snapshot?.Lord.JobLevelRules.FirstOrDefault(item => item.Level == job.Level);
        if (rule == null)
            return;
        job.RequiredLordLevel = rule.RequiredLordLevel;
        job.TotalAttributePoints = rule.TotalAttributePoints;
        var attributeRule = job.AttributeRules.FirstOrDefault(item => item.Level == job.Level);
        if (attributeRule != null)
        {
            // 每个职业以自身当前存档总加成为锚点累计升级点数，不能使用单次升级奖励。
            job.TotalAttributePoints = attributeRule.TotalAttributePoints;
            job.StrengthMinimum = attributeRule.StrengthMinimum;
            job.StrengthMaximum = attributeRule.StrengthMaximum;
            job.DexterityMinimum = attributeRule.DexterityMinimum;
            job.DexterityMaximum = attributeRule.DexterityMaximum;
            job.IntelligenceMinimum = attributeRule.IntelligenceMinimum;
            job.IntelligenceMaximum = attributeRule.IntelligenceMaximum;
        }
        foreach (var talent in job.TalentBonuses)
        {
            talent.MaximumLevel = rule.MaximumTalentBonusLevel;
            if (talent.Level > talent.MaximumLevel)
                talent.Level = talent.MaximumLevel;
        }
    }

    private async void ApplyLordButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            CommitGrid(LordJobsGrid);
            CommitGrid(LordTalentsGrid);
            var lord = _snapshot?.Lord ?? throw new InvalidOperationException("请先连接并读取游戏数据。");
            var level = ParseInt(LordLevelText.Text, "崇拜者等级");
            if (level < 1 || level > lord.MaximumLevel)
                throw new InvalidOperationException($"当前游戏表允许的崇拜者等级为 1-{lord.MaximumLevel}。");
            lord.Level = level;
            // 桌面端不强制六个职业一起合法；桥接只对实际修改的魔偶按最新游戏规则校验。
            foreach (var job in lord.Jobs) SyncLordJobRule(job);
            var response = await BridgeClient.SendAsync(new EditorRequest { Action = "updateLord", Lord = lord });
            EnsureSuccess(response);
            BindLord(response.Lord ?? throw new InvalidDataException("桥接没有返回更新后的崇拜者数据。"));
            return response.Message;
        });
    }

    private void InventorySearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inventoryView == null)
            return;
        RefreshInventoryFilters();
    }

    private void InventoryTypeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 子控件的 SelectionChanged 会冒泡到 TabControl，只处理类型 Tab 自己的切换事件。
        if (e.Source != InventoryTypeTabs || _inventoryTemplateView == null || _inventoryView == null)
            return;
        RefreshInventoryFilters();
        EnsureInventoryTemplateSelection();
        if (InventoryGrid.SelectedItem != null && !_inventoryView.Contains(InventoryGrid.SelectedItem))
            InventoryGrid.SelectedItem = null;
    }

    private void RefreshInventoryFilters()
    {
        if (_inventoryTemplateView == null || _inventoryView == null)
            return;
        var type = GetSelectedInventoryType();
        var templateKeyword = InventoryTemplateSearchText.Text.Trim();
        var bagKeyword = InventorySearchText.Text.Trim();
        _inventoryTemplateView.Filter = item => item is InventoryTemplate template && template.Type == type &&
            (string.IsNullOrWhiteSpace(templateKeyword) ||
             template.Name.Contains(templateKeyword, StringComparison.CurrentCultureIgnoreCase) ||
             template.Id.ToString(CultureInfo.InvariantCulture).Contains(templateKeyword, StringComparison.OrdinalIgnoreCase) ||
             template.LevelDescription.Contains(templateKeyword, StringComparison.CurrentCultureIgnoreCase));
        _inventoryView.Filter = item => item is InventoryItemEdit inventoryItem && inventoryItem.Type == type &&
            (string.IsNullOrWhiteSpace(bagKeyword) ||
             inventoryItem.Name.Contains(bagKeyword, StringComparison.CurrentCultureIgnoreCase) ||
             inventoryItem.Id.ToString(CultureInfo.InvariantCulture).Contains(bagKeyword, StringComparison.OrdinalIgnoreCase));
        _inventoryTemplateView.Refresh();
        _inventoryView.Refresh();
    }

    private void EnsureInventoryTemplateSelection()
    {
        if (_inventoryTemplateView == null)
            return;
        if (InventoryTemplateCombo.SelectedItem != null &&
            !_inventoryTemplateView.Contains(InventoryTemplateCombo.SelectedItem))
            InventoryTemplateCombo.SelectedItem = null;
        if (InventoryTemplateCombo.SelectedItem == null && !_inventoryTemplateView.IsEmpty)
            InventoryTemplateCombo.SelectedIndex = 0;
    }

    private int GetSelectedInventoryType()
    {
        if (InventoryTypeTabs.SelectedItem is TabItem tab &&
            int.TryParse(tab.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            return type;
        return 1;
    }

    private async void AddInventoryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var template = InventoryTemplateCombo.SelectedItem as InventoryTemplate
                ?? throw new InvalidOperationException("请选择要增加的物品。");
            var count = ParseInt(AddInventoryCountText.Text, "增加数量");
            if (count <= 0)
                throw new InvalidOperationException("增加数量必须大于 0。");
            var response = await BridgeClient.SendAsync(new EditorRequest
            {
                Action = "addInventoryItem",
                InventoryAdd = new InventoryAddEdit
                {
                    Type = template.Type, Id = template.Id, Quality = template.Quality,
                    Level = template.Level, Count = count
                }
            });
            EnsureSuccess(response);
            BindInventory(response.Inventory ?? throw new InvalidDataException("桥接没有返回最新背包数据。"));
            return response.Message;
        });
    }

    private async void ApplyInventoryCountButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            // 表格内直接编辑「数量（可编辑）」列即可，一次提交所有改动过的行。
            CommitGrid(InventoryGrid);
            if (HasValidationError(InventoryGrid))
                throw new InvalidOperationException("物品数量只能填写大于等于 0 的整数。");
            var items = _snapshot?.Inventory.BagItems;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("还没有读取到背包数据，请先点「连接 / 刷新」。");

            var changed = items.Where(item => item.Count != item.OriginalCount).ToList();
            if (changed.Count == 0)
                throw new InvalidOperationException("表格里没有需要应用的改动：请直接在「数量（可编辑）」列里改数字。");
            var negative = changed.FirstOrDefault(item => item.Count < 0);
            if (negative != null)
                throw new InvalidOperationException($"“{negative.Name}”的数量不能小于 0；填 0 表示删除。");

            var response = await BridgeClient.SendAsync(new EditorRequest
            {
                Action = "updateInventoryCounts",
                InventoryItems = changed
            });
            EnsureSuccess(response);
            BindInventory(response.Inventory ?? throw new InvalidDataException("桥接没有返回最新背包数据。"));
            return response.Message;
        });
    }

    private void EquipmentModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != EquipmentModeTabs) return;
        var editMode = EquipmentModeTabs.SelectedIndex == 1;
        if (_equipmentEditMode == editMode) return;

        // 需求 4：切走之前把当前模式的选择与词条列表存回它自己的状态。
        CaptureActiveSelections();
        ActiveState.Affixes = _affixes.ToList();

        _equipmentEditMode = editMode;

        _loadingControls = true;
        try
        {
            if (editMode)
            {
                EquipmentTemplateLabel.Content = "背包中的装备";
                EquipmentActionButton.Content = "应用修改（移除原装备并重新生成）";
                RefreshEquipmentListButton.Visibility = Visibility.Visible;
                EquipmentTemplateCombo.ItemsSource = _equipmentInventoryView;
            }
            else
            {
                EquipmentTemplateLabel.Content = "装备模板";
                EquipmentActionButton.Content = "生成装备并加入背包";
                RefreshEquipmentListButton.Visibility = Visibility.Collapsed;
                EquipmentTemplateCombo.ItemsSource = _equipmentView;
            }
        }
        finally { _loadingControls = false; }
        UpdateSlotInfoText();

        // 恢复该模式自己的词条列表与下拉选择，再按需刷新规则。
        LoadActiveAffixes();
        if (RestoreActiveSelections() && editMode &&
            EquipmentTemplateCombo.SelectedItem is EquipmentInventoryEntry entry)
            _ = ApplyEditorEntrySelectionAsync(entry);
        else
            _ = LoadEquipmentRulesAsync();
    }

    // 把界面上当前的选择存进当前模式的状态（需求 4）。
    private void CaptureActiveSelections()
    {
        var state = ActiveState;
        state.TemplateId = GetSelectedTemplateId();
        if (QualityCombo.SelectedItem is RuleOption quality) state.Quality = quality.Value;
        state.Level = ReadLevel();
        state.ForgeLevel = ReadForgeLevel();
        if (EquipmentGradeCombo.SelectedItem is RuleOption grade) state.MainAttrGrade = grade.Value;
    }

    // 用当前模式的状态恢复界面选择；返回是否恢复出了一个可用的装备选择。
    private bool RestoreActiveSelections()
    {
        var state = ActiveState;
        _loadingControls = true;
        try
        {
            EquipmentTemplateCombo.SelectedItem = null;
            if (state.TemplateId.HasValue &&
                EquipmentTemplateCombo.ItemsSource is ICollectionView view)
                foreach (var item in view.SourceCollection)
                    if (GetItemTemplateId(item) == state.TemplateId.Value)
                    {
                        EquipmentTemplateCombo.SelectedItem = item;
                        break;
                    }
            if (EquipmentTemplateCombo.SelectedItem == null &&
                EquipmentTemplateCombo.ItemsSource is ICollectionView all && !all.IsEmpty)
                EquipmentTemplateCombo.SelectedIndex = 0;

            RefreshQualityOptions(preferredQuality: state.Quality > 0 ? state.Quality : null);
            ApplyLevelRange(state.Level);
            ApplyForgeLevelRange(state.ForgeLevel);
            SelectGrade(state.MainAttrGrade);
        }
        finally { _loadingControls = false; }
        return EquipmentTemplateCombo.SelectedItem != null;
    }

    private void LoadActiveAffixes()
    {
        _affixes.Clear();
        foreach (var affix in ActiveState.Affixes)
            _affixes.Add(affix);
        ResizeAffixColumnsToContent();
    }

    private int? GetSelectedTemplateId() => GetItemTemplateId(EquipmentTemplateCombo.SelectedItem);

    private static int GetItemTemplateId(object? item) => item switch
    {
        EquipmentTemplate template => template.Id,
        EquipmentInventoryEntry entry => entry.TemplateId,
        _ => 0
    };

    private async Task ApplyEquipmentInventoryAsync(EquipmentInventorySnapshot inventory, bool applySelection)
    {
        // 无论哪种模式，先把最新数据缓存下来；切到编辑器 tab 时可直接使用。
        _equipmentInventoryView = CollectionViewSource.GetDefaultView(inventory.Entries);

        // 需求 4（同时修掉「生成器模式刷新后无法生成」）：
        // 生成器模式的模板下拉必须保持模板列表，绝不能被背包装备列表覆盖。
        if (!_equipmentEditMode)
            return;

        // 静默刷新：只更新缓存，不动当前 UI。生成完装备后走这里。
        if (!applySelection)
            return;

        // 恢复选中：GUID 每次刷新都会变，用内容字段匹配上一次的选中项。
        var previousEntry = _currentEditEntry;
        EquipmentInventoryEntry? restored = null;
        if (previousEntry != null)
        {
            restored = inventory.Entries.FirstOrDefault(item =>
                item.TemplateId == previousEntry.TemplateId &&
                item.Quality == previousEntry.Quality &&
                item.Level == previousEntry.Level &&
                item.ForgeLevel == previousEntry.ForgeLevel &&
                item.Affixes.Count == previousEntry.Affixes.Count);
        }

        _loadingControls = true;
        try
        {
            EquipmentTemplateCombo.ItemsSource = _equipmentInventoryView;
            if (restored != null)
                EquipmentTemplateCombo.SelectedItem = restored;
            else
                EquipmentTemplateCombo.SelectedIndex = inventory.Entries.Count > 0 ? 0 : -1;
        }
        finally { _loadingControls = false; }

        // 刷新期间 SelectionChanged 被 _loadingControls 吞掉，这里显式驱动一次。
        if (EquipmentTemplateCombo.SelectedItem is EquipmentInventoryEntry entry)
            await ApplyEditorEntrySelectionAsync(entry);
        else
            await LoadEquipmentRulesAsync();
    }

    private async Task ReloadEquipmentInventoryAsync(bool applySelection)
    {
        // 拉取 + 绑定。
        var response = await BridgeClient.SendAsync(new EditorRequest { Action = "equipmentInventory" });
        EnsureSuccess(response);
        await ApplyEquipmentInventoryAsync(
            response.EquipmentInventory ?? throw new InvalidDataException("桥接没有返回装备背包。"),
            applySelection);
    }

    private async void RefreshEquipmentListButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await ReloadEquipmentInventoryAsync(applySelection: true);
            var count = _equipmentInventoryView?.Cast<object>().Count() ?? 0;
            return count == 0 ? "背包里没有可编辑的装备。" : $"已读取 {count} 件背包装备。";
        });
    }

    private async Task ApplyEditorEntrySelectionAsync(EquipmentInventoryEntry entry)
    {
        // 编辑器模式下，把一个装备条目的全部信息填到控件里，并刷新规则。
        // RefreshEquipmentListButton_Click 也会显式调用，因为刷新期间的 SelectionChanged 被 _loadingControls 吞掉了。
        _loadingControls = true;
        try
        {
            RefreshQualityOptions(entry.TemplateId, entry.Quality);
            // 需求 3：等级/锻造是输入框 + 滑块。锻造范围要等规则回来，所以先记住目标值。
            ActiveState.Level = entry.Level;
            ActiveState.ForgeLevel = entry.ForgeLevel;
            ApplyLevelRange(entry.Level);
            SelectGrade(entry.MainAttrGrade);
            _affixes.Clear();
            foreach (var affix in entry.Affixes)
                _affixes.Add(CloneAffix(affix));
            _currentEditEntry = entry;
        }
        finally { _loadingControls = false; }

        await LoadEquipmentRulesAsync();
    }

    private void EquipmentSearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 需求 5.2：空格分隔多个关键字，每个关键字都要命中（slot / 等级 / 品质 / 名称）。
        var keywords = EquipmentSearchText.Text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 使用 WPF 集合视图过滤，不复制装备集合，搜索时可以保留原始规则数据。
        var view = _equipmentEditMode ? _equipmentInventoryView : _equipmentView;
        if (view == null) return;

        view.Filter = item => keywords.Length == 0 || keywords.All(keyword => MatchesEquipment(item, keyword));
        view.Refresh();
        if (EquipmentTemplateCombo.SelectedItem == null && !view.IsEmpty)
            EquipmentTemplateCombo.SelectedIndex = 0;
    }

    private static bool TextHas(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

    // 编辑器模式：slot / 等级 / 品质 / 名称（外加显示串兜底）；生成器模式：名称 / ID / 部位。
    private static bool MatchesEquipment(object item, string keyword)
    {
        static bool HasNumber(int value, string keyword) =>
            value.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase);

        return item switch
        {
            EquipmentInventoryEntry entry =>
                HasNumber(entry.FieldIndex, keyword) ||
                HasNumber(entry.Level, keyword) ||
                TextHas(entry.QualityName, keyword) ||
                TextHas(entry.Name, keyword) ||
                TextHas(entry.Display, keyword),
            EquipmentTemplate template =>
                TextHas(template.Name, keyword) ||
                TextHas(template.PartName, keyword) ||
                HasNumber(template.Id, keyword),
            _ => true
        };
    }

    private async void EquipmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls)
            return;

        if (sender == EquipmentTemplateCombo)
        {
            if (_equipmentEditMode && EquipmentTemplateCombo.SelectedItem is EquipmentInventoryEntry entry)
            {
                await ApplyEditorEntrySelectionAsync(entry);
                return;
            }
            _loadingControls = true;
            try
            {
                RefreshQualityOptions();
                _currentEditEntry = null;
            }
            finally { _loadingControls = false; }
        }
        await LoadEquipmentRulesAsync();
    }

    private void RefreshQualityOptions(int? templateId = null, int? preferredQuality = null)
    {
        if (_snapshot == null) { QualityCombo.ItemsSource = null; return; }

        EquipmentTemplate? template = templateId.HasValue
            ? _snapshot.EquipmentTemplates.FirstOrDefault(t => t.Id == templateId.Value)
            : EquipmentTemplateCombo.SelectedItem as EquipmentTemplate;
        if (template == null) { QualityCombo.ItemsSource = null; return; }

        // 生成器模式：只展示该模板在游戏随机流程里会产出的品级（原来的行为）。
        // 编辑器模式：展示全部品级，因为已存在的装备实例可能持有不在随机产出枚举里的品级（例如“套装”）。
        var options = _equipmentEditMode
            ? _snapshot.EquipmentQualities.ToList()
            : _snapshot.EquipmentQualities.Where(option => template.AllowedQualities.Contains(option.Value)).ToList();
        QualityCombo.ItemsSource = options;
        if (options.Count == 0) return;

        var index = preferredQuality.HasValue
            ? options.FindIndex(option => option.Value == preferredQuality.Value)
            : -1;
        if (index < 0) index = options.FindIndex(option => option.Value == template.BaseQuality);
        QualityCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private async Task LoadEquipmentRulesAsync()
    {
        if (EquipmentTemplateCombo.SelectedItem is not { } selected ||
            QualityCombo.SelectedItem is not RuleOption quality)
            return;
        var level = ReadLevel();
        if (level <= 0)
            return;
        var mainAttrGrade = EquipmentGradeCombo.SelectedItem is RuleOption grade ? grade.Value : 0;

        // 连续切换控件时只接受最后一次响应，避免较慢的旧响应覆盖当前选择。
        // 生成器模式从 EquipmentTemplate 取模板 ID；编辑器模式从 EquipmentInventoryEntry 取。
        var templateId = GetItemTemplateId(selected);
        if (templateId == 0) return;

        // 请求发出时的模式；响应回来时若已切换模式就丢弃（需求 4：两边独立）。
        var requestEditMode = _equipmentEditMode;
        var requestVersion = ++_rulesRequestVersion;
        _affixRuleBaseText = "正在读取当前游戏的词条池和数量限制……";
        AffixRuleText.Text = _affixRuleBaseText;
        try
        {
            var response = await BridgeClient.SendAsync(new EditorRequest
            {
                Action = "equipmentRules",
                Equipment = new EquipmentEdit
                {
                    TemplateId = templateId,
                    Quality = quality.Value,
                    Level = level,
                    MainAttrGrade = mainAttrGrade
                }
            });
            if (requestVersion != _rulesRequestVersion || requestEditMode != _equipmentEditMode)
                return;
            EnsureSuccess(response);
            _equipmentRules = response.EquipmentRules ?? throw new InvalidDataException("桥接没有返回装备规则。");

            // 需求 3：锻造等级的合法范围来自规则，用滑块 + 输入框呈现（范围依赖当前装备等级）。
            ApplyForgeLevelRange(ActiveState.ForgeLevel);
            var affixCategories = _equipmentRules.AffixQualityLimits
                .OrderBy(pair => pair.Key)
                .Select(pair => new AffixCategoryOption
                {
                    Quality = pair.Key,
                    Name = _equipmentRules.AffixQualityNames.TryGetValue(pair.Key, out var name) ? name : $"词条档位 {pair.Key}",
                    Limit = pair.Value
                })
                .ToList();
            // 「污染」独立成组：候选池就是档位3（原生腐蚀从那里挑未用过的 id），
            // 额外面板显示当前存档允许的腐蚀次数；新增的行会带上污染标记。
            if (_equipmentRules.AllowedAffixes.Any(option => option.IsCorruptPool))
            {
                affixCategories.Add(new AffixCategoryOption
                {
                    Quality = 0,
                    Name = string.IsNullOrWhiteSpace(_equipmentRules.CorruptGroupName) ? "污染" : _equipmentRules.CorruptGroupName,
                    Limit = _equipmentRules.MaximumCorruptCount,
                    IsCorrupt = true
                });
            }
            // 「镶嵌」独立成组：每行一个孔位，「数值」列填类型码（0 随机 / 1 攻击 / 2 防御），
            // x = 该品级的开孔上限（TEquipQuality.slotCountArr 最大值）；组为空则交给游戏随机。
            affixCategories.Add(new AffixCategoryOption
            {
                Quality = 0,
                Name = string.IsNullOrWhiteSpace(_equipmentRules.SocketGroupName) ? "镶嵌" : _equipmentRules.SocketGroupName,
                Limit = _equipmentRules.MaximumSlotCount,
                IsSocket = true
            });
            AffixQualityCombo.ItemsSource = affixCategories;
            AffixQualityCombo.SelectedIndex = affixCategories.Count > 0 ? 0 : -1;

            // 关键差异：只有「生成器」模式才用规则里的默认词条填充列表；
            // 编辑器模式保留装备自带的词条，用户直接在上面改。
            //
            // 另外只在「模板或品级变化」（结构变化）时才重建默认词条：
            // 改装备等级或装备质量只影响数值，不应把用户已经编辑好的词条冲掉。
            var state = ActiveState;
            var structuralChange = state.LastTemplateId != templateId || state.LastQuality != quality.Value;
            // 装备等级变化：把受等级影响的词条（精良/稀有）等级顶到上界。
            var levelChanged = state.LastLevel != ReadLevel();
            state.LastTemplateId = templateId;
            state.LastQuality = quality.Value;
            state.LastLevel = ReadLevel();
            if (!_equipmentEditMode && structuralChange)
                MergeGeneratedAffixes();

            // 统一的规则注入：等级上限 + 参考值，并夹紧等级（需求 2.1.3 / 2.2）。
            ApplyRulesToAffixes();
            if (!_equipmentEditMode && levelChanged)
            {
                foreach (var affix in _affixes)
                    affix.LevelToUpperBound();
                ApplyRulesToAffixes();
            }

            ResizeAffixColumnsToContent();
            // 规则里的参考值是按「新增默认等级」算的；这里再按每条词条自己的等级取一次准确值。
            await RefreshAffixReferenceAsync();
            // 档位额度按原生区间显示：固定条数写「N 条」，随机档位写「N~M 条」（校验仍用上限 M）。
            var qualityLimits = string.Join("，", _equipmentRules.AffixQualityLimits
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                {
                    var name = _equipmentRules.AffixQualityNames.TryGetValue(pair.Key, out var qualityName)
                        ? qualityName
                        : $"档位 {pair.Key}";
                    var minimum = _equipmentRules.AffixQualityMinimums.TryGetValue(pair.Key, out var min) ? min : pair.Value;
                    return minimum < pair.Value
                        ? $"{name} {minimum}~{pair.Value} 条"
                        : $"{name} {pair.Value} 条";
                }));
            var maximumForgeLevel = _equipmentRules.AllowedForgeLevels.Count > 0
                ? _equipmentRules.AllowedForgeLevels.Max()
                : 0;
            var fixedAffixCount = _affixes.Count(affix => affix.Quality == 8);
            var fixedAffixNote = fixedAffixCount > 0
                ? $"（另有 {fixedAffixCount} 条品级强制词条不占额度）"
                : "";
            // 圣碑腐蚀可以额外加稀有词条（不进上面的原生上限）：只作说明，需要时用「越界」添加。
            var corruptNote = _equipmentRules.MaximumCorruptCount > 0
                ? $"（另外圣碑腐蚀最多可再加 {_equipmentRules.MaximumCorruptCount} 条稀有词条，不计入上限；需要时勾选「越界」）"
                : "";
            // 旧版（1.1.0 重做前）污染残留：原生 GetCorruptInfoText() 的文案，只能用【涤魂泪滴】清除。
            var legacyCorruptText = _equipmentEditMode ? ActiveState.EditEntry?.LegacyCorruptText ?? "" : "";
            var legacyNote = string.IsNullOrWhiteSpace(legacyCorruptText)
                ? ""
                : $"　旧版污染残留：{legacyCorruptText}（只能用【涤魂泪滴】清除）";
            _affixRuleBaseText = $"游戏规则：锻造等级 {_equipmentRules.AllowedForgeLevels.FirstOrDefault()}-{maximumForgeLevel}；总计最多 {_equipmentRules.MaximumAffixCount} 条{fixedAffixNote}{corruptNote}；{qualityLimits}；词条等级 1-{_equipmentRules.MaximumAffixLevel}；合法候选 {_equipmentRules.AllowedAffixes.Count} 条。参考数值由游戏规则算出，仅供参考、不限制输入；勾选「强制越界」才会把数值写入存档。{legacyNote}";
            UpdateSlotInfoText();
            SetStatus("已根据当前装备、品级和等级刷新游戏规则。", true);
        }
        catch (Exception exception)
        {
            if (requestVersion != _rulesRequestVersion || requestEditMode != _equipmentEditMode)
                return;
            _equipmentRules = null;
            _affixes.Clear();
            AffixQualityCombo.ItemsSource = null;
            AllowedAffixCombo.ItemsSource = null;
            ApplyForgeLevelRange(0);
            _affixRuleBaseText = $"无法读取规则：{exception.Message}";
            AffixRuleText.Text = _affixRuleBaseText;
            SetStatus($"读取装备规则失败：{exception.Message}", false);
        }
    }

    private void AffixQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_equipmentRules == null || AffixQualityCombo.SelectedItem is not AffixCategoryOption category)
        {
            AllowedAffixCombo.ItemsSource = null;
            return;
        }

        // 每次只显示当前词条档位的原生候选池，避免不同档位的数量规则混在一起。
        // 「污染」分组显示的是可作污染词的候选（IsCorruptPool，即档位3池）；「镶嵌」分组不需要候选。
        if (category.IsSocket)
        {
            AllowedAffixCombo.ItemsSource = null;
            return;
        }
        var options = _equipmentRules.AllowedAffixes
            .Where(option => category.IsCorrupt ? option.IsCorruptPool : option.Quality == category.Quality)
            .OrderBy(option => option.Id)
            .ToList();
        AllowedAffixCombo.ItemsSource = options;
        AllowedAffixCombo.SelectedIndex = options.Count > 0 ? 0 : -1;
    }

    private void AddAffixButton_Click(object sender, RoutedEventArgs e)
    {
        if (_equipmentRules == null)
        {
            SetStatus("请先选择合法的装备、品级和等级。", false);
            return;
        }
        // 当前选中的是「档位分组」「污染」还是「镶嵌」分组。
        var category = AffixQualityCombo.SelectedItem as AffixCategoryOption;
        // 「越界」勾选（添加按钮左侧）：忽略词条/孔位的**数量**限制。
        // 与表格内每行的「强制越界」互不影响 —— 后者只管该行的数值 / 孔位类型。
        var overrideLimits = OverrideLimitsCheck.IsChecked == true;
        // 镶嵌组：每一行 = 一个孔位（开孔逻辑并入词条表）。上限 x 由该装备自身的规则给出。
        if (category?.IsSocket == true)
        {
            var socketCount = _affixes.Count(row => row.IsSocket);
            if (!overrideLimits && socketCount >= _equipmentRules.MaximumSlotCount)
            {
                SetStatus($"镶嵌组最多 {_equipmentRules.MaximumSlotCount} 条（该品级的开孔上限），已经满了；" +
                          "勾选左侧「越界」可以无视该上限添加。", false);
                return;
            }
            AddSocketRow(overrideLimits);
            return;
        }
        // 只有会写进存档的行才占额度：档位 8（独有技能）、档位 4（传说专属）、神话里非 specialType==1 的
        // 档位 5 行都由原生在运行时按模板重建（GameAssembly.c:1623227-1623245 / :1623344-1623349），不占额度。
        var quotaAffixCount = _affixes.Count(affix => affix.ConsumesAffixQuota);
        if (!overrideLimits && quotaAffixCount >= _equipmentRules.MaximumAffixCount)
        {
            SetStatus($"当前品级最多允许 {_equipmentRules.MaximumAffixCount} 条词条；勾选左侧「越界」可以无视该上限。", false);
            return;
        }
        if (AllowedAffixCombo.SelectedItem is not AffixOption option)
        {
            SetStatus("当前组合没有可添加的合法词条。", false);
            return;
        }
        // 需求 2.5：重复性只在同一档位内部检查，不同档位允许同一个词条 ID。
        if (_affixes.Any(affix => affix.Id == option.Id && affix.Quality == option.Quality))
        {
            SetStatus("同一档位内不能重复添加同一个词条。", false);
            return;
        }
        // 污染词条额外按原生规则查重：TryAddCorruptRareAffix 用 HashSet 收集「非基础档位」的 id，
        // 只会挑该装备没用过的 id（GameAssembly.c（1.1.4）:1618301），所以跨档位也不允许重复。
        if (category?.IsCorrupt == true && _affixes.Any(affix => affix.Id == option.Id && affix.Quality != 1))
        {
            SetStatus($"词条 {option.Id} 已经在这件装备上（原生腐蚀只会挑没用过的 id）。", false);
            return;
        }
        var currentQualityCount = _affixes.Count(affix => affix.Quality == option.Quality);
        if (!overrideLimits &&
            (!_equipmentRules.AffixQualityLimits.TryGetValue(option.Quality, out var qualityLimit) ||
             currentQualityCount >= qualityLimit))
        {
            SetStatus($"{option.QualityName}最多允许 {qualityLimit} 条词条；勾选左侧「越界」可以无视该上限。", false);
            return;
        }
        // 新增词条的等级：随等级变化的默认取最高等级，不适用等级的固定为 -1。
        var levelApplicable = option.ValueKind == AffixValueKind.GradeBased;
        var grade = EquipmentGradeCombo.SelectedItem is RuleOption selected ? Math.Clamp(selected.Value, 1, 3) : 1;
        _affixes.Add(new AffixEdit
        {
            Id = option.Id,
            Quality = option.Quality,
            QualityName = option.QualityName,
            Name = option.Name,
            ValueKind = option.ValueKind,
            GradeValues = new List<int>(option.GradeValues),
            FixedValue = option.FixedValue,
            RandomMinimum = option.RandomMinimum,
            RandomMaximum = option.RandomMaximum,
            // 等级区间随档位不同：精良/稀有 = 1..affixMaxLevel，独特 = 1，传说/神话 = affixMaxLevel（固定）。
            LevelMinimum = option.LevelMinimum,
            LevelMaximum = option.LevelMaximum,
            // 需求 2.4.1：未勾选强制越界时显示的「应有的值」= 当前装备质量档位对应的值。
            ResolvedValue = option.ValueKind == AffixValueKind.GradeBased
                ? (option.GradeValues.Count >= grade ? option.GradeValues[grade - 1] : option.FixedValue)
                : option.FixedValue,
            MaximumLevel = _equipmentRules.MaximumAffixLevel,
            Level = levelApplicable ? _equipmentRules.MaximumAffixLevel : -1,
            // 从「污染」分组加入的行带污染标记（原生 SaveAffixData.isCorrupt），其余为普通词条。
            IsCorrupt = category?.IsCorrupt == true,
            // 默认不勾选强制越界：数值交给游戏计算（需求 2.4.1）。
            ForceOverflow = false
        });
        _affixes[^1].ApplyResponse();
        ResizeAffixColumnsToContent();
        if (category?.IsCorrupt == true)
        {
            var corruptCount = _affixes.Count(affix => affix.IsCorrupt);
            SetStatus(corruptCount > _equipmentRules.MaximumCorruptCount && _equipmentRules.MaximumCorruptCount >= 0
                    ? $"已添加污染词条 {option.Id}：当前存档只允许腐蚀 {_equipmentRules.MaximumCorruptCount} 次，已超出（游戏内需要推进终焉阶段）。"
                    : $"已添加污染词条 {option.Id}（当前可腐蚀 {_equipmentRules.MaximumCorruptCount} 次）。",
                true);
        }
        else
        {
            SetStatus($"已添加词条 {option.Id}，提交前仍会由游戏规则复核。", true);
        }
        // 按当前装备质量与词条等级向游戏换取准确的参考值。
        _ = RefreshAffixReferenceAsync();
    }

    // 镶嵌组的组名（带上限）：x 由该装备自身的规则校验（TEquipQuality.slotCountArr 最大值）。
    private string SocketGroupLabelForRules()
    {
        var name = string.IsNullOrEmpty(_equipmentRules?.SocketGroupName) ? "镶嵌" : _equipmentRules!.SocketGroupName;
        return $"{name}·最多{_equipmentRules?.MaximumSlotCount ?? 0}条";
    }

    // 新增一个孔位行：Id/Name/Level 留空表示「没有镶嵌符文」（这三列只用于回显已有镶嵌的装备）。
    private void AddSocketRow(bool overrideLimits)
    {
        _affixes.Add(new AffixEdit
        {
            Id = 0,
            Name = "",
            Quality = 0,
            QualityName = SocketGroupLabelForRules(),
            Level = 0,
            ValueKind = AffixValueKind.SlotType,
            // 0 = 随机（复刻原生"特殊孔位"的 50/50）；1 = 攻击孔；2 = 防御孔。
            SocketType = 0,
            MaximumLevel = 0,
            ForceOverflow = false
        });
        ResizeAffixColumnsToContent();
        var count = _affixes.Count(row => row.IsSocket);
        SetStatus(overrideLimits
            ? $"已越界添加一个孔位（当前 {count} 个，该品级原生上限 {_equipmentRules?.MaximumSlotCount ?? 0} 条）；" +
              "「数值」列填 0 随机 / 1 攻击 / 2 防御。"
            : $"已添加一个孔位（当前 {count}/{_equipmentRules?.MaximumSlotCount ?? 0} 个）：" +
              "「数值」列填 0 随机 / 1 攻击 / 2 防御；全部删空则交给游戏随机开孔。", true);
    }

    // 把镶嵌（开孔）信息追加到「词条编辑」的信息栏里（AffixRuleText）：
    // 该装备当前孔数 / 镶嵌组上限 / 原生候选（生成表 + 锻造表）/ 开孔等级门槛 / 当前镶嵌组行数。
    private void UpdateSlotInfoText()
    {
        if (_equipmentRules == null)
        {
            AffixRuleText.Text = _affixRuleBaseText;
            return;
        }
        var candidates = _equipmentRules.SlotCountCandidates.Count > 0
            ? string.Join("/", _equipmentRules.SlotCountCandidates)
            : "-";
        var forge = _equipmentRules.ForgeSlotCountCandidates.Count > 0
            ? string.Join("/", _equipmentRules.ForgeSlotCountCandidates)
            : "-";
        var socketRows = _affixes.Count(row => row.IsSocket);
        var current = _equipmentEditMode && _currentEditEntry != null
            ? $"该装备当前 {_currentEditEntry.SlotCount} 孔"
            : "生成器";
        var level = ReadLevel();
        var gateNote = _equipmentRules.SlotLevelRequirement > 0
            ? (level >= _equipmentRules.SlotLevelRequirement
                ? $"装备等级 {level} ≥ {_equipmentRules.SlotLevelRequirement}，原生可能开孔"
                : $"装备等级 {level} < {_equipmentRules.SlotLevelRequirement}，原生不会开孔（该门槛只影响游戏随机开孔）")
            : "";
        var limitNote = _equipmentRules.MaximumSlotCount > 0
            ? $"镶嵌组上限 {_equipmentRules.MaximumSlotCount} 条（生成表 {candidates}，工坊表 {forge}）"
            : $"该品级无原生开孔表（生成 {candidates} / 工坊 {forge}）→ 原生不会开孔，需要勾选「越界」才能加孔位行";
        AffixRuleText.Text = $"{_affixRuleBaseText}　【镶嵌】{current}；{limitNote}；{gateNote}；" +
                             $"当前镶嵌组 {socketRows} 行" + (socketRows == 0 ? "（留空 = 交给游戏随机开孔）" : "") + "。";
    }

    // 让「表格卡片」在空间足够时占满剩余高度、空间不足时交给外层滚动条：
    // ScrollViewer 会给子元素无限高度，所以给内容 Grid 设 MinHeight = 视口高度。
    //   · 内容自然高度 < 视口 → Grid 被撑到视口高度，* 行吃掉剩余空间 → 表格变大；
    //   · 内容自然高度 > 视口 → Grid 更高，垂直滚动条出现 → 小窗口也能滚到按钮。
    private void ContentScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.Content is not FrameworkElement content)
            return;
        var viewport = scroll.ViewportHeight;
        if (viewport <= 0 || Math.Abs(content.MinHeight - viewport) < 0.5)
            return;
        content.MinHeight = viewport;
    }

    private void ResizeAffixColumnsToContent()
    {
        // 等待绑定文本生成单元格后再重置为 Auto，强制 WPF 使用最新内容重新测量列宽。
        Dispatcher.BeginInvoke(() =>
        {
            AffixesGrid.UpdateLayout();
            foreach (var column in AffixesGrid.Columns)
            {
                column.Width = new DataGridLength(column.ActualWidth, DataGridLengthUnitType.Pixel);
                column.Width = DataGridLength.Auto;
            }
            AffixesGrid.UpdateLayout();
        }, DispatcherPriority.Loaded);
    }

    private void ResizeTalentColumnsToContent()
    {
        // 分组和虚拟化会让模板列首次只按空单元格测量；内容生成后重新计算实际宽度。
        Dispatcher.BeginInvoke(() =>
        {
            TalentsGrid.UpdateLayout();
            foreach (var column in TalentsGrid.Columns)
            {
                column.Width = new DataGridLength(column.ActualWidth, DataGridLengthUnitType.Pixel);
                column.Width = column is DataGridTemplateColumn &&
                    column.Header?.ToString()?.StartsWith("技能 / 天赋", StringComparison.Ordinal) == true
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : DataGridLength.Auto;
            }
            TalentsGrid.UpdateLayout();
        }, DispatcherPriority.Loaded);
    }

    // 表格首列的「删除」按钮：删除该行（词条或孔位）。
    private void RemoveAffixRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AffixEdit affix)
            return;
        RemoveAffix(affix);
    }

    private void RemoveAffix(AffixEdit affix)
    {
        // 传说(4)/神话(5)/独特(8) 词条只作展示：不允许删除。
        if (affix.IsTierLocked)
        {
            SetStatus($"词条 {affix.Id}（{affix.QualityName}）属于传说/神话/独有档位，仅作展示、不能删除。", false);
            return;
        }
        // 装备自带的词条（档位 8 独有技能、传说/神话专属、基础词条）由装备本身决定，不允许删除。
        if (affix.EquipmentFixed)
        {
            SetStatus($"词条 {affix.Id}（{affix.QualityName}）是装备自带词条，不能删除；数值仍可修改。", false);
            return;
        }
        _affixes.Remove(affix);
        SetStatus(affix.IsSocket
            ? $"已删除一个孔位（当前 {_affixes.Count(row => row.IsSocket)} 个；删空则交给游戏随机开孔）。"
            : $"已从待生成装备中删除词条 {affix.Id}。", true);
    }

    private async void EquipmentActionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            CommitGrid(AffixesGrid);
            if (HasValidationError(AffixesGrid))
                throw new InvalidOperationException("词条等级和数值只能填写整数；词条数值可以留空并由游戏随机生成。");
            if (QualityCombo.SelectedItem is not RuleOption quality)
                throw new InvalidOperationException("请选择合法品级。");
            var level = ReadLevel();
            if (level <= 0)
                throw new InvalidOperationException("请填写合法装备等级。");
            var forgeLevel = ReadForgeLevel();
            if (_equipmentRules == null)
                throw new InvalidOperationException("尚未读取当前组合的游戏规则。");
            if (EquipmentGradeCombo.SelectedItem is not RuleOption grade)
                throw new InvalidOperationException("请选择装备质量。");
            if (quality.Value <= 0)
                throw new InvalidOperationException("请选择合法品级。");

            // 需求 2.1.3：提交前再夹一次等级，避免界面状态过期。
            foreach (var affix in _affixes)
                affix.ClampLevel();
            var affixes = _affixes.Select(CloneAffix).ToList();

            if (_equipmentEditMode)
            {
                if (_currentEditEntry == null)
                    throw new InvalidOperationException("请先在背包中选择要编辑的装备。");
                var entry = _currentEditEntry;
                var response = await BridgeClient.SendAsync(new EditorRequest
                {
                    Action = "replaceEquipment",
                    EquipmentReplace = new EquipmentReplaceEdit
                    {
                        // GUID 由 equipmentInventory 分配；只要不刷新列表就一直有效。
                        Guid = entry.Guid,
                        Equipment = new EquipmentEdit
                        {
                            TemplateId = entry.TemplateId,
                            Quality = quality.Value,
                            Level = level,
                            ForgeLevel = forgeLevel,
                            MainAttrGrade = grade.Value,
                            // 越界添加：勾选后桥接不再校验词条/孔位数量上限。
                            OverrideLimits = OverrideLimitsCheck.IsChecked == true,
                            // 开孔与孔位类型都在 Affixes 的「镶嵌」组里（ValueKind == SlotType）。
                            Affixes = affixes
                        }
                    }
                });
                EnsureSuccess(response);
                if (response.EquipmentInventory != null)
                    await ApplyEquipmentInventoryAsync(response.EquipmentInventory, applySelection: true);
                if (response.Inventory != null)
                    BindInventory(response.Inventory);
                return response.Message;
            }
            else
            {
                var template = EquipmentTemplateCombo.SelectedItem as EquipmentTemplate
                    ?? throw new InvalidOperationException("请选择装备模板。");
                var response = await BridgeClient.SendAsync(new EditorRequest
                {
                    Action = "generateEquipment",
                    Equipment = new EquipmentEdit
                    {
                        TemplateId = template.Id,
                        Quality = quality.Value,
                        Level = level,
                        ForgeLevel = forgeLevel,
                        MainAttrGrade = grade.Value,
                        // 越界添加：勾选后桥接不再校验词条/孔位数量上限。
                        OverrideLimits = OverrideLimitsCheck.IsChecked == true,
                        // 开孔与孔位类型都在 Affixes 的「镶嵌」组里（ValueKind == SlotType）。
                        Affixes = affixes
                    }
                });
                EnsureSuccess(response);
                // 生成后静默刷新装备列表缓存，用户切到编辑器 tab 时立即看到最新装备。
                await ReloadEquipmentInventoryAsync(applySelection: false);
                return response.Message;
            }
        });
    }

    private void HeroCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HeroCombo.SelectedItem is not HeroEdit hero)
            return;
        HeroNameText.Text = hero.Name;
        HeroLevelText.Text = hero.Level.ToString(CultureInfo.InvariantCulture);
        StrengthText.Text = hero.Strength.ToString(CultureInfo.InvariantCulture);
        DexterityText.Text = hero.Dexterity.ToString(CultureInfo.InvariantCulture);
        IntelligenceText.Text = hero.Intelligence.ToString(CultureInfo.InvariantCulture);
        RemainingPointsText.Text = hero.RemainingSkillPoints.ToString(CultureInfo.InvariantCulture);
        BlessingLevelCombo.SelectedItem = hero.BlessingLevel;
        HeroQualityCombo.SelectedItem = _snapshot?.HeroQualities.FirstOrDefault(item => item.Value == hero.Quality);
        GrowthGrid.ItemsSource = hero.GrowthAttributes;
        PrepareTalentSkillOptions(hero);
        var talentView = CollectionViewSource.GetDefaultView(hero.TalentSlots);
        talentView.GroupDescriptions.Clear();
        talentView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TalentSlotEdit.Category)));
        TalentsGrid.ItemsSource = talentView;
        ResizeTalentColumnsToContent();
        var basePointTotal = hero.GrowthAttributes.Where(x => x.Group == "base").Sum(x => x.Points);
        var mainPointTotal = hero.GrowthAttributes.Where(x => x.Group == "main").Sum(x => x.Points);
        var baseGrowTotal = hero.GrowthAttributes.Where(x => x.Group == "base").Sum(x => x.Value);
        var mainGrowTotal = hero.GrowthAttributes.Where(x => x.Group == "main").Sum(x => x.Value);
        GrowthRuleText.Text =
            $"可直接编辑且不消耗血肉结晶；范围取自当前职业、品级的游戏规则。" +
            $"点数：生命+魔力 {basePointTotal:0.###} / 力量+敏捷+智力 {mainPointTotal:0.###}；" +
            $"成长：生命+魔力 {baseGrowTotal:0.###} / 力量+敏捷+智力 {mainGrowTotal:0.###}。";
        ExtraTalentRuleText.Text = $"异化技能 {hero.AlienSkillCount}/{hero.MaximumAlienSkills}，启迪天赋 {hero.InspiredTalentCount}/{hero.MaximumInspiredTalents}；现有项目可直接修改技能或天赋及其等级。";
        SetStatus($"已选择“{hero.Name}”；角色合法等级为 1-{hero.MaximumLevel}。", true);
    }

    private async void ChangeHeroQualityButton_Click(object sender, RoutedEventArgs e)
    {
        var hero = HeroCombo.SelectedItem as HeroEdit;
        var quality = HeroQualityCombo.SelectedItem as RuleOption;
        if (hero == null || quality == null)
        {
            SetStatus("操作失败：请选择角色和目标品级。", false);
            return;
        }
        hero.Quality = quality.Value;
        await RunHeroActionAsync("changeHeroQuality");
    }

    private async Task RunHeroActionAsync(string action)
    {
        await RunAsync(async () =>
        {
            var hero = HeroCombo.SelectedItem as HeroEdit
                ?? throw new InvalidOperationException("请选择角色。");
            var response = await BridgeClient.SendAsync(new EditorRequest { Action = action, Hero = hero });
            EnsureSuccess(response);
            var refreshed = response.Snapshot ?? throw new InvalidDataException("桥接没有返回更新后的角色数据。");
            _snapshot = refreshed;
            BlessingLevelCombo.ItemsSource = refreshed.BlessingLevels;
            HeroQualityCombo.ItemsSource = refreshed.HeroQualities;
            HeroCombo.ItemsSource = refreshed.Heroes;
            HeroCombo.SelectedItem = refreshed.Heroes.FirstOrDefault(item => item.UniqueId == hero.UniqueId);
            BindInventory(refreshed.Inventory);
            return response.Message;
        });
    }

    private void SkillOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingTalentSkillOptions)
            return;
        if (sender is not ComboBox combo || combo.DataContext is not TalentSlotEdit slot || combo.SelectedItem is not SkillOption option)
            return;

        // ComboBox 首次创建或被虚拟化回收时也会触发 SelectionChanged；这些不是用户操作。
        if (e.RemovedItems.Count == 0 || (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen))
            return;

        slot.TalentId = option.TalentId;
        slot.SkillId = option.SkillId;
        slot.Name = option.Name;
        // 不同技能的上限可能不同，切换技能时同步收紧当前编辑值。
        slot.MaximumLevel = option.MaximumLevel;
        if (slot.Level > slot.MaximumLevel)
            slot.Level = slot.MaximumLevel;
        if (HeroCombo.SelectedItem is HeroEdit hero && slot.Category == "天赋技能")
            RefreshTalentSkillOptions(hero);
        SetStatus($"位置 {slot.SlotId} 已选择“{option.Name}”，合法等级上限 {option.MaximumLevel}。", true);
    }

    private void PrepareTalentSkillOptions(HeroEdit hero)
    {
        // 新快照或切换角色时保存每个天赋技能槽的完整候选池；后续联动过滤不能覆盖原始列表。
        if (!ReferenceEquals(_talentSkillCatalogHero, hero))
        {
            _talentSkillCatalogHero = hero;
            _talentSkillOptionCatalogs.Clear();
            foreach (var slot in hero.TalentSlots)
                if (slot.Category == "天赋技能")
                    _talentSkillOptionCatalogs[slot.SlotId] = slot.SkillOptions.ToList();
        }
        RefreshTalentSkillOptions(hero);
    }

    private void RefreshTalentSkillOptions(HeroEdit hero)
    {
        var selectedSkillIds = hero.TalentSlots
            .Where(item => item.Category == "天赋技能")
            .Select(item => item.SkillId)
            .ToHashSet();
        _refreshingTalentSkillOptions = true;
        try
        {
            foreach (var slot in hero.TalentSlots)
            {
                if (slot.Category != "天赋技能" ||
                    !_talentSkillOptionCatalogs.TryGetValue(slot.SlotId, out var catalog))
                    continue;
                // 当前槽位保留自己的已选项；其他槽位选中的技能不会重复出现在此下拉框。
                slot.SkillOptions = catalog
                    .Where(option => option.TalentId == slot.TalentId || !selectedSkillIds.Contains(option.SkillId))
                    .ToList();
            }
        }
        finally
        {
            _refreshingTalentSkillOptions = false;
        }
    }

    private async void ApplyHeroButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            CommitGrid(GrowthGrid);
            CommitGrid(TalentsGrid);
            var hero = HeroCombo.SelectedItem as HeroEdit
                ?? throw new InvalidOperationException("请选择角色。");
            var level = ParseInt(HeroLevelText.Text, "角色等级");
            if (level < 1 || level > hero.MaximumLevel)
                throw new InvalidOperationException($"当前游戏规则允许的角色等级为 1-{hero.MaximumLevel}。");
            foreach (var slot in hero.TalentSlots)
            {
                if (slot.Level < slot.MinimumLevel || slot.Level > slot.MaximumLevel)
                    throw new InvalidOperationException($"位置 {slot.SlotId}“{slot.Name}”的合法等级为 {slot.MinimumLevel}-{slot.MaximumLevel}。");
            }
            foreach (var growth in hero.GrowthAttributes)
            {
                if (Math.Abs(growth.Value - MathF.Round(growth.Value)) > 0.0001f)
                    throw new InvalidOperationException($"“{growth.Name}”每级成长必须是整数。");
                if (growth.Value < growth.MinimumValue || growth.Value > growth.MaximumValue)
                    throw new InvalidOperationException($"“{growth.Name}”每级成长合法范围为 {growth.MinimumValue}-{growth.MaximumValue}。");
                if (Math.Abs(growth.Points - MathF.Round(growth.Points)) > 0.0001f)
                    throw new InvalidOperationException($"“{growth.Name}”点数必须是整数。");
                if (growth.Points < growth.PointMinimum || growth.Points > growth.PointMaximum)
                    throw new InvalidOperationException($"“{growth.Name}”点数合法范围为 {growth.PointMinimum}-{growth.PointMaximum}。");
            }
            hero.Name = HeroNameText.Text;
            hero.Level = level;
            hero.BlessingLevel = BlessingLevelCombo.SelectedItem is int blessingLevel
                ? blessingLevel
                : throw new InvalidOperationException("请选择合法赐福等级。");
            hero.Strength = ParseFloat(StrengthText.Text, "力量");
            hero.Dexterity = ParseFloat(DexterityText.Text, "敏捷");
            hero.Intelligence = ParseFloat(IntelligenceText.Text, "智力");
            hero.RemainingSkillPoints = ParseInt(RemainingPointsText.Text, "剩余技能点");
            var response = await BridgeClient.SendAsync(new EditorRequest { Action = "updateHero", Hero = hero });
            EnsureSuccess(response);
            return response.Message;
        });
    }

    private async Task RunAsync(Func<Task<string>> action)
    {
        RefreshButton.IsEnabled = false;
        SetStatus("正在处理，请稍候……", true);
        try
        {
            SetStatus(await action(), true);
        }
        catch (TimeoutException)
        {
            SetStatus("连接超时：请确认游戏已启动、桥接 Mod 已加载，并且已经进入游戏存档。", false);
        }
        catch (Exception exception)
        {
            SetStatus($"操作失败：{exception.Message}", false);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool success)
    {
        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#22C55E" : "#EF4444"));
    }

    private static AffixEdit CloneAffix(AffixEdit value)
    {
        var clone = new AffixEdit
        {
            Id = value.Id,
            Quality = value.Quality,
            QualityName = value.QualityName,
            Name = value.Name,
            ValueKind = value.ValueKind,
            GradeValues = new List<int>(value.GradeValues),
            FixedValue = value.FixedValue,
            RandomMinimum = value.RandomMinimum,
            RandomMaximum = value.RandomMaximum,
            ResolvedValue = value.ResolvedValue,
            StoredValue = value.StoredValue,
            MaximumLevel = value.MaximumLevel,
            Level = value.Level,
            ForceOverflow = value.ForceOverflow
        };
        // 用桥接下发的 ResolvedValue / StoredValue 初始化显示值（需求 2.4）：
        // 未勾选显示游戏算出来的值，勾选后显示存档里保存的值。
        clone.ApplyResponse();
        // 需求 2.3：数值本身由 AffixEdit.Value 按规则计算，所以必须把**编辑后的显示值**一起带过去。
        // 必须在 ApplyResponse 之后 —— 它会用 StoredValue/ResolvedValue 重置显示值。
        clone.CopyEditedValueFrom(value);
        clone.ValueEdited = value.ValueEdited;
        // 「装备自带」标记必须一起复制：表格里的行是由 CloneAffix 生成的，
        // 漏了这行会让删除拦截永远不触发（实测：提示“已从待生成装备中删除词条”）。
        clone.EquipmentFixed = value.EquipmentFixed;
        // 污染标记同理：提交走的就是 CloneAffix，漏了会把装备上的污染洗掉（面板不再显示、无法用涤魂泪滴净化）。
        clone.IsCorrupt = value.IsCorrupt;
        clone.IsCorruptLocked = value.IsCorruptLocked;
        // 孔位行（镶嵌组）：类型码与组名也要带过去，否则分组与类型码会丢。
        clone.SocketType = value.SocketType;
        clone.SocketGroupLabel = value.SocketGroupLabel;
        return clone;
    }

    // ============================================================
    // 需求 3：装备等级 / 锻造等级的「输入框 + 滑块」
    // ============================================================

    // 用游戏表里的合法等级设置滑块范围，并把取值夹进范围。
    private void ApplyLevelRange(int level)
    {
        var values = _snapshot?.EquipmentLevels;
        var minimum = values is { Count: > 0 } ? values[0] : 0;
        var maximum = values is { Count: > 0 } ? values[^1] : 0;
        var clamped = maximum > minimum ? Math.Clamp(level <= 0 ? minimum : level, minimum, maximum) : minimum;

        _loadingControls = true;
        try
        {
            EquipmentLevelSlider.Minimum = minimum;
            EquipmentLevelSlider.Maximum = maximum;
            EquipmentLevelSlider.Value = clamped;
            EquipmentLevelText.Text = clamped.ToString(CultureInfo.InvariantCulture);
            EquipmentLevelMinText.Text = minimum.ToString(CultureInfo.InvariantCulture);
            EquipmentLevelMaxText.Text = maximum.ToString(CultureInfo.InvariantCulture);
            if (ActiveState.Level == 0) ActiveState.Level = clamped;
        }
        finally { _loadingControls = false; }
    }

    private void ApplyForgeLevelRange(int forgeLevel)
    {
        var values = _equipmentRules?.AllowedForgeLevels;
        var minimum = values is { Count: > 0 } ? values[0] : 0;
        var maximum = values is { Count: > 0 } ? values[^1] : 0;
        var clamped = maximum > minimum ? Math.Clamp(forgeLevel, minimum, maximum) : minimum;

        _loadingControls = true;
        try
        {
            EquipmentForgeLevelSlider.Minimum = minimum;
            EquipmentForgeLevelSlider.Maximum = maximum;
            EquipmentForgeLevelSlider.Value = clamped;
            EquipmentForgeLevelText.Text = clamped.ToString(CultureInfo.InvariantCulture);
            EquipmentForgeLevelMinText.Text = minimum.ToString(CultureInfo.InvariantCulture);
            EquipmentForgeLevelMaxText.Text = maximum.ToString(CultureInfo.InvariantCulture);
        }
        finally { _loadingControls = false; }
    }

    // 读取并纠正输入框内容（需求 3.2）：小于下界取下界、范围内小数取整、其它非法取上界。
    private int ReadRangeInput(TextBox text, Slider slider)
    {
        var minimum = (int)slider.Minimum;
        var maximum = (int)slider.Maximum;
        if (maximum < minimum) maximum = minimum;
        var value = ValueCoercion.Coerce(text.Text, minimum, maximum);
        if (Math.Abs(slider.Value - value) > 0.001)
            slider.Value = value;
        var normalized = value.ToString(CultureInfo.InvariantCulture);
        if (text.Text != normalized)
            text.Text = normalized;
        return value;
    }

    private int ReadLevel() => ReadRangeInput(EquipmentLevelText, EquipmentLevelSlider);

    private int ReadForgeLevel() => ReadRangeInput(EquipmentForgeLevelText, EquipmentForgeLevelSlider);

    private void EquipmentRangeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingControls)
            return;
        // 输入过程中不打扰用户，只在失焦 / 回车时纠正（见 LostFocus / KeyDown 处理）。
    }

    private void EquipmentRangeInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingControls)
            return;
        var level = ReadLevel();
        var forge = ReadForgeLevel();
        ActiveState.Level = level;
        ActiveState.ForgeLevel = forge;
    }

    private void EquipmentRangeInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;
        EquipmentRangeInput_LostFocus(sender, e);
        _ = ReloadRulesDebouncedAsync();
    }

    private void EquipmentLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingControls)
            return;
        EquipmentLevelText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        ActiveState.Level = (int)e.NewValue;
        _ = ReloadRulesDebouncedAsync();
    }

    private void EquipmentForgeLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingControls)
            return;
        EquipmentForgeLevelText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        ActiveState.ForgeLevel = (int)e.NewValue;
    }

    // 滑块默认不响应滚轮，这里手动按步长 1 调整（需求 3.1）。
    private void EquipmentRangeSlider_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not Slider slider)
            return;
        e.Handled = true;
        var step = e.Delta > 0 ? 1 : -1;
        var value = Math.Clamp(slider.Value + step, slider.Minimum, slider.Maximum);
        slider.Value = value;
    }

    // 装备等级变化会改变词条等级上限与参考值，所以防抖后重载规则（需求 2.1.3 / 2.2）。
    private async Task ReloadRulesDebouncedAsync()
    {
        var version = ++_affixReferenceVersion;
        await Task.Delay(250);
        if (version != _affixReferenceVersion)
            return;
        await LoadEquipmentRulesAsync();
    }

    private void SelectGrade(int mainAttrGrade)
    {
        if (EquipmentGradeCombo.ItemsSource == null)
            return;
        var index = -1;
        var count = 0;
        foreach (var item in EquipmentGradeCombo.ItemsSource)
        {
            if (item is RuleOption option && option.Value == mainAttrGrade)
            {
                index = count;
                break;
            }
            count++;
        }
        _loadingControls = true;
        try
        {
            EquipmentGradeCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { _loadingControls = false; }
    }

    // 把规则注入到现有词条：等级上限、参考值，并夹紧等级。
    private void ApplyRulesToAffixes()
    {
        if (_equipmentRules == null)
            return;
        var maximum = _equipmentRules.MaximumAffixLevel;
        foreach (var affix in _affixes)
        {
            // 孔位行（镶嵌组）不参与词条规则：等级显示的是符文品质，只读。
            if (affix.IsSocket)
                continue;
            affix.MaximumLevel = maximum;
            ApplyRuleReference(affix);
            affix.ClampLevel();
            affix.ApplyReference();
        }
    }

    // 按新组合重建默认词条，但保留仍然存在的同 (词条ID, 档位) 行，避免丢失用户编辑。
    private void MergeGeneratedAffixes()
    {
        if (_equipmentRules == null)
            return;
        var existing = _affixes.ToList();
        _affixes.Clear();
        // 换模板/品级 = 换了另一件装备：镶嵌组不继承（原生的开孔表按品级给，套装/传说/神话/独特/稀有
        // 根本没有开孔表）。镶嵌组为空 = 交给游戏随机开孔。用户手动加的孔位只在本组合内保留。
        foreach (var generated in _equipmentRules.GeneratedAffixes)
        {
            var keep = existing.FirstOrDefault(a => !a.IsSocket && a.Id == generated.Id && a.Quality == generated.Quality);
            if (keep != null)
            {
                // 用户已经编辑过的行原样保留（等级、数值、强制越界都不动）。
                _affixes.Add(keep);
                continue;
            }
            var clone = CloneAffix(generated);
            // 必须先注入等级上限再夹紧，否则会被默认的 MaximumLevel = 1 夹成 1。
            clone.MaximumLevel = _equipmentRules.MaximumAffixLevel;
            // 新增的默认行取该组合允许的最高词条等级（与旧行为一致）；等级不适用的保持 -1。
            if (clone.LevelApplicable)
                clone.Level = _equipmentRules.MaximumAffixLevel;
            clone.ClampLevel();
            clone.ApplyResponse();
            _affixes.Add(clone);
        }
    }

    // 等级列与数值列现在都是普通 DataGridTextColumn（UpdateSourceTrigger=LostFocus）：
    // 标准编辑手势（F2 / Del / 退格 / 直接输入覆盖）由 DataGrid 原生提供；
    // 等级夹紧在 LevelText 的 setter 内完成，数值修正在 CommitValueText 内完成，
    // 提交由 AffixesGrid_CellEditEnding 统一触发，这里只需刷新参考值。

    // 用规则里的候选参考值填充一条词条（按 (词条ID, 档位) 精确匹配）。
    private void ApplyRuleReference(AffixEdit affix)
    {
        if (_equipmentRules == null)
            return;
        var grade = EquipmentGradeCombo.SelectedItem is RuleOption selected ? Math.Clamp(selected.Value, 1, 3) : 1;
        foreach (var option in _equipmentRules.AllowedAffixes)
            if (option.Id == affix.Id && option.Quality == affix.Quality)
            {
                affix.ValueKind = option.ValueKind;
                affix.GradeValues = new List<int>(option.GradeValues);
                affix.FixedValue = option.FixedValue;
                affix.RandomMinimum = option.RandomMinimum;
                affix.RandomMaximum = option.RandomMaximum;
                // 等级区间随档位不同：精良/稀有 = 1..affixMaxLevel，独特 = 1，传说/神话 = affixMaxLevel（固定）。
                affix.LevelMinimum = option.LevelMinimum;
                affix.LevelMaximum = option.LevelMaximum;
                affix.MaximumLevel = option.LevelMaximum > 0 ? option.LevelMaximum : affix.MaximumLevel;
                // 需求 2.4.1：「应有的值」= 当前装备质量档位对应的值。
                affix.ResolvedValue = option.ValueKind == AffixValueKind.GradeBased
                    ? (option.GradeValues.Count >= grade ? option.GradeValues[grade - 1] : option.FixedValue)
                    : option.FixedValue;
                return;
            }
    }

    // 需求 2.2：词条等级变动后按原生规则刷新参考数值。
    // 需求（方案 A）：传说(4)/独特技能(8) 的专属词条数值只读。
    // 用 BeginningEdit 而不是把列改回模板列 —— 模板列会丢掉 F2/Del/直接输入覆盖 等原生编辑手势。
    private void AffixesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not AffixEdit affix)
            return;
        // 孔位行：只有「数值」（类型码 0/1/2）与「强制越界」可编辑，等级是符文品质的回显。
        if (affix.IsSocket)
        {
            var socketHeader = e.Column.Header as string;
            if (socketHeader == "等级" || socketHeader == "词条 ID" || socketHeader == "词条描述" || socketHeader == "类别")
            {
                e.Cancel = true;
                SetStatus("孔位行的「等级」显示该孔符文的品质（没镶嵌为 0），只能改「数值」：0 随机 / 1 攻击 / 2 防御。", false);
            }
            return;
        }
        if (!affix.ValueReadOnly)
        {
            // 传说/神话/独特词条的「等级」由原生钉死（档位4/8 = 固定，档位5 = TAffix 专属行的等级），
            // 仅作展示，不允许改；数值的可改性由 ValueReadOnly 决定（神话专属需先勾强制越界）。
            if (affix.IsTierLocked && e.Column.Header as string == "等级")
            {
                e.Cancel = true;
                SetStatus($"词条 {affix.Id}（{affix.QualityName}）属于传说/神话/独有档位，等级由游戏决定、仅供展示。", false);
            }
            return;
        }
        if (e.Column.Header as string != "数值")
            return;
        e.Cancel = true;
        SetStatus(affix.Quality == 5
            ? $"神话专属词条 {affix.Id}：先勾选该行的「强制越界」，才能修改数值。"
            : $"词条 {affix.Id}（{affix.QualityName}）的数值由装备本身决定，无法修改。", false);
    }

    private async void AffixesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_loadingControls || e.EditAction != DataGridEditAction.Commit)
            return;
        if (e.Row.Item is not AffixEdit)
            return;
        // 绑定用 UpdateSourceTrigger=PropertyChanged，模型已经是最新的；等这一帧结束再请求参考值。
        await Task.Yield();
        await RefreshAffixReferenceAsync();
    }

    private async Task RefreshAffixReferenceAsync()
    {
        if (_equipmentRules == null || _snapshot == null)
            return;
        var templateId = GetSelectedTemplateId();
        if (templateId is not > 0)
            return;
        if (QualityCombo.SelectedItem is not RuleOption quality)
            return;
        var mainAttrGrade = EquipmentGradeCombo.SelectedItem is RuleOption grade ? grade.Value : 0;
        var version = ++_affixReferenceVersion;
        // 孔位行（镶嵌组）不参与词条参考值：请求与回填都只针对非孔位行，保证按顺序一一对应。
        var affixRows = _affixes.Where(row => !row.IsSocket).ToList();
        try
        {
            var response = await BridgeClient.SendAsync(new EditorRequest
            {
                Action = "affixReference",
                Equipment = new EquipmentEdit
                {
                    TemplateId = templateId.Value,
                    Quality = quality.Value,
                    Level = ReadLevel(),
                    MainAttrGrade = mainAttrGrade,
                    Affixes = affixRows.Select(CloneAffix).ToList()
                }
            });
            if (version != _affixReferenceVersion)
                return;
            EnsureSuccess(response);
            var references = response.AffixReferences;
            if (references == null)
                return;
            for (var i = 0; i < affixRows.Count && i < references.Count; i++)
            {
                var target = affixRows[i];
                var source = references[i];
                if (target.Id != source.Id || target.Quality != source.Quality)
                    continue;
                target.ValueKind = source.ValueKind;
                target.GradeValues = new List<int>(source.GradeValues);
                target.FixedValue = source.FixedValue;
                target.RandomMinimum = source.RandomMinimum;
                target.RandomMaximum = source.RandomMaximum;
                target.LevelMinimum = source.LevelMinimum;
                target.LevelMaximum = source.LevelMaximum;
                target.Level = source.Level;
                target.ResolvedValue = source.ResolvedValue;
                target.ApplyReference();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"刷新参考数值失败：{exception.Message}", false);
        }
    }

    private static void CommitGrid(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static bool HasValidationError(DependencyObject element)
    {
        if (Validation.GetHasError(element))
            return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (HasValidationError(VisualTreeHelper.GetChild(element, index)))
                return true;
        return false;
    }

    private static void EnsureSuccess(EditorResponse response)
    {
        if (!response.Success) throw new InvalidOperationException(response.Message);
    }

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw new FormatException($"{name}必须是整数。");

    private static float ParseFloat(string value, string name) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result : throw new FormatException($"{name}必须是数字（小数点使用 .）。");
}
