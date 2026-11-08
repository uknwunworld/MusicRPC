using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MusicRPC;

/// <summary>Минимальный клиент Discord Rich Presence поверх локального named pipe (discord-ipc-N).</summary>
public sealed class DiscordIpc : IDisposable
{
    NamedPipeClientStream? _pipe;
    CancellationTokenSource? _loopCts;
    readonly SemaphoreSlim _lock = new(1, 1);
    volatile bool _ready;

    public string ClientId { get; private set; } = "";
    public bool Connected => _ready && _pipe is { IsConnected: true };
    public string Status { get; private set; } = "Не подключено";
    public string? UserName { get; private set; }
    public string? LastError { get; private set; }

    public async Task<bool> ConnectAsync(string clientId, CancellationToken ct)
    {
        Disconnect();
        ClientId = clientId;
        LastError = null;

        NamedPipeClientStream? pipe = null;
        for (int i = 0; i < 10 && pipe == null; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await p.ConnectAsync(400, ct); pipe = p; }
            catch (OperationCanceledException) { p.Dispose(); throw; }
            catch { p.Dispose(); }
        }
        if (pipe == null)
        {
            Status = "Discord не запущен";
            return false;
        }

        _pipe = pipe;
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(6));
            await WriteFrame(0, new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString(), to.Token);
            var (_, json) = await ReadFrame(to.Token);
            var node = JsonNode.Parse(json);
            var evt = node?["evt"]?.GetValue<string>();
            if (evt != "READY")
            {
                LastError = node?["data"]?["message"]?.GetValue<string>() ?? "Discord отклонил Client ID";
                Status = "Ошибка: " + LastError;
                Disconnect(keepStatus: true);
                return false;
            }
            UserName = node?["data"]?["user"]?["username"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Status = "Не удалось связаться с Discord";
            Diagnostics.LogThrottled("discord-connect", ex);
            Disconnect(keepStatus: true);
            return false;
        }

        _ready = true;
        Status = UserName != null ? $"Подключено · {UserName}" : "Подключено";
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(pipe, _loopCts.Token));
        return true;
    }

    async Task ReadLoop(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, json) = await ReadFrame(ct);
                if (op == 3) await SendRaw(4, json, ct);          // PING -> PONG
                else if (op == 2) break;                          // CLOSE
                else
                {
                    try
                    {
                        var n = JsonNode.Parse(json);
                        if (n?["evt"]?.GetValue<string>() == "ERROR")
                            LastError = n["data"]?["message"]?.GetValue<string>();
                    }
                    catch { }
                }
            }
        }
        catch { }
        if (ReferenceEquals(_pipe, pipe) && !ct.IsCancellationRequested)
        {
            _ready = false;
            Status = "Discord отключился";
        }
    }

    public async Task SetActivityAsync(JsonObject? activity, CancellationToken ct)
    {
        if (!Connected) return;
        var payload = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = activity,
            },
            ["nonce"] = Guid.NewGuid().ToString(),
        };
        try { await SendRaw(1, payload.ToJsonString(), ct); }
        catch (Exception ex) { _ready = false; Status = "Discord отключился"; Diagnostics.LogThrottled("discord-send", ex); }
    }

    async Task SendRaw(int op, string json, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { await WriteFrame(op, json, ct); }
        finally { _lock.Release(); }
    }

    async Task WriteFrame(int op, string json, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException();
        var data = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + data.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), data.Length);
        data.CopyTo(buf, 8);
        await pipe.WriteAsync(buf, ct);
        await pipe.FlushAsync(ct);
    }

    async Task<(int, string)> ReadFrame(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException();
        var head = new byte[8];
        await pipe.ReadExactlyAsync(head, ct);
        int op = BitConverter.ToInt32(head, 0);
        int len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1_000_000) throw new InvalidDataException();
        var body = new byte[len];
        await pipe.ReadExactlyAsync(body, ct);
        return (op, Encoding.UTF8.GetString(body));
    }

    public void Disconnect(bool keepStatus = false)
    {
        _ready = false;
        try { _loopCts?.Cancel(); } catch { }
        _loopCts = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        if (!keepStatus) Status = "Не подключено";
    }

    public void Dispose() => Disconnect();
}

