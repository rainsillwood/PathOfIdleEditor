using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PathOfIdleEditor.App;

internal static class BridgeClient
{
    // 桌面端不直接接触游戏文件，只通过同机命名管道调用桥接 Mod。
    private const string PipeName = "PathOfIdleEditor.v1";

    // 整体请求超时：连接、写入、读取、反序列化加起来不能超过这个值。
    // 服务端 ListenLoop 内部有 10 秒的主线程处理超时，这里给 15 秒留出管道往返和序列化余量。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<EditorResponse> SendAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        // timeoutCts 链接了调用方传入的取消令牌和内部超时，两者任一触发都会取消下面的所有 await。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            // 请求级短连接便于游戏重启后重新连接，也无需在 UI 中维护长连接状态机。
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            // 3 秒是为了「游戏没启动时快速失败」；整体超时兜底防住「连上了但对方不回应」。
            await pipe.ConnectAsync(3000, timeoutCts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeoutCts.Token);
            var responseJson = await reader.ReadLineAsync(timeoutCts.Token);
            var response = JsonSerializer.Deserialize<EditorResponse>(responseJson ?? "", JsonOptions)
                ?? throw new InvalidDataException("Mod 桥接返回了无效数据。");

            // 协议版本校验：两侧字段不对齐时主动拒绝，避免静默数据丢失。
            if (response.ProtocolVersion != BridgeProtocol.Version)
                throw new InvalidOperationException(
                    $"协议版本不匹配：桥接 Mod 为 {response.ProtocolVersion}，桌面端为 {BridgeProtocol.Version}。" +
                    "请确认 PathOfIdleEditor.Mod.dll 与客户端为同一版本。");

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 内层 token 触发的取消就是超时；外部 token 触发的取消仍然抛给上层。
            throw new TimeoutException($"请求超过 {RequestTimeout.TotalSeconds:0} 秒仍未完成，已中止。");
        }
    }
}