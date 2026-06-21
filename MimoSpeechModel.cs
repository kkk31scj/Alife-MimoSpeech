using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Alife.Function.Speech;
using Alife.Platform;
using Microsoft.Extensions.Logging;

namespace Alife.Function.MimoSpeech;

/* ── 配置 ── */
public class MimoSpeechModelConfig
{
    [Description("MiMo API Key（platform.xiaomimimo.com 获取）")]
    public string ApiKey { get; set; } = "";
    [Description("音色（冰糖 / 白桦 / Mia / Milo / Dean 等）")]
    public string Voice { get; set; } = "冰糖";
    [Description("语音风格（台湾腔 / 东北话 / 四川话 / 粤语 等，留空=默认）")]
    public string Style { get; set; } = "";
    [Description("角色人设描述（发给MiMo的用户消息，自定义角色语感）")]
    public string Personality { get; set; } = "";
}

/* ── 语音模型 ── */
[Module("MiMo语音",
    "小米MiMo TTS语音引擎。支持唱歌、方言、情绪演绎。\n启用后关闭VitsSpeechModel模块。",
    defaultCategory: "Alife 官方/模型接入/语音模型")]
public class MimoSpeechModel(
    ILogger<MimoSpeechModel> logger,
    XmlFunctionCaller functionService
) :
    InteractiveModule<MimoSpeechModel>,
    ISpeechModel,
    IConfigurable<MimoSpeechModelConfig>
{
    public MimoSpeechModelConfig? Configuration { get; set; }
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    static string TempDir => Path.Combine(AlifePath.TempFolderPath, "mimo_speech");

    // ═══════════ ISpeechModel ═══════════

    public async Task<string?> GenerateSpeechFileAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cfg = Configuration;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey)) return null;

        string hash = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(text + cfg.Voice + cfg.Style))).Replace("-", "");
        string cache = Path.Combine(TempDir, $"mimo_{hash}.wav");
        if (File.Exists(cache)) return cache;

        var (user, assistant) = BuildMessages(text);
        var msgArray = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = assistant }
        };
        if (!string.IsNullOrWhiteSpace(user))
            msgArray.Insert(0, new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = user });

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = "mimo-v2.5-tts",
            ["messages"] = msgArray,
            ["audio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = "pcm16",
                ["voice"] = cfg.Voice ?? "冰糖"
            },
            ["stream"] = true
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.xiaomimimo.com/v1/chat/completions");
        req.Headers.Add("api-key", cfg.ApiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                logger.LogWarning("[MiMo] API {Code}: {Msg}", (int)resp.StatusCode, err[..Math.Min(200, err.Length)]);
                return null;
            }

            // 流式收 PCM16
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chunks = new System.Collections.Generic.List<byte[]>();
            int total = 0;

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:")) continue;
                string json = line[5..].Trim();
                if (json == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    {
                        var first = ch[0];
                        string? b64 = null;
                        if (first.TryGetProperty("delta", out var d) && d.TryGetProperty("audio", out var a) && a.TryGetProperty("data", out var dd))
                            b64 = dd.GetString();
                        if (b64 != null) { var pcm = Convert.FromBase64String(b64); chunks.Add(pcm); total += pcm.Length; }
                    }
                }
                catch { }
            }

            if (total == 0) return null;
            byte[] wav = BuildWav(chunks, total, 24000);
            Directory.CreateDirectory(TempDir);
            await File.WriteAllBytesAsync(cache, wav, ct);
            return cache;
        }
        catch (TaskCanceledException) { logger.LogWarning("[MiMo] 超时"); return null; }
        catch (Exception ex) { logger.LogWarning("[MiMo] {Ex}", ex.Message); return null; }
    }

    // ═══════════ 文本清理 ═══════════

    static string CleanText(string text)
    {
        // 移除所有全角括号内超过10字的内容（那是叙事，不是音频指令）
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\uff08') // （
            {
                int end = text.IndexOf('\uff09', i); // ）
                if (end > i)
                {
                    int len = end - i - 1;
                    if (len <= 10)
                    {
                        // 短括号保留（有效的音频事件）
                        sb.Append(text, i, end - i + 1);
                    }
                    // 长括号直接跳过（砍掉）
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // ═══════════ 消息构建 ═══════════

    (string user, string assistant) BuildMessages(string text)
    {
        // 清理文本：砍掉长括号叙事（超过10字的括号会被MiMo朗读）
        text = CleanText(text);

        bool isSing = text.Contains("(唱歌)");
        string style = Configuration?.Style ?? "";

        // user 消息：角色人设描述（留空则不发送人设，MiMo用默认语感）
        // 唱歌模式保留 user="唱歌" 短提示
        string personality = (Configuration?.Personality ?? "").Trim();
        string user;
        if (isSing) user = "唱歌";
        else user = string.IsNullOrWhiteSpace(personality) ? "" : personality;

        // assistant 消息：风格标签 + 文本
        string assistant;
        if (isSing) assistant = text; // (唱歌)lyrics
        else if (!string.IsNullOrWhiteSpace(style))
            assistant = $"({style}){text}";
        else assistant = text;

        return (user, assistant);
    }

    // ═══════════ PCM → WAV ═══════════

    static byte[] BuildWav(System.Collections.Generic.List<byte[]> chunks, int total, int rate = 24000)
    {
        byte[] wav = new byte[44 + total];
        // RIFF
        BitConverter.GetBytes(0x46464952).CopyTo(wav, 0);
        BitConverter.GetBytes(36 + total).CopyTo(wav, 4);
        BitConverter.GetBytes(0x45564157).CopyTo(wav, 8);
        // fmt
        BitConverter.GetBytes(0x20746D66).CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(rate).CopyTo(wav, 24);
        BitConverter.GetBytes(rate * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        // data
        BitConverter.GetBytes(0x61746164).CopyTo(wav, 36);
        BitConverter.GetBytes(total).CopyTo(wav, 40);
        int off = 44;
        foreach (var c in chunks) { Buffer.BlockCopy(c, 0, wav, off, c.Length); off += c.Length; }
        return wav;
    }

    // ═══════════ XML 函数 ═══════════

    [XmlFunction(FunctionMode.OneShot)]
    [Description("用MiMo唱歌。lyrics为歌词（中文效果最佳）")]
    public async Task<string> Sing(string lyrics)
    {
        var cfg = Configuration;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            Poke("[MiMo] 请配置 API Key\nhttps://platform.xiaomimimo.com");
            return "未配置";
        }
        string? f = null;
        try { f = await GenerateSpeechFileAsync($"(唱歌){lyrics}"); }
        catch (Exception ex) { Poke($"[MiMo] 失败：{ex.Message}"); return "失败"; }

        if (f != null)
        {
            PlayWav(f);
            Poke($"[MiMo] ♪ 唱完了\n{f}\n{new FileInfo(f).Length / 1024} KB");
            return $"唱歌完成。发送QQ语音时请用: [CQ:record,file={f}]";
        }
        return "生成失败";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看可用音色")]
    public string Voices() => "冰糖 / 白桦 / Mia / Milo / Dean";

    static void PlayWav(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"(New-Object System.Media.SoundPlayer '{path}').PlaySync()\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        System.Diagnostics.Process.Start(psi);
    }

    // ═══════════ 生命周期 ═══════════

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        Directory.CreateDirectory(TempDir);
        functionService.RegisterHandlerWithoutDocument(new XmlHandler(this));

        string keyOk = string.IsNullOrWhiteSpace(Configuration?.ApiKey) ? "❌ 未配置" : "✅ 已配置";
        string voice = Configuration?.Voice ?? "冰糖";
        string style = string.IsNullOrWhiteSpace(Configuration?.Style) ? "默认" : Configuration!.Style;

        Prompt($"""
            你是 MiMo 语音引擎驱动的桌宠角色。当前配置：

            ## 语音状态
            - API：{keyOk}
            - 音色：{voice}（冰糖/冰糖/冰糖/白桦/Mia/Milo/Dean）
            - 风格：{style}（台湾腔/东北话/四川话/粤语/温柔/开心等）

            ## 可用函数
            - <Sing lyrics="歌词"/> — 唱歌，中文歌词最佳
            - <Voices/> — 查看音色

            ## 语音风格
            所有情绪控制通过 Style 配置自动注入，不要在你的回复文本中手动写任何标签或括号。
            插件会自动拼接 (风格标签) 到文本开头传给 MiMo API。
            可选风格参考：温柔 高冷 活泼 严肃 慵懒 俏皮 磁性 甜美 沙哑 台湾腔 东北话 四川话 粤语 开心 悲伤 兴奋 委屈 怅然 无奈

            ## 唱歌后发QQ语音条
            Sing 返回路径后，用以下格式发QQ语音（不带voice属性！）：
            <qchat>[CQ:record,file=Sing返回的路径]</qchat>
            
            ## 正常QQ语音消息
            普通聊天需要QQ语音时，用 voice="true" 让系统实时合成：
            <qchat voice="true">想说的话</qchat>
            
            绝对不要把 [CQ:record] 用在非唱歌场景。[CQ:record] 是发已有文件的，voice="true" 才是实时合成语音。
            """);
    }
}
