using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
namespace PathOfIdleEditor;

internal static class GameEditorService
{
    internal static EditorSnapshot GetSnapshot()
    {
        var lord = GetLord();
        var snapshot = new EditorSnapshot();
        // 每次连接都重新读取游戏表，游戏更新后无需同步维护桌面端的等级和品级常量。
        var qualityTable = TEquipQuality.create();
        foreach (var pair in qualityTable)
            snapshot.EquipmentQualities.Add(new RuleOption { Value = pair.Key, Name = pair.Value?.name ?? $"品级 {pair.Key}" });
        snapshot.EquipmentQualities.Sort((a, b) => a.Value.CompareTo(b.Value));
        var levelTable = TEquipLevel.create();
        foreach (var pair in levelTable)
            snapshot.EquipmentLevels.Add(pair.Key);
        snapshot.EquipmentLevels.Sort();
        // 主属性档位（原生的 TEquipGrade：1/2/3）用于「装备质量」下拉。
        // 它同时决定主属性数值与档位 1/2/3 词条的 equipGrade，所以必须让用户可见可改。
        foreach (var pair in TEquipGrade.create())
            if (pair.Value != null)
                snapshot.EquipmentGrades.Add(new RuleOption
                {
                    Value = pair.Key,
                    Name = pair.Value.name ?? $"档位 {pair.Key}"
                });
        snapshot.EquipmentGrades.Sort((a, b) => a.Value.CompareTo(b.Value));
        // 0 表示未赐福，其余合法等级完全来自当前游戏的赐福等级表。
        snapshot.BlessingLevels.Add(0);
        foreach (var pair in THeroBlessLevel.create())
            if (!snapshot.BlessingLevels.Contains(pair.Key)) snapshot.BlessingLevels.Add(pair.Key);
        snapshot.BlessingLevels.Sort();
        // 角色品级选项直接来自当前游戏表，名称和可用品级会随版本更新。
        foreach (var pair in THeroQuality.create())
            if (pair.Value != null)
                snapshot.HeroQualities.Add(new RuleOption
                {
                    Value = pair.Key,
                    Name = pair.Value.name ?? $"品级 {pair.Key}"
                });
        snapshot.HeroQualities.Sort((a, b) => a.Value.CompareTo(b.Value));
        var partTable = TEquipPart.create();
        var templates = TEquip.create();
        var maximumHeroLevel = GetMaximumHeroLevel();
        // 同类装备会共享游戏随机池参数；缓存池结果可显著减少主线程中的原生调用次数。
        var qualityPoolCache = new Dictionary<string, HashSet<int>>();
        foreach (var pair in templates)
        {
            var value = pair.Value;
            if (value == null)
                continue;
            snapshot.EquipmentTemplates.Add(new EquipmentTemplate
            {
                Id = value.id,
                Name = value.name ?? $"装备 {value.id}",
                Part = value.part,
                PartName = partTable.ContainsKey(value.part) ? partTable[value.part].name : $"部位 {value.part}",
                BaseQuality = value.baseQuality,
                AllowedQualities = GetAllowedQualities(value, snapshot.EquipmentQualities, qualityPoolCache)
            });
        }
        snapshot.EquipmentTemplates.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < lord.heroFieldList.Count; i++)
        {
            var hero = lord.heroFieldList[i]?.heroData;
            if (hero?.saveHeroData != null)
                snapshot.Heroes.Add(CreateHeroEdit(hero, maximumHeroLevel));
        }
        snapshot.Inventory = GetInventorySnapshot(lord);
        snapshot.Lord = CreateLordEdit(lord);
        return snapshot;
    }
    private static LordEdit CreateLordEdit(LordData lord)
    {
        var lordLevels = TLordLevel.create();
        var jobLevels = TJobLevel.create();
        var maximumLordLevel = GetMaximumTableKey(lordLevels, "崇拜者等级");
        var maximumJobLevel = GetMaximumTableKey(jobLevels, "魔偶等级");
        var result = new LordEdit
        {
            Level = lord.saveLordData.level,
            MaximumLevel = maximumLordLevel
        };
        foreach (var pair in jobLevels)
        {
            if (pair.Value == null)
                continue;
            result.JobLevelRules.Add(new LordJobLevelRule
            {
                Level = pair.Key,
                RequiredLordLevel = GetRequiredLordLevel(pair.Key, jobLevels),
                // TJobLevel.totalAttr 是从该级升到下一级时新增的点数，不是当前等级总点数。
                TotalAttributePoints = GetCumulativeLordJobAttributeTotal(pair.Key, jobLevels),
                MaximumTalentBonusLevel = pair.Value.masteryMaxLevel
            });
        }
        result.JobLevelRules.Sort((a, b) => a.Level.CompareTo(b.Level));
        var talentTable = TTalent.create();
        foreach (var pair in lord.jobDic)
        {
            var runtime = pair.Value;
            var save = runtime?.saveLordJobData;
            if (runtime == null || save == null)
                continue;
            var levelRule = jobLevels.ContainsKey(save.level) ? jobLevels[save.level] : null;
            var edit = new LordJobEdit
            {
                JobId = save.jobId,
                JobName = runtime.tHeroJobData?.name ?? $"职业 {save.jobId}",
                Level = save.level,
                MaximumLevel = maximumJobLevel,
                RequiredLordLevel = GetRequiredLordLevel(save.level, jobLevels),
                TotalAttributePoints = ReadLordJobAttribute(save, EAttrType.STR) +
                    ReadLordJobAttribute(save, EAttrType.DEX) + ReadLordJobAttribute(save, EAttrType.INT),
                Strength = ReadLordJobAttribute(save, EAttrType.STR),
                Dexterity = ReadLordJobAttribute(save, EAttrType.DEX),
                Intelligence = ReadLordJobAttribute(save, EAttrType.INT)
            };
            foreach (var rule in result.JobLevelRules)
                edit.AttributeRules.Add(CreateLordJobAttributeRule(runtime, rule, jobLevels));
            ApplyLordJobAttributeRule(edit, edit.AttributeRules.Find(item => item.Level == edit.Level));
            // LordTalentData.Create 接收 TTalent.id；魔偶项目是当前职业下带 masteryId 的天赋。
            // 直接从表中枚举全部合法项，未获得项显示为 0，避免依赖尚未建立的运行时列表。
            foreach (var talentPair in talentTable)
            {
                var talent = talentPair.Value;
                if (talent == null || talent.jobId != save.jobId || talent.masteryId <= 0)
                    continue;
                edit.TalentBonuses.Add(new LordTalentBonusEdit
                {
                    TalentId = talent.id,
                    Kind = talent.skillId > 0 ? "技能" : "天赋/专精",
                    Name = GetTalentDisplayName(talent),
                    Level = save.talentDic.ContainsKey(talent.id) ? save.talentDic[talent.id] : 0,
                    MaximumLevel = Math.Max(0, save.GetMasteryMaxLevel())
                });
            }
            edit.TalentBonuses.Sort((a, b) => a.TalentId.CompareTo(b.TalentId));
            result.Jobs.Add(edit);
        }
        result.Jobs.Sort((a, b) => a.JobId.CompareTo(b.JobId));
        return result;
    }
    internal static EditorResponse UpdateLord(LordEdit edit)
    {
        var lord = GetLord();
        var lordLevels = TLordLevel.create();
        var jobLevels = TJobLevel.create();
        if (!lordLevels.ContainsKey(edit.Level))
            throw new InvalidOperationException($"崇拜者等级 {edit.Level} 不存在于当前游戏等级表中。");
        // 只校验和写入实际发生变化的职业；单独修改崇拜者等级时不能重写六个魔偶。
        var validated = new List<(LordJobData Runtime, LordJobEdit Request, TJobLevel Rule)>();
        var seenJobs = new HashSet<int>();
        foreach (var request in edit.Jobs)
        {
            if (!seenJobs.Add(request.JobId))
                throw new InvalidOperationException($"职业 {request.JobId} 重复出现，请刷新后重试。");
            if (!lord.jobDic.ContainsKey(request.JobId))
                throw new InvalidOperationException($"职业 {request.JobId} 已不存在，请刷新游戏数据。");
            var runtime = lord.jobDic[request.JobId];
            var currentSave = runtime.saveLordJobData;
            var jobChanged = request.Level != currentSave.level ||
                request.Strength != ReadLordJobAttribute(currentSave, EAttrType.STR) ||
                request.Dexterity != ReadLordJobAttribute(currentSave, EAttrType.DEX) ||
                request.Intelligence != ReadLordJobAttribute(currentSave, EAttrType.INT) ||
                HasLordTalentChanges(runtime, request.TalentBonuses);
            if (!jobChanged)
                continue;
            if (!jobLevels.ContainsKey(request.Level))
                throw new InvalidOperationException($"“{request.JobName}”的魔偶等级 {request.Level} 不存在于当前游戏等级表中。");
            var requiredLordLevel = GetRequiredLordLevel(request.Level, jobLevels);
            if (edit.Level < requiredLordLevel)
                throw new InvalidOperationException($"“{request.JobName}”升到 {request.Level} 级要求崇拜者至少 {requiredLordLevel} 级。");
            var rule = jobLevels[request.Level];
            var attributeRule = CreateLordJobAttributeRule(runtime, new LordJobLevelRule
            {
                Level = request.Level,
                TotalAttributePoints = GetCumulativeLordJobAttributeTotal(request.Level, jobLevels)
            }, jobLevels);
            ValidateLordJobAttribute(request.JobName, "力量", request.Strength, attributeRule.StrengthMinimum, attributeRule.StrengthMaximum);
            ValidateLordJobAttribute(request.JobName, "敏捷", request.Dexterity, attributeRule.DexterityMinimum, attributeRule.DexterityMaximum);
            ValidateLordJobAttribute(request.JobName, "智力", request.Intelligence, attributeRule.IntelligenceMinimum, attributeRule.IntelligenceMaximum);
            var attributeTotal = request.Strength + request.Dexterity + request.Intelligence;
            if (attributeTotal != attributeRule.TotalAttributePoints)
                throw new InvalidOperationException($"“{request.JobName}”{request.Level} 级的力量、敏捷、智力累计总和必须为 {attributeRule.TotalAttributePoints}，当前为 {attributeTotal}。");
            var currentTalentIds = GetLordTalentIds(currentSave.jobId);
            if (request.TalentBonuses.Count != currentTalentIds.Count)
                throw new InvalidOperationException($"“{request.JobName}”的天赋加成列表已经变化，请刷新后重试。");
            var requestedTalentIds = new HashSet<int>();
            foreach (var talent in request.TalentBonuses)
            {
                if (!requestedTalentIds.Add(talent.TalentId) || !currentTalentIds.Contains(talent.TalentId))
                    throw new InvalidOperationException($"“{request.JobName}”包含无效的天赋加成 {talent.TalentId}。");
                var maximumTalentLevel = Math.Max(0, rule.masteryMaxLevel);
                if (talent.Level < 0 || talent.Level > maximumTalentLevel)
                    throw new InvalidOperationException($"“{request.JobName}”的“{talent.Name}”等级加成合法范围为 0-{maximumTalentLevel}。");
            }
            validated.Add((runtime, request, rule));
        }
        lord.saveLordData.level = edit.Level;
        lord.saveLordData.exp = 0;
        lord.tLordLevelData = lordLevels[edit.Level];
        //lord.CreateOfflineResList();
        foreach (var entry in validated)
        {
            var runtime = entry.Runtime;
            var save = runtime.saveLordJobData;
            var previousLevel = save.level;
            var previousRule = runtime.tJobLevelData;
            var previousAttributes = CopyIntDictionary(save.attrUpDic);
            var previousTalents = CopyIntDictionary(save.talentDic);
            try
            {
                runtime.RemoveJobAttrUp();
                save.level = entry.Request.Level;
                runtime.tJobLevelData = entry.Rule;
                save.attrUpDic[(int)EAttrType.STR] = entry.Request.Strength;
                save.attrUpDic[(int)EAttrType.DEX] = entry.Request.Dexterity;
                save.attrUpDic[(int)EAttrType.INT] = entry.Request.Intelligence;
                save.talentDic.Clear();
                foreach (var talent in entry.Request.TalentBonuses)
                    if (talent.Level > 0)
                        save.talentDic[talent.TalentId] = talent.Level;
                // 原生初始化会重建显示列表、技能加成和等级锁，再把新属性加成应用到对应职业角色。
                runtime.Init();
                runtime.AddJobAttrUp();
            }
            catch (Exception exception)
            {
                // 原生初始化失败时恢复本次写入前的内存数据，避免游戏继续运行在半修改状态。
                save.level = previousLevel;
                runtime.tJobLevelData = previousRule;
                RestoreIntDictionary(save.attrUpDic, previousAttributes);
                RestoreIntDictionary(save.talentDic, previousTalents);
                runtime.Init();
                runtime.AddJobAttrUp();
                throw new InvalidOperationException($"“{entry.Request.JobName}”初始化失败，修改已回滚：{exception.Message}", exception);
            }
        }
        SaveNow();
        return new EditorResponse
        {
            Success = true,
            Message = validated.Count == 0
                ? $"已保存崇拜者等级 {edit.Level}；六个职业魔偶未被改写。"
                : $"已保存崇拜者等级 {edit.Level}，并更新 {validated.Count} 个职业魔偶。",
            Lord = CreateLordEdit(lord)
        };
    }
    private static int GetRequiredLordLevel(
        int jobLevel,
        Il2CppSystem.Collections.Generic.Dictionary<int, TJobLevel> jobLevels)
    {
        if (jobLevel <= 1)
            return 1;
        return jobLevels.ContainsKey(jobLevel - 1) ? Math.Max(1, jobLevels[jobLevel - 1].lordLevel) : 1;
    }
    private static int GetCumulativeLordJobAttributeTotal(
        int level,
        Il2CppSystem.Collections.Generic.Dictionary<int, TJobLevel> jobLevels)
    {
        var total = 0;
        // 等级 N 的总加成由 1->2、2->3……N-1->N 的升级奖励累计而成。
        foreach (var pair in jobLevels)
            if (pair.Value != null && pair.Key < level)
                total += Math.Max(0, pair.Value.totalAttr);
        return total;
    }
    private static int GetTargetLordJobAttributeTotal(
        SaveLordJobData save,
        int targetLevel,
        Il2CppSystem.Collections.Generic.Dictionary<int, TJobLevel> jobLevels)
    {
        var total = ReadLordJobAttribute(save, EAttrType.STR) +
            ReadLordJobAttribute(save, EAttrType.DEX) + ReadLordJobAttribute(save, EAttrType.INT);
        if (targetLevel > save.level)
        {
            for (var level = save.level; level < targetLevel; level++)
                if (jobLevels.ContainsKey(level)) total += Math.Max(0, jobLevels[level].totalAttr);
        }
        else
        {
            for (var level = targetLevel; level < save.level; level++)
                if (jobLevels.ContainsKey(level)) total -= Math.Max(0, jobLevels[level].totalAttr);
        }
        return Math.Max(0, total);
    }
    private static int GetMaximumTableKey<T>(Il2CppSystem.Collections.Generic.Dictionary<int, T> table, string tableName)
    {
        var maximum = 0;
        foreach (var pair in table) maximum = Math.Max(maximum, pair.Key);
        if (maximum <= 0)
            throw new InvalidOperationException($"当前游戏的{tableName}表为空。");
        return maximum;
    }
    private static int ReadLordJobAttribute(SaveLordJobData save, EAttrType type)
    {
        var key = (int)type;
        return save.attrUpDic != null && save.attrUpDic.ContainsKey(key) ? save.attrUpDic[key] : 0;
    }
    private static Dictionary<int, int> CopyIntDictionary(
        Il2CppSystem.Collections.Generic.Dictionary<int, int> source)
    {
        var result = new Dictionary<int, int>();
        foreach (var pair in source) result[pair.Key] = pair.Value;
        return result;
    }
    private static void RestoreIntDictionary(
        Il2CppSystem.Collections.Generic.Dictionary<int, int> target,
        Dictionary<int, int> source)
    {
        target.Clear();
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }
    private static HashSet<int> GetLordTalentIds(int jobId)
    {
        var result = new HashSet<int>();
        foreach (var pair in TTalent.create())
            if (pair.Value != null && pair.Value.jobId == jobId && pair.Value.masteryId > 0)
                result.Add(pair.Key);
        return result;
    }
    private static bool HasLordTalentChanges(LordJobData runtime, List<LordTalentBonusEdit> requested)
    {
        var save = runtime.saveLordJobData;
        var currentIds = GetLordTalentIds(save.jobId);
        if (currentIds.Count != requested.Count)
            return true;
        foreach (var talent in requested)
        {
            var currentLevel = save.talentDic.ContainsKey(talent.TalentId) ? save.talentDic[talent.TalentId] : 0;
            if (!currentIds.Contains(talent.TalentId) || currentLevel != talent.Level)
                return true;
        }
        return false;
    }
    private static LordJobAttributeRule CreateLordJobAttributeRule(
        LordJobData runtime,
        LordJobLevelRule levelRule,
        Il2CppSystem.Collections.Generic.Dictionary<int, TJobLevel> jobLevels)
    {
        var save = runtime.saveLordJobData;
        var targetTotal = GetTargetLordJobAttributeTotal(save, levelRule.Level, jobLevels);
        var result = new LordJobAttributeRule
        {
            Level = levelRule.Level,
            TotalAttributePoints = targetTotal
        };
        if (!jobLevels.ContainsKey(levelRule.Level))
            return result;
        var previousRule = runtime.tJobLevelData;
        try
        {
            // 原生 CreateAttrRangeList 返回的是单次升级可分配范围。总加成的合法范围
            // 必须把 1->2 到目标等级的每次升级范围逐项累加。
            foreach (var levelPair in jobLevels)
            {
                if (levelPair.Value == null || levelPair.Key >= levelRule.Level)
                    continue;
                runtime.tJobLevelData = levelPair.Value;
                runtime.CreateAttrRangeList();
                for (var index = 0; index < runtime.attrRangeList.Count; index++)
                {
                    var range = runtime.attrRangeList[index];
                    if (range == null) continue;
                    if (range.type == EAttrType.STR) { result.StrengthMinimum += range.minValue; result.StrengthMaximum += range.maxValue; }
                    if (range.type == EAttrType.DEX) { result.DexterityMinimum += range.minValue; result.DexterityMaximum += range.maxValue; }
                    if (range.type == EAttrType.INT) { result.IntelligenceMinimum += range.minValue; result.IntelligenceMaximum += range.maxValue; }
                }
            }
        }
        finally
        {
            runtime.tJobLevelData = previousRule;
            runtime.CreateAttrRangeList();
        }
        return result;
    }
    private static void ApplyLordJobAttributeRule(LordJobEdit edit, LordJobAttributeRule? rule)
    {
        if (rule == null)
            return;
        edit.TotalAttributePoints = rule.TotalAttributePoints;
        edit.StrengthMinimum = rule.StrengthMinimum;
        edit.StrengthMaximum = rule.StrengthMaximum;
        edit.DexterityMinimum = rule.DexterityMinimum;
        edit.DexterityMaximum = rule.DexterityMaximum;
        edit.IntelligenceMinimum = rule.IntelligenceMinimum;
        edit.IntelligenceMaximum = rule.IntelligenceMaximum;
    }
    private static void ValidateLordJobAttribute(string jobName, string attributeName, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"“{jobName}”的{attributeName}加成合法范围为 {minimum}-{maximum}，当前为 {value}。");
    }
    internal static InventorySnapshot GetInventorySnapshot() => GetInventorySnapshot(GetLord());
    private static InventorySnapshot GetInventorySnapshot(LordData lord)
    {
        var snapshot = new InventorySnapshot();
        // 可添加物品完全来自当前游戏表；装备由装备生成器处理，不在这里重复暴露。
        foreach (var pair in TRes.create())
            if (pair.Value != null) snapshot.AvailableItems.Add(new InventoryTemplate
            {
                Type = (int)EItemType.res,
                TypeName = GetItemTypeName(EItemType.res),
                Id = pair.Key,
                Name = pair.Value.name ?? $"资源 {pair.Key}",
                Quality = pair.Value.quality
            });
        var boxLevelTable = TBoxLevel.create();
        var maximumBoxLevel = Math.Max(0, Game.dataMgr?.nowVersionData?.equipBoxMaxLevel ?? 0);
        foreach (var pair in TTool.create())
        {
            var tool = pair.Value;
            if (tool == null)
                continue;
            if (tool.type == (int)EToolType.equipBox)
            {
                // 装备宝箱的 level 会直接决定开箱时使用的装备等级范围，不能统一写成 1。
                foreach (var boxLevelPair in boxLevelTable)
                {
                    if (boxLevelPair.Value == null || boxLevelPair.Key > maximumBoxLevel)
                        continue;
                    snapshot.AvailableItems.Add(new InventoryTemplate
                    {
                        Type = (int)EItemType.tool,
                        TypeName = GetItemTypeName(EItemType.tool),
                        Id = pair.Key,
                        Name = tool.name ?? $"工具 {pair.Key}",
                        Quality = tool.quality,
                        Level = boxLevelPair.Key,
                        LevelDescription = $"宝箱等级 {boxLevelPair.Key}（装备 {boxLevelPair.Value.minEquipLevel}-{boxLevelPair.Value.maxEquipLevel} 级）"
                    });
                }
            }
            else
            {
                snapshot.AvailableItems.Add(new InventoryTemplate
                {
                    Type = (int)EItemType.tool,
                    TypeName = GetItemTypeName(EItemType.tool),
                    Id = pair.Key,
                    Name = tool.name ?? $"工具 {pair.Key}",
                    Quality = tool.quality,
                    Level = 1
                });
            }
        }
        var runeQualityTable = TRuneQuality.create();
        foreach (var pair in TRune.create())
        {
            if (pair.Value == null)
                continue;
            // 原生 CreateRune 只校验品级是否存在于 TRuneQuality，并不把符文限制在 baseQuality。
            // 因此把当前游戏表中的全部合法品级交给用户选择，而不是在编辑器中硬编码范围。
            foreach (var qualityPair in runeQualityTable)
            {
                if (qualityPair.Value == null)
                    continue;
                snapshot.AvailableItems.Add(new InventoryTemplate
                {
                    Type = (int)EItemType.rune,
                    TypeName = GetItemTypeName(EItemType.rune),
                    Id = pair.Key,
                    Name = pair.Value.name ?? $"符文 {pair.Key}",
                    Quality = qualityPair.Key,
                    LevelDescription = $"品级 {qualityPair.Key} · {qualityPair.Value.name}"
                });
            }
        }
        foreach (var pair in TCurio.create())
            if (pair.Value != null) snapshot.AvailableItems.Add(new InventoryTemplate
            {
                Type = (int)EItemType.curio,
                TypeName = GetItemTypeName(EItemType.curio),
                Id = pair.Key,
                Name = pair.Value.name ?? $"奇物 {pair.Key}",
                Quality = pair.Value.quality
            });
        snapshot.AvailableItems.Sort((a, b) =>
        {
            var typeCompare = a.Type.CompareTo(b.Type);
            if (typeCompare != 0) return typeCompare;
            var idCompare = a.Id.CompareTo(b.Id);
            if (idCompare != 0) return idCompare;
            var qualityCompare = a.Quality.CompareTo(b.Quality);
            return qualityCompare != 0 ? qualityCompare : a.Level.CompareTo(b.Level);
        });
        // 需求 8：资源不在背包里，改为直接列出存档里的资源容器。
        // 列出全部 TRes（未拥有的数量为 0），这样在「资源」标签下可以直接给任意资源设数量。
        var resourceStore = TryGetResourceStore();
        var resourceTown = Game.dataMgr?.nowSeasonData?.townData;
        foreach (var pair in TRes.create())
        {
            if (pair.Value == null)
                continue;
            var count = resourceStore != null && resourceStore.ContainsKey(pair.Key) && resourceStore[pair.Key] != null
                ? resourceStore[pair.Key].count
                : 0;
            // 上限交给客户端做输入修正（需求：超出上限修正为上限）。
            // GetResMax 对未定义的类型返回 0，与原版 AddRes 的「max <= 0 视为无上限」一致。
            var maximum = 0;
            if (resourceTown != null)
            {
                try { maximum = resourceTown.GetResMax((EResType)pair.Key); }
                catch (Exception exception) { Plugin.Log.LogWarning($"[ResourceWrite] 读取资源 {pair.Key} 上限失败：{exception.Message}"); }
            }
            snapshot.BagItems.Add(new InventoryItemEdit
            {
                Container = ResourceContainer,
                ContainerName = "资源",
                // FieldIndex 在资源槽里借用为资源 ID。
                FieldIndex = pair.Key,
                Type = (int)EItemType.res,
                TypeName = GetItemTypeName(EItemType.res),
                Id = pair.Key,
                Name = pair.Value.name ?? $"资源 {pair.Key}",
                Quality = pair.Value.quality,
                Level = 0,
                Count = count,
                Maximum = maximum
            });
        }
        // 符文背包、奇物背包以及 5x5 道具袋仍是独立的原生背包容器。
        AddInventoryFields(snapshot, lord.lordBagData.GetFieldList(EItemType.rune), 0, "背包");
        AddInventoryFields(snapshot, lord.lordBagData.GetFieldList(EItemType.curio), 0, "背包");
        AddInventoryFields(snapshot, lord.lordWalletData.fieldList, 1, "5x5 道具袋");
        snapshot.BagItems.Sort((a, b) =>
        {
            var typeCompare = a.Type.CompareTo(b.Type);
            return typeCompare != 0 ? typeCompare : a.FieldIndex.CompareTo(b.FieldIndex);
        });
        return snapshot;
    }
    internal static EditorResponse UpdateInventoryItem(InventoryItemEdit edit)
    {
        var lord = GetLord();
        var change = ApplyInventoryCount(lord, edit);
        if (string.IsNullOrEmpty(change))
            return new EditorResponse
            {
                Success = true,
                Message = $"“{edit.Name}”的数量与当前存档一致（{edit.Count}），未做修改。",
                Inventory = GetInventorySnapshot(lord)
            };
        SaveNow();
        return new EditorResponse
        {
            Success = true,
            Message = $"已修改：{change}。",
            Inventory = GetInventorySnapshot(lord)
        };
    }
    // 批量修改（需求：在表格里直接编辑、一次提交多行）。
    // 逐行只写真正变化的值，最后统一保存一次；返回逐项变更摘要。
    internal static EditorResponse UpdateInventoryItems(List<InventoryItemEdit> edits)
    {
        var lord = GetLord();
        var changes = new List<string>();
        foreach (var edit in edits)
        {
            var change = ApplyInventoryCount(lord, edit);
            if (!string.IsNullOrEmpty(change))
                changes.Add(change);
        }
        if (changes.Count == 0)
            return new EditorResponse
            {
                Success = true,
                Message = "表格里没有需要应用的改动（数量与当前存档一致）。",
                Inventory = GetInventorySnapshot(lord)
            };
        SaveNow();
        var preview = string.Join("；", changes.Count > 6 ? changes.GetRange(0, 6) : changes);
        var more = changes.Count > 6 ? $"；……共 {changes.Count} 项" : "";
        return new EditorResponse
        {
            Success = true,
            Message = $"已应用 {changes.Count} 项修改：{preview}{more}。",
            Inventory = GetInventorySnapshot(lord)
        };
    }
    // 写入单条数量，返回可读的变更描述；数量未变化时返回空串。
    // 单条与批量都走这里，避免两套逻辑（需求 7）。
    private static string ApplyInventoryCount(LordData lord, InventoryItemEdit edit)
    {
        if (edit.Count < 0)
            throw new InvalidOperationException("物品数量不能小于 0。");
        var type = (EItemType)edit.Type;
        if (type == EItemType.equip || !IsEditableItemType(type))
            throw new InvalidOperationException("该物品类型不能在物品编辑器中修改。");
        // 需求 8：资源槽（Container = 2，FieldIndex 借用为资源 ID）直接写存档里的资源容器。
        if (edit.Container == ResourceContainer)
        {
            var resourceTable = TRes.create();
            var resourceName = resourceTable.ContainsKey(edit.Id) ? resourceTable[edit.Id].name : $"资源 {edit.Id}";
            var previous = GetResourceCount(edit.Id);
            if (previous == edit.Count)
                return "";
            var effective = SetResourceCount(edit.Id, edit.Count);
            return effective == edit.Count
                ? $"{resourceName} {previous} → {effective}"
                : $"{resourceName} {previous} → {effective}（受上限限制，请求 {edit.Count}）";
        }
        if (edit.Container != 0 && edit.Container != 1)
            throw new InvalidOperationException("物品存放位置无效，请刷新游戏数据后重试。");
        var fields = edit.Container == 1 ? lord.lordWalletData.fieldList : lord.lordBagData.GetFieldList(type);
        ItemFieldData? target = null;
        for (var i = 0; fields != null && i < fields.Count; i++)
        {
            var field = fields[i];
            var save = field?.itemData?.saveItemData;
            if (field?.saveItemFieldData?.index == edit.FieldIndex && save != null &&
                save.type == type && save.id == edit.Id && save.quality == edit.Quality && save.level == edit.Level)
            {
                target = field;
                break;
            }
        }
        if (target?.itemData?.saveItemData == null)
            throw new InvalidOperationException("背包物品已经发生变化，请刷新后重试。");
        var oldCount = target.itemData.saveItemData.count;
        if (edit.Count == oldCount)
            return "";
        if (edit.Count > oldCount)
        {
            if (edit.Container == 1) lord.lordWalletData.StackItem(target, edit.Count - oldCount);
            else lord.lordBagData.StackItem(target, edit.Count - oldCount);
        }
        else
        {
            if (edit.Container == 1) lord.lordWalletData.ReduceItem(target, oldCount - edit.Count);
            else lord.lordBagData.ReduceItem(target, oldCount - edit.Count);
        }
        var bagName = string.IsNullOrEmpty(edit.Name) ? $"物品 {edit.Id}" : edit.Name;
        return edit.Count == 0 ? $"{bagName} {oldCount} → 已删除" : $"{bagName} {oldCount} → {edit.Count}";
    }
    internal static EditorResponse AddInventoryItem(InventoryAddEdit edit)
    {
        if (edit.Count <= 0)
            throw new InvalidOperationException("增加数量必须大于 0。");
        var lord = GetLord();
        var type = (EItemType)edit.Type;
        // 需求 8：资源不进背包，直接写存档里的资源容器（原生模型见 SetResourceCount 的注释）。
        if (type == EItemType.res)
        {
            Plugin.Log.LogInfo($"[ResourceWrite] 收到增加请求 id={edit.Id} count={edit.Count}");
            var resourceTable = TRes.create();
            if (!resourceTable.ContainsKey(edit.Id))
                throw new InvalidOperationException($"资源 {edit.Id} 不存在于当前游戏表中。");
            var resourceName = resourceTable[edit.Id].name;
            var previous = GetResourceCount(edit.Id);
            var effective = SetResourceCount(edit.Id, previous + edit.Count);
            SaveNow();
            return new EditorResponse
            {
                Success = true,
                Message = effective == previous + edit.Count
                    ? $"已增加“{resourceName}”×{edit.Count}（{previous} → {effective}）。"
                    : $"“{resourceName}”受游戏上限限制，实际从 {previous} 增加到 {effective}。",
                Inventory = GetInventorySnapshot(lord)
            };
        }
        SaveItemData? saveItem;
        string name;
        switch (type)
        {
            case EItemType.tool:
                {
                    var table = TTool.create();
                    if (!table.ContainsKey(edit.Id)) throw new InvalidOperationException($"工具 {edit.Id} 不存在于当前游戏表中。");
                    var tool = table[edit.Id];
                    var level = Math.Max(1, edit.Level);
                    if (tool.type == (int)EToolType.equipBox)
                    {
                        var boxLevels = TBoxLevel.create();
                        var maximumLevel = Math.Max(0, Game.dataMgr?.nowVersionData?.equipBoxMaxLevel ?? 0);
                        if (!boxLevels.ContainsKey(level) || level > maximumLevel)
                            throw new InvalidOperationException($"装备宝箱等级 {level} 不存在于当前游戏版本规则中。");
                    }
                    name = tool.name;
                    saveItem = SaveItemData.CreateTool(edit.Id, edit.Count, level);
                    break;
                }
            case EItemType.rune:
                {
                    var table = TRune.create();
                    if (!table.ContainsKey(edit.Id)) throw new InvalidOperationException($"符文 {edit.Id} 不存在于当前游戏表中。");
                    if (!TRuneQuality.create().ContainsKey(edit.Quality))
                        throw new InvalidOperationException($"符文品质 {edit.Quality} 不存在于当前游戏规则表中。");
                    name = table[edit.Id].name;
                    saveItem = SaveItemData.CreateRune(edit.Id, edit.Quality, edit.Count);
                    break;
                }
            case EItemType.curio:
                {
                    var table = TCurio.create();
                    if (!table.ContainsKey(edit.Id)) throw new InvalidOperationException($"奇物 {edit.Id} 不存在于当前游戏表中。");
                    name = table[edit.Id].name;
                    saveItem = SaveItemData.CreateCurio(edit.Id, edit.Count);
                    break;
                }
            default:
                throw new InvalidOperationException("该物品类型不能通过物品编辑器增加。");
        }
        if (saveItem == null)
            throw new InvalidOperationException("游戏原生物品生成器拒绝了当前物品。");
        // 背包物品没有“需求数量”；传 1 会让游戏把数量错误显示成“当前数量/1”。
        var item = ItemData.Create(saveItem, EItemPosType.bag, 0)
            ?? throw new InvalidOperationException("游戏创建运行时物品数据失败。");
        if (!lord.lordBagData.addItemToBag(item))
            throw new InvalidOperationException("背包空间不足，物品没有加入存档。");
        SaveNow();
        return new EditorResponse
        {
            Success = true,
            Message = $"已增加“{name}”×{edit.Count}。",
            Inventory = GetInventorySnapshot(lord)
        };
    }
    private static void AddInventoryFields(
        InventorySnapshot snapshot,
        Il2CppSystem.Collections.Generic.List<ItemFieldData> fields,
        int container,
        string containerName)
    {
        for (var i = 0; fields != null && i < fields.Count; i++)
        {
            var field = fields[i];
            var itemData = field?.itemData;
            var fieldSave = field?.saveItemFieldData;
            var save = itemData?.saveItemData;
            if (itemData == null || fieldSave == null || save == null || save.count <= 0 ||
                save.type == EItemType.equip || !IsEditableItemType(save.type))
                continue;
            snapshot.BagItems.Add(new InventoryItemEdit
            {
                Container = container,
                ContainerName = containerName,
                FieldIndex = fieldSave.index,
                Type = (int)save.type,
                TypeName = GetItemTypeName(save.type),
                Id = save.id,
                Name = itemData.GetName() ?? $"物品 {save.id}",
                Quality = save.quality,
                Level = save.level,
                Count = save.count
            });
        }
    }
    private static bool IsEditableItemType(EItemType type) =>
        type == EItemType.res || type == EItemType.tool || type == EItemType.rune || type == EItemType.curio;
    private static string GetItemTypeName(EItemType type) => type switch
    {
        EItemType.res => "资源",
        EItemType.tool => "工具",
        EItemType.rune => "符文",
        EItemType.curio => "奇物",
        _ => $"类型 {(int)type}"
    };
    /*
    private static void DumpFields(string tag, Il2CppSystem.Object obj)
    {
        if (obj == null) { Plugin.Log.LogInfo($"[{tag}] null"); return; }
        var type = obj.GetType();
        Plugin.Log.LogInfo($"[{tag}] type={type.FullName}");
        var flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;
        var fields = type.GetFields(flags);
        Plugin.Log.LogInfo($"[{tag}] fieldCount={fields.Length}");
        foreach (var f in fields)
        {
            string value;
            try { value = f.GetValue(obj)?.ToString() ?? "null"; }
            catch (Exception ex) { value = $"<err: {ex.GetType().Name}>"; }
            Plugin.Log.LogInfo($"[{tag}] {f.Name} ({f.FieldType.Name}) = {value}");
        }
    }
    */
    private static readonly Dictionary<string, EquipmentLocator> EquipmentLocators = new();
    private sealed class EquipmentLocator
    {
        internal int FieldIndex;
        internal int TemplateId;
        internal int Quality;
        internal int Level;
        internal int ForgeLevel;
        internal string Fingerprint = "";
    }
    internal static EquipmentRules GetEquipmentRules(EquipmentEdit edit)
    {
        var template = RequireEquipmentRequest(edit);
        var levelData = TEquipLevel.create()[edit.Level];
        // 创建未入包的预览装备，让游戏原生生成器决定该组合的默认词条和数量规则。
        var preview = SaveItemData.CreateEquip(edit.TemplateId, edit.Quality, edit.Level, 0f)
            ?? throw new InvalidOperationException("游戏原生装备生成器拒绝了当前组合。");
        // 神话生成会把基础装备 ID 切换到 upMyth 指向的专用模板；专属词条位于该模板，
        // 不能继续从下拉框中选中的基础模板读取。
        var effectiveTemplate = ResolveGeneratedEquipmentTemplate(preview, template);
        var effectiveBaseAffixId = GetBaseAffixId(effectiveTemplate);
        var rules = new EquipmentRules
        {
            MaximumAffixLevel = Math.Max(1, levelData.affixMaxLevel)
        };
        var equipmentLevelTable = TEquipLevel.create();
        // 游戏称该系统为“锻造”。锻造等级必须同时存在于锻造表，且基础等级加锻造等级后仍有装备等级数据。
        foreach (var pair in TEquipForge.create())
            if (equipmentLevelTable.ContainsKey(edit.Level + pair.Key))
                rules.AllowedForgeLevels.Add(pair.Key);
        rules.AllowedForgeLevels.Sort();
        if (rules.AllowedForgeLevels.Count == 0)
            throw new InvalidOperationException($"装备等级 {edit.Level} 没有可用的锻造等级。");
        var affixTable = TAffix.create();
        var affixQualityTable = TAffixQuality.create();
        var equipmentQuality = TEquipQuality.create()[edit.Quality];
        // 原生「存档 affixList」是几条固定通道拼出来的，额度必须按原生规则算，
        // 不能只看一次随机预览抽到了什么（预览的稀有条数可能只抽到下限 2 条，那样额度会偏小）：
        //   · 档位 1 基础词条：TEquip.baseAffix 非空则恒 1 条，装备初始化时写入（:2219170-2219197）
        //   · 档位 2 精良：AddFineAffix，数量 = TEquipQuality.fineAffixCount（:2215324，创建 :2215412）
        //   · 档位 3 稀有：AddRareAffix 按品级分支（:2215720-2215740）——
        //         品级 3/6/8 → RandomRangeInt(2, rareAffixCount + 1)，上限 rareAffixCount 且至少 2 条
        //         品级 4/5   → legendAffixCount
        //   · 档位 5 神话专属：AddMythAffix，仅品级 5 且 legendAffixArr 非空时追加（:2215526-2215571）
        //   · 档位 8 独有技能：运行时追加，不占额度（:1623227-1623245 / :2148328-2148341）
        AddAffixQualityLimit(rules, affixQualityTable, (int)EItemQualityType.fine,
            Math.Max(0, equipmentQuality.fineAffixCount));
        // 精良是固定条数 → 下限 = 上限。
        SetAffixQualityMinimum(rules, (int)EItemQualityType.fine, Math.Max(0, equipmentQuality.fineAffixCount));
        var nativeRareAffixCount = edit.Quality is (int)EItemQualityType.legend or (int)EItemQualityType.myth
            ? effectiveTemplate.legendAffixCount
            : equipmentQuality.rareAffixCount;
        AddAffixQualityLimit(rules, affixQualityTable, (int)EItemQualityType.rare, Math.Max(0, nativeRareAffixCount));
        // 稀有：品级 3/6/8 是 RandomRangeInt(2, rareAffixCount+1)（Unity 上界排他）→ 下限 2；
        //       品级 4/5 是 legendAffixCount 固定值 → 下限 = 上限；品级 1/2/7 AddRareAffix 直接跳过 → 0。
        var nativeRareMinimum = edit.Quality is (int)EItemQualityType.rare
            or (int)EItemQualityType.sets or (int)EItemQualityType.unique
            ? Math.Min(2, Math.Max(0, nativeRareAffixCount))
            : Math.Max(0, nativeRareAffixCount);
        SetAffixQualityMinimum(rules, (int)EItemQualityType.rare, nativeRareMinimum);
        if (effectiveBaseAffixId > 0)
        {
            AddAffixQualityLimit(rules, affixQualityTable, (int)EItemQualityType.common, 1);
            SetAffixQualityMinimum(rules, (int)EItemQualityType.common, 1);
        }
        var generatedQualityCounts = new Dictionary<int, int>();
        if (preview.affixList != null)
        {
            for (var i = 0; i < preview.affixList.Count; i++)
            {
                var affix = preview.affixList[i];
                if (affix == null)
                    continue;
                generatedQualityCounts.TryGetValue(affix.quality, out var qualityCount);
                generatedQualityCounts[affix.quality] = qualityCount + 1;
                var qualityName = affixQualityTable.ContainsKey(affix.quality)
                    ? affixQualityTable[affix.quality].name
                    : $"词条档位 {affix.quality}";
                rules.AffixQualityNames[affix.quality] = qualityName;
                rules.GeneratedAffixes.Add(new AffixEdit
                {
                    Id = affix.id,
                    Quality = affix.quality,
                    QualityName = qualityName,
                    Name = affixTable.ContainsKey(affix.id) ? GetAffixName(affixTable[affix.id]) : $"词条 {affix.id}",
                    Level = Math.Clamp(affix.level, 1, rules.MaximumAffixLevel),
                    // 编辑器默认不复用预览装备的随机值，提交时让游戏为最终装备重新随机。
                    Value = null,
                    // 基础词条（档位 1，见 GetBaseAffixId）是装备自带：不允许删除，否则写回时会丢。
                    EquipmentFixed = affix.quality == 1 && affix.id == effectiveBaseAffixId
                });
            }
        }
        // 套装、传奇等装备可能带有品级表计数之外的固定词条；这些类别以原生结果为准保留。
        foreach (var pair in generatedQualityCounts)
        {
            rules.AffixQualityLimits.TryGetValue(pair.Key, out var configuredCount);
            AddAffixQualityLimit(rules, affixQualityTable, pair.Key, Math.Max(configuredCount, pair.Value));
        }
        // 基础词条（档位 1）是装备自带的固定行，原生只在装备初始化时写一次（:2219170-2219197）。
        // 预览不一定带出来，这里按模板补一条：生成器与游戏面板因此都是「1 基础 + 精良 + 稀有」，
        // 提交时它也会被写回存档（EquipmentFixed 保证客户端不会把它删掉）。
        if (effectiveBaseAffixId > 0 && affixTable.ContainsKey(effectiveBaseAffixId) &&
            !rules.GeneratedAffixes.Exists(a => a.Quality == (int)EItemQualityType.common &&
                                                 a.Id == effectiveBaseAffixId))
        {
            var baseQualityName = affixQualityTable.ContainsKey((int)EItemQualityType.common)
                ? affixQualityTable[(int)EItemQualityType.common].name
                : "普通";
            rules.AffixQualityNames[(int)EItemQualityType.common] = baseQualityName;
            rules.GeneratedAffixes.Add(new AffixEdit
            {
                Id = effectiveBaseAffixId,
                Quality = (int)EItemQualityType.common,
                QualityName = baseQualityName,
                Name = GetAffixName(affixTable[effectiveBaseAffixId]),
                Level = rules.MaximumAffixLevel,
                Value = null,
                // 装备自带：客户端不允许删除，数值仍可编辑。
                EquipmentFixed = true
            });
            Plugin.Verbose($"[BaseAffix] 规则中补入档位 1 基础词条 id={effectiveBaseAffixId}");
        }
        // 传奇/神话专属词条由具体装备的 legendAffixArr 声明。但只有**会落存档**的行才占额度：
        //   · 品级 5：AddMythAffix / ItemEquipData 只把 TAffix.specialType == 1 的行写进 affixList
        //     （GameAssembly.c:2215548-2215576、:1623369-1623377）→ 只数这些；
        //   · 品级 4：档位 4 的专属行完全由 ItemEquipData 运行时现算（:1623344-1623349），不落存档 → 不占额度。
        var equipmentSpecialAffixes = BuildEquipmentSpecialAffixMap(effectiveTemplate, edit.Quality);
        if (equipmentSpecialAffixes.Count > 0 && edit.Quality == (int)EItemQualityType.myth)
        {
            var storedSpecialCount = 0;
            foreach (var pair in equipmentSpecialAffixes)
            {
                var specialAffix = affixTable.ContainsKey(pair.Key.Id) ? affixTable[pair.Key.Id] : null;
                if (specialAffix != null && specialAffix.specialType == 1)
                    storedSpecialCount++;
            }
            if (storedSpecialCount > 0)
            {
                rules.AffixQualityLimits.TryGetValue(edit.Quality, out var configuredSpecialCount);
                AddAffixQualityLimit(rules, affixQualityTable, edit.Quality,
                    Math.Max(configuredSpecialCount, storedSpecialCount));
                // 神话专属是固定条数（模板里 specialType==1 的行）→ 下限 = 上限。
                SetAffixQualityMinimum(rules, edit.Quality, storedSpecialCount);
            }
        }
        // 圣碑腐蚀每成功一次就往存档 affixList 追加 1 条档位3词条（ItemEquipData.TryAddCorruptRareAffix，
        // GameAssembly.c（1.1.4）:1618301），不检查 legendAffixCount —— 所以被腐蚀过的装备可能超过下面的生成上限。
        // 但**上限本身必须是原生生成上限**：把可腐蚀次数加进额度会让普通/精良也显示"稀有 3 条"、
        // 传说显示 7 条（实测用户反馈）。腐蚀多出来的那部分留给「越界」勾选处理。
        var corruptAllowance = GetCorruptAllowance();
        rules.MaximumCorruptCount = corruptAllowance;
        foreach (var limit in rules.AffixQualityLimits.Values)
            rules.MaximumAffixCount += limit;
        // 只按实际数量大于 0 的词条档位读取原生池；稀有专属装备不会混入普通词条。
        var poolByKey = BuildAffixPoolMap(effectiveTemplate, edit.Level, rules.AffixQualityLimits.Keys);
        // 额度还要受「池子实际条数」约束：原生抽词条时会做同样的裁剪
        // （AddRareAffix：`if (v18 >= pool.count) v18 = pool.count`；AddFineAffix 同理）。
        ClampAffixLimitsByPool(rules, poolByKey);
        var allowedAffixKeys = new HashSet<(int Id, int Quality)>();
        foreach (var pair in poolByKey)
        {
            if (!affixTable.ContainsKey(pair.Key.Id))
                continue;
            var affix = affixTable[pair.Key.Id];
            var qualityName = affixQualityTable.ContainsKey(pair.Key.Quality)
                ? affixQualityTable[pair.Key.Quality].name
                : $"词条档位 {pair.Key.Quality}";
            rules.AffixQualityNames[pair.Key.Quality] = qualityName;
            rules.AllowedAffixes.Add(new AffixOption
            {
                Id = affix.id,
                // 同一基础词条可由不同档位的原生池生成，类别必须使用查询池的档位。
                Quality = pair.Key.Quality,
                QualityName = qualityName,
                Name = GetAffixName(affix),
            });
            allowedAffixKeys.Add(pair.Key);
        }
        // 品级 3/6/8 的稀有条数原生是随机的（RandomRangeInt(2, rareAffixCount + 1)，:2215740）。
        // 按「统一取最高」的要求：预览只滚到下限时，把精良/稀有补到额度上限，默认就给满。
        TopUpGeneratedAffixes(rules, poolByKey, affixTable, affixQualityTable, corruptAllowance);
        foreach (var pair in equipmentSpecialAffixes)
        {
            if (!affixTable.ContainsKey(pair.Key.Id))
                continue;
            var affix = affixTable[pair.Key.Id];
            var qualityName = affixQualityTable.ContainsKey(pair.Key.Quality)
                ? affixQualityTable[pair.Key.Quality].name
                : $"词条档位 {pair.Key.Quality}";
            rules.AffixQualityNames[pair.Key.Quality] = qualityName;
            if (allowedAffixKeys.Add(pair.Key))
            {
                rules.AllowedAffixes.Add(new AffixOption
                {
                    Id = affix.id,
                    Quality = pair.Key.Quality,
                    QualityName = qualityName,
                    Name = GetAffixName(affix),
                });
            }
            // CreateEquip 尚未构造运行时 ItemEquipData，因此可能还没有执行 AppendSpecialAffix。
            // 专属词条是模板固定内容，直接补进默认编辑行，最终提交仍走原生 SaveAffixData.Create。
            var alreadyGenerated = false;
            foreach (var generated in rules.GeneratedAffixes)
                if (generated.Id == pair.Key.Id && generated.Quality == pair.Key.Quality)
                {
                    alreadyGenerated = true;
                    break;
                }
            if (!alreadyGenerated)
                rules.GeneratedAffixes.Add(new AffixEdit
                {
                    Id = affix.id,
                    Quality = pair.Key.Quality,
                    QualityName = qualityName,
                    Name = GetAffixName(affix),
                    Level = rules.MaximumAffixLevel,
                    Value = null,
                    // 传说/神话专属词条属于具体装备（legendAffixArr），客户端不允许增删，数值仍可编辑。
                    EquipmentFixed = true
                });
        }
        // 需求（方案 A）：装备自带的专属词条只作为「固定行」下发，不进候选列表。
        //   · 档位 8（独有技能）每件装备恒 1 条，由 TEquip.uniqueSkillAffix 决定；
        //   · 传说(4)/神话(5) 的 legendAffixArr 行属于具体装备，不是通用随机池的一部分。
        // 这样它们的行仍会显示、数值仍可编辑，但不会被添加到别的装备上，也不会被当作可删除的普通词条。
        // 档位 1/2/3 等通用池词条保持原样（可增删）。
        foreach (var generated in rules.GeneratedAffixes)
        {
            if (generated.Quality == 8)
                continue;
            if (equipmentSpecialAffixes.ContainsKey((generated.Id, generated.Quality)))
                continue;
            if (!allowedAffixKeys.Add((generated.Id, generated.Quality)))
                continue;
            rules.AllowedAffixes.Add(new AffixOption
            {
                Id = generated.Id,
                Quality = generated.Quality,
                QualityName = generated.QualityName,
                Name = generated.Name
            });
        }
        // 参考值与取值分类：候选行按「新增时的默认等级」算，生成行按自身等级算。
        // 全部走原生方法取值（见 ComputeAffixReference 注释）。
        // 「污染」是独立的候选池分组：原生腐蚀就是从档位3池里挑未用过的 id
        // （ItemEquipData.TryAddCorruptRareAffix，GameAssembly.c（1.1.4）:1618301），所以这里直接标记该档位候选。
        rules.CorruptGroupName = "污染";
        foreach (var option in rules.AllowedAffixes)
            option.IsCorruptPool = option.Quality == (int)EItemQualityType.rare;
        // 开孔：候选来自该品级的 TEquipQuality.slotCountArr（锻造另有 forgeSlotCountArr），
        // 等级门槛来自 TConstant.equipSlotLevel（SaveItemData.EquipRandomSlot 的入口判定）。
        rules.SlotCountCandidates = GetSlotCountCandidates(edit.Quality, false);
        rules.ForgeSlotCountCandidates = GetSlotCountCandidates(edit.Quality, true);
        rules.MaximumSlotCount = MaxOf(rules.SlotCountCandidates);
        rules.IsWeaponSlotLayout = effectiveTemplate != null && effectiveTemplate.part <= 3;
        try
        {
            rules.SlotLevelRequirement = TConstant.equipSlotLevel;
        }
        catch
        {
            rules.SlotLevelRequirement = 0;
        }
        var rateByKey = new Dictionary<(int Id, int Quality), float>();
        foreach (var pair in poolByKey)
            rateByKey[pair.Key] = pair.Value.Rate;
        foreach (var pair in equipmentSpecialAffixes)
            rateByKey[pair.Key] = pair.Value;
        // 基础词条不在部位随机池里：倍率取 TEquip.baseAffix[1]，与原生创建时的 partRate 一致（:2219193）。
        if (effectiveBaseAffixId > 0)
            rateByKey[(effectiveBaseAffixId, (int)EItemQualityType.common)] = GetBaseAffixRate(effectiveTemplate);
        foreach (var option in rules.AllowedAffixes)
        {
            if (!affixTable.ContainsKey(option.Id) || affixTable[option.Id] == null)
                continue;
            rateByKey.TryGetValue((option.Id, option.Quality), out var rate);
            if (rate <= 0f) rate = 1f;
            ApplyReference(option, ComputeAffixReference(affixTable[option.Id], option.Id, option.Quality,
                rate, edit.Quality, edit.MainAttrGrade, rules.MaximumAffixLevel, rules.MaximumAffixLevel));
        }
        foreach (var generated in rules.GeneratedAffixes)
        {
            if (!affixTable.ContainsKey(generated.Id) || affixTable[generated.Id] == null)
                continue;
            rateByKey.TryGetValue((generated.Id, generated.Quality), out var rate);
            if (rate <= 0f) rate = 1f;
            ApplyReference(generated, ComputeAffixReference(affixTable[generated.Id], generated.Id, generated.Quality,
                rate, edit.Quality, edit.MainAttrGrade, rules.MaximumAffixLevel, generated.Level));
            // 数值默认留空：未勾选强制越界时不写入存档，由游戏按自身规则计算（需求 2.4.1）。
            generated.Value = null;
        }
        // 方案 A：装备自带的专属词条作为「固定行」直接进入 GeneratedAffixes，
        // 客户端因此总能显示它们（不再因为不在候选池里而被丢掉），同时仍可编辑数值。
        // 档位 8（独有技能词条）每件装备只有 1 条，由 TEquip.uniqueSkillAffix 决定，等级恒为 1（:1623232）。
        // 注意用 effectiveTemplate：模板 ID 解析失败时 template 可能为 null（快照会遍历背包内全部物品），
        // 而 effectiveTemplate 由存档解析出来、始终有效。
        if (edit.Quality == 8 && effectiveTemplate != null && effectiveTemplate.uniqueSkillAffix > 0 &&
            !rules.GeneratedAffixes.Exists(a => a.Id == effectiveTemplate.uniqueSkillAffix && a.Quality == 8))
        {
            var uniqueId = effectiveTemplate.uniqueSkillAffix;
            var uniqueQualityName = affixQualityTable.ContainsKey(8)
                ? affixQualityTable[8].name
                : "独有技能";
            rules.AffixQualityNames[8] = uniqueQualityName;
            rules.GeneratedAffixes.Add(new AffixEdit
            {
                Id = uniqueId,
                Quality = 8,
                QualityName = uniqueQualityName,
                Name = affixTable.ContainsKey(uniqueId) ? GetAffixName(affixTable[uniqueId]) : $"词条 {uniqueId}",
                Level = 1,
                LevelMinimum = 1,
                LevelMaximum = 1,
                Value = null,
                // 装备自带（品级强制）：客户端不允许增删，数值仍可编辑。
                EquipmentFixed = true
            });
            Plugin.Verbose($"[UniqueAffix] 规则中补入档位 8 固定行 id={uniqueId}");
        }
        rules.AllowedAffixes.Sort((a, b) => a.Id.CompareTo(b.Id));
        return rules;
    }
    // 「affixReference」动作：词条等级 / 装备质量 / 装备等级变化后，只刷新参考值（需求 2.2）。
    // 只做「查倍率 + 算参考值」，不重建候选池，避免每次改等级都跑一遍完整的 GetEquipmentRules。
    internal static List<AffixEdit> GetAffixReferences(EquipmentEdit edit)
    {
        var template = RequireEquipmentRequest(edit);
        var levelData = TEquipLevel.create()[edit.Level];
        var maximumAffixLevel = Math.Max(1, levelData.affixMaxLevel);
        // 倍率来源与 GetEquipmentRules 保持一致：通用池按档位查询，专属词条取 legendAffixArr 的倍率。
        var rateByKey = new Dictionary<(int Id, int Quality), float>();
        var equipmentQuality = TEquipQuality.create()[edit.Quality];
        var poolQualities = new List<int>();
        if (equipmentQuality != null)
        {
            if (equipmentQuality.fineAffixCount > 0) poolQualities.Add((int)EItemQualityType.fine);
            if (equipmentQuality.rareAffixCount > 0) poolQualities.Add((int)EItemQualityType.rare);
        }
        foreach (var quality in poolQualities)
        {
            var pool = EquipSys.GetEquipAffixPool(template.id, template.part, quality, edit.Level);
            for (var i = 0; pool != null && i < pool.Count; i++)
                rateByKey[(pool[i].id, quality)] = pool[i].Rate;
        }
        foreach (var pair in BuildEquipmentSpecialAffixMap(template, edit.Quality))
            rateByKey[pair.Key] = pair.Value;
        // 基础词条（档位 1）不在部位随机池里：倍率取 TEquip.baseAffix[1]（与原生创建时的 partRate 一致，:2219193）。
        var templateBaseAffixId = GetBaseAffixId(template);
        if (templateBaseAffixId > 0)
            rateByKey[(templateBaseAffixId, (int)EItemQualityType.common)] = GetBaseAffixRate(template);
        var affixTable = TAffix.create();
        var result = new List<AffixEdit>();
        foreach (var requested in edit.Affixes)
        {
            // 镶嵌孔位行不是词条（Id = 符文 id，没镶嵌为 0），不参与词条参考值计算。
            if (requested.ValueKind == AffixValueKind.SlotType)
                continue;
            if (!affixTable.ContainsKey(requested.Id) || affixTable[requested.Id] == null)
                continue;
            rateByKey.TryGetValue((requested.Id, requested.Quality), out var rate);
            if (rate <= 0f) rate = 1f;
            var item = new AffixEdit
            {
                Id = requested.Id,
                Quality = requested.Quality,
                QualityName = requested.QualityName,
                Name = requested.Name
            };
            ApplyReference(item, ComputeAffixReference(affixTable[requested.Id], requested.Id, requested.Quality,
                rate, edit.Quality, edit.MainAttrGrade, maximumAffixLevel, requested.Level));
            result.Add(item);
        }
        return result;
    }
    internal static EquipmentInventorySnapshot GetEquipmentInventorySnapshot() => GetEquipmentInventorySnapshot(GetLord());
    private static string BuildEquipmentFingerprint(SaveItemData save)
    {
        // 用内容而不是槽位来识别装备：玩家在游戏内整理过背包后槽位会变，但内容不变。
        var parts = new List<string>
            {
                save.id.ToString(),
                save.quality.ToString(),
                save.level.ToString(),
                save.forgeLevel.ToString()
            };
        if (save.affixList != null)
        {
            parts.Add(save.affixList.Count.ToString());
            for (var i = 0; i < save.affixList.Count; i++)
            {
                var affix = save.affixList[i];
                parts.Add(affix == null ? "null" : $"{affix.id}:{affix.quality}:{affix.level}:{affix.value}");
            }
        }
        return string.Join("|", parts);
    }
    // ============================================================
    // 需求 8：资源不在背包里
    //
    // 原生模型（GameAssembly.c:2183904-2183941、:2182238-2182259、:1452632-1452646）：
    //   · SaveTownData.resDict  : Dictionary<int, SaveResData>      存档侧，键 = 资源 ID；
    //   · TownData.resDic       : Dictionary<EResType, ResData>     运行时视图，键 = 同一个资源 ID
    //                             （加载时由 resDict 逐条 ResData.Create 建出来）；
    //   · ResData 内部持有 tResData 与同一个 saveResData 引用 → 改 saveResData.count 两边同步；
    //   · UI 刷新：EventMgr.sendEvent(53, resData)；容量上限：TownData.GetResMax(id)。
    //
    // 所以资源的读取/写入都走 resDict，不能用背包字段列表。
    // 协议里用 Container = 2 表示「资源槽」，FieldIndex 借用为资源 ID。
    // ============================================================
    private const int ResourceContainer = 2;
    private static Il2CppSystem.Collections.Generic.Dictionary<int, SaveResData>? TryGetResourceStore()
        => Game.dataMgr?.nowSeasonData?.townData?.saveTownData?.resDict;
    private static Il2CppSystem.Collections.Generic.Dictionary<int, SaveResData> GetResourceStore()
        => TryGetResourceStore() ?? throw new InvalidOperationException("当前存档没有资源容器。");
    private static int GetResourceCount(int resId)
    {
        var store = GetResourceStore();
        return store.ContainsKey(resId) && store[resId] != null ? store[resId].count : 0;
    }
    // 按原版写入资源数量：走 TownData.AddRes
    // （内部 ResData.AddRes → 按 TownData.GetResMax 夹紧、把存档标脏，再 sendEvent(53, resData) 刷新 UI）。
    // 对应 GameAssembly.c:2182215-2182260；夹紧细节见 ResData.AddRes :1921700-1921751。
    //
    // 注意 AddRes 是经 resDic[type] 取 ResData 的，条目缺失会空引用（:2182240-2182257），
    // 所以先按加载逻辑（:2183931）补齐存档侧与运行时两侧的条目。
    private static int SetResourceCount(int resId, int target)
    {
        var town = Game.dataMgr?.nowSeasonData?.townData
            ?? throw new InvalidOperationException("请先启动游戏并进入一个游戏存档。");
        var store = town.saveTownData?.resDict
            ?? throw new InvalidOperationException("当前存档没有资源容器。");
        var value = Math.Max(0, target);
        if (!store.ContainsKey(resId) || store[resId] == null)
            store[resId] = SaveResData.Create(resId, 0)
                ?? throw new InvalidOperationException($"游戏拒绝创建资源 {resId}。");
        var saveResData = store[resId];
        // 运行时视图：resDic 只包含「加载时已存在」的条目，新资源要补进去 AddRes 才不会空引用。
        // ContainsKey / Add 都放在 try 里：枚举键的字典在互操作下语义可能有差异，
        // 若键其实已存在，Add 会抛「已添加了具有相同键的项」——那种情况本来就不用补，忽略即可。
        var key = (EResType)resId;
        var runtimeDic = town.resDic;
        var hasRuntimeEntry = false;
        if (runtimeDic != null)
        {
            try
            {
                hasRuntimeEntry = runtimeDic.ContainsKey(key);
                if (!hasRuntimeEntry)
                {
                    var created = ResData.Create(saveResData);
                    if (created != null)
                    {
                        runtimeDic.Add(key, created);
                        hasRuntimeEntry = true;
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning($"[ResourceWrite] 运行时条目检查异常 id={resId}：{exception.Message}");
                hasRuntimeEntry = true;
            }
        }
        var before = saveResData.count;
        Plugin.Log.LogInfo($"[ResourceWrite] id={resId} target={value} before={before} " +
                           $"resDic={(runtimeDic != null)} hasRuntimeEntry={hasRuntimeEntry}");
        // 原版入口：传增量以达到目标值。GetResMax 为 0（未定义的类型）时原版视为无上限，不夹紧。
        town.AddRes(key, value - before);
        Plugin.Log.LogInfo($"[ResourceWrite] id={resId} after={saveResData.count}");
        return saveResData.count;
    }
    private static EquipmentInventorySnapshot GetEquipmentInventorySnapshot(LordData lord)
    {
        var result = new EquipmentInventorySnapshot();
        EquipmentLocators.Clear();
        var fields = lord.lordBagData.GetFieldList(EItemType.equip);
        var templateTable = TEquip.create();
        var qualityTable = TEquipQuality.create();
        var affixTable = TAffix.create();
        var affixQualityTable = TAffixQuality.create();
        var equipLevelTable = TEquipLevel.create();
        for (var i = 0; fields != null && i < fields.Count; i++)
        {
            var field = fields[i];
            var itemData = field?.itemData;
            var fieldSave = field?.saveItemFieldData;
            var save = itemData?.saveItemData;
            if (itemData == null || fieldSave == null || save == null ||
                save.type != EItemType.equip || save.count <= 0)
                continue;
            var guid = System.Guid.NewGuid().ToString("N");
            EquipmentLocators[guid] = new EquipmentLocator
            {
                FieldIndex = fieldSave.index,
                TemplateId = save.id,
                Quality = save.quality,
                Level = save.level,
                ForgeLevel = save.forgeLevel,
                Fingerprint = BuildEquipmentFingerprint(save)
            };
            var entry = new EquipmentInventoryEntry
            {
                Guid = guid,
                FieldIndex = fieldSave.index,
                TemplateId = save.id,
                Name = itemData.GetName()
                    ?? (templateTable.ContainsKey(save.id) ? templateTable[save.id].name : $"装备 {save.id}"),
                Quality = save.quality,
                QualityName = qualityTable.ContainsKey(save.quality)
                    ? qualityTable[save.quality].name
                    : $"品级 {save.quality}",
                Level = save.level,
                ForgeLevel = save.forgeLevel,
                MainAttrGrade = save.mainAttrGrade,
                // 旧版（1.1.0 重做前）污染残留：原生用 TEquipCorrupt.des 渲染，只能用【涤魂泪滴】清除。
                LegacyCorruptText = ReadLegacyCorruptText(save),
                // 开孔：已开孔数 + 该品级上限（孔位明细随后以「镶嵌组」的词条行追加）。
                SlotCount = save.slotCount,
                MaximumSlotCount = MaxOf(GetSlotCountCandidates(save.quality, false))
            };
            // 档位 8 是「品级强制词条」：原生不把它保存在 affixList 里，而是在运行时按模板现加
            // （:2148324 case 8 → qualityAffixList），所以这里需要单独判断、单独补下发。
            var hasQuality8Affix = false;
            // 已存在的词条键，用于判断哪些「装备自带」词条需要合成下发。
            var readAffixKeys = new HashSet<(int Id, int Quality)>();
            // 该装备的传说/神话专属词条集合（legendAffixArr）：这些词条属于装备本身，不允许客户端增删。
            var readSpecialAffixes = templateTable.ContainsKey(save.id)
                ? BuildEquipmentSpecialAffixMap(templateTable[save.id], save.quality)
                : new Dictionary<(int Id, int Quality), float>();
            // 基础词条（档位 1）也属于「装备自带」：原生在装备初始化时就把它写进 affixList
            // （GameAssembly.c:2219170-2219197），客户端删掉它会让写回的装备永久缺少基础词条。
            var readBaseAffixId = templateTable.ContainsKey(save.id) ? GetBaseAffixId(templateTable[save.id]) : 0;
            if (save.affixList != null)
            {
                for (var j = 0; j < save.affixList.Count; j++)
                {
                    var affix = save.affixList[j];
                    if (affix == null)
                        continue;
                    if (affix.quality == 8)
                        hasQuality8Affix = true;
                    readAffixKeys.Add((affix.id, affix.quality));
                    var affixQualityName = affixQualityTable.ContainsKey(affix.quality)
                        ? affixQualityTable[affix.quality].name
                        : $"词条档位 {affix.quality}";
                    var editable = new AffixEdit
                    {
                        Id = affix.id,
                        Quality = affix.quality,
                        QualityName = affixQualityName,
                        Name = affixTable.ContainsKey(affix.id)
                            ? GetAffixName(affixTable[affix.id])
                            : $"词条 {affix.id}",
                        // 需求 2.4.1：未勾选强制越界时显示游戏解析出来的值，而不是存档里的缓存值。
                        ResolvedValue = save.GetResolvedEquipAffixValue(affix),
                        // 需求 2.4.2：勾选后显示/编辑存档里保存的值。
                        StoredValue = affix.value,
                        // 需求 2.3：存档里带哨兵就说明此前勾选过强制越界。
                        ForceOverflow = SaveAffixValuePatch.HasManualValue(affix),
                        // 装备自带（档位 8 / 传说神话专属 / 基础词条）：客户端不允许删除，数值仍可编辑。
                        EquipmentFixed = affix.quality == 8 ||
                                         (affix.quality == 1 && affix.id == readBaseAffixId) ||
                                         readSpecialAffixes.ContainsKey((affix.id, affix.quality)),
                        // 污染标记必须原样带回：写回时如果丢了，装备面板不再显示污染、
                        // 玩家也无法再用【涤魂泪滴】净化（原生 SaveAffixData.isCorrupt / isCorruptLocked）。
                        IsCorrupt = affix.isCorrupt,
                        IsCorruptLocked = affix.isCorruptLocked
                    };
                    // 参考值与取值分类：装备实例按它自己的档位算。
                    var affixRow = affixTable.ContainsKey(affix.id) ? affixTable[affix.id] : null;
                    if (affixRow != null)
                    {
                        var affixMaxLevel = equipLevelTable.ContainsKey(save.level)
                            ? Math.Max(1, equipLevelTable[save.level].affixMaxLevel)
                            : 1;
                        var reference = ComputeAffixReference(affixRow, affix.id, affix.quality,
                            GetEffectiveAffixRate(GetAffixPoolRate(save, affix), save.quality, affix.quality),
                            save.quality, save.mainAttrGrade, affixMaxLevel, affix.level);
                        ApplyReference(editable, reference);
                        // 装备实例上等级由游戏决定：档位相关的保留存档等级，其余标记为不适用。
                        if (reference.Kind == AffixValueKind.GradeBased)
                            editable.Level = affix.level;
                    }
                    else
                    {
                        // 读不到词条定义时退化为「等级不适用」，避免界面显示错误的可编辑等级。
                        editable.ValueKind = AffixValueKind.Fixed;
                        editable.Level = -1;
                        editable.ResolvedValue = save.GetResolvedEquipAffixValue(affix);
                        editable.FixedValue = editable.ResolvedValue;
                    }
                    entry.Affixes.Add(editable);
                }
            }
            // 方案 A（读回侧）：装备自带的品级强制词条不在 affixList 里，必须在这里合成下发，
            // 否则编辑器永远读不到独有技能词条（游戏内是有的，因为原生运行时自己会加）。
            // 数值保持可编辑：ValueKind 与参考值按 ClassifyAffix / NativeCreateValue 的规则给。
            if (save.quality == 8 && !hasQuality8Affix && templateTable.ContainsKey(save.id))
            {
                var uniqueId = templateTable[save.id].uniqueSkillAffix;
                if (uniqueId > 0 && affixTable.ContainsKey(uniqueId))
                {
                    var uniqueQualityName = affixQualityTable.ContainsKey(8)
                        ? affixQualityTable[8].name
                        : "独有技能";
                    entry.Affixes.Add(new AffixEdit
                    {
                        Id = uniqueId,
                        Quality = 8,
                        QualityName = uniqueQualityName,
                        Name = GetAffixName(affixTable[uniqueId]),
                        // 原生钉死：档位 8 等级恒为 1、rate 恒为 1.0（:1623232）。
                        Level = 1,
                        LevelMinimum = 1,
                        LevelMaximum = 1,
                        ValueKind = AffixValueKind.RandomRange,
                        ResolvedValue = NativeCreateValue(uniqueId, 8, 1, 1f, ValueTypeRandom),
                        FixedValue = NativeCreateValue(uniqueId, 8, 1, 1f, ValueTypeRandom),
                        StoredValue = 0,
                        // 装备自带：客户端不允许删除，数值仍可编辑（需求：方案 A）。
                        EquipmentFixed = true
                    });
                    Plugin.Verbose($"[EquipmentReadBack] 补入品级强制词条（档位 8）id={uniqueId}");
                }
            }
            // 传说(4) 的 legendAffixArr 专属词条与档位 8 同理：由原生在运行时按模板加入，**不保存在 affixList 里**
            // （实测：写入 17 条（含 11 条档位 4），读回只剩 6 条）。神话(5) 因为走 AddMythAffix 会持久化，所以不受影响。
            // 这里按模板补下发缺失的档位 4 行，保证编辑器能读到「传说装备的专属词条」。
            foreach (var pair in readSpecialAffixes)
            {
                if (pair.Key.Quality != 4 || readAffixKeys.Contains(pair.Key) || !affixTable.ContainsKey(pair.Key.Id))
                    continue;
                var legendLevel = equipLevelTable.ContainsKey(save.level)
                    ? Math.Max(1, equipLevelTable[save.level].affixMaxLevel)
                    : 1;
                var legendQualityName = affixQualityTable.ContainsKey(4) ? affixQualityTable[4].name : "传说";
                var legendValue = NativeCreateValue(pair.Key.Id, 4, legendLevel, pair.Value, ValueTypeLow);
                entry.Affixes.Add(new AffixEdit
                {
                    Id = pair.Key.Id,
                    Quality = 4,
                    QualityName = legendQualityName,
                    Name = GetAffixName(affixTable[pair.Key.Id]),
                    // 传说专属词条的等级与区间都由装备决定（原生死值），装备自带、不可增删。
                    Level = legendLevel,
                    LevelMinimum = legendLevel,
                    LevelMaximum = legendLevel,
                    ValueKind = AffixValueKind.Fixed,
                    FixedValue = legendValue,
                    ResolvedValue = legendValue,
                    StoredValue = 0,
                    EquipmentFixed = true
                });
                readAffixKeys.Add(pair.Key);
                Plugin.Verbose($"[EquipmentReadBack] 补入传说专属词条（档位 4）id={pair.Key.Id}");
            }
            // 镶嵌孔位：以「镶嵌组」的词条行追加（组名带上限，x 来自该品级的 slotCountArr）。
            if (entry.SlotCount > 0)
            {
                foreach (var socketRow in BuildSocketAffixRows(save, $"镶嵌·最多{entry.MaximumSlotCount}条"))
                    entry.Affixes.Add(socketRow);
            }
            // 诊断（需开启详细日志）：读回时实际下发了哪些词条。
            // 用于区分「桥接没发」与「客户端把它丢了」——尤其是档位 8 的独有技能词条。
            if (Plugin.VerboseEnabled)
            {
                var readIds = new List<string>();
                foreach (var readAffix in entry.Affixes)
                    readIds.Add($"{readAffix.Id}/档位{readAffix.Quality}");
                Plugin.Log.LogInfo($"[EquipmentReadBack] 装备 {save.id}(品级{save.quality}) " +
                                   $"词条({entry.Affixes.Count})：[{string.Join(", ", readIds)}]");
            }
            result.Entries.Add(entry);
        }
        // 需求 5.1：装备编辑器按背包 slot 排序（FieldIndex 是稀疏槽位号，正是玩家看到的位置）。
        result.Entries.Sort((a, b) => a.FieldIndex.CompareTo(b.FieldIndex));
        return result;
    }
    // ============================================================
    // 取某条存档词条在原生池里的倍率；查不到时回退 1。
    private static float GetAffixPoolRate(SaveItemData save, SaveAffixData affix)
    {
        var template = TEquip.create();
        var equip = template.ContainsKey(save.id) ? template[save.id] : null;
        if (equip == null)
            return 1f;
        var pool = EquipSys.GetEquipAffixPool(equip.id, equip.part, affix.quality, save.level);
        for (var i = 0; pool != null && i < pool.Count; i++)
            if (pool[i].id == affix.id && pool[i].Rate > 0f)
                return pool[i].Rate;
        // 专属词条（legendAffixArr）不在通用池里。
        foreach (var pair in BuildEquipmentSpecialAffixMap(equip, save.quality))
            if (pair.Key.Id == affix.id && pair.Key.Quality == affix.quality)
                return pair.Value;
        return 1f;
    }
    // ============================================================
    // 诊断探针：各档位词条的 valueType（只在模组设置里开启「输出详细日志」时输出）。
    //
    // 依据反编译确认的档位 → valueType 映射：
    //   · 普通/精良/稀有(1/2/3)：走通用池 GetEquipAffixPool，数值由档位区间计算，不用 legendAffixArr；
    //   · 传说(4)：AppendSpecialAffixesFromTable → SaveAffixData.Create(affix, 4, 1, 1.0, valueType 0)；
    //   · 神话(5)：普通行同上（valueType 0）；specialType==1 的行是 valueType 3（有符号特殊随机）；
    //   · 独特(8)：Create(tEquip.uniqueSkillAffix, 8, 1, 1.0, valueType 1) —— 每件装备只有 1 条。
    // 实测（1.1.3）：11503 银锤-斯丁的 11 条神话专属为 specialType==1，区间 -5~10。
    // ============================================================
    internal static string ProbeAffixValueTypes()
    {
        var equipTable = TEquip.create();
        var affixTable = TAffix.create();
        var mythSpecial = new SortedSet<int>();
        // 随词条等级变化的专属行（可直接用来验证数值是否随等级变）。按编号族统计：
        // 18xxx 是传说族（传说装备的专属行），19xxx 是神话族。
        var variableLegend = new List<string>();
        var variableLegend18 = 0;
        var variableLegend19 = 0;
        var uniqueLines = new List<string>();
        var legendRows = 0;
        var mythRows = 0;
        foreach (var pair in equipTable)
        {
            var equip = pair.Value;
            if (equip == null)
                continue;
            // 档位 8：独有技能词条（单条，字段就是词条 ID）。
            var uniqueId = equip.uniqueSkillAffix;
            if (uniqueId > 0)
            {
                var known = affixTable.ContainsKey(uniqueId) ? affixTable[uniqueId] : null;
                uniqueLines.Add($"{pair.Key}({equip.name}) uniqueSkillAffix={uniqueId}" +
                                (known != null ? $"({GetAffixName(known)}) effectType={known.effectType} specialType={known.specialType}" : "") +
                                " 档位8->valueType 1(区间随机)");
            }
            var rawAffixArr = equip.legendAffixArr;
            if (rawAffixArr == null)
                continue;
            var affixArr = rawAffixArr.Cast<Il2CppSystem.Array>();
            for (var row = 0; row < affixArr.GetLength(0); row++)
            {
                var affixId = affixArr.GetValue(row, 0).Unbox<int>();
                if (affixId <= 0 || !affixTable.ContainsKey(affixId))
                    continue;
                var affix = affixTable[affixId];
                // legendAffixArr 同时供传说(4)与神话(5)使用，同一行在两种档位下 valueType 不同。
                legendRows++;
                mythRows++;
                if (affix.specialType == 1)
                    mythSpecial.Add(affixId);
                // 传说专属行里哪些**随词条等级变化**？用 vt2(固定取下端) 在 L=1/5/9 各算一次。
                // v23 = L²lr0 + L·lr1 + lr2 —— 系数为 0 的词条（如 18241）三值相同，
                // 系数非 0 的（如神话 19211）三值递增，这类就是可用来验证"传说是否随等级变"的样本。
                var rowRate = affixArr.GetValue(row, 1).Unbox<int>() / 100f;
                if (rowRate <= 0f)
                    rowRate = 1f;
                var atL1 = NativeCreateValue(affixId, 4, 1, rowRate, ValueTypeLow);
                var atL5 = NativeCreateValue(affixId, 4, 5, rowRate, ValueTypeLow);
                var atL9 = NativeCreateValue(affixId, 4, 9, rowRate, ValueTypeLow);
                if (atL1 != atL5 || atL5 != atL9)
                {
                    // 按「装备版本」分组，而不是词条编号：编号前缀 18xxx/19xxx 不是传说/神话的可靠代理
                    // （实测 18512 出现在神话版 20507、18842 出现在神话版 61503）。
                    // 装备编号百位 4 = 传说版、5 = 神话版（11401↔11501、20407↔20507、61403↔61503）。
                    if ((pair.Key / 100) % 10 == 5)
                        variableLegend19++;
                    else
                        variableLegend18++;
                    variableLegend.Add($"{affixId}({GetAffixName(affix)}) @{pair.Key}({equip.name}) L1/L5/L9={atL1}/{atL5}/{atL9} rate={rowRate:0.##}");
                }
            }
        }
        var summary = new List<string>
        {
            // 实测枚举数值：确认 ValueTypeLow(min) 到底是 2(固定取下端) 还是 0(区间随机)。
            $"EAffixValueType 实测：min={(int)ValueTypeLow} random={(int)ValueTypeRandom} " +
            $"specialRandom={(int)ValueTypeSpecialRandom}",
            // 取值探针（见方法末尾）已定论：vt2 恒定 = 游戏内实际值，vt1 会波动 → 真实路径是 vt2。
            $"传说(4) 专属词条行 {legendRows} 条 → valueType 2(固定取下端，取值探针已证 vt1 会波动而游戏恒为 vt2)",
            $"神话(5) 专属词条行 {mythRows} 条 → 普通行 valueType 2(固定取下端)；specialType==1 的行 valueType 3(有符号随机)",
            $"神话 specialType==1 词条 {mythSpecial.Count} 个：{string.Join(",", mythSpecial)}",
            $"legendAffixArr 中随词条等级变化的行 {variableLegend.Count} 条（按档位 4 计算；" +
            $"挂在传说装备上 {variableLegend18} 条 / 挂在神话装备上 {variableLegend19} 条）：" +
            string.Join("；", variableLegend),
            $"独特技能词条(档位 8) {uniqueLines.Count} 件装备：valueType 1(区间随机)"
        };
        // 取值方式探针：对固定的 (词条,档位,等级) 样本，分别用各 valueType 各取 3 次。
        // 判定规则（用来确定传说(4)/神话普通行到底是固定取下端还是区间随机）：
        //   · random(1) 出现波动、而游戏内实测恒为 2  → 真实路径是 min(2)（固定取下端）；
        //   · random(1) 恒等于 min(2)，且 max(4) 也相同 → 区间宽度为 0（退化，等价固定）；
        //   · random(1) 恒等于 min(2)，但 max(4) 不同 → 区间非退化，需要结合游戏内值再判断。
        // 样本等级用固定值（求解的是 valueType 行为，与装备等级无关）。
        var valueTypeSamples = new (int Id, int Quality, int Level)[]
        {
            (18491, 4, 6),   // 传说专属（银锤-斯丁 11403）
            (19211, 5, 6),   // 神话普通行（巨骷髅 11501）
            (19181, 5, 6),   // 神话专属 specialType==1
            (17211, 8, 1)    // 独特技能（档位 8 恒 1 级）
        };
        foreach (var sample in valueTypeSamples)
        {
            if (!affixTable.ContainsKey(sample.Id))
                continue;
            var kindResults = new List<string>();
            foreach (var valueType in new[]
                     { ValueTypeLow, ValueTypeRandom, ValueTypeSpecialRandom, (EAffixValueType)4 })
            {
                var repeated = new List<string>();
                for (var repeat = 0; repeat < 3; repeat++)
                    repeated.Add((SaveAffixData.Create(sample.Id, sample.Quality, sample.Level, 1f, valueType)?.value ?? 0)
                        .ToString());
                kindResults.Add($"vt{(int)valueType}=[{string.Join(",", repeated)}]");
            }
            summary.Add($"{sample.Id}/档位{sample.Quality}/等级{sample.Level}：{string.Join(" ", kindResults)}");
        }
        // 前缀品质探针：同一 (词条,档位,等级,倍率=1) 分别按 mainAttrGrade 1/2/3 计算。
        // 判定：档位 1/4/5 走 SaveAffixData.Create(...)（签名里没有 grade 参数）→ 三档必然相同；
        //       档位 2/3 走 CreateEquipmentAffix(..., grade, ...) → 三档应各不相同。
        // 这样就能确认「前缀品质只影响精良/稀有」。
        var gradeSamples = new (int Id, int Quality, int Level)[]
        {
            (10511, 1, 6),   // 普通（11401 巨骷髅的档位 1 词条）
            (18241, 4, 6),   // 传说专属
            (10111, 3, 6),   // 稀有
            (10051, 3, 6)    // 稀有
        };
        foreach (var sample in gradeSamples)
        {
            if (!affixTable.ContainsKey(sample.Id))
                continue;
            var byGrade = new List<string>();
            var corruptByGrade = new List<string>();
            for (var grade = 1; grade <= 3; grade++)
            {
                byGrade.Add($"g{grade}={NativeEquipmentAffixValue(sample.Id, sample.Quality, sample.Level, 1f, grade)}");
                corruptByGrade.Add($"g{grade}={NativeCorruptAffixValue(sample.Id, sample.Quality, sample.Level, 1f, grade)}");
            }
            summary.Add($"{sample.Id}/档位{sample.Quality}/等级{sample.Level}/倍率1：" +
                        $"Create(无grade)={NativeCreateValue(sample.Id, sample.Quality, sample.Level, 1f, ValueTypeLow)} " +
                        $"CreateEquipmentAffix {string.Join(" ", byGrade)} " +
                        $"| isCorrupt=1 {string.Join(" ", corruptByGrade)}");
        }
        // 34 件装备全部列出（原上限 20 会截断，导致查不到排在后面的装备）。
        foreach (var line in uniqueLines)
            summary.Add(line);
        return string.Join("；", summary);
    }
    internal static EditorResponse ReplaceEquipment(EquipmentReplaceEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.Guid))
            throw new InvalidOperationException("缺少装备定位标识，请刷新装备列表后重试。");
        if (!EquipmentLocators.TryGetValue(edit.Guid, out var locator))
            throw new InvalidOperationException("该装备的定位标识已失效（装备列表可能已被刷新），请重新刷新后重试。");
        var lord = GetLord();
        var request = edit.Equipment ?? throw new InvalidOperationException("缺少装备数据。");
        // 定位旧装备：FieldIndex 是稀疏槽位号，不是列表位置，必须循环匹配。
        var fields = lord.lordBagData.GetFieldList(EItemType.equip);
        ItemFieldData? target = null;
        for (var i = 0; fields != null && i < fields.Count; i++)
        {
            var field = fields[i];
            if (field?.saveItemFieldData?.index == locator.FieldIndex &&
                field.itemData?.saveItemData != null &&
                BuildEquipmentFingerprint(field.itemData.saveItemData) == locator.Fingerprint)
            {
                target = field;
                break;
            }
        }
        // 第二优先：槽位失效时按内容指纹全背包唯一匹配，避免玩家整理背包后无法应用。
        if (target == null && fields != null)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field?.itemData?.saveItemData == null)
                    continue;
                if (BuildEquipmentFingerprint(field.itemData.saveItemData) != locator.Fingerprint)
                    continue;
                if (target != null)
                    throw new InvalidOperationException("背包中存在多件完全相同的装备，无法唯一定位，请刷新装备列表后重试。");
                target = field;
            }
        }
        if (target?.itemData?.saveItemData == null)
            throw new InvalidOperationException("原装备已经发生变化，请刷新装备列表后重试。");
        // 第一步：在内存里构造好新装备。构造失败时旧装备原封不动，不需要任何回滚。
        var (newItem, message) = BuildEquipment(request);
        // 1.1.5 新增的装备标记必须在替换时带上（否则「影子复制品」会被洗成普通装备、复制次数限制也会重置）：
        //   isShadowReplica / isShadowCopied → 影子石复制机制（ItemSys.TryUseShadowStoneOnEquip）
        //   isAutoSealSuggest               → 印章建议的自动开关
        var carryOverFlags = CopyCarryOverFlags(target.itemData.saveItemData, newItem.saveItemData);
        // 第二步：把旧装备从背包里移除，再把新装备放进去。
        // 新装备已经构造完成，addItemToBag 只可能因背包状态异常失败；
        // 真失败时把旧装备放回去，保证存档不丢件。
        var oldItemData = target.itemData;
        var oldCount = oldItemData.saveItemData.count;
        // 旧装备上已镶嵌的符文：在移除前抄成快照（新装备里不会保留它们，替换后返还背包）。
        var oldRunes = CaptureInlaidRunes(oldItemData.saveItemData);
        lord.lordBagData.ReduceItem(target, oldCount);
        if (!lord.lordBagData.addItemToBag(newItem))
        {
            if (!lord.lordBagData.addItemToBag(oldItemData))
                throw new InvalidOperationException("背包状态异常：新装备未加入，且旧装备无法放回，请手动检查背包。");
            throw new InvalidOperationException("领主背包已满，无法替换装备；原装备已放回背包。");
        }
        // 新装备已经进背包，此刻再返还旧装备上的符文：这样即使新装备加入失败也不会多出符文。
        var refundMessage = RefundInlaidRunes(lord, oldRunes);
        // 本次编辑使用的 GUID 已随旧装备消失，立刻失效以免复用。
        EquipmentLocators.Remove(edit.Guid);
        SaveNow();
        return new EditorResponse
        {
            Success = true,
            Message = string.IsNullOrEmpty(refundMessage)
                ? $"{message}（已移除原装备并重新生成{carryOverFlags}）"
                : $"{message}（已移除原装备并重新生成{carryOverFlags}；{refundMessage}）",
            Inventory = GetInventorySnapshot(lord),
            EquipmentInventory = GetEquipmentInventorySnapshot(lord)
        };
    }
    internal static string GenerateEquipment(EquipmentEdit edit)
    {
        var lord = GetLord();
        // 先在内存里构造新装备；构造失败时不会动背包。
        ItemData item;
        string message;
        try
        {
            (item, message) = BuildEquipment(edit);
        }
        catch (Exception exception)
        {
            // 空引用等异常在 Release 下会被内联进本方法、丢掉行号，这里把请求内容一并打出来定位。
            var affixList = new List<string>();
            foreach (var affix in edit.Affixes)
                affixList.Add($"{affix.Id}/档位{affix.Quality}/等级{affix.Level}");
            Plugin.Log.LogError(
                $"[GenerateEquipment] 失败：模板={edit.TemplateId} 品级={edit.Quality} 等级={edit.Level} " +
                $"锻造=+{edit.ForgeLevel} 质量档位={edit.MainAttrGrade} " +
                $"词条({edit.Affixes.Count})=[{string.Join(", ", affixList)}] → {exception}");
            throw;
        }
        if (!lord.lordBagData.addItemToBag(item))
            throw new InvalidOperationException("领主背包已满，装备没有加入存档。");
        SaveNow();
        return message;
    }
    private static (ItemData Item, string Message) BuildEquipment(EquipmentEdit edit)
    {
        // 在内存里构造一件完整的运行时装备，不做入包。
        // 由调用方决定什么时候把返回的 ItemData 放进背包，并统一负责回滚。
        var template = RequireEquipmentRequest(edit);
        var rules = GetEquipmentRules(edit);
        if (!rules.AllowedForgeLevels.Contains(edit.ForgeLevel))
            throw new InvalidOperationException($"锻造等级 {edit.ForgeLevel} 不适用于当前装备等级。");
        // 只有「会被原生存进 SaveItemData.affixList」的行才占额度，其余都是运行时现算的展示行：
        //   · 档位 1/2/3：基础 / 精良 / 稀有 —— 写进存档（:2219170-2219197、:2215412、:2215786）
        //   · 档位 5 且 TAffix.specialType == 1：AddMythAffix / ItemEquipData 写进存档（:2215548-2215576、:1623369-1623377）
        //   · 档位 8 独有技能（:1623227-1623245）、档位 4 传说专属（:1623344-1623349）、
        //     档位 5 且 specialType != 1（:1623344-1623349）→ 只存在于运行时，不占额度。
        // 客户端会把这些固定行一起回传，计数时必须排除，否则会被误判超限。
        var affixTable = TAffix.create();
        // 镶嵌孔位行（ValueKind == SlotType）不占词条额度，单独摘出来处理。
        var socketRows = new List<AffixEdit>();
        var affixRows = new List<AffixEdit>();
        foreach (var row in edit.Affixes)
        {
            if (row.ValueKind == AffixValueKind.SlotType)
                socketRows.Add(row);
            else
                affixRows.Add(row);
        }
        var quotaAffixCount = 0;
        foreach (var affix in affixRows)
        {
            if (affix.Quality is 1 or 2 or 3)
            {
                quotaAffixCount++;
                continue;
            }
            var quotaAffix = affixTable.ContainsKey(affix.Id) ? affixTable[affix.Id] : null;
            if (affix.Quality == (int)EItemQualityType.myth && quotaAffix != null && quotaAffix.specialType == 1)
                quotaAffixCount++;
        }
        if (quotaAffixCount > rules.MaximumAffixCount)
        {
            if (!edit.OverrideLimits)
                throw new InvalidOperationException($"当前品级最多允许 {rules.MaximumAffixCount} 条词条。");
            // 「越界」勾选：只提示不拦截（行级 ForceOverflow 与它无关，仍按行生效）。
            Plugin.Verbose($"[OverrideLimits] 越界添加词条：{quotaAffixCount} 条 > 规则上限 {rules.MaximumAffixCount}");
        }
        // 先创建保存数据，以实际生成后的模板 ID 读取神话专属词条。
        var saveItem = SaveItemData.CreateEquip(edit.TemplateId, edit.Quality, edit.Level, 0f)
            ?? throw new InvalidOperationException("游戏原生装备生成器创建失败。");
        // 需求 1：写入用户指定的装备质量档位。
        // 必须在 CreateEquip（它会 roll 档位）之后、ItemData.Create（按档位物化属性与词条数值）之前。
        // 档位写 1..3 且 mainAttrValue 为 0 时，原生的 EnsureMainAttrGrade 不会改写它。
        if (edit.MainAttrGrade is >= 1 and <= 3)
            saveItem.mainAttrGrade = edit.MainAttrGrade;
        var effectiveTemplate = ResolveGeneratedEquipmentTemplate(saveItem, template);
        var poolByKey = BuildAffixPoolMap(effectiveTemplate, edit.Level, rules.AffixQualityLimits.Keys);
        var equipmentSpecialAffixes = BuildEquipmentSpecialAffixMap(effectiveTemplate, edit.Quality);
        // 需求 2.5：重复性只在「同一档位内部」检查，不同档位允许出现同一个词条 ID。
        var seen = new HashSet<(int Id, int Quality)>();
        var requestedQualityCounts = new Dictionary<int, int>();
        var nativeSpecialAffixKeys = new HashSet<(int Id, int Quality)>();
        foreach (var generated in rules.GeneratedAffixes)
        {
            var generatedKey = (generated.Id, generated.Quality);
            if (!poolByKey.ContainsKey(generatedKey)) nativeSpecialAffixKeys.Add(generatedKey);
        }
        // 原生流程已建立完整装备结构，下面只替换用户明确编辑过的词条列表。
        // 在构造运行时 ItemData 前写入锻造等级，让原生初始化流程按最终装备等级重算基础属性。
        saveItem.forgeLevel = edit.ForgeLevel;
        // 档位 8（独有技能词条）由原生流程按 TEquip.uniqueSkillAffix 直接写进 affixList（:1623227-:1623244），
        // 每件装备只有 1 条。下面的 Clear() 会把它一并删掉，导致生成的独特装备没有独有技能词条、编辑器也读不到。
        // 因此先摘出来，清空后原样放回（不重建，保持原生的等级 1 / rate 1.0 / valueType 1）。
        SaveAffixData? nativeUniqueAffix = null;
        foreach (var nativeAffix in saveItem.affixList)
        {
            if (nativeAffix != null && nativeAffix.quality == 8)
            {
                nativeUniqueAffix = nativeAffix;
                break;
            }
        }
        saveItem.affixList.Clear();
        if (nativeUniqueAffix != null)
        {
            saveItem.affixList.Add(nativeUniqueAffix);
            Plugin.Verbose($"[UniqueAffix] 保留原生注入的档位 8 词条 id={nativeUniqueAffix.id} value={nativeUniqueAffix.value}");
        }
        else if (edit.Quality == 8 && effectiveTemplate.uniqueSkillAffix > 0)
        {
            // 档位 8 不写进 saveItem.affixList：它是「品级强制词条」，原生在运行时按 TEquip.uniqueSkillAffix
            // 现加（:2148324 case 8 → qualityAffixList），手工写进 affixList 会被 ItemData.Create 丢掉
            // —— 实测：写入时 5 条含 17211，紧接着读回只剩 4 条。编辑器侧改由读回路径合成下发。
            Plugin.Verbose($"[UniqueAffix] 品级 8 的独有技能词条 id={effectiveTemplate.uniqueSkillAffix} 由原生运行时注入，无需写入存档");
        }
        else
        {
            Plugin.Verbose($"[UniqueAffix] 品级={edit.Quality} uniqueSkillAffix={effectiveTemplate.uniqueSkillAffix} → 该装备没有档位 8 词条");
        }
        foreach (var requested in affixRows)
        {
            // 档位 8 不由客户端添加，也不走通用池：它已由原生按 TEquip.uniqueSkillAffix 注入（每件 1 条）。
            // 客户端把它作为一条词条回传时直接跳过，避免重复或走到错误的分支（此前正是这里抛空引用）。
            if (requested.Quality == 8)
                continue;
            // 即使桌面端已经过滤，桥接层仍需再次校验，防止旧客户端或手工请求绕过规则。
            if (!seen.Add((requested.Id, requested.Quality)))
                throw new InvalidOperationException($"词条 {requested.Id}（档位 {requested.Quality}）重复。");
            var requestedKey = (requested.Id, requested.Quality);
            if (!affixTable.ContainsKey(requested.Id) ||
                (!poolByKey.ContainsKey(requestedKey) &&
                 !equipmentSpecialAffixes.ContainsKey(requestedKey) &&
                 !nativeSpecialAffixKeys.Contains(requestedKey)))
                throw new InvalidOperationException($"词条 {requested.Id} 不在当前装备、品级和等级的游戏词条池中。");
            var affix = affixTable[requested.Id];
            var kind = ClassifyAffix(affix, requested.Quality);
            // 档位额度只对**会写进存档**的行生效（与上面的总额度计数同一口径）：
            //   · 档位 1/2/3、以及档位 5 的 specialType == 1 行 → 占额度，逐条校验；
            //   · 档位 4（传说专属）、档位 8（独有技能）、档位 5 的非 specialType 行都是运行时现算，
            //     数量由模板决定、不在 AffixQualityLimits 里 —— 以前会误报"档位 4 最多允许 0 条词条"。
            var quotaConsuming = requested.Quality is 1 or 2 or 3 ||
                                 (requested.Quality == 5 && affix.specialType == 1);
            if (quotaConsuming)
            {
                // 套装装备的实例档位可能与 TAffix 中的基础档位不同，必须按用户所见的实际档位计数。
                requestedQualityCounts.TryGetValue(requested.Quality, out var requestedQualityCount);
                requestedQualityCount++;
                if (!rules.AffixQualityLimits.TryGetValue(requested.Quality, out var qualityLimit) ||
                    requestedQualityCount > qualityLimit)
                    throw new InvalidOperationException($"档位 {requested.Quality} 最多允许 {qualityLimit} 条词条。");
                requestedQualityCounts[requested.Quality] = requestedQualityCount;
            }
            // 装备专属词条使用 legendAffixArr 的原生倍率；神话装备的档位 3 再补上原生 ×1.5。
            var poolRate = poolByKey.TryGetValue(requestedKey, out var poolInfo)
                ? poolInfo.Rate
                : equipmentSpecialAffixes.TryGetValue(requestedKey, out var specialRate) ? specialRate : 1f;
            if (poolRate <= 0f) poolRate = 1f;
            var rate = GetEffectiveAffixRate(poolRate, edit.Quality, requested.Quality);
            SaveAffixData? saveAffix;
            if (kind == AffixValueKind.GradeBased)
            {
                // 档位 2/3：走原生"创建 1 级 + 逐级 LevelUp"的路径（与游戏内词条升级完全同一条流水线）。
                var level = Math.Clamp(requested.Level, 1, Math.Max(1, rules.MaximumAffixLevel));
                saveAffix = CreateAffixByNativeLevelUp(requested.Id, requested.Quality, level, rate,
                    Math.Clamp(saveItem.mainAttrGrade, 1, 3));
            }
            else
            {
                // 其它档位由原生创建流程直接写入，等级被钉死（档位 8 为 1，其余为 affixMaxLevel）。
                var pinned = GetPinnedAffixLevel(requested.Quality, Math.Max(1, rules.MaximumAffixLevel));
                var level = pinned <= 0 ? Math.Max(1, rules.MaximumAffixLevel) : pinned;
                if (requested.Quality == 8)
                {
                    saveAffix = SaveAffixData.Create(requested.Id, 8, 1, 1f, ValueTypeRandom);
                }
                else
                {
                    // 只有神话(5)且 specialType==1 才用有符号特殊随机；传说(4) 与其余行固定取下端。
                    var valueType = requested.Quality == (int)EItemQualityType.myth && affix.specialType == 1
                        ? ValueTypeSpecialRandom
                        : ValueTypeLow;
                    saveAffix = SaveAffixData.Create(requested.Id, requested.Quality, level, rate, valueType);
                }
            }
            if (saveAffix == null)
                throw new InvalidOperationException($"游戏拒绝创建词条 {requested.Id}。");
            // 污染标记：原生在 1.1.0 后把腐蚀结果记成「档位3 + isCorrupt」，
            // 这里按客户端带回来的标记原样写回，避免用编辑器改一次装备就把污染洗掉。
            if (requested.IsCorrupt)
                saveAffix.isCorrupt = true;
            if (requested.IsCorruptLocked)
                saveAffix.isCorruptLocked = true;
            // 需求 2.3 修正 + 传说/神话/独有档位限制：
            //   · 档位 4（传说专属）与档位 8（独有技能）是运行时现算、不落存档 → 永远不写用户数值；
            //   · 档位 5 只有 specialType == 1 的行会落存档，且按界面约定必须先勾「强制越界」才允许写；
            //   · 其余（1/2/3）保持原行为：给出明确数值就写入，哨兵只在勾选强制越界时打。
            var tierLocked = requested.Quality is 4 or 5 or 8;
            var tierWritable = requested.Quality == 5 && affix.specialType == 1 && requested.ForceOverflow;
            var requestedValue = requested.Value;
            var mayWriteValue = affix.effectType == 1 && requestedValue.HasValue &&
                                (!tierLocked || tierWritable);
            if (mayWriteValue && requestedValue.HasValue)
            {
                saveAffix.value = requestedValue.Value;
                if (requested.ForceOverflow)
                {
                    // 双字段哨兵：talentLevel=1 + abilityId=int.MinValue。
                    // 只有两者同时匹配，SaveAffixValuePatch 才直接返回存档值。
                    saveAffix.talentLevel = 1;
                    saveAffix.abilityId = SaveAffixValuePatch.ManualValueAbilityIdSentinel;
                }
            }
            else if (tierLocked && requestedValue.HasValue)
            {
                Plugin.Verbose($"[TierLocked] 忽略档位 {requested.Quality} 词条 {requested.Id} 的用户数值" +
                               $"（specialType={affix.specialType}，forceOverflow={requested.ForceOverflow}）：该行不落存档或未勾选强制越界");
            }
            // 诊断（受模组设置里的「输出详细日志」控制）：记录下发给原生的实际数值。
            Plugin.Verbose($"[AffixCreate] id={requested.Id}({GetAffixName(affix)}) tier={requested.Quality} " +
                           $"kind={kind} effectType={affix.effectType} specialType={affix.specialType} " +
                           $"level={saveAffix.level} rate={rate:0.####} value={saveAffix.value} " +
                           $"requestedValue={(requested.Value.HasValue ? requested.Value.Value.ToString() : "null")} " +
                           $"forceOverflow={requested.ForceOverflow}");
            saveItem.affixList.Add(saveAffix);
        }
        // ── 空组交给游戏随机 ──
        // 某个档位一条都没写时，调用原生 Add* 让游戏按自己的规则掷数量与内容：
        //   · 档位 2 精良：AddFineAffix（数量 = TEquipQuality.fineAffixCount）
        //   · 档位 3 稀有：AddRareAffix（4/5 = legendAffixCount；3/6/8 = RandomRangeInt(2, rare+1)；1/2/7 原生跳过）
        //   · 档位 5 神话专属：AddMythAffix（只落 specialType == 1 的行）
        // 基础词条（档位 1）不走这套：它由 TEquip.baseAffix 决定，客户端始终带着那条固定行。
        var rolledGroups = new List<string>();
        var rollFailures = new List<string>();
        var writtenQualities = new HashSet<int>();
        foreach (var row in affixRows)
            writtenQualities.Add(row.Quality);
        var qualityTable = TEquipQuality.create();
        var qualityRow = qualityTable.ContainsKey(edit.Quality) ? qualityTable[edit.Quality] : null;
        if (!writtenQualities.Contains((int)EItemQualityType.fine) && (qualityRow?.fineAffixCount ?? 0) > 0)
        {
            try
            {
                saveItem.AddFineAffix();
                rolledGroups.Add("精良");
            }
            catch (Exception exception)
            {
                rollFailures.Add($"精良（{exception.Message}）");
            }
        }
        if (!writtenQualities.Contains((int)EItemQualityType.rare))
        {
            try
            {
                saveItem.AddRareAffix();
                rolledGroups.Add("稀有");
            }
            catch (Exception exception)
            {
                rollFailures.Add($"稀有（{exception.Message}）");
            }
        }
        if (edit.Quality == (int)EItemQualityType.myth && !writtenQualities.Contains((int)EItemQualityType.myth))
        {
            try
            {
                saveItem.AddMythAffix();
                rolledGroups.Add("神话专属");
            }
            catch (Exception exception)
            {
                rollFailures.Add($"神话专属（{exception.Message}）");
            }
        }
        if (rolledGroups.Count > 0 || rollFailures.Count > 0)
        {
            Plugin.Verbose($"[EmptyGroupRoll] 空组交给原生随机：{(rolledGroups.Count > 0 ? string.Join("、", rolledGroups) : "无")}" +
                           $"{(rollFailures.Count > 0 ? $"；失败：{string.Join("、", rollFailures)}" : "")}");
        }
        // 诊断（需开启详细日志）：列出最终写入存档的词条，确认档位 8 是否在位。
        if (Plugin.VerboseEnabled)
        {
            var finalAffixes = new List<string>();
            foreach (var finalAffix in saveItem.affixList)
                finalAffixes.Add(finalAffix == null ? "null" : $"{finalAffix.id}/档位{finalAffix.quality}/值{finalAffix.value}");
            Plugin.Log.LogInfo($"[AffixList] 最终词条({saveItem.affixList.Count})：[{string.Join(", ", finalAffixes)}]");
        }
        // 镶嵌组（开孔逻辑并入词条表）：非空才写 slotCount / slotRuneList；
        // 空组 = 完全不干预，沿用原生 CreateEquip 的开孔结果。
        var slotNote = ApplySocketRows(saveItem, socketRows, template, edit.OverrideLimits);
        // 第三个参数是 needCount，普通背包实例必须为 0，否则图标会显示“数量/1”。
        var item = ItemData.Create(saveItem, EItemPosType.bag, 0)
        ?? throw new InvalidOperationException("游戏创建运行时装备数据失败。");
        var summary = $"已生成“{template.name}”：品级 {edit.Quality}，等级 {edit.Level}，锻造 +{edit.ForgeLevel}，" +
                      $"{edit.Affixes.Count} 条词条";
        if (!string.IsNullOrEmpty(slotNote))
            summary += $"，{slotNote}";
        if (rolledGroups.Count > 0)
            summary += $"，空组交给原生随机：{string.Join("、", rolledGroups)}";
        if (rollFailures.Count > 0)
            summary += $"，random 失败：{string.Join("、", rollFailures)}";
        return (item, summary + "。");
    }
    internal static string UpdateHero(HeroEdit edit)
    {
        var lord = GetLord();
        var hero = FindHero(lord, edit.UniqueId);
        if (hero.IsAdventureBusy())
            throw new InvalidOperationException("该角色正在进行冒险，请返回城镇后再修改。");
        var save = hero.saveHeroData;
        if (edit.Quality != 0 && edit.Quality != save.quality)
            throw new InvalidOperationException("角色品级已在游戏中变化，请刷新后再编辑其他属性；品级请使用单独的“应用品级并重算”按钮。");
        var heroLevelTable = THeroLevel.create();
        var maxHeroLevel = GetMaximumHeroLevel(heroLevelTable);
        if (!heroLevelTable.ContainsKey(edit.Level))
            throw new InvalidOperationException($"角色等级 {edit.Level} 不存在于当前游戏等级表中；表内上限为 {maxHeroLevel}。");
        var blessingTable = THeroBlessLevel.create();
        if (edit.BlessingLevel != 0 && !blessingTable.ContainsKey(edit.BlessingLevel))
            throw new InvalidOperationException($"赐福等级 {edit.BlessingLevel} 不存在于当前游戏规则表中。");
        var growthRules = CreateGrowthAttributeRules(hero);
        var growthByType = new Dictionary<int, GrowthAttributeEdit>();
        foreach (var growth in edit.GrowthAttributes)
        {
            if (!growthByType.TryAdd(growth.Type, growth))
                throw new InvalidOperationException($"属性行 {growth.Type} 重复出现，请刷新数据。");
            var rule = growthRules.Find(item => item.Type == growth.Type)
                ?? throw new InvalidOperationException($"属性类型 {growth.Type} 已不在当前游戏规则中。");
            if (Math.Abs(growth.Value - MathF.Round(growth.Value)) > 0.0001f)
                throw new InvalidOperationException($"“{growth.Name}”每级成长必须是整数。");
            if (growth.Value < rule.MinimumValue || growth.Value > rule.MaximumValue)
                throw new InvalidOperationException($"“{growth.Name}”每级成长合法范围为 {rule.MinimumValue}-{rule.MaximumValue}。");
            if (Math.Abs(growth.Points - MathF.Round(growth.Points)) > 0.0001f)
                throw new InvalidOperationException($"“{growth.Name}”点数必须是整数。");
            if (growth.Points < rule.PointMinimum || growth.Points > rule.PointMaximum)
                throw new InvalidOperationException($"“{growth.Name}”点数合法范围为 {rule.PointMinimum}-{rule.PointMaximum}。");
        }
        if (growthByType.Count != growthRules.Count)
            throw new InvalidOperationException("属性行不完整，请刷新游戏数据后重试。");
        // 分组校验：生命+魔力（main）与力+敏+智（base）各自的点数、成长总和必须等于品质规定的总量。
        var basePointSum = 0f;
        var mainPointSum = 0f;
        var baseGrowSum = 0f;
        var mainGrowSum = 0f;
        foreach (var growth in edit.GrowthAttributes)
        {
            if (growth.Group == "main")
            {
                mainPointSum += growth.Points;
                mainGrowSum += growth.Value;
            }
            else
            {
                basePointSum += growth.Points;
                baseGrowSum += growth.Value;
            }
        }
        var requiredBasePoint = hero.tHeroQualityData?.baseAttrTotal ?? 0;
        var requiredMainPoint = hero.tHeroQualityData?.mainAttrTotal ?? 0;
        var requiredBaseGrow = hero.tHeroQualityData?.baseAttrGrow ?? 0;
        var requiredMainGrow = hero.tHeroQualityData?.mainAttrGrow ?? 0;
        if (Math.Abs(basePointSum - requiredBasePoint) > 0.0001f)
            throw new InvalidOperationException($"生命+魔力点数总和必须为 {requiredMainPoint}，当前为 {mainPointSum:0.###}。");
        if (Math.Abs(mainPointSum - requiredMainPoint) > 0.0001f)
            throw new InvalidOperationException($"力量+敏捷+智力点数总和必须为 {requiredBasePoint}，当前为 {basePointSum:0.###}。");
        if (Math.Abs(baseGrowSum - requiredBaseGrow) > 0.0001f)
            throw new InvalidOperationException($"生命+魔力成长总和必须为 {requiredMainGrow}，当前为 {mainGrowSum:0.###}。");
        if (Math.Abs(mainGrowSum - requiredMainGrow) > 0.0001f)
            throw new InvalidOperationException($"力量+敏捷+智力成长总和必须为 {requiredBaseGrow}，当前为 {baseGrowSum:0.###}。");
        // 提交时重新构建技能树规则，不能信任桌面端连接时缓存的等级上限和候选项。
        var currentSlots = BuildTalentSlots(hero);
        var bySlot = new Dictionary<int, TalentSlotEdit>();
        foreach (var slot in currentSlots) bySlot[slot.SlotId] = slot;
        var selectedTalentSkillIds = new HashSet<int>();
        var extraRekeys = new Dictionary<int, int>();
        var requestedExtraKeys = new HashSet<int>();
        foreach (var requested in edit.TalentSlots)
        {
            if (!bySlot.TryGetValue(requested.SlotId, out var rule))
                throw new InvalidOperationException($"技能树位置 {requested.SlotId} 已不存在，请刷新数据。");
            SkillOption? selected = null;
            foreach (var option in rule.SkillOptions)
                if (option.TalentId == requested.TalentId) { selected = option; break; }
            if (selected == null)
                throw new InvalidOperationException($"技能树位置 {requested.SlotId} 不支持天赋/技能 {requested.TalentId}。");
            if (!rule.CanChangeTalent && requested.TalentId != rule.TalentId)
                throw new InvalidOperationException($"技能树位置 {requested.SlotId} 是基础技能，游戏规则不允许修改。");
            if (requested.Level < rule.MinimumLevel || requested.Level > selected.MaximumLevel)
                throw new InvalidOperationException($"“{selected.Name}”的合法等级为 {rule.MinimumLevel}-{selected.MaximumLevel}。");
            if (rule.Category == "天赋技能" && !selectedTalentSkillIds.Add(selected.SkillId))
                throw new InvalidOperationException($"天赋技能“{selected.Name}”已经被其他天赋技能位置选择。");
            if (rule.IsAlien || rule.IsInspired)
            {
                if (!requestedExtraKeys.Add(requested.TalentId))
                    throw new InvalidOperationException($"额外技能/天赋 {requested.TalentId} 被重复选择。");
                extraRekeys[requested.SlotId] = requested.TalentId;
            }
        }
        var oldExtraKeys = new HashSet<int>(extraRekeys.Keys);
        foreach (var pair in extraRekeys)
            if (save.talentDic.ContainsKey(pair.Value) && !oldExtraKeys.Contains(pair.Value))
                throw new InvalidOperationException($"不能把额外技能/天赋改为 {pair.Value}：该存档位置已经被占用。");
        var targetBlessingLevel = edit.BlessingLevel;
        save.level = edit.Level;
        save.exp = 0;
        save.name = edit.Name.Trim();
        save.mainAttrDic[EAttrType.STR] = Math.Max(0, edit.Strength);
        save.mainAttrDic[EAttrType.DEX] = Math.Max(0, edit.Dexterity);
        save.mainAttrDic[EAttrType.INT] = Math.Max(0, edit.Intelligence);
        // 点数与成长按分组写回：生命/魔力 → base*，力/敏/智 → main*（会覆盖上面的三行，以表格为准）
        foreach (var growth in edit.GrowthAttributes)
        {
            var attrType = (EAttrType)growth.Type;
            if (growth.Group == "main")
            {
                save.mainAttrDic[attrType] = growth.Points;
                save.mainAttrUpDic[attrType] = growth.Value;
            }
            else
            {
                save.baseAttrDic[attrType] = growth.Points;
                save.baseAttrUpDic[attrType] = growth.Value;
            }
        }
        // 额外项目以天赋 ID 作为字典键。先保存对象并统一移除旧键，才能安全支持互换候选。
        var extraTargets = new Dictionary<int, SaveTalentData>();
        foreach (var pair in extraRekeys)
        {
            extraTargets[pair.Key] = save.talentDic[pair.Key];
            save.talentDic.Remove(pair.Key);
        }
        foreach (var requested in edit.TalentSlots)
        {
            var isExtra = extraTargets.TryGetValue(requested.SlotId, out var extraTarget);
            var target = isExtra ? extraTarget! : save.talentDic[requested.SlotId];
            target.id = requested.TalentId;
            var slotRule = bySlot[requested.SlotId];
            // 固定主动槽即使选择其他职业技能也不占异化名额；只有原生额外槽保留来源标记。
            target.isInspired = slotRule.IsInspired;
            target.isAlien = slotRule.IsAlien;
            target.SetLevel(requested.Level);
            if (isExtra)
                save.talentDic[requested.TalentId] = target;
        }
        // 重新初始化运行时角色，使属性派生值、装备效果和技能对象与存档字段保持一致。
        hero.Init();
        // 新版本已移除 ClearAllBlessLevel，也没有提供可反向扣除随机赐福属性的原生方法。
        // 降级时保留界面中明确提交的三项主属性，只更新赐福等级并重建运行时数据。
        if (targetBlessingLevel < save.blessLevel)
        {
            save.blessLevel = targetBlessingLevel;
            hero.Init();
        }
        while (save.blessLevel < targetBlessingLevel)
        {
            var previousLevel = save.blessLevel;
            hero.BlessLevelUp();
            if (save.blessLevel <= previousLevel)
                throw new InvalidOperationException($"游戏无法把赐福等级提升到 {targetBlessingLevel}。");
        }
        // 游戏更新后由该方法根据赐福里程碑重算赐福天赋点。
        hero.heroTalentData.RecalcBlessTalentPointByMilestones();
        save.talentRemainPoint = Math.Max(0, edit.RemainingSkillPoints);
        hero.SetName(save.name);
        Game.eventMgr?.sendEvent(EEvent.talentChange, hero);
        Game.eventMgr?.sendEvent(EEvent.heroLevelUp, hero);
        SaveNow();
        return $"已保存角色“{GetHeroName(hero)}”，等级 {save.level}。";
    }
    internal static EditorResponse ChangeHeroQuality(int uniqueId, int targetQuality)
    {
        var lord = GetLord();
        var hero = FindHero(lord, uniqueId);
        if (hero.IsAdventureBusy())
            throw new InvalidOperationException("该角色正在进行冒险，请返回城镇后再修改品级。");
        var qualityTable = THeroQuality.create();
        if (!qualityTable.ContainsKey(targetQuality))
            throw new InvalidOperationException($"角色品级 {targetQuality} 不存在于当前游戏 THeroQuality 表中。");
        var previousQuality = hero.saveHeroData.quality;
        const int maximumSteps = 100;
        var steps = 0;
        while (hero.saveHeroData.quality != targetQuality)
        {
            if (++steps > maximumSteps)
                throw new InvalidOperationException("游戏原生品级修改没有到达目标品级，操作已中止。");
            var currentQuality = hero.saveHeroData.quality;
            var direction = currentQuality < targetQuality ? 1 : -1;
            var nextQuality = currentQuality + direction;
            if (!qualityTable.ContainsKey(nextQuality))
                throw new InvalidOperationException($"当前游戏品级表在 {currentQuality} 与目标品级 {targetQuality} 之间不连续。");
            // ChangeQuality 会原生重建主属性、基础属性和每级成长，并同步异化技能与技能点。
            // 100% 使用原生最高升品概率，0% 在品级大于 1 时固定降一级；循环会校验实际结果直至目标。
            hero.ChangeQuality(direction > 0 ? 100f : 0f);
            var changedQuality = hero.saveHeroData.quality;
            if (Math.Abs(changedQuality - currentQuality) != 1 || !qualityTable.ContainsKey(changedQuality))
                throw new InvalidOperationException($"游戏拒绝从品级 {currentQuality} 执行有效的逐级调整。");
        }
        SaveNow();
        var qualityName = qualityTable[targetQuality]?.name ?? $"品级 {targetQuality}";
        return new EditorResponse
        {
            Success = true,
            Message = previousQuality == targetQuality
                ? $"“{GetHeroName(hero)}”已经是“{qualityName}”，未修改属性。"
                : $"已使用游戏原生方法把“{GetHeroName(hero)}”从品级 {previousQuality} 调整为“{qualityName}”，并重算属性、成长与技能点。",
            Snapshot = GetSnapshot()
        };
    }
    private static int GetMaximumInspiredTalents()
    {
        var house = Game.dataMgr?.nowSeasonData?.townData?.GetHouse((EHouseType)101);
        return house?.houseAttrData == null
            ? 0
            : Math.Max(0, (int)Math.Round(house.houseAttrData.GetAttrValue((EHouseAttrType)10140, null)));
    }
    private static HeroEdit CreateHeroEdit(HeroData hero, int maximumLevel)
    {
        var save = hero.saveHeroData;
        var result = new HeroEdit
        {
            UniqueId = save.uniqueId,
            Name = GetHeroName(hero),
            Level = save.level,
            MaximumLevel = Math.Max(1, maximumLevel),
            BlessingLevel = save.blessLevel,
            Quality = save.quality,
            Strength = ReadAttribute(save, EAttrType.STR),
            Dexterity = ReadAttribute(save, EAttrType.DEX),
            Intelligence = ReadAttribute(save, EAttrType.INT),
            RemainingSkillPoints = save.talentRemainPoint,
            MaximumAlienSkills = Math.Max(0, save.GetNeededAlienSkillCount()),
            MaximumInspiredTalents = GetMaximumInspiredTalents(),
            TalentSlots = BuildTalentSlots(hero)
        };
        result.GrowthAttributes = CreateGrowthAttributeRules(hero);
        return result;
    }

    private static List<GrowthAttributeEdit> CreateGrowthAttributeRules(HeroData hero)
    {
        // 五行：生命 / 魔力 / 力量 / 敏捷 / 智力。
        // 实测映射（AttrDiag 已确认）：生命/魔力 → mainAttrDic + mainScopeArr（行 0/1）+ mainAttrTotal/mainAttrGrow；
        //                       力/敏/智 → baseAttrDic + baseScopeArr（行 0/1/2）+ baseAttrTotal/baseAttrGrow。
        var result = new List<GrowthAttributeEdit>();
        var save = hero.saveHeroData;
        if (save == null)
            return result;
        var mainScope = hero.tHeroJobData?.mainScopeArr;
        var baseScope = hero.tHeroJobData?.baseScopeArr;
        var mainPointTotal = hero.tHeroQualityData?.mainAttrTotal ?? 0;
        var mainGrowTotal = hero.tHeroQualityData?.mainAttrGrow ?? 0;
        var basePointTotal = hero.tHeroQualityData?.baseAttrTotal ?? 0;
        var baseGrowTotal = hero.tHeroQualityData?.baseAttrGrow ?? 0;
        AddGrowthRow(result, EAttrType.hp, "main", mainScope, save.mainAttrDic, save.mainAttrUpDic, mainPointTotal, mainGrowTotal, 0);
        AddGrowthRow(result, EAttrType.mp, "main", mainScope, save.mainAttrDic, save.mainAttrUpDic, mainPointTotal, mainGrowTotal, 1);
        AddGrowthRow(result, EAttrType.STR, "base", baseScope, save.baseAttrDic, save.baseAttrUpDic, basePointTotal, baseGrowTotal, 0);
        AddGrowthRow(result, EAttrType.DEX, "base", baseScope, save.baseAttrDic, save.baseAttrUpDic, basePointTotal, baseGrowTotal, 1);
        AddGrowthRow(result, EAttrType.INT, "base", baseScope, save.baseAttrDic, save.baseAttrUpDic, basePointTotal, baseGrowTotal, 2);
        return result;
    }

    private static void AddGrowthRow(List<GrowthAttributeEdit> target, EAttrType type, string group,
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? scope, Il2CppSystem.Collections.Generic.Dictionary<EAttrType, float>? pointDic,
        Il2CppSystem.Collections.Generic.Dictionary<EAttrType, float>? growDic,
        int pointTotal, int growTotal, int positionalRow)
    {
        var pointMin = 0;
        var pointMax = 0;
        var growMin = 0;
        var growMax = 0;

        // 行号按组内顺序：生命/魔力 0-1，力/敏/智 0-2；先读 scope 行数，越界时跳过原生计算（避免原生索引越界）。
        var rows = GetScopeRowCount(scope);
        var row = positionalRow;
        if (scope != null && row >= 0 && row < rows)
        {
            try
            {
                DataTool.GetConstrainedIntRangeInScopeArrPartition(scope, pointTotal, row, out pointMin, out pointMax);
                DataTool.GetConstrainedIntRangeInScopeArrPartition(scope, growTotal, row, out growMin, out growMax);
            }
            catch (Exception exception)
            {
                pointMin = pointMax = growMin = growMax = 0;
                Plugin.Log.LogWarning($"[GrowthRow] {GetAttributeName(type)} 范围计算失败（rows={rows}, row={row}）：{exception.Message}");
            }
        }

        target.Add(new GrowthAttributeEdit
        {
            Type = (int)type,
            Name = GetAttributeName(type),
            Group = group,
            Points = pointDic != null && pointDic.ContainsKey(type) ? pointDic[type] : 0f,
            PointMinimum = pointMin,
            PointMaximum = pointMax,
            Value = growDic != null && growDic.ContainsKey(type) ? growDic[type] : 0f,
            MinimumValue = growMin,
            MaximumValue = growMax
        });
    }
    // scope 是 int[,]/float[,] 之类的多维数组，用 System.Array 接口安全取行数。
    private static int GetScopeRowCount(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? scope)
    {
        if (scope == null)
            return 0;
        try
        {
            var array = scope.TryCast<Il2CppSystem.Array>();
            return array?.GetLength(0) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static List<TalentSlotEdit> BuildTalentSlots(HeroData hero)
    {
        var result = new List<TalentSlotEdit>();
        var save = hero.saveHeroData;
        var runtime = hero.heroTalentData.talentDic;
        var talentTable = TTalent.create();
        var jobTable = THeroJob.create();
        // 同一角色中候选天赋的等级上限与候选本身有关，不随普通技能槽位变化。
        // 所有职业技能进入每个下拉框后候选数量较大，缓存可避免反复创建临时 TalentData。
        var candidateCapCache = new Dictionary<int, int>();
        var otherJobSkillPool = save.GetOtherJobSkillPool();
        var otherJobMasteryPool = hero.heroTalentData.GetOtherJobMasteryPool();
        var skillPositions = new HashSet<int>();
        var masteryPositions = new HashSet<int>();
        var skillPositionList = save.GetSkillPosList();
        var masteryPositionList = save.GetMasteryPosList();
        for (var index = 0; skillPositionList != null && index < skillPositionList.Count; index++)
            skillPositions.Add(skillPositionList[index]);
        for (var index = 0; masteryPositionList != null && index < masteryPositionList.Count; index++)
            masteryPositions.Add(masteryPositionList[index]);
        foreach (var pair in save.talentDic)
        {
            var saved = pair.Value;
            if (saved == null || !talentTable.ContainsKey(saved.id))
                continue;
            var currentTable = talentTable[saved.id];
            var currentRuntime = runtime.ContainsKey(pair.Key) ? runtime[pair.Key] : null;
            var cap = currentRuntime?.GetTalentLevelCap() ?? Math.Max(0, saved.level);
            var minimum = currentRuntime == null ? 0 : Math.Max(0, hero.heroTalentData.GetTalentMinSaveLevel(currentRuntime));
            var isExtra = saved.isAlien || saved.isInspired;
            var isTalentSkill = !isExtra && skillPositions.Contains(saved.posId);
            var isTalentMastery = !isExtra && masteryPositions.Contains(saved.posId);
            // CreateBaseSkillTalentDic 与 GetSkillPosList 是两条独立原生路径；不属于天赋技能位置的
            // 固定职业技能就是三个基础技能，不能只用单个 baseSkillId 判断。
            var isBaseSkill = !isExtra && currentTable.skillId > 0 && !isTalentSkill;
            var kind = saved.isInspired ? "启迪天赋" : saved.isAlien ? "异化技能" :
                isBaseSkill ? "基础技能" : isTalentSkill ? "天赋技能" : "天赋专精";
            var categoryOrder = isBaseSkill ? 0 : isTalentSkill ? 1 : isTalentMastery ? 2 :
                saved.isAlien ? 3 : saved.isInspired ? 4 : 2;
            var slot = new TalentSlotEdit
            {
                SlotId = pair.Key,
                TalentId = saved.id,
                SkillId = currentTable.skillId,
                Kind = kind,
                Category = kind,
                CategoryOrder = categoryOrder,
                IsAlien = saved.isAlien,
                IsInspired = saved.isInspired,
                CanChangeTalent = saved.isAlien || saved.isInspired || isTalentSkill,
                Name = GetTalentDisplayName(currentTable),
                Level = saved.level,
                MinimumLevel = minimum,
                MaximumLevel = Math.Max(minimum, cap)
            };
            // 额外技能/天赋以天赋 ID 作为存档字典键；候选来自各自的游戏原生跨职业池，
            // 提交切换时会同步重建字典键并保留异化、启迪来源标记。
            if (saved.isAlien)
            {
                // 异化技能只能从游戏原生的其他职业技能池选择，并保持当前额外槽位的行与类型。
                for (var i = 0; otherJobSkillPool != null && i < otherJobSkillPool.Count; i++)
                {
                    var candidate = otherJobSkillPool[i];
                    if (candidate == null || candidate.skillId <= 0 ||
                        candidate.type != currentTable.type || candidate.floor != currentTable.floor)
                        continue;
                    slot.SkillOptions.Add(new SkillOption
                    {
                        TalentId = candidate.id,
                        SkillId = candidate.skillId,
                        Name = GetTalentDisplayName(candidate),
                        JobName = GetHeroJobName(jobTable, candidate.jobId),
                        MaximumLevel = GetCandidateTalentCap(hero, saved, candidate.id, cap, candidateCapCache)
                    });
                }
            }
            else if (saved.isInspired)
            {
                // 启迪候选完全来自游戏的其他职业专精池；当前项会在下方补回，便于保持不变。
                for (var i = 0; otherJobMasteryPool != null && i < otherJobMasteryPool.Count; i++)
                {
                    var candidate = otherJobMasteryPool[i];
                    if (candidate == null || candidate.masteryId <= 0)
                        continue;
                    slot.SkillOptions.Add(new SkillOption
                    {
                        TalentId = candidate.id,
                        SkillId = candidate.skillId,
                        Name = GetTalentDisplayName(candidate),
                        JobName = GetHeroJobName(jobTable, candidate.jobId),
                        MaximumLevel = GetCandidateTalentCap(hero, saved, candidate.id, cap, candidateCapCache)
                    });
                }
            }
            else if (isTalentSkill)
            {
                // 游戏原生 GetSkillList 使用 TTalent.type == 1 识别天赋技能。这里不再按当前
                // 槽位的 index 缩小候选池，让每个可编辑槽位都能选择所有职业的有效天赋技能。
                foreach (var candidatePair in talentTable)
                {
                    var candidate = candidatePair.Value;
                    if (candidate == null || candidate.skillId <= 0 || candidate.type != 1)
                        continue;
                    slot.SkillOptions.Add(new SkillOption
                    {
                        TalentId = candidate.id,
                        SkillId = candidate.skillId,
                        Name = GetTalentDisplayName(candidate),
                        JobName = GetHeroJobName(jobTable, candidate.jobId),
                        MaximumLevel = GetCandidateTalentCap(hero, saved, candidate.id, cap, candidateCapCache)
                    });
                }
            }
            if (slot.SkillOptions.Count == 0)
            {
                slot.SkillOptions.Add(new SkillOption
                {
                    TalentId = currentTable.id,
                    SkillId = currentTable.skillId,
                    Name = GetTalentDisplayName(currentTable),
                    JobName = GetHeroJobName(jobTable, currentTable.jobId),
                    MaximumLevel = Math.Max(minimum, cap)
                });
            }
            if (!ContainsTalent(slot.SkillOptions, currentTable.id))
            {
                slot.SkillOptions.Add(new SkillOption
                {
                    TalentId = currentTable.id,
                    SkillId = currentTable.skillId,
                    Name = GetTalentDisplayName(currentTable),
                    JobName = GetHeroJobName(jobTable, currentTable.jobId),
                    MaximumLevel = Math.Max(minimum, cap)
                });
            }
            slot.SkillOptions.Sort((a, b) =>
            {
                var jobCompare = string.Compare(a.JobName, b.JobName, StringComparison.CurrentCulture);
                return jobCompare != 0 ? jobCompare : a.TalentId.CompareTo(b.TalentId);
            });
            result.Add(slot);
        }
        result.Sort((a, b) =>
        {
            var categoryCompare = a.CategoryOrder.CompareTo(b.CategoryOrder);
            return categoryCompare != 0 ? categoryCompare : a.SlotId.CompareTo(b.SlotId);
        });
        return result;
    }
    private static int GetCandidateTalentCap(
        HeroData hero,
        SaveTalentData current,
        int candidateId,
        int fallback,
        Dictionary<int, int> cache)
    {
        if (cache.TryGetValue(candidateId, out var cached))
            return cached;
        try
        {
            // 临时 TalentData 不写入存档，仅用于调用游戏的动态等级上限计算。
            var previewSave = SaveTalentData.Create(candidateId, 0, current.posId, current.isFixed);
            var preview = TalentData.Create(previewSave, hero);
            var result = Math.Max(0, preview.GetTalentLevelCap());
            cache[candidateId] = result;
            return result;
        }
        catch
        {
            return Math.Max(0, fallback);
        }
    }
    private static int GetMaximumHeroLevel(
        Il2CppSystem.Collections.Generic.Dictionary<int, THeroLevel>? levelTable = null)
    {
        levelTable ??= THeroLevel.create();
        var maximum = 0;
        foreach (var pair in levelTable)
            maximum = Math.Max(maximum, pair.Key);
        if (maximum < 1)
            throw new InvalidOperationException("当前游戏的角色等级表为空。");
        return maximum;
    }
    private static TEquip RequireEquipmentRequest(EquipmentEdit edit)
    {
        // 只校验模板、品级、等级三项存在于当前游戏表中；不校验品级是否在随机产出枚举里，
        // 因为游戏存在通过随机流程以外的渠道获得的装备（例如“套装”品级），
        // 它们不在 GetAllowedQualities 的结果中，但仍然是合法品级。
        var templates = TEquip.create();
        if (!templates.ContainsKey(edit.TemplateId))
            throw new InvalidOperationException($"装备模板 {edit.TemplateId} 不存在于当前游戏表中。");
        var template = templates[edit.TemplateId];
        if (!TEquipQuality.create().ContainsKey(edit.Quality))
            throw new InvalidOperationException($"品级 {edit.Quality} 不存在于当前游戏表中。");
        if (!TEquipLevel.create().ContainsKey(edit.Level))
            throw new InvalidOperationException($"装备等级 {edit.Level} 不存在于当前游戏规则表中。");
        return template;
    }
    private static List<int> GetAllowedQualities(
        TEquip template,
        List<RuleOption> qualities,
        Dictionary<string, HashSet<int>>? poolCache = null)
    {
        var result = new List<int>();
        foreach (var quality in qualities)
        {
            try
            {
                // CollectRandomEquipIds 是游戏生成装备时使用的筛选入口，比写死品级映射更耐更新。
                //
                // 但它的语义是「返回 baseQuality == checkQuality 的模板」（GameAssembly.c:1786442，
                // baseQuality 在 TEquip 偏移 +0x28），不是「该模板能产出哪些品级」。
                // 而原生 RandomEquip 抽到 common/fine/rare(<=3) 时会先把 checkQuality 强制成 common
                // （GameAssembly.c:1788070-1788074：`if (v16 <= 3) v16 = 1;`），
                // 也就是说 精良/稀有 实际都由「普通(baseQuality==1)」模板产出。
                // 直接用 quality 去查会得到空集 —— 普通装备因此选不了精良/稀有。
                var checkQuality = quality.Value is >= (int)EItemQualityType.common and <= (int)EItemQualityType.rare
                    ? (int)EItemQualityType.common
                    : quality.Value;
                var cacheKey = $"{checkQuality}:{template.part}:{template.minType}:{template.specialGet}:{template.boxLevel}";
                HashSet<int>? cachedIds = null;
                if (poolCache == null || !poolCache.TryGetValue(cacheKey, out cachedIds))
                {
                    cachedIds = new HashSet<int>();
                    var ids = EquipSys.CollectRandomEquipIds(checkQuality, template.part, template.minType,
                        template.specialGet, template.boxLevel);
                    for (var i = 0; ids != null && i < ids.Count; i++) cachedIds.Add(ids[i]);
                    poolCache?.Add(cacheKey, cachedIds);
                }
                if (cachedIds.Contains(template.id) || quality.Value == template.baseQuality)
                    result.Add(quality.Value);
            }
            catch
            {
                if (quality.Value == template.baseQuality)
                    result.Add(quality.Value);
            }
        }
        result.Sort();
        return result;
    }
    private static HeroData FindHero(LordData lord, int uniqueId)
    {
        for (var i = 0; i < lord.heroFieldList.Count; i++)
        {
            var hero = lord.heroFieldList[i]?.heroData;
            if (hero?.saveHeroData?.uniqueId == uniqueId)
                return hero;
        }
        throw new InvalidOperationException($"角色 {uniqueId} 已不存在，请刷新游戏数据。");
    }
    private static bool ContainsTalent(List<SkillOption> options, int talentId)
    {
        foreach (var option in options) if (option.TalentId == talentId) return true;
        return false;
    }
    // ============================================================
    // 词条取值分类与参考值（需求 2.2 / 2.4）
    //
    // 原生把词条数值分成三族（见 docs/WORKFLOW.md 13.3 / 13.7）：
    //   1) 档位 2/3（精良/稀有）走 CreateEquipmentAffix，
    //      数值 = 档位倍率 × 品质倍率 × 多项式(等级) × 词条池倍率 → 随「装备质量」与「词条等级」变化；
    //   2) 档位 4/5 的专属词条由创建流程直接写入：specialType==1 用有符号特殊随机，否则固定取下端；
    //   3) 档位 8（独有技能词条）用区间均匀随机，等级固定 1。
    //
    // 这里全部调用原生方法取值，而不是自己复刻公式，避免与游戏版本漂移。
    // 注意：这比旧实现（对每条候选词条按 1..affixMaxLevel 逐级调 CalcBodyAttrValueRange）更省，
    // 每条词条只需 1-3 次原生调用。
    // ============================================================
    // 原生 TConstant.mythEquipAffixRate = 150（GameAssembly.c:1509437）。
    // 神话装备的档位 3 词条在创建(:2215733)、解析(:2218817)、提示(:1624117) 三处都会乘 ×1.5，
    // 但 EquipSys.GetEquipAffixPool 不乘，所以桥接必须自己补，否则数值会偏小 1.5 倍。
    private const float MythEquipAffixRate = 150f;
    // 原生 valueType 常量（SaveAffixData.Create 的第 5 个参数，GameAssembly.c:2194353-2194380）。
    // 实测：min=2（固定取下端）。注意 valueType 0 与 1 都是「区间随机」（case 0/1 同体），
    // 固定取下端是 2、固定取上端是 4、有符号特殊随机是 3 —— 见 :2194353 的 switch。
    private const EAffixValueType ValueTypeLow = EAffixValueType.min;
    private const EAffixValueType ValueTypeRandom = EAffixValueType.random;                // 1 区间均匀随机
    private const EAffixValueType ValueTypeSpecialRandom = EAffixValueType.specialRandom;  // 3 有符号特殊随机
    private static readonly EAffixValueType ValueTypeHigh = (EAffixValueType)4;            // 4 取上端
    private struct AffixReference
    {
        internal AffixValueKind Kind;
        internal List<int> GradeValues;
        internal int FixedValue;
        internal int RandomMinimum;
        internal int RandomMaximum;
        internal int Level;          // 实际等级（不再用 -1 表示不适用）
        internal int LevelMinimum;   // 等级可编辑区间；固定档位时与 LevelMaximum 相等
        internal int LevelMaximum;
        internal int ResolvedValue;  // 未勾选强制越界时显示的「应有的值」
    }
    private static float GetEffectiveAffixRate(float poolRate, int equipQuality, int affixQuality) => equipQuality == (int)EItemQualityType.myth && affixQuality == 3
            ? poolRate * MythEquipAffixRate / 100f
            : poolRate;
    private static AffixValueKind ClassifyAffix(TAffix affix, int affixQuality)
    {
        // 通用池的档位 1/2/3 都由 CreateEquipmentAffix 计算：随【装备质量档位(mainAttrGrade)】与【词条等级】变化。
        // 实测依据（游戏内面板，巨骷髅 11401 的 10511/档位1，仅保留档位1+传说）：
        //   装备等级 1/10/50/100（前缀品质=普通）→ 12/12/26/36   （随装备等级 ↑，因为词条等级=affixMaxLevel）
        //   装备等级 100，前缀品质 普通/优质/极品 → 36/43/48      （随 mainAttrGrade ↑）
        // 也就是说档位 1 同样吃 mainAttrGrade —— 早先只把 2/3 归为 GradeBased 是错的，
        // 那会让桥接写入的档位 1 数值与游戏面板不一致。
        if (affixQuality is 1 or 2 or 3)
            return AffixValueKind.GradeBased;
        // 档位 8（独有技能词条）用 valueType 1：区间均匀随机。
        if (affixQuality == 8)
            return AffixValueKind.RandomRange;
        // 其余是传说/神话专属：只有「神话(装备品质 5) 且 specialType == 1」才走有符号特殊随机(3)，
        // 传说(4) 与神话里 specialType != 1 的行是 valueType 2 = **固定取下端**（round(v28×rate)，:2194361）。
        // 实测依据（[AffixProbe] 取值探针，18491/档位4/等级6）：
        //   vt2(min)=[2,2,2] 恒定、vt1(random)=[2,2,3] 会波动、vt4(max)=[3,3,3]，
        //   而游戏内每次生成恒为 2 → 真实路径是 vt2（固定取下端），不是区间随机。
        return affixQuality == (int)EItemQualityType.myth && affix.specialType == 1
            ? AffixValueKind.RandomRange
            : AffixValueKind.Fixed;
    }
    // 该档位在原生创建流程里被钉死的等级；返回 0 表示由泵随机（档位 2/3）。
    private static int GetPinnedAffixLevel(int affixQuality, int maximumAffixLevel) => affixQuality switch
    {
        8 => 1,                 // :1623232 固定 level = 1
        2 or 3 => 0,            // :2215386 / :2219193 由泵随机
        _ => maximumAffixLevel  // :1623349 / :2215564 固定 affixMaxLevel
    };
    private static int NativeCreateValue(int id, int quality, int level, float rate, EAffixValueType valueType) =>
        SaveAffixData.Create(id, quality, level, rate, valueType)?.value ?? 0;
    private static int NativeEquipmentAffixValue(int id, int quality, int level, float rate, int equipGrade) =>
        SaveAffixData.CreateEquipmentAffix(id, quality, level, rate, equipGrade, false, true)?.value ?? 0;
    // 与写入路径一致的取值：创建 1 级后逐级 SaveAffixData.LevelUp() 到目标等级（参考数值列必须用这个，
    // 否则界面显示的是"直接按目标等级创建"的值，而存档里是"逐级升级缩放"的值，会出现 1~几点的舍入差）。
    private static int NativeEquipmentAffixValueByLevelUp(int id, int quality, int level, float rate, int equipGrade) =>
        CreateAffixByNativeLevelUp(id, quality, level, rate, equipGrade, logLevelUp: false)?.value ?? 0;
    // 污染词缀走的是同一个原生创建入口，只是 isCorrupt = true（ItemEquipData.TryAddCorruptRareAffix，
    // GameAssembly.c（1.1.4）:1618301 调 CreateEquipmentAffix(..., isCorrupt:1, applyQualityValueRate:1)）。
    // 用它对比就能验证「污染词缀强度是否受装备质量档位影响」。
    private static int NativeCorruptAffixValue(int id, int quality, int level, float rate, int equipGrade) =>
        SaveAffixData.CreateEquipmentAffix(id, quality, level, rate, equipGrade, true, true)?.value ?? 0;
    private static AffixReference ComputeAffixReference(
        TAffix affix,
        int affixId,
        int affixQuality,
        float poolRate,
        int equipQuality,
        int mainAttrGrade,
        int maximumAffixLevel,
        int requestedLevel)
    {
        var reference = new AffixReference
        {
            Kind = ClassifyAffix(affix, affixQuality),
            GradeValues = new List<int>()
        };
        var rate = GetEffectiveAffixRate(poolRate, equipQuality, affixQuality);
        switch (reference.Kind)
        {
            case AffixValueKind.GradeBased:
                {
                    // 精良(2)/稀有(3)：原生 level = RandomRangeInt(1, affixMaxLevel + 1)（:2215386、:2215760）。
                    // 普通(1)：等级由装备等级唯一确定（= affixMaxLevel），游戏内没有随机空间 ——
                    // 所以钉死上下界使其不可编辑；要越界改数值请直接改数值（走「强制越界」的守卫路径）。
                    var gradeLevelIsFixed = affixQuality == 1;
                    reference.LevelMinimum = gradeLevelIsFixed ? Math.Max(1, maximumAffixLevel) : 1;
                    reference.LevelMaximum = Math.Max(1, maximumAffixLevel);
                    reference.Level = Math.Clamp(
                        requestedLevel <= 0 ? maximumAffixLevel : requestedLevel, 1, Math.Max(1, maximumAffixLevel));
                    reference.GradeValues.Add(NativeEquipmentAffixValueByLevelUp(affixId, affixQuality, reference.Level, rate, 1));
                    reference.GradeValues.Add(NativeEquipmentAffixValueByLevelUp(affixId, affixQuality, reference.Level, rate, 2));
                    reference.GradeValues.Add(NativeEquipmentAffixValueByLevelUp(affixId, affixQuality, reference.Level, rate, 3));
                    // 未勾选强制越界时显示的「应有的值」= 当前装备质量档位对应的值。
                    reference.ResolvedValue = reference.GradeValues[Math.Clamp(mainAttrGrade, 1, 3) - 1];
                    break;
                }
            case AffixValueKind.RandomRange:
                {
                    // 独特(8) 固定为 1；神话专属(5) 固定为 affixMaxLevel（原生 :2215560 直接把该字段当等级）。
                    // v23 = L²·lr0 + L·lr1 + lr2：level 为 0 时只剩 lr2，很容易整条算成 0，所以至少取 1。
                    var level = Math.Max(1, GetPinnedAffixLevel(affixQuality, maximumAffixLevel));
                    reference.Level = level;
                    reference.LevelMinimum = level;
                    reference.LevelMaximum = level;
                    if (affixQuality == 8)
                    {
                        // valueType 1：value = round(Random.Range(下端, 上端) × rate)（:2194358）
                        reference.RandomMinimum = NativeCreateValue(affixId, affixQuality, level, rate, ValueTypeLow);
                        reference.RandomMaximum = NativeCreateValue(affixId, affixQuality, level, rate, ValueTypeHigh);
                    }
                    else
                    {
                        // valueType 3（:2194365-2194375）：
                        //   base  = round(v28)                     ← v28 与 rate 无关，所以要用 rate=1 取回 base
                        //   n     = RandomRangeInt(base / -2, base + 1)
                        //   value = round(n × rate)，为 0 时回退 ±base/2
                        // 原来直接拿 valueType 0（= round(v28 × rate)）当上界，等于把 rate 乘了两次。
                        var baseValue = NativeCreateValue(affixId, affixQuality, level, 1f, ValueTypeLow);
                        reference.RandomMaximum = (int)Math.Round(baseValue * rate, MidpointRounding.AwayFromZero);
                        reference.RandomMinimum = (int)Math.Round(baseValue / -2.0 * rate, MidpointRounding.AwayFromZero);
                        // 数值列显示 ResolvedValue：随机档位原来没赋值 → 恒定显示 0。
                        // 新生成的随机词条没有唯一确定值，用区间上界作为展示基准（参考列已给出完整区间）。
                        reference.ResolvedValue = reference.RandomMaximum;
                    }
                    return reference;
                }
            default:
                {
                    // 传说(4)/普通(1) 等：原生同样把 affixMaxLevel 当等级用（:1623346）。
                    var level = Math.Max(1, GetPinnedAffixLevel(affixQuality, maximumAffixLevel));
                    reference.Level = level;
                    reference.LevelMinimum = level;
                    reference.LevelMaximum = level;
                    reference.FixedValue = NativeCreateValue(affixId, affixQuality, level, rate, ValueTypeLow);
                    // 数值列显示的就是 ResolvedValue：固定值档位必须一起赋值，
                    // 否则客户端拿到的 ResolvedValue 是 0 → 普通/传说词条一律显示 0。
                    reference.ResolvedValue = reference.FixedValue;
                    break;
                }
        }
        return reference;
    }
    private static void ApplyReference(AffixEdit target, in AffixReference reference)
    {
        target.ValueKind = reference.Kind;
        target.GradeValues = reference.GradeValues ?? new List<int>();
        target.FixedValue = reference.FixedValue;
        target.RandomMinimum = reference.RandomMinimum;
        target.RandomMaximum = reference.RandomMaximum;
        target.Level = reference.Level;
        target.ResolvedValue = reference.ResolvedValue;
        target.LevelMinimum = reference.LevelMinimum;
        target.LevelMaximum = reference.LevelMaximum;
    }
    private static void ApplyReference(AffixOption target, in AffixReference reference)
    {
        target.ValueKind = reference.Kind;
        target.GradeValues = reference.GradeValues ?? new List<int>();
        target.FixedValue = reference.FixedValue;
        target.RandomMinimum = reference.RandomMinimum;
        target.RandomMaximum = reference.RandomMaximum;
        target.LevelMinimum = reference.LevelMinimum;
        target.LevelMaximum = reference.LevelMaximum;
    }
    private static Dictionary<(int Id, int Quality), SAffixInfo> BuildAffixPoolMap(
        TEquip template,
        int equipmentLevel,
        IEnumerable<int> affixQualities)
    {
        var result = new Dictionary<(int Id, int Quality), SAffixInfo>();
        foreach (var affixQuality in affixQualities)
        {
            var pool = EquipSys.GetEquipAffixPool(template.id, template.part, affixQuality, equipmentLevel);
            for (var i = 0; pool != null && i < pool.Count; i++)
                result[(pool[i].id, affixQuality)] = pool[i];
        }
        return result;
    }
    private static TEquip ResolveGeneratedEquipmentTemplate(SaveItemData saveItem, TEquip fallback)
    {
        var equipmentTable = TEquip.create();
        if (saveItem.quality == (int)EItemQualityType.myth && fallback.upMyth > 0 &&
            equipmentTable.ContainsKey(fallback.upMyth))
            return equipmentTable[fallback.upMyth];
        return saveItem.id > 0 && equipmentTable.ContainsKey(saveItem.id)
            ? equipmentTable[saveItem.id]
            : fallback;
    }
    // 把额度按「档位 2/3 的实际池子条数」裁剪：原生抽词条时会做同样的裁剪
    // （GameAssembly.c 的 AddRareAffix：`if (v18 >= pool.count) v18 = pool.count`，AddFineAffix 同理），
    // 池子里条目不够时游戏本身也出不了那么多条。
    // 只在真的查到条目（poolCount > 0）时裁剪：查不到可能是调用失败，不能把额度误清零。
    private static void ClampAffixLimitsByPool(
        EquipmentRules rules,
        Dictionary<(int Id, int Quality), SAffixInfo> pool)
    {
        var poolCounts = new Dictionary<int, int>();
        foreach (var key in pool.Keys)
        {
            poolCounts.TryGetValue(key.Quality, out var count);
            poolCounts[key.Quality] = count + 1;
        }
        var clamped = false;
        foreach (var quality in new List<int>(rules.AffixQualityLimits.Keys))
        {
            if (quality != (int)EItemQualityType.fine && quality != (int)EItemQualityType.rare)
                continue;
            if (!poolCounts.TryGetValue(quality, out var poolCount) || poolCount <= 0)
                continue;
            if (rules.AffixQualityLimits[quality] <= poolCount)
                continue;
            rules.AffixQualityLimits[quality] = poolCount;
            // 上限被池子压下来时，下限不能高于上限。
            if (rules.AffixQualityMinimums.TryGetValue(quality, out var minimum) && minimum > poolCount)
                rules.AffixQualityMinimums[quality] = poolCount;
            clamped = true;
        }
        if (!clamped)
            return;
        rules.MaximumAffixCount = 0;
        foreach (var limit in rules.AffixQualityLimits.Values)
            rules.MaximumAffixCount += limit;
    }
    // 诊断（需开启详细日志）：把「品级表字段 / 模板字段 / 实际池子条数」三者列出来，
    // 用来确认某个品级的词条上限到底是多少 —— 原生上限 = min(TEquip.legendAffixCount, 档位3池条数)。
    internal static string BuildTemplateReport()
    {
        var parts = new List<string>();
        try
        {
            var qualityText = new List<string>();
            foreach (var pair in TEquipQuality.create())
            {
                var quality = pair.Value;
                if (quality == null)
                    continue;
                qualityText.Add($"{pair.Key}({quality.name}) fine={quality.fineAffixCount} rare={quality.rareAffixCount} " +
                                $"孔位表[{DescribeSlotCountTable(quality.slotCountArr)}] 锻造[{DescribeSlotCountTable(quality.forgeSlotCountArr)}]");
            }
            qualityText.Sort(StringComparer.Ordinal);
            parts.Add("TEquipQuality[" + string.Join(" ", qualityText) + "]");
            // 开孔门槛：TEquipLevel[id] >= TConstant.equipSlotLevel 才可能开出孔（SaveItemData.EquipRandomSlot 的入口判定）。
            parts.Add($"装备开孔门槛 equipSlotLevel={TConstant.equipSlotLevel}");
        }
        catch (Exception exception)
        {
            parts.Add($"TEquipQuality 读取失败: {exception.Message}");
        }
        var legendByPart = new Dictionary<int, int>();
        var representative = new Dictionary<int, TEquip>();
        var histogram = new SortedDictionary<int, int>();
        var baseAffixTemplates = 0;
        var uniqueAffixTemplates = 0;
        var templateTotal = 0;
        try
        {
            foreach (var pair in TEquip.create())
            {
                var template = pair.Value;
                if (template == null)
                    continue;
                templateTotal++;
                var legend = template.legendAffixCount;
                histogram.TryGetValue(legend, out var seen);
                histogram[legend] = seen + 1;
                if (template.baseAffix != null && template.baseAffix.Length >= 2)
                    baseAffixTemplates++;
                if (template.uniqueSkillAffix > 0)
                    uniqueAffixTemplates++;
                if (!legendByPart.TryGetValue(template.part, out var partMax) || legend > partMax)
                    legendByPart[template.part] = legend;
                if (!representative.TryGetValue(template.part, out var current) ||
                    legend > current.legendAffixCount)
                    representative[template.part] = template;
            }
            var histogramText = new List<string>();
            foreach (var pair in histogram)
                histogramText.Add($"{pair.Key}×{pair.Value}");
            parts.Add($"TEquip 共 {templateTotal} 件；legendAffixCount 分布 {string.Join(", ", histogramText)}");
            parts.Add($"baseAffix 非空 {baseAffixTemplates} 件；uniqueSkillAffix>0 {uniqueAffixTemplates} 件");
        }
        catch (Exception exception)
        {
            parts.Add($"TEquip 读取失败: {exception.Message}");
        }
        try
        {
            var saveMapData = Game.dataMgr?.nowSeasonData?.advData?.mapData?.saveMapData;
            var doomStage = saveMapData?.doomStage ?? 0;
            var allowance = GetCorruptAllowance();
            var stageText = new List<string>();
            foreach (var pair in TDoomStage.create())
            {
                var stage = pair.Value;
                if (stage == null)
                    continue;
                stageText.Add($"{pair.Key}:腐蚀{stage.equipCorruptCount}/锁定{stage.affixLockCount}/" +
                              $"污染词缀等级[{DescribeIntArray(stage.equipAffixLevelArr)}]");
            }
            // 当前存档实际可用的污染词缀等级（阶段表缺失时原生用静态兜底数组）。
            var currentLevels = saveMapData != null
                ? DescribeIntArray(MapDoomData.GetCurrentCorruptAffixLevels(saveMapData))
                : "-";
            var fallbackLevels = DescribeIntArray(MapDoomData.FallbackCorruptAffixLevels);
            parts.Add($"终焉阶段 doomStage={doomStage}；单件可腐蚀次数上限={allowance}；" +
                      $"当前污染词缀等级=[{currentLevels}]（兜底=[{fallbackLevels}]）；" +
                      $"TDoomStage[{string.Join(" ", stageText)}]");
        }
        catch (Exception exception)
        {
            parts.Add($"终焉进度读取失败: {exception.Message}");
        }
        // 腐蚀结果表：用来核对"每次腐蚀到底加什么"（文档说按品质抽结果池，代码里只看到加 1 条档位3词条）。
        try
        {
            var affixTable = TAffix.create();
            var byIndex = new SortedDictionary<int, int>();
            var effectTypes = new SortedDictionary<int, int>();
            var corruptRows = 0;
            foreach (var pair in TEquipCorrupt.create())
            {
                var corrupt = pair.Value;
                if (corrupt == null)
                    continue;
                corruptRows++;
                byIndex.TryGetValue(corrupt.index, out var indexCount);
                byIndex[corrupt.index] = indexCount + 1;
                var effectType = affixTable.ContainsKey(corrupt.id) && affixTable[corrupt.id] != null
                    ? affixTable[corrupt.id].effectType
                    : -1;
                effectTypes.TryGetValue(effectType, out var effectCount);
                effectTypes[effectType] = effectCount + 1;
            }
            var indexText = new List<string>();
            foreach (var pair in byIndex)
                indexText.Add($"{pair.Key}×{pair.Value}");
            var effectText = new List<string>();
            foreach (var pair in effectTypes)
                effectText.Add($"eff{pair.Key}×{pair.Value}");
            parts.Add($"TEquipCorrupt 共 {corruptRows} 行；按 index 分组 {string.Join(", ", indexText)}；" +
                      $"id 的 TAffix.effectType 分布 {string.Join(", ", effectText)}");
        }
        catch (Exception exception)
        {
            parts.Add($"TEquipCorrupt 读取失败: {exception.Message}");
        }
        // ── 监牢扭曲：每次消耗的血肉与升品期望 ──
        // 价格（PriDistortData.GetDistortPrice，1.1.5 :1934003）：
        //   base  = round(THeroLevel[等级].distortPrice × THeroQuality[品质].distortPriceRate)
        //   price = round(base × (100 − 房屋 10241「扭曲价格降低%」) / 100)
        // 成功率（GetDistortQualityUpRate，:1934059）：
        //   rate = clamp((房屋 10240「扭曲成功率+%」+100) × THeroQuality[品质].distortQualityUpRate / 100, 0, 100)
        // 期望（每次尝试都先扣血肉再判定，几何分布）：E[次数] = 100/rate；E[血肉] = price × E[次数]
        try
        {
            var heroQualityTable = THeroQuality.create();
            var heroLevelTable = THeroLevel.create();
            var heroLevels = new List<int>();
            foreach (var pair in heroLevelTable)
                heroLevels.Add(pair.Key);
            heroLevels.Sort();
            var sampleLevels = new List<int>();
            if (heroLevels.Count > 0)
            {
                sampleLevels.Add(heroLevels[0]);
                if (heroLevels.Count > 2)
                    sampleLevels.Add(heroLevels[heroLevels.Count / 2]);
                if (heroLevels.Count > 1)
                    sampleLevels.Add(heroLevels[^1]);
            }
            var rateBonus = 0f;
            var priceDown = 0f;
            var prisonMaxQuality = 0f;
            try
            {
                var houseDic = Game.dataMgr?.nowSeasonData?.townData?.houseDic;
                if (houseDic != null && houseDic.ContainsKey(EHouseType.Prison))
                {
                    var houseAttrs = houseDic[EHouseType.Prison]?.houseAttrData;
                    if (houseAttrs != null)
                    {
                        // 10240 = prisonDistortQualityUpRate（成功率 +%）
                        // 10241 = 扭曲价格降低%（不在 EHouseAttrType 枚举里，按表 id 直取）
                        // 10250 = prisonHeroMaxQuality（监牢允许的最高囚犯品质）
                        rateBonus = houseAttrs.GetAttrValue((EHouseAttrType)10240, null);
                        priceDown = houseAttrs.GetAttrValue((EHouseAttrType)10241, null);
                        prisonMaxQuality = houseAttrs.GetAttrValue((EHouseAttrType)10250, null);
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Verbose($"[Distort] 房屋属性读取失败，按 0 加成计算：{exception.Message}");
            }
            var qualityLines = new List<string>();
            foreach (var pair in heroQualityTable)
            {
                var quality = pair.Value;
                if (quality == null)
                    continue;
                var rate = Math.Clamp((rateBonus + 100f) * quality.distortQualityUpRate / 100f, 0f, 100f);
                var cells = new List<string>();
                foreach (var level in sampleLevels)
                {
                    var basePrice = heroLevelTable.ContainsKey(level) && heroLevelTable[level] != null
                        ? heroLevelTable[level].distortPrice
                        : 0;
                    var price = Math.Round(basePrice * quality.distortPriceRate * (100f - priceDown) / 100f);
                    var attempts = rate > 0f ? 100.0 / rate : double.PositiveInfinity;
                    var blood = rate > 0f ? price * attempts : double.PositiveInfinity;
                    cells.Add($"L{level} 价格{price:0} E次数{attempts:0.0} E血肉{blood:0}");
                }
                qualityLines.Add($"{pair.Key}({quality.name}) 成功率{rate:0.##}% 价格率{quality.distortPriceRate:0.###} → " +
                                 string.Join("；", cells));
            }
            parts.Add($"监牢扭曲（房屋：成功率+{rateBonus:0.##}% 价格-{priceDown:0.##}% 监牢最高品质={prisonMaxQuality:0}）：" +
                      string.Join(" | ", qualityLines));
        }
        catch (Exception exception)
        {
            parts.Add($"监牢扭曲表读取失败: {exception.Message}");
        }
        try
        {
            var levels = new List<int>();
            foreach (var pair in TEquipLevel.create())
                levels.Add(pair.Key);
            levels.Sort();
            var sample = new List<int>();
            if (levels.Count > 0)
            {
                sample.Add(levels[0]);
                if (levels.Count > 2)
                    sample.Add(levels[levels.Count / 2]);
                if (levels.Count > 1)
                    sample.Add(levels[levels.Count - 1]);
            }
            var lines = new List<string>();
            var partKeys = new List<int>(representative.Keys);
            partKeys.Sort();
            foreach (var part in partKeys)
            {
                var template = representative[part];
                var cells = new List<string>();
                foreach (var level in sample)
                {
                    var finePool = CountEquipAffixPool(template, (int)EItemQualityType.fine, level);
                    var rarePool = CountEquipAffixPool(template, (int)EItemQualityType.rare, level);
                    cells.Add($"L{level} 精良池={finePool} 稀有池={rarePool}");
                }
                var legend = template.legendAffixCount;
                lines.Add($"part={part} id={template.id} legend={legend}（部位内最大）→ {string.Join("；", cells)}；" +
                          $"稀有实际上限=min(legend,池)");
            }
            parts.Add("池子抽样（每部位取 legendAffixCount 最大的一件）：" + string.Join(" | ", lines));
        }
        catch (Exception exception)
        {
            parts.Add($"池子抽样失败: {exception.Message}");
        }
        return string.Join(" | ", parts);
    }
    // 返回 -1 表示查询失败（与「池子为空(0)」区分开）。
    private static int CountEquipAffixPool(TEquip template, int affixQuality, int level)
    {
        try
        {
            var pool = EquipSys.GetEquipAffixPool(template.id, template.part, affixQuality, level);
            return pool?.Count ?? 0;
        }
        catch
        {
            return -1;
        }
    }
    // 把两张表原样导出成 TSV（诊断用）：品级表 + 装备模板表。
    // 目的是离线核对「某件装备的 legendAffixCount / baseAffix / uniqueSkillAffix」到底是多少 ——
    // 这些是运行时表数据，本地文件里搜不到（见 decompile/README.md 的说明）。
    internal static int WriteTemplateDump(string path)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("#TEquipQuality\tid\tname\tfineAffixCount\trareAffixCount\tslotCountArr\tforgeSlotCountArr");
        foreach (var pair in TEquipQuality.create())
        {
            var quality = pair.Value;
            builder.AppendLine($"TEquipQuality\t{pair.Key}\t{quality?.name}\t{quality?.fineAffixCount}\t{quality?.rareAffixCount}\t" +
                               $"{DescribeSlotCountTable(quality?.slotCountArr)}\t{DescribeSlotCountTable(quality?.forgeSlotCountArr)}");
        }
        // 装备等级表：开孔/镶嵌相关的价格与"孔位符文品质"都在这里（TEquipLevel.inlayRuneQuality）。
        builder.AppendLine("#TEquipLevel\tid\taffixMaxLevel\twearLevel\tinlayRuneQuality\tinlayPrice\tforgePrice\tcorruptPrice\trefineRuneQuality\trefineRuneCount");
        foreach (var pair in TEquipLevel.create())
        {
            var level = pair.Value;
            if (level == null)
                continue;
            builder.AppendLine($"TEquipLevel\t{pair.Key}\t{level.affixMaxLevel}\t{level.wearLevel}\t{level.inlayRuneQuality}\t" +
                               $"{level.inlayPrice}\t{level.forgePrice}\t{level.corruptPrice}\t{level.refineRuneQuality}\t{level.refineRuneCount}");
        }
        builder.AppendLine("#TEquip\tid\tname\tpart\tbaseQuality\tlegendAffixCount\tbaseAffixId\tbaseAffixRate\tuniqueSkillAffix");
        // 终焉阶段表：单件装备的可腐蚀次数 / 可锁定词条数上限按阶段提升。
        builder.AppendLine("#TDoomStage\tid\tname\tequipCorruptCount\taffixLockCount\tequipAffixLevelArr\tsiteCount\theroMaxLevel\tequipMaxLevel");
        foreach (var pair in TDoomStage.create())
        {
            var stage = pair.Value;
            if (stage == null)
                continue;
            builder.AppendLine($"TDoomStage\t{pair.Key}\t{stage.name}\t{stage.equipCorruptCount}\t{stage.affixLockCount}\t" +
                               $"{DescribeIntArray(stage.equipAffixLevelArr)}\t{stage.siteCount}\t{stage.heroMaxLevel}\t{stage.equipMaxLevel}");
        }
        var rows = 0;
        foreach (var pair in TEquip.create())
        {
            var template = pair.Value;
            if (template == null)
                continue;
            rows++;
            var baseAffixId = 0;
            var baseAffixRate = 0f;
            try
            {
                if (template.baseAffix != null && template.baseAffix.Length >= 2)
                {
                    baseAffixId = template.baseAffix[0];
                    baseAffixRate = template.baseAffix[1];
                }
            }
            catch
            {
                // 单条数据异常不影响整体导出。
            }
            builder.AppendLine($"TEquip\t{template.id}\t{template.name}\t{template.part}\t{template.baseQuality}\t" +
                               $"{template.legendAffixCount}\t{baseAffixId}\t{baseAffixRate}\t{template.uniqueSkillAffix}");
        }
        // 圣碑腐蚀的「结果」描述表：id 指向 TAffix，index 疑似按装备品质分组（用于核对腐蚀结果池）。
        var affixTable = TAffix.create();
        builder.AppendLine("#TEquipCorrupt\tid\tindex\tdes\teffectType\tspecialType\taffixName");
        foreach (var pair in TEquipCorrupt.create())
        {
            var corrupt = pair.Value;
            if (corrupt == null)
                continue;
            var affix = affixTable.ContainsKey(corrupt.id) ? affixTable[corrupt.id] : null;
            builder.AppendLine($"TEquipCorrupt\t{corrupt.id}\t{corrupt.index}\t{corrupt.des}\t" +
                               $"{(affix != null ? affix.effectType : -1)}\t{(affix != null ? affix.specialType : -1)}\t" +
                               $"{(affix != null ? GetAffixName(affix) : "")}");
        }
        System.IO.File.WriteAllText(path, builder.ToString());
        return rows;
    }
    // 监牢扭曲相关表：每次扭曲的血肉价格 = THeroLevel.distortPrice × THeroQuality.distortPriceRate（再打折），
    // 升品成功率 = THeroQuality.distortQualityUpRate（再加成）。单独导出便于离线算期望。
    internal static int WriteDistortTableDump(string path)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("#THeroQuality\tid\tname\tdistortPriceRate\tdistortQualityUpRate\tdeludePriceRate\ttalentPoint\tmainAttrTotal\tbaseAttrTotal\tequipRate");
        var qualityRows = 0;
        foreach (var pair in THeroQuality.create())
        {
            var quality = pair.Value;
            if (quality == null)
                continue;
            qualityRows++;
            builder.AppendLine($"THeroQuality\t{pair.Key}\t{quality.name}\t{quality.distortPriceRate}\t" +
                               $"{quality.distortQualityUpRate}\t{quality.deludePriceRate}\t{quality.talentPoint}\t" +
                               $"{quality.mainAttrTotal}\t{quality.baseAttrTotal}\t{quality.equipRate}");
        }
        builder.AppendLine("#THeroLevel\tid\tdistortPrice\tdeludePrice\ttorturePrice\tslayBone\tinspBlood\twashBlood\timbueBlood\tmythCount");
        var levelRows = 0;
        foreach (var pair in THeroLevel.create())
        {
            var level = pair.Value;
            if (level == null)
                continue;
            levelRows++;
            builder.AppendLine($"THeroLevel\t{pair.Key}\t{level.distortPrice}\t{level.deludePrice}\t{level.torturePrice}\t" +
                               $"{level.slayBone}\t{level.inspBlood}\t{level.washBlood}\t{level.imbueBlood}\t{level.mythCount}");
        }
        // 当前的房屋加成（监狱）：成功率 +%、价格降低%、监牢最高品质。
        try
        {
            var houseDic = Game.dataMgr?.nowSeasonData?.townData?.houseDic;
            if (houseDic != null && houseDic.ContainsKey(EHouseType.Prison))
            {
                var houseAttrs = houseDic[EHouseType.Prison]?.houseAttrData;
                if (houseAttrs != null)
                {
                    builder.AppendLine($"#PrisonDistortAttrs\tqualityUpRateBonus={houseAttrs.GetAttrValue((EHouseAttrType)10240, null)}\t" +
                                       $"priceDown={houseAttrs.GetAttrValue((EHouseAttrType)10241, null)}\t" +
                                       $"maxPrisonQuality={houseAttrs.GetAttrValue((EHouseAttrType)10250, null)}");
                }
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine($"#PrisonDistortAttrs\t读取失败: {exception.Message}");
        }
        System.IO.File.WriteAllText(path, builder.ToString());
        return qualityRows + levelRows;
    }
    // TEquip.baseAffix = [词条 id, 倍率]：原生在装备初始化时就用它建一条 quality = 1 的词条写进
    // SaveItemData.affixList（GameAssembly.c:2219170-2219197，data[0] 是词条 id、data[1] 是倍率），
    // 并且 HasUpgradeableBaseAffix / BaseAffixLevelUp（锻造「基础词条升级」，WorkForgeData:2189795）
    // 也是靠「affixList 里 quality == 1」来认它（:2219035 / :2216000）。
    // 所以档位 1 的那行属于装备自带，客户端不应删除。
    // 把规则里的默认词条按 AffixQualityLimits 补到上限（同档位内不重复、不超过额度）。
    // 原生创建装备时对品级 3/6/8 的稀有条数做随机（GameAssembly.c:2215740：RandomRangeInt(2, rareAffixCount + 1)），
    // 编辑器按「统一取最高」给满；用户仍可自行删除或替换这些行。
    // 只补档位 2 精良 / 3 稀有（来源是部位随机池）；档位 1 是模板固定行，
    // 档位 4/5/8 由模板（legendAffixArr / uniqueSkillAffix）或运行时决定，不在这里补。
    private static void TopUpGeneratedAffixes(
        EquipmentRules rules,
        Dictionary<(int Id, int Quality), SAffixInfo> pool,
        Il2CppSystem.Collections.Generic.Dictionary<int, TAffix> affixTable,
        Il2CppSystem.Collections.Generic.Dictionary<int, TAffixQuality> affixQualityTable,
        int corruptAllowance)
    {
        var countsByQuality = new Dictionary<int, int>();
        var usedKeys = new HashSet<(int Id, int Quality)>();
        foreach (var generated in rules.GeneratedAffixes)
        {
            countsByQuality.TryGetValue(generated.Quality, out var count);
            countsByQuality[generated.Quality] = count + 1;
            usedKeys.Add((generated.Id, generated.Quality));
        }
        foreach (var limitPair in rules.AffixQualityLimits)
        {
            var quality = limitPair.Key;
            if (quality != (int)EItemQualityType.fine && quality != (int)EItemQualityType.rare)
                continue;
            // 额度里可能含「圣碑腐蚀」能追加的份数；默认行只补到生成上限，
            // 腐蚀那部分留给玩家在编辑器里手动加（生成流程本身产不出那么多）。
            var generationLimit = limitPair.Value;
            if (quality == (int)EItemQualityType.rare)
                generationLimit = Math.Max(0, generationLimit - corruptAllowance);
            countsByQuality.TryGetValue(quality, out var currentCount);
            var missing = generationLimit - currentCount;
            if (missing <= 0)
                continue;
            // 按 Id 排序，保证同一组合每次给出相同的默认行。
            var candidates = new List<(int Id, int Quality)>();
            foreach (var key in pool.Keys)
                if (key.Quality == quality)
                    candidates.Add(key);
            candidates.Sort((a, b) => a.Id.CompareTo(b.Id));
            var qualityName = affixQualityTable.ContainsKey(quality)
                ? affixQualityTable[quality].name
                : $"词条档位 {quality}";
            foreach (var key in candidates)
            {
                if (missing <= 0)
                    break;
                var affix = affixTable.ContainsKey(key.Id) ? affixTable[key.Id] : null;
                if (affix == null || !usedKeys.Add(key))
                    continue;
                rules.AffixQualityNames[quality] = qualityName;
                rules.GeneratedAffixes.Add(new AffixEdit
                {
                    Id = key.Id,
                    Quality = quality,
                    QualityName = qualityName,
                    Name = GetAffixName(affix),
                    Level = rules.MaximumAffixLevel,
                    Value = null
                });
                missing--;
            }
        }
    }
    // 读取孔位，转成「镶嵌组」的词条行（ValueKind == SlotType）：
    //   Id = 已镶嵌符文的 runeId（没镶嵌为 0）、Name = 符文名字、Level = 符文品质（存档里没有单独的等级字段）、
    //   SocketType = 孔位类型码（0 随机 / 1 攻击 / 2 防御）。
    private static List<AffixEdit> BuildSocketAffixRows(SaveItemData saveItem, string groupLabel)
    {
        var result = new List<AffixEdit>();
        try
        {
            var slots = saveItem?.slotRuneList;
            if (slots == null)
                return result;
            var runeTable = TRune.create();
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot == null)
                    continue;
                var runeId = slot.runeId > 0 ? slot.runeId : 0;
                result.Add(new AffixEdit
                {
                    Id = runeId,
                    Name = runeId > 0 && runeTable.ContainsKey(runeId) ? runeTable[runeId].name : "",
                    Quality = 0,
                    QualityName = groupLabel,
                    Level = runeId > 0 ? Math.Max(0, slot.quality) : 0,
                    ValueKind = AffixValueKind.SlotType,
                    SocketType = (int)slot.slotType,
                    // 孔位行可以删除（删掉 = 少开一个孔），不属于「装备自带」的固定行。
                    EquipmentFixed = false
                });
            }
        }
        catch (Exception exception)
        {
            Plugin.Verbose($"[SocketRuneRead] 读取孔位失败：{exception.Message}");
        }
        return result;
    }
    // 旧版（1.1.0 重做前）污染残留的文案：SaveItemData.GetCorruptInfoText() 会用 TEquipCorrupt.des
    // 把 corruptIdList 里的结果码渲染成「装备等级提升5级 / 增加1个稀有词缀 …」这类说明。
    // 1.1.4 里这套结果池已经不再抽签（表只被这个提示方法读取），残留效果只能用【涤魂泪滴】清除。
    private static string ReadLegacyCorruptText(SaveItemData saveItem)
    {
        try
        {
            if (saveItem == null || saveItem.GetCorruptIdList() == null || saveItem.GetCorruptIdList().Count == 0)
                return "";
            return saveItem.GetCorruptInfoText() ?? "";
        }
        catch
        {
            // 老存档或字段缺失时静默忽略：这只是提示信息，不影响编辑流程。
            return "";
        }
    }
    // 渲染开孔表：每行 {孔数, 权重} → "1(权重60),2(权重30),3(权重10)"；读不到时带上诊断原因。
    private static string DescribeSlotCountTable(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? value)
    {
        try
        {
            var rows = ReadSlotCountTable(value, out var diagnostic);
            if (rows.Count == 0)
                return $"-（{diagnostic}）";
            var parts = new List<string>();
            foreach (var (count, weight) in rows)
                parts.Add($"{count}(权重{weight})");
            return string.Join(",", parts);
        }
        catch (Exception exception)
        {
            return $"-（失败：{exception.Message}）";
        }
    }
    // 把一个 Il2CppStructArray<int> 渲染成 "1,3,5" 形式（读取失败返回 "-"）。
    private static string DescribeIntArray(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>? values)
    {
        try
        {
            if (values == null || values.Length == 0)
                return "-";
            var parts = new List<string>();
            for (var i = 0; i < values.Length; i++)
                parts.Add(values[i].ToString());
            return string.Join(",", parts);
        }
        catch
        {
            return "-";
        }
    }
    // TEquipQuality.slotCountArr / forgeSlotCountArr 的真实结构是**二维 int 数组**：
    //   每一行 = { 孔数候选, 权重 }
    // 依据原生 SaveItemData.EquipRandomSlot（GameAssembly.c（1.1.4）:2215880）：
    //   第一遍取 [row][0] 的最大值，第二遍对每行 AddEntry([row][0], 权重 = [row][1])，
    //   最后 GetRandom() 得到 slotCount；maxSlotWeightUp > 0 时会把"最大值那一行"的权重放大 (100+up)/100。
    // 所以**上限 = 第 0 列的最大值** —— 把整张表拍平取最大值会把权重（100 之类）当成孔数。
    private static List<(int Count, int Weight)> ReadSlotCountTable(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? value,
        out string diagnostic)
    {
        var rows = new List<(int Count, int Weight)>();
        diagnostic = "null";
        if (value == null)
            return rows;
        // 1) 通用数组接口（与 ProbeAffixValueTypes 读 legendAffixArr 用的是同一套 API）。
        try
        {
            var array = value.TryCast<Il2CppSystem.Array>();
            if (array != null)
            {
                var rowCount = array.GetLength(0);
                var columnCount = 1;
                try
                {
                    columnCount = array.GetLength(1);
                }
                catch
                {
                    // 一维表：只有孔数、没有权重。
                }
                for (var row = 0; row < rowCount; row++)
                {
                    var count = array.GetValue(row, 0).Unbox<int>();
                    var weight = columnCount > 1 ? array.GetValue(row, 1).Unbox<int>() : 0;
                    rows.Add((count, weight));
                }
                diagnostic = $"array[{rowCount}×{columnCount}]";
                return rows;
            }
        }
        catch (Exception exception)
        {
            diagnostic = $"array 失败({exception.Message})";
        }
        // 2) 兜底：按 IL2CPP 二维数组内存布局读。
        //    头 = klass+monitor(16) + bounds(8) + 总元素数(8)，元素从 +32 起、行主序，
        //    行跨度 = 第 1 维长度（bounds[1].length 位于 bounds 指针 +16）。
        try
        {
            var pointer = Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(value);
            if (pointer != IntPtr.Zero)
            {
                var total = System.Runtime.InteropServices.Marshal.ReadInt32(pointer, 24);
                var bounds = System.Runtime.InteropServices.Marshal.ReadInt64(pointer, 16);
                var columns = bounds != 0
                    ? System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)bounds, 16)
                    : 1;
                if (columns <= 0)
                    columns = 1;
                var rowCount = columns > 0 ? total / columns : 0;
                for (var row = 0; row < rowCount; row++)
                {
                    var count = System.Runtime.InteropServices.Marshal.ReadInt32(pointer, 32 + row * columns * 4);
                    var weight = columns > 1
                        ? System.Runtime.InteropServices.Marshal.ReadInt32(pointer, 32 + (row * columns + 1) * 4)
                        : 0;
                    rows.Add((count, weight));
                }
                diagnostic += $" raw[{rowCount}×{columns}]";
            }
        }
        catch (Exception exception)
        {
            diagnostic += $" raw 失败({exception.Message})";
        }
        return rows;
    }
    // 该品级可开出的孔数候选：TEquipQuality.slotCountArr（工坊开孔是 forgeSlotCountArr）。
    private static List<int> GetSlotCountCandidates(int quality, bool isForge)
    {
        var result = new List<int>();
        try
        {
            var table = TEquipQuality.create();
            if (!table.ContainsKey(quality) || table[quality] == null)
            {
                Plugin.Verbose($"[SocketCandidates] 品级 {quality} 不在 TEquipQuality 里 → 0 孔");
                return result;
            }
            var raw = isForge ? table[quality].forgeSlotCountArr : table[quality].slotCountArr;
            var rows = ReadSlotCountTable(raw, out var diagnostic);
            var detail = new List<string>();
            foreach (var (count, weight) in rows)
            {
                detail.Add($"{count}(权重{weight})");
                if (count > 0 && !result.Contains(count))
                    result.Add(count);
            }
            result.Sort();
            Plugin.Verbose($"[SocketCandidates] 品级 {quality} {(isForge ? "forgeSlotCountArr" : "slotCountArr")} " +
                           $"诊断={diagnostic} 表=[{string.Join(",", detail)}] → 候选[{string.Join(",", result)}]");
        }
        catch (Exception exception)
        {
            Plugin.Verbose($"[SocketCandidates] 品级 {quality} 读取异常：{exception.Message}");
        }
        return result;
    }
    private static int MaxOf(List<int> values)
    {
        var maximum = 0;
        foreach (var value in values)
            maximum = Math.Max(maximum, value);
        return maximum;
    }
    // 按原生流程创建并升级词条（档位 2/3）：
    //   · 先用 CreateEquipmentAffix 以 **等级 1** 创建 —— 数值 = GetEquipAffixGradeRate(装备质量)
    //     × TEquipQuality[品级].valueRate × poly_1 × 词条池倍率；
    //   · 再逐级调用 SaveAffixData.LevelUp() 推到目标等级 —— 与游戏内词条升级（锻造/印记）同一条路径，
    //     LevelUp 会按 TAffix.levelRate 的比例把 value 缩放到新等级（仅 effectType == 1 生效）。
    //   这样编辑器不再自己算 value，等级与数值都交给原生；「强制越界」的数值覆盖仍在之后执行。
    //   注意：逐级缩放会做中间取整，最终值可能与"直接用目标等级 Create"相差 1~几点的舍入误差
    //   （原生升级路径本身就是这样算的）。
    private static SaveAffixData CreateAffixByNativeLevelUp(int affixId, int quality, int targetLevel, float rate,
        int equipGrade, bool logLevelUp = true)
    {
        var target = Math.Max(1, targetLevel);
        var affix = SaveAffixData.CreateEquipmentAffix(affixId, quality, 1, rate, equipGrade, false, true)
                    ?? throw new InvalidOperationException($"游戏拒绝创建词条 {affixId}。");
        // LevelUp 每次 +1 级；上限保护避免异常数据把循环拖死（正常最多 9 级）。
        var guard = 0;
        while (affix.level < target && guard++ < 64)
            affix.LevelUp();
        if (logLevelUp)
        {
            Plugin.Verbose($"[LevelUp] 词条 {affixId}(档位{quality}) 目标等级 {target} → 实际 {affix.level}，" +
                           $"value={affix.value}（rate={rate:0.####} grade={equipGrade}）");
            if (affix.level != target)
                Plugin.Log.LogWarning($"[LevelUp] 词条 {affixId} 未能升到目标等级 {target}（当前 {affix.level}）。");
        }
        return affix;
    }
    // 该装备等级对应的「孔位符文品质」（TEquipLevel.inlayRuneQuality）。
    private static int GetInlayRuneQuality(int level)
    {
        try
        {
            var table = TEquipLevel.create();
            return table.ContainsKey(level) && table[level] != null ? table[level].inlayRuneQuality : 1;
        }
        catch
        {
            return 1;
        }
    }
    // 孔位类型是否违反原生布局规则：
    //   · 随机（0）永远允许；
    //   · 武器默认攻击孔、防具默认防御孔，永远允许；
    //   · 另一种类型只有"特殊孔位"允许（武器最后一孔 / 防具第一孔，且孔数 > 1）。
    private static bool IsNativeSlotTypeAllowed(int slotType, int index, int count, bool isWeapon)
    {
        if (slotType == 0)
            return true;
        var defaultType = isWeapon ? 1 : 2;
        if (slotType == defaultType)
            return true;
        if (count <= 1)
            return false;
        var specialIndex = isWeapon ? count - 1 : 0;
        return index == specialIndex;
    }
    // 按「镶嵌组」的词条行重写开孔：行顺序 = 孔位顺序（第 0 行 = 孔 0）。
    //   · 组为空时完全不动 slotCount / slotRuneList（交给原生随机开孔）；
    //   · 行数超过该品级上限时按上限截断（x 由装备自身规则校验）；
    //   · 每行的 SocketType = 0 随机（复刻原生 50/50）/ 1 攻击 / 2 防御；
    //     未勾选「强制越界」时，不符合原生布局的类型会被纠正成默认类型并计数说明；
    //   · 已经有符文的孔位不改类型（符文类型必须匹配孔位，原生不允许）。
    private static string ApplySocketRows(SaveItemData saveItem, List<AffixEdit> socketRows, TEquip template,
        bool overrideLimits)
    {
        if (socketRows.Count == 0)
            return "";
        var maximum = MaxOf(GetSlotCountCandidates(saveItem.quality, false));
        // 「越界」勾选时不再按该品级上限截断（原生上限见 TEquipQuality.slotCountArr）。
        var target = overrideLimits
            ? Math.Max(0, socketRows.Count)
            : Math.Max(0, Math.Min(socketRows.Count, maximum));
        if (overrideLimits && socketRows.Count > maximum)
            Plugin.Verbose($"[OverrideLimits] 越界开孔：{socketRows.Count} 孔 > 该品级原生上限 {maximum}");
        var list = saveItem.slotRuneList;
        if (list == null)
        {
            list = new Il2CppSystem.Collections.Generic.List<SaveSlotRuneData>();
            saveItem.slotRuneList = list;
        }
        while (list.Count > target)
            list.RemoveAt(list.Count - 1);
        var inlayQuality = GetInlayRuneQuality(saveItem.level);
        var isWeapon = template != null && template.part <= 3;
        while (list.Count < target)
        {
            var index = list.Count;
            var slotType = isWeapon ? ERuneSlotType.atk : ERuneSlotType.def;
            list.Add(SaveSlotRuneData.Create(index, slotType, inlayQuality));
        }
        var rolled = 0;
        var corrected = 0;
        var keptByRune = 0;
        for (var i = 0; i < target; i++)
        {
            var row = socketRows[i];
            var slot = list[i];
            if (slot == null)
                continue;
            if (slot.runeId > 0)
            {
                // 已镶嵌符文的孔位保持原类型，避免符文与孔位类型不匹配。
                keptByRune++;
                continue;
            }
            int slotType;
            if (row.SocketType == 0)
            {
                slotType = UnityEngine.Random.RandomRangeInt(0, 2) == 1 ? 1 : 2;
                rolled++;
            }
            else if (IsNativeSlotTypeAllowed(row.SocketType, i, target, isWeapon) || row.ForceOverflow)
            {
                slotType = row.SocketType;
            }
            else
            {
                // 不符合原生布局且没有勾选强制越界 → 纠正为默认类型。
                slotType = isWeapon ? 1 : 2;
                corrected++;
            }
            slot.slotType = (ERuneSlotType)slotType;
        }
        saveItem.slotCount = target;
        var note = $"镶嵌 {target}/{maximum}";
        if (socketRows.Count > maximum)
        {
            note += overrideLimits
                ? $"（越界：超过该品级原生上限 {maximum} 条，按输入 {socketRows.Count} 条写入）"
                : $"（输入 {socketRows.Count} 条超过该品级上限，已按上限处理）";
        }
        if (rolled > 0)
            note += $"，{rolled} 个孔位随机";
        if (corrected > 0)
            note += $"，{corrected} 个孔位类型不符合原生布局已纠正（勾选「强制越界」可保留）";
        if (keptByRune > 0)
            note += $"，{keptByRune} 个孔位已有符文、类型保持不变";
        return note;
    }
    // 把旧装备上的「非词条/非孔位」标记搬到新装备上（1.1.5 引入的影子石复制与印章建议）。
    // 重新生成走的是全新的 SaveItemData，这些标记不搬就会丢：
    //   · isShadowReplica：这件装备是影子复制品（复制品不能再被复制）
    //   · isShadowCopied ：这件装备已经被复制过（每件只能复制一次，ItemSys.TryUseShadowStoneOnEquip 会检查）
    //   · isAutoSealSuggest：印章建议的自动开关
    // 返回一段用于状态消息的说明（无标记时为空）。
    private static string CopyCarryOverFlags(SaveItemData? source, SaveItemData? target)
    {
        if (source == null || target == null)
            return "";
        var carried = new List<string>();
        try
        {
            if (source.isShadowReplica)
            {
                target.isShadowReplica = true;
                carried.Add("影子复制品");
            }
            if (source.isShadowCopied)
            {
                target.isShadowCopied = true;
                carried.Add("已复制标记");
            }
            if (source.isAutoSealSuggest)
            {
                target.isAutoSealSuggest = true;
                carried.Add("印章建议开关");
            }
        }
        catch (Exception exception)
        {
            Plugin.Verbose($"[CarryOverFlags] 复制 1.1.5 装备标记失败：{exception.Message}");
            return "";
        }
        return carried.Count > 0 ? $"，已保留{string.Join("、", carried)}" : "";
    }
    // 在移除旧装备之前，先把已镶嵌的符文抄成快照（runeId + quality）：
    // 不依赖旧 ItemData 在 ReduceItem 之后是否仍可访问。
    private static List<(int RuneId, int Quality)> CaptureInlaidRunes(SaveItemData oldItem)
    {
        var result = new List<(int RuneId, int Quality)>();
        try
        {
            var slots = oldItem?.slotRuneList;
            for (var i = 0; slots != null && i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot != null && slot.runeId > 0)
                    result.Add((slot.runeId, Math.Max(1, slot.quality)));
            }
        }
        catch (Exception exception)
        {
            Plugin.Verbose($"[RuneRefund] 读取旧装备符文失败：{exception.Message}");
        }
        return result;
    }
    // 把旧装备上已镶嵌的符文返还背包：按孔位重建符文物品（与「添加物品」用同一条原生路径）。
    // 必须在旧装备移除之后、新装备成功加入之后调用，避免返还后新装备加入失败导致符文凭空多出。
    private static string RefundInlaidRunes(LordData lord, List<(int RuneId, int Quality)> runes)
    {
        try
        {
            if (runes.Count == 0)
                return "";
            var runeTable = TRune.create();
            var refunded = new List<string>();
            var failed = new List<string>();
            foreach (var (runeId, quality) in runes)
            {
                var name = runeTable.ContainsKey(runeId) ? runeTable[runeId].name : $"符文 {runeId}";
                try
                {
                    var runeSave = SaveItemData.CreateRune(runeId, quality, 1);
                    var runeItem = runeSave == null ? null : ItemData.Create(runeSave, EItemPosType.bag, 0);
                    if (runeItem != null && lord.lordBagData.addItemToBag(runeItem))
                        refunded.Add($"{name}（品质{quality}）");
                    else
                        failed.Add($"{name}（背包已满）");
                }
                catch (Exception exception)
                {
                    failed.Add($"{name}（{exception.Message}）");
                }
            }
            var parts = new List<string>();
            if (refunded.Count > 0)
                parts.Add($"已返还符文：{string.Join("、", refunded)}");
            if (failed.Count > 0)
                parts.Add($"未能返还：{string.Join("、", failed)}");
            return string.Join("；", parts);
        }
        catch (Exception exception)
        {
            return $"符文返还失败：{exception.Message}";
        }
    }
    // 单件装备当前可用的圣碑腐蚀次数上限。
    // 原生 SteleCorruptData.GetMaxCorruptCount() 就是
    // MapDoomData.GetEquipCorruptCountMax(Game.dataMgr.nowSeasonData.advData.mapData.saveMapData)：
    // 取「已完成的终焉阶段」的 TDoomStage.equipCorruptCount；当前阶段的据点全部打通后才升到当前阶段的值。
    private static int GetCorruptAllowance()
    {
        try
        {
            var saveMapData = Game.dataMgr?.nowSeasonData?.advData?.mapData?.saveMapData;
            return saveMapData != null ? MapDoomData.GetEquipCorruptCountMax(saveMapData) : 0;
        }
        catch
        {
            // 存档未就绪或阶段表缺失时按 0 处理（额度退回纯生成上限）。
            return 0;
        }
    }
    private static int GetBaseAffixId(TEquip? template)
    {
        var baseAffix = template?.baseAffix;
        return baseAffix != null && baseAffix.Length >= 2 ? baseAffix[0] : 0;
    }
    // baseAffix[1] 就是原生创建基础词条时传给 CreateEquipmentAffix 的 partRate（:2219193），
    // 直接原样使用，避免参考值与游戏面板不一致。
    private static float GetBaseAffixRate(TEquip? template)
    {
        var baseAffix = template?.baseAffix;
        return baseAffix != null && baseAffix.Length >= 2 ? baseAffix[1] : 1f;
    }
    private static Dictionary<(int Id, int Quality), float> BuildEquipmentSpecialAffixMap(
        TEquip template,
        int equipmentQuality)
    {
        var result = new Dictionary<(int Id, int Quality), float>();
        if (equipmentQuality != (int)EItemQualityType.legend &&
            equipmentQuality != (int)EItemQualityType.myth)
            return result;
        var rawAffixArr = template.legendAffixArr;
        if (rawAffixArr == null)
            return result;
        // IL2CPP 对二维数组只暴露通用对象，需要转换为游戏侧 Array 后按坐标读取。
        var affixArr = rawAffixArr.Cast<Il2CppSystem.Array>();
        // 每行第 1 列是词条 ID，第 2 列是百分制倍率；这与游戏追加专属词条的原生路径一致。
        for (var index = 0; index < affixArr.GetLength(0); index++)
        {
            var affixId = affixArr.GetValue(index, 0).Unbox<int>();
            if (affixId > 0)
                result[(affixId, equipmentQuality)] = affixArr.GetValue(index, 1).Unbox<int>() / 100f;
        }
        return result;
    }
    private static void AddAffixQualityLimit(
        EquipmentRules rules,
        Il2CppSystem.Collections.Generic.Dictionary<int, TAffixQuality> qualityTable,
        int quality,
        int count)
    {
        if (count <= 0)
            return;
        rules.AffixQualityLimits[quality] = count;
        rules.AffixQualityNames[quality] = qualityTable.ContainsKey(quality)
            ? qualityTable[quality].name
            : $"词条档位 {quality}";
        // 下限缺失时按"等于上限"兜底，避免界面出现 0~N 这种假区间。
        if (!rules.AffixQualityMinimums.ContainsKey(quality))
            rules.AffixQualityMinimums[quality] = count;
    }
    // 记录原生下限；同时保证下限不超过上限（上限可能被池子裁剪）。
    private static void SetAffixQualityMinimum(EquipmentRules rules, int quality, int minimum)
    {
        var clamped = Math.Max(0, minimum);
        if (rules.AffixQualityLimits.TryGetValue(quality, out var limit))
            clamped = Math.Min(clamped, limit);
        rules.AffixQualityMinimums[quality] = clamped;
    }
    private static string GetTalentDisplayName(TTalent talent)
    {
        if (talent.skillId > 0)
        {
            var skills = TSkill.create();
            if (skills.ContainsKey(talent.skillId))
                return $"{skills[talent.skillId].name}（技能 {talent.skillId}）";
        }
        if (talent.masteryId > 0)
        {
            // 天赋专精的实际效果由 masteryId 指向。TTalent.name 是天赋节点名称，个别成对节点
            // （例如“无畏”和“不屈”）与实际专精名称顺序不同，不能拿它标注启迪候选。
            var mastery = TableData.getTMasteryData(talent.masteryId);
            if (mastery != null && !string.IsNullOrWhiteSpace(mastery.name))
                return mastery.name;
        }
        return string.IsNullOrWhiteSpace(talent.name) ? $"天赋 {talent.id}" : talent.name;
    }
    private static string GetHeroJobName(
        Il2CppSystem.Collections.Generic.Dictionary<int, THeroJob> jobTable,
        int jobId) => jobTable.ContainsKey(jobId) && jobTable[jobId] != null
            ? jobTable[jobId].name ?? $"职业 {jobId}"
            : $"职业 {jobId}";
    private static string GetAffixName(TAffix affix) =>
        string.IsNullOrWhiteSpace(affix.des) ? $"词条 {affix.id}" : affix.des;
    private static LordData GetLord() => Game.dataMgr?.nowSeasonData?.lordData
        ?? throw new InvalidOperationException("请先启动游戏并进入一个游戏存档。");
    private static string GetHeroName(HeroData hero)
    {
        var name = hero.saveHeroData?.name;
        if (string.IsNullOrWhiteSpace(name)) name = hero.saveHeroData?.GetL10nName();
        return string.IsNullOrWhiteSpace(name) ? $"角色 #{hero.saveHeroData?.uniqueId}" : name;
    }
    private static float ReadAttribute(SaveHeroData save, EAttrType type) =>
        save.mainAttrDic != null && save.mainAttrDic.ContainsKey(type) ? save.mainAttrDic[type] : 0;
    private static string GetAttributeName(EAttrType type) => type switch
    {
        EAttrType.hp => "生命",
        EAttrType.mp => "魔力",
        EAttrType.STR => "力量",
        EAttrType.DEX => "敏捷",
        EAttrType.INT => "智力",
        _ => type.ToString()
    };
    private static void SaveNow() =>
        // 只在完整操作成功后调用原生保存，校验失败时不会产生半完成存档。
        (Game.dataMgr?.nowSeasonData?.nativeSeasonData
         ?? throw new InvalidOperationException("当前游戏存档不可用。"))
        .SaveData();
}
