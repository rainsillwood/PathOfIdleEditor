using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PathOfIdleEditor;

internal static class BridgeServer
{
    // 固定协议版本可以避免新旧桌面端和 Mod 误连后写入不兼容的数据。
    internal const string PipeName = "PathOfIdleEditor.v1";
    private static readonly ConcurrentQueue<PendingRequest> Pending = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static CancellationTokenSource? _cancellation;

    internal static void Start()
    {
        if (_cancellation != null)
            return;
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cancellation.Token));
    }

    internal static void Stop()
    {
        _cancellation?.Cancel();
        _cancellation = null;
    }

    internal static void ProcessPendingRequests()
    {
        // 此方法只由 BridgeBehaviour.Update 调用，下面的游戏 API 调用均发生在 Unity 主线程。
        while (Pending.TryDequeue(out var pending))
        {
            try
            {
                pending.Response = Handle(pending.Request);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(exception);
                pending.Response = new EditorResponse { Success = false, Message = exception.Message };
            }
            finally
            {
                pending.Completed.Set();
            }
        }
    }

    private static EditorResponse Handle(EditorRequest request) => request.Action switch
    {
        "snapshot" => HandleSnapshot(),
        // 诊断：随时重新输出词条档位/数值探针（只在模组设置里开启了详细日志时才有内容）。
        "affixProbe" when Plugin.VerboseEnabled => new EditorResponse { Success = true, Message = GameEditorService.ProbeAffixValueTypes() },
        // 诊断：输出「品级表字段 / 模板 legendAffixCount / 各部位池子条数」对照表。
        "templateReport" when Plugin.VerboseEnabled => new EditorResponse { Success = true, Message = GameEditorService.BuildTemplateReport() },
        "equipmentRules" when request.Equipment != null => new EditorResponse { Success = true, Message = "已读取当前装备规则。", EquipmentRules = GameEditorService.GetEquipmentRules(request.Equipment) },
        // 需求 2.2：词条等级 / 装备质量 / 装备等级变化后只刷新参考值，不重建整个候选池。
        "affixReference" when request.Equipment != null => new EditorResponse { Success = true, Message = "", AffixReferences = GameEditorService.GetAffixReferences(request.Equipment) },
        "generateEquipment" when request.Equipment != null => new EditorResponse { Success = true, Message = GameEditorService.GenerateEquipment(request.Equipment) },
        "updateHero" when request.Hero != null => new EditorResponse { Success = true, Message = GameEditorService.UpdateHero(request.Hero) },
        "changeHeroQuality" when request.Hero != null => GameEditorService.ChangeHeroQuality(request.Hero.UniqueId, request.Hero.Quality),
        "updateLord" when request.Lord != null => GameEditorService.UpdateLord(request.Lord),
        "inventory" => new EditorResponse { Success = true, Message = "已刷新背包物品。", Inventory = GameEditorService.GetInventorySnapshot() },
        "updateInventoryItem" when request.InventoryItem != null => GameEditorService.UpdateInventoryItem(request.InventoryItem),
        // 批量：一次提交表格里所有改动过的行。
        "updateInventoryCounts" when request.InventoryItems != null => GameEditorService.UpdateInventoryItems(request.InventoryItems),
        "addInventoryItem" when request.InventoryAdd != null => GameEditorService.AddInventoryItem(request.InventoryAdd),
        "equipmentInventory" => new EditorResponse
        {
            Success = true,
            Message = "已读取背包中的装备。",
            EquipmentInventory = GameEditorService.GetEquipmentInventorySnapshot()
        },
        "replaceEquipment" when request.EquipmentReplace != null => GameEditorService.ReplaceEquipment(request.EquipmentReplace),
        _ => new EditorResponse { Success = false, Message = $"不支持的请求：{request.Action}" }
    };

    // 首次连接时打一次探针（只在开启详细日志时执行），方便对照客户端界面排查。
    internal static void LogAffixProbe(string reason)
    {
        if (!Plugin.VerboseEnabled)
            return;
        try
        {
            Plugin.Log.LogInfo($"[AffixProbe] {reason}：{GameEditorService.ProbeAffixValueTypes()}");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning($"[AffixProbe] 执行失败：{exception.Message}");
        }
    }

    // 诊断：把「品级表字段 vs 模板字段 vs 实际池子条数」的对照表写进日志（只在开启详细日志时执行）。
    internal static void LogTemplateReport(string reason)
    {
        if (!Plugin.VerboseEnabled)
            return;
        try
        {
            Plugin.Log.LogInfo($"[TemplateReport] {reason}：{GameEditorService.BuildTemplateReport()}");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning($"[TemplateReport] 执行失败：{exception.Message}");
        }
        try
        {
            var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "PathOfIdleEditor-tables.tsv");
            var rows = GameEditorService.WriteTemplateDump(path);
            Plugin.Log.LogInfo($"[TemplateReport] 表数据已导出：{path}（TEquip {rows} 行）");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning($"[TemplateReport] 导出表数据失败：{exception.Message}");
        }
        try
        {
            var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "PathOfIdleEditor-distort.tsv");
            var rows = GameEditorService.WriteDistortTableDump(path);
            Plugin.Log.LogInfo($"[TemplateReport] 监牢扭曲表已导出：{path}（{rows} 行）");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning($"[TemplateReport] 导出监牢扭曲表失败：{exception.Message}");
        }
    }

    private static EditorResponse HandleSnapshot()
    {
        LogAffixProbe("首次连接");
        LogTemplateReport("首次连接");
        return new EditorResponse { Success = true, Message = "已连接游戏。", Snapshot = GameEditorService.GetSnapshot() };
    }

    private static async Task ListenLoop(CancellationToken cancellationToken)
    {
        // 每个请求使用一次短连接，桌面程序退出或崩溃后不会长期占用管道实例。
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var request = JsonSerializer.Deserialize<EditorRequest>(line, JsonOptions)
                    ?? throw new InvalidDataException("编辑器请求格式无效。");
                var pending = new PendingRequest(request);
                Pending.Enqueue(pending);

                // 后台线程等待主线程完成请求；超时后返回错误，避免桌面端永久挂起。
                if (!pending.Completed.Wait(TimeSpan.FromSeconds(10)))
                    pending.Response = new EditorResponse { Success = false, Message = "等待游戏主线程响应超时。" };
                await writer.WriteLineAsync(JsonSerializer.Serialize(pending.Response, JsonOptions));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning($"编辑器桥接连接失败：{exception.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private sealed class PendingRequest
    {
        internal PendingRequest(EditorRequest request) => Request = request;
        internal EditorRequest Request { get; }
        internal ManualResetEventSlim Completed { get; } = new(false);
        internal EditorResponse Response { get; set; } = new() { Success = false, Message = "游戏没有返回响应。" };
    }
}
