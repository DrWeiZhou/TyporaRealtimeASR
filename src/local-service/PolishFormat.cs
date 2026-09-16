using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TyporaAsr;

/// <summary>One topic block of a polished window. Topic is "new" (starts a numbered item) or "continue".</summary>
public sealed record PolishBlock(string Topic,string Title,string[] Paragraphs);

/// <summary>Request/response contract for window polishing. The contract is fixed in code because the result is parsed.</summary>
public static class PolishFormat {
 public const int MaxTopics=30,MaxContextChars=600,MaxTitleChars=40;
 public const string DefaultTitle="记录";
 public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 public const string Contract="""
【输出格式：程序会解析你的输出，必须严格遵守，并优先于上文任何关于输出形式的要求】
输入分三部分：<上文> 是已整理内容的结尾，仅用于理解指代，不要输出；<话题列表> 是已有话题，最后一个为当前话题；<待整理> 是本次需要整理的转写文本。
只输出一个 JSON 对象，不要代码围栏、解释或其他文字，格式如下：
{"blocks":[{"topic":"continue","paragraphs":["……"]},{"topic":"new","title":"话题名","paragraphs":["……","……"]}]}
规则：
1. blocks 按原文顺序排列；topic 只能是 "continue"（接续当前话题）或 "new"（开始新话题，必须给出不超过 20 字的 title）。
2. 内容回到话题列表中较早的话题（不是当前话题）时也用 "new"，title 写成“原话题名（续）”。
3. 话题列表为空时，第一个 block 必须是 "new"。
4. 同一话题的内容合并成连贯的段落，每段通常 2–5 句；不要逐句成段，不要编号，段落内不要换行。
5. 待整理文本全部是寒暄、语气词或无效内容时，输出 {"blocks":[]}。
6. [存疑] 只用于影响理解、且结合上下文仍无法确认的人名、机构名、文件名等专有名词，每段最多一处；普通词语的同音错字按上下文直接改正；残缺、含糊的口语片段整理通顺或省略，不要标注 [存疑]。本条优先于上文关于 [存疑] 的要求。
7. 待整理文本开头可能接着上文没说完的话，请顺着上文把意思补通，不要当成新话题。
""";

 public static string SystemPrompt(string prompt)=>prompt.TrimEnd()+"\n\n"+Contract;

 public static string Input(string context,IReadOnlyList<string> topics,string text) {
  var b=new StringBuilder();
  b.Append("<上文>\n").Append(context.Length>0?context:"（无）").Append("\n</上文>\n\n<话题列表>\n");
  if(topics.Count==0)b.Append("（无）\n");
  for(var i=0;i<topics.Count;i++)b.Append(i+1).Append(". ").Append(topics[i]).Append(i==topics.Count-1?"（当前话题）":"").Append('\n');
  return b.Append("</话题列表>\n\n<待整理>\n").Append(text).Append("\n</待整理>").ToString();
 }

 /// <summary>Returns null when the output cannot be trusted and should be requested again.</summary>
 public static List<PolishBlock>? Parse(string content,bool hasTopic) {
  var text=content.Trim();
  if(text.StartsWith("```",StringComparison.Ordinal)){
   var firstLine=text.IndexOf('\n');text=firstLine<0?"":text[(firstLine+1)..];
   var fence=text.LastIndexOf("```",StringComparison.Ordinal);if(fence>=0)text=text[..fence];
   text=text.Trim();
  }
  var open=text.IndexOf('{');var close=text.LastIndexOf('}');
  var blocks=new List<PolishBlock>();
  if(open>=0) {
   if(close<=open)return null;
   try {
    using var doc=JsonDocument.Parse(text[open..(close+1)]);
    if(doc.RootElement.ValueKind!=JsonValueKind.Object||!doc.RootElement.TryGetProperty("blocks",out var array)||array.ValueKind!=JsonValueKind.Array)return null;
    foreach(var item in array.EnumerateArray()) {
     if(item.ValueKind!=JsonValueKind.Object)return null;
     var topic=item.TryGetProperty("topic",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
     if(topic is not ("continue" or "new"))return null;
     var title=item.TryGetProperty("title",out var n)&&n.ValueKind==JsonValueKind.String?n.GetString()??"":"";
     var paragraphs=new List<string>();
     if(item.TryGetProperty("paragraphs",out var p)) {
      if(p.ValueKind==JsonValueKind.String)paragraphs.Add(p.GetString()??"");
      else if(p.ValueKind==JsonValueKind.Array)foreach(var x in p.EnumerateArray()){if(x.ValueKind!=JsonValueKind.String)return null;paragraphs.Add(x.GetString()??"");}
      else return null;
     }
     var cleaned=paragraphs.Select(Clean).Where(x=>x.Length>0).ToArray();
     if(cleaned.Length>0)blocks.Add(new(topic,title,cleaned));
    }
   } catch(JsonException){return null;}
  } else {
   // The model ignored the format but produced prose: keep it as a continuation rather than losing polished text.
   var paragraphs=Regex.Split(text,@"\n\s*\n").Select(Clean).Where(x=>x.Length>0&&!IsPlaceholder(x)).ToArray();
   if(paragraphs.Length>0)blocks.Add(new("continue","",paragraphs));
  }
  return Normalize(blocks,hasTopic);
 }

 private static List<PolishBlock> Normalize(List<PolishBlock> blocks,bool hasTopic) {
  var result=new List<PolishBlock>();
  foreach(var block in blocks) {
   if(block.Topic=="continue"&&hasTopic){result.Add(block with{Title=""});continue;}
   var title=Clean(block.Topic=="new"?block.Title:"");
   if(title.Length==0)title=DefaultTitle;
   if(title.Length>MaxTitleChars)title=title[..MaxTitleChars];
   result.Add(new("new",title,block.Paragraphs));hasTopic=true;
  }
  return result;
 }

 private static string Clean(string? value)=>Regex.Replace(value??"",@"\s*[\r\n]+\s*","").Trim();
 private static bool IsPlaceholder(string value)=>Regex.IsMatch(value,@"^[（(][^（）()]*[)）]$");

 public static string Serialize(IReadOnlyList<PolishBlock> blocks)=>JsonSerializer.Serialize(new Envelope(blocks.ToArray()),Json);
 public static PolishBlock[] Deserialize(string json){try{return JsonSerializer.Deserialize<Envelope>(json,Json)?.Blocks??[];}catch(JsonException){return [];}}
 public static string PlainText(IEnumerable<PolishBlock> blocks)=>string.Join("\n\n",blocks.Select(b=>(b.Topic=="new"?b.Title+"\n":"")+string.Join("\n",b.Paragraphs)));

 /// <summary>Last paragraphs of an earlier result, used only as reference context.</summary>
 public static string Tail(IEnumerable<string> paragraphs) {
  var list=paragraphs.ToList();var text=string.Join("\n\n",list.Skip(Math.Max(0,list.Count-2)));
  return text.Length<=MaxContextChars?text:text[^MaxContextChars..];
 }
 public sealed record Envelope(PolishBlock[] Blocks);
}
