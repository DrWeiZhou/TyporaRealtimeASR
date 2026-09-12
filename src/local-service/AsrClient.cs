using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TyporaAsr;

public sealed class AsrOutput
{
    private readonly StringBuilder raw = new();
    private string? finish;
    public void Add(string text, string? reason = null) { raw.Append(text); if(reason != null) finish = reason; }
    public string Text { get { var s = raw.ToString(); var i = s.IndexOf("<asr_text>", StringComparison.Ordinal); return i >= 0 ? s[(i+10)..].Trim() : s.StartsWith("language ") ? "" : s.Trim(); } }
    public string FinalText() {
        if(finish=="stop")return Text;
        var error=new InvalidDataException($"ASR did not finish normally ({finish ?? "missing finish"})");error.Data["FinishReason"]=finish??"";throw error;
    }
}

public sealed class AsrClient(HttpClient http, string endpoint, string model)
{
    private readonly SemaphoreSlim inferenceGate=new(1,1);
    public async Task<string> Recognize(short[] pcm, CancellationToken ct)
    {
        await inferenceGate.WaitAsync(ct);
        try{return await Infer(pcm,ct);}finally{inferenceGate.Release();}
    }
    private async Task<string> Infer(short[] pcm,CancellationToken ct)
    {
        using var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, Encoding.UTF8, true)) {
            writer.Write("RIFF"u8); writer.Write(36+pcm.Length*2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
            writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8);
            writer.Write(pcm.Length*2); foreach(var x in pcm) writer.Write(x);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/')+"/v1/chat/completions");
        request.Content = JsonContent.Create(new { model, stream=true, temperature=0, max_tokens=Math.Clamp(pcm.Length/16000*24+96,128,768), cache_prompt=false,
            messages=new[]{new { role="user", content=new[]{new { type="input_audio", input_audio=new { data=Convert.ToBase64String(wav.ToArray()), format="wav" } } } } } });
        using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var output = new AsrOutput();
        while(await reader.ReadLineAsync(ct) is { } line) {
            if(!line.StartsWith("data:")) continue;
            var data = line[5..].Trim(); if(data == "[DONE]") break;
            using var json = JsonDocument.Parse(data);
            if(json.RootElement.TryGetProperty("error",out var error)) throw new InvalidDataException(error.ToString());
            if(!json.RootElement.TryGetProperty("choices",out var choices)) continue;
            foreach(var choice in choices.EnumerateArray()) {
                var text = choice.TryGetProperty("delta",out var delta) && delta.TryGetProperty("content",out var c) ? c.GetString() ?? "" : "";
                var reason = choice.TryGetProperty("finish_reason",out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                output.Add(text,reason);
            }
        }
        return output.FinalText();
    }
}
